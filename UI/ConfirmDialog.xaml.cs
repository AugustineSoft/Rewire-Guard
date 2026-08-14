using System.Windows;

namespace RewireGuard.UI;

/// <summary>
/// Themed replacement for MessageBox. A stock white system dialog in the middle of a full-screen
/// dark overlay looked like a different application had interrupted the interruption.
/// </summary>
public partial class ConfirmDialog : ThemedWindow
{
    public ConfirmDialog(string title, string body, string confirmText = "Continue", string cancelText = "Cancel")
    {
        // The override prompt must be answered deliberately, so Escape does not dismiss it.
        CloseOnEscape = false;

        InitializeComponent();

        TitleText.Text = title;
        BodyText.Text = body;
        ConfirmButton.Content = confirmText;
        CancelButton.Content = cancelText;

        ConfirmButton.Click += (_, _) => { DialogResult = true; };
        CancelButton.Click += (_, _) => { DialogResult = false; };

        // Cancel is the safe default; focus it so a stray Enter or Space does not confirm.
        Loaded += (_, _) => CancelButton.Focus();
    }

    public static bool Show(Window? owner, string title, string body,
                            string confirmText = "Continue", string cancelText = "Cancel")
    {
        var dialog = new ConfirmDialog(title, body, confirmText, cancelText);

        // Only take an owner that is actually on screen; a hidden or closed owner throws.
        if (owner != null && owner.IsLoaded && owner.IsVisible)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        return dialog.ShowDialog() == true;
    }
}
