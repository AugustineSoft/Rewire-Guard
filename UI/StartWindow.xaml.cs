using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using RewireGuard.Services;

namespace RewireGuard.UI;

/// <summary>
/// Start screen and Pavlok connection flow, replacing the bare login dialog. Two panes in one
/// window: a welcome pane that states what the app does and whether it is actually ready, and a
/// connect pane supporting either an API key or email/password.
///
/// The welcome pane exists because the old flow opened straight into an unexplained email and
/// password prompt from an app with no main window -- which looks indistinguishable from a
/// phishing dialog.
/// </summary>
public partial class StartWindow : ThemedWindow
{
    private readonly string _modelPath;
    private CancellationTokenSource? _downloadCts;
    private bool _busy;

    /// <summary>Token obtained during this session, if any.</summary>
    public string? Token { get; private set; }

    /// <summary>Set when the user asked to open settings instead of connecting.</summary>
    public bool SettingsRequested { get; private set; }

    /// <summary>Set when a model was fetched, so the host can load it without a restart.</summary>
    public bool ModelDownloaded { get; private set; }

    public StartWindow(bool modelPresent, bool pavlokConnected, string modelPath = "")
    {
        InitializeComponent();

        _modelPath = modelPath;

        MakeDraggable(HeaderBar);

        VersionText.Text = "Version " +
            (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");

        SetStatus(ModelDot, ModelStatus, modelPresent, modelPresent ? "Ready" : "Not installed");
        SetStatus(PavlokDot, PavlokStatus, pavlokConnected,
                  pavlokConnected ? "Connected" : "Not connected");

        // Offer the download only when we know where to put it and there is nothing there yet.
        if (!modelPresent && !string.IsNullOrEmpty(_modelPath))
            ModelDownloadPanel.Visibility = Visibility.Visible;

        if (pavlokConnected) ConnectButton.Content = "Reconnect Pavlok";

        CloseButton.Click += (_, _) => Close();
        SkipButton.Click += (_, _) => Close();
        SettingsButton.Click += (_, _) => { SettingsRequested = true; Close(); };

        ConnectButton.Click += (_, _) => ShowPane(connect: true);
        BackButton.Click += (_, _) => ShowPane(connect: false);

        ApiKeyMode.Checked += (_, _) => UpdateModeFields();
        PasswordMode.Checked += (_, _) => UpdateModeFields();

        SubmitButton.Click += async (_, _) => await SubmitAsync();
        DownloadModelButton.Click += async (_, _) => await DownloadModelAsync();

        // A half-finished 330 MB download should not survive the window closing.
        Closing += (_, _) => _downloadCts?.Cancel();
    }

    private async Task DownloadModelAsync()
    {
        if (_downloadCts != null)
        {
            _downloadCts.Cancel();
            return;
        }

        _downloadCts = new CancellationTokenSource();
        DownloadError.Visibility = Visibility.Collapsed;
        DownloadProgressPanel.Visibility = Visibility.Visible;
        DownloadModelButton.Content = "Cancel download";
        DownloadStatus.Text = "starting...";
        SkipButton.IsEnabled = false;

        var progress = new Progress<DownloadProgress>(p =>
        {
            DownloadFill.Width = Math.Max(0, DownloadTrack.ActualWidth * p.Fraction);
            DownloadStatus.Text = p.Describe();
        });

        try
        {
            await ModelDownloader.DownloadAsync(_modelPath, progress, _downloadCts.Token);

            ModelDownloaded = true;
            SetStatus(ModelDot, ModelStatus, true, "Ready");
            ModelDownloadPanel.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
            DownloadStatus.Text = "cancelled";
            DownloadFill.Width = 0;
        }
        catch (Exception ex)
        {
            DownloadError.Text = ex.Message;
            DownloadError.Visibility = Visibility.Visible;
            DownloadStatus.Text = "failed";
            DownloadFill.Width = 0;
        }
        finally
        {
            _downloadCts?.Dispose();
            _downloadCts = null;
            DownloadModelButton.Content = "Download model (330 MB)";
            SkipButton.IsEnabled = true;
        }
    }

    private void SetStatus(System.Windows.Shapes.Ellipse dot, System.Windows.Controls.TextBlock label,
                           bool ok, string text)
    {
        dot.Fill = (Brush)FindResource(ok ? "AccentCalmBrush" : "TextTertiaryBrush");
        label.Text = text;
    }

    private void ShowPane(bool connect)
    {
        WelcomePane.Visibility = connect ? Visibility.Collapsed : Visibility.Visible;
        ConnectPane.Visibility = connect ? Visibility.Visible : Visibility.Collapsed;
        ErrorText.Visibility = Visibility.Collapsed;

        if (connect) Dispatcher.BeginInvoke(() => ApiKeyBox.Focus());
    }

    private void UpdateModeFields()
    {
        bool apiKey = ApiKeyMode.IsChecked == true;
        ApiKeyFields.Visibility = apiKey ? Visibility.Visible : Visibility.Collapsed;
        PasswordFields.Visibility = apiKey ? Visibility.Collapsed : Visibility.Visible;
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private async Task SubmitAsync()
    {
        if (_busy) return;

        if (ApiKeyMode.IsChecked == true)
        {
            var key = ApiKeyBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                ShowError("Enter the API key from your Pavlok dashboard.");
                return;
            }

            // An API key is used as-is; there is no endpoint to validate it against short of
            // firing a stimulus, which is not something to do as a connection test.
            Token = key;
            DialogResult = true;
            return;
        }

        var email = EmailBox.Text?.Trim();
        var password = PasswordBox.Password ?? "";

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            ShowError("Enter both your email and password.");
            return;
        }

        SetBusy(true);
        try
        {
            using var client = new PavlokClient();
            await client.LoginAsync(email, password);

            if (client.Token == null)
            {
                ShowError("Signed in, but Pavlok did not return a token.");
                return;
            }

            Token = client.Token;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            Log.Error("Pavlok sign-in failed.", ex);
            ShowError(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        SubmitButton.IsEnabled = !busy;
        BackButton.IsEnabled = !busy;
        SubmitButton.Content = busy ? "Connecting..." : "Connect";
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
