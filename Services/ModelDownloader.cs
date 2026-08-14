using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace RewireGuard.Services;

/// <summary>Progress of an in-flight model download.</summary>
public readonly record struct DownloadProgress(long BytesReceived, long TotalBytes)
{
    public double Fraction => TotalBytes > 0 ? (double)BytesReceived / TotalBytes : 0;
    public string Describe() => $"{BytesReceived / 1024d / 1024d:N0} of {TotalBytes / 1024d / 1024d:N0} MB";
}

/// <summary>
/// Fetches the ONNX classifier from Hugging Face.
///
/// The model is ~330 MB, well past GitHub's 100 MB per-file limit, so it cannot live in the
/// repository -- which left a fresh clone unable to run until the user manually exported one with
/// optimum-cli. Downloading it during onboarding closes that gap.
/// </summary>
public static class ModelDownloader
{
    public const string ModelUrl =
        "https://huggingface.co/AdamCodd/vit-base-nsfw-detector/resolve/main/onnx/model.onnx";

    public const string SourceDescription = "AdamCodd/vit-base-nsfw-detector (Hugging Face)";

    /// <summary>Expected size, used only to show a sensible progress bar before headers arrive.</summary>
    public const long ExpectedBytes = 344_569_044;

    /// <summary>
    /// SHA-256 of the published file, taken from Hugging Face's X-Linked-Etag for the LFS object.
    ///
    /// Verified rather than trusted: this file is loaded straight into the process by ONNX
    /// Runtime, so accepting whatever bytes arrive would mean executing an unverified download.
    /// If Hugging Face ever republishes the export this constant must be updated -- a mismatch
    /// fails the download rather than silently accepting a different file.
    /// </summary>
    public const string ExpectedSha256 = "87dbc7d4e8301b774ecb3c2d986154604fdedad08567afa00cb2c29e1d818c99";

    /// <summary>
    /// Downloads the model to <paramref name="destinationPath"/>, replacing anything already there
    /// only after the hash checks out. Throws on failure; the partial file is always cleaned up.
    /// </summary>
    public static async Task DownloadAsync(
        string destinationPath,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // Download beside the target so the final move is a same-volume rename, and so a failed
        // or cancelled attempt can never leave a truncated model.onnx that then fails to load.
        var partPath = destinationPath + ".part";

        using var client = new HttpClient
        {
            // Generous: this is a 330 MB transfer, and the per-read timeout below is what
            // actually catches a dead connection.
            Timeout = TimeSpan.FromMinutes(30)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RewireGuard/1.0");

        try
        {
            using var response = await client.GetAsync(
                ModelUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            long total = response.Content.Headers.ContentLength ?? ExpectedBytes;
            Log.Info($"Downloading model ({total / 1024 / 1024} MB) from {ModelUrl}");

            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var destination = new FileStream(
                partPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                var buffer = new byte[1 << 18];   // 256 KB
                long received = 0;
                int reportedPercent = -1;

                while (true)
                {
                    int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;

                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    hasher.AppendData(buffer, 0, read);
                    received += read;

                    // Report on whole-percent changes only; a progress event per 256 KB chunk is
                    // ~1300 dispatcher marshals for no visible benefit.
                    int percent = total > 0 ? (int)(received * 100 / total) : 0;
                    if (percent != reportedPercent)
                    {
                        reportedPercent = percent;
                        progress?.Report(new DownloadProgress(received, total));
                    }
                }

                if (total > 0 && received != total)
                    throw new IOException($"Download ended early: got {received} of {total} bytes.");
            }

            var actual = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            if (!string.Equals(actual, ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The downloaded model did not match its expected checksum, so it was discarded. " +
                    $"Expected {ExpectedSha256}, got {actual}. Export the model yourself instead " +
                    "(see README step 1).");
            }

            // Only now is it safe to replace an existing model.
            if (File.Exists(destinationPath)) File.Delete(destinationPath);
            File.Move(partPath, destinationPath);

            Log.Info($"Model downloaded and verified: {destinationPath}");
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException) Log.Error("Model download failed.", ex);
            throw;
        }
        finally
        {
            try { if (File.Exists(partPath)) File.Delete(partPath); }
            catch (Exception ex) { Log.Warn($"Could not clean up {partPath}: {ex.Message}"); }
        }
    }
}
