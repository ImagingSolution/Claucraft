using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace Claucraft.Controls;

/// <summary>
/// A window dragged out of the main one. It is the dock area and its tab strip and nothing else -
/// no toolbar, side panel or status bar - which is all an editor or a commit graph needs.
///
/// The windows in it stay in the main window's lists: it owns the session, the file and the
/// repository, and is still what closes them. This window only says where they are shown.
/// </summary>
internal partial class DetachedWindow : Window, IDockOwner
{
    private readonly List<IMdiLayoutItem> _items = new();
    private IMdiLayoutItem? _active;

    /// <summary>Set once the contents have been closed, so the second close can go through.</summary>
    private bool _emptied;

    /// <summary>Set while this window is closing its own contents, which empties it on the way.</summary>
    private bool _closing;

    /// <summary>Set once a terminal has landed here, so the swap is arranged only once.</summary>
    private bool _promoting;

    public DetachedWindow()
    {
        InitializeComponent();
        DockOwners.Register(this);
    }

    /// <summary>How many windows are in here. Nothing left means nothing to stay open for.</summary>
    internal int Count => _items.Count;

    // ── IDockOwner ──

    Window IDockOwner.Window => this;
    DockHost IDockOwner.Host => MdiHost;
    Panel IDockOwner.Strip => WindowStrip;
    Control IDockOwner.StripScroll => WindowStripScroll;
    Canvas IDockOwner.Overlay => DragOverlay;

    /// <summary>
    /// Everything. A terminal is typed into through an IME box and reports itself through a
    /// status bar, and this window has neither - so a terminal dropped here is answered by the
    /// window becoming one that has them, rather than by the drop being refused.
    /// </summary>
    public bool Accepts(IMdiLayoutItem item) => true;

    public void Release(IMdiLayoutItem item)
    {
        _items.Remove(item);
        MdiHost.Detach(item);
        MdiHost.Remove(item);
        WindowStrip.Children.Remove(item.StripButton);
        item.Owner = null;

        if (ReferenceEquals(_active, item)) _active = _items.Count > 0 ? _items[^1] : null;
        Relayout();

        // Closing the last window in here leaves an empty frame with nothing to put in it. The
        // close that is already under way in OnClosing gets there on its own.
        if (!_closing) CloseIfEmpty();
    }

    public void Adopt(IMdiLayoutItem item, DockDropTarget target)
    {
        item.Owner = this;
        _items.Add(item);
        WindowStrip.Children.Add(item.StripButton);

        if (MdiHost.Root == null) MdiHost.Root = DockLeafNode.Of(item);
        else if (target.Kind == DockDropKind.None) Stack(item);
        else MdiHost.DropInto(item, target);

        _active = item;
        Relayout();

        // Not here: what just arrived is still being laid out, and it is about to move again.
        if (item.Kind == MdiItemKind.Terminal && !_promoting)
        {
            _promoting = true;
            Dispatcher.UIThread.Post(Promote, DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// Swaps this window for one with a whole shell in it, carrying everything across. That is
    /// what a terminal landing here means: it is worked through a toolbar, an IME box and a
    /// status bar, so the window grows them rather than the terminal going without.
    /// </summary>
    private void Promote()
    {
        var window = new ShellWindow
        {
            Position = Position,
            Width = Width,
            Height = Height
        };
        var items = _items.ToList();

        // Out of here before the new window opens, and into it only on a later pass. A control
        // that changes window inside one layout pass leaves the window it left holding an
        // invalidation for a control it no longer owns, which that window throws on. Releasing
        // the last of them is also what closes this window.
        foreach (var item in items) Release(item);

        window.Show();

        Dispatcher.UIThread.Post(() =>
        {
            foreach (var item in items) ((IDockOwner)window.Shell).Adopt(item, default);
        }, DispatcherPriority.Background);
    }

    public void DockInside(IMdiLayoutItem item, DockDropTarget target)
    {
        MdiHost.DropInto(item, target);
        _active = item;
        Relayout();
    }

    public void ReorderStrip(IMdiLayoutItem item, int index)
    {
        int current = WindowStrip.Children.IndexOf(item.StripButton);
        if (current < 0) return;

        // The slot the tab itself occupies closes up behind it once it is lifted out.
        if (index > current) index--;
        index = System.Math.Clamp(index, 0, WindowStrip.Children.Count - 1);
        if (index == current) return;

        WindowStrip.Children.RemoveAt(current);
        WindowStrip.Children.Insert(index, item.StripButton);

        _items.Remove(item);
        _items.Insert(System.Math.Clamp(index, 0, _items.Count), item);
    }

    public void SetActive(IMdiLayoutItem item)
    {
        if (!_items.Contains(item)) return;

        _active = item;

        // Retargeting the pane rather than rebuilding the tree: a rebuild re-parents every
        // window in here, and a tab click is no reason for that.
        var leaf = MdiHost.FindLeaf(item);
        if (leaf != null && !ReferenceEquals(leaf.Active, item))
        {
            leaf.Active = item;
            MdiHost.SyncVisibility();
        }

        Title = item.Title;
        Paint();
        Activate();
    }

    public void CloseIfEmpty()
    {
        if (_items.Count > 0) return;
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

    // ── Layout ──

    /// <summary>Adds a window to whichever pane is already there, as another tab in it.</summary>
    private void Stack(IMdiLayoutItem item)
    {
        var leaf = MdiHost.Leaves().FirstOrDefault();
        if (leaf == null) { MdiHost.Root = DockLeafNode.Of(item); return; }

        leaf.Tabs.Add(item);
        leaf.Active = item;
    }

    private void Relayout()
    {
        WindowStripBar.IsVisible = _items.Count > 1;
        MdiHost.Rebuild();
        Title = _active?.Title ?? "Claucraft";
        Paint();
    }

    private void Paint()
    {
        foreach (var item in _items)
            AppShell.PaintStripSelection(
                item.StripButton, item.Container, ReferenceEquals(item, _active));
    }

    // ── Closing ──

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;

        if (_emptied || _items.Count == 0) { DockOwners.Unregister(this); return; }

        // Closing this window closes what is in it, and an unsaved editor still gets its say -
        // so the window waits for those to finish rather than going down on top of them.
        e.Cancel = true;
        _closing = true;
        _ = CloseContentsAsync();
    }

    private async Task CloseContentsAsync()
    {
        // Per item rather than through one shell: what is in here can have come from any of
        // them, and closing a window is the shell that opened it doing its own teardown.
        foreach (var item in _items.ToList())
            if (AppShell.OwnerOf(item) is { } shell)
                await shell.CloseLayoutItemAsync(item);

        // A window the user backed out of closing is still here, and so is this one.
        _closing = false;
        if (_items.Count > 0) return;

        Discard();
    }
}
