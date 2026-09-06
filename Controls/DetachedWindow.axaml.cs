using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

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
    /// A terminal is typed into through the main window's IME box and reports itself through its
    /// status bar, neither of which exist here, so this window does not take one.
    /// </summary>
    public bool Accepts(IMdiLayoutItem item) => item.Kind != MdiItemKind.Terminal;

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
            MainWindow.PaintStripSelection(
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
        if (Shell() is not { } main) return;

        foreach (var item in _items.ToList())
            await main.CloseLayoutItemAsync(item);

        // A window the user backed out of closing is still here, and so is this one.
        _closing = false;
        if (_items.Count > 0) return;

        _emptied = true;
        Close();
    }

    /// <summary>
    /// The window that owns the sessions and files shown here. Looked up rather than held: this
    /// window can outlive nothing, and the main window is the application's own.
    /// </summary>
    private static MainWindow? Shell() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
        ?.MainWindow as MainWindow;
}
