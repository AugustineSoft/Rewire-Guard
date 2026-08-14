using System;
using System.Windows;
using System.Windows.Input;

namespace RewireGuard.UI;

/// <summary>
/// Base for the app's chromeless dark windows. The default Win32 title bar is light-themed and
/// cannot be recoloured from WPF, so every window draws its own header; this supplies the two
/// behaviours that go missing when you drop the system chrome -- dragging by the header, and
/// closing with Escape.
/// </summary>
public abstract class ThemedWindow : Window
{
    protected ThemedWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && CloseOnEscape)
            {
                e.Handled = true;
                Close();
            }
        };
    }

    /// <summary>Whether Escape dismisses the window. Off for anything that must be answered.</summary>
    protected bool CloseOnEscape { get; init; } = true;

    /// <summary>Hook a header element up as the drag surface.</summary>
    protected void MakeDraggable(UIElement header)
    {
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            try { DragMove(); }
            catch (InvalidOperationException) { /* mouse released mid-drag; harmless */ }
        };
    }
}
