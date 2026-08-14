using System;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RestSharp;

namespace RewireGuard.Services;

/// <summary>Outcome of a stimulus attempt, so callers can react to an expired token specifically.</summary>
public enum StimulusOutcome
{
    Success,
    Unauthorized,   // token rejected -- caller should clear it and re-authenticate
    RateLimited,
    Failed
}

public readonly record struct StimulusResult(StimulusOutcome Outcome, string? Detail)
{
    public bool IsSuccess => Outcome == StimulusOutcome.Success;
}

/// <summary>
/// Pavlok v5 client implemented with RestSharp (simpler request/response handling).
/// Keeps the same robust token extraction logic but uses RestSharp for HTTP calls.
/// </summary>
public sealed class PavlokClient : IDisposable
{
    private const string BaseUrl = "https://api.pavlok.com";

    private readonly RestClient _client;
    private string? _token;
    private bool _disposed;

    public PavlokClient(TimeSpan? timeout = null)
    {
        var options = new RestClientOptions(BaseUrl)
        {
            ThrowOnAnyError = false,
            // Without this a hung connection ties up the request for RestSharp's default
            // (100s) -- long enough that the rate limiter has moved on and stimuli silently
            // stack up behind it.
            Timeout = timeout ?? TimeSpan.FromSeconds(10)
        };
        _client = new RestClient(options);
    }

    public bool IsLoggedIn => _token != null;
    public string? Token => _token;

    /// <summary>
    /// Sets the bearer token. The Authorization header is attached per request rather than via
    /// AddDefaultHeader, which appends rather than replaces -- calling this twice (as "Reconnect
    /// Pavlok" did) used to send two conflicting Authorization headers.
    /// </summary>
    public void UseExistingToken(string token)
    {
        _token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
    }

    public void ClearToken() => _token = null;

    /// <summary>
    /// Login via /api/v5/users/login (email/password). On success the token is stored.
    /// Throws on HTTP error.
    /// </summary>
    public async Task LoginAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        var req = new RestRequest("/api/v5/users/login", Method.Post);
        req.AddJsonBody(new { user = new { email, password } });

        var resp = await _client.ExecuteAsync(req, cancellationToken).ConfigureAwait(false);
        if (resp == null)
            throw new InvalidOperationException("Login request returned no response from Pavlok API.");

        if (!resp.IsSuccessful)
            throw new InvalidOperationException(
                $"Login HTTP error: {(int)resp.StatusCode} {resp.StatusDescription} - {resp.ErrorMessage}");

        var content = resp.Content ?? string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            // Fast-path: token at user.token
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("user", out var userElem) &&
                userElem.ValueKind == JsonValueKind.Object &&
                userElem.TryGetProperty("token", out var tokenProp) &&
                tokenProp.ValueKind == JsonValueKind.String)
            {
                UseExistingToken(tokenProp.GetString()!);
                Log.Info("Pavlok login succeeded.");
                return;
            }

            // Fallback: recursive search for common token names
            if (TryFindToken(root, out var tokenRecursive))
            {
                UseExistingToken(tokenRecursive);
                Log.Info("Pavlok login succeeded (token found by fallback search).");
                return;
            }
        }
        catch (JsonException je)
        {
            Log.Error("Pavlok login response was not valid JSON.", je);
        }

        // Deliberately does not log the body: on some error shapes it echoes request fields.
        throw new InvalidOperationException("Login succeeded but no token was found in the response.");
    }

    /// <summary>
    /// Send a stimulus. stimulusType is typically "vibe", "beep", or "zap". stimulusValue 1-100.
    /// The API expects a top-level "stimulus" object and returns 422 without it.
    ///
    /// Returns a result rather than throwing: a failed stimulus is an expected, recoverable
    /// condition on a flaky connection, and the caller needs to distinguish an expired token
    /// (re-authenticate) from a transient failure (ignore and try next poll).
    /// </summary>
    public async Task<StimulusResult> SendStimulusAsync(
        string stimulusType,
        int stimulusValue,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsLoggedIn)
            return new StimulusResult(StimulusOutcome.Unauthorized, "Not authenticated.");

        var payload = new
        {
            stimulus = new
            {
                stimulusType,
                stimulusValue = Math.Clamp(stimulusValue, 1, 100),
                reason
            }
        };

        var req = new RestRequest("/api/v5/stimulus/send", Method.Post);
        req.AddHeader("Authorization", $"Bearer {_token}");
        req.AddJsonBody(payload);

        RestResponse resp;
        try
        {
            resp = await _client.ExecuteAsync(req, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new StimulusResult(StimulusOutcome.Failed, ex.Message);
        }

        if (resp == null)
            return new StimulusResult(StimulusOutcome.Failed, "No response from Pavlok API.");

        if (resp.IsSuccessful)
            return new StimulusResult(StimulusOutcome.Success, null);

        var detail = $"{(int)resp.StatusCode} {resp.StatusDescription}; {resp.ErrorMessage ?? resp.ResponseStatus.ToString()}";

        return resp.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                => new StimulusResult(StimulusOutcome.Unauthorized, detail),
            HttpStatusCode.TooManyRequests
                => new StimulusResult(StimulusOutcome.RateLimited, detail),
            _ => new StimulusResult(StimulusOutcome.Failed, detail)
        };
    }

    /// <summary>
    /// Recursively search a JsonElement for a string property named token or access_token (case-insensitive).
    /// </summary>
    private static bool TryFindToken(JsonElement el, out string token)
    {
        token = null!;
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in el.EnumerateObject())
            {
                var name = prop.Name?.Trim();
                if (!string.IsNullOrEmpty(name) &&
                    (string.Equals(name, "token", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(name, "access_token", StringComparison.OrdinalIgnoreCase)))
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        token = prop.Value.GetString()!;
                        return true;
                    }
                }

                // Dive deeper
                if (prop.Value.ValueKind == JsonValueKind.Object || prop.Value.ValueKind == JsonValueKind.Array)
                {
                    if (TryFindToken(prop.Value, out token))
                        return true;
                }
            }
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                if (TryFindToken(item, out token))
                    return true;
            }
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _token = null;
        // RestClient owns an HttpClient and its handler; the previous no-op Dispose leaked both
        // every time "Reconnect Pavlok" built a new client.
        _client.Dispose();
    }
}
