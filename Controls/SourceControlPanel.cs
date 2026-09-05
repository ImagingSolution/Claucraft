using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Claucraft.Services;

namespace Claucraft.Controls;

/// <summary>
/// The few things the panel needs from the window it sits in: somewhere to put text the user
/// wants the AI to read, and the three dialogs the app already has. Bundled into one record so
/// the panel's constructor does not grow a parameter per callback.
/// </summary>
public sealed record SourceControlHost(
    Action<string> SendToTerminal,
    Action<string, string> ShowMessage,
    Func<string, string, Task<bool>> Confirm,
    Func<string, string, string, Task<string?>> TextInput);

/// <summary>
/// Everything git and GitHub in one panel: what has changed, what is staged, the branch and how
/// far it has drifted from its remote, the open pull requests, and the history underneath.
///
/// It is built for someone who does not know git. Every button is one named action - 取得 / 取込
/// / 送信 rather than fetch / pull / push - and nothing here can lose committed work: pull
/// rebases, branch deletion is <c>-d</c> only, and a conflict is answered with "abort" or "ask
/// the AI" rather than a hand-written merge. When git or gh refuses, its own words are shown
/// unedited, because the reason is usually the instruction.
/// </summary>
public sealed class SourceControlPanel : UserControl
{
    /// <summary>How much history the embedded graph shows. The window is there for more.</summary>
    private const int GraphLimit = 200;

    /// <summary>Interval of the background fetch, when it is switched on.</summary>
    private static readonly TimeSpan AutoFetchInterval = TimeSpan.FromMinutes(5);

    private readonly bool _isDark;
    private readonly Typeface _mono;
    private readonly AppSettings _settings;
    private readonly CliProviderService _cli;
    private readonly SourceControlHost _host;

    // ── Repository state ──

    /// <summary>The project folder as the window gave it, before git had a say.</summary>
    private string _folder = "";

    /// <summary>Top of the working tree, or empty when the folder is not in a repository.</summary>
    private string _repo = "";

    private List<GitChange> _changes = new();
    private BranchState _branch = BranchState.None;
    private RepoOperation _operation = RepoOperation.None;
    private List<string> _conflicts = new();
    private List<PullRequestInfo> _pullRequests = new();

    /// <summary>Null until gh has been asked once; false hides the pull-request section.</summary>
    private bool? _ghReady;

    /// <summary>Whether the repository open right now is hosted on GitHub at all.</summary>
    private bool _onGitHub;

    /// <summary>One write at a time - every button is rebuilt from the result of the last one.</summary>
    private bool _busy;

    /// <summary>Set while the "stage all" box is being brought in line with the list.</summary>
    private bool _suppressStageAll;

    /// <summary>A reload started later owns the view; earlier ones drop their results.</summary>
    private int _refreshGeneration;

    private bool _panelShown;

    private DispatcherTimer? _fetchTimer;
    private CancellationTokenSource? _generateCts;

    // ── Controls ──

    private readonly Button _btnFetch;
    private readonly Button _btnPull;
    private readonly Button _btnPush;
    private readonly Button _btnNewBranch;
    private readonly Button _btnMerge;
    private readonly Button _btnPr;
    private readonly Button _btnRefresh;
    private readonly Button _btnExpand;

    private readonly Button _btnBranch;
    private readonly TextBlock _lblTracking;
    private readonly TextBlock _status;

    private readonly Border _conflictBanner;
    private readonly TextBlock _lblConflicts;
    private readonly Button _btnConflictAsk;
    private readonly Button _btnConflictContinue;
    private readonly Button _btnConflictAbort;

    private readonly Expander _prExpander;
    private readonly StackPanel _prList;

    private readonly CheckBox _chkStageAll;
    private readonly TextBlock _lblSummary;
    private readonly StackPanel _changesList;

    private readonly Border _commitBox;
    private readonly TextBox _txtMessage;
    private readonly Button _btnGenerate;
    private readonly Button _btnLanguage;
    private readonly Button _btnCommit;
    private readonly Button _btnCommitPush;

    private readonly CommitGraphView _graph;

    /// <summary>Raised after a write, so the window can bring its own git readouts up to date.</summary>
    public event EventHandler? GitChanged;

    public SourceControlPanel(bool isDark, Typeface mono, AppSettings settings,
        CliProviderService cli, SourceControlHost host)
    {
        _isDark = isDark;
        _mono = mono;
        _settings = settings;
        _cli = cli;
        _host = host;

        // ── Toolbar ──

        _btnFetch = ToolButton(Loc.Get("FetchAction", "Fetch"), Loc.Get("FetchTooltip", ""), OnFetch);
        _btnPull = ToolButton(Loc.Get("PullAction", "Pull"), Loc.Get("PullTooltip", ""), OnPull);
        _btnPush = ToolButton(Loc.Get("PushAction", "Push"), Loc.Get("PushTooltip", ""), OnPush);
        _btnNewBranch = ToolButton(Loc.Get("NewBranch", "New branch"), "", OnNewBranch);
        _btnMerge = ToolButton(Loc.Get("MergeAction", "Merge"), Loc.Get("MergeTooltip", ""), OnMerge);
        _btnPr = ToolButton(Loc.Get("CreatePrAction", "Pull request"), "", OnCreatePullRequest);
        _btnRefresh = ToolButton("⟳", Loc.Get("Refresh", "Refresh"), () => _ = RefreshAsync());
        _btnExpand = ToolButton("⤢", Loc.Get("OpenGraphAction", "Open in a window"), OpenGraphWindow);

        var remoteRow = Row(_btnFetch, _btnPull, _btnPush);
        var branchRow = Row(_btnNewBranch, _btnMerge, _btnPr);
        // Refresh and "open in a window" ride along on the branch row below. A third toolbar row
        // costs the file list a line it cannot spare at sidebar width.
        var viewRow = Row(_btnRefresh, _btnExpand);
        viewRow.Margin = new Thickness(6, 0, 0, 0);

        var toolbarStack = new StackPanel { Spacing = 4, Margin = new Thickness(8, 6) };
        toolbarStack.Children.Add(remoteRow);
        toolbarStack.Children.Add(branchRow);

        var toolbar = new Border
        {
            Child = toolbarStack,
            BorderBrush = new SolidColorBrush(Divider()),
            BorderThickness = new Thickness(0, 0, 0, 0.5),
        };
        DockPanel.SetDock(toolbar, Dock.Top);

        // ── Branch row ──

        _btnBranch = new Button
        {
            Content = "-",
            FontSize = 12,
            Padding = new Thickness(6, 2),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        ToolTip.SetTip(_btnBranch, Loc.Get("BranchMenuTooltip", "Switch, create or delete a branch"));
        _btnBranch.Click += (_, _) => ShowBranchMenu();

        _lblTracking = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(DimText()),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var branchGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        var branchGlyph = new TextBlock
        {
            Text = "⎇",
            FontSize = 13,
            Margin = new Thickness(8, 0, 0, 0),
            Foreground = new SolidColorBrush(DimText()),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(branchGlyph, 0);
        Grid.SetColumn(_btnBranch, 1);
        Grid.SetColumn(_lblTracking, 2);
        Grid.SetColumn(viewRow, 3);
        branchGrid.Children.Add(branchGlyph);
        branchGrid.Children.Add(_btnBranch);
        branchGrid.Children.Add(_lblTracking);
        branchGrid.Children.Add(viewRow);

        var branchBorder = new Border
        {
            Child = branchGrid,
            Padding = new Thickness(0, 2, 6, 2),
            BorderBrush = new SolidColorBrush(Divider()),
            BorderThickness = new Thickness(0, 0.5, 0, 0.5),
        };
        DockPanel.SetDock(branchBorder, Dock.Top);

        // ── Status line ──

        _status = new TextBlock
        {
            FontSize = 11,
            IsVisible = false,
            Margin = new Thickness(10, 4, 10, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(DimText()),
        };
        DockPanel.SetDock(_status, Dock.Top);

        // ── Conflict banner ──

        _lblConflicts = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(WarningText()),
        };
        _btnConflictAsk = ToolButton(Loc.Get("AskAiAction", "Ask the AI"), "", OnAskAiAboutConflict);
        _btnConflictContinue = ToolButton(Loc.Get("ContinueAction", "Continue"), "", OnContinueOperation);
        _btnConflictAbort = ToolButton(Loc.Get("AbortAction", "Abort"), "", OnAbortOperation);

        var conflictStack = new StackPanel { Spacing = 6 };
        conflictStack.Children.Add(_lblConflicts);
        conflictStack.Children.Add(Row(_btnConflictAsk, _btnConflictContinue, _btnConflictAbort));

        _conflictBanner = new Border
        {
            Child = conflictStack,
            IsVisible = false,
            Padding = new Thickness(10, 8),
            Background = new SolidColorBrush(WarningBg()),
            BorderBrush = new SolidColorBrush(WarningBorder()),
            BorderThickness = new Thickness(0, 0, 0, 0.5),
        };
        DockPanel.SetDock(_conflictBanner, Dock.Top);

        // ── Pull requests ──

        _prList = new StackPanel { Spacing = 2, Margin = new Thickness(0, 4, 0, 0) };
        _prExpander = new Expander
        {
            Header = Loc.Get("PullRequests", "Pull requests"),
            Content = _prList,
            IsVisible = false,
            FontSize = 12,
            Padding = new Thickness(8, 4),
        };
        DockPanel.SetDock(_prExpander, Dock.Top);

        // ── Commit box ──

        _txtMessage = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 52,
            MaxHeight = 120,
            FontSize = 12,
            PlaceholderText = Loc.Get("CommitMessage", "Commit message"),
        };

        _btnGenerate = ToolButton(Loc.Get("GenerateMessage", "AI draft"), "", OnGenerateMessage);
        _btnLanguage = ToolButton("EN", Loc.Get("CommitLanguageTooltip", ""), OnToggleLanguage);
        _btnLanguage.MinWidth = 34;

        _btnCommit = ToolButton(Loc.Get("CommitAction", "Commit"), "", OnCommit);
        _btnCommit.HorizontalAlignment = HorizontalAlignment.Stretch;
        _btnCommitPush = ToolButton(Loc.Get("CommitAndPush", "Commit & push"), "", OnCommitAndPush);

        var draftRow = Row(_btnGenerate, _btnLanguage);
        var commitRow = Row(_btnCommit, _btnCommitPush);

        var commitStack = new StackPanel { Spacing = 6 };
        commitStack.Children.Add(_txtMessage);
        commitStack.Children.Add(draftRow);
        commitStack.Children.Add(commitRow);

        _commitBox = new Border
        {
            Child = commitStack,
            IsVisible = false,
            Padding = new Thickness(8, 8),
            BorderBrush = new SolidColorBrush(Divider()),
            BorderThickness = new Thickness(0, 0.5, 0, 0),
        };
        DockPanel.SetDock(_commitBox, Dock.Bottom);

        // ── Changes list ──

        _chkStageAll = new CheckBox
        {
            MinWidth = 0,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 4, 0),
            IsVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(_chkStageAll, Loc.Get("StageAll", "Stage all"));
        _chkStageAll.IsCheckedChanged += (_, _) => OnStageAllChanged();

        _lblSummary = new TextBlock
        {
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(DimText()),
        };

        var listHeader = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(10, 6, 10, 2),
        };
        Grid.SetColumn(_chkStageAll, 0);
        Grid.SetColumn(_lblSummary, 1);
        listHeader.Children.Add(_chkStageAll);
        listHeader.Children.Add(_lblSummary);
        DockPanel.SetDock(listHeader, Dock.Top);

        _changesList = new StackPanel { Spacing = 1, Margin = new Thickness(4, 2) };
        var changesScroller = new ScrollViewer
        {
            Content = _changesList,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var changesArea = new DockPanel();
        changesArea.Children.Add(listHeader);
        changesArea.Children.Add(changesScroller);

        // ── Graph ──

        _graph = new CommitGraphView(_isDark) { CompactColumns = true };
        _graph.RowActivated += (_, _) => OpenGraphWindow();

        var graphScroller = new ScrollViewer
        {
            Content = _graph,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        _graph.AttachScroller(graphScroller);

        var graphHeading = new TextBlock
        {
            Text = Loc.Get("HistorySection", "History"),
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(10, 5, 10, 3),
            Foreground = new SolidColorBrush(DimText()),
        };
        DockPanel.SetDock(graphHeading, Dock.Top);

        var graphArea = new DockPanel();
        graphArea.Children.Add(graphHeading);
        graphArea.Children.Add(graphScroller);

        var graphBorder = new Border
        {
            Child = graphArea,
            BorderBrush = new SolidColorBrush(Divider()),
            BorderThickness = new Thickness(0, 0.5, 0, 0),
        };

        // The two halves are resizable because which one matters depends on the moment: reviewing
        // a change wants the file list, working out what to branch from wants the history.
        var centre = new Grid { RowDefinitions = new RowDefinitions("3*,4,2*") };
        var splitter = new GridSplitter
        {
            Height = 4,
            ResizeDirection = GridResizeDirection.Rows,
            Background = new SolidColorBrush(Divider()),
        };
        Grid.SetRow(changesArea, 0);
        Grid.SetRow(splitter, 1);
        Grid.SetRow(graphBorder, 2);
        centre.Children.Add(changesArea);
        centre.Children.Add(splitter);
        centre.Children.Add(graphBorder);

        var root = new DockPanel();
        root.Children.Add(toolbar);
        root.Children.Add(branchBorder);
        root.Children.Add(_status);
        root.Children.Add(_conflictBanner);
        root.Children.Add(_prExpander);
        root.Children.Add(_commitBox);
        root.Children.Add(centre);

        Content = root;
        ApplyState();
    }

    // ── Lifecycle ──────────────────────────────────────────────────────

    /// <summary>
    /// Points the panel at another project. Called for every route that changes the active
    /// project, including the ones that clear it, so the panel can never show a stale repository.
    /// </summary>
    public async void SetRepository(string? projectFolder)
    {
        var next = projectFolder ?? "";
        if (string.Equals(next, _folder, StringComparison.OrdinalIgnoreCase)) return;

        _folder = next;
        _repo = "";
        _changes = new List<GitChange>();
        _branch = BranchState.None;
        _operation = RepoOperation.None;
        _conflicts = new List<string>();
        _pullRequests = new List<PullRequestInfo>();
        _changesList.Children.Clear();
        _prList.Children.Clear();
        _graph.SetGraph(CommitGraphLayout.Build(new List<GitCommit>()), false);
        _txtMessage.Text = "";
        ApplyState();

        if (next.Length > 0)
        {
            var root = await Task.Run(() => GitCli.FindRepoRoot(next));

            // The project may have changed again while git was answering.
            if (!string.Equals(next, _folder, StringComparison.OrdinalIgnoreCase)) return;
            _repo = root ?? "";
        }

        if (_panelShown) await RefreshAsync();
        else ApplyState();
    }

    /// <summary>Called when the panel becomes the visible one in the sidebar.</summary>
    public void OnPanelShown()
    {
        _panelShown = true;
        _ = RefreshAsync();

        if (_fetchTimer == null)
        {
            _fetchTimer = new DispatcherTimer { Interval = AutoFetchInterval };
            _fetchTimer.Tick += (_, _) => _ = AutoFetchAsync();
        }
        _fetchTimer.Start();
    }

    public void OnPanelHidden()
    {
        _panelShown = false;
        _fetchTimer?.Stop();
    }

    /// <summary>
    /// The settings screen owns the same two preferences this panel shows on its toolbar, so a
    /// change there has to reach the [JA|EN] toggle here.
    /// </summary>
    public void OnSettingsChanged() => ApplyState();

    // ── Reload ─────────────────────────────────────────────────────────

    /// <summary>
    /// Rereads everything the panel shows. Cheap enough to call on any change - the four git
    /// readings run together - and safe to call while another reload is in flight.
    /// </summary>
    public async Task RefreshAsync()
    {
        // Nothing here is visible while another sidebar panel is up, and each reload is four
        // git processes, so a hidden panel simply waits for OnPanelShown.
        if (!_panelShown) return;

        int generation = ++_refreshGeneration;

        if (_repo.Length == 0)
        {
            _changes = new List<GitChange>();
            _changesList.Children.Clear();
            _lblSummary.Text = Loc.Get("NotAGitRepo");
            _branch = BranchState.None;
            _operation = RepoOperation.None;
            _conflicts = new List<string>();
            _graph.SetGraph(CommitGraphLayout.Build(new List<GitCommit>()), false);
            ApplyState();
            return;
        }

        var repo = _repo;
        if (_changes.Count == 0) _lblSummary.Text = Loc.Get("LoadingChanges");

        var changesTask = GitChangeService.GetChangesAsync(repo);
        var branchTask = GitWriteService.GetBranchStateAsync(repo);
        var operationTask = GitWriteService.GetRepoOperationAsync(repo);
        var conflictTask = GitWriteService.GetConflictsAsync(repo);
        var logTask = GitLogService.GetLogAsync(repo, GraphLimit);

        try
        {
            await Task.WhenAll(changesTask, branchTask, operationTask, conflictTask, logTask);
        }
        catch
        {
            // Each service already answers with an empty result rather than throwing; this only
            // catches the unexpected, and the checks below then keep whatever did arrive.
        }

        if (generation != _refreshGeneration) return;

        _changes = Settled(changesTask) ?? new List<GitChange>();
        _branch = Settled(branchTask) ?? BranchState.None;
        _operation = operationTask.IsCompletedSuccessfully ? operationTask.Result : RepoOperation.None;
        _conflicts = Settled(conflictTask) ?? new List<string>();
        var commits = Settled(logTask) ?? new List<GitCommit>();

        BuildChangesList();
        _graph.SetGraph(CommitGraphLayout.Build(commits), _changes.Count > 0, keepSelection: true);
        ApplyState();

        _ = RefreshPullRequestsAsync(generation);
    }

    /// <summary>The result of a reading that finished, or null for one that faulted.</summary>
    private static T? Settled<T>(Task<T> task) where T : class =>
        task.IsCompletedSuccessfully ? task.Result : null;

    private async Task AutoFetchAsync()
    {
        if (!_settings.GitAutoFetch || !_panelShown || _busy || _repo.Length == 0) return;

        // Quiet on purpose: an offline laptop must not produce a dialog every five minutes.
        var result = await GitWriteService.FetchAsync(_repo, quiet: true);
        if (result.Ok) await RefreshAsync();
    }

    // ── Enabling and labelling ─────────────────────────────────────────

    /// <summary>
    /// Brings every control in line with what the repository currently is. The single place that
    /// decides what is clickable, so "busy" cannot leak a half-enabled toolbar.
    /// </summary>
    private void ApplyState()
    {
        bool isRepo = _repo.Length > 0;
        bool idle = isRepo && !_busy;
        bool settled = idle && _operation == RepoOperation.None;
        bool hasBranch = _branch.Current.Length > 0;
        int staged = _changes.Count(c => c.Staged);

        _btnFetch.IsEnabled = idle;
        _btnPull.IsEnabled = settled;
        _btnPush.IsEnabled = settled && hasBranch;
        _btnNewBranch.IsEnabled = settled;
        _btnMerge.IsEnabled = settled;
        _btnPr.IsEnabled = settled && hasBranch && _ghReady == true && _onGitHub;
        _btnRefresh.IsEnabled = idle;
        _btnExpand.IsEnabled = isRepo;
        _btnBranch.IsEnabled = settled;

        _btnPull.Content = _branch.Behind > 0
            ? Loc.Get("PullAction", "Pull") + " ↓" + _branch.Behind
            : Loc.Get("PullAction", "Pull");
        _btnPush.Content = _branch.Ahead > 0
            ? Loc.Get("PushAction", "Push") + " ↑" + _branch.Ahead
            : Loc.Get("PushAction", "Push");

        _btnBranch.Content = hasBranch ? _branch.Current : "-";
        _lblTracking.Text = !hasBranch ? ""
            : !_branch.HasUpstream ? Loc.Get("NoUpstream", "not published")
            : _branch.Ahead == 0 && _branch.Behind == 0 ? Loc.Get("UpToDate", "up to date")
            : $"↑{_branch.Ahead} ↓{_branch.Behind}";

        _chkStageAll.IsVisible = isRepo && _changes.Count > 0;
        _suppressStageAll = true;
        _chkStageAll.IsChecked = _changes.Count > 0 && staged == _changes.Count;
        _suppressStageAll = false;

        _commitBox.IsVisible = isRepo;
        _btnCommit.IsEnabled = settled && staged > 0;
        _btnCommitPush.IsEnabled = settled && staged > 0 && hasBranch;

        // A CLI without a one-shot mode cannot draft anything, and the tooltip says which.
        bool canDraft = !string.IsNullOrWhiteSpace(_cli.Active.OneShotArgs);
        _btnGenerate.IsEnabled = settled && staged > 0 && canDraft;
        ToolTip.SetTip(_btnGenerate, canDraft
            ? string.Format(Loc.Get("GenerateTooltipFmt", "Draft a message with {0}"), _cli.Active.Name)
            : string.Format(Loc.Get("GenerateUnavailableFmt", "{0} has no one-shot mode"), _cli.Active.Name));

        _btnLanguage.Content =
            CommitMessageService.ResolveLanguage(_settings.CommitMessageLanguage) == "ja" ? "日" : "EN";

        bool interrupted = _operation != RepoOperation.None || _conflicts.Count > 0;
        _conflictBanner.IsVisible = isRepo && interrupted;
        if (interrupted)
        {
            _lblConflicts.Text = _conflicts.Count > 0
                ? string.Format(Loc.Get("ConflictCountFmt", "{0} file(s) conflict"), _conflicts.Count)
                : Loc.Get(_operation == RepoOperation.Rebase ? "RebaseInProgress" : "MergeInProgress",
                    "An operation is unfinished");
            _btnConflictAsk.IsEnabled = idle && _conflicts.Count > 0;
            _btnConflictContinue.IsEnabled = idle && _conflicts.Count == 0
                && _operation != RepoOperation.None;
            _btnConflictAbort.IsEnabled = idle && _operation != RepoOperation.None;
        }
    }

    private void BuildChangesList()
    {
        _changesList.Children.Clear();

        _lblSummary.Text = _changes.Count == 0
            ? Loc.Get("NoChanges")
            : string.Format(Loc.Get("ChangedFilesCount"), _changes.Count);

        if (_changes.Count == 0) return;

        // Staged first and under its own heading: what the next commit will contain is the one
        // thing a newcomer most often gets wrong.
        var staged = _changes.Where(c => c.Staged).ToList();
        var unstaged = _changes.Where(c => !c.Staged).ToList();

        if (staged.Count > 0)
        {
            _changesList.Children.Add(SectionHeading(
                string.Format(Loc.Get("StagedSectionFmt", "Staged ({0})"), staged.Count)));
            foreach (var change in staged)
                _changesList.Children.Add(BuildChangeRow(change));
        }

        if (unstaged.Count > 0)
        {
            _changesList.Children.Add(SectionHeading(
                string.Format(Loc.Get("UnstagedSectionFmt", "Changes ({0})"), unstaged.Count)));
            foreach (var change in unstaged)
                _changesList.Children.Add(BuildChangeRow(change));
        }
    }

    private TextBlock SectionHeading(string text) => new()
    {
        Text = text,
        FontSize = 10.5,
        FontWeight = FontWeight.SemiBold,
        Margin = new Thickness(6, 6, 0, 2),
        Foreground = new SolidColorBrush(DimText()),
    };

    private Control BuildChangeRow(GitChange change)
    {
        var repo = _repo;
        var glyphColor = change.StatusGlyph switch
        {
            "A" => Color.FromRgb(48, 209, 88),
            "D" => Color.FromRgb(255, 69, 58),
            "R" => Color.FromRgb(10, 132, 255),
            "?" => Color.FromRgb(142, 142, 147),
            _ => Color.FromRgb(255, 214, 10),
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*") };

        // Staged state is git's, not the panel's: the box shows what the index already holds and
        // a click asks git to change it, so the two can never drift.
        var stage = new CheckBox
        {
            IsChecked = change.Staged,
            MinWidth = 0,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(stage, Loc.Get(change.Staged ? "UnstageFile" : "StageFile"));
        stage.IsCheckedChanged += (_, _) =>
        {
            bool want = stage.IsChecked == true;
            if (want == change.Staged || _busy) return;
            var one = new List<string> { change.Path };
            _ = RunAsync(Loc.Get(want ? "StagingStatus" : "UnstagingStatus", "..."),
                () => want
                    ? GitWriteService.StageAsync(repo, one)
                    : GitWriteService.UnstageAsync(repo, one));
        };

        var glyph = new TextBlock
        {
            Text = change.StatusGlyph,
            Width = 14,
            FontSize = 11,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(glyphColor),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var name = new TextBlock
        {
            Text = change.DisplayName,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var dir = new TextBlock
        {
            Text = change.DisplayDir,
            FontSize = 10,
            Opacity = 0.55,
            Margin = new Thickness(6, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(stage, 0);
        Grid.SetColumn(glyph, 1);
        Grid.SetColumn(name, 2);
        Grid.SetColumn(dir, 3);
        grid.Children.Add(stage);
        grid.Children.Add(glyph);
        grid.Children.Add(name);
        grid.Children.Add(dir);

        var row = new Border
        {
            Padding = new Thickness(6, 4),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = grid,
        };
        ToolTip.SetTip(row, change.Path + "  -  " + change.StatusLabel);

        var hover = new SolidColorBrush(_isDark
            ? Color.FromArgb(30, 255, 255, 255)
            : Color.FromArgb(20, 0, 0, 0));
        row.PointerEntered += (_, _) => row.Background = hover;
        row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
        // Left button only, or the right-click that opens the menu below would open the diff
        // window behind it as well.
        row.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) ShowDiff(change);
        };

        var comment = new MenuItem { Header = Loc.Get("CommentOnFile", "Comment on this file...") };
        comment.Click += (_, _) => CommentOnFile(change);
        row.ContextMenu = new ContextMenu { ItemsSource = new[] { comment } };

        return row;
    }

    // ── Running one git write ──────────────────────────────────────────

    /// <summary>
    /// Runs one write with the whole panel disabled, shows git's own words when it refuses, and
    /// reloads afterwards whether it worked or not - a failed merge still leaves state behind.
    ///
    /// <paramref name="quietOnConflict"/> is for the two operations that are *meant* to stop on a
    /// conflict. There the non-zero exit is not news: the banner names the files and offers the way
    /// out, and a dialog reading "Git could not do that" on top of it only frightens.
    /// </summary>
    private async Task<bool> RunAsync(string status, Func<Task<GitResult>> work,
        bool quietOnConflict = false)
    {
        if (_busy) return false;

        _busy = true;
        _status.Text = status;
        _status.IsVisible = true;
        ApplyState();

        bool ok = false;
        try
        {
            var result = await work();
            ok = result.Ok;
            if (!ok && !(quietOnConflict && (await GitWriteService.GetConflictsAsync(_repo)).Count > 0))
            {
                var detail = result.Message;
                _host.ShowMessage(Loc.Get("GitFailedTitle"),
                    detail.Length > 0 ? detail : Loc.Get("GitFailedTitle"));
            }
        }
        catch (Exception ex)
        {
            _host.ShowMessage(Loc.Get("GitFailedTitle"), ex.Message);
        }
        finally
        {
            _busy = false;
            _status.Text = "";
            _status.IsVisible = false;
            ApplyState();

            // The window reloads its own git readouts and calls back into RefreshAsync, so the
            // status bar and the panel can never disagree about which branch is checked out.
            GitChanged?.Invoke(this, EventArgs.Empty);
        }

        return ok;
    }

    // ── Remote ─────────────────────────────────────────────────────────

    private void OnFetch() =>
        _ = RunAsync(Loc.Get("FetchingStatus", "Fetching..."),
            () => GitWriteService.FetchAsync(_repo, quiet: false));

    private void OnPull() =>
        _ = RunAsync(Loc.Get("PullingStatus", "Pulling..."),
            () => GitWriteService.PullRebaseAsync(_repo), quietOnConflict: true);

    private async void OnPush()
    {
        if (_repo.Length == 0 || _busy) return;

        var state = _branch;
        if (state.Current.Length == 0) return;

        if (state.HasUpstream && state.Ahead == 0)
        {
            _host.ShowMessage(Loc.Get("PushAction"), Loc.Get("NothingToPush"));
            return;
        }

        // Publishing is outward-facing and awkward to walk back, so it is always confirmed.
        var detail = state.HasUpstream
            ? string.Format(Loc.Get("PushConfirmFmt"),
                string.Format(Loc.Get("BranchAheadFmt"), state.Ahead), state.Current)
            : Loc.Get("PushConfirmNewUpstream");

        if (!await _host.Confirm(Loc.Get("PushConfirmTitle"), detail)) return;

        await RunAsync(Loc.Get("PushingStatus", "Pushing..."),
            () => GitWriteService.PushAsync(_repo, state));
    }

    // ── Branches ───────────────────────────────────────────────────────

    private async void ShowBranchMenu()
    {
        if (_repo.Length == 0 || _busy) return;

        var repo = _repo;
        var (branches, current) = await GitWriteService.GetBranchesAsync(repo);
        if (!string.Equals(repo, _repo, StringComparison.OrdinalIgnoreCase)) return;

        // Rebuilt on every click: branches come and go, unlike the fixed model and effort lists.
        var flyout = new MenuFlyout { Placement = PlacementMode.Bottom };
        foreach (var branch in branches)
        {
            var name = branch;
            var item = new MenuItem
            {
                Header = name == current ? "✓ " + name : "   " + name,
                IsEnabled = name != current,
            };
            item.Click += (_, _) => SwitchBranch(name);
            flyout.Items.Add(item);
        }

        if (branches.Count > 0) flyout.Items.Add(new Separator());

        var create = new MenuItem { Header = Loc.Get("NewBranch") };
        create.Click += (_, _) => OnNewBranch();
        flyout.Items.Add(create);

        var others = branches.Where(b => b != current).ToList();
        if (others.Count > 0)
        {
            var deleteItems = new List<MenuItem>();
            foreach (var branch in others)
            {
                var name = branch;
                var item = new MenuItem { Header = name };
                item.Click += (_, _) => DeleteBranch(name);
                deleteItems.Add(item);
            }
            flyout.Items.Add(new MenuItem
            {
                Header = Loc.Get("DeleteBranch", "Delete branch"),
                ItemsSource = deleteItems,
            });
        }

        flyout.ShowAt(_btnBranch);
    }

    private async void SwitchBranch(string branch)
    {
        if (!await _host.Confirm(
                Loc.Get("SwitchBranchConfirmTitle"),
                string.Format(Loc.Get("SwitchBranchConfirmFmt"), branch)))
            return;

        // git switch refuses on conflicting local changes rather than carrying them across, so
        // a dirty tree surfaces as git's own message instead of silently moving the work.
        await RunAsync(Loc.Get("SwitchingStatus", "Switching..."),
            () => GitWriteService.CheckoutBranchAsync(_repo, branch));
    }

    private async void OnNewBranch()
    {
        if (_repo.Length == 0 || _busy) return;

        var name = await _host.TextInput(Loc.Get("NewBranch"), Loc.Get("NewBranchPrompt"), "");
        if (string.IsNullOrWhiteSpace(name)) return;

        await RunAsync(Loc.Get("SwitchingStatus", "Switching..."),
            () => GitWriteService.CreateBranchAsync(_repo, name.Trim()));
    }

    private async void DeleteBranch(string branch)
    {
        if (!await _host.Confirm(
                Loc.Get("DeleteBranchConfirmTitle", "Delete branch"),
                string.Format(Loc.Get("DeleteBranchConfirmFmt",
                    "Delete the local branch \"{0}\"? Work that has not been merged is kept - git refuses in that case."),
                    branch)))
            return;

        await RunAsync(Loc.Get("DeletingBranchStatus", "Deleting..."),
            () => GitWriteService.DeleteBranchAsync(_repo, branch));
    }

    private async void OnMerge()
    {
        if (_repo.Length == 0 || _busy) return;

        var repo = _repo;
        var (branches, current) = await GitWriteService.GetBranchesAsync(repo);
        if (!string.Equals(repo, _repo, StringComparison.OrdinalIgnoreCase)) return;

        var others = branches.Where(b => b != current).ToList();
        if (others.Count == 0)
        {
            _host.ShowMessage(Loc.Get("MergeAction", "Merge"),
                Loc.Get("NoOtherBranch", "There is no other branch to merge."));
            return;
        }

        var flyout = new MenuFlyout { Placement = PlacementMode.Bottom };
        foreach (var branch in others)
        {
            var name = branch;
            var item = new MenuItem { Header = name };
            item.Click += (_, _) => MergeBranch(name, current);
            flyout.Items.Add(item);
        }
        flyout.ShowAt(_btnMerge);
    }

    private async void MergeBranch(string source, string target)
    {
        if (!await _host.Confirm(
                Loc.Get("MergeConfirmTitle", "Merge"),
                string.Format(Loc.Get("MergeConfirmFmt", "Bring \"{0}\" into \"{1}\"?"), source, target)))
            return;

        await RunAsync(Loc.Get("MergingStatus", "Merging..."),
            () => GitWriteService.MergeAsync(_repo, source), quietOnConflict: true);
    }

    // ── Conflicts ──────────────────────────────────────────────────────

    private async void OnAbortOperation()
    {
        var operation = _operation;
        if (operation == RepoOperation.None) return;

        if (!await _host.Confirm(
                Loc.Get("AbortConfirmTitle", "Abort"),
                Loc.Get("AbortConfirmText",
                    "Put the repository back exactly as it was before this operation started?")))
            return;

        await RunAsync(Loc.Get("AbortingStatus", "Aborting..."),
            () => GitWriteService.AbortAsync(_repo, operation));
    }

    private void OnContinueOperation()
    {
        var operation = _operation;
        if (operation == RepoOperation.None) return;

        _ = RunAsync(Loc.Get("ContinuingStatus", "Continuing..."),
            () => GitWriteService.ContinueAsync(_repo, operation));
    }

    /// <summary>
    /// Hands the conflict to the session: which files, and the one command that finishes the job
    /// once they are resolved. Written in whichever language commit messages are set to, because
    /// that is the language the user picked to be talked to in.
    ///
    /// One line, no breaks: this is typed into the session rather than pasted, so a newline
    /// here would submit half a sentence.
    /// </summary>
    private void OnAskAiAboutConflict()
    {
        if (_conflicts.Count == 0) return;

        string finish = _operation == RepoOperation.Rebase
            ? "git rebase --continue"
            : "git merge --continue";

        var files = string.Join(", ", _conflicts);
        var sb = new StringBuilder();
        bool ja = CommitMessageService.ResolveLanguage(_settings.CommitMessageLanguage) == "ja";

        if (ja)
        {
            sb.Append("git のコンフリクトを解決してください。衝突しているファイル: ").Append(files).Append("。");
            sb.Append("それぞれの衝突箇所を確認し、両方の意図を残す形で解決してください。");
            sb.Append("解決したら該当ファイルをステージして `").Append(finish).Append("` を実行してください。");
        }
        else
        {
            sb.Append("Please resolve these git conflicts: ").Append(files).Append(". ");
            sb.Append("Look at each conflict and keep what both sides intended. ");
            sb.Append("When they are resolved, stage the files and run `").Append(finish).Append("`.");
        }

        _host.SendToTerminal(sb.ToString());
    }

    // ── Commit ─────────────────────────────────────────────────────────

    private void OnStageAllChanged()
    {
        if (_suppressStageAll || _busy || _repo.Length == 0) return;

        bool stage = _chkStageAll.IsChecked == true;
        var paths = _changes
            .Where(c => c.Staged != stage)
            .Select(c => c.Path)
            .ToList();
        if (paths.Count == 0) return;

        _ = RunAsync(Loc.Get(stage ? "StagingStatus" : "UnstagingStatus", "..."),
            () => stage
                ? GitWriteService.StageAsync(_repo, paths)
                : GitWriteService.UnstageAsync(_repo, paths));
    }

    private async void OnCommit() => await CommitAsync();

    private async void OnCommitAndPush()
    {
        if (!await CommitAsync()) return;

        // The branch state the commit produced, not the one the button was drawn from.
        var state = await GitWriteService.GetBranchStateAsync(_repo);
        if (state.Current.Length == 0) return;

        await RunAsync(Loc.Get("PushingStatus", "Pushing..."),
            () => GitWriteService.PushAsync(_repo, state));
    }

    private async Task<bool> CommitAsync()
    {
        if (_repo.Length == 0 || _busy) return false;

        if (!_changes.Any(c => c.Staged))
        {
            _host.ShowMessage(Loc.Get("CommitAction"), Loc.Get("NothingStaged"));
            return false;
        }

        var message = _txtMessage.Text ?? "";
        if (string.IsNullOrWhiteSpace(message))
        {
            _host.ShowMessage(Loc.Get("CommitAction"), Loc.Get("NoCommitMessage"));
            _txtMessage.Focus();
            return false;
        }

        bool ok = await RunAsync(Loc.Get("CommittingStatus", "Committing..."),
            () => GitWriteService.CommitAsync(_repo, message));
        if (ok) _txtMessage.Text = "";
        return ok;
    }

    private void OnToggleLanguage()
    {
        _settings.CommitMessageLanguage =
            CommitMessageService.ResolveLanguage(_settings.CommitMessageLanguage) == "ja" ? "en" : "ja";
        _settings.Save();
        ApplyState();
    }

    private async void OnGenerateMessage()
    {
        if (_repo.Length == 0 || _busy) return;

        var provider = _cli.Active;
        if (string.IsNullOrWhiteSpace(provider.OneShotArgs)) return;

        _generateCts?.Cancel();
        var cts = new CancellationTokenSource();
        _generateCts = cts;

        _busy = true;
        _btnGenerate.Content = Loc.Get("GeneratingMessage", "Drafting...");
        ApplyState();

        string? text = null;
        try
        {
            text = await CommitMessageService.GenerateAsync(_repo, provider,
                _settings.CommitMessageLanguage, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // A second click replaced this draft; the newer one owns the box.
        }
        catch (Exception ex)
        {
            _host.ShowMessage(Loc.Get("GenerateMessage", "AI draft"), ex.Message);
        }
        finally
        {
            _busy = false;
            _btnGenerate.Content = Loc.Get("GenerateMessage", "AI draft");
            ApplyState();
        }

        if (cts.IsCancellationRequested) return;

        if (string.IsNullOrWhiteSpace(text))
        {
            _host.ShowMessage(Loc.Get("GenerateMessage", "AI draft"),
                Loc.Get("GenerateFailed", "The CLI returned no message. Write one by hand, or check the one-shot arguments in providers.json."));
            return;
        }

        _txtMessage.Text = text;
        _txtMessage.Focus();
    }

    // ── Pull requests ──────────────────────────────────────────────────

    private async Task RefreshPullRequestsAsync(int generation)
    {
        _ghReady ??= await GitHubCli.IsReadyAsync();
        if (generation != _refreshGeneration) return;

        _onGitHub = _repo.Length > 0 && await GitHubCli.HasGitHubRemoteAsync(_repo);
        if (generation != _refreshGeneration) return;

        bool show = _ghReady == true && _onGitHub;
        _prExpander.IsVisible = show;
        ApplyState();
        if (!show) return;

        var list = await GitHubCli.ListAsync(_repo);
        if (generation != _refreshGeneration) return;

        _pullRequests = list;
        _prExpander.Header = string.Format(Loc.Get("PullRequestsFmt", "Pull requests ({0})"), list.Count);
        BuildPullRequestList();
    }

    private void BuildPullRequestList()
    {
        _prList.Children.Clear();

        if (_pullRequests.Count == 0)
        {
            _prList.Children.Add(new TextBlock
            {
                Text = Loc.Get("NoPullRequests", "No open pull requests"),
                FontSize = 11,
                Margin = new Thickness(6, 2),
                Foreground = new SolidColorBrush(DimText()),
            });
            return;
        }

        foreach (var pr in _pullRequests)
            _prList.Children.Add(BuildPullRequestRow(pr));
    }

    private Control BuildPullRequestRow(PullRequestInfo pr)
    {
        var title = new TextBlock
        {
            Text = "#" + pr.Number + "  " + pr.Title,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var approved = string.Equals(pr.ReviewDecision, "APPROVED", StringComparison.OrdinalIgnoreCase);
        var meta = new TextBlock
        {
            Text = pr.Author + "   " + pr.HeadBranch + " → " + pr.BaseBranch
                + (pr.IsDraft ? "   " + Loc.Get("PrDraft", "draft") : "")
                + (approved ? "   ✓ " + Loc.Get("PrApproved", "approved") : ""),
            FontSize = 10,
            Opacity = 0.6,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var approve = ToolButton(Loc.Get("ApproveAction", "Approve"), "", () => ApprovePullRequest(pr));
        approve.IsEnabled = !approved;
        var open = ToolButton(Loc.Get("OpenInBrowser", "Open"), "", () => OpenUrl(pr.Url));

        var stack = new StackPanel { Spacing = 3 };
        stack.Children.Add(title);
        stack.Children.Add(meta);
        stack.Children.Add(Row(approve, open));

        return new Border
        {
            Child = stack,
            Padding = new Thickness(6, 5),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 0, 0, 2),
            BorderBrush = new SolidColorBrush(Divider()),
            BorderThickness = new Thickness(0.5),
        };
    }

    private async void ApprovePullRequest(PullRequestInfo pr)
    {
        if (_busy || _repo.Length == 0) return;

        // GitHub does not let an author approve their own pull request. That refusal comes back
        // as gh's message and is shown as-is: it is the answer, not a failure to hide.
        await RunAsync(Loc.Get("ApprovingStatus", "Approving..."),
            () => GitHubCli.ApproveAsync(_repo, pr.Number));
    }

    private async void OnCreatePullRequest()
    {
        if (_repo.Length == 0 || _busy || _ghReady != true || !_onGitHub) return;

        var repo = _repo;
        var subjectTask = GitWriteService.GetLastSubjectAsync(repo);
        var baseTask = GitHubCli.GetDefaultBranchAsync(repo);
        await Task.WhenAll(subjectTask, baseTask);
        if (!string.Equals(repo, _repo, StringComparison.OrdinalIgnoreCase)) return;

        var draft = await ShowPullRequestDialogAsync(subjectTask.Result, baseTask.Result);
        if (draft == null) return;

        string createdUrl = "";
        await RunAsync(Loc.Get("CreatingPrStatus", "Creating the pull request..."), async () =>
        {
            var result = await GitHubCli.CreateAsync(repo, draft.Value.Title, draft.Value.Body,
                draft.Value.Base);
            if (result.Ok) createdUrl = GitHubCli.ExtractUrl(result.StdOut + "\n" + result.StdErr);
            return result;
        });

        // gh has already published it by the time the URL comes back, so opening it is the last
        // step rather than a second confirmation.
        if (createdUrl.Length > 0) OpenUrl(createdUrl);
    }

    /// <summary>The three things gh needs, or null when the user backed out.</summary>
    private Task<(string Title, string Body, string Base)?> ShowPullRequestDialogAsync(
        string defaultTitle, string defaultBase)
    {
        var source = new TaskCompletionSource<(string, string, string)?>();
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner == null)
        {
            source.SetResult(null);
            return source.Task;
        }

        var titleBox = new TextBox
        {
            Text = defaultTitle,
            FontSize = 13,
            Padding = new Thickness(8, 6),
            PlaceholderText = Loc.Get("PrTitleLabel", "Title"),
        };
        var bodyBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 140,
            FontSize = 12.5,
            Padding = new Thickness(8, 6),
            PlaceholderText = Loc.Get("PrBodyLabel", "What does this change, and why?"),
        };
        var baseBox = new TextBox
        {
            Text = defaultBase,
            FontSize = 13,
            Padding = new Thickness(8, 6),
            PlaceholderText = Loc.Get("PrBaseLabel", "Merge into"),
        };

        var ok = new Button
        {
            Content = Loc.Get("CreatePrAction", "Create"),
            MinWidth = 88,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        var cancel = new Button
        {
            Content = Loc.Get("Cancel"),
            MinWidth = 88,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(22, 20) };
        panel.Children.Add(FieldLabel(Loc.Get("PrTitleLabel", "Title")));
        panel.Children.Add(titleBox);
        panel.Children.Add(FieldLabel(Loc.Get("PrBodyLabel", "Description")));
        panel.Children.Add(bodyBox);
        panel.Children.Add(FieldLabel(Loc.Get("PrBaseLabel", "Merge into")));
        panel.Children.Add(baseBox);
        panel.Children.Add(buttons);

        var dialog = new Window
        {
            Title = Loc.Get("CreatePrAction", "Pull request"),
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(_isDark
                ? Color.FromRgb(30, 30, 32)
                : Color.FromRgb(246, 246, 250)),
            Content = panel,
        };

        bool answered = false;
        ok.Click += (_, _) =>
        {
            var title = (titleBox.Text ?? "").Trim();
            if (title.Length == 0) { titleBox.Focus(); return; }

            answered = true;
            source.TrySetResult((title, bodyBox.Text ?? "", (baseBox.Text ?? "").Trim()));
            dialog.Close();
        };
        cancel.Click += (_, _) => { answered = true; source.TrySetResult(null); dialog.Close(); };
        dialog.Closed += (_, _) => { if (!answered) source.TrySetResult(null); };

        _ = dialog.ShowDialog(owner);
        Dispatcher.UIThread.Post(() => { titleBox.Focus(); titleBox.SelectAll(); });
        return source.Task;
    }

    private TextBlock FieldLabel(string text) => new()
    {
        Text = text,
        FontSize = 11,
        FontWeight = FontWeight.SemiBold,
        Foreground = new SolidColorBrush(DimText()),
    };

    // ── Windows this panel opens ───────────────────────────────────────

    private void OpenGraphWindow()
    {
        if (_repo.Length == 0) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        new CommitGraphWindow(_repo, System.IO.Path.GetFileName(_repo.TrimEnd('\\', '/')),
            _isDark, _mono, _host.SendToTerminal).Show(owner);
    }

    private async void ShowDiff(GitChange change)
    {
        string diff;
        try
        {
            diff = await GitChangeService.GetDiffAsync(_repo, change);
        }
        catch
        {
            diff = "";
        }

        if (string.IsNullOrWhiteSpace(diff))
        {
            _host.ShowMessage(Loc.Get("Diff"), Loc.Get("DiffEmpty"));
            return;
        }

        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        new DiffWindow(change.Path, diff, _isDark, _mono, change.Path, _host.SendToTerminal)
            .Show(owner);
    }

    /// <summary>
    /// Asks for a comment about a whole file and hands it to the session as "@path &lt;comment&gt;".
    /// The diff window does the same for a range of lines; this is the version for a file the
    /// user has already made up their mind about.
    /// </summary>
    private async void CommentOnFile(GitChange change)
    {
        var comment = await _host.TextInput(
            change.Path,
            Loc.Get("CommentOnFileHint", "What should change in this file?"),
            "");
        if (comment == null) return;

        // Only real line breaks: a comment may legitimately mention "\n" and mean the text.
        comment = System.Text.RegularExpressions.Regex.Replace(comment, @"\s*\r?\n\s*", " ").Trim();
        _host.SendToTerminal(comment.Length > 0
            ? "@" + change.Path + " " + comment
            : "@" + change.Path + " ");
    }

    private static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // No browser association, or the user cancelled the shell prompt.
        }
    }

    // ── Small builders ─────────────────────────────────────────────────

    private Button ToolButton(string text, string tooltip, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            FontSize = 11.5,
            Padding = new Thickness(8, 3),
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        if (tooltip.Length > 0) ToolTip.SetTip(button, tooltip);
        button.Click += (_, _) => onClick();
        return button;
    }

    private static StackPanel Row(params Control[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var child in children) row.Children.Add(child);
        return row;
    }

    // ── Theme ──────────────────────────────────────────────────────────

    private Color Divider() => _isDark ? Color.FromRgb(56, 56, 58) : Color.FromRgb(210, 210, 215);

    private Color DimText() => _isDark ? Color.FromRgb(140, 140, 148) : Color.FromRgb(110, 110, 118);

    private Color WarningBg() =>
        _isDark ? Color.FromRgb(58, 44, 24) : Color.FromRgb(255, 248, 225);

    private Color WarningBorder() =>
        _isDark ? Color.FromRgb(120, 90, 40) : Color.FromRgb(232, 200, 120);

    private Color WarningText() =>
        _isDark ? Color.FromRgb(240, 200, 120) : Color.FromRgb(140, 90, 10);
}
