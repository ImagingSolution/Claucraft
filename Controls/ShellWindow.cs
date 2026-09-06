using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Claucraft.Controls;

/// <summary>
/// A window dragged out with a terminal in it. It holds a whole shell - toolbar, side panel, IME
/// box, status bar - because that is what a terminal is worked through; the only thing it is not
/// is the main window, so closing it does not end the application.
///
/// No XAML of its own: one control fills it, and a code-behind pairing would only add a second
/// place for the two to disagree.
/// </summary>
internal class ShellWindow : Window
{
    /// <summary>Set once the contents have been closed, so the second close can go through.</summary>
    private bool _emptied;

    internal ShellWindow()
    {
        Shell = new AppShell(primary: false);
        Content = Shell;

        Title = "Claucraft";
        Width = 1100;
        Height = 750;
        MinWidth = 600;
        MinHeight = 400;

        try
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Claucraft/icon.ico")));
        }
        catch { }
    }

    /// <summary>The shell filling this window. It is what windows are docked into, not the window.</summary>
    internal AppShell Shell { get; }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _emptied) return;

        var items = Shell.DockedItems();
        if (items.Count == 0) return;

        // Closing this window closes what is in it, and an unsaved editor still gets its say -
        // so the window waits for those rather than going down on top of them.
        // Said before the first one goes: closing the last terminal in here would otherwise
        // read as "this shell has no terminal left" and open a lighter window mid-teardown.
        Shell.Closing = true;

        e.Cancel = true;
        _ = CloseContentsAsync(items);
    }

    private async Task CloseContentsAsync(List<IMdiLayoutItem> items)
    {
        foreach (var item in items)
            await Shell.CloseLayoutItemAsync(item);

        // A window the user backed out of closing is still here, and so is this one.
        if (Shell.DockedItems().Count > 0) return;

        Discard();
    }

    /// <summary>
    /// Closes this window without asking what is in it first. That is the application ending:
    /// the main window has already gone, and the shells have already been wound down.
    /// </summary>
    internal void Discard()
    {
        _emptied = true;
        Close();
    }

    /// <summary>
    /// The shell winds itself down after the window is gone - it sends /exit to whatever
    /// terminals are left and waits for them, which cannot happen while the window is still up.
    /// </summary>
    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        await Shell.ShutdownAsync();
    }
}
