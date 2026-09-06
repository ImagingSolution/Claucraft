using System;
using Avalonia.Controls;

namespace Claucraft;

/// <summary>
/// The application's own window. Everything in it is one <see cref="AppShell"/> - the same shell
/// a window dragged out with a terminal in it holds. The two differ only in lifetime: closing
/// this one closes the application, which is what makes it the main window.
/// </summary>
internal partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Closing this window is the application ending, not one window closing: what was dragged
    /// out into windows of its own goes with it. The winding down happens after the window is
    /// gone because it sends /exit to the terminals and waits for them.
    /// </summary>
    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        await AppShell.ShutdownApplicationAsync();
    }
}
