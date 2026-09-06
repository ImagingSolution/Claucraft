using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Claucraft.Controls;

/// <summary>Where a tab dropped at a given point would land.</summary>
internal enum DockDropKind
{
    /// <summary>Nowhere - the point is not over a pane, or the drop would change nothing.</summary>
    None,
    Left,
    Right,
    Top,
    Bottom,

    /// <summary>Onto the pane itself, joining its tabs.</summary>
    Center,
}

/// <summary>
/// The pane under the cursor and what a drop there would do. <paramref name="Preview"/> is the
/// area the drop would occupy, in <see cref="DockHost"/> coordinates, which is what gets painted.
/// </summary>
internal readonly record struct DockDropTarget(DockLeafNode? Leaf, DockDropKind Kind, Rect Preview);

/// <summary>
/// Renders a dock tree. Every pane is a real grid cell and every boundary between panes is a
/// <see cref="GridSplitter"/>, which is what makes the divisions draggable - the MDI windows used
/// to be positioned by hand on a <c>Canvas</c>, so nothing between them could be grabbed.
/// </summary>
public class DockHost : UserControl
{
    /// <summary>Width of the grab strip between two panes.</summary>
    private const double SplitterThickness = 4;

    /// <summary>
    /// How much of a pane, along each edge, means "split here" rather than "join this pane".
    /// A quarter is wide enough to hit without aiming and still leaves a large centre.
    /// </summary>
    private const double EdgeBand = 0.25;

    private readonly Panel _surface = new();

    /// <summary>
    /// Sits above the panes for the drop preview to be drawn on. A layer of its own rather than
    /// the panes' own borders, because <c>PaintStripSelection</c> and <c>BlinkActiveFrame</c>
    /// already contend over those.
    /// </summary>
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };

    /// <summary>The drop preview itself, moved and resized rather than recreated per frame.</summary>
    private readonly Rectangle _preview = new()
    {
        Fill = new SolidColorBrush(Color.FromArgb(60, 60, 140, 240)),
        Stroke = new SolidColorBrush(Color.FromArgb(190, 90, 165, 255)),
        StrokeThickness = 1.5,
        IsVisible = false,
    };

    /// <summary>Windows currently parented into the panes, so <see cref="Rebuild"/> can let go of
    /// exactly what it took - the tree it is building from no longer names a window just closed.</summary>
    private readonly List<IMdiLayoutItem> _attached = new();

    /// <summary>Each pane's control, so a screen point can be turned back into a tree node.</summary>
    private readonly List<(DockLeafNode Leaf, Panel Pane)> _panes = new();

    /// <summary>Root of the tree being shown, or null when there are no windows.</summary>
    internal DockNode? Root { get; set; }

    public DockHost()
    {
        var panel = new Panel();
        panel.Children.Add(_surface);
        _overlay.Children.Add(_preview);
        panel.Children.Add(_overlay);
        Content = panel;
        ClipToBounds = true;
    }

    // ── Tree queries ──

    /// <summary>Every pane, left to right and top to bottom.</summary>
    internal IEnumerable<DockLeafNode> Leaves() => Root == null ? Enumerable.Empty<DockLeafNode>() : Walk(Root);

    /// <summary>Every window in the tree, in pane order.</summary>
    internal IEnumerable<IMdiLayoutItem> Items() => Leaves().SelectMany(leaf => leaf.Tabs);

    /// <summary>The pane holding <paramref name="item"/>, or null when it is not in the tree.</summary>
    internal DockLeafNode? FindLeaf(IMdiLayoutItem item) => Leaves().FirstOrDefault(leaf => leaf.Tabs.Contains(item));

    private static IEnumerable<DockLeafNode> Walk(DockNode node)
    {
        switch (node)
        {
            case DockLeafNode leaf:
                yield return leaf;
                break;
            case DockSplitNode split:
                foreach (var child in split.Children)
                    foreach (var leaf in Walk(child))
                        yield return leaf;
                break;
        }
    }

    // ── Rendering ──

    /// <summary>
    /// Throws the visual tree away and builds it again from <see cref="Root"/>. Splitter drags
    /// don't come through here - they only move star sizes, which are written back to the tree -
    /// so this runs when the set of windows or the chosen preset changes, not on every resize.
    /// </summary>
    internal void Rebuild()
    {
        Release();
        _surface.Children.Clear();
        _panes.Clear();

        if (Root == null) return;

        _surface.Children.Add(Build(Root));
        SyncVisibility();
    }

    /// <summary>
    /// Takes <paramref name="item"/> out of the pane it is in. A window keeps its visual parent
    /// until something removes it, and a closed window is gone from the tree before
    /// <see cref="Rebuild"/> next runs, so it would otherwise never be let go of.
    /// </summary>
    internal void Detach(IMdiLayoutItem item)
    {
        _attached.Remove(item);
        if (item.Container.Parent is Panel parent) parent.Children.Remove(item.Container);
    }

    /// <summary>
    /// Shows the active tab in each pane and hides the rest. Cheap enough to call on every tab
    /// switch, which is the point: switching tabs must not re-parent a live terminal.
    /// </summary>
    internal void SyncVisibility()
    {
        foreach (var leaf in Leaves())
        {
            if (leaf.Active == null || !leaf.Tabs.Contains(leaf.Active))
                leaf.Active = leaf.Tabs.Count > 0 ? leaf.Tabs[^1] : null;

            foreach (var tab in leaf.Tabs)
                tab.Container.IsVisible = ReferenceEquals(tab, leaf.Active);
        }
    }

    private void Release()
    {
        foreach (var item in _attached)
            if (item.Container.Parent is Panel parent)
                parent.Children.Remove(item.Container);
        _attached.Clear();
    }

    private Control Build(DockNode node)
    {
        if (node is DockLeafNode leaf)
        {
            var pane = new Panel();
            foreach (var tab in leaf.Tabs)
            {
                pane.Children.Add(tab.Container);
                _attached.Add(tab);
            }
            _panes.Add((leaf, pane));
            return pane;
        }

        var split = (DockSplitNode)node;
        var grid = new Grid();
        bool horizontal = split.Orientation == Orientation.Horizontal;
        split.NormalizeRatios();

        // Slot i*2 holds child i; the odd slots in between are the fixed-width grab strips. Giving
        // the strips a slot of their own keeps them out of the star sizes, so the ratios stay the
        // proportions of the panes themselves.
        for (int i = 0; i < split.Children.Count; i++)
        {
            if (i > 0)
            {
                if (horizontal) grid.ColumnDefinitions.Add(new ColumnDefinition(SplitterThickness, GridUnitType.Pixel));
                else grid.RowDefinitions.Add(new RowDefinition(SplitterThickness, GridUnitType.Pixel));
            }

            double ratio = split.Ratios[i] <= 0 ? 1 : split.Ratios[i];
            if (horizontal) grid.ColumnDefinitions.Add(new ColumnDefinition(ratio, GridUnitType.Star));
            else grid.RowDefinitions.Add(new RowDefinition(ratio, GridUnitType.Star));
        }

        for (int i = 0; i < split.Children.Count; i++)
        {
            int slot = i * 2;

            var child = Build(split.Children[i]);
            if (horizontal) Grid.SetColumn(child, slot); else Grid.SetRow(child, slot);
            grid.Children.Add(child);

            if (i == 0) continue;

            var splitter = new GridSplitter
            {
                Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                ResizeDirection = horizontal ? GridResizeDirection.Columns : GridResizeDirection.Rows,
                Cursor = new Cursor(horizontal ? StandardCursorType.SizeWestEast : StandardCursorType.SizeNorthSouth),
            };
            if (horizontal) Grid.SetColumn(splitter, slot - 1); else Grid.SetRow(splitter, slot - 1);
            splitter.DragCompleted += (_, _) => CaptureRatios(split, grid, horizontal);
            grid.Children.Add(splitter);
        }

        return grid;
    }

    /// <summary>
    /// Reads the star sizes the drag left behind back into the tree, so the next rebuild comes up
    /// with the boundaries where the user put them.
    /// </summary>
    private static void CaptureRatios(DockSplitNode split, Grid grid, bool horizontal)
    {
        split.Ratios.Clear();
        for (int i = 0; i < split.Children.Count; i++)
        {
            int slot = i * 2;
            double value = horizontal
                ? grid.ColumnDefinitions[slot].Width.Value
                : grid.RowDefinitions[slot].Height.Value;
            split.Ratios.Add(value <= 0 ? 1 : value);
        }
        split.NormalizeRatios();
    }

    // ── Drop targeting ──

    /// <summary>
    /// Works out what dropping <paramref name="dragged"/> at <paramref name="point"/> - given in
    /// this control's coordinates - would do. Near a pane's edge that is a split in that
    /// direction; anywhere else in the pane it is a join.
    /// </summary>
    internal DockDropTarget HitTestDropTarget(Point point, IMdiLayoutItem? dragged)
    {
        foreach (var (leaf, pane) in _panes)
        {
            var origin = pane.TranslatePoint(new Point(0, 0), this);
            if (origin == null) continue;

            var bounds = new Rect(origin.Value, pane.Bounds.Size);
            if (bounds.Width <= 0 || bounds.Height <= 0 || !bounds.Contains(point)) continue;

            double left = (point.X - bounds.X) / bounds.Width;
            double top = (point.Y - bounds.Y) / bounds.Height;
            double nearest = Math.Min(Math.Min(left, 1 - left), Math.Min(top, 1 - top));

            DockDropKind kind;
            if (nearest >= EdgeBand) kind = DockDropKind.Center;
            else if (nearest == left) kind = DockDropKind.Left;
            else if (nearest == 1 - left) kind = DockDropKind.Right;
            else if (nearest == top) kind = DockDropKind.Top;
            else kind = DockDropKind.Bottom;

            if (!Moves(leaf, kind, dragged)) return new DockDropTarget(leaf, DockDropKind.None, bounds);
            return new DockDropTarget(leaf, kind, PreviewArea(bounds, kind));
        }

        return default;
    }

    /// <summary>
    /// Whether the drop would actually rearrange anything. A window dropped back on the pane it
    /// came from stays put, and a lone window split off from its own pane would only be put back
    /// where it already is.
    /// </summary>
    private bool Moves(DockLeafNode leaf, DockDropKind kind, IMdiLayoutItem? dragged)
    {
        if (dragged == null) return true;
        if (!ReferenceEquals(FindLeaf(dragged), leaf)) return true;
        return kind != DockDropKind.Center && leaf.Tabs.Count > 1;
    }

    private static Rect PreviewArea(Rect pane, DockDropKind kind) => kind switch
    {
        DockDropKind.Left => pane.WithWidth(pane.Width / 2),
        DockDropKind.Right => new Rect(pane.X + pane.Width / 2, pane.Y, pane.Width / 2, pane.Height),
        DockDropKind.Top => pane.WithHeight(pane.Height / 2),
        DockDropKind.Bottom => new Rect(pane.X, pane.Y + pane.Height / 2, pane.Width, pane.Height / 2),
        _ => pane,
    };

    /// <summary>Paints where the drop would land, or clears the preview when it would land nowhere.</summary>
    internal void ShowDropPreview(DockDropTarget target)
    {
        if (target.Kind == DockDropKind.None || target.Preview.Width <= 0 || target.Preview.Height <= 0)
        {
            ClearDropPreview();
            return;
        }

        Canvas.SetLeft(_preview, target.Preview.X);
        Canvas.SetTop(_preview, target.Preview.Y);
        _preview.Width = target.Preview.Width;
        _preview.Height = target.Preview.Height;
        _preview.IsVisible = true;
    }

    /// <summary>
    /// The preview for a window arriving from elsewhere with no pane under the cursor - an empty
    /// dock area, or the window around it. The whole area, because that is where it will land.
    /// </summary>
    internal void ShowAdoptPreview() =>
        ShowDropPreview(new DockDropTarget(null, DockDropKind.Center, new Rect(Bounds.Size)));

    internal void ClearDropPreview() => _preview.IsVisible = false;

    // ── Tree edits ──

    /// <summary>
    /// Moves <paramref name="item"/> to where <paramref name="target"/> says. The caller still has
    /// to <see cref="Rebuild"/> - the tree is the model, and one drop changes it once.
    /// </summary>
    internal void DropInto(IMdiLayoutItem item, DockDropTarget target)
    {
        if (target.Kind == DockDropKind.None || target.Leaf == null) return;

        var leaf = target.Leaf;
        Remove(item);

        // Taking the item out can leave the pane it was aimed at empty, and an empty pane is
        // pruned - so fall back to whatever pane is left rather than dropping into a dead node.
        if (leaf.Tabs.Count == 0)
        {
            if (Root == null) { Root = DockLeafNode.Of(item); return; }
            leaf = Leaves().First();
            leaf.Tabs.Add(item);
            leaf.Active = item;
            return;
        }

        if (target.Kind == DockDropKind.Center)
        {
            leaf.Tabs.Add(item);
            leaf.Active = item;
            return;
        }

        Split(leaf, item, target.Kind);
    }

    /// <summary>
    /// Brings the tree back in line with <paramref name="items"/> without disturbing an
    /// arrangement the user built by hand: windows that are gone are dropped, and new ones join
    /// the pane the active window is in.
    /// </summary>
    internal void Reconcile(IReadOnlyList<IMdiLayoutItem> items, IMdiLayoutItem? active)
    {
        foreach (var gone in Items().Where(item => !items.Contains(item)).ToList())
            Remove(gone);

        foreach (var item in items)
        {
            if (FindLeaf(item) != null) continue;

            if (Root == null)
            {
                Root = DockLeafNode.Of(item);
                continue;
            }

            var host = (active == null ? null : FindLeaf(active)) ?? Leaves().First();
            host.Tabs.Add(item);
            host.Active = item;
        }
    }

    /// <summary>Takes a window out of the tree, collapsing whatever that leaves empty.</summary>
    internal void Remove(IMdiLayoutItem item)
    {
        var leaf = FindLeaf(item);
        if (leaf == null) return;

        leaf.Tabs.Remove(item);
        if (ReferenceEquals(leaf.Active, item))
            leaf.Active = leaf.Tabs.Count > 0 ? leaf.Tabs[^1] : null;

        if (leaf.Tabs.Count == 0) Prune(leaf);
    }

    private void Prune(DockNode node)
    {
        var parent = node.Parent;
        node.Parent = null;

        if (parent == null)
        {
            if (ReferenceEquals(Root, node)) Root = null;
            return;
        }

        int index = parent.Children.IndexOf(node);
        if (index < 0) return;

        parent.Children.RemoveAt(index);
        if (index < parent.Ratios.Count) parent.Ratios.RemoveAt(index);
        parent.NormalizeRatios();

        // A split of one is just its child, and a split of none is nothing at all - either way it
        // would otherwise leave a grab strip against the edge of the host with nothing beyond it.
        if (parent.Children.Count == 0) { Prune(parent); return; }
        if (parent.Children.Count > 1) return;

        var only = parent.Children[0];
        var grandparent = parent.Parent;
        if (grandparent == null)
        {
            only.Parent = null;
            parent.Parent = null;
            Root = only;
            return;
        }

        int at = grandparent.Children.IndexOf(parent);
        grandparent.Children[at] = only;
        only.Parent = grandparent;
        parent.Parent = null;
    }

    private void Split(DockLeafNode leaf, IMdiLayoutItem item, DockDropKind kind)
    {
        bool horizontal = kind is DockDropKind.Left or DockDropKind.Right;
        bool before = kind is DockDropKind.Left or DockDropKind.Top;
        var orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical;
        var arrival = DockLeafNode.Of(item);
        var parent = leaf.Parent;

        // A split already running the right way takes the new pane as another child rather than
        // nesting a second split inside itself, which keeps the boundaries in one row of grips.
        if (parent != null && parent.Orientation == orientation)
        {
            int index = parent.Children.IndexOf(leaf);
            int at = before ? index : index + 1;
            double share = parent.Ratios.Count > 0 ? parent.Ratios.Average() : 1;

            arrival.Parent = parent;
            parent.Children.Insert(at, arrival);
            parent.Ratios.Insert(Math.Min(at, parent.Ratios.Count), share);
            parent.NormalizeRatios();
            return;
        }

        var split = new DockSplitNode { Orientation = orientation };
        if (parent == null)
        {
            Root = split;
        }
        else
        {
            split.Parent = parent;
            parent.Children[parent.Children.IndexOf(leaf)] = split;
        }

        leaf.Parent = null;
        if (before) { split.Add(arrival); split.Add(leaf); }
        else { split.Add(leaf); split.Add(arrival); }
    }
}
