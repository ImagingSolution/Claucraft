using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Claucraft.Services;

namespace Claucraft.Controls;

/// <summary>
/// Commit history for one repository, drawn as a graph with a detail pane underneath, in the
/// spirit of VS Code's Git Graph. Read-only throughout: it reads the log, lists what a commit
/// changed, and hands a single file's diff to <see cref="DiffWindow"/>. Nothing here writes to
/// the repository.
/// </summary>
public class CommitGraphWindow : Window
{
    private const int PageSize = 500;

    private readonly string _repoRoot;
    private readonly bool _isDark;
    private readonly Typeface _mono;

    private readonly CommitGraphView _view;
    private readonly ScrollViewer _scroller;
    private readonly Button _loadMoreButton;
    private readonly TextBlock _statusText;
    private readonly Grid _header;

    private readonly TextBlock _detailHash;
    private readonly TextBlock _detailAuthor;
    private readonly TextBlock _detailDate;
    private readonly SelectableTextBlock _detailMessage;
    private readonly TextBlock _filesHeading;
    private readonly StackPanel _fileRows;

    private int _limit = PageSize;

    /// <summary>What double-clicking each listed file does, in the order the files are shown.</summary>
    private readonly List<Action> _fileActions = new();

    /// <summary>Guards against a slow detail load landing after the selection has moved on.</summary>
    private int _detailGeneration;

    public CommitGraphWindow(string repoRoot, string repoLabel, bool isDark, Typeface mono)
    {
        _repoRoot = repoRoot;
        _isDark = isDark;
        _mono = mono;

        Title = string.IsNullOrEmpty(repoLabel)
            ? Loc.Get("CommitGraphTitle", "Commit Graph")
            : Loc.Get("CommitGraphTitle", "Commit Graph") + " - " + repoLabel;
        Width = 1100;
        Height = 780;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Bg());

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        // -- Toolbar --
        var refreshButton = ToolButton(Loc.Get("GraphRefresh", "Refresh"));
        refreshButton.Click += (_, _) => { _limit = PageSize; _ = ReloadAsync(); };

        _loadMoreButton = ToolButton(Loc.Get("GraphLoadMore", "Load more"));
        _loadMoreButton.IsEnabled = false;
        _loadMoreButton.Click += (_, _) => { _limit += PageSize; _ = ReloadAsync(); };

        _statusText = new TextBlock
        {
            Foreground = new SolidColorBrush(DimText()),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(10, 6),
        };
        toolbar.Children.Add(refreshButton);
        toolbar.Children.Add(_loadMoreButton);
        toolbar.Children.Add(_statusText);

        // -- Column header, aligned to the graph once its width is known --
        _header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            Margin = new Thickness(0, 0, 0, 0),
        };
        AddHeaderCell(Loc.Get("GraphColumnDescription", "Description"), 0, double.NaN);
        AddHeaderCell(Loc.Get("GraphColumnAuthor", "Author"), 1, CommitGraphView.AuthorWidth);
        AddHeaderCell(Loc.Get("GraphColumnDate", "Date"), 2, CommitGraphView.DateWidth);

        var headerBorder = new Border
        {
            Child = _header,
            Padding = new Thickness(0, 4),
            Background = new SolidColorBrush(PanelBg()),
            BorderBrush = new SolidColorBrush(Divider()),
            BorderThickness = new Thickness(0, 1, 0, 1),
        };

        // -- Graph list --
        _view = new CommitGraphView(_isDark);
        _view.SelectionChanged += (_, _) => ShowSelection();
        _view.RowActivated += (_, _) => OpenSelectedFileDiff();

        _scroller = new ScrollViewer
        {
            Content = _view,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        _view.AttachScroller(_scroller);

        // -- Detail pane --
        _detailHash = new TextBlock
        {
            FontFamily = _mono.FontFamily,
            FontSize = 12,
            Foreground = new SolidColorBrush(TextColor()),
        };
        _detailAuthor = new TextBlock { FontSize = 12, Foreground = new SolidColorBrush(DimText()) };
        _detailDate = new TextBlock { FontSize = 12, Foreground = new SolidColorBrush(DimText()) };

        var metaRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 16,
            Margin = new Thickness(12, 8, 12, 6),
        };
        metaRow.Children.Add(_detailHash);
        metaRow.Children.Add(_detailAuthor);
        metaRow.Children.Add(_detailDate);

        _detailMessage = new SelectableTextBlock
        {
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(TextColor()),
            Margin = new Thickness(12, 0, 12, 10),
        };

        _filesHeading = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(DimText()),
            Margin = new Thickness(12, 0, 12, 4),
        };

        _fileRows = new StackPanel { Margin = new Thickness(4, 0, 4, 8) };

        var detailBody = new Grid { ColumnDefinitions = new ColumnDefinitions("*,340") };

        var messageScroller = new ScrollViewer
        {
            Content = _detailMessage,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        Grid.SetColumn(messageScroller, 0);
        detailBody.Children.Add(messageScroller);

        var filesPanel = new DockPanel();
        DockPanel.SetDock(_filesHeading, Dock.Top);
        filesPanel.Children.Add(_filesHeading);
        filesPanel.Children.Add(new ScrollViewer
        {
            Content = _fileRows,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        });

        var filesBorder = new Border
        {
            Child = filesPanel,
            BorderBrush = new SolidColorBrush(Divider()),
            BorderThickness = new Thickness(1, 0, 0, 0),
        };
        Grid.SetColumn(filesBorder, 1);
        detailBody.Children.Add(filesBorder);

        var detailPane = new DockPanel { Background = new SolidColorBrush(PanelBg()) };
        DockPanel.SetDock(metaRow, Dock.Top);
        detailPane.Children.Add(metaRow);
        detailPane.Children.Add(detailBody);

        // -- Assembly --
        var split = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto,260"),
        };

        var listArea = new DockPanel();
        DockPanel.SetDock(headerBorder, Dock.Top);
        listArea.Children.Add(headerBorder);
        listArea.Children.Add(_scroller);
        Grid.SetRow(listArea, 0);
        split.Children.Add(listArea);

        var splitter = new GridSplitter
        {
            Height = 4,
            Background = new SolidColorBrush(Divider()),
            ResizeDirection = GridResizeDirection.Rows,
        };
        Grid.SetRow(splitter, 1);
        split.Children.Add(splitter);

        Grid.SetRow(detailPane, 2);
        split.Children.Add(detailPane);

        var toolbarBorder = new Border
        {
            Child = toolbar,
            BorderBrush = new SolidColorBrush(Divider()),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };

        var root = new DockPanel();
        DockPanel.SetDock(toolbarBorder, Dock.Top);
        root.Children.Add(toolbarBorder);
        root.Children.Add(split);
        Content = root;

        Opened += (_, _) => { FitToScreen(); _ = ReloadAsync(); };
    }

    /// <summary>
    /// Pulls the window back inside the screen. The preferred size is roomy, and on a display
    /// with little logical space -- a high-DPI laptop panel, say -- it would otherwise open with
    /// the detail pane hanging off the bottom edge.
    /// </summary>
    private void FitToScreen()
    {
        try
        {
            var area = Screens.ScreenFromWindow(this)?.WorkingArea ?? Screens.Primary?.WorkingArea;
            if (area is not { } bounds) return;

            double scale = RenderScaling > 0 ? RenderScaling : 1;
            double maxWidth = bounds.Width / scale * 0.94;
            double maxHeight = bounds.Height / scale * 0.94;

            Width = Math.Min(Width, maxWidth);
            Height = Math.Min(Height, maxHeight);
        }
        catch
        {
            // A window that cannot ask about screens keeps the size it was given.
        }
    }

    // -- Loading --------------------------------------------------------

    private async Task ReloadAsync()
    {
        _statusText.Text = Loc.Get("GraphLoading", "Loading...");
        _loadMoreButton.IsEnabled = false;

        var commits = await GitLogService.GetLogAsync(_repoRoot, _limit);
        bool dirty = await GitLogService.HasUncommittedChangesAsync(_repoRoot);

        _view.SetGraph(CommitGraphLayout.Build(commits), dirty);
        _header.Margin = new Thickness(_view.GraphWidth, 0, 0, 0);

        _statusText.Text = commits.Count == 0
            ? Loc.Get("GraphNoCommits", "No commits to show")
            : Format(Loc.Get("GraphCommitCountFmt", "{0} commits"), commits.Count);

        // A short page means the log ran out, so there is nothing further to fetch.
        _loadMoreButton.IsEnabled = commits.Count >= _limit;
    }

    // -- Detail pane ----------------------------------------------------

    private void ShowSelection()
    {
        int generation = ++_detailGeneration;

        _fileRows.Children.Clear();
        _fileActions.Clear();

        if (_view.IsUncommittedSelected)
        {
            _detailHash.Text = "";
            _detailAuthor.Text = "";
            _detailDate.Text = "";
            _detailMessage.Text = Loc.Get("GraphUncommitted", "Uncommitted Changes");
            _filesHeading.Text = Loc.Get("GraphChangedFiles", "Changed files");
            _ = LoadWorkingTreeFilesAsync(generation);
            return;
        }

        var commit = _view.SelectedCommit;
        if (commit == null)
        {
            _detailHash.Text = "";
            _detailAuthor.Text = "";
            _detailDate.Text = "";
            _detailMessage.Text = "";
            _filesHeading.Text = "";
            return;
        }

        _detailHash.Text = commit.Hash;
        _detailAuthor.Text = string.IsNullOrEmpty(commit.AuthorEmail)
            ? commit.Author
            : $"{commit.Author} <{commit.AuthorEmail}>";
        _detailDate.Text = commit.Date == default
            ? ""
            : commit.Date.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);

        _detailMessage.Text = string.IsNullOrEmpty(commit.Body)
            ? commit.Subject
            : commit.Subject + "\n\n" + commit.Body;

        _filesHeading.Text = Loc.Get("GraphChangedFiles", "Changed files");
        _ = LoadCommitFilesAsync(commit, generation);
    }

    private async Task LoadCommitFilesAsync(GitCommit commit, int generation)
    {
        var files = await GitLogService.GetCommitFilesAsync(_repoRoot, commit.Hash);
        if (generation != _detailGeneration) return;

        _filesHeading.Text = Format(Loc.Get("GraphChangedFilesFmt", "Changed files ({0})"), files.Count);

        if (files.Count == 0)
        {
            _fileRows.Children.Add(EmptyNote(Loc.Get("GraphNoFiles", "No files changed")));
            return;
        }

        foreach (var file in files)
        {
            var captured = file;
            AddFileRow(FileRow(file.StatusGlyph, file.Path, file.OldPath),
                () => _ = OpenCommitDiffAsync(commit, captured));
        }
    }

    private async Task LoadWorkingTreeFilesAsync(int generation)
    {
        var changes = await GitChangeService.GetChangesAsync(_repoRoot);
        if (generation != _detailGeneration) return;

        _filesHeading.Text = Format(Loc.Get("GraphChangedFilesFmt", "Changed files ({0})"), changes.Count);

        if (changes.Count == 0)
        {
            _fileRows.Children.Add(EmptyNote(Loc.Get("GraphNoFiles", "No files changed")));
            return;
        }

        foreach (var change in changes)
        {
            var captured = change;
            AddFileRow(FileRow(change.StatusGlyph, change.Path, null),
                () => _ = OpenWorkingTreeDiffAsync(captured));
        }
    }

    private void AddFileRow(Control row, Action open)
    {
        row.DoubleTapped += (_, _) => open();
        _fileRows.Children.Add(row);
        _fileActions.Add(open);
    }

    private void OpenSelectedFileDiff()
    {
        // Double-clicking a commit row opens the first file it touched, which is the quickest
        // way into a one-file commit; the file list stays there for anything larger.
        if (_fileActions.Count > 0) _fileActions[0]();
    }

    private async Task OpenCommitDiffAsync(GitCommit commit, GitFileChange file)
    {
        var diff = await GitLogService.GetCommitFileDiffAsync(_repoRoot, commit.Hash, file.Path);
        ShowDiff($"{file.Path} @ {commit.ShortHash}", diff);
    }

    private async Task OpenWorkingTreeDiffAsync(GitChange change)
    {
        var diff = await GitChangeService.GetDiffAsync(_repoRoot, change);
        ShowDiff(change.Path, diff);
    }

    private void ShowDiff(string title, string diff)
    {
        if (string.IsNullOrWhiteSpace(diff)) return;
        new DiffWindow(title, diff, _isDark, _mono).Show(this);
    }

    // -- Small pieces ---------------------------------------------------

    private Control FileRow(string glyph, string path, string? oldPath)
    {
        var statusColor = glyph switch
        {
            "A" => Color.FromRgb(0x30, 0xD1, 0x58),
            "D" => Color.FromRgb(0xFF, 0x45, 0x3A),
            "R" or "C" => Color.FromRgb(0xD2, 0x99, 0x22),
            _ => Color.FromRgb(0x3B, 0x8E, 0xEA),
        };

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(8, 2),
        };
        panel.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = _mono.FontFamily,
            FontSize = 11,
            Width = 12,
            Foreground = new SolidColorBrush(statusColor),
            VerticalAlignment = VerticalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = oldPath == null ? path : $"{path}  ({oldPath})",
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(TextColor()),
            VerticalAlignment = VerticalAlignment.Center,
        });

        return new Border
        {
            Child = panel,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            CornerRadius = new CornerRadius(4),
        };
    }

    private TextBlock EmptyNote(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = new SolidColorBrush(DimText()),
        Margin = new Thickness(12, 4),
    };

    private void AddHeaderCell(string text, int column, double width)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(DimText()),
            Margin = new Thickness(0, 0, 0, 0),
        };
        if (!double.IsNaN(width)) block.Width = width;
        Grid.SetColumn(block, column);
        _header.Children.Add(block);
    }

    private Button ToolButton(string text) => new()
    {
        Content = text,
        FontSize = 12,
        Padding = new Thickness(10, 4),
        Background = new SolidColorBrush(_isDark ? Color.FromRgb(50, 50, 52) : Color.FromRgb(230, 230, 235)),
        Foreground = new SolidColorBrush(TextColor()),
        BorderThickness = new Thickness(0),
        CornerRadius = new CornerRadius(4),
        Cursor = new Cursor(StandardCursorType.Hand),
        Margin = new Thickness(0, 0, 6, 0),
    };

    private static string Format(string format, object arg)
    {
        try { return string.Format(CultureInfo.CurrentCulture, format, arg); }
        catch { return format; }
    }

    // -- Theme ----------------------------------------------------------

    private Color Bg() => _isDark ? Color.FromRgb(28, 28, 30) : Color.FromRgb(255, 255, 255);

    private Color PanelBg() => _isDark ? Color.FromRgb(36, 36, 38) : Color.FromRgb(246, 246, 249);

    private Color Divider() => _isDark ? Color.FromRgb(56, 56, 58) : Color.FromRgb(210, 210, 215);

    private Color TextColor() => _isDark ? Color.FromRgb(220, 220, 225) : Color.FromRgb(28, 28, 30);

    private Color DimText() => _isDark ? Color.FromRgb(140, 140, 148) : Color.FromRgb(110, 110, 118);
}
