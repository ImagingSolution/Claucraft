using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Claucraft.Services;

namespace Claucraft.Controls;

/// <summary>
/// The side pane's Diff tab: what the working tree changed against HEAD, file by file, in a
/// unified or side-by-side layout. A click on a line opens a comment on it; the comments go to
/// the composer together as one prompt. Each file can be staged, unstaged or put back.
/// </summary>
public sealed class DiffViewerPanel : DockPanel
{
    private const int LinesShownAtFirst = 400;
    private const int MaxUntrackedFiles = 200;
    private const long MaxUntrackedBytes = 256 * 1024;

    private sealed record Comment(string Path, int Line, bool OldSide, string Text);

    private readonly Func<string?> _folder;
    private readonly FontFamily _mono;
    private readonly TextBlock _summary;
    private readonly Button _layoutButton;
    private readonly Button _refreshButton;
    private readonly StackPanel _files = new() { Spacing = 8, Margin = new Thickness(8) };
    private readonly ScrollViewer _scroll;
    private readonly Border _commentBar;
    private readonly TextBlock _commentCount;
    private readonly DispatcherTimer _timer;
    private readonly List<Comment> _comments = new();
    private readonly HashSet<string> _collapsed = new();
    private readonly HashSet<string> _showAll = new();
    private List<DiffFile> _diff = new();
    private string? _repoRoot;
    private string _key = "";
    private bool _split;
    private bool _isDark;
    private bool _loading;
    private (string Path, int Line, bool OldSide)? _editing;

    /// <summary>The reader sent the comments: the prompt text to put in the composer.</summary>
    public event Action<string>? CommentsSubmitted;

    public DiffViewerPanel(bool isDark, Func<string?> folder, FontFamily mono)
    {
        _isDark = isDark;
        _folder = folder;
        _mono = mono;

        _summary = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        _layoutButton = ToolButton("", () => { _split = !_split; UpdateLayoutButton(); Rebuild(); });
        _refreshButton = ToolButton("⟳", () => _ = LoadAsync(force: true));
        ToolTip.SetTip(_refreshButton, Loc.Get("DiffRefresh"));
        UpdateLayoutButton();

        var toolbar = new DockPanel { Margin = new Thickness(10, 6, 6, 6) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        buttons.Children.Add(_layoutButton);
        buttons.Children.Add(_refreshButton);
        DockPanel.SetDock(buttons, Dock.Right);
        toolbar.Children.Add(buttons);
        toolbar.Children.Add(_summary);
        DockPanel.SetDock(toolbar, Dock.Top);
        Children.Add(toolbar);

        _commentCount = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        var send = ToolButton(Loc.Get("DiffSendComments"), SubmitComments);
        send.Background = new SolidColorBrush(ChatTheme.Accent);
        send.Foreground = Brushes.White;
        var clear = ToolButton(Loc.Get("DiffClearComments"), () => { _comments.Clear(); Rebuild(); });
        var barButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        barButtons.Children.Add(clear);
        barButtons.Children.Add(send);
        var bar = new DockPanel();
        DockPanel.SetDock(barButtons, Dock.Right);
        bar.Children.Add(barButtons);
        bar.Children.Add(_commentCount);
        _commentBar = new Border { Padding = new Thickness(10, 6), BorderThickness = new Thickness(0, 1, 0, 0), Child = bar, IsVisible = false };
        DockPanel.SetDock(_commentBar, Dock.Bottom);
        Children.Add(_commentBar);

        _scroll = new ScrollViewer
        {
            Content = _files,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        Children.Add(_scroll);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _timer.Tick += (_, _) => _ = LoadAsync(force: false);
        ApplyTheme(isDark);
    }

    /// <summary>Loads when the tab comes into view and keeps up with the tree while it stays there.</summary>
    public void SetActive(bool active)
    {
        if (active == _timer.IsEnabled) return;
        if (active)
        {
            _timer.Start();
            _ = LoadAsync(force: false);
        }
        else
        {
            _timer.Stop();
        }
    }

    public void ApplyTheme(bool isDark)
    {
        _isDark = isDark;
        Background = new SolidColorBrush(ChatTheme.Background(isDark));
        _summary.Foreground = Dim;
        _commentCount.Foreground = Fg;
        _commentBar.Background = new SolidColorBrush(ChatTheme.Surface(isDark));
        _commentBar.BorderBrush = new SolidColorBrush(ChatTheme.Outline(isDark));
        foreach (var b in new[] { _layoutButton, _refreshButton }) b.Foreground = Fg;
        Rebuild();
    }

    private IBrush Fg => new SolidColorBrush(_isDark ? Color.FromRgb(225, 225, 228) : Color.FromRgb(35, 35, 38));
    private IBrush Dim => new SolidColorBrush(_isDark ? Color.FromRgb(150, 150, 155) : Color.FromRgb(105, 105, 110));
    private Color AddedBg => _isDark ? Color.FromArgb(70, 46, 160, 67) : Color.FromRgb(230, 255, 236);
    private Color RemovedBg => _isDark ? Color.FromArgb(70, 200, 60, 60) : Color.FromRgb(255, 235, 233);
    private Color HunkBg => _isDark ? Color.FromRgb(36, 40, 52) : Color.FromRgb(240, 244, 252);

    private Button ToolButton(string text, Action onClick)
    {
        var b = new Button
        {
            Content = text,
            Padding = new Thickness(8, 2),
            FontSize = 12,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(ChatTheme.Outline(_isDark)),
            CornerRadius = new CornerRadius(6),
            Cursor = new Cursor(StandardCursorType.Hand),
            Focusable = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    private void UpdateLayoutButton()
    {
        _layoutButton.Content = Loc.Get(_split ? "DiffUnified" : "DiffSplit");
    }

    // ── Loading ──

    private async Task LoadAsync(bool force)
    {
        if (_loading) return;
        _loading = true;
        try
        {
            var folder = _folder();
            var result = await Task.Run(() => ReadDiff(folder));
            if (result.Error != null)
            {
                _repoRoot = null;
                _diff = new List<DiffFile>();
                _key = "";
                _summary.Text = result.Error;
                _files.Children.Clear();
                return;
            }
            if (!force && result.Key == _key) return;
            _repoRoot = result.Root;
            _key = result.Key;
            _diff = result.Files;
            Rebuild();
        }
        catch (Exception ex)
        {
            _summary.Text = ex.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    private sealed record DiffResult(string? Root, List<DiffFile> Files, string Key, string? Error);

    private static DiffResult ReadDiff(string? folder)
    {
        var root = string.IsNullOrEmpty(folder) ? null : GitCli.FindRepoRoot(folder);
        if (root == null) return new DiffResult(null, new(), "", Loc.Get("DiffNotRepo"));

        bool hasHead = GitCli.Execute(root, null, "rev-parse", "--verify", "--quiet", "HEAD").Ok;
        string text = hasHead
            ? GitCli.Run(root, "-c", "core.quotepath=false", "diff", "HEAD", "--no-color", "--no-ext-diff")
            : GitCli.Run(root, "-c", "core.quotepath=false", "diff", "--cached", "--no-color", "--no-ext-diff")
              + GitCli.Run(root, "-c", "core.quotepath=false", "diff", "--no-color", "--no-ext-diff");
        text = GitCli.TruncateDiff(text);
        var staged = GitCli.Run(root, "-c", "core.quotepath=false", "diff", "--cached", "--name-only")
            .Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var untracked = GitCli.Run(root, "-c", "core.quotepath=false", "ls-files", "--others", "--exclude-standard")
            .Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var files = UnifiedDiffParser.Parse(text);
        foreach (var f in files) f.IsStaged = staged.Contains(f.Path);

        var key = new StringBuilder(text).Append('\u001E').Append(string.Join("|", staged));
        foreach (var path in untracked.Take(MaxUntrackedFiles))
        {
            string? content = null;
            try
            {
                var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
                var info = new FileInfo(full);
                if (!info.Exists) continue;
                key.Append('|').Append(path).Append(info.Length).Append(info.LastWriteTimeUtc.Ticks);
                if (info.Length <= MaxUntrackedBytes)
                {
                    var bytes = File.ReadAllBytes(full);
                    if (Array.IndexOf(bytes, (byte)0) < 0) content = Encoding.UTF8.GetString(bytes);
                }
            }
            catch { }
            files.Add(UnifiedDiffParser.ForUntracked(path, content));
        }
        return new DiffResult(root, files, key.ToString(), null);
    }

    // ── Rendering ──

    private void Rebuild()
    {
        _files.Children.Clear();
        int added = _diff.Sum(f => f.Added), removed = _diff.Sum(f => f.Removed);
        if (_repoRoot != null)
            _summary.Text = _diff.Count == 0
                ? Loc.Get("DiffNoChanges")
                : string.Format(Loc.Get("DiffSummary"), _diff.Count, added, removed);
        foreach (var f in _diff) _files.Children.Add(FileSection(f));

        _commentBar.IsVisible = _comments.Count > 0;
        _commentCount.Text = string.Format(Loc.Get("DiffCommentCount"), _comments.Count);
    }

    private Control FileSection(DiffFile f)
    {
        bool collapsed = _collapsed.Contains(f.Path);
        var outline = new SolidColorBrush(ChatTheme.Outline(_isDark));

        var header = new DockPanel { Margin = new Thickness(8, 5) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var stage = ToolButton(Loc.Get(f.IsStaged ? "DiffUnstage" : "DiffStage"), () => _ = StageAsync(f));
        stage.Foreground = Fg;
        actions.Children.Add(stage);
        if (!f.IsUntracked && !f.IsNew)
        {
            Button? discard = null;
            bool armed = false;
            discard = ToolButton(Loc.Get("DiffDiscard"), () =>
            {
                // Two presses: the first only arms it, since the change cannot be brought back
                if (!armed)
                {
                    armed = true;
                    discard!.Content = Loc.Get("DiffDiscardConfirm");
                    discard.Foreground = new SolidColorBrush(Color.FromRgb(220, 70, 60));
                    DispatcherTimer.RunOnce(() =>
                    {
                        armed = false;
                        discard.Content = Loc.Get("DiffDiscard");
                        discard.Foreground = Fg;
                    }, TimeSpan.FromSeconds(3));
                    return;
                }
                _ = DiscardAsync(f);
            });
            discard.Foreground = Fg;
            actions.Children.Add(discard);
        }
        foreach (var c in actions.Children.OfType<Button>()) c.Foreground ??= Fg;
        DockPanel.SetDock(actions, Dock.Right);
        header.Children.Add(actions);

        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock { Text = collapsed ? "▸" : "▾", Foreground = Dim, FontSize = 12 });
        var name = f.OldPath != null ? f.OldPath + " → " + f.Path : f.Path;
        title.Children.Add(new TextBlock
        {
            Text = name, Foreground = Fg, FontSize = 12, FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.PathSegmentEllipsis, MaxWidth = 420,
        });
        title.Children.Add(new TextBlock { Text = "+" + f.Added, Foreground = new SolidColorBrush(Color.FromRgb(70, 170, 90)), FontSize = 12 });
        title.Children.Add(new TextBlock { Text = "−" + f.Removed, Foreground = new SolidColorBrush(Color.FromRgb(210, 80, 70)), FontSize = 12 });
        string? badge = f.IsUntracked ? "DiffBadgeUntracked" : f.IsNew ? "DiffBadgeNew" : f.IsDeleted ? "DiffBadgeDeleted" : null;
        if (badge != null) title.Children.Add(new TextBlock { Text = Loc.Get(badge), Foreground = Dim, FontSize = 11 });
        if (f.IsStaged) title.Children.Add(new TextBlock { Text = Loc.Get("DiffBadgeStaged"), Foreground = new SolidColorBrush(ChatTheme.Accent), FontSize = 11 });
        ToolTip.SetTip(title, name);
        header.Children.Add(title);

        var headerBorder = new Border
        {
            Background = new SolidColorBrush(ChatTheme.Surface(_isDark)),
            Child = header,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        headerBorder.PointerPressed += (_, e) =>
        {
            if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) != null) return;
            if (!_collapsed.Remove(f.Path)) _collapsed.Add(f.Path);
            Rebuild();
            e.Handled = true;
        };

        var stack = new StackPanel();
        stack.Children.Add(headerBorder);
        if (!collapsed)
        {
            if (f.IsBinary)
                stack.Children.Add(new TextBlock { Text = Loc.Get("DiffBinary"), Foreground = Dim, Margin = new Thickness(10, 6), FontSize = 12 });
            else
                stack.Children.Add(_split ? SplitBody(f) : UnifiedBody(f));
        }

        return new Border
        {
            BorderBrush = outline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Child = stack,
        };
    }

    private IEnumerable<DiffLine> VisibleLines(DiffFile f, out int hidden)
    {
        hidden = 0;
        if (_showAll.Contains(f.Path) || f.Lines.Count <= LinesShownAtFirst) return f.Lines;
        hidden = f.Lines.Count - LinesShownAtFirst;
        return f.Lines.Take(LinesShownAtFirst);
    }

    private Control ShowMore(DiffFile f, int hidden)
    {
        var b = ToolButton(string.Format(Loc.Get("DiffShowMore"), hidden), () => { _showAll.Add(f.Path); Rebuild(); });
        b.Foreground = Fg;
        b.Margin = new Thickness(8, 4);
        return b;
    }

    private Control UnifiedBody(DiffFile f)
    {
        var body = new StackPanel();
        var visible = VisibleLines(f, out int hidden);
        foreach (var line in visible)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("40,40,14,*") };
            row.Children.Add(Num(line.OldNo, 0));
            row.Children.Add(Num(line.NewNo, 1));
            var sign = new TextBlock
            {
                Text = line.Kind switch { DiffLineKind.Added => "+", DiffLineKind.Removed => "-", _ => "" },
                FontFamily = _mono, FontSize = 12, Foreground = Dim,
            };
            Grid.SetColumn(sign, 2);
            row.Children.Add(sign);
            var text = CodeText(line);
            Grid.SetColumn(text, 3);
            row.Children.Add(text);

            var (commentLine, oldSide) = CommentAnchor(line);
            body.Children.Add(LineBorder(row, line.Kind, f.Path, commentLine, oldSide));
            AddCommentRows(body, f.Path, commentLine, oldSide);
        }
        if (hidden > 0) body.Children.Add(ShowMore(f, hidden));
        return body;
    }

    private Control SplitBody(DiffFile f)
    {
        var body = new StackPanel();
        var lines = VisibleLines(f, out int hidden).ToList();
        int i = 0;
        while (i < lines.Count)
        {
            var line = lines[i];
            if (line.Kind is DiffLineKind.Hunk or DiffLineKind.Note or DiffLineKind.Context)
            {
                body.Children.Add(SplitRow(f.Path, line, line));
                if (line.Kind == DiffLineKind.Context) AddCommentRows(body, f.Path, line.NewNo ?? 0, false);
                i++;
                continue;
            }
            // A run of removals followed by a run of additions pairs up line by line
            var removed = new List<DiffLine>();
            var added = new List<DiffLine>();
            while (i < lines.Count && lines[i].Kind == DiffLineKind.Removed) removed.Add(lines[i++]);
            while (i < lines.Count && lines[i].Kind == DiffLineKind.Added) added.Add(lines[i++]);
            for (int k = 0; k < Math.Max(removed.Count, added.Count); k++)
            {
                var l = k < removed.Count ? removed[k] : null;
                var r = k < added.Count ? added[k] : null;
                body.Children.Add(SplitRow(f.Path, l, r));
                if (l != null) AddCommentRows(body, f.Path, l.OldNo ?? 0, true);
                if (r != null) AddCommentRows(body, f.Path, r.NewNo ?? 0, false);
            }
        }
        if (hidden > 0) body.Children.Add(ShowMore(f, hidden));
        return body;
    }

    private Control SplitRow(string path, DiffLine? left, DiffLine? right)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,1,*") };
        if (left != null && ReferenceEquals(left, right) && left.Kind is DiffLineKind.Hunk or DiffLineKind.Note)
        {
            var hunk = LineBorder(CodeText(left), left.Kind, path, 0, false);
            Grid.SetColumnSpan(hunk, 3);
            grid.Children.Add(hunk);
            return grid;
        }

        Control Side(DiffLine? l, bool oldSide)
        {
            if (l == null) return new Border { Background = new SolidColorBrush(ChatTheme.Hover(_isDark)) };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("40,*") };
            row.Children.Add(Num(oldSide ? l.OldNo : l.NewNo, 0));
            var t = CodeText(l);
            Grid.SetColumn(t, 1);
            row.Children.Add(t);
            var kind = l.Kind == DiffLineKind.Context ? DiffLineKind.Context : l.Kind;
            int no = (oldSide ? l.OldNo : l.NewNo) ?? 0;
            return LineBorder(row, kind, path, no, oldSide);
        }

        var leftSide = Side(left, true);
        var rightSide = Side(right, false);
        var divider = new Border { Background = new SolidColorBrush(ChatTheme.Outline(_isDark)) };
        Grid.SetColumn(divider, 1);
        Grid.SetColumn(rightSide, 2);
        grid.Children.Add(leftSide);
        grid.Children.Add(divider);
        grid.Children.Add(rightSide);
        return grid;
    }

    private TextBlock Num(int? n, int column)
    {
        var t = new TextBlock
        {
            Text = n?.ToString() ?? "",
            FontFamily = _mono, FontSize = 11, Foreground = Dim,
            TextAlignment = TextAlignment.Right,
            Margin = new Thickness(0, 1, 6, 1),
        };
        Grid.SetColumn(t, column);
        return t;
    }

    private TextBlock CodeText(DiffLine line) => new()
    {
        Text = line.Text.Replace("\t", "    "),
        FontFamily = _mono,
        FontSize = 12,
        Foreground = line.Kind is DiffLineKind.Hunk or DiffLineKind.Note ? Dim : Fg,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(line.Kind == DiffLineKind.Hunk ? 8 : 0, 1, 6, 1),
    };

    /// <summary>The line a comment on this row is about: the new side, or the old for a removal.</summary>
    private static (int line, bool oldSide) CommentAnchor(DiffLine l) => l.Kind switch
    {
        DiffLineKind.Removed => (l.OldNo ?? 0, true),
        DiffLineKind.Added or DiffLineKind.Context => (l.NewNo ?? 0, false),
        _ => (0, false),
    };

    private Border LineBorder(Control content, DiffLineKind kind, string path, int commentLine, bool oldSide)
    {
        var border = new Border
        {
            Child = content,
            Background = kind switch
            {
                DiffLineKind.Added => new SolidColorBrush(AddedBg),
                DiffLineKind.Removed => new SolidColorBrush(RemovedBg),
                DiffLineKind.Hunk => new SolidColorBrush(HunkBg),
                _ => Brushes.Transparent,
            },
        };
        if (commentLine > 0)
        {
            border.Cursor = new Cursor(StandardCursorType.Hand);
            ToolTip.SetTip(border, Loc.Get("DiffCommentHint"));
            border.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(border).Properties.IsLeftButtonPressed) return;
                _editing = (path, commentLine, oldSide);
                Rebuild();
                e.Handled = true;
            };
        }
        return border;
    }

    private void AddCommentRows(StackPanel body, string path, int line, bool oldSide)
    {
        if (line <= 0) return;
        foreach (var c in _comments.Where(c => c.Path == path && c.Line == line && c.OldSide == oldSide).ToList())
        {
            var note = new DockPanel { Margin = new Thickness(8, 4) };
            var remove = ToolButton("✕", () => { _comments.Remove(c); Rebuild(); });
            remove.Foreground = Dim;
            DockPanel.SetDock(remove, Dock.Right);
            note.Children.Add(remove);
            note.Children.Add(new TextBlock { Text = c.Text, Foreground = Fg, FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            body.Children.Add(new Border
            {
                Child = note,
                Margin = new Thickness(44, 2, 8, 4),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(ChatTheme.Accent),
                Background = new SolidColorBrush(ChatTheme.Surface(_isDark)),
            });
        }

        if (_editing is not { } ed || ed.Path != path || ed.Line != line || ed.OldSide != oldSide) return;
        var box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 56,
            FontSize = 12,
            PlaceholderText = Loc.Get("DiffCommentPlaceholder"),
        };
        void Add()
        {
            var text = box.Text?.Trim();
            if (!string.IsNullOrEmpty(text)) _comments.Add(new Comment(path, line, oldSide, text));
            _editing = null;
            Rebuild();
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { Add(); e.Handled = true; }
            else if (e.Key == Key.Escape) { _editing = null; Rebuild(); e.Handled = true; }
        };
        var add = ToolButton(Loc.Get("DiffAddComment"), Add);
        add.Background = new SolidColorBrush(ChatTheme.Accent);
        add.Foreground = Brushes.White;
        var cancel = ToolButton(Loc.Get("DiffCancel"), () => { _editing = null; Rebuild(); });
        cancel.Foreground = Fg;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(add);
        var editor = new StackPanel { Margin = new Thickness(44, 4, 8, 6) };
        editor.Children.Add(box);
        editor.Children.Add(buttons);
        body.Children.Add(editor);
        Dispatcher.UIThread.Post(() => box.Focus(), DispatcherPriority.Background);
    }

    private void SubmitComments()
    {
        if (_comments.Count == 0) return;
        var sb = new StringBuilder(Loc.Get("DiffCommentsHeader")).Append('\n');
        foreach (var c in _comments)
        {
            var where = c.Path + ":" + c.Line + (c.OldSide ? Loc.Get("DiffOldLineSuffix") : "");
            sb.Append("- ").Append(string.Format(Loc.Get("DiffCommentLine"), where, c.Text.Replace("\n", " "))).Append('\n');
        }
        _comments.Clear();
        _editing = null;
        Rebuild();
        CommentsSubmitted?.Invoke(sb.ToString().TrimEnd());
    }

    // ── Actions ──

    private async Task StageAsync(DiffFile f)
    {
        if (_repoRoot == null) return;
        var paths = new List<string> { f.Path };
        var r = f.IsStaged ? await GitWriteService.UnstageAsync(_repoRoot, paths) : await GitWriteService.StageAsync(_repoRoot, paths);
        if (!r.Ok) _summary.Text = r.Message;
        await LoadAsync(force: true);
    }

    /// <summary>Puts a tracked file back to HEAD, in both the index and the working tree.</summary>
    private async Task DiscardAsync(DiffFile f)
    {
        if (_repoRoot == null) return;
        var root = _repoRoot;
        var paths = new List<string> { GitCli.Pathspec(f.Path) };
        if (f.OldPath != null) paths.Add(GitCli.Pathspec(f.OldPath));
        var args = new List<string> { "restore", "--source=HEAD", "--staged", "--worktree", "--" };
        args.AddRange(paths);
        var r = await Task.Run(() => GitCli.Execute(root, null, args.ToArray()));
        if (!r.Ok) _summary.Text = r.Message;
        await LoadAsync(force: true);
    }
}
