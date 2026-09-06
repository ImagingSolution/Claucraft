using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Claucraft.Services;
using Claucraft.Terminal;

namespace Claucraft;

public partial class MainWindow : Window
{
    private enum MdiLayout { Maximize, Tile, TileHorizontal, TileVertical, Cascade }
    private enum SidebarPanel { None, Explorer, Snippets, Settings, Windows, SourceControl, Slash, Extensions }

    private string? _projectFolderBacking;

    /// <summary>
    /// The project the toolbar, explorer, session list and git readout are all showing.
    ///
    /// Five different paths move it - startup, the folder picker, activating another child,
    /// duplicating a tab, restoring a workspace - and the file watcher is bound to whichever
    /// folder it was given, so rebinding lives here rather than at each assignment. Missing one
    /// of them leaves the branch readout watching a project the user has already left.
    /// </summary>
    private string? _projectFolder
    {
        get => _projectFolderBacking;
        set
        {
            if (string.Equals(_projectFolderBacking, value, StringComparison.OrdinalIgnoreCase)) return;
            _projectFolderBacking = value;
            StartFileWatcher();
            // The extensions panel reads the project's .mcp.json and .claude/skills, so its
            // cache belongs to the folder we just left.
            _extensions = null;
            if (_activeSidePanel == SidebarPanel.Extensions) RefreshExtensionsPanel();
        }
    }
    private string? _gitRepoUrl;
    private FileSystemWatcher? _fileWatcher;
    private DispatcherTimer? _fileWatcherDebounce;
    private DispatcherTimer? _gitInfoDebounce;
    private readonly UsageTracker _usageTracker = new();

    /// <summary>
    /// Stands in for the cost readout when no window is active. Never tracked, so it reports an
    /// empty session and every readout built from it hides itself.
    /// </summary>
    private readonly SessionCostMonitor _noCost = new();

    /// <summary>
    /// Marginal cost of the session in the active window. Each window owns its own monitor -
    /// see <see cref="MdiChildInfo.Cost"/> - so switching windows swaps the readout rather than
    /// re-deriving it, and a window can never report a neighbour's numbers.
    /// </summary>
    private SessionCostMonitor ActiveCost =>
        _activeChildIndex >= 0 && _activeChildIndex < _children.Count
            ? _children[_activeChildIndex].Cost
            : _noCost;

    /// <summary>The account's real 5-hour and 7-day limits. See RateLimitService.</summary>
    private readonly RateLimitService _rateLimits = new();

    /// <summary>
    /// The model the user just picked, held until the transcript confirms it. The name on the
    /// bar is read from the transcript, which only learns about a switch when the next reply
    /// lands - without this the bar would keep naming the old model until then. Set both when
    /// Claucraft's own dropdown sends the switch and when the CLI's own "Set model to X" banner
    /// is spotted on screen, so a model changed by typing "/model" directly, or through the
    /// CLI's own picker, is not missed either.
    /// </summary>
    private string? _pendingModelLabel;
    private List<LaunchProfile> _profiles = new();
    private bool _suppressProfileChange;
    private bool _costRefreshInFlight;

    /// <summary>Turn-end tracking for the frame blink. See NoteRunState.</summary>
    private bool _sawWorking;
    private int _idlePolls;

    /// <summary>Banner action that opens the hand-off flow rather than typing a slash command.</summary>
    private const string HandoffActionCommand = "claucraft:handoff";

    /// <summary>
    /// The CLI's own context-remaining percentage below which the low-context banner shows
    /// when no transcript has been attached yet. Once the transcript is read, the token-based
    /// <see cref="AppSettings.HandoffBannerTokens"/> takes over.
    /// </summary>
    private const int CliContextLowPercent = 20;
    private bool _isDark = true;
    private MdiLayout _layout = MdiLayout.Maximize;
    private int _activeChildIndex = -1;

    // Which window ArrangeChildren treats as "on top" for Maximize/Cascade. Separate from
    // _activeChildIndex, which drives terminal-only concerns (command routing, project context
    // switching) and only ever indexes _children - an editor window can be the active layout
    // item without being a valid terminal index.
    private IMdiLayoutItem? _activeLayoutItem;
    private readonly List<MdiChildInfo> _children = new();
    private readonly AppSettings _settings;
    private readonly CliProviderService _cli;
    // DocView is now embedded in TerminalControl, not in MainWindow

    private bool _suppressFolderSelectionChanged;
    private bool _suppressProviderChanged;
    private readonly List<ProviderRadioRow> _providerRadioRows = new();

    private record ProviderRadioRow(RadioButton Radio, TextBlock Label, TextBlock Hint, CliProvider Provider);

    // Sidebar state
    private SidebarPanel _activeSidePanel = SidebarPanel.None;
    private readonly List<(SlashCommand Cmd, bool FromProject)> _slashEntries = new();
    private double _sidePanelWidth = 250;
    private bool _settingsInitialized;
    private bool _snippetsInitialized;
    // Set while the settings panel is being populated, so seeding a checkbox does not
    // write a half-filled panel back over the saved settings.
    private bool _suppressSettingsChanged;
    private readonly SnippetStore _snippetStore;
    private readonly NotificationService _notifications = new();
    private readonly CheckpointService _checkpoints = new();

    // Live status read off the terminal screen (mode / activity / context / errors)
    private DispatcherTimer? _insightTimer;
    private TerminalSnapshot _insight = new();
    private string? _bannerKey;
    private string? _bannerActionCommand;
    private readonly HashSet<string> _dismissedBanners = new();
    /// <summary>
    /// The source-control panel, built in code and hosted by SourceControlHost. It owns every
    /// git readout the sidebar shows; the window only tells it which repository to look at and
    /// when to reload.
    /// </summary>
    private Controls.SourceControlPanel? _sourceControl;

    /// <summary>Plan ids in the order the settings combo lists them.</summary>
    private static readonly string[] PlanTierIds = { "Pro", "Max5x", "Max20x" };

    // Snippet drag state
    private bool _snippetDragging;
    private Border? _snippetDragItem;
    private int _snippetDragIndex;
    private Point _snippetDragStartPos;

    // Drag state
    private bool _isDragging;
    private Point _dragStart;
    private double _dragChildLeft;
    private double _dragChildTop;
    private MdiChildInfo? _dragChild;

    // Font list for settings panel
    private static readonly List<string> FontList = new()
    {
        "Cascadia Mono", "Cascadia Code", "Consolas", "Courier New",
        "Source Code Pro", "JetBrains Mono", "Fira Code", "Hack",
        "DejaVu Sans Mono", "Lucida Console",
        "Segoe UI", "Arial", "Verdana", "Tahoma", "Calibri",
        "MS Gothic", "BIZ UDGothic", "Yu Gothic", "Yu Gothic UI",
        "Meiryo", "Meiryo UI", "BIZ UDMincho", "MS Mincho",
    };

    private static readonly List<string> LanguageList = new() { "English", "日本語" };

    /// <summary>
    /// Common surface for anything ArrangeChildren/BringToFront place on the MDI canvas -
    /// terminal windows and floating editor windows alike - so layout code can treat both
    /// uniformly without knowing about TerminalControl or TextFileDocument.
    /// </summary>
    private interface IMdiLayoutItem
    {
        Border Container { get; }
        Border TitleBar { get; }
    }

    private record MdiChildInfo(
        Border Container,
        Border TitleBar,
        TextBlock TitleText,
        Ellipse StatusDot,
        Ellipse StripDot,
        TerminalControl Terminal,
        Button StripButton,
        TextBlock StripText
    ) : IMdiLayoutItem
    {
        public string? ProjectFolder { get; set; }
        public string? FirstInput { get; set; }

        /// <summary>Session this window resumed, so the Session box can show it as selected.</summary>
        public string? SessionId { get; set; }

        /// <summary>
        /// Cost and context readout for this window's transcript. One per window: a single
        /// shared monitor had to be re-pointed on every switch, which threw away the parse of
        /// a multi-megabyte transcript and left the status bar reporting the window the user
        /// had just left until the re-read finished.
        /// </summary>
        public SessionCostMonitor Cost { get; } = new();

        /// <summary>
        /// Reasoning effort this window is running at. Effort never reaches the transcript, so
        /// this is the only record of it there is: seeded from the launch arguments, then moved
        /// by the status bar dropdown. A level typed straight into the terminal goes unseen.
        /// </summary>
        public string? Effort { get; set; }

        /// <summary>
        /// Display name of the model this window was launched on, read off its launch line or
        /// the user's settings. It is what the status bar shows until the transcript names a
        /// model itself, which cannot happen before the session's first reply.
        /// </summary>
        public string? StartingModel { get; set; }

        /// <summary>
        /// Set once CloseChild starts tearing this window down. The strip button lives on while
        /// the CLI takes its time quitting, so this is what keeps a second × click from running
        /// the teardown a second time.
        /// </summary>
        public bool IsClosing { get; set; }

        /// <summary>
        /// Turn-end detection, per window: true once this window has been seen mid-turn, so the
        /// working → idle edge can be told apart from a window that was idle all along.
        /// </summary>
        public bool SawWorking { get; set; }

        /// <summary>Consecutive idle polls since <see cref="SawWorking"/>, debouncing that edge.</summary>
        public int IdlePolls { get; set; }

        /// <summary>
        /// The name the transcript gives this window's session, once one has been read. It
        /// outranks anything the screen says: the opening prompt and the terminal's own OSC
        /// title are both guesses at a name the session now states outright.
        /// </summary>
        public string? SessionTitle { get; set; }

        /// <summary>
        /// The isolated checkout this window works in, or null when it shares the project
        /// folder with every other window. Owned by the window: closing it takes the checkout
        /// with it.
        /// </summary>
        public string? WorktreePath { get; set; }

        /// <summary>Branch the worktree is on, so it can be tidied up with the checkout.</summary>
        public string? WorktreeBranch { get; set; }

        /// <summary>The repository the worktree was cut from - not where the session works.</summary>
        public string? WorktreeOrigin { get; set; }
    };

    public MainWindow()
    {
        InitializeComponent();

        _settings = AppSettings.Load();
        _snippetStore = SnippetStore.Load();
        _snippetStore.SeedDefaultsIfEmpty(_settings.Language);
        _isDark = _settings.IsDark;

        _checkpoints.Load();
        _notifications.EnableToast = _settings.NotifyOnComplete;
        _notifications.EnableSound = _settings.NotifySound;

        _cli = new CliProviderService { ActiveId = _settings.CliProviderId };
        // A retired CLI (e.g. gemini -> antigravity) is remapped on assignment; persist it
        // so the old id does not sit in appsettings.json forever.
        if (_settings.CliProviderId != _cli.ActiveId)
        {
            _settings.CliProviderId = _cli.ActiveId;
            _settings.Save();
        }
        BuildProviderRadios();
        InitializeProviderFieldHandlers();

        UsageTracker.DailyLimit = PlanDailyLimit(_settings.PlanTier);
        _rateLimits.Updated += OnRateLimitsUpdated;
        BuildModelFlyout();
        BuildEffortFlyout();

        // Mirror what the CLI is doing into the status bar. Cheap: reads the screen buffer
        // that is already in memory, no extra process or file access.
        _insightTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _insightTimer.Tick += (_, _) =>
        {
            RefreshLiveStatus();
            RefreshGenerationBars();
            RefreshSubagents();
        };
        _insightTimer.Start();


        _projectFolder = !string.IsNullOrEmpty(_settings.ProjectFolder) && Directory.Exists(_settings.ProjectFolder)
            ? _settings.ProjectFolder
            : Environment.CurrentDirectory;
        LoadRecentProjectFolders();

        MdiContainer.SizeChanged += (_, _) =>
        {
            Dispatcher.UIThread.Post(ArrangeChildren, DispatcherPriority.Render);
        };

        // Global keyboard shortcuts
        KeyDown += OnGlobalKeyDown;
        // F1 and Ctrl+/ never survive the bubble route: with a terminal focused, its input
        // TextBox marks them handled before the window ever sees them. Catch those two on the
        // way down instead. Everything else stays on the bubble route, so the terminal keeps
        // first claim on the keys it needs.
        AddHandler(KeyDownEvent, OnAppShortcutTunnel, RoutingStrategies.Tunnel);

        // Apply saved language and theme
        Loc.Language = _settings.Language;
        ApplyLocalization();

        if (!_settings.IsDark)
        {
            _isDark = false;
            if (Application.Current is App app)
                app.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
            UpdateThemeResources();
        }

        // Built after the theme is settled: the panel bakes its colours in at construction,
        // so OnDarkModeChanged rebuilds it rather than restyling it.
        CreateSourceControlPanel();

        RefreshGitInfo();
        RefreshSessionList();
        RefreshFileTree();
        HookFileTreeDrag();

        // Probe `--version` off the UI thread; labels fill in as results arrive
        _ = DetectProviderVersionsAsync();

        // Show welcome page or auto-launch
        if (_settings.ShowWelcomePage)
        {
            Dispatcher.UIThread.Post(ShowWelcomePage, DispatcherPriority.Background);
        }
        else if (!string.IsNullOrEmpty(_projectFolder) && Directory.Exists(_projectFolder))
        {
            Dispatcher.UIThread.Post(LaunchClaudeWithInitialPrompt, DispatcherPriority.Background);
        }

        // First run: surface a missing CLI or sign-in before the user hits a wall in the
        // terminal. Stays silent when everything checks out.
        if (!_settings.SetupDoctorShown)
        {
            _settings.SetupDoctorShown = true;
            _settings.Save();
            _ = ShowSetupDoctorIfProblemsAsync();
        }
    }

    // ── Localization ──

    private void ApplyLocalization()
    {
        // Toolbar
        LblProject.Text = Loc.Get("Project");
        CmbProjectFolder.PlaceholderText = Loc.Get("SelectProjectFolder");
        ToolTip.SetTip(BtnNewProject, Loc.Get("NewProject"));
        LblSession.Text = Loc.Get("Session");
        CmbSessions.PlaceholderText = Loc.Get("SelectSession");
        // LblNewClaude / LblResume depend on the active provider — set by ApplyProviderUi()

        // Status Bar - git info updated via RefreshGitInfo()

        // Explorer panel header
        ToolTip.SetTip(BtnBrowseFolder, Loc.Get("SelectProjectFolder"));

        // Activity Bar tooltips
        ToolTip.SetTip(BtnActivityExplorer, Loc.Get("ExplorerTooltip"));
        ToolTip.SetTip(BtnActivitySnippets, Loc.Get("SnippetsTooltip"));
        ToolTip.SetTip(BtnActivityWindows, Loc.Get("WindowsTooltip"));
        ToolTip.SetTip(BtnActivityDiagram, Loc.Get("DiagramTooltip"));
        ToolTip.SetTip(BtnActivityDocView, Loc.Get("DocViewTooltip"));
        ToolTip.SetTip(BtnActivityCompact, Loc.Get("CompactTooltip"));
        ToolTip.SetTip(BtnActivitySettings, Loc.Get("SettingsTooltip"));

        // Side Panel title (if open)
        if (_activeSidePanel != SidebarPanel.None)
            ShowPanelContent(_activeSidePanel);

        // Explorer context menu
        MenuTreeOpen.Header = Loc.Get("Open");
        MenuTreeOpenWith.Header = Loc.Get("OpenWith");
        MenuTreeOpenInEditor.Header = Loc.Get("OpenInEditor");
        MenuTreeShowInExplorer.Header = Loc.Get("ShowInExplorer");
        MenuTreeCopyPath.Header = Loc.Get("CopyPath");
        MenuTreeCopyFilename.Header = Loc.Get("CopyFilename");

        // Settings panel labels
        LblConsoleSettings.Text = Loc.Get("ConsoleSettings");
        LblLanguage.Text = Loc.Get("LanguageSetting");
        LblFontFamily.Text = Loc.Get("FontFamily");
        LblFontSize.Text = Loc.Get("FontSize");
        LblInitialPrompt.Text = Loc.Get("InitialPrompt");
        LblApplySettings.Text = Loc.Get("Apply");
        ChkShowWelcomePage.Content = Loc.Get("ShowWelcomePage");
        ChkEnableCharts.Content = Loc.Get("EnableCharts");

        // AI provider panel (LblOpenClaudeFolder is provider-dependent — ApplyProviderUi)
        LblAiProvider.Text = Loc.Get("AiProvider");
        LblProviderExe.Text = Loc.Get("ExecutableFile");
        LblProviderNewArgs.Text = Loc.Get("NewArgs");
        LblProviderContinueArgs.Text = Loc.Get("ContinueArgs");
        LblProviderResumeArgs.Text = Loc.Get("ResumeArgs");
        LblRestoreDefaults.Text = Loc.Get("RestoreDefaults");
        LblOpenConfigFolder.Text = Loc.Get("OpenConfigFolder");
        ToolTip.SetTip(BtnAiSelector, Loc.Get("SwitchAiTooltip"));

        // Snippets panel
        LblAddSnippet.Text = Loc.Get("AddSnippet");

        // Window strip tooltips
        ToolTip.SetTip(BtnLayoutTile, Loc.Get("TileWindows"));
        ToolTip.SetTip(BtnLayoutTileH, Loc.Get("TileHorizontally"));
        ToolTip.SetTip(BtnLayoutTileV, Loc.Get("TileVertically"));
        ToolTip.SetTip(BtnLayoutCascade, Loc.Get("CascadeWindows"));
        ToolTip.SetTip(BtnLayoutMaximize, Loc.Get("FullView"));

        // Slash command panel
        ToolTip.SetTip(BtnActivitySlash, Loc.Get("SlashCommandsTooltip"));
        ToolTip.SetTip(BtnActivityExtensions, Loc.Get("ExtensionsTooltip"));
        ToolTip.SetTip(BtnRefreshExtensions, Loc.Get("Refresh"));
        TxtExtensionSearch.PlaceholderText = Loc.Get("ExtensionsSearch");
        ToolTip.SetTip(BtnManageSessions, Loc.Get("ManageSessionsTooltip"));
        // The cached rows carry summaries built in the language we just left.
        _extensions = null;
        if (_activeSidePanel == SidebarPanel.Extensions) RefreshExtensionsPanel();
        TxtSlashSearch.PlaceholderText = Loc.Get("SearchSlashCommands");
        if (SlashPanel.IsVisible) RefreshSlashPanel();

        // Setup check, shortcuts, notifications, checkpoints
        LblSetupDoctor.Text = Loc.Get("SetupDoctor");
        ToolTip.SetTip(BtnSetupDoctor, Loc.Get("SetupDoctorTooltip"));
        LblShortcuts.Text = Loc.Get("Shortcuts");
        ToolTip.SetTip(BtnShortcuts, Loc.Get("ShortcutsTooltip"));
        LblNotifications.Text = Loc.Get("Notifications");
        ChkNotifyOnComplete.Content = Loc.Get("NotifyOnComplete");
        ChkNotifySound.Content = Loc.Get("NotifySound");
        LblCheckpoints.Text = Loc.Get("Checkpoints");
        ChkEnableCheckpoints.Content = Loc.Get("EnableCheckpoints");

        // Source control, tokens & cost, and the live status readouts
        ToolTip.SetTip(BtnActivitySourceControl, Loc.Get("SourceControlTooltip"));
        ToolTip.SetTip(StatusBranchName, Loc.Get("SourceControlTooltip"));
        ToolTip.SetTip(BtnBranchSwitch, Loc.Get("BranchSwitchTooltip"));
        LblIsolate.Text = Loc.Get("IsolateSession");
        ToolTip.SetTip(ChkIsolate, Loc.Get("IsolateTooltip"));
        LblSourceControlSettings.Text = Loc.Get("SOURCE_CONTROL");
        LblCommitLanguage.Text = Loc.Get("CommitLanguage");
        ChkGitAutoFetch.Content = Loc.Get("GitAutoFetch");
        ToolTip.SetTip(BtnActivityCost, Loc.Get("CostTooltip"));
        ToolTip.SetTip(StatusModeBadge, Loc.Get("ModeBadgeTooltip"));
        ToolTip.SetTip(StatusContextPanel, Loc.Get("ContextMeterTooltip"));
        ToolTip.SetTip(BtnBannerDismiss, Loc.Get("Dismiss"));
        LblLiveStatus.Text = Loc.Get("LiveStatus");
        ChkEnableLiveStatus.Content = Loc.Get("EnableLiveStatus");
        ChkEnableErrorBanner.Content = Loc.Get("EnableErrorBanner");
        LblPlanTier.Text = Loc.Get("PlanTier");
        LblOpenCostDashboard.Text = Loc.Get("CostDashboard");
        LblOpenUsageChart.Text = Loc.Get("PaletteUsageChart");
        LblOpenCheckpoints.Text = Loc.Get("Checkpoints");
        ToolTip.SetTip(BtnWorkspaces, Loc.Get("Workspaces"));
        if (_settingsInitialized) FillPlanTierCombo();

        // Window title, the labels that embed the AI name, plus feature gating
        ApplyProviderUi();
    }

    // ── AI Provider ──

    private void BuildProviderRadios()
    {
        PnlProviderRadios.Children.Clear();
        _providerRadioRows.Clear();

        foreach (var provider in _cli.Providers)
        {
            var label = new TextBlock
            {
                Text = provider.Name,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var hint = new TextBlock
            {
                FontSize = 11,
                Opacity = 0.7,
                VerticalAlignment = VerticalAlignment.Center,
                IsVisible = false,
            };

            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            content.Children.Add(label);
            content.Children.Add(hint);

            var radio = new RadioButton
            {
                GroupName = "CliProvider",
                Content = content,
                Tag = provider.Id,
                Margin = new Thickness(0, 1),
            };
            radio[!RadioButton.ForegroundProperty] = new DynamicResourceExtension("SubtleText");
            radio.IsCheckedChanged += OnProviderRadioChanged;

            PnlProviderRadios.Children.Add(radio);
            _providerRadioRows.Add(new ProviderRadioRow(radio, label, hint, provider));
        }
    }

    private void OnProviderRadioChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressProviderChanged) return;
        if (sender is not RadioButton radio || radio.IsChecked != true) return;

        var id = radio.Tag as string ?? "";
        if (TrySwitchProvider(id)) return;

        // Rejected — put the selection back. Avalonia unchecks the sibling radios after this
        // event returns, so the restore has to run once that bookkeeping is done.
        Dispatcher.UIThread.Post(RefreshProviderRadios, DispatcherPriority.Background);
    }

    /// <summary>Refreshes radio labels, install state and selection without rebuilding controls.</summary>
    private void RefreshProviderRadios()
    {
        _suppressProviderChanged = true;
        foreach (var row in _providerRadioRows)
        {
            row.Label.Text = row.Provider.DisplayName;
            row.Radio.IsEnabled = row.Provider.IsInstalled;
            row.Radio.Opacity = row.Provider.IsInstalled ? 1.0 : 0.45;
            row.Radio.IsChecked = row.Provider.Id == _cli.ActiveId;

            row.Hint.IsVisible = !row.Provider.IsInstalled;
            row.Hint.Text = $"— {Loc.Get("NotInstalled")}";

            ToolTip.SetTip(row.Radio, row.Provider.IsInstalled
                ? row.Provider.ResolvedPath
                : row.Provider.InstallHint);
        }
        _suppressProviderChanged = false;
    }

    private void InitializeProviderFieldHandlers()
    {
        TxtProviderExe.LostFocus += (_, _) => SaveProviderField(p => p.Exe = TxtProviderExe.Text?.Trim() ?? "");
        TxtProviderNewArgs.LostFocus += (_, _) => SaveProviderField(p => p.NewArgs = TxtProviderNewArgs.Text?.Trim() ?? "");
        TxtProviderContinueArgs.LostFocus += (_, _) => SaveProviderField(p => p.ContinueArgs = TxtProviderContinueArgs.Text?.Trim() ?? "");
        TxtProviderResumeArgs.LostFocus += (_, _) => SaveProviderField(p => p.ResumeArgs = TxtProviderResumeArgs.Text?.Trim() ?? "");
    }

    private void SaveProviderField(Action<CliProvider> apply)
    {
        var provider = _cli.Active;
        apply(provider);
        _cli.Save();
        // The executable may now point somewhere else — re-detect and refresh the UI.
        _cli.ResolveExecutables();
        RefreshProviderRadios();
        UpdateAiSelector();
        _ = DetectProviderVersionsAsync();
    }

    private void LoadProviderFieldsIntoUi()
    {
        var p = _cli.Active;
        TxtProviderExe.Text = p.Exe;
        TxtProviderNewArgs.Text = p.NewArgs;
        TxtProviderContinueArgs.Text = p.ContinueArgs;
        TxtProviderResumeArgs.Text = p.ResumeArgs;
    }

    private void OnRestoreProviderDefaults(object? sender, RoutedEventArgs e)
    {
        if (!_cli.RestoreDefaults(_cli.ActiveId)) return;
        ApplyProviderUi();
        _ = DetectProviderVersionsAsync();
    }

    private void OnOpenProviderConfigFolder(object? sender, RoutedEventArgs e)
    {
        var dir = CliProviderService.ConfigFolderPath;
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch { }
    }

    /// <summary>
    /// Applies the active provider everywhere: labels, feature gating and live terminals.
    /// Safe to call repeatedly; it never rebuilds controls.
    /// </summary>
    private void ApplyProviderUi()
    {
        var provider = _cli.Active;
        var features = provider.Features;

        // Toolbar
        LblNewClaude.Text = Loc.Get("NewSession");
        ToolTip.SetTip(BtnNewClaude, string.Format(Loc.Get("NewSessionTooltipFmt"), provider.Name));

        // Session row — only Claude-style CLIs expose a session index Claucraft can read.
        LblSession.IsVisible = features.SessionList;
        SessionRow.IsVisible = features.SessionList;
        if (features.SessionList)
        {
            LblResume.Text = Loc.Get("Resume");
            ToolTip.SetTip(BtnResumeSession, Loc.Get("Resume"));
            BtnResumeSession.IsEnabled = CmbSessions.SelectedItem is SessionInfo;
        }
        else
        {
            LblResume.Text = Loc.Get("ContinueSession");
            ToolTip.SetTip(BtnResumeSession, Loc.Get("ContinueSessionTooltip"));
            BtnResumeSession.IsEnabled = !string.IsNullOrWhiteSpace(provider.ContinueArgs);
        }

        // Activity bar — hide what this CLI does not implement
        BtnActivityDocView.IsVisible = features.ChatView;
        // BtnActivityDiagram stays hidden: diagram detection scrapes Claude Code's
        // terminal output, which breaks whenever Claude changes its renderer. The
        // handler and inline rendering are still live — restore this line and drop
        // IsVisible="False" in the axaml to bring the button back.
        BtnActivityCompact.IsVisible = features.CompactButton;

        // Launch profiles are per-CLI; a CLI that defines none keeps the picker hidden.
        _profiles = _cli.ActiveProfiles.ToList();
        _suppressProfileChange = true;
        CmbLaunchProfile.ItemsSource = _profiles.Select(p => p.Name).ToList();
        CmbLaunchProfile.IsVisible = _profiles.Count > 0;
        if (_profiles.Count > 0)
        {
            var activeProfile = _cli.FindProfile(_settings.ActiveProfileId);
            int profileIndex = activeProfile == null ? 0 : _profiles.FindIndex(p => p.Id == activeProfile.Id);
            CmbLaunchProfile.SelectedIndex = profileIndex < 0 ? 0 : profileIndex;
            UpdateProfileTooltip();
        }
        _suppressProfileChange = false;

        // Both readouts are Claude-account specific: the tracker reads Claude Code's own
        // transcripts, the rate limits belong to the signed-in Claude plan.
        if (features.UsageTracker)
        {
            _usageTracker.Start();
            _rateLimits.Start();
        }
        else
        {
            _usageTracker.Stop();
            _rateLimits.Stop();
        }

        // Settings panel
        var configDir = string.IsNullOrWhiteSpace(provider.ConfigDir) ? "" : provider.ConfigDir;
        BtnOpenClaudeFolder.IsVisible = configDir.Length > 0;
        LblOpenClaudeFolder.Text = string.Format(Loc.Get("OpenConfigDirFmt"), configDir);
        LoadProviderFieldsIntoUi();
        RefreshProviderRadios();

        UpdateAiSelector();
        UpdateWindowTitle();

        foreach (var child in _children)
            ApplyProviderToTerminal(child.Terminal);
    }

    private void ApplyProviderToTerminal(TerminalControl terminal)
    {
        var features = _cli.Features;
        terminal.EnablePermissionOverlay = features.PermissionOverlay;
        terminal.ExitCommand = features.ExitCommand;
        terminal.EnableChartRendering = _settings.EnableChartRendering && features.DiagramViewer;
    }

    /// <summary>
    /// Window title, e.g. "Claucraft Ver.0.1.12.244". Called from
    /// ApplyProviderUi() so it follows both a language change and an AI switch.
    /// </summary>
    private void UpdateWindowTitle()
    {
        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        var verStr = ver != null ? $"Ver.{ver.Major}.{ver.Minor}.{ver.Build}.{ver.Revision}" : "";
        Title = $"{Loc.Get("AppTitle")} {verStr}";
    }

    /// <summary>
    /// Switches the active AI. Rejected while any terminal is running, because those
    /// child processes belong to the previous CLI.
    /// </summary>
    private bool TrySwitchProvider(string id)
    {
        if (string.IsNullOrEmpty(id) || id == _cli.ActiveId) return true;

        if (_children.Count > 0)
        {
            ShowMessageDialog(Loc.Get("CannotSwitchTitle"), Loc.Get("CannotSwitchWhileRunning"));
            return false;
        }

        _cli.ActiveId = id;
        _settings.CliProviderId = _cli.ActiveId;
        _settings.Save();

        ApplyProviderUi();
        RefreshSessionList();
        return true;
    }

    private void UpdateAiSelector()
    {
        var provider = _cli.Active;
        LblAiSelector.Text = provider.DisplayName;

        var installed = _cli.InstalledProviders.ToList();
        BtnAiSelector.IsEnabled = installed.Count > 0;

        var flyout = new MenuFlyout { Placement = PlacementMode.Top };
        foreach (var candidate in installed)
        {
            var item = new MenuItem { Header = candidate.DisplayName };
            if (candidate.Id == provider.Id)
            {
                item.Icon = new PathIcon
                {
                    Data = Geometry.Parse("M9 16.17L4.83 12L3.41 13.41L9 19L21 7L19.59 5.59Z"),
                    Width = 12,
                    Height = 12,
                };
            }
            var capturedId = candidate.Id;
            item.Click += (_, _) => TrySwitchProvider(capturedId);
            flyout.Items.Add(item);
        }
        BtnAiSelector.Flyout = flyout;

        ToolTip.SetTip(BtnAiSelector, provider.IsInstalled
            ? provider.ResolvedPath
            : Loc.Get("SwitchAiTooltip"));
    }

    private async Task DetectProviderVersionsAsync()
    {
        try
        {
            await _cli.DetectVersionsAsync();
        }
        catch { }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            RefreshProviderRadios();
            UpdateAiSelector();
        });
    }

    private void ShowMessageDialog(string title, string message)
    {
        var panelBg = _isDark ? Color.FromRgb(44, 44, 46) : Color.FromRgb(240, 240, 245);
        var fg = _isDark ? Color.FromRgb(210, 210, 215) : Color.FromRgb(40, 40, 45);

        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Foreground = new SolidColorBrush(fg),
        };
        var ok = new Button
        {
            Content = Loc.Get("OK"),
            MinWidth = 88,
            HorizontalAlignment = HorizontalAlignment.Right,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };

        var panel = new StackPanel { Spacing = 18, Margin = new Thickness(22, 20) };
        panel.Children.Add(text);
        panel.Children.Add(ok);

        var dialog = new Window
        {
            Title = title,
            Width = 430,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(panelBg),
            Content = panel,
        };
        ok.Click += (_, _) => dialog.Close();
        // A modal that only closes by mouse also blocks the app from shutting down.
        dialog.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape || args.Key == Key.Enter) dialog.Close();
        };
        _ = dialog.ShowDialog(this);
    }

    // ── Sidebar Panel ──

    private void OnActivityExplorer(object? sender, RoutedEventArgs e)
    {
        ToggleSidePanel(SidebarPanel.Explorer);
    }

    private void OnActivitySettings(object? sender, RoutedEventArgs e)
    {
        ToggleSidePanel(SidebarPanel.Settings);
    }

    private void OnActivitySnippets(object? sender, RoutedEventArgs e)
    {
        ToggleSidePanel(SidebarPanel.Snippets);
    }

    private void OnActivityWindows(object? sender, RoutedEventArgs e)
    {
        ToggleSidePanel(SidebarPanel.Windows);
    }

    private void OnActivityDocView(object? sender, RoutedEventArgs e)
    {
        if (_activeChildIndex < 0 || _activeChildIndex >= _children.Count) return;
        var terminal = _children[_activeChildIndex].Terminal;

        // Find session JSONL path
        string? sessionPath = null;
        if (CmbSessions.SelectedItem is SessionInfo selected)
            sessionPath = SessionMessageReader.FindSessionFile(_projectFolder ?? "", selected.Id);
        sessionPath ??= SessionMessageReader.FindMostRecentSession(_projectFolder ?? "");

        if (sessionPath != null)
            terminal.SetDocumentViewSession(sessionPath);

        terminal.ToggleDocumentView();
        SetActivityButtonActive(BtnActivityDocView, terminal.IsDocumentView);
    }

    private async void OnActivityDiagram(object? sender, RoutedEventArgs e)
    {
        var typeface = new Avalonia.Media.Typeface(_settings.FontFamily + ", Consolas, Courier New");

        // Check if current terminal has any diagrams (detected or cached)
        var diagrams = new List<Terminal.CodeBlockInfo>();
        if (_activeChildIndex >= 0 && _activeChildIndex < _children.Count)
            diagrams.AddRange(_children[_activeChildIndex].Terminal.GetAllDiagrams());

        if (diagrams.Count > 0)
        {
            // Show the most recent cached/detected diagram
            var win = new Terminal.DiagramWindow(diagrams[^1], _isDark, typeface);
            win.Show(this);
        }
        else
        {
            // No diagrams - open file dialog
            await Terminal.DiagramWindow.OpenFile(this, _isDark, typeface);
        }
    }

    /// <summary>
    /// Cycles the CLI's mode by sending it Shift+Tab. The status bar badge is the only button
    /// wired to this; the command palette calls it directly.
    /// </summary>
    private void SendModeSwitch()
    {
        if (_activeChildIndex >= 0 && _activeChildIndex < _children.Count)
        {
            _children[_activeChildIndex].Terminal.SendText("\x1b[Z"); // Shift+Tab
            _children[_activeChildIndex].Terminal.FocusTerminal();
        }
    }

    private void OnActivityCompact(object? sender, RoutedEventArgs e)
    {
        if (_activeChildIndex >= 0 && _activeChildIndex < _children.Count)
        {
            _children[_activeChildIndex].Terminal.SendText("/compact\r");
            BringToFront(_activeChildIndex);
            _children[_activeChildIndex].Terminal.FocusTerminal();
        }
    }

    private void ToggleSidePanel(SidebarPanel panel)
    {
        if (_activeSidePanel == panel)
        {
            // Close panel
            SaveSidePanelWidth();
            _activeSidePanel = SidebarPanel.None;
            SetSidePanelVisible(false);
            // Stop the auto-fetch timer; a hidden panel has nothing to show for the spawn.
            if (panel == SidebarPanel.SourceControl) _sourceControl?.OnPanelHidden();
        }
        else
        {
            _activeSidePanel = panel;
            ShowPanelContent(panel);
            SetSidePanelVisible(true);

            if (panel == SidebarPanel.Settings && !_settingsInitialized)
                InitializeSettingsPanel();
            if (panel == SidebarPanel.Snippets && !_snippetsInitialized)
                LoadSnippetsPanel();
            if (panel == SidebarPanel.Slash)
                Dispatcher.UIThread.Post(() => TxtSlashSearch.Focus());
        }

        UpdateActivityBarHighlight();
    }

    private void SetSidePanelVisible(bool visible)
    {
        var colDefs = MainContentGrid.ColumnDefinitions;

        if (visible)
        {
            colDefs[1].Width = new GridLength(_sidePanelWidth);
            colDefs[1].MinWidth = 150;
            colDefs[1].MaxWidth = 600;
            colDefs[2].Width = new GridLength(4);
            PanelSplitter.IsVisible = true;
        }
        else
        {
            colDefs[1].Width = new GridLength(0);
            colDefs[1].MinWidth = 0;
            colDefs[1].MaxWidth = 0;
            colDefs[2].Width = new GridLength(0);
            PanelSplitter.IsVisible = false;
        }
    }

    private void SaveSidePanelWidth()
    {
        var w = MainContentGrid.ColumnDefinitions[1].ActualWidth;
        if (w > 50) _sidePanelWidth = w;
    }

    private void ShowPanelContent(SidebarPanel panel)
    {
        ExplorerPanel.IsVisible = panel == SidebarPanel.Explorer;
        SettingsPanel.IsVisible = panel == SidebarPanel.Settings;
        SnippetsPanel.IsVisible = panel == SidebarPanel.Snippets;
        WindowsPanel.IsVisible = panel == SidebarPanel.Windows;
        SourceControlHost.IsVisible = panel == SidebarPanel.SourceControl;
        SlashPanel.IsVisible = panel == SidebarPanel.Slash;
        ExtensionsPanel.IsVisible = panel == SidebarPanel.Extensions;
        SidePanelTitle.Text = panel switch
        {
            SidebarPanel.Explorer => Loc.Get("EXPLORER"),
            SidebarPanel.Settings => Loc.Get("SETTINGS"),
            SidebarPanel.Snippets => Loc.Get("SNIPPETS"),
            SidebarPanel.Windows => Loc.Get("WINDOWS"),
            SidebarPanel.SourceControl => Loc.Get("SOURCE_CONTROL"),
            SidebarPanel.Slash => Loc.Get("SLASH"),
            SidebarPanel.Extensions => Loc.Get("EXTENSIONS"),
            _ => ""
        };
        BtnBrowseFolder.IsVisible = panel == SidebarPanel.Explorer;
        if (panel == SidebarPanel.Windows)
            RefreshWindowsPanel();
        if (panel == SidebarPanel.Settings && _settingsInitialized)
        {
            // The source-control panel has its own [JA|EN] toggle over the same setting.
            _suppressSettingsChanged = true;
            FillCommitLanguageCombo();
            _suppressSettingsChanged = false;
        }
        // Shown starts the auto-fetch timer and reloads; anything else stops it.
        if (panel == SidebarPanel.SourceControl)
            _sourceControl?.OnPanelShown();
        else
            _sourceControl?.OnPanelHidden();
        if (panel == SidebarPanel.Slash)
            RefreshSlashPanel();
        if (panel == SidebarPanel.Extensions)
            RefreshExtensionsPanel();
    }

    private void UpdateActivityBarHighlight()
    {
        SetActivityButtonActive(BtnActivityExplorer, _activeSidePanel == SidebarPanel.Explorer);
        SetActivityButtonActive(BtnActivitySnippets, _activeSidePanel == SidebarPanel.Snippets);
        SetActivityButtonActive(BtnActivitySettings, _activeSidePanel == SidebarPanel.Settings);
        SetActivityButtonActive(BtnActivityWindows, _activeSidePanel == SidebarPanel.Windows);
        SetActivityButtonActive(BtnActivitySourceControl, _activeSidePanel == SidebarPanel.SourceControl);
        SetActivityButtonActive(BtnActivitySlash, _activeSidePanel == SidebarPanel.Slash);
        SetActivityButtonActive(BtnActivityExtensions, _activeSidePanel == SidebarPanel.Extensions);
        // DocView button state is managed by OnActivityDocView, not side panel
    }

    private static void SetActivityButtonActive(Button btn, bool active)
    {
        if (active)
        {
            if (!btn.Classes.Contains("active"))
                btn.Classes.Add("active");
        }
        else
        {
            btn.Classes.Remove("active");
        }
    }

    // ── Windows Panel ──

    private void RefreshWindowsPanel()
    {
        if (!WindowsPanel.IsVisible) return;
        WindowsList.Children.Clear();

        for (int i = 0; i < _children.Count; i++)
        {
            var child = _children[i];
            int idx = i;
            bool isActive = _activeLayoutItem == null
                ? i == _activeChildIndex
                : ReferenceEquals(child, _activeLayoutItem);
            bool isRunning = child.Terminal.IsProcessRunning;

            var dot = new Ellipse
            {
                Width = 8, Height = 8,
                Fill = isRunning
                    ? new SolidColorBrush(Color.FromRgb(48, 209, 88))
                    : new SolidColorBrush(Color.FromRgb(142, 142, 147)),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 5, 0, 0),
            };

            // The tab is the one name a window has, so this row reads it rather than keeping a
            // second opinion: a rename, or a session renamed with /rename, reaches both at once.
            var displayText = !string.IsNullOrWhiteSpace(child.StripText.Text)
                ? child.StripText.Text
                : child.Terminal.FirstUserInput ?? _cli.Active.Name;

            var title = new TextBlock
            {
                Text = displayText,
                FontSize = 13,
                FontWeight = FontWeight.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            var textStack = new StackPanel { Spacing = 1 };
            textStack.Children.Add(title);

            var closeBtn = new Button
            {
                Content = "\u00D7",
                FontSize = 12,
                Padding = new Thickness(4, 0),
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)),
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(4),
                Cursor = new Cursor(StandardCursorType.Hand),
                Margin = new Thickness(4, 2, 0, 0),
            };
            closeBtn.Click += (_, ev) => { CloseChild(child); ev.Handled = true; };

            var grid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto") };
            Grid.SetColumn(dot, 0);
            Grid.SetColumn(textStack, 1);
            Grid.SetColumn(closeBtn, 2);
            grid.Children.Add(dot);
            grid.Children.Add(textStack);
            grid.Children.Add(closeBtn);
            grid.Margin = new Thickness(4, 0);

            var item = new Border
            {
                Child = grid,
                Padding = new Thickness(6, 5),
                CornerRadius = new CornerRadius(6),
                Background = isActive
                    ? new SolidColorBrush(Color.FromArgb(30, 0, 122, 255))
                    : Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand),
            };

            // Preview tooltip (first 10 lines of terminal output)
            var preview = child.Terminal.GetPreviewText(10);
            if (!string.IsNullOrWhiteSpace(preview))
            {
                var previewBlock = new TextBlock
                {
                    Text = preview,
                    FontSize = 11,
                    FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
                    MaxWidth = 500,
                    TextWrapping = TextWrapping.NoWrap,
                };
                ToolTip.SetTip(item, previewBlock);
                ToolTip.SetShowDelay(item, 300);
            }

            item.PointerPressed += (_, _) =>
            {
                BringToFront(idx);
                _children[idx].Terminal.FocusTerminal();
            };
            // Hover
            item.PointerEntered += (s, _) =>
            {
                if (!isActive) ((Border)s!).Background = new SolidColorBrush(_isDark ? Color.FromArgb(20, 255, 255, 255) : Color.FromArgb(20, 0, 0, 0));
            };
            item.PointerExited += (s, _) =>
            {
                if (!isActive) ((Border)s!).Background = Brushes.Transparent;
            };

            WindowsList.Children.Add(item);

            foreach (var run in SubagentMonitor.ReadRunning(ResolveSessionPath(child)))
                WindowsList.Children.Add(BuildSubagentRow(run));
        }

        foreach (var editor in _editorChildren)
            WindowsList.Children.Add(BuildEditorWindowRow(editor));

        foreach (var graph in _graphChildren)
            WindowsList.Children.Add(BuildGraphWindowRow(graph));
    }

    /// <summary>
    /// An open file window, listed alongside the sessions: same click-to-activate and close as a
    /// session row, with the path on the tooltip since the row only has room for a file name.
    /// </summary>
    private Control BuildEditorWindowRow(EditorChildInfo editor) =>
        BuildLayoutWindowRow(editor, editor.StripText.Text ?? "", editor.Doc.Path, EditorDotColor);

    /// <summary>The commit history, listed the same way an open file is.</summary>
    private Control BuildGraphWindowRow(GraphChildInfo graph) =>
        BuildLayoutWindowRow(graph, graph.StripText.Text ?? "", graph.RepoRoot, GraphDotColor);

    /// <summary>
    /// One non-terminal MDI window as a row in the windows panel: a coloured dot naming what
    /// kind of window it is, its title, and a close button.
    /// </summary>
    private Control BuildLayoutWindowRow(IMdiLayoutItem item, string text, string tooltip, Color dotColor)
    {
        bool isActive = ReferenceEquals(item, _activeLayoutItem);

        var dot = new Ellipse
        {
            Width = 8, Height = 8,
            Fill = new SolidColorBrush(dotColor),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 5, 0, 0),
        };

        var title = new TextBlock
        {
            Text = text,
            FontSize = 13,
            FontWeight = FontWeight.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var closeBtn = new Button
        {
            Content = "×",
            FontSize = 12,
            Padding = new Thickness(4, 0),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Top,
            CornerRadius = new CornerRadius(4),
            Cursor = new Cursor(StandardCursorType.Hand),
            Margin = new Thickness(4, 2, 0, 0),
        };
        closeBtn.Click += (_, ev) => { CloseLayoutItem(item); ev.Handled = true; };

        var grid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto") };
        Grid.SetColumn(dot, 0);
        Grid.SetColumn(title, 1);
        Grid.SetColumn(closeBtn, 2);
        grid.Children.Add(dot);
        grid.Children.Add(title);
        grid.Children.Add(closeBtn);
        grid.Margin = new Thickness(4, 0);

        var row = new Border
        {
            Child = grid,
            Padding = new Thickness(6, 5),
            CornerRadius = new CornerRadius(6),
            Background = isActive
                ? new SolidColorBrush(Color.FromArgb(30, 0, 122, 255))
                : Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(row, tooltip);
        ToolTip.SetShowDelay(row, 300);

        row.PointerPressed += (_, _) => ActivateLayoutItem(item);
        row.PointerEntered += (s, _) =>
        {
            if (!isActive) ((Border)s!).Background = new SolidColorBrush(_isDark ? Color.FromArgb(20, 255, 255, 255) : Color.FromArgb(20, 0, 0, 0));
        };
        row.PointerExited += (s, _) =>
        {
            if (!isActive) ((Border)s!).Background = Brushes.Transparent;
        };

        return row;
    }

    /// <summary>
    /// One running subagent, indented under the window that spawned it. There is nothing to
    /// click: the CLI owns the task, and the row exists to answer "what is it doing".
    /// </summary>
    private Control BuildSubagentRow(SubagentRun run)
    {
        var dot = new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = new SolidColorBrush(Color.FromRgb(255, 214, 10)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0),
        };

        var label = new TextBlock
        {
            Text = run.Label,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var elapsed = new TextBlock
        {
            Text = FormatElapsed(DateTime.Now - run.Started),
            FontSize = 10,
            Opacity = 0.5,
            Margin = new Thickness(6, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var grid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto") };
        Grid.SetColumn(dot, 0);
        Grid.SetColumn(label, 1);
        Grid.SetColumn(elapsed, 2);
        grid.Children.Add(dot);
        grid.Children.Add(label);
        grid.Children.Add(elapsed);

        var tip = run.Label;
        if (!string.IsNullOrEmpty(run.AgentType))
            tip += Environment.NewLine + run.AgentType + (run.Model != null ? "  ·  " + run.Model : "");
        if (run.Depth > 1)
            tip += Environment.NewLine + string.Format(Loc.Get("SubagentDepthFmt"), run.Depth);
        ToolTip.SetTip(grid, tip);

        // Nested agents step in again, so the depth reads off the indent.
        return new Border
        {
            Child = grid,
            Padding = new Thickness(6, 3),
            Margin = new Thickness(14 + (run.Depth - 1) * 10, 0, 4, 0),
            CornerRadius = new CornerRadius(4),
        };
    }

    private static string FormatElapsed(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        return span.TotalHours >= 1
            ? ((int)span.TotalHours) + "h" + span.Minutes + "m"
            : span.TotalMinutes >= 1
                ? span.Minutes + "m" + span.Seconds + "s"
                : span.Seconds + "s";
    }

    /// <summary>
    /// Which subagents are in flight, as one string. The panel is rebuilt from scratch, so it
    /// is only rebuilt when this changes - otherwise a hover would be dropped twice a second.
    /// </summary>
    private string _subagentSignature = "";
    private DateTime _subagentDrawn = DateTime.MinValue;

    /// <summary>
    /// Keeps the subagent rows current while the windows panel is open. The scan itself only
    /// reads what the transcripts have grown by, but the panel is rebuilt wholesale - so a
    /// rebuild waits for the set to change, or for a second to pass so the timers move.
    /// </summary>
    private void RefreshSubagents()
    {
        if (!WindowsPanel.IsVisible) return;

        var signature = SubagentSignature();
        bool ticking = signature.Length > 0
            && DateTime.Now - _subagentDrawn > TimeSpan.FromSeconds(1);
        if (signature == _subagentSignature && !ticking) return;

        _subagentSignature = signature;
        _subagentDrawn = DateTime.Now;
        RefreshWindowsPanel();
    }

    private string SubagentSignature()
    {
        var parts = new List<string>();
        foreach (var child in _children)
            foreach (var run in SubagentMonitor.ReadRunning(ResolveSessionPath(child)))
                parts.Add(run.Id);
        return string.Join("|", parts);
    }

    // ── File Tree ──

    private void RefreshFileTree()
    {
        if (!string.IsNullOrEmpty(_projectFolder) && Directory.Exists(_projectFolder))
        {
            FileTree.ItemsSource = FileTreeNode.CreateRootNodes(_projectFolder);
        }
        else
        {
            FileTree.ItemsSource = null;
        }
    }

    private void StartFileWatcher()
    {
        _fileWatcher?.Dispose();
        _fileWatcher = null;

        if (string.IsNullOrEmpty(_projectFolder) || !Directory.Exists(_projectFolder))
            return;

        var watcher = new FileSystemWatcher(_projectFolder)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
        };

        void OnChanged(object s, FileSystemEventArgs e) => Route(e.FullPath);
        void OnRenamed(object s, RenamedEventArgs e) => Route(e.FullPath);

        // A checkout moves HEAD; everything else is the working tree.
        void Route(string path)
        {
            if (IsHeadWrite(path)) ScheduleGitInfoRefresh();
            else ScheduleFileTreeRefresh();
        }

        watcher.Created += OnChanged;
        watcher.Deleted += OnChanged;
        watcher.Renamed += OnRenamed;
        watcher.EnableRaisingEvents = true;
        _fileWatcher = watcher;
    }

    private void ScheduleFileTreeRefresh()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_fileWatcherDebounce == null)
            {
                _fileWatcherDebounce = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(500)
                };
                _fileWatcherDebounce.Tick += (_, _) =>
                {
                    _fileWatcherDebounce.Stop();
                    RefreshFileTree();
                };
            }
            _fileWatcherDebounce.Stop();
            _fileWatcherDebounce.Start();
        });
    }

    /// <summary>
    /// True for the file git rewrites when the checked-out branch changes. Git never writes
    /// HEAD in place - it fills HEAD.lock and renames it over the top - so watching for names
    /// alone catches a checkout without subscribing to content changes across the whole tree.
    /// Matching on the name rather than on a .git path keeps it working inside a worktree,
    /// where HEAD lives under .git/worktrees instead.
    /// </summary>
    private static bool IsHeadWrite(string path) =>
        System.IO.Path.GetFileName(path).StartsWith("HEAD", StringComparison.Ordinal);

    /// <summary>
    /// Re-reads the git readout once HEAD settles. A checkout touches HEAD two or three times
    /// in quick succession, and each read costs two git processes, so the debounce is what
    /// keeps a branch switch to a single refresh.
    /// </summary>
    private void ScheduleGitInfoRefresh()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_gitInfoDebounce == null)
            {
                _gitInfoDebounce = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(400)
                };
                _gitInfoDebounce.Tick += (_, _) =>
                {
                    _gitInfoDebounce.Stop();
                    RefreshGitInfo();
                };
            }
            _gitInfoDebounce.Stop();
            _gitInfoDebounce.Start();
        });
    }

    // ── Explorer editor (floating MDI windows) ──

    /// <summary>
    /// One floating text-editor window living on the same MDI canvas as terminal windows.
    /// Kept entirely separate from <see cref="MdiChildInfo"/>/_children: that record is woven
    /// through terminal-session concerns (cost polling, effort/model commands, exit-and-wait
    /// disposal) that make no sense for a text file, so an editor window gets its own minimal
    /// lifecycle instead of forcing every one of those call sites to special-case it.
    /// </summary>
    private sealed class EditorChildInfo : IMdiLayoutItem
    {
        public required Border Container { get; init; }
        public required Border TitleBar { get; init; }
        public required TextBox EditorBox { get; init; }
        public required TextBlock TitleText { get; init; }
        public required Button SaveButton { get; init; }
        public required TextBlock NoticeText { get; init; }
        public required Button StripButton { get; init; }
        public required TextBlock StripText { get; init; }
        public required TextFileDocument Doc { get; set; }
        public bool Dirty { get; set; }
    }

    private readonly List<EditorChildInfo> _editorChildren = new();

    /// <summary>
    /// Opens <paramref name="path"/> in its own floating editor window on the MDI canvas, or
    /// brings the existing one to front if that file is already open.
    /// </summary>
    private void OpenFileEditorWindow(string path)
    {
        var existing = _editorChildren.FirstOrDefault(c =>
            string.Equals(c.Doc.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null) { BringEditorToFront(existing); return; }

        TextFileDocument doc;
        try { doc = TextFileEditor.Read(path); }
        catch { return; }

        var titleText = new TextBlock
        {
            Text = System.IO.Path.GetFileName(doc.Path),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(210, 210, 215)),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        ToolTip.SetTip(titleText, doc.Path);

        var saveButton = new Button
        {
            Content = new PathIcon
            {
                Data = StreamGeometry.Parse("M17 3H5C3.89 3 3 3.9 3 5V19C3 20.1 3.89 21 5 21H19C20.1 21 21 20.1 21 19V7L17 3ZM12 19C10.34 19 9 17.66 9 16C9 14.34 10.34 13 12 13C13.66 13 15 14.34 15 16C15 17.66 13.66 19 12 19ZM15 9H5V5H15V9Z"),
                Width = 13, Height = 13
            },
            Padding = new Thickness(6, 0),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)),
            BorderThickness = new Thickness(0),
            IsVisible = doc.Editable,
            IsEnabled = false,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        ToolTip.SetTip(saveButton, Loc.Get("EditorSaveTooltip"));

        var closeButton = new Button
        {
            Content = "×",
            FontSize = 14,
            Padding = new Thickness(6, 0),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand)
        };

        var titleLeft = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        titleLeft.Children.Add(titleText);

        var titleRight = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        titleRight.Children.Add(saveButton);
        titleRight.Children.Add(closeButton);

        var titleGrid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        Grid.SetColumn(titleLeft, 0);
        Grid.SetColumn(titleRight, 1);
        titleGrid.Children.Add(titleLeft);
        titleGrid.Children.Add(titleRight);

        var titleBar = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(44, 44, 46)),  // Apple elevated surface
            Padding = new Thickness(0, 6),
            Child = titleGrid,
            Cursor = new Cursor(StandardCursorType.Hand)
        };

        var noticeText = new TextBlock
        {
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
            Margin = new Thickness(9, 4, 8, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(152, 152, 157))
        };

        var editorBox = new TextBox
        {
            Text = doc.Text,
            FontSize = ClampEditorFontSize(_settings.EditorFontSize),
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap,
            IsReadOnly = !doc.Editable,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Color.FromRgb(28, 28, 30)),  // Apple systemBackground
            Foreground = new SolidColorBrush(Color.FromRgb(224, 224, 228)),
            CaretBrush = new SolidColorBrush(Color.FromRgb(224, 224, 228)),
            Padding = new Thickness(8, 4),
            CaretIndex = 0
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(editorBox, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(editorBox, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);

        var body = new DockPanel();
        DockPanel.SetDock(noticeText, Dock.Bottom);
        body.Children.Add(noticeText);
        body.Children.Add(editorBox);

        var dockPanel = new DockPanel();
        DockPanel.SetDock(titleBar, Dock.Top);
        dockPanel.Children.Add(titleBar);
        dockPanel.Children.Add(body);

        var container = new Border
        {
            Child = dockPanel,
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            BorderThickness = new Thickness(0.5),
            ClipToBounds = true,
            Background = new SolidColorBrush(Color.FromRgb(28, 28, 30))
        };

        // --- Window strip button, built like a terminal window's so the two read as one set ---
        var stripDot = new Ellipse
        {
            Width = 7, Height = 7,
            Fill = new SolidColorBrush(Color.FromRgb(0, 122, 255)),  // Apple Blue: a file, not a session
            VerticalAlignment = VerticalAlignment.Center
        };
        var stripText = new TextBlock
        {
            Text = System.IO.Path.GetFileName(doc.Path),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 120
        };
        var stripCloseBtn = new Button
        {
            Content = "×",
            FontSize = 12,
            Padding = new Thickness(3, 0),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(3),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        var stripContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        stripContent.Children.Add(stripDot);
        stripContent.Children.Add(stripText);
        stripContent.Children.Add(stripCloseBtn);

        var stripButton = new Button
        {
            Content = stripContent,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 4),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        ToolTip.SetTip(stripButton, doc.Path);

        var entry = new EditorChildInfo
        {
            Container = container,
            TitleBar = titleBar,
            EditorBox = editorBox,
            TitleText = titleText,
            SaveButton = saveButton,
            NoticeText = noticeText,
            StripButton = stripButton,
            StripText = stripText,
            Doc = doc
        };

        stripButton.Click += (_, _) => ActivateLayoutItem(entry);
        stripCloseBtn.Click += (_, e) => { _ = CloseEditorWindowAsync(entry); e.Handled = true; };

        var notice = doc.Block switch
        {
            EditBlock.TooLarge => Loc.Get("EditorTooLarge"),
            EditBlock.Binary => Loc.Get("EditorBinary"),
            EditBlock.NotUtf8 => Loc.Get("EditorNotUtf8"),
            _ => null,
        };
        noticeText.Text = notice ?? "";
        noticeText.IsVisible = notice != null;

        closeButton.Click += (_, _) => _ = CloseEditorWindowAsync(entry);
        editorBox.TextChanged += (_, _) => OnEditorTextChanged(entry);
        editorBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                _ = SaveEditorAsync(entry);
                e.Handled = true;
            }
        };
        saveButton.Click += (_, _) => _ = SaveEditorAsync(entry);

        bool dragging = false;
        Point dragStart = default;
        double dragLeft = 0, dragTop = 0;
        // The editor TextBox and the title-bar buttons mark PointerPressed handled, so a plain
        // bubbling handler here would only ever see clicks on the window's padding. Tunnel runs
        // before them, which is what makes clicking anywhere in the window select it.
        container.AddHandler(InputElement.PointerPressedEvent,
            (_, _) => BringEditorToFront(entry), RoutingStrategies.Tunnel);
        // Ctrl+wheel zooms the text like any editor. Tunnel for the same reason: the TextBox's
        // ScrollViewer would otherwise scroll the file and swallow the wheel.
        container.AddHandler(InputElement.PointerWheelChangedEvent, (_, e) =>
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
            AdjustEditorFontSize(e.Delta.Y > 0 ? 1 : -1);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        titleBar.PointerPressed += (_, e) =>
        {
            dragging = true;
            dragStart = e.GetPosition(MdiContainer);
            double left = Canvas.GetLeft(container);
            double top = Canvas.GetTop(container);
            dragLeft = double.IsNaN(left) ? 0 : left;
            dragTop = double.IsNaN(top) ? 0 : top;
            e.Pointer.Capture(titleBar);
            e.Handled = true;
        };
        titleBar.PointerMoved += (_, e) =>
        {
            if (!dragging) return;
            var pos = e.GetPosition(MdiContainer);
            Canvas.SetLeft(container, dragLeft + pos.X - dragStart.X);
            Canvas.SetTop(container, dragTop + pos.Y - dragStart.Y);
            e.Handled = true;
        };
        titleBar.PointerReleased += (_, e) =>
        {
            dragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        };

        _editorChildren.Add(entry);
        MdiContainer.Children.Add(container);
        WindowStrip.Children.Add(stripButton);
        _activeLayoutItem = entry;
        ArrangeChildren();
        Dispatcher.UIThread.Post(() => editorBox.Focus());
    }

    private void BringEditorToFront(EditorChildInfo entry) => SetActiveLayoutItem(entry);

    private const double EditorFontMin = 8;
    private const double EditorFontMax = 32;

    private static double ClampEditorFontSize(double size) =>
        double.IsNaN(size) || size <= 0 ? 12 : Math.Clamp(size, EditorFontMin, EditorFontMax);

    /// <summary>
    /// Ctrl+wheel zoom. One size is shared by every editor window and saved with the settings,
    /// so a file opened later comes up at the size the user last picked.
    /// </summary>
    private void AdjustEditorFontSize(int step)
    {
        double size = ClampEditorFontSize(ClampEditorFontSize(_settings.EditorFontSize) + step);
        if (Math.Abs(size - _settings.EditorFontSize) < 0.01) return;

        _settings.EditorFontSize = size;
        foreach (var editor in _editorChildren) editor.EditorBox.FontSize = size;
        _settings.Save();
    }

    /// <summary>
    /// Marks which window ArrangeChildren treats as "on top". Tile layouts don't overlap so
    /// there's nothing to reorder there; Maximize/Cascade need a re-layout to show the change.
    /// </summary>
    private void SetActiveLayoutItem(IMdiLayoutItem item)
    {
        // Every click inside a window now reaches here. Re-running the layout for a window that
        // already holds the front would snap a Cascade window back from wherever it was dragged.
        if (ReferenceEquals(_activeLayoutItem, item)) return;

        _activeLayoutItem = item;
        if (_layout == MdiLayout.Cascade || _layout == MdiLayout.Maximize)
        {
            ArrangeChildren();   // repaints the strip on its way out
            return;
        }
        UpdateStripSelection();
        RefreshWindowsPanel();
    }

    /// <summary>Brings any MDI window - terminal or editor - to the front and focuses it.</summary>
    private void ActivateLayoutItem(IMdiLayoutItem item)
    {
        switch (item)
        {
            case MdiChildInfo child:
                int idx = _children.IndexOf(child);
                if (idx < 0) return;
                BringToFront(idx);
                child.Terminal.FocusTerminal();
                break;

            case EditorChildInfo editor:
                SetActiveLayoutItem(editor);
                editor.EditorBox.Focus();
                break;

            case GraphChildInfo graph:
                SetActiveLayoutItem(graph);
                graph.Panel.Focus();
                break;
        }
    }

    private void CloseLayoutItem(IMdiLayoutItem item)
    {
        if (item is MdiChildInfo child) CloseChild(child);
        else if (item is EditorChildInfo editor) _ = CloseEditorWindowAsync(editor);
        else if (item is GraphChildInfo graph) CloseGraphWindow(graph);
    }

    /// <summary>
    /// Dirty is "differs from what was read", not "a key was pressed". TextChanged also fires
    /// for the load itself, and this way typing something back the way it was clears the mark
    /// rather than leaving a file marked unsaved with nothing to save.
    /// </summary>
    private void OnEditorTextChanged(EditorChildInfo entry)
    {
        if (!entry.Doc.Editable) return;

        bool dirty = (entry.EditorBox.Text ?? "") != entry.Doc.Text;
        if (dirty == entry.Dirty) return;

        entry.Dirty = dirty;
        entry.SaveButton.IsEnabled = dirty;
        entry.TitleText.Text = dirty
            ? System.IO.Path.GetFileName(entry.Doc.Path) + " ●"
            : System.IO.Path.GetFileName(entry.Doc.Path);
        entry.StripText.Text = entry.TitleText.Text;
    }

    /// <summary>
    /// Offers to save before the window closes. False means the user backed out and the window
    /// should stay open.
    /// </summary>
    private async Task<bool> ReleaseEditorAsync(EditorChildInfo entry)
    {
        if (!entry.Dirty) return true;

        var save = await ShowConfirmDialog(
            Loc.Get("EditorUnsavedTitle"),
            string.Format(Loc.Get("EditorUnsavedFmt"), System.IO.Path.GetFileName(entry.Doc.Path)));

        // Cancel discards: the alternative is a third button, and the edits are still on screen
        // until something replaces them.
        return !save || await SaveEditorAsync(entry);
    }

    private async Task<bool> SaveEditorAsync(EditorChildInfo entry)
    {
        if (!entry.Doc.Editable || !entry.Dirty) return true;

        if (TextFileEditor.ChangedOnDisk(entry.Doc) &&
            !await ShowConfirmDialog(
                Loc.Get("EditorConflictTitle"),
                string.Format(Loc.Get("EditorConflictFmt"), System.IO.Path.GetFileName(entry.Doc.Path))))
            return false;

        var saved = TextFileEditor.Write(entry.Doc, entry.EditorBox.Text ?? "", out var error);
        if (saved == null)
        {
            entry.NoticeText.Text = string.Format(Loc.Get("EditorSaveFailedFmt"), error);
            entry.NoticeText.IsVisible = true;
            return false;
        }

        entry.Doc = saved;
        entry.Dirty = false;
        entry.SaveButton.IsEnabled = false;
        entry.NoticeText.IsVisible = false;
        entry.TitleText.Text = System.IO.Path.GetFileName(entry.Doc.Path);
        entry.StripText.Text = entry.TitleText.Text;
        return true;
    }

    private async Task CloseEditorWindowAsync(EditorChildInfo entry)
    {
        if (!await ReleaseEditorAsync(entry)) return;
        _editorChildren.Remove(entry);
        MdiContainer.Children.Remove(entry.Container);
        WindowStrip.Children.Remove(entry.StripButton);
        if (ReferenceEquals(_activeLayoutItem, entry)) _activeLayoutItem = null;
        ArrangeChildren();
        // ArrangeChildren bails out when nothing is left on the canvas, so the strip and the
        // windows panel would keep showing a closed file's row without this.
        UpdateStripSelection();
        RefreshWindowsPanel();
    }


    // ── Commit graph (floating MDI windows) ──

    /// <summary>Apple Blue: an open file.</summary>
    private static readonly Color EditorDotColor = Color.FromRgb(0, 122, 255);

    /// <summary>Apple Purple: a repository's history, so it reads apart from files at a glance.</summary>
    private static readonly Color GraphDotColor = Color.FromRgb(191, 90, 242);

    /// <summary>
    /// One commit-history window on the MDI canvas. Built like <see cref="EditorChildInfo"/> and
    /// for the same reason: the history has no session, no file and nothing to save, so it gets
    /// its own small lifecycle rather than being fitted into either of the other two.
    /// </summary>
    private sealed class GraphChildInfo : IMdiLayoutItem
    {
        public required Border Container { get; init; }
        public required Border TitleBar { get; init; }
        public required Controls.CommitGraphPanel Panel { get; init; }
        public required Button StripButton { get; init; }
        public required TextBlock StripText { get; init; }

        /// <summary>The repository being shown. One window per repository.</summary>
        public required string RepoRoot { get; init; }
    }

    private readonly List<GraphChildInfo> _graphChildren = new();

    /// <summary>
    /// Opens the commit history for <paramref name="repoRoot"/> in its own window on the MDI
    /// canvas, or brings the existing one to front if that repository is already showing.
    /// </summary>
    private void OpenCommitGraphWindow(string repoRoot, string repoLabel)
    {
        if (string.IsNullOrEmpty(repoRoot)) return;

        var existing = _graphChildren.FirstOrDefault(g =>
            string.Equals(g.RepoRoot, repoRoot, StringComparison.OrdinalIgnoreCase));
        if (existing != null) { ActivateLayoutItem(existing); return; }

        var panel = new Controls.CommitGraphPanel(
            repoRoot, repoLabel, _isDark,
            new Typeface(_settings.FontFamily + ", Consolas, Courier New"),
            SendToActiveTerminal);

        var titleText = new TextBlock
        {
            Text = panel.GraphTitle,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(210, 210, 215)),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        ToolTip.SetTip(titleText, repoRoot);

        var closeButton = new Button
        {
            Content = "×",
            FontSize = 14,
            Padding = new Thickness(6, 0),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand)
        };

        var titleLeft = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        titleLeft.Children.Add(titleText);

        var titleRight = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        titleRight.Children.Add(closeButton);

        var titleGrid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        Grid.SetColumn(titleLeft, 0);
        Grid.SetColumn(titleRight, 1);
        titleGrid.Children.Add(titleLeft);
        titleGrid.Children.Add(titleRight);

        var titleBar = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(44, 44, 46)),  // Apple elevated surface
            Padding = new Thickness(0, 6),
            Child = titleGrid,
            Cursor = new Cursor(StandardCursorType.Hand)
        };

        var dockPanel = new DockPanel();
        DockPanel.SetDock(titleBar, Dock.Top);
        dockPanel.Children.Add(titleBar);
        dockPanel.Children.Add(panel);

        var container = new Border
        {
            Child = dockPanel,
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            BorderThickness = new Thickness(0.5),
            ClipToBounds = true,
            Background = new SolidColorBrush(Color.FromRgb(28, 28, 30))
        };

        // --- Window strip button, built like an editor window's so the two read as one set ---
        var stripDot = new Ellipse
        {
            Width = 7, Height = 7,
            Fill = new SolidColorBrush(GraphDotColor),
            VerticalAlignment = VerticalAlignment.Center
        };
        var stripText = new TextBlock
        {
            Text = panel.GraphTitle,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 120
        };
        var stripCloseBtn = new Button
        {
            Content = "×",
            FontSize = 12,
            Padding = new Thickness(3, 0),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(3),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        var stripContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        stripContent.Children.Add(stripDot);
        stripContent.Children.Add(stripText);
        stripContent.Children.Add(stripCloseBtn);

        var stripButton = new Button
        {
            Content = stripContent,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 4),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        ToolTip.SetTip(stripButton, repoRoot);

        var entry = new GraphChildInfo
        {
            Container = container,
            TitleBar = titleBar,
            Panel = panel,
            StripButton = stripButton,
            StripText = stripText,
            RepoRoot = repoRoot
        };

        stripButton.Click += (_, _) => ActivateLayoutItem(entry);
        stripCloseBtn.Click += (_, e) => { CloseGraphWindow(entry); e.Handled = true; };
        closeButton.Click += (_, _) => CloseGraphWindow(entry);
        panel.CloseRequested += (_, _) => CloseGraphWindow(entry);

        bool dragging = false;
        Point dragStart = default;
        double dragLeft = 0, dragTop = 0;
        // The graph list and the detail pane mark PointerPressed handled, so a plain bubbling
        // handler here would only ever see clicks on the window's padding. Tunnel runs before
        // them, which is what makes clicking anywhere in the window select it.
        container.AddHandler(InputElement.PointerPressedEvent,
            (_, _) => SetActiveLayoutItem(entry), RoutingStrategies.Tunnel);
        titleBar.PointerPressed += (_, e) =>
        {
            dragging = true;
            dragStart = e.GetPosition(MdiContainer);
            double left = Canvas.GetLeft(container);
            double top = Canvas.GetTop(container);
            dragLeft = double.IsNaN(left) ? 0 : left;
            dragTop = double.IsNaN(top) ? 0 : top;
            e.Pointer.Capture(titleBar);
            e.Handled = true;
        };
        titleBar.PointerMoved += (_, e) =>
        {
            if (!dragging) return;
            var pos = e.GetPosition(MdiContainer);
            Canvas.SetLeft(container, dragLeft + pos.X - dragStart.X);
            Canvas.SetTop(container, dragTop + pos.Y - dragStart.Y);
            e.Handled = true;
        };
        titleBar.PointerReleased += (_, e) =>
        {
            dragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        };

        _graphChildren.Add(entry);
        MdiContainer.Children.Add(container);
        WindowStrip.Children.Add(stripButton);
        _activeLayoutItem = entry;
        ArrangeChildren();
        Dispatcher.UIThread.Post(() => panel.Focus());
    }

    private void CloseGraphWindow(GraphChildInfo entry)
    {
        _graphChildren.Remove(entry);
        MdiContainer.Children.Remove(entry.Container);
        WindowStrip.Children.Remove(entry.StripButton);
        if (ReferenceEquals(_activeLayoutItem, entry)) _activeLayoutItem = null;
        ArrangeChildren();
        // ArrangeChildren bails out when nothing is left on the canvas, so the strip and the
        // windows panel would keep showing a closed window's row without this.
        UpdateStripSelection();
        RefreshWindowsPanel();
    }


    // ── Explorer → terminal drag & drop ──

    private Point _treeDragOrigin;
    private FileTreeNode? _treeDragCandidate;
    private bool _treeDragActive;

    // DoDragDropAsync wants the press that started the gesture, but the drag only
    // begins once the pointer has moved far enough, so the args are kept until then.
    private PointerPressedEventArgs? _treeDragTrigger;

    private void HookFileTreeDrag()
    {
        // Tunnel: TreeViewItem handles pointer events for selection, so bubbling never reaches us
        FileTree.AddHandler(InputElement.PointerPressedEvent, OnFileTreePointerPressed, RoutingStrategies.Tunnel);
        FileTree.AddHandler(InputElement.PointerMovedEvent, OnFileTreePointerMoved, RoutingStrategies.Tunnel);
        FileTree.AddHandler(InputElement.PointerReleasedEvent, OnFileTreePointerReleased, RoutingStrategies.Tunnel);
    }

    private void OnFileTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _treeDragCandidate = null;
        _treeDragTrigger = null;
        if (!e.GetCurrentPoint(FileTree).Properties.IsLeftButtonPressed) return;

        var node = FindTreeNode(e.Source);
        if (node == null || string.IsNullOrEmpty(node.FullPath)) return;

        _treeDragOrigin = e.GetPosition(FileTree);
        _treeDragCandidate = node;
        _treeDragTrigger = e;
    }

    private async void OnFileTreePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_treeDragCandidate == null || _treeDragActive) return;

        if (!e.GetCurrentPoint(FileTree).Properties.IsLeftButtonPressed)
        {
            _treeDragCandidate = null;
            return;
        }

        var pos = e.GetPosition(FileTree);
        if (Math.Abs(pos.X - _treeDragOrigin.X) < 4 && Math.Abs(pos.Y - _treeDragOrigin.Y) < 4)
            return;

        var node = _treeDragCandidate;
        var trigger = _treeDragTrigger;
        _treeDragCandidate = null;
        _treeDragTrigger = null;
        if (trigger == null) return;

        _treeDragActive = true;
        try
        {
            var data = await BuildTreeDragDataAsync(node);
            await DragDrop.DoDragDropAsync(trigger, data, DragDropEffects.Copy | DragDropEffects.Link);
        }
        catch { /* drag aborted */ }
        finally { _treeDragActive = false; }
    }

    private void OnFileTreePointerReleased(object? sender, PointerReleasedEventArgs e)
        => _treeDragCandidate = null;

    private async Task<DataTransfer> BuildTreeDragDataAsync(FileTreeNode node)
    {
        var data = new DataTransfer();
        var entry = new DataTransferItem();
        var path = node.FullPath;

        // Own format + plain text keep the drop working even when the storage
        // lookup below fails (UNC paths, permission issues, …)
        entry.Set(TerminalControl.ExplorerPathFormat, path);
        entry.SetText(path.Contains(' ') ? "\"" + path + "\"" : path);

        try
        {
            IStorageItem? item = node.IsDirectory
                ? await StorageProvider.TryGetFolderFromPathAsync(path)
                : await StorageProvider.TryGetFileFromPathAsync(path);
            if (item != null)
                entry.SetFile(item);
        }
        catch { /* keep the text-only payload */ }

        // Added last so the item carries every format it is going to have
        data.Add(entry);
        return data;
    }

    private static FileTreeNode? FindTreeNode(object? source)
    {
        var visual = source as Visual;
        while (visual != null)
        {
            if (visual is Control { DataContext: FileTreeNode node })
                return node;
            visual = visual.GetVisualParent();
        }
        return null;
    }

    private FileTreeNode? GetSelectedTreeNode()
    {
        return FileTree.SelectedItem as FileTreeNode;
    }

    private void OnTreeItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        var node = GetSelectedTreeNode();
        if (node == null || node.IsDirectory) return;
        OpenFileDefault(node.FullPath);
        e.Handled = true;
    }

    private void OnTreeOpenDefault(object? sender, RoutedEventArgs e)
    {
        var node = GetSelectedTreeNode();
        if (node == null) return;

        if (node.IsDirectory)
            OpenFolderInExplorer(node.FullPath);
        else
            OpenFileDefault(node.FullPath);
    }

    private void OnTreeOpenWith(object? sender, RoutedEventArgs e)
    {
        var node = GetSelectedTreeNode();
        if (node == null || node.IsDirectory) return;
        OpenFileWith(node.FullPath);
    }

    private void OnTreeOpenInEditor(object? sender, RoutedEventArgs e)
    {
        var node = GetSelectedTreeNode();
        if (node == null || node.IsDirectory) return;
        OpenFileEditorWindow(node.FullPath);
    }

    private void OnTreeShowInExplorer(object? sender, RoutedEventArgs e)
    {
        var node = GetSelectedTreeNode();
        if (node == null) return;

        // Open parent folder with the item selected
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{node.FullPath}\"",
                UseShellExecute = true
            });
        }
        catch { }
    }

    private async void OnTreeCopyPath(object? sender, RoutedEventArgs e)
    {
        var node = GetSelectedTreeNode();
        if (node != null) await CopyToClipboard(node.FullPath);
    }

    private async void OnTreeCopyFilename(object? sender, RoutedEventArgs e)
    {
        var node = GetSelectedTreeNode();
        if (node != null) await CopyToClipboard(System.IO.Path.GetFileName(node.FullPath));
    }

    private async Task CopyToClipboard(string text)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
                await clipboard.SetTextAsync(text);
        }
        catch { }
    }

    private static void OpenFileDefault(string filePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = filePath,
                UseShellExecute = true
            });
        }
        catch { }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenAsInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string FileName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ClassName;
        public int Flags;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHOpenWithDialog(IntPtr hwndParent, ref OpenAsInfo info);

    /// <summary>OAIF_EXEC: open the file once an app has been picked.</summary>
    private const int OaifExec = 0x04;

    /// <summary>
    /// OAIF_HIDE_REGISTRATION: drop the "always use this app" option. Windows 11 answers the
    /// registration flags with a "change the default app in Settings" message box instead of
    /// the picker, so asking for them loses the dialog entirely.
    /// </summary>
    private const int OaifHideRegistration = 0x20;

    /// <summary>
    /// Shows Windows' "Open with" picker. Neither obvious route works from here: rundll32
    /// shell32,OpenAs_RunDLL(W) starts and exits without drawing anything, and ShellExecute's
    /// "openas" verb throws SE_ERR_NOASSOC on an extension with no association at all - which is
    /// the case the picker exists for. SHOpenWithDialog is the API behind both and copes with an
    /// unknown extension. It blocks until dismissed and wants an STA thread, so it gets one of
    /// its own instead of freezing the UI.
    /// </summary>
    private static void OpenFileWith(string filePath)
    {
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                var info = new OpenAsInfo
                {
                    FileName = filePath,
                    ClassName = null,
                    Flags = OaifExec | OaifHideRegistration,
                };
                // The result is not worth branching on: a cancelled picker and a picked app
                // both come back S_OK here, and there is nothing to undo either way.
                SHOpenWithDialog(IntPtr.Zero, ref info);
            }
            catch { }
        });
        // The guard is for the analyser: the project targets plain net8.0, so it cannot tell
        // that everything here is Windows-only already.
        if (OperatingSystem.IsWindows())
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
    }

    private static void OpenFolderInExplorer(string folderPath)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{folderPath}\"",
                UseShellExecute = true
            });
        }
        catch { }
    }

    // ── Settings Panel (inline) ──

    private void InitializeSettingsPanel()
    {
        _settingsInitialized = true;

        CmbSettingsLanguage.ItemsSource = LanguageList;
        CmbSettingsLanguage.SelectedItem = LanguageList.Contains(_settings.Language)
            ? _settings.Language
            : LanguageList[0];

        var availableFonts = new List<string>();
        foreach (var name in FontList)
        {
            try
            {
                var tf = new Typeface(name);
                if (tf.GlyphTypeface != null)
                    availableFonts.Add(name);
            }
            catch { }
        }
        if (availableFonts.Count == 0)
            availableFonts.AddRange(FontList);

        CmbSettingsFontFamily.ItemsSource = availableFonts;
        CmbSettingsFontFamily.SelectedItem = availableFonts.Contains(_settings.FontFamily)
            ? _settings.FontFamily
            : availableFonts[0];

        NumSettingsFontSize.Value = (decimal)_settings.FontSize;

        TxtInitialPrompt.Text = _settings.InitialPrompt;
        TxtInitialPrompt.LostFocus += (_, _) =>
        {
            _settings.InitialPrompt = TxtInitialPrompt.Text?.Trim() ?? "-c";
            _settings.Save();
        };

        ChkShowWelcomePage.IsChecked = _settings.ShowWelcomePage;
        ChkEnableCharts.IsChecked = _settings.EnableChartRendering;
        ChkDarkMode.IsChecked = _settings.IsDark;
        _suppressSettingsChanged = true;
        ChkNotifyOnComplete.IsChecked = _settings.NotifyOnComplete;
        ChkNotifySound.IsChecked = _settings.NotifySound;
        ChkEnableCheckpoints.IsChecked = _settings.EnableCheckpoints;
        ChkEnableLiveStatus.IsChecked = _settings.EnableLiveStatus;
        ChkEnableErrorBanner.IsChecked = _settings.EnableErrorBanner;
        ChkGitAutoFetch.IsChecked = _settings.GitAutoFetch;
        FillCommitLanguageCombo();
        FillPlanTierCombo();
        _suppressSettingsChanged = false;
    }

    private bool _suppressWelcomeCheckChanged;

    private void OnShowWelcomePageChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressWelcomeCheckChanged) return;
        _settings.ShowWelcomePage = ChkShowWelcomePage.IsChecked == true;
        _settings.Save();
    }

    private void OnEnableChartsChanged(object? sender, RoutedEventArgs e)
    {
        _settings.EnableChartRendering = ChkEnableCharts.IsChecked == true;
        _settings.Save();
        var enabled = _settings.EnableChartRendering && _cli.Features.DiagramViewer;
        foreach (var child in _children)
            child.Terminal.EnableChartRendering = enabled;
    }

    private void OnDarkModeChanged(object? sender, RoutedEventArgs e)
    {
        _isDark = ChkDarkMode.IsChecked == true;
        _settings.IsDark = _isDark;
        _settings.Save();
        if (Application.Current is App app)
            app.RequestedThemeVariant = _isDark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;

        // Update application-wide color resources
        UpdateThemeResources();

        var containerBg = _isDark ? Color.FromRgb(28, 28, 30) : Color.FromRgb(255, 255, 255);
        var titleBarBg = _isDark ? Color.FromRgb(44, 44, 46) : Color.FromRgb(235, 235, 240);
        var titleFg = _isDark ? Color.FromRgb(210, 210, 215) : Color.FromRgb(40, 40, 45);

        foreach (var child in _children)
        {
            child.Terminal.IsDarkTheme = _isDark;
            child.Container.Background = new SolidColorBrush(containerBg);
            child.TitleBar.Background = new SolidColorBrush(titleBarBg);
            child.TitleText.Foreground = new SolidColorBrush(titleFg);
        }
        // DocView theme is updated via terminal.ApplyThemeColors()

        // The source-control panel resolves its colours once, at construction, so the only
        // way to re-theme it is to build it again.
        CreateSourceControlPanel();
    }

    private void UpdateThemeResources()
    {
        var res = Application.Current?.Resources;
        if (res == null) return;

        if (_isDark)
        {
            res["ToolBarBg"] = new SolidColorBrush(Color.FromRgb(44, 44, 46));
            res["StatusBarBg"] = new SolidColorBrush(Color.FromRgb(28, 28, 30));
            res["SurfaceBg"] = new SolidColorBrush(Color.FromRgb(0, 0, 0));
            res["SubtleText"] = new SolidColorBrush(Color.FromRgb(152, 152, 157));
            res["DividerColor"] = new SolidColorBrush(Color.FromRgb(56, 56, 58));
            res["ActivityBarBg"] = new SolidColorBrush(Color.FromRgb(28, 28, 30));
            res["SidePanelBg"] = new SolidColorBrush(Color.FromRgb(44, 44, 46));
        }
        else
        {
            res["ToolBarBg"] = new SolidColorBrush(Color.FromRgb(240, 240, 245));
            res["StatusBarBg"] = new SolidColorBrush(Color.FromRgb(232, 232, 237));
            res["SurfaceBg"] = new SolidColorBrush(Color.FromRgb(246, 246, 248));
            res["SubtleText"] = new SolidColorBrush(Color.FromRgb(100, 100, 110));
            res["DividerColor"] = new SolidColorBrush(Color.FromRgb(200, 200, 205));
            res["ActivityBarBg"] = new SolidColorBrush(Color.FromRgb(232, 232, 237));
            res["SidePanelBg"] = new SolidColorBrush(Color.FromRgb(240, 240, 245));
        }
    }

    private void OnApplySettings(object? sender, RoutedEventArgs e)
    {
        var language = CmbSettingsLanguage.SelectedItem as string ?? "English";
        var fontFamily = CmbSettingsFontFamily.SelectedItem as string ?? "Cascadia Mono";
        var fontSize = (double)(NumSettingsFontSize.Value ?? 14);
        _settings.Language = language;
        _settings.FontFamily = fontFamily;
        _settings.FontSize = fontSize;
        _settings.Save();

        Loc.Language = language;
        ApplyLocalization();

        foreach (var child in _children)
        {
            child.Terminal.SetFont(_settings.FontFamily, _settings.FontSize);
        }
    }

    private void OnOpenClaudeFolder(object? sender, RoutedEventArgs e)
    {
        var configDir = _cli.ActiveConfigDirPath;
        if (!string.IsNullOrEmpty(configDir) && Directory.Exists(configDir))
        {
            Process.Start(new ProcessStartInfo { FileName = configDir, UseShellExecute = true });
        }
    }

    // ── Snippets Panel ──

    // A snippet may write a newline as the escape text \r, \n or \r\n, or hold a real
    // line break. All of them mean "press Enter", which the console expects as a CR.
    private static readonly Regex SnippetNewlineRegex =
        new(@"\\r\\n|\\r|\\n|\r\n|\r|\n", RegexOptions.Compiled);

    private static string NormalizeSnippetNewlines(string text)
        => SnippetNewlineRegex.Replace(text, "\r");

    private void LoadSnippetsPanel()
    {
        _snippetsInitialized = true;
        var sorted = _snippetStore.Snippets.OrderBy(s => s.Order).ToList();
        foreach (var item in sorted)
        {
            var border = CreateSnippetEntry(item);
            // Defer height adjustment until layout is ready
            if (border.Child is TextBox tb)
                Dispatcher.UIThread.Post(() => AdjustSnippetHeight(tb), DispatcherPriority.Render);
        }
    }

    private static void AdjustSnippetHeight(TextBox textBox)
    {
        var text = textBox.Text ?? "";
        int lineCount = text.Split('\n').Length;
        if (string.IsNullOrEmpty(text))
            lineCount = 0;

        // Each line ~ 18px (FontSize 13 + line spacing), padding 12 total
        double lineHeight = 18;
        double padding = 16;
        double contentHeight = Math.Max(1, lineCount) * lineHeight + padding;

        // MinHeight: fit content, but at least 1 line worth
        textBox.MinHeight = contentHeight;
    }

    private void OnAddSnippet(object? sender, RoutedEventArgs e)
    {
        var item = new SnippetItem { Order = _snippetStore.Snippets.Count };
        _snippetStore.Snippets.Add(item);
        var border = CreateSnippetEntry(item);
        _snippetStore.Save();

        // Focus the new snippet's textbox
        if (border.Child is Grid g)
        {
            var tb = g.Children.OfType<TextBox>().FirstOrDefault();
            if (tb != null)
                Dispatcher.UIThread.Post(() => tb.Focus(), DispatcherPriority.Background);
        }
    }

    private Border CreateSnippetEntry(SnippetItem item)
    {
        var snBg = _isDark ? Color.FromRgb(28, 28, 30) : Color.FromRgb(255, 255, 255);
        var snFg = _isDark ? Color.FromRgb(255, 255, 255) : Color.FromRgb(28, 28, 30);
        var snBorder = _isDark ? Color.FromRgb(58, 58, 60) : Color.FromRgb(200, 200, 205);
        var snHandleBg = _isDark ? Color.FromRgb(44, 44, 46) : Color.FromRgb(235, 235, 240);
        var snGripFg = _isDark ? Color.FromRgb(100, 100, 105) : Color.FromRgb(160, 160, 170);

        var textBox = new TextBox
        {
            Text = item.Text,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 34,
            FontSize = 13,
            Padding = new Thickness(10, 6),
            CornerRadius = new CornerRadius(0),
            BorderThickness = new Thickness(0, 1, 0, 1),
            BorderBrush = new SolidColorBrush(snBorder),
            Background = new SolidColorBrush(snBg),
            Foreground = new SolidColorBrush(snFg),
            PlaceholderText = Loc.Get("EnterSnippetText"),
            Classes = { "snippet-text" }
        };

        // Drag handle (grip area on the left)
        var dragHandle = new Border
        {
            Width = 20,
            Background = new SolidColorBrush(snHandleBg),
            BorderBrush = new SolidColorBrush(snBorder),
            BorderThickness = new Thickness(1, 1, 0, 1),
            CornerRadius = new CornerRadius(8, 0, 0, 8),
            Cursor = new Cursor(StandardCursorType.SizeNorthSouth),
            Child = new TextBlock
            {
                Text = "\u2847",  // braille dots as grip icon
                FontSize = 14,
                Foreground = new SolidColorBrush(snGripFg),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var sendBtn = new Button
        {
            Content = new PathIcon
            {
                Data = StreamGeometry.Parse("M8 5V19L19 12L8 5Z"),
                Width = 10, Height = 10
            },
            Background = new SolidColorBrush(snHandleBg),
            Foreground = new SolidColorBrush(Color.FromRgb(48, 209, 88)),
            BorderBrush = new SolidColorBrush(snBorder),
            BorderThickness = new Thickness(0, 1, 1, 1),
            CornerRadius = new CornerRadius(0, 8, 8, 0),
            Padding = new Thickness(4, 0),
            VerticalAlignment = VerticalAlignment.Stretch,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        ToolTip.SetTip(sendBtn, Loc.Get("SendToConsole"));

        sendBtn.Click += (_, _) =>
        {
            if (_activeChildIndex >= 0 && _activeChildIndex < _children.Count
                && !string.IsNullOrEmpty(textBox.Text))
            {
                var terminal = _children[_activeChildIndex].Terminal;
                var snippetText = NormalizeSnippetNewlines(textBox.Text);
                if (terminal.IsDocumentView && terminal.IsExpanded)
                    terminal.AppendToExpandedInput(snippetText);
                else if (terminal.IsDocumentView)
                    terminal.SetInputText(snippetText);
                else if (terminal.IsExpanded)
                    terminal.AppendToExpandedInput(snippetText);
                else
                    terminal.SendText(snippetText);
                BringToFront(_activeChildIndex);
                terminal.FocusTerminal();
            }
        };

        var grid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto")
        };
        Grid.SetColumn(dragHandle, 0);
        Grid.SetColumn(textBox, 1);
        Grid.SetColumn(sendBtn, 2);
        grid.Children.Add(dragHandle);
        grid.Children.Add(textBox);
        grid.Children.Add(sendBtn);

        var border = new Border
        {
            Child = grid,
            CornerRadius = new CornerRadius(8),
            Tag = item
        };

        // Lost focus: save and adjust height
        textBox.LostFocus += (_, _) =>
        {
            item.Text = textBox.Text ?? "";
            _snippetStore.Save();
            AdjustSnippetHeight(textBox);
        };

        // Right-click context menu
        textBox.ContextMenu = new ContextMenu
        {
            Items =
            {
                CreateSnippetMenuItem(Loc.Get("Delete"), "M6 19C6 20.1 6.9 21 8 21H16C17.1 21 18 20.1 18 19V7H6V19ZM19 4H15.5L14.5 3H9.5L8.5 4H5V6H19V4Z", () =>
                {
                    _snippetStore.Snippets.Remove(item);
                    SnippetsList.Children.Remove(border);
                    _snippetStore.Save();
                })
            }
        };

        // Drag-and-drop via drag handle
        dragHandle.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(dragHandle).Properties.IsLeftButtonPressed)
            {
                _snippetDragging = true;
                _snippetDragItem = border;
                _snippetDragIndex = SnippetsList.Children.IndexOf(border);
                _snippetDragStartPos = e.GetPosition(SnippetsList);
                border.Opacity = 0.6;
                e.Pointer.Capture(dragHandle);
                e.Handled = true;
            }
        };

        dragHandle.PointerMoved += (_, e) =>
        {
            if (!_snippetDragging || _snippetDragItem != border) return;

            var pos = e.GetPosition(SnippetsList);

            // Find which item we're hovering over by Y position
            int targetIdx = -1;
            double accY = 0;
            for (int i = 0; i < SnippetsList.Children.Count; i++)
            {
                if (SnippetsList.Children[i] is not Border b) continue;
                double itemH = b.Bounds.Height + 3; // 3 = StackPanel Spacing
                if (pos.Y < accY + itemH / 2)
                {
                    targetIdx = i;
                    break;
                }
                accY += itemH;
            }
            if (targetIdx < 0)
                targetIdx = SnippetsList.Children.Count - 1;

            int currentIdx = SnippetsList.Children.IndexOf(border);
            if (targetIdx != currentIdx)
            {
                SnippetsList.Children.RemoveAt(currentIdx);
                SnippetsList.Children.Insert(targetIdx, border);
                // Re-capture pointer after visual tree re-insertion (removal releases capture)
                e.Pointer.Capture(dragHandle);
                SyncSnippetOrder();
                // Restore foreground on all TextBoxes after visual tree re-insertion
                foreach (var child in SnippetsList.Children)
                {
                    if (child is Border cb && cb.Child is Grid cg)
                    {
                        var ctb = cg.Children.OfType<TextBox>().FirstOrDefault();
                        if (ctb != null)
                            ctb.Foreground = new SolidColorBrush(_isDark ? Color.FromRgb(255, 255, 255) : Color.FromRgb(28, 28, 30));
                    }
                }
            }

            e.Handled = true;
        };

        dragHandle.PointerReleased += (_, e) =>
        {
            if (_snippetDragging && _snippetDragItem == border)
            {
                _snippetDragging = false;
                _snippetDragItem = null;
                border.Opacity = 1.0;
                // Restore foreground on all snippet TextBoxes after drag
                foreach (var child in SnippetsList.Children)
                {
                    if (child is Border b && b.Child is Grid g)
                    {
                        var tb = g.Children.OfType<TextBox>().FirstOrDefault();
                        if (tb != null)
                            tb.Foreground = new SolidColorBrush(_isDark ? Color.FromRgb(255, 255, 255) : Color.FromRgb(28, 28, 30));
                    }
                }
                e.Pointer.Capture(null);
                e.Handled = true;
            }
        };

        SnippetsList.Children.Add(border);
        return border;
    }

    private static MenuItem CreateSnippetMenuItem(string header, string iconData, Action action)
    {
        var menuItem = new MenuItem
        {
            Header = header,
            Icon = new PathIcon { Data = StreamGeometry.Parse(iconData), Width = 14, Height = 14 }
        };
        menuItem.Click += (_, _) => action();
        return menuItem;
    }

    private void SyncSnippetOrder()
    {
        var reordered = new List<SnippetItem>();
        foreach (var child in SnippetsList.Children)
        {
            if (child is Border b && b.Tag is SnippetItem si)
            {
                si.Order = reordered.Count;
                reordered.Add(si);
            }
        }
        _snippetStore.Snippets = reordered;
        _snippetStore.Save();
    }

    // ── Project Folder ──

    private async void LoadRecentProjectFolders()
    {
        var recentFolders = await SessionService.GetRecentProjectFoldersAsync();

        // Isolated checkouts get a transcript folder of their own, but they are scratch space
        // belonging to a window, not projects anyone chose to open.
        recentFolders.RemoveAll(WorktreeService.IsWorktreePath);

        var items = new List<string>();

        if (!string.IsNullOrEmpty(_projectFolder) && Directory.Exists(_projectFolder))
        {
            items.Add(_projectFolder);
            recentFolders.RemoveAll(f => f.Equals(_projectFolder, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var folder in recentFolders)
        {
            if (items.Count >= 10) break;
            items.Add(folder);
        }

        _suppressFolderSelectionChanged = true;
        CmbProjectFolder.ItemsSource = items;
        if (items.Count > 0 && !string.IsNullOrEmpty(_projectFolder))
        {
            CmbProjectFolder.SelectedIndex = 0;
        }
        _suppressFolderSelectionChanged = false;
    }

    private void OnProjectFolderSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressFolderSelectionChanged) return;

        if (CmbProjectFolder.SelectedItem is string selected)
        {
            if (Directory.Exists(selected))
            {
                SetProjectFolder(selected);
            }
        }
    }

    private void OnProjectFolderKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var text = CmbProjectFolder.Text?.Trim();
            if (!string.IsNullOrEmpty(text) && Directory.Exists(text))
            {
                SetProjectFolder(text);
                LoadRecentProjectFolders();
            }
            e.Handled = true;
        }
    }

    private async void OnBrowseFolder(object? sender, RoutedEventArgs e)
    {
        var startLocation = !string.IsNullOrEmpty(_projectFolder) && Directory.Exists(_projectFolder)
            ? await StorageProvider.TryGetFolderFromPathAsync(_projectFolder)
            : null;

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Project Folder",
            AllowMultiple = false,
            SuggestedStartLocation = startLocation
        });

        if (folders.Count > 0)
        {
            SetProjectFolder(folders[0].Path.LocalPath);
            LoadRecentProjectFolders();
        }
    }

    private void SetProjectFolder(string path)
    {
        _projectFolder = path;
        RefreshGitInfo();
        RefreshSessionList();
        RefreshFileTree();
    }

    private void OnRepoNameDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_gitRepoUrl))
        {
            Process.Start(new ProcessStartInfo { FileName = _gitRepoUrl, UseShellExecute = true });
        }
        e.Handled = true;
    }

    /// <summary>
    /// Builds the source-control panel and hands it to the host in the sidebar. Called once at
    /// startup and again whenever the theme flips, since the panel resolves its colours at
    /// construction.
    /// </summary>
    private void CreateSourceControlPanel()
    {
        var typeface = new Typeface(_settings.FontFamily + ", Consolas, Courier New");
        var host = new Controls.SourceControlHost(
            SendToActiveTerminal,
            ShowMessageDialog,
            ShowConfirmDialog,
            (title, watermark, initial) => ShowTextInputDialog(title, watermark, initial),
            OpenCommitGraphWindow);

        var panel = new Controls.SourceControlPanel(_isDark, typeface, _settings, _cli, host);
        // A write inside the panel moves the branch or the working tree, which the status bar
        // and the file tree also read. Route it through the one refresh the window already has.
        panel.GitChanged += (_, _) => RefreshGitInfo();

        _sourceControl = panel;
        SourceControlHost.Content = panel;
        panel.SetRepository(_projectFolder);
        if (_activeSidePanel == SidebarPanel.SourceControl) panel.OnPanelShown();
    }

    /// <summary>Opens the source-control panel, where the commit graph now lives.</summary>
    private void OnBranchNameDoubleTapped(object? sender, TappedEventArgs e)
    {
        e.Handled = true;

        if (_activeSidePanel != SidebarPanel.SourceControl)
            ToggleSidePanel(SidebarPanel.SourceControl);
    }

    private void RefreshGitInfo()
    {
        // Set before the early return: a folder that is not a repository still has to reach
        // the panel, or it keeps showing the previous project's branch.
        _sourceControl?.SetRepository(_projectFolder);

        StatusRepoName.Text = "";
        StatusBranchName.Text = "";
        BtnBranchSwitch.IsVisible = false;
        _gitRepoUrl = null;

        if (string.IsNullOrEmpty(_projectFolder) || !Directory.Exists(_projectFolder))
            return;

        try
        {
            // Get remote origin URL -> extract repo name
            // Trimmed: GitCli.Run hands back git's stdout verbatim, trailing newline and
            // all. Left on, that newline makes the status-bar label two lines tall - the
            // name then rides above everything else on the row - and it rides along into
            // the URL the repo name opens.
            var remoteUrl = GitCli.Run(_projectFolder, "remote", "get-url", "origin").Trim();

            if (!string.IsNullOrEmpty(remoteUrl))
            {
                // Build browser URL from remote
                // https://github.com/owner/repo.git or git@github.com:owner/repo.git
                var cleanUrl = remoteUrl;
                if (cleanUrl.EndsWith(".git"))
                    cleanUrl = cleanUrl[..^4];
                if (cleanUrl.StartsWith("git@"))
                {
                    // git@github.com:owner/repo -> https://github.com/owner/repo
                    cleanUrl = cleanUrl.Replace("git@", "https://").Replace(":", "/");
                }
                _gitRepoUrl = cleanUrl;

                // Extract "owner/repo" for display
                var repoName = cleanUrl;
                var idx = repoName.LastIndexOf('/');
                if (idx >= 0)
                {
                    var ownerStart = repoName.LastIndexOf('/', idx - 1);
                    repoName = ownerStart >= 0 ? repoName[(ownerStart + 1)..] : repoName[(idx + 1)..];
                }
                StatusRepoName.Text = repoName;
            }

            // Get current branch name
            var branch = GitCli.Run(_projectFolder, "rev-parse", "--abbrev-ref", "HEAD").Trim();

            if (!string.IsNullOrEmpty(branch))
            {
                StatusBranchName.Text = branch;
                BtnBranchSwitch.IsVisible = true;
            }
        }
        catch { }

        UpdateIsolateToggle();

        // Keep the source-control panel on the project the status bar just switched to. It
        // returns immediately while another sidebar panel is up.
        _ = _sourceControl?.RefreshAsync();
        if (SlashPanel.IsVisible) RefreshSlashPanel();
    }

    private async void RefreshSessionList()
    {
        // Non-Claude CLIs have no readable session index; the button acts as "continue" instead.
        if (!_cli.Features.SessionList)
        {
            CmbSessions.ItemsSource = null;
            return;
        }

        // The scan is slow enough that the active project can change while it runs, so the
        // sessions that come back are matched against the folder they were actually read from.
        var folder = _projectFolder;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            CmbSessions.ItemsSource = null;
            BtnResumeSession.IsEnabled = false;
            return;
        }

        var sessions = await SessionService.GetSessionsForProjectAsync(folder);
        CmbSessions.ItemsSource = sessions;
        SyncSessionSelection();

        // Same snapshot, same strings: whatever the box lists for a running session is what its
        // tab, title bar and Windows row say.
        ApplySessionTitles(sessions, folder);
    }

    /// <summary>
    /// Point the Session box at the session the active window is running, so a resumed session
    /// stays selected there instead of the box coming up empty.
    /// </summary>
    private void SyncSessionSelection()
    {
        // The button means "continue" for CLIs without a session index; leave its state alone.
        if (!_cli.Features.SessionList) return;

        string? sessionId = _activeChildIndex >= 0 && _activeChildIndex < _children.Count
            ? _children[_activeChildIndex].SessionId
            : null;

        int index = -1;
        if (!string.IsNullOrEmpty(sessionId) && CmbSessions.ItemsSource is List<SessionInfo> sessions)
            index = sessions.FindIndex(s => s.Id.Equals(sessionId, StringComparison.OrdinalIgnoreCase));

        CmbSessions.SelectedIndex = index;
        BtnResumeSession.IsEnabled = CmbSessions.SelectedItem is SessionInfo;
    }

    private void OnSessionSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_cli.Features.SessionList) return;
        BtnResumeSession.IsEnabled = CmbSessions.SelectedItem is SessionInfo;
    }

    // ── Session picker ──

    private const int MaxSessionRows = 60;

    /// <summary>
    /// A searchable view of the same sessions the combo holds, with a way to throw one away.
    /// The combo is fine for picking among a handful and useless at the dozens a long-running
    /// project accumulates, and it has nowhere to put a per-row action.
    /// </summary>
    private void OnManageSessions(object? sender, RoutedEventArgs e)
    {
        var sessions = CmbSessions.ItemsSource as List<SessionInfo> ?? new List<SessionInfo>();

        var search = new TextBox
        {
            PlaceholderText = Loc.Get("SearchSessions"),
            FontSize = 12,
            Padding = new Thickness(8, 5),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 0, 0, 6),
        };
        var rows = new StackPanel { Spacing = 1 };
        var hint = new TextBlock
        {
            FontSize = 10,
            Opacity = 0.55,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 6, 2, 0),
        };

        var flyout = new Flyout
        {
            // The button sits at the right of the toolbar, so the panel grows back across the
            // window rather than off the side of it.
            Placement = PlacementMode.BottomEdgeAlignedRight,
            Content = new DockPanel
            {
                Width = 460,
                Children =
                {
                    Docked(search, Avalonia.Controls.Dock.Top),
                    Docked(hint, Avalonia.Controls.Dock.Bottom),
                    new ScrollViewer
                    {
                        MaxHeight = 360,
                        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                        Content = rows,
                    },
                },
            },
        };

        void Render()
        {
            var query = search.Text?.Trim() ?? "";
            var matches = sessions.Where(s => MatchesSession(s, query)).ToList();

            rows.Children.Clear();
            foreach (var session in matches.Take(MaxSessionRows))
                rows.Children.Add(BuildSessionRow(session, flyout, sessions, Render));

            hint.Text = matches.Count == 0
                ? Loc.Get("NoMatches")
                : matches.Count > MaxSessionRows
                    ? string.Format(Loc.Get("ExtensionsMoreFmt"), matches.Count - MaxSessionRows)
                    : string.Format(Loc.Get("SessionCountFmt"), matches.Count);
        }

        search.TextChanged += (_, _) => Render();
        Render();

        flyout.ShowAt(BtnManageSessions);
        Dispatcher.UIThread.Post(() => search.Focus());
    }

    /// <summary>Sets a child's dock side inline, so a DockPanel can be built as one initializer.</summary>
    private static Control Docked(Control control, Avalonia.Controls.Dock side)
    {
        DockPanel.SetDock(control, side);
        return control;
    }

    private static bool MatchesSession(SessionInfo session, string query)
    {
        if (query.Length == 0) return true;
        return Has(session.DisplayTitle) || Has(session.Summary) || Has(session.Id)
            || Has(session.Timestamp?.ToString("yyyy/MM/dd HH:mm"));

        bool Has(string? text) =>
            text != null && text.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private Control BuildSessionRow(
        SessionInfo session, Flyout flyout, List<SessionInfo> all, Action render)
    {
        var stamp = new TextBlock
        {
            Text = session.Timestamp?.ToString("yyyy/MM/dd HH:mm") ?? "",
            FontSize = 10,
            Opacity = 0.6,
            Width = 104,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var title = new TextBlock
        {
            Text = session.DisplayTitle ?? session.Summary ?? session.Id,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var remove = new Button
        {
            Content = new TextBlock { Text = "×", FontSize = 13 },
            Padding = new Thickness(6, 0),
            MinHeight = 0,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Opacity = 0.55,
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(remove, Loc.Get("DeleteSession"));
        remove.Click += async (_, args) =>
        {
            args.Handled = true;
            await DeleteSessionAsync(session, all, render);
        };

        var grid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto,Auto") };
        Grid.SetColumn(stamp, 0);
        Grid.SetColumn(title, 1);
        Grid.SetColumn(remove, 3);
        grid.Children.Add(stamp);
        grid.Children.Add(title);
        grid.Children.Add(remove);

        // What a resume would start from. The number that decides whether to pick the session up
        // again or hand it off, shown before the choice is made rather than after.
        if (session.LastContextTokens is long context && context > 0)
        {
            bool heavy = context >= _settings.HandoffBannerTokens;
            var badgeText = new TextBlock
            {
                Text = FormatTokens(context),
                FontSize = 10,
                Opacity = heavy ? 1.0 : 0.7,
            };
            // Only the warning badge sets a colour: a null Foreground would replace the inherited
            // theme brush rather than fall back to it, leaving the plain badge blank.
            if (heavy) badgeText.Foreground = Brushes.Black;
            var badge = new Border
            {
                Child = badgeText,
                Padding = new Thickness(5, 1),
                Margin = new Thickness(6, 0, 4, 0),
                CornerRadius = new CornerRadius(3),
                VerticalAlignment = VerticalAlignment.Center,
                Background = heavy
                    ? new SolidColorBrush(Color.FromRgb(255, 214, 10))
                    : new SolidColorBrush(_isDark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(25, 0, 0, 0)),
            };
            double perTurn = CostAnalytics.EstimateNextTurnCostUsd(session.LastModel ?? "", context);
            ToolTip.SetTip(badge, string.Format(Loc.Get("SessionContextTipFmt"), FormatTokens(context), FormatUsd(perTurn)));
            Grid.SetColumn(badge, 2);
            grid.Children.Add(badge);
        }

        var row = new Border
        {
            Child = grid,
            Padding = new Thickness(6, 5),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(row, session.Id);

        var hover = new SolidColorBrush(_isDark
            ? Color.FromArgb(30, 255, 255, 255)
            : Color.FromArgb(20, 0, 0, 0));
        row.PointerEntered += (_, _) => row.Background = hover;
        row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
        row.PointerPressed += (_, args) =>
        {
            if (!args.GetCurrentPoint(row).Properties.IsLeftButtonPressed) return;
            CmbSessions.SelectedItem = session;
            flyout.Hide();
        };

        var fresh = new MenuItem { Header = Loc.Get("StartFreshFromBrief") };
        fresh.Click += (_, _) =>
        {
            flyout.Hide();
            _ = StartHandoffFromSessionAsync(session);
        };
        row.ContextMenu = new ContextMenu { Items = { fresh } };

        return row;
    }

    /// <summary>
    /// Hand-off from a listed session instead of the active window: the brief comes from that
    /// session's transcript, the new window opens with it in the input box, and the old session
    /// is left on disk untouched.
    /// </summary>
    private async Task StartHandoffFromSessionAsync(SessionInfo session)
    {
        var folder = _projectFolder ?? "";
        string? path = string.IsNullOrEmpty(folder) ? null : SessionMessageReader.FindSessionFile(folder, session.Id);
        if (path == null)
        {
            await ShowConfirmDialog(Loc.Get("HandoffDialogTitle"), Loc.Get("HandoffNoSession"));
            return;
        }
        await StartHandoffFromTranscriptAsync(path);
    }

    private async Task DeleteSessionAsync(SessionInfo session, List<SessionInfo> all, Action render)
    {
        var label = session.DisplayTitle ?? session.Summary ?? session.Id;
        if (!await ShowConfirmDialog(
                Loc.Get("DeleteSession"),
                string.Format(Loc.Get("DeleteSessionConfirmFmt"), label)))
            return;

        var error = SessionService.Delete(_projectFolder ?? "", session.Id);
        if (error != null)
        {
            await ShowConfirmDialog(Loc.Get("DeleteSession"),
                string.Format(Loc.Get("DeleteSessionFailedFmt"), error));
            return;
        }

        // The combo is bound to this same list, so it has to be reset to notice the removal.
        all.Remove(session);
        CmbSessions.ItemsSource = null;
        CmbSessions.ItemsSource = all;
        SyncSessionSelection();
        render();
    }

    private void OnResumeSession(object? sender, RoutedEventArgs e)
    {
        // CLIs without a readable session index fall back to "continue most recent"
        if (!_cli.Features.SessionList)
        {
            CreateNewChild(_cli.BuildContinueCommand(ActiveLaunchProfile()), _cli.Active.Name);
            return;
        }

        if (CmbSessions.SelectedItem is SessionInfo session)
        {
            // A session runs in one place at a time. Resuming one already held elsewhere would
            // start a CLI that exits on the spot, leaving the new window at "[Process exited]".
            if (RunningSessionService.IsLive(session.Id, _projectFolder))
            {
                ShowMessageDialog(Loc.Get("SessionBusyTitle"),
                    Loc.Get(RunningSessionService.IsHeldByAgent(session.Id, _projectFolder)
                        ? "SessionBusyAgent"
                        : "SessionBusyElsewhere"));
                return;
            }

            string cmd = _cli.BuildResumeCommand(session.Id, ActiveLaunchProfile());
            var displayTitle = session.DisplayTitle ?? session.Summary;
            string tabLabel = !string.IsNullOrEmpty(displayTitle)
                ? (displayTitle.Length > 30 ? displayTitle[..30] + "..." : displayTitle)
                : $"Session: {session.Id[..Math.Min(8, session.Id.Length)]}";
            CreateNewChild(cmd, tabLabel, displayTitle, session.Id);

            // Load cached diagrams for this project
            if (_activeChildIndex >= 0 && _activeChildIndex < _children.Count)
                _children[_activeChildIndex].Terminal.LoadCachedDiagrams();
        }
    }

    private void OnNewClaude(object? sender, RoutedEventArgs e)
    {
        LaunchClaudeWithInitialPrompt();
    }

    private void OnCloseTab(object? sender, RoutedEventArgs e) => CloseActiveWindow();

    /// <summary>Closes whichever window holds the front - a session or an open file.</summary>
    private void CloseActiveWindow()
    {
        if (_activeLayoutItem != null)
        {
            CloseLayoutItem(_activeLayoutItem);
            return;
        }
        if (_activeChildIndex >= 0 && _activeChildIndex < _children.Count)
            CloseChild(_children[_activeChildIndex]);
    }

    /// <summary>Ctrl+Tab order runs across every MDI window, sessions and files alike.</summary>
    private void CycleWindows(int dir)
    {
        var items = AllLayoutItems();
        if (items.Count < 2) return;

        int current = _activeLayoutItem == null ? _activeChildIndex : items.IndexOf(_activeLayoutItem);
        if (current < 0) current = 0;

        ActivateLayoutItem(items[(current + dir + items.Count) % items.Count]);
    }

    // ── Global Keyboard Shortcuts ──

    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        // Ctrl+N: New session
        if (e.Key == Key.N && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            LaunchClaudeWithInitialPrompt();
            e.Handled = true;
            return;
        }
        // Ctrl+W: Close active tab
        if (e.Key == Key.W && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            CloseActiveWindow();
            e.Handled = true;
            return;
        }
        // Ctrl+Tab / Ctrl+Shift+Tab: Switch tabs
        if (e.Key == Key.Tab && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            CycleWindows(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
            e.Handled = true;
            return;
        }
        // Ctrl+Shift+P: Command palette
        if (e.Key == Key.P && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            ShowCommandPalette();
            e.Handled = true;
            return;
        }
        // Ctrl+Shift+E: Toggle explorer
        if (e.Key == Key.E && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            ToggleSidePanel(SidebarPanel.Explorer);
            e.Handled = true;
            return;
        }
        // Ctrl+Shift+G: Toggle source control
        if (e.Key == Key.G && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            ToggleSidePanel(SidebarPanel.SourceControl);
            e.Handled = true;
            return;
        }
    }

    /// <summary>
    /// Shortcuts that have to win over whatever holds focus, handled while the key event is
    /// still tunnelling down to it. Keep this list short: anything added here is taken away
    /// from the terminal and from every text box in the window.
    /// </summary>
    private void OnAppShortcutTunnel(object? sender, KeyEventArgs e)
    {
        // F1: keyboard shortcut cheat sheet
        if (e.Key == Key.F1)
        {
            ShowShortcutSheet();
            e.Handled = true;
            return;
        }
        // Ctrl+/: slash command palette
        if ((e.Key == Key.OemQuestion || e.Key == Key.Divide)
            && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            ToggleSlashPanel();
            e.Handled = true;
        }
    }

    // ── Status Bar Updates ──


    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool FlashWindow(IntPtr hWnd, bool invert);

    private void FlashTaskbar()
    {
        try
        {
            if (TryGetPlatformHandle() is { } handle)
                FlashWindow(handle.Handle, true);
        }
        catch { }
    }

    // ── Live Status: mode, activity, context, error diagnosis ──

    /// <summary>
    /// Reads the active terminal's screen and mirrors what the CLI is doing into the status
    /// bar: which mode it is in, what it is working on, and how much context is left.
    /// </summary>
    private void RefreshLiveStatus()
    {
        if (_activeChildIndex < 0 || _activeChildIndex >= _children.Count)
        {
            ClearLiveStatus();
            if (_bannerKey != null) HideBanner();
            _sawWorking = false;
            _idlePolls = 0;
            return;
        }

        string screen;
        try { screen = _children[_activeChildIndex].Terminal.GetScreenText(); }
        catch { return; }

        _insight = TerminalInsight.Analyze(screen);

        // The blink is a signal, not a readout, so it survives the status-bar toggles.
        NoteRunState(_insight);

        if (!_settings.EnableLiveStatus && !_settings.EnableErrorBanner)
        {
            ClearLiveStatus();
            if (_bannerKey != null) HideBanner();
            return;
        }

        if (_settings.EnableLiveStatus)
            ApplyLiveStatus(_insight);
        else
            ClearLiveStatus();

        RefreshSessionReadout(_insight);
        UpdateAdviceBanner(_insight);
    }

    /// <summary>
    /// Runs the progress line under the input row of every child that is mid-turn, so a window
    /// working in the background still shows it. The active child reuses the snapshot
    /// <see cref="RefreshLiveStatus"/> just took; the rest only need the spinner, which is
    /// always on screen, so they read the screen without its scrollback.
    /// </summary>
    private void RefreshGenerationBars()
    {
        for (int i = 0; i < _children.Count; i++)
        {
            var child = _children[i];
            var terminal = child.Terminal;
            try
            {
                bool working = i == _activeChildIndex
                    ? _insight.IsWorking
                    : TerminalInsight.IsWorking(terminal.GetScreenText(0));
                terminal.IsGenerating = working;
                PaintChildDots(child);
                NoteChildTurnEnd(child, working);
            }
            catch
            {
                // The pty read thread writes the buffer this walks - same race the active
                // child's read already lives with. Skip this child until the next poll.
            }
        }
    }

    /// <summary>Apple Green: the CLI is up and waiting at the prompt.</summary>
    private static readonly SolidColorBrush DotIdleBrush = new(Color.FromRgb(48, 209, 88));

    /// <summary>Apple Orange: the CLI is mid-turn.</summary>
    private static readonly SolidColorBrush DotBusyBrush = new(Color.FromRgb(255, 159, 10));

    /// <summary>Apple systemGray: the process has exited.</summary>
    private static readonly SolidColorBrush DotExitedBrush = new(Color.FromRgb(142, 142, 147));

    /// <summary>
    /// Colours a window's title-bar and strip dots from its run state. The strip dot is the
    /// only thing a background window shows of itself, so it has to separate "mid-turn" from
    /// "waiting at the prompt" - green alone could not say which. Called from the 700 ms poll,
    /// hence the shared brushes and the reference check: repainting is the rare case.
    /// </summary>
    private static void PaintChildDots(MdiChildInfo child)
    {
        var brush = !child.Terminal.IsProcessRunning ? DotExitedBrush
            : child.Terminal.IsGenerating ? DotBusyBrush
            : DotIdleBrush;

        if (!ReferenceEquals(child.StatusDot.Fill, brush)) child.StatusDot.Fill = brush;
        if (!ReferenceEquals(child.StripDot.Fill, brush)) child.StripDot.Fill = brush;
    }

    /// <summary>How many polls the idle state must hold before the turn counts as over.</summary>
    private const int TurnEndIdlePolls = 2;

    /// <summary>
    /// Watches the working → idle edge and blinks the active window's frame when the CLI hands
    /// the prompt back. The idle state has to hold for two polls: mid-turn the spinner can be
    /// absent from a single frame, and blinking on that would be noise.
    /// </summary>
    private void NoteRunState(TerminalSnapshot snap)
    {
        if (snap.IsWorking)
        {
            _sawWorking = true;
            _idlePolls = 0;
            return;
        }

        if (!_sawWorking) return;
        if (++_idlePolls < TurnEndIdlePolls) return;

        _sawWorking = false;
        _idlePolls = 0;
        BlinkActiveFrame();
    }

    /// <summary>
    /// The same working → idle edge as <see cref="NoteRunState"/>, tracked per window rather than
    /// for the active one only, so a window that answered in the background gets its title
    /// refreshed too. The idle state has to hold for two polls for the same reason it does there:
    /// the spinner can be missing from a single frame mid-turn.
    /// </summary>
    private void NoteChildTurnEnd(MdiChildInfo entry, bool working)
    {
        if (working)
        {
            entry.SawWorking = true;
            entry.IdlePolls = 0;
            return;
        }

        if (!entry.SawWorking) return;
        if (++entry.IdlePolls < TurnEndIdlePolls) return;

        entry.SawWorking = false;
        entry.IdlePolls = 0;
        _ = SyncTitleFromSessionAsync(entry);
    }

    /// <summary>
    /// Re-reads a window's session name once its answer is in. The CLI names a session as the
    /// conversation grows and /rename replaces that name outright, but neither reaches the
    /// terminal, so the transcript is the only place the current name exists. A window the user
    /// renamed by hand keeps that name - a manual title outranks everything.
    /// </summary>
    private async Task SyncTitleFromSessionAsync(MdiChildInfo entry)
    {
        if (!_cli.Features.SessionList) return;
        if (entry.Terminal.IsManualTitle || entry.IsClosing) return;

        var folder = entry.ProjectFolder;
        var sessionId = entry.SessionId;
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(sessionId)) return;

        var info = await SessionService.GetSessionAsync(folder, sessionId);
        if (info == null || !_children.Contains(entry)) return;
        if (!ApplySessionTitle(entry, info)) return;

        // The name moved, so the Session box is now showing the old one. Re-reading it is the
        // expensive half of this - every transcript in the project - and a turn that did not
        // rename anything never gets here.
        if (PathEquals(folder, _projectFolder)) RefreshSessionList();
    }

    /// <summary>
    /// Points every window of a project at the names in the list the Session box is about to
    /// show, so the two are painted from one snapshot and cannot drift apart.
    /// </summary>
    private void ApplySessionTitles(List<SessionInfo> sessions, string folder)
    {
        foreach (var child in _children)
        {
            if (child.Terminal.IsManualTitle || child.IsClosing) continue;
            if (string.IsNullOrEmpty(child.SessionId)) continue;
            if (!PathEquals(child.ProjectFolder, folder)) continue;

            var info = sessions.Find(s => s.Id.Equals(child.SessionId, StringComparison.OrdinalIgnoreCase));
            if (info != null) ApplySessionTitle(child, info);
        }
    }

    /// <summary>
    /// Writes a session's name onto the window showing it - title bar, tab strip and, through
    /// <see cref="RefreshWindowsPanel"/>, the Windows list. Takes <see cref="SessionInfo.DisplayTitle"/>
    /// verbatim because that is the string the Session box renders; anything trimmed or
    /// substituted here is a name that reads as a different session. Returns true when it changed.
    /// </summary>
    private bool ApplySessionTitle(MdiChildInfo entry, SessionInfo info)
    {
        var title = info.DisplayTitle;
        if (string.IsNullOrWhiteSpace(title)) return false;

        entry.SessionTitle = title;
        if (entry.StripText.Text == title && entry.TitleText.Text == title) return false;

        entry.TitleText.Text = title;
        entry.StripText.Text = title;
        entry.FirstInput = title;
        RefreshWindowsPanel();
        return true;
    }

    /// <summary>Two folder paths naming the same directory, separators and trailing slash aside.</summary>
    private static bool PathEquals(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        static string Norm(string p) => p.Replace('/', '\\').TrimEnd('\\');
        return Norm(a).Equals(Norm(b), StringComparison.OrdinalIgnoreCase);
    }

    private void ClearLiveStatus()
    {
        // The badge stays. With the activity-bar button gone it is the only mode switch in the
        // UI, so it has to outlive the readout toggles as well as an unreadable mode name.
        ApplyModeBadge(null);
        StatusContextPanel.IsVisible = false;
        StatusRunSeparator.IsVisible = false;
        StatusActivityText.IsVisible = false;
    }

    /// <summary>
    /// Draws the mode badge. A null mode means the screen did not say which mode the CLI is in —
    /// the badge still shows, labelled "Switch Mode", because clicking it sends Shift+Tab either
    /// way. Only a CLI that has no modes at all, or having no session, takes it off screen.
    ///
    /// The label is the CLI's own wording, copied from its status line, so the two never read as
    /// different modes. The localized name moves to the tooltip, which is free to explain.
    /// </summary>
    private void ApplyModeBadge(AiMode? mode, string modeText = "")
    {
        bool show = _cli.Features.ModeSwitchButton
                    && _activeChildIndex >= 0 && _activeChildIndex < _children.Count;
        StatusModeBadge.IsVisible = show;
        if (!show) return;

        bool known = mode is not null and not AiMode.Unknown;
        var color = ModeBadgeColor(mode, modeText);
        StatusModeText.Text = !string.IsNullOrEmpty(modeText)
            ? modeText
            : known
                ? TerminalInsight.ModeShortLabel(mode!.Value)
                : Loc.Get("ModeSwitchFallback", "Switch Mode");
        StatusModeText.Foreground = new SolidColorBrush(color);
        StatusModeBadge.BorderBrush = new SolidColorBrush(color);
        StatusModeBadge.Background = new SolidColorBrush(color, 0.14);
        ToolTip.SetTip(StatusModeBadge, known
            ? TerminalInsight.ModeLabel(mode!.Value) + Environment.NewLine + Loc.Get("ModeBadgeTooltip")
            : Loc.Get("ModeSwitchTooltip", "Switch mode (Shift+Tab)"));
    }

    /// <summary>
    /// The colour the CLI prints a mode in, taken from its own dark-theme palette so the badge
    /// and the status line read as one thing rather than two: autoAccept for "accept edits on",
    /// warning for "auto mode on", planMode for "plan mode on".
    ///
    /// Auto mode and accept edits both come through as <see cref="AiMode.AcceptEdits"/> - they
    /// let edits through the same way - but the CLI colours them apart, so the wording it printed
    /// is what separates them here. Bypass gets no colour of its own on the status line; the red
    /// it has always had stands in.
    /// </summary>
    private static Color ModeBadgeColor(AiMode? mode, string modeText) => mode switch
    {
        AiMode.AcceptEdits => modeText.Contains("auto mode", StringComparison.OrdinalIgnoreCase)
            ? Color.FromRgb(255, 193, 7)
            : Color.FromRgb(175, 135, 255),
        AiMode.Plan => Color.FromRgb(72, 150, 140),
        AiMode.BypassPermissions => Color.FromRgb(255, 69, 58),
        _ => Color.FromRgb(142, 142, 147),
    };

    private void ApplyLiveStatus(TerminalSnapshot snap)
    {
        ApplyModeBadge(snap.Mode, snap.ModeText);
        ApplyContextMeter();
        ApplyRunReadout(snap);
    }

    /// <summary>
    /// What the AI is doing right now, with the elapsed time, and the stop control beside it.
    /// Both belong to a turn in flight, so they share one visibility and vanish together when
    /// the prompt comes back.
    /// </summary>
    private void ApplyRunReadout(TerminalSnapshot snap)
    {
        bool running = snap.IsWorking;

        string text = snap.ActivityText;
        if (snap.ElapsedSeconds is int secs && secs > 0)
        {
            var elapsed = FormatElapsed(secs);
            text = text.Length > 0 ? text + "  " + elapsed : elapsed;
        }

        StatusRunSeparator.IsVisible = running;
        StatusActivityText.IsVisible = running && text.Length > 0;
        StatusActivityText.Text = text;
    }

    private static string FormatElapsed(int seconds) =>
        seconds < 60 ? seconds + "s" : (seconds / 60) + "m" + (seconds % 60).ToString("00") + "s";

    /// <summary>
    /// How much of the context window the session has filled.
    ///
    /// Read off the transcript, not the screen. The CLI prints a context figure of its own only
    /// in the last stretch before it compacts, and the one it prints there counts down to the
    /// compaction trigger rather than up through the window - so scraping it would leave the
    /// meter blank for most of a session and then bring it back on a different scale. The
    /// transcript carries the size of the conversation prefix each turn was billed for, which
    /// is the figure the CLI divides by the window for its own status line, so the number here
    /// and the number on the line above it stay the same number.
    /// </summary>
    private void ApplyContextMeter()
    {
        var session = ActiveCost.Current;
        // Guarded on the live-status toggle here rather than only at the call site: the meter is
        // now painted from the transcript read as well, which does not go through ApplyLiveStatus.
        // Shown for any window that is up, rather than only for one with a figure to show: the
        // meter belongs to the window from the moment it opens, and one that arrived partway
        // through the row on the first reply read as something having gone wrong until then.
        bool showContext = _settings.EnableLiveStatus && _cli.Features.CompactButton
                           && _activeChildIndex >= 0 && _activeChildIndex < _children.Count;
        StatusContextPanel.IsVisible = showContext;
        if (!showContext) return;

        // Reported as context used rather than context left. The two rate-limit meters beside
        // it fill as their window is spent, and a row of bars where one grows the opposite way
        // to its neighbours is read wrong at a glance however it is labelled.
        //
        // A session reads empty until its first reply lands. Nothing has been sent by then, so
        // nothing has been billed for a prefix - the window really is untouched, and the figure
        // the first reply brings back is the first one anybody, this app or the CLI, can know.
        int used = session.HasData && session.ContextTokens > 0
            ? SessionCostMonitor.ContextUsedPercent(session.ContextTokens, session.Model)
            : 0;
        StatusContextLabel.Text = Loc.Get("ContextLabel");
        StatusContextText.Text = used + "%";
        StatusContextFill.Width = 48 * (used / 100.0);
        StatusContextFill.Background = MeterFill(used);
    }

    /// <summary>
    /// The colour a status-bar meter fills in at a given utilisation. All three meters - context,
    /// the 5-hour window, the 7-day window - come through here, so a bar of the same length and
    /// colour carries the same meaning wherever it sits on the bar.
    /// </summary>
    private static SolidColorBrush MeterFill(int usedPercent) => new(
        usedPercent >= 80 ? Color.FromRgb(255, 69, 58)
        : usedPercent >= 50 ? Color.FromRgb(255, 214, 10)
        : Color.FromRgb(48, 209, 88));

    /// <summary>Shows the one banner that matters right now: a known error, or the compact hint.</summary>
    private void UpdateAdviceBanner(TerminalSnapshot snap)
    {
        string? key = null, title = null, detail = null, actionLabel = null, actionCommand = null;
        var accent = Color.FromRgb(255, 69, 58);

        if (_settings.EnableErrorBanner && snap.Error != null)
        {
            key = "err:" + snap.Error.Kind;
            title = snap.Error.Title;
            detail = snap.Error.Detail;
            actionLabel = snap.Error.ActionLabel;
            actionCommand = snap.Error.ActionCommand;
        }
        else if (_settings.EnableLiveStatus && _cli.Features.CompactButton)
        {
            var cost = ActiveCost.Current;

            // The prefix size comes from the transcript, in tokens, and is judged against an
            // absolute threshold. The CLI's own "n% left" is only a fallback for when the
            // transcript has not been found yet: on a 1M-token model that percentage does not
            // turn red until the conversation is already several times past the point where a
            // fresh start would have been cheaper.
            if (cost.HasData && cost.ContextTokens >= _settings.HandoffBannerTokens)
            {
                key = "compact";
                title = Loc.Get("HandoffTitle");
                detail = string.Format(Loc.Get("HandoffDetailFormat"),
                    FormatTokens(cost.ContextTokens), FormatUsd(cost.NextTurnUsd));
            }
            else if (!cost.HasData && snap.ContextRemainingPercent is int left
                     && left <= CliContextLowPercent)
            {
                key = "compact";
                title = Loc.Get("ContextLowTitle");
                detail = string.Format(Loc.Get("ContextLowDetail"), left);
            }

            if (key != null)
            {
                // Handing off is the cheaper of the two ways out, so it gets the button. /compact
                // stays one click away on the context meter itself.
                actionLabel = Loc.Get("HandoffAction");
                actionCommand = HandoffActionCommand;
                accent = Color.FromRgb(255, 214, 10);
            }
        }

        if (key == null)
        {
            // Condition gone: re-arm anything the user dismissed.
            _dismissedBanners.Clear();
            if (_bannerKey != null) HideBanner();
            return;
        }

        if (_dismissedBanners.Contains(key))
        {
            if (_bannerKey != null) HideBanner();
            return;
        }

        if (_bannerKey == key) return;
        ShowBanner(key, title ?? "", detail ?? "", actionLabel, actionCommand, accent);
    }

    private void ShowBanner(string key, string title, string detail,
                            string? actionLabel, string? actionCommand, Color accent)
    {
        _bannerKey = key;
        _bannerActionCommand = actionCommand;
        BannerTitle.Text = title;
        BannerTitle.Foreground = new SolidColorBrush(accent);
        BannerAccent.Background = new SolidColorBrush(accent);
        // The tint rides on top of the banner's own opaque background; the banner
        // now floats over the terminal, so the console text must not show through.
        BannerTint.Background = new SolidColorBrush(accent, 0.14);
        BannerDetail.Text = detail;
        BannerDetail.IsVisible = !string.IsNullOrWhiteSpace(detail);
        LblBannerAction.Text = actionLabel ?? "";
        BtnBannerAction.IsVisible = !string.IsNullOrWhiteSpace(actionLabel);
        InfoBanner.IsVisible = true;
    }

    private void HideBanner()
    {
        _bannerKey = null;
        _bannerActionCommand = null;
        InfoBanner.IsVisible = false;
    }

    private void OnBannerAction(object? sender, RoutedEventArgs e)
    {
        var cmd = _bannerActionCommand;

        if (cmd == HandoffActionCommand)
        {
            if (_bannerKey != null) _dismissedBanners.Add(_bannerKey);
            HideBanner();
            _ = StartHandoffAsync();
            return;
        }

        if (!string.IsNullOrEmpty(cmd))
        {
            if (cmd.StartsWith("/") && _activeChildIndex >= 0 && _activeChildIndex < _children.Count)
            {
                // A slash command belongs to the CLI: type it for the user.
                _children[_activeChildIndex].Terminal.SendText(cmd + "\r");
                _children[_activeChildIndex].Terminal.FocusTerminal();
            }
            else
            {
                // A shell command is for a terminal of their choosing: hand it over on the clipboard.
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard != null) _ = clipboard.SetTextAsync(cmd);
            }
        }
        if (_bannerKey != null) _dismissedBanners.Add(_bannerKey);
        HideBanner();
    }

    private void OnBannerDismiss(object? sender, RoutedEventArgs e)
    {
        if (_bannerKey != null) _dismissedBanners.Add(_bannerKey);
        HideBanner();
    }

    private void OnModeBadgePressed(object? sender, PointerPressedEventArgs e)
    {
        SendModeSwitch();
        e.Handled = true;
    }

    private void OnContextMeterPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_activeChildIndex >= 0 && _activeChildIndex < _children.Count)
        {
            _children[_activeChildIndex].Terminal.SendText("/compact\r");
            _children[_activeChildIndex].Terminal.FocusTerminal();
        }
        e.Handled = true;
    }

    // ── Source control ──

    private void OnActivitySourceControl(object? sender, RoutedEventArgs e)
    {
        ToggleSidePanel(SidebarPanel.SourceControl);
    }

    // ── Isolated sessions (git worktree) ──

    /// <summary>A checkout handed to one window, with what is needed to take it away again.</summary>
    private sealed record WorktreeLease(string Path, string Branch, string RepoRoot);

    /// <summary>
    /// Creates the checkout the next session will work in, when the user has asked for one.
    /// Returns null for every other case - the toggle off, no repository, or git refusing -
    /// and the session then opens in the project folder as it always has.
    /// </summary>
    private async Task<WorktreeLease?> PrepareWorktreeAsync()
    {
        if (ChkIsolate.IsChecked != true) return null;

        var repo = _projectFolder;
        if (string.IsNullOrEmpty(repo)) return null;

        // Isolating an isolated session would cut the new branch from the old one. Go back to
        // the repository it came from instead.
        var origin = _children.FirstOrDefault(c =>
            string.Equals(c.WorktreePath, repo, StringComparison.OrdinalIgnoreCase))?.WorktreeOrigin;
        if (!string.IsNullOrEmpty(origin)) repo = origin;

        var root = GitCli.FindRepoRoot(repo);
        if (root == null) return null;

        var (path, result) = await WorktreeService.CreateAsync(root);
        if (path == null)
        {
            var detail = result.Message;
            ShowMessageDialog(Loc.Get("WorktreeFailedTitle"),
                detail.Length > 0 ? detail : Loc.Get("WorktreeFailedTitle"));
            return null;
        }

        var name = System.IO.Path.GetFileName(path);
        return new WorktreeLease(path, WorktreeService.BranchPrefix + name, root);
    }

    /// <summary>
    /// Brings back the checkout a saved tab was working in. The folder usually survives between
    /// runs; when it does not but its branch is still there, git re-creates it. A checkout that
    /// cannot be recovered leaves the tab to open in the project folder instead of failing.
    /// </summary>
    private async Task<WorktreeLease?> ReattachWorktreeAsync(WorkspaceTab tab)
    {
        if (string.IsNullOrEmpty(tab.WorktreePath) || string.IsNullOrEmpty(tab.WorktreeOrigin))
            return null;
        if (!Directory.Exists(tab.WorktreeOrigin)) return null;

        var path = await WorktreeService.ReattachAsync(tab.WorktreeOrigin, tab.WorktreePath, tab.WorktreeBranch);
        return path == null ? null : new WorktreeLease(path, tab.WorktreeBranch, tab.WorktreeOrigin);
    }

    /// <summary>
    /// Asks before a closing window takes its checkout with it. The removal is forced, so
    /// anything still uncommitted in that tree goes too. Returns false when the user would
    /// rather keep the window.
    /// </summary>
    private async Task<bool> ConfirmWorktreeReleaseAsync(MdiChildInfo entry)
    {
        if (string.IsNullOrEmpty(entry.WorktreePath) || string.IsNullOrEmpty(entry.WorktreeOrigin))
            return true;

        if (!await WorktreeService.HasUncommittedChangesAsync(entry.WorktreePath))
            return true;

        return await ShowConfirmDialog(
            Loc.Get("WorktreeDirtyTitle"),
            string.Format(Loc.Get("WorktreeDirtyFmt"), entry.WorktreeBranch ?? entry.WorktreePath));
    }

    /// <summary>Removes the checkout, once the window that lived in it is gone.</summary>
    private async Task ReleaseWorktreeAsync(MdiChildInfo entry)
    {
        if (string.IsNullOrEmpty(entry.WorktreePath) || string.IsNullOrEmpty(entry.WorktreeOrigin))
            return;

        await WorktreeService.RemoveAsync(entry.WorktreeOrigin, entry.WorktreePath, entry.WorktreeBranch);
        entry.WorktreePath = null;

        if (string.Equals(_projectFolder, entry.ProjectFolder, StringComparison.OrdinalIgnoreCase))
            RefreshGitInfo();
    }

    /// <summary>
    /// The toggle only means anything inside a repository, and only for a folder that is not
    /// already an isolated checkout.
    /// </summary>
    private void UpdateIsolateToggle()
    {
        bool possible = !string.IsNullOrEmpty(_projectFolder)
            && GitChangeService.IsGitRepository(_projectFolder);

        ChkIsolate.IsVisible = possible;
        if (!possible) ChkIsolate.IsChecked = false;
    }

    private void OnIsolateChanged(object? sender, RoutedEventArgs e)
    {
        // Deliberately not persisted: it decides where the next session's files live, which is
        // not a preference to inherit silently on the next launch.
    }

    // ── Git write: the status bar's branch switcher ──
    //
    // Staging, committing, pushing and the rest live in Controls/SourceControlPanel.cs. What
    // stays here is the branch dropdown next to the branch name in the status bar, which is
    // reachable without opening the sidebar.

    /// <summary>One git write at a time: the readouts are rebuilt from the result of each.</summary>
    private bool _gitWriteBusy;

    private async void OnBranchSwitch(object? sender, RoutedEventArgs e)
    {
        var repo = _projectFolder;
        if (string.IsNullOrEmpty(repo) || _gitWriteBusy) return;

        var (branches, current) = await GitWriteService.GetBranchesAsync(repo);
        if (!string.Equals(repo, _projectFolder, StringComparison.OrdinalIgnoreCase)) return;

        // Rebuilt on every click: branches come and go, unlike the fixed model and effort lists.
        var flyout = new MenuFlyout { Placement = PlacementMode.Top };
        foreach (var branch in branches)
        {
            var name = branch;
            var item = new MenuItem
            {
                Header = name == current ? "✓ " + name : "   " + name,
                IsEnabled = name != current,
            };
            item.Click += (_, _) => _ = SwitchBranchAsync(name);
            flyout.Items.Add(item);
        }

        if (branches.Count > 0) flyout.Items.Add(new Separator());

        var create = new MenuItem { Header = Loc.Get("NewBranch") };
        create.Click += (_, _) => _ = CreateBranchAsync();
        flyout.Items.Add(create);

        flyout.ShowAt(BtnBranchSwitch);
    }

    private async Task SwitchBranchAsync(string branch)
    {
        var repo = _projectFolder;
        if (string.IsNullOrEmpty(repo)) return;

        if (!await ShowConfirmDialog(
                Loc.Get("SwitchBranchConfirmTitle"),
                string.Format(Loc.Get("SwitchBranchConfirmFmt"), branch)))
            return;

        // git switch refuses on conflicting local changes rather than carrying them across, so
        // a dirty tree surfaces as git's own message instead of silently moving the work.
        await RunGitWriteAsync(GitWriteService.CheckoutBranchAsync(repo, branch));
    }

    private async Task CreateBranchAsync()
    {
        var repo = _projectFolder;
        if (string.IsNullOrEmpty(repo)) return;

        var name = await ShowTextInputDialog(Loc.Get("NewBranch"), Loc.Get("NewBranchPrompt"), "");
        if (string.IsNullOrWhiteSpace(name)) return;

        await RunGitWriteAsync(GitWriteService.CreateBranchAsync(repo, name.Trim()));
    }

    /// <summary>
    /// Runs one git write, shows git's own words when it refuses, and rebuilds everything the
    /// result could have changed. Returns whether it succeeded.
    /// </summary>
    private async Task<bool> RunGitWriteAsync(Task<GitResult> work)
    {
        _gitWriteBusy = true;
        try
        {
            var result = await work;
            if (!result.Ok)
            {
                var detail = result.Message;
                ShowMessageDialog(Loc.Get("GitFailedTitle"),
                    detail.Length > 0 ? detail : Loc.Get("GitFailedTitle"));
            }

            return result.Ok;
        }
        finally
        {
            _gitWriteBusy = false;
            // RefreshGitInfo also reloads the source-control panel.
            RefreshGitInfo();
        }
    }

    // ── Extensions: MCP servers, skills and plugins ──

    private ExtensionSnapshot? _extensions;
    private bool _extensionsLoading;
    /// <summary>Overrides the hint line once, to report what a write did.</summary>
    private string? _extensionsNotice;
    /// <summary>Skills outnumber the rest by two orders of magnitude, so that section starts folded.</summary>
    private readonly HashSet<ExtensionKind> _extensionsCollapsed = new() { ExtensionKind.Skill };

    private const int MaxExtensionRows = 150;

    private void OnActivityExtensions(object? sender, RoutedEventArgs e)
    {
        ToggleSidePanel(SidebarPanel.Extensions);
    }

    private void OnRefreshExtensions(object? sender, RoutedEventArgs e)
    {
        _extensions = null;
        _extensionsNotice = null;
        RefreshExtensionsPanel();
    }

    private void OnExtensionSearchChanged(object? sender, TextChangedEventArgs e)
    {
        _extensionsNotice = null;
        RenderExtensions();
    }

    private async void RefreshExtensionsPanel()
    {
        if (_extensionsLoading || !ExtensionsPanel.IsVisible) return;
        if (_extensions != null) { RenderExtensions(); return; }

        _extensionsLoading = true;
        ExtensionsList.Children.Clear();
        LblExtensionsHint.Text = Loc.Get("ExtensionsLoading");
        try
        {
            _extensions = await ExtensionCatalog.LoadAsync(_projectFolder);
        }
        catch
        {
            _extensions = new ExtensionSnapshot();
        }
        finally
        {
            _extensionsLoading = false;
        }

        if (ExtensionsPanel.IsVisible) RenderExtensions();
    }

    private void RenderExtensions()
    {
        ExtensionsList.Children.Clear();
        if (_extensions == null) return;

        var query = TxtExtensionSearch.Text?.Trim() ?? "";
        int shown = 0;
        shown += AddExtensionSection(ExtensionKind.Mcp, Loc.Get("McpServers"), _extensions.Mcp, query);
        shown += AddExtensionSection(ExtensionKind.Skill, Loc.Get("Skills"), _extensions.Skills, query);
        shown += AddExtensionSection(ExtensionKind.Plugin, Loc.Get("Plugins"), _extensions.Plugins, query);

        LblExtensionsHint.Text = _extensionsNotice
            ?? (shown == 0 && query.Length > 0 ? Loc.Get("NoMatches") : Loc.Get("ExtensionsApplyHint"));
    }

    /// <summary>Renders one section and returns how many rows matched, folded or not.</summary>
    private int AddExtensionSection(ExtensionKind kind, string title,
        IReadOnlyList<ExtensionItem> items, string query)
    {
        var matches = items.Where(i => MatchesExtensionQuery(i, query)).ToList();
        // A search that hits nothing in a section should not leave its header behind.
        if (matches.Count == 0 && query.Length > 0) return 0;

        // Searching means the user is after a row, so the fold gets out of the way.
        bool open = query.Length > 0 || !_extensionsCollapsed.Contains(kind);
        ExtensionsList.Children.Add(BuildExtensionHeader(kind, title, matches, open));
        if (!open) return matches.Count;

        foreach (var item in matches.Take(MaxExtensionRows))
            ExtensionsList.Children.Add(BuildExtensionRow(item));

        if (matches.Count > MaxExtensionRows)
            ExtensionsList.Children.Add(new TextBlock
            {
                Text = string.Format(Loc.Get("ExtensionsMoreFmt"), matches.Count - MaxExtensionRows),
                FontSize = 10,
                Opacity = 0.5,
                Margin = new Thickness(24, 3, 6, 6),
            });

        return matches.Count;
    }

    private static bool MatchesExtensionQuery(ExtensionItem item, string query)
    {
        if (query.Length == 0) return true;
        return Has(item.Name) || Has(item.Source) || Has(item.Description);

        bool Has(string? text) =>
            text != null && text.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private Control BuildExtensionHeader(ExtensionKind kind, string title,
        List<ExtensionItem> items, bool open)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

        var chevron = new TextBlock
        {
            Text = open ? "▾" : "▸",
            FontSize = 9,
            Width = 12,
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var label = new TextBlock
        {
            Text = title + "  (" + items.Count + ")",
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            LetterSpacing = 1,
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(chevron, 0);
        Grid.SetColumn(label, 1);
        grid.Children.Add(chevron);
        grid.Children.Add(label);

        int switchable = items.Count(i => i.CanToggle && i.Enabled);
        if (switchable > 0)
        {
            var off = new Button
            {
                Content = new TextBlock { Text = Loc.Get("DisableAll"), FontSize = 10 },
                Padding = new Thickness(6, 1),
                MinHeight = 0,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Opacity = 0.7,
                Cursor = new Cursor(StandardCursorType.Hand),
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(off, string.Format(Loc.Get("DisableAllFmt"), switchable));
            off.Click += (_, _) => DisableAllExtensions(kind, items);
            Grid.SetColumn(off, 2);
            grid.Children.Add(off);
        }

        var header = new Border
        {
            Padding = new Thickness(6, 9, 4, 3),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = grid,
        };
        // The button above handles its own press, so it never reaches this fold.
        header.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(header).Properties.IsLeftButtonPressed) return;
            if (!_extensionsCollapsed.Remove(kind)) _extensionsCollapsed.Add(kind);
            RenderExtensions();
        };
        return header;
    }

    private Control BuildExtensionRow(ExtensionItem item)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

        Control lead;
        if (item.CanToggle)
        {
            var box = new CheckBox
            {
                IsChecked = item.Enabled,
                MinWidth = 0,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            box.IsCheckedChanged += (_, _) =>
            {
                bool want = box.IsChecked == true;
                if (want != item.Enabled) ApplyExtensionToggle(item, want);
            };
            lead = box;
        }
        else
        {
            // Nothing here to switch, so the dot only reports whether the owner is on.
            lead = new Ellipse
            {
                Width = 6,
                Height = 6,
                Margin = new Thickness(4, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = new SolidColorBrush(item.Enabled
                    ? Color.FromRgb(48, 209, 88)
                    : Color.FromRgb(142, 142, 147)),
            };
        }

        var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = item.Name,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var second = string.IsNullOrEmpty(item.Detail) ? item.Description : item.Detail;
        if (!string.IsNullOrEmpty(second))
            text.Children.Add(new TextBlock
            {
                Text = second,
                FontSize = 10,
                Opacity = 0.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

        Grid.SetColumn(lead, 0);
        Grid.SetColumn(text, 1);
        grid.Children.Add(lead);
        grid.Children.Add(text);

        // For a server or a skill the source names who to switch off, so it earns the column.
        // A plugin's source is its marketplace - the same string on nearly every row, and it
        // truncates to nothing in a narrow panel - so that one stays in the tooltip.
        if (item.Kind != ExtensionKind.Plugin)
        {
            var source = new TextBlock
            {
                Text = item.Source,
                FontSize = 9,
                Opacity = 0.45,
                MaxWidth = 84,
                Margin = new Thickness(6, 0, 2, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(source, 2);
            grid.Children.Add(source);
        }

        var row = new Border
        {
            Padding = new Thickness(6, 4),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Opacity = item.Enabled ? 1.0 : 0.5,
            Child = grid,
        };

        var tip = item.Description ?? item.Name;
        if (item.Kind == ExtensionKind.Plugin && item.Source.Length > 0)
            tip += Environment.NewLine + string.Format(Loc.Get("FromMarketplaceFmt"), item.Source);
        if (!string.IsNullOrEmpty(item.Path))
            tip += Environment.NewLine + item.Path;
        if (item.Kind == ExtensionKind.Skill)
            tip += Environment.NewLine + string.Format(Loc.Get("SkillInvokeFmt"), SkillCommand(item));
        else if (!string.IsNullOrEmpty(item.Path))
            tip += Environment.NewLine + Loc.Get("DoubleClickOpens");
        if (item.Kind == ExtensionKind.Mcp && !item.CanToggle)
            tip += Environment.NewLine + (item.Source == "user"
                ? Loc.Get("McpUserScoped")
                : string.Format(Loc.Get("McpOwnedByFmt"), item.Source));
        ToolTip.SetTip(row, tip);

        row.Cursor = new Cursor(StandardCursorType.Hand);
        var hover = new SolidColorBrush(_isDark
            ? Color.FromArgb(30, 255, 255, 255)
            : Color.FromArgb(20, 0, 0, 0));
        row.PointerEntered += (_, _) => row.Background = hover;
        row.PointerExited += (_, _) => row.Background = Brushes.Transparent;

        // Double click runs the row, the way the slash panel does. A single click is left
        // alone because these rows carry a checkbox, and a stray click that launches an
        // editor - or a turn - is worse than one that does nothing.
        row.DoubleTapped += (_, e) =>
        {
            // Double clicking the box is two toggles, not a request to run anything.
            if (e.Source is Visual source && source.FindAncestorOfType<CheckBox>(true) != null) return;
            if (item.Kind == ExtensionKind.Skill) RunSkill(item);
            else if (!string.IsNullOrEmpty(item.Path)) OpenPath(item.Path!);
        };

        if (!string.IsNullOrEmpty(item.Path))
        {
            var open = new MenuItem { Header = Loc.Get("Open") };
            open.Click += (_, _) => OpenPath(item.Path!);
            row.ContextMenu = new ContextMenu { ItemsSource = new[] { open } };
        }

        return row;
    }

    /// <summary>
    /// The slash form that runs a skill. A plugin's skills are addressed through it, so the
    /// plugin name is part of the command; a project or personal skill stands alone.
    /// </summary>
    private static string SkillCommand(ExtensionItem item) =>
        item.Source is "project" or "user"
            ? "/" + item.Name
            : "/" + item.Source + ":" + item.Name;

    private void RunSkill(ExtensionItem item)
    {
        if (_activeChildIndex < 0 || _activeChildIndex >= _children.Count)
        {
            _extensionsNotice = Loc.Get("SlashNeedsSession");
            RenderExtensions();
            return;
        }
        // Only a bare terminal takes the carriage return as "send". With the chat view or the
        // expanded input open the text lands in a box, where it would just be a stray
        // character - so there the command is typed and the user presses Enter.
        var terminal = _children[_activeChildIndex].Terminal;
        bool writesToPty = !terminal.IsDocumentView && !terminal.IsExpanded;
        SendToActiveTerminal(writesToPty ? SkillCommand(item) + "\r" : SkillCommand(item));
    }

    private void ApplyExtensionToggle(ExtensionItem item, bool enabled)
    {
        var project = _projectFolder;
        string? error;
        if (item.Kind == ExtensionKind.Plugin)
            error = ExtensionCatalog.SetPluginEnabled(item.Id, enabled);
        else if (item.Kind == ExtensionKind.Mcp && !string.IsNullOrEmpty(project))
            error = ExtensionCatalog.SetProjectMcpEnabled(project, item.Id, enabled);
        else
            return;

        ReportExtensionWrite(error, 1);
    }

    private async void DisableAllExtensions(ExtensionKind kind, List<ExtensionItem> items)
    {
        var targets = items.Where(i => i.CanToggle && i.Enabled).ToList();
        if (targets.Count == 0) return;

        if (!await ShowConfirmDialog(Loc.Get("DisableAll"),
                string.Format(Loc.Get("DisableAllConfirmFmt"), targets.Count)))
            return;

        var project = _projectFolder;
        string? error = null;
        foreach (var item in targets)
        {
            if (kind == ExtensionKind.Plugin)
                error = ExtensionCatalog.SetPluginEnabled(item.Id, false);
            else if (!string.IsNullOrEmpty(project))
                error = ExtensionCatalog.SetProjectMcpEnabled(project, item.Id, false);
            if (error != null) break;
        }

        ReportExtensionWrite(error, targets.Count);
    }

    private void ReportExtensionWrite(string? error, int count)
    {
        _extensionsNotice = error != null
            ? string.Format(Loc.Get("ExtensionsWriteFailedFmt"), error)
            : string.Format(Loc.Get("ExtensionsChangedFmt"), count);
        _extensions = null;
        RefreshExtensionsPanel();
    }

    private static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch { }
    }

    // ── Tokens & Cost ──

    private void OnActivityCost(object? sender, RoutedEventArgs e)
    {
        new Controls.CostDashboardWindow(_isDark, _projectFolder).Show(this);
    }

    // ── Plan ──

    private static int PlanDailyLimit(string planTier) => planTier switch
    {
        "Max5x" => 5000,
        "Max20x" => 20000,
        _ => 1000,
    };

    private void FillPlanTierCombo()
    {
        CmbPlanTier.ItemsSource = new List<string>
        {
            Loc.Get("PlanPro"),
            Loc.Get("PlanMax5x"),
            Loc.Get("PlanMax20x"),
        };
        int idx = Array.IndexOf(PlanTierIds, _settings.PlanTier);
        CmbPlanTier.SelectedIndex = idx >= 0 ? idx : 0;
    }

    private void OnPlanTierChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_settingsInitialized || _suppressSettingsChanged) return;

        int idx = CmbPlanTier.SelectedIndex;
        if (idx < 0 || idx >= PlanTierIds.Length) return;

        _settings.PlanTier = PlanTierIds[idx];
        _settings.Save();
        UsageTracker.DailyLimit = PlanDailyLimit(_settings.PlanTier);
    }

    /// <summary>Commit-message languages in the order the settings combo lists them.</summary>
    private static readonly string[] CommitLanguageIds = { "auto", "ja", "en" };

    private void FillCommitLanguageCombo()
    {
        CmbCommitLanguage.ItemsSource = new List<string>
        {
            Loc.Get("CommitLanguageAuto"),
            Loc.Get("CommitLanguageJa"),
            Loc.Get("CommitLanguageEn"),
        };
        int idx = Array.IndexOf(CommitLanguageIds, _settings.CommitMessageLanguage);
        CmbCommitLanguage.SelectedIndex = idx >= 0 ? idx : 0;
    }

    private void OnCommitLanguageChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_settingsInitialized || _suppressSettingsChanged) return;

        int idx = CmbCommitLanguage.SelectedIndex;
        if (idx < 0 || idx >= CommitLanguageIds.Length) return;

        _settings.CommitMessageLanguage = CommitLanguageIds[idx];
        _settings.Save();
        // The panel shows the same setting on its own [JA|EN] toggle.
        _sourceControl?.OnSettingsChanged();
    }

    private void OnGitAutoFetchChanged(object? sender, RoutedEventArgs e)
    {
        if (!_settingsInitialized || _suppressSettingsChanged) return;

        _settings.GitAutoFetch = ChkGitAutoFetch.IsChecked == true;
        _settings.Save();
    }

    private void OnLiveStatusSettingChanged(object? sender, RoutedEventArgs e)
    {
        if (!_settingsInitialized || _suppressSettingsChanged) return;

        _settings.EnableLiveStatus = ChkEnableLiveStatus.IsChecked == true;
        _settings.EnableErrorBanner = ChkEnableErrorBanner.IsChecked == true;
        _settings.Save();

        if (!_settings.EnableLiveStatus) ClearLiveStatus();
        if (!_settings.EnableErrorBanner && _bannerKey != null && _bannerKey.StartsWith("err:")) HideBanner();
    }

    // ── Command Palette ──

    private void ShowCommandPalette()
    {
        // English stays alongside the localized label so the palette answers to either. Someone
        // running the Japanese UI who has learnt "tile" from the docs still finds the entry, and
        // the Japanese label is what makes the palette usable at all in that language.
        var commands = new List<(string Key, string English, string Shortcut, Action Execute)>
        {
            ("NewSession", "New Session", "Ctrl+N", () => LaunchClaudeWithInitialPrompt()),
            ("PaletteSourceControl", "Source Control", "Ctrl+Shift+G", () => ToggleSidePanel(SidebarPanel.SourceControl)),
            ("CostDashboard", "Tokens & Cost", "", () => new Controls.CostDashboardWindow(_isDark, _projectFolder).Show(this)),
            ("PaletteCloseTab", "Close Tab", "Ctrl+W", CloseActiveWindow),
            ("PaletteNextTab", "Next Tab", "Ctrl+Tab", () => CycleWindows(1)),
            ("PalettePrevTab", "Previous Tab", "Ctrl+Shift+Tab", () => CycleWindows(-1)),
            ("PaletteToggleExplorer", "Toggle Explorer", "Ctrl+Shift+E", () => ToggleSidePanel(SidebarPanel.Explorer)),
            ("PaletteToggleSnippets", "Toggle Snippets", "", () => ToggleSidePanel(SidebarPanel.Snippets)),
            ("PaletteToggleWindows", "Toggle Windows Panel", "", () => ToggleSidePanel(SidebarPanel.Windows)),
            ("PaletteToggleSettings", "Toggle Settings", "", () => ToggleSidePanel(SidebarPanel.Settings)),
            ("TileWindows", "Tile Windows", "", () => { _layout = MdiLayout.Tile; ArrangeChildren(); }),
            ("CascadeWindows", "Cascade Windows", "", () => { _layout = MdiLayout.Cascade; ArrangeChildren(); }),
            ("TileHorizontally", "Tile Horizontally", "", () => { _layout = MdiLayout.TileHorizontal; ArrangeChildren(); }),
            ("TileVertically", "Tile Vertically", "", () => { _layout = MdiLayout.TileVertical; ArrangeChildren(); }),
            ("FullView", "Full View", "", () => { _layout = MdiLayout.Maximize; ArrangeChildren(); }),
            ("RunCompact", "Compact (/compact)", "", () => OnActivityCompact(null, null!)),
            ("PaletteSwitchMode", "Switch Mode (Shift+Tab)", "", SendModeSwitch),
            ("SaveWorkspace", "Save Workspace", "", SaveWorkspace),
            ("SaveWorkspaceAs", "Save Workspace As...", "", () => _ = PromptSaveWorkspaceAsync()),
            ("RestoreWorkspace", "Restore Workspace", "", () => RestoreWorkspace()),
            ("Workspaces", "Workspaces...", "", ShowWorkspaceList),
            ("SlashCommands", "Slash Commands", "Ctrl+/", ToggleSlashPanel),
            ("Checkpoints", "Checkpoints", "", ShowCheckpointList),
            ("StopTask", "Stop", "Esc", () => OnStopTask(null, null!)),
            ("PaletteUsageChart", "Usage Chart", "", () => new UsageChartWindow().Show(this)),
            ("SetupDoctor", "Setup Check", "", () => _ = ShowSetupDoctorAsync()),
            ("Shortcuts", "Keyboard Shortcuts", "F1", ShowShortcutSheet),
            ("CommandPalette", "Command Palette", "Ctrl+Shift+P", ShowCommandPalette),
        };

        var dialog = new Window
        {
            Title = Loc.Get("CommandPalette"),
            Width = 450, Height = 350,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            WindowDecorations = WindowDecorations.BorderOnly,
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 32)),
            Topmost = true,
        };

        var searchBox = new TextBox
        {
            PlaceholderText = Loc.Get("TypeToSearch"),
            FontSize = 14,
            Padding = new Thickness(10, 8),
            Background = new SolidColorBrush(Color.FromRgb(44, 44, 46)),
            Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 225)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(65, 65, 70)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(0),
        };

        var listBox = new ListBox
        {
            Background = Brushes.Transparent,
            Margin = new Thickness(0, 4, 0, 0),
        };

        void UpdateList(string filter)
        {
            listBox.Items.Clear();
            foreach (var cmd in commands)
            {
                var label = Loc.Get(cmd.Key, cmd.English);

                if (!string.IsNullOrEmpty(filter) &&
                    !label.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                    !cmd.English.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    continue;

                var nameText = new TextBlock { Text = label, FontSize = 13, Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 225)) };
                var shortcutText = new TextBlock { Text = cmd.Shortcut, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 125)), VerticalAlignment = VerticalAlignment.Center };
                var grid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
                Grid.SetColumn(nameText, 0);
                Grid.SetColumn(shortcutText, 1);
                grid.Children.Add(nameText);
                grid.Children.Add(shortcutText);

                var item = new ListBoxItem
                {
                    Content = grid,
                    Tag = cmd.Execute,
                    Padding = new Thickness(10, 6),
                };
                listBox.Items.Add(item);
            }
            if (listBox.Items.Count > 0)
                listBox.SelectedIndex = 0;
        }

        searchBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
                UpdateList(searchBox.Text ?? "");
        };

        searchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { dialog.Close(); e.Handled = true; }
            else if (e.Key == Key.Down && listBox.Items.Count > 0)
            {
                listBox.SelectedIndex = Math.Min(listBox.SelectedIndex + 1, listBox.Items.Count - 1);
                e.Handled = true;
            }
            else if (e.Key == Key.Up && listBox.Items.Count > 0)
            {
                listBox.SelectedIndex = Math.Max(listBox.SelectedIndex - 1, 0);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && listBox.SelectedItem is ListBoxItem sel && sel.Tag is Action action)
            {
                dialog.Close();
                action();
                e.Handled = true;
            }
        };

        listBox.DoubleTapped += (_, _) =>
        {
            if (listBox.SelectedItem is ListBoxItem sel && sel.Tag is Action action)
            {
                dialog.Close();
                action();
            }
        };
        // Escape has to work even before the search box has taken focus.
        dialog.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape) { dialog.Close(); args.Handled = true; }
        };

        var dock = new DockPanel();
        DockPanel.SetDock(searchBox, Dock.Top);
        dock.Children.Add(searchBox);
        dock.Children.Add(listBox);
        dialog.Content = dock;

        UpdateList("");
        dialog.ShowDialog(this);
        Dispatcher.UIThread.Post(() => searchBox.Focus());
    }

    // ── Stop button / Checkpoints (v0.2) ──

    /// <summary>Routes text to wherever the active window is taking input right now.</summary>
    private void SendToActiveTerminal(string text)
    {
        if (_activeChildIndex < 0 || _activeChildIndex >= _children.Count) return;

        var terminal = _children[_activeChildIndex].Terminal;
        if (terminal.IsDocumentView && terminal.IsExpanded)
            terminal.AppendToExpandedInput(text);
        else if (terminal.IsDocumentView)
            terminal.SetInputText(text);
        else if (terminal.IsExpanded)
            terminal.AppendToExpandedInput(text);
        else
            terminal.SendText(text);

        BringToFront(_activeChildIndex);
        terminal.FocusTerminal();
    }

    /// <summary>Escape is what every supported CLI reads as "stop what you are doing".</summary>
    // Three windows that used to be reachable only by typing their name into the command
    // palette. Each now sits next to the setting or the buttons it belongs with.
    private void OnShowCheckpoints(object? sender, RoutedEventArgs e) => ShowCheckpointList();

    private void OnShowUsageChart(object? sender, RoutedEventArgs e)
        => new UsageChartWindow().Show(this);

    private void OnShowWorkspaces(object? sender, RoutedEventArgs e) => ShowWorkspaceList();

    private void OnStopTask(object? sender, RoutedEventArgs e)
    {
        if (_activeChildIndex < 0 || _activeChildIndex >= _children.Count) return;

        var terminal = _children[_activeChildIndex].Terminal;
        terminal.SendText("\x1b");
        terminal.FocusTerminal();
    }

    private void OnNotifySettingChanged(object? sender, RoutedEventArgs e)
    {
        if (!_settingsInitialized || _suppressSettingsChanged) return;

        _settings.NotifyOnComplete = ChkNotifyOnComplete.IsChecked == true;
        _settings.NotifySound = ChkNotifySound.IsChecked == true;
        _settings.Save();

        _notifications.EnableToast = _settings.NotifyOnComplete;
        _notifications.EnableSound = _settings.NotifySound;
    }

    private void OnEnableCheckpointsChanged(object? sender, RoutedEventArgs e)
    {
        if (!_settingsInitialized || _suppressSettingsChanged) return;

        _settings.EnableCheckpoints = ChkEnableCheckpoints.IsChecked == true;
        _settings.Save();
    }

    /// <summary>
    /// Snapshots the project before a prompt runs. Git repos get a dangling stash commit, which
    /// leaves the working tree untouched; folders outside Git get a file copy.
    /// </summary>
    private async void CaptureCheckpoint(MdiChildInfo entry, string? prompt)
    {
        if (!_settings.EnableCheckpoints) return;

        var folder = entry.ProjectFolder;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;

        // Enter also confirms permission prompts and answers y/n, so snapshots are rate
        // limited; identical trees are then dropped by the service's SHA de-duplication.
        var latest = _checkpoints.LatestFor(folder);
        if (latest != null && (DateTime.Now - latest.CreatedAt).TotalSeconds < 20) return;

        // The prompt text is only ever a label. It is scraped back out of the cell grid and
        // the CLI may have drawn over it, so a snapshot must never depend on reading it.
        var trimmed = prompt?.Trim() ?? "";
        var label = trimmed.Length == 0
            ? DateTime.Now.ToString("HH:mm:ss")
            : trimmed.Length > 60 ? trimmed[..60] : trimmed;

        Debug.WriteLine($"[Checkpoint] capturing for {folder}: {label}");

        try
        {
            await _checkpoints.CreateAsync(folder, label);
        }
        catch { }
    }

    private void ShowCheckpointList()
    {
        if (_activeChildIndex < 0 || _activeChildIndex >= _children.Count) return;

        var folder = _children[_activeChildIndex].ProjectFolder ?? "";
        var points = _checkpoints.ForProject(folder).ToList();
        if (points.Count == 0)
        {
            ShowMessageDialog(Loc.Get("Checkpoints"), Loc.Get("NoCheckpoints"));
            return;
        }

        var list = new ListBox { Background = Brushes.Transparent };
        foreach (var cp in points)
        {
            var label = new TextBlock
            {
                Text = cp.Label,
                FontSize = 13,
                Foreground = new SolidColorBrush(DialogForeground()),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var when = new TextBlock
            {
                Text = cp.CreatedAt.ToString("MM/dd HH:mm"),
                FontSize = 11,
                Foreground = new SolidColorBrush(DialogSubtle()),
            };
            var stack = new StackPanel { Spacing = 2 };
            stack.Children.Add(label);
            stack.Children.Add(when);
            list.Items.Add(new ListBoxItem { Content = stack, Tag = cp, Padding = new Thickness(10, 6) });
        }
        list.SelectedIndex = 0;

        var restore = new Button
        {
            Content = Loc.Get("Undo"),
            MinWidth = 96,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        var cancel = new Button
        {
            Content = Loc.Get("Cancel"),
            MinWidth = 96,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(restore);

        var dock = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(buttons);
        dock.Children.Add(list);

        var dialog = CreateToolDialog(Loc.Get("Checkpoints"), 460, 380);
        dialog.Content = dock;

        cancel.Click += (_, _) => dialog.Close();
        restore.Click += async (_, _) =>
        {
            if (list.SelectedItem is not ListBoxItem sel || sel.Tag is not Checkpoint cp) return;

            dialog.Close();
            if (!await ShowConfirmDialog(Loc.Get("Undo"), Loc.Get("RestoreCheckpointFmt"))) return;

            var error = await _checkpoints.RestoreAsync(cp);
            ShowMessageDialog(
                Loc.Get("Checkpoints"),
                error == null
                    ? Loc.Get("CheckpointRestored")
                    : string.Format(Loc.Get("CheckpointFailedFmt"), error));

            RefreshGitInfo();
            RefreshFileTree();
        };

        _ = dialog.ShowDialog(this);
    }

    // ── Slash command panel (v0.2) ──

    private void OnActivitySlashCommands(object? sender, RoutedEventArgs e) => ToggleSlashPanel();

    /// <summary>
    /// Opens the side panel listing the CLI's slash commands with a description each, so they can
    /// be picked instead of memorised. Project commands from .claude/commands are appended below
    /// the built-ins.
    /// </summary>
    private void ToggleSlashPanel() => ToggleSidePanel(SidebarPanel.Slash);

    /// <summary>Reloads the list for the active session's provider and project folder.</summary>
    private void RefreshSlashPanel()
    {
        if (!SlashPanel.IsVisible) return;

        bool hasSession = _activeChildIndex >= 0 && _activeChildIndex < _children.Count;
        var folder = hasSession ? _children[_activeChildIndex].ProjectFolder : _projectFolder;

        _slashEntries.Clear();
        foreach (var c in SlashCommandCatalog.ForProvider(_cli.ActiveId))
            _slashEntries.Add((c, false));
        foreach (var c in SlashCommandCatalog.ForProject(_cli.ActiveId, folder))
            _slashEntries.Add((c, true));

        LblSlashHint.Text = hasSession ? Loc.Get("SlashPanelHint") : Loc.Get("SlashNeedsSession");
        FilterSlashList(TxtSlashSearch.Text ?? "");
    }

    private void FilterSlashList(string filter)
    {
        bool japanese = Loc.Language == "日本語";
        SlashList.Items.Clear();

        foreach (var (cmd, fromProject) in _slashEntries)
        {
            var description = japanese ? cmd.DescriptionJa : cmd.Description;
            if (!string.IsNullOrEmpty(filter)
                && !cmd.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !description.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;

            var stack = new StackPanel { Spacing = 1 };
            stack.Children.Add(new TextBlock
            {
                Text = cmd.Name,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
            });
            stack.Children.Add(new TextBlock
            {
                Text = cmd.NeedsArgument
                    ? description + "  (" + Loc.Get("NeedsArgument") + ")"
                    : description,
                FontSize = 10,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
            });

            if (fromProject)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = Loc.Get("ProjectCommands"),
                    FontSize = 9,
                    Foreground = new SolidColorBrush(Color.FromRgb(100, 165, 255)),
                });
            }

            SlashList.Items.Add(new ListBoxItem
            {
                Content = stack,
                Tag = cmd,
                Padding = new Thickness(8, 5),
            });
        }

        if (SlashList.ItemCount > 0) SlashList.SelectedIndex = 0;
    }

    private void SendSelectedSlashCommand()
    {
        if (SlashList.SelectedItem is not ListBoxItem sel || sel.Tag is not SlashCommand cmd) return;
        if (_activeChildIndex < 0 || _activeChildIndex >= _children.Count)
        {
            LblSlashHint.Text = Loc.Get("SlashNeedsSession");
            return;
        }
        // Commands that take an argument are only typed in, so the user can finish the line.
        SendToActiveTerminal(cmd.NeedsArgument ? cmd.Name + " " : cmd.Name + "\r");
    }

    private void OnSlashSearchChanged(object? sender, TextChangedEventArgs e)
        => FilterSlashList(TxtSlashSearch.Text ?? "");

    private void OnSlashSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && SlashList.ItemCount > 0)
        {
            SlashList.SelectedIndex = Math.Min(SlashList.SelectedIndex + 1, SlashList.ItemCount - 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Up && SlashList.ItemCount > 0)
        {
            SlashList.SelectedIndex = Math.Max(SlashList.SelectedIndex - 1, 0);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter) { SendSelectedSlashCommand(); e.Handled = true; }
        else if (e.Key == Key.Escape) { ToggleSidePanel(SidebarPanel.Slash); e.Handled = true; }
    }

    private void OnSlashListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { SendSelectedSlashCommand(); e.Handled = true; }
        else if (e.Key == Key.Escape) { ToggleSidePanel(SidebarPanel.Slash); e.Handled = true; }
    }

    private void OnSlashListDoubleTapped(object? sender, TappedEventArgs e)
        => SendSelectedSlashCommand();

    // ── Setup diagnostics (v0.2) ──

    private void OnSetupDoctor(object? sender, RoutedEventArgs e) => _ = ShowSetupDoctorAsync();

    /// <summary>First-run pass: only interrupts the user when something actually needs fixing.</summary>
    private async Task ShowSetupDoctorIfProblemsAsync()
    {
        try
        {
            var results = await SetupDoctor.RunAsync(_cli, _projectFolder);
            if (results.Any(r => r.Status != DiagnosticStatus.Ok))
                ShowSetupDoctorDialog(results);
        }
        catch { }
    }

    private async Task ShowSetupDoctorAsync()
    {
        try
        {
            ShowSetupDoctorDialog(await SetupDoctor.RunAsync(_cli, _projectFolder));
        }
        catch { }
    }

    private void ShowSetupDoctorDialog(List<DiagnosticResult> results)
    {
        var rows = new StackPanel { Spacing = 14, Margin = new Thickness(18, 16) };

        bool allOk = results.All(r => r.Status == DiagnosticStatus.Ok);
        rows.Children.Add(new TextBlock
        {
            Text = allOk ? Loc.Get("DoctorAllOk") : Loc.Get("DoctorHasIssues"),
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(allOk
                ? Color.FromRgb(48, 209, 88)
                : Color.FromRgb(255, 214, 10)),
        });

        foreach (var result in results)
        {
            var dot = new Ellipse
            {
                Width = 8,
                Height = 8,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 5, 0, 0),
                Fill = new SolidColorBrush(result.Status switch
                {
                    DiagnosticStatus.Ok => Color.FromRgb(48, 209, 88),
                    DiagnosticStatus.Warning => Color.FromRgb(255, 214, 10),
                    _ => Color.FromRgb(255, 69, 58),
                }),
            };

            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock
            {
                Text = Loc.Get(result.TitleKey),
                FontSize = 13,
                Foreground = new SolidColorBrush(DialogForeground()),
            });
            text.Children.Add(new TextBlock
            {
                Text = result.Detail,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(DialogSubtle()),
            });

            if (!string.IsNullOrEmpty(result.FixHint))
            {
                text.Children.Add(new TextBlock
                {
                    Text = result.FixHint,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.FromRgb(100, 165, 255)),
                });
            }

            if (!string.IsNullOrEmpty(result.FixCommand))
            {
                var command = result.FixCommand;
                var copy = new Button
                {
                    Content = Loc.Get("CopyCommand") + ":  " + command,
                    FontSize = 11,
                    Padding = new Thickness(8, 3),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 3, 0, 0),
                    Cursor = new Cursor(StandardCursorType.Hand),
                };
                copy.Click += async (_, _) =>
                {
                    await CopyToClipboard(command);
                    copy.Content = Loc.Get("Copied");
                };
                text.Children.Add(copy);
            }

            var row = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("18,*") };
            Grid.SetColumn(dot, 0);
            Grid.SetColumn(text, 1);
            row.Children.Add(dot);
            row.Children.Add(text);
            rows.Children.Add(row);
        }

        var rerun = new Button
        {
            Content = Loc.Get("RunAgain"),
            MinWidth = 96,
            HorizontalAlignment = HorizontalAlignment.Right,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        };
        rows.Children.Add(rerun);

        var dialog = CreateToolDialog(Loc.Get("SetupDoctor"), 480, 0);
        dialog.SizeToContent = SizeToContent.Height;
        dialog.Content = new ScrollViewer
        {
            Content = rows,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            MaxHeight = 620,
        };

        rerun.Click += (_, _) => { dialog.Close(); _ = ShowSetupDoctorAsync(); };
        _ = dialog.ShowDialog(this);
    }

    // ── Keyboard shortcut cheat sheet (v0.2) ──

    private void OnShowShortcuts(object? sender, RoutedEventArgs e) => ShowShortcutSheet();

    private void ShowShortcutSheet()
    {
        var groups = new (string TitleKey, (string Keys, string What)[] Rows)[]
        {
            ("ShortcutsWindows", new[]
            {
                ("Ctrl+N", Loc.Get("NewSession")),
                ("Ctrl+W", Loc.Get("Close")),
                ("Ctrl+Tab", "Next tab"),
                ("Ctrl+Shift+Tab", "Previous tab"),
            }),
            ("ShortcutsPanels", new[]
            {
                ("Ctrl+Shift+E", Loc.Get("EXPLORER")),
                ("Ctrl+Shift+G", Loc.Get("SOURCE_CONTROL")),
                ("Ctrl+Shift+P", Loc.Get("CommandPalette")),
                ("Ctrl+/", Loc.Get("SlashCommands")),
                ("F1", Loc.Get("Shortcuts")),
            }),
            ("ShortcutsTerminal", new[]
            {
                ("Ctrl+F", "Search"),
                ("Ctrl+Up / Ctrl+Down", "Jump between prompts"),
                ("Ctrl+Scroll", "Zoom font"),
                ("Ctrl+0", "Reset font size"),
                ("Shift+Enter", "New line without sending"),
                ("Ctrl+Enter", "Send from the expanded input"),
                ("Esc", Loc.Get("StopTask")),
                ("Ctrl+C", "Copy, or interrupt when nothing is selected"),
                ("Ctrl+V", "Paste, images included"),
            }),
        };

        var panel = new StackPanel { Spacing = 16, Margin = new Thickness(20, 18) };
        foreach (var (titleKey, entries) in groups)
        {
            panel.Children.Add(new TextBlock
            {
                Text = Loc.Get(titleKey),
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(100, 165, 255)),
            });

            foreach (var (keys, what) in entries)
            {
                var keyText = new TextBlock
                {
                    Text = keys,
                    FontSize = 12,
                    FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
                    Foreground = new SolidColorBrush(DialogForeground()),
                };
                var whatText = new TextBlock
                {
                    Text = what,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(DialogSubtle()),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                var row = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("190,*") };
                Grid.SetColumn(keyText, 0);
                Grid.SetColumn(whatText, 1);
                row.Children.Add(keyText);
                row.Children.Add(whatText);
                panel.Children.Add(row);
            }
        }

        var dialog = CreateToolDialog(Loc.Get("Shortcuts"), 480, 0);
        dialog.SizeToContent = SizeToContent.Height;
        dialog.Content = new ScrollViewer
        {
            Content = panel,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            MaxHeight = 640,
        };
        dialog.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape || args.Key == Key.F1) dialog.Close();
        };
        _ = dialog.ShowDialog(this);
    }

    // ── Shared dialog chrome ──

    private Color DialogForeground() =>
        _isDark ? Color.FromRgb(220, 220, 225) : Color.FromRgb(40, 40, 45);

    private Color DialogSubtle() =>
        _isDark ? Color.FromRgb(140, 140, 148) : Color.FromRgb(110, 110, 118);

    private Window CreateToolDialog(string title, double width, double height)
    {
        var dialog = new Window
        {
            Title = title,
            Width = width,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
            Background = new SolidColorBrush(_isDark
                ? Color.FromRgb(30, 30, 32)
                : Color.FromRgb(246, 246, 250)),
        };
        if (height > 0) dialog.Height = height;
        return dialog;
    }

    private Task<bool> ShowConfirmDialog(string title, string message)
    {
        var source = new TaskCompletionSource<bool>();

        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Foreground = new SolidColorBrush(DialogForeground()),
        };
        var ok = new Button
        {
            Content = Loc.Get("OK"),
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

        var panel = new StackPanel { Spacing = 18, Margin = new Thickness(22, 20) };
        panel.Children.Add(text);
        panel.Children.Add(buttons);

        var dialog = CreateToolDialog(title, 430, 0);
        dialog.SizeToContent = SizeToContent.Height;
        dialog.Content = panel;

        bool answered = false;
        ok.Click += (_, _) => { answered = true; source.TrySetResult(true); dialog.Close(); };
        cancel.Click += (_, _) => { answered = true; source.TrySetResult(false); dialog.Close(); };
        dialog.Closed += (_, _) => { if (!answered) source.TrySetResult(false); };

        _ = dialog.ShowDialog(this);
        return source.Task;
    }

    // ── Launch profiles ──

    private void OnLaunchProfileChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressProfileChange) return;

        int index = CmbLaunchProfile.SelectedIndex;
        if (index < 0 || index >= _profiles.Count) return;

        _settings.ActiveProfileId = _profiles[index].Id;
        _settings.Save();
        UpdateProfileTooltip();
    }

    private void UpdateProfileTooltip()
    {
        var profile = _cli.FindProfile(_settings.ActiveProfileId);
        ToolTip.SetTip(CmbLaunchProfile, profile == null
            ? Loc.Get("LaunchProfileTooltip")
            : $"{Loc.Get(profile.Description)}\n\n{profile.ExtraArgs}");
    }

    // ── Marginal cost ──

    /// <summary>
    /// Transcript backing the active window, or null while the window has no session of its own.
    /// </summary>
    private string? ResolveActiveSessionPath()
        => _activeChildIndex >= 0 && _activeChildIndex < _children.Count
            ? ResolveSessionPath(_children[_activeChildIndex])
            : null;

    private string? ResolveSessionPath(MdiChildInfo child)
    {
        var folder = string.IsNullOrEmpty(child.ProjectFolder) ? _projectFolder : child.ProjectFolder;
        if (string.IsNullOrEmpty(folder)) return null;

        // Only the window's own session, never a guess. Falling back to the newest transcript in
        // the project put whichever session happened to be written last on the status bar - a
        // neighbouring window mid-turn, or a CLI running outside Claucraft altogether - under
        // this window's name. TrackSessionIdAsync learns the real id within a poll or two of
        // launch, and until it does, no readout at all beats a confident wrong one.
        if (string.IsNullOrEmpty(child.SessionId)) return null;
        return SessionMessageReader.FindSessionFile(folder, child.SessionId!);
    }

    /// <summary>
    /// Keeps the session readout - which model is answering, how much context it has filled,
    /// how long the session has been running - in step with the active transcript.
    ///
    /// Read every tick rather than on the turn-end edge. The read is incremental and stops at a
    /// length check when nothing has been appended, so the cost of polling is negligible, while
    /// waiting for an edge meant a compaction - which rewrites the prefix without a reply of its
    /// own - left the meter on the pre-compact figure until the user sent another message.
    /// </summary>
    private async void RefreshSessionReadout(TerminalSnapshot snap)
    {
        // Caught here regardless of what triggered it - Claucraft's own dropdown, "/model x"
        // typed straight at the prompt, or the CLI's own interactive picker - since all three
        // end with the CLI printing this same banner.
        if (snap.ModelSwitchedTo is { Length: > 0 } switched) _pendingModelLabel = switched;

        if (!_cli.Features.CompactButton)
        {
            ClearSessionReadout();
            return;
        }

        var monitor = ActiveCost;
        string? path = ResolveActiveSessionPath();
        if (path == null || ReferenceEquals(monitor, _noCost))
        {
            ClearSessionReadout();
            return;
        }

        if (!string.Equals(monitor.Path, path, StringComparison.OrdinalIgnoreCase))
        {
            _pendingModelLabel = null;
            monitor.Track(path);
        }

        // One read at a time across all windows: the first read of a large transcript can outrun
        // the poll, and stacking those would have several threads parsing megabytes at once. A
        // skipped tick costs nothing - the next one picks up from the same offset.
        if (_costRefreshInFlight) return;
        _costRefreshInFlight = true;
        try { await monitor.RefreshAsync(); }
        catch { /* a transcript that cannot be read just leaves the readout as it was */ }
        finally { _costRefreshInFlight = false; }

        ApplySessionReadout();
    }

    /// <summary>
    /// What the bar can say for a window with no transcript to read yet - a session that has
    /// only just started, or one this CLI keeps no record of. The model still gets named: the
    /// launch line and the settings file both know which one is about to answer, and leaving
    /// the slot empty until the first reply made the model look unknown when it was not. The
    /// context meter stands at empty for the same reason - the window is open and untouched,
    /// which is a thing worth showing.
    /// </summary>
    private void ClearSessionReadout()
    {
        ApplyContextMeter();
        ShowModelName(_pendingModelLabel ?? StartingModelName());
        ApplyEffortReadout();
    }

    /// <summary>The model that is answering, the context it has filled, and the effort it is
    /// answering at. Painting the meter here as well as on the live-status tick is what keeps
    /// it from trailing the transcript by a poll after a switch or a compaction.</summary>
    private void ApplySessionReadout()
    {
        var session = ActiveCost.Current;

        ApplyContextMeter();

        var model = SessionCostMonitor.ModelDisplayName(session.Model);

        // A switch only reaches the transcript with the next reply, so the picked name stands
        // in until then - cleared once the transcript names the same model itself.
        if (_pendingModelLabel != null)
        {
            if (string.Equals(model, _pendingModelLabel, StringComparison.OrdinalIgnoreCase))
                _pendingModelLabel = null;
            else
                model = _pendingModelLabel;
        }

        // A transcript names no model until its first reply lands, so until then the window
        // still speaks for the one it was started on.
        if (model.Length == 0) model = StartingModelName();

        ShowModelName(model);

        ApplyEffortReadout();
    }

    /// <summary>Puts a model name on the bar, or takes the whole control away when there is
    /// none to give.</summary>
    private void ShowModelName(string model)
    {
        StatusModelName.IsVisible = model.Length > 0;
        if (model.Length == 0) return;

        StatusModelText.Text = model;
        ToolTip.SetTip(StatusModelName, Loc.Get("ModelTooltip"));
    }

    /// <summary>The model the window in front was started on, as far as anything outside the
    /// transcript knows it. Empty for a CLI with no model to speak of.</summary>
    private string StartingModelName()
    {
        if (!_cli.Features.CompactButton) return "";
        if (_activeChildIndex < 0 || _activeChildIndex >= _children.Count) return "";
        return _children[_activeChildIndex].StartingModel ?? "";
    }

    /// <summary>
    /// What the model dropdown offers, as (alias to send, id that alias resolves to today).
    /// The alias is what gets sent - it always points at the newest release in its line, so a
    /// new version needs no change here. The id exists only to name the entry, and it is run
    /// through the same table the status bar reads with, which keeps the displayed names in
    /// one place: when a line ships a new version, ModelDisplayName is the only edit.
    /// </summary>
    private static readonly (string Alias, string ModelId)[] SwitchableModels =
    {
        ("fable", "claude-fable-5-1"),
        ("opus", "claude-opus-5"),
        ("sonnet", "claude-sonnet-5"),
        ("haiku", "claude-haiku-4-5"),
    };

    /// <summary>
    /// Fills the model dropdown. Built once: the entries never change, and which one is active
    /// is already on the bar next to it.
    /// </summary>
    private void BuildModelFlyout()
    {
        var flyout = new MenuFlyout { Placement = PlacementMode.Top };

        foreach (var (alias, modelId) in SwitchableModels)
        {
            var label = SessionCostMonitor.ModelDisplayName(modelId);
            var item = new MenuItem { Header = label };
            var capturedAlias = alias;
            var capturedLabel = label;
            item.Click += (_, _) => SwitchModel(capturedAlias, capturedLabel);
            flyout.Items.Add(item);
        }

        flyout.Items.Add(new Separator());

        // Anything the list above does not cover - a preview model, a pinned version - is still
        // reachable: a bare /model opens the CLI's own picker.
        var other = new MenuItem { Header = Loc.Get("ModelOther") };
        other.Click += (_, _) => SwitchModel(null, null);
        flyout.Items.Add(other);

        StatusModelName.Flyout = flyout;
    }

    /// <summary>
    /// Switches the session's model. A null alias sends a bare /model, which hands over to the
    /// CLI's own picker rather than choosing anything here.
    /// </summary>
    private void SwitchModel(string? alias, string? label)
    {
        if (_activeChildIndex < 0 || _activeChildIndex >= _children.Count) return;

        _children[_activeChildIndex].Terminal.SendText(
            alias == null ? "/model\r" : "/model " + alias + "\r");

        if (label == null) return;

        _pendingModelLabel = label;
        StatusModelText.Text = label;
        StatusModelName.IsVisible = true;
    }

    /// <summary>
    /// What the effort dropdown offers, as (level to send, name to show). The CLI takes these
    /// verbatim after /effort. "auto" is listed apart from them because it pins nothing - it
    /// hands the choice back to the CLI for every turn.
    /// </summary>
    private static readonly (string Level, string Label)[] SwitchableEfforts =
    {
        ("low", "Low"),
        ("medium", "Medium"),
        ("high", "High"),
        ("xhigh", "XHigh"),
        ("max", "Max"),
    };

    /// <summary>
    /// Fills the effort dropdown. Built once: the levels are fixed, and which one is running is
    /// already on the bar next to it.
    /// </summary>
    private void BuildEffortFlyout()
    {
        var flyout = new MenuFlyout { Placement = PlacementMode.Top };

        foreach (var (level, label) in SwitchableEfforts)
        {
            var item = new MenuItem { Header = label };
            var capturedLevel = level;
            item.Click += (_, _) => SwitchEffort(capturedLevel);
            flyout.Items.Add(item);
        }

        flyout.Items.Add(new Separator());

        var auto = new MenuItem { Header = Loc.Get("EffortAuto") };
        auto.Click += (_, _) => SwitchEffort("auto");
        flyout.Items.Add(auto);

        StatusEffortName.Flyout = flyout;
    }

    /// <summary>
    /// Switches the session's reasoning effort. The picked level goes straight onto the bar:
    /// nothing ever comes back to confirm it the way a model switch shows up in the transcript.
    /// </summary>
    private void SwitchEffort(string level)
    {
        if (_activeChildIndex < 0 || _activeChildIndex >= _children.Count) return;

        _children[_activeChildIndex].Terminal.SendText("/effort " + level + "\r");
        _children[_activeChildIndex].Effort = level;
        ApplyEffortReadout();
    }

    /// <summary>
    /// Draws the effort of the window in front. Hidden for a CLI that has no such notion, and
    /// hidden until there is a window to speak for.
    /// </summary>
    private void ApplyEffortReadout()
    {
        string? effort = _cli.Features.CompactButton
                         && _activeChildIndex >= 0 && _activeChildIndex < _children.Count
            ? _children[_activeChildIndex].Effort
            : null;

        StatusEffortName.IsVisible = effort != null;
        if (effort == null) return;

        StatusEffortText.Text = EffortDisplayName(effort);
        ToolTip.SetTip(StatusEffortName, Loc.Get("EffortTooltip"));
    }

    /// <summary>
    /// The name a level goes by. A level this build has never heard of passes through unchanged,
    /// so one added later still reads as something true.
    /// </summary>
    private static string EffortDisplayName(string level)
    {
        foreach (var (candidate, label) in SwitchableEfforts)
        {
            if (string.Equals(candidate, level, StringComparison.OrdinalIgnoreCase)) return label;
        }
        return string.Equals(level, "auto", StringComparison.OrdinalIgnoreCase)
            ? Loc.Get("EffortAuto")
            : level;
    }

    /// <summary>
    /// The effort a new window starts at: whatever its launch profile pinned on the command
    /// line, else the default the CLI would pick up for itself for the model it is starting on.
    /// </summary>
    private static string? StartingEffort(string command)
    {
        var pinned = Regex.Match(command, "--effort[= ]+([A-Za-z]+)");
        if (pinned.Success) return pinned.Groups[1].Value.ToLowerInvariant();

        var model = Regex.Match(command, "--model[= ]+([A-Za-z0-9.-]+)");
        return DefaultEffort(model.Success ? model.Groups[1].Value : null);
    }

    /// <summary>
    /// The effort the CLI would start a model on, out of the user's settings.json. It keeps two
    /// records: a per-model level under modelSettings, written whenever an effort is saved as
    /// the default, and a plain effortLevel behind that. Read afresh for every window rather
    /// than cached once, because the CLI rewrites the file as either of them moves.
    /// </summary>
    private static string? DefaultEffort(string? modelAlias)
    {
        try
        {
            string path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude", "settings.json");
            if (!File.Exists(path)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            string? modelId = ModelIdForAlias(modelAlias ?? ReadSetting(root, "model"));
            if (modelId != null
                && root.TryGetProperty("modelSettings", out var perModel)
                && perModel.ValueKind == JsonValueKind.Object
                && perModel.TryGetProperty(modelId, out var entry)
                && ReadSetting(entry, "effortLevel") is { } perModelLevel)
            {
                return perModelLevel;
            }

            return ReadSetting(root, "effortLevel");
        }
        catch { return null; }
    }

    private static string? ReadSetting(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(name, out var prop)
        && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    /// <summary>
    /// The id an alias stands for, so a per-model entry can be found from the alias a launch
    /// line or a settings file carries. Anything already an id passes through unchanged.
    /// </summary>
    private static string? ModelIdForAlias(string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias)) return null;
        foreach (var (candidate, modelId) in SwitchableModels)
        {
            if (string.Equals(candidate, alias, StringComparison.OrdinalIgnoreCase)) return modelId;
        }
        return alias;
    }

    /// <summary>
    /// The model a new window starts on, named the way the status bar names one. A launch
    /// profile that pins a model settles it; otherwise it is whatever the CLI takes from the
    /// user's settings.json. Null when neither points at a model id, so that a setting that
    /// names no single model - "default", "opusplan" - leaves the bar to wait for the
    /// transcript rather than printing a word that is not a model name.
    /// </summary>
    private static string? StartingModel(string command)
    {
        var pinned = Regex.Match(command, "--model[= ]+\"?([^\\s\"]+)");
        string? alias = pinned.Success ? pinned.Groups[1].Value : DefaultModel();
        if (string.IsNullOrWhiteSpace(alias)) return null;

        // A context-window suffix ("opus[1m]") still names the same model, and the id written
        // to the transcript carries one too - the bar has always read straight past it.
        int suffix = alias.IndexOf('[');
        if (suffix > 0) alias = alias[..suffix];

        string? id = ModelIdForAlias(alias);
        if (id == null || !id.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)) return null;

        string name = SessionCostMonitor.ModelDisplayName(id);
        return name.Length > 0 ? name : null;
    }

    /// <summary>
    /// The model the CLI would start on with nothing said on the command line, out of the
    /// user's settings.json. Read afresh for every window, like the effort beside it: the CLI
    /// rewrites the file whenever a model is saved as the default.
    /// </summary>
    private static string? DefaultModel()
    {
        try
        {
            string path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude", "settings.json");
            if (!File.Exists(path)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return ReadSetting(doc.RootElement, "model");
        }
        catch { return null; }
    }

    /// <summary>
    /// Draws the two rate-limit windows. A null readout means the limits could not be read at
    /// all - no credentials, an expired token, an endpoint that moved - and showing nothing is
    /// the honest answer there, not a number that might be stale or wrong.
    /// </summary>
    private void OnRateLimitsUpdated(RateLimitInfo? info)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ApplyRateLimitWindow(info?.FiveHour, StatusFiveHourPanel, StatusFiveHourText,
                StatusFiveHourFill, "RateLimit5hTooltip");
            ApplyRateLimitWindow(info?.SevenDay, StatusSevenDayPanel, StatusSevenDayText,
                StatusSevenDayFill, "RateLimit7dTooltip");
        });
    }

    private void ApplyRateLimitWindow(RateLimitWindow? window, StackPanel panel, TextBlock text,
        Border fill, string tooltipKey)
    {
        panel.IsVisible = window != null;
        if (window == null) return;

        int pct = window.UtilizationPercent;
        string resetsIn = window.ResetsIn;

        text.Text = resetsIn.Length > 0 ? $"{pct}% ({resetsIn})" : $"{pct}%";
        ToolTip.SetTip(panel, string.Format(Loc.Get(tooltipKey), pct,
            resetsIn.Length > 0 ? resetsIn : Loc.Get("RateLimitUnknownReset")));

        fill.Width = 48 * (pct / 100.0);
        fill.Background = MeterFill(pct);
    }

    private static string FormatUsd(double usd) =>
        usd >= 100 ? usd.ToString("N0", CultureInfo.InvariantCulture)
        : usd >= 1 ? usd.ToString("0.00", CultureInfo.InvariantCulture)
        : usd.ToString("0.000", CultureInfo.InvariantCulture);

    private static string FormatTokens(long tokens) =>
        tokens >= 1_000_000 ? (tokens / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture) + "M"
        : tokens >= 1_000 ? (tokens / 1_000.0).ToString("0", CultureInfo.InvariantCulture) + "k"
        : tokens.ToString(CultureInfo.InvariantCulture);

    // ── Hand-off to a new session ──

    /// <summary>
    /// Builds a brief from the active session's transcript, lets the user edit it, then opens a
    /// fresh session with it waiting in the input box. Nothing is sent: the user presses Enter.
    /// This replaces /compact, which pays full model price to summarise a context that is
    /// already sitting on disk.
    /// </summary>
    private async Task StartHandoffAsync()
    {
        string? path = ResolveActiveSessionPath();
        if (path == null)
        {
            await ShowConfirmDialog(Loc.Get("HandoffDialogTitle"), Loc.Get("HandoffNoSession"));
            return;
        }

        await StartHandoffFromTranscriptAsync(path);
    }

    /// <summary>
    /// The hand-off proper, from any transcript on disk: the active window's, or one picked from
    /// the session list that was never opened in this run. Same brief, same dialog, same launch.
    /// </summary>
    private async Task StartHandoffFromTranscriptAsync(string path)
    {
        string brief;
        try { brief = await HandoffBuilder.BuildAsync(path); }
        catch { brief = ""; }

        if (string.IsNullOrWhiteSpace(brief))
        {
            await ShowConfirmDialog(Loc.Get("HandoffDialogTitle"), Loc.Get("HandoffEmpty"));
            return;
        }

        string? edited = await ShowHandoffDialog(brief);
        if (string.IsNullOrWhiteSpace(edited)) return;

        LaunchClaudeWithInitialPrompt();

        // The brief goes into Claucraft's own input panel, not the PTY, so it does not race
        // the CLI's startup and cannot be sent before the user has read it.
        if (_children.Count > 0)
            _children[^1].Terminal.ShowInExpandedInput(edited!);
    }

    private Task<string?> ShowHandoffDialog(string brief)
    {
        var source = new TaskCompletionSource<string?>();

        var hint = new TextBlock
        {
            Text = Loc.Get("HandoffDialogHint"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = new SolidColorBrush(DialogSubtle()),
        };

        var editor = new TextBox
        {
            Text = brief,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 380,
            FontFamily = new FontFamily(_settings.FontFamily),
            FontSize = 12,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(editor, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);

        var start = new Button
        {
            Content = Loc.Get("HandoffStart"),
            MinWidth = 140,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        var cancel = new Button
        {
            Content = Loc.Get("HandoffCancel"),
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
        buttons.Children.Add(start);

        var panel = new StackPanel { Spacing = 14, Margin = new Thickness(22, 20) };
        panel.Children.Add(hint);
        panel.Children.Add(editor);
        panel.Children.Add(buttons);

        var dialog = CreateToolDialog(Loc.Get("HandoffDialogTitle"), 720, 0);
        dialog.SizeToContent = SizeToContent.Height;
        dialog.Content = panel;

        bool answered = false;
        start.Click += (_, _) =>
        {
            answered = true;
            source.TrySetResult(editor.Text);
            dialog.Close();
        };
        cancel.Click += (_, _) => { answered = true; source.TrySetResult(null); dialog.Close(); };
        dialog.Closed += (_, _) => { if (!answered) source.TrySetResult(null); };

        _ = dialog.ShowDialog(this);
        return source.Task;
    }

    // ── Tab Context Menu ──

    private ContextMenu CreateTabContextMenu(MdiChildInfo entry)
    {
        var closeItem = new MenuItem { Header = Loc.Get("Close") };
        closeItem.Click += (_, _) => CloseChild(entry);

        var closeOthersItem = new MenuItem { Header = Loc.Get("CloseOthers") };
        closeOthersItem.Click += (_, _) =>
        {
            var toClose = _children.Where(c => c != entry).ToList();
            foreach (var c in toClose) CloseChild(c);
        };

        var closeRightItem = new MenuItem { Header = Loc.Get("CloseToRight") };
        closeRightItem.Click += (_, _) =>
        {
            int idx = _children.IndexOf(entry);
            var toClose = _children.Skip(idx + 1).ToList();
            foreach (var c in toClose) CloseChild(c);
        };

        var dupItem = new MenuItem { Header = Loc.Get("Duplicate") };
        dupItem.Click += (_, _) =>
        {
            _projectFolder = entry.ProjectFolder;
            LaunchClaudeWithInitialPrompt();
        };

        var exportItem = new MenuItem { Header = Loc.Get("ExportOutput") };
        exportItem.Click += async (_, _) =>
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Loc.Get("ExportOutput"),
                DefaultExtension = "txt",
                SuggestedFileName = $"claude_output_{DateTime.Now:yyyyMMdd_HHmmss}",
                FileTypeChoices = new[] { new FilePickerFileType("Text") { Patterns = new[] { "*.txt" } } }
            });
            if (file != null)
            {
                var text = entry.Terminal.ExportAsText();
                await System.IO.File.WriteAllTextAsync(file.Path.LocalPath, text);
            }
        };

        return new ContextMenu
        {
            Items = { closeItem, closeOthersItem, closeRightItem, new Separator(), dupItem, exportItem }
        };
    }

    // ── Workspace ──

    private void SaveWorkspace() => SaveWorkspaceAs(WorkspaceInfo.DefaultName);

    /// <summary>
    /// Captures the open windows down to the transcript each one is showing, so restoring brings
    /// the conversations back instead of opening blank sessions.
    /// </summary>
    private void SaveWorkspaceAs(string name)
    {
        var ws = new WorkspaceInfo
        {
            Name = string.IsNullOrWhiteSpace(name) ? WorkspaceInfo.DefaultName : name.Trim(),
            Layout = _layout.ToString(),
        };

        foreach (var child in _children)
        {
            double left = Canvas.GetLeft(child.Container);
            double top = Canvas.GetTop(child.Container);

            ws.Tabs.Add(new WorkspaceTab
            {
                ProjectFolder = child.ProjectFolder ?? "",
                TabTitle = child.StripText.Text ?? _cli.Active.Name,
                SessionId = child.SessionId ?? "",
                ProviderId = _cli.ActiveId,
                IsManualTitle = child.Terminal.IsManualTitle,
                WorktreePath = child.WorktreePath ?? "",
                WorktreeBranch = child.WorktreeBranch ?? "",
                WorktreeOrigin = child.WorktreeOrigin ?? "",
                Left = double.IsNaN(left) ? 0 : left,
                Top = double.IsNaN(top) ? 0 : top,
                Width = child.Container.Bounds.Width,
                Height = child.Container.Bounds.Height,
            });
        }

        WorkspaceService.Save(ws);
    }

    private async Task PromptSaveWorkspaceAsync()
    {
        var name = await ShowTextInputDialog(
            Loc.Get("SaveWorkspaceAs"), Loc.Get("WorkspaceName"), WorkspaceInfo.DefaultName);
        if (!string.IsNullOrWhiteSpace(name))
            SaveWorkspaceAs(name);
    }

    private async void RestoreWorkspace(string? name = null)
    {
        var ws = WorkspaceService.Load(name);
        if (ws == null || ws.Tabs.Count == 0) return;

        if (Enum.TryParse<MdiLayout>(ws.Layout, out var layout))
            _layout = layout;

        foreach (var tab in ws.Tabs)
        {
            var lease = await ReattachWorktreeAsync(tab);

            if (lease != null)
                _projectFolder = lease.Path;
            else if (!string.IsNullOrEmpty(tab.ProjectFolder) && Directory.Exists(tab.ProjectFolder))
                _projectFolder = tab.ProjectFolder;

            // Resume the exact transcript when the CLI can address one; otherwise open a new session.
            bool canResume = !string.IsNullOrEmpty(tab.SessionId)
                             && tab.ProviderId == _cli.ActiveId
                             && _cli.Features.SessionList;

            if (canResume)
                CreateNewChild(_cli.BuildResumeCommand(tab.SessionId, ActiveLaunchProfile()), tab.TabTitle, tab.TabTitle, tab.SessionId, lease);
            else
                CreateNewChild(_cli.BuildNewCommand(_settings.InitialPrompt, ActiveLaunchProfile()), tab.TabTitle, worktree: lease);

            if (tab.IsManualTitle && _children.Count > 0)
            {
                var child = _children[^1];
                child.Terminal.IsManualTitle = true;
                child.TitleText.Text = tab.TabTitle;
                child.StripText.Text = tab.TabTitle;
            }
        }

        // Saved geometry only means anything while windows float freely.
        if (_layout == MdiLayout.Cascade)
        {
            for (int i = 0; i < _children.Count && i < ws.Tabs.Count; i++)
            {
                var tab = ws.Tabs[i];
                if (tab.Width <= 0 || tab.Height <= 0) continue;

                var container = _children[i].Container;
                Canvas.SetLeft(container, tab.Left);
                Canvas.SetTop(container, tab.Top);
                container.Width = tab.Width;
                container.Height = tab.Height;
            }
        }
        else
        {
            ArrangeChildren();
        }

        RefreshWindowsPanel();
    }

    private void ShowWorkspaceList()
    {
        var names = WorkspaceService.Names();
        if (names.Count == 0)
        {
            ShowMessageDialog(Loc.Get("Workspaces"), Loc.Get("NoWorkspaces"));
            return;
        }

        var list = new ListBox { Background = Brushes.Transparent };
        foreach (var name in names)
        {
            list.Items.Add(new ListBoxItem
            {
                Content = new TextBlock
                {
                    Text = name,
                    FontSize = 13,
                    Foreground = new SolidColorBrush(DialogForeground()),
                },
                Tag = name,
                Padding = new Thickness(10, 6),
            });
        }
        list.SelectedIndex = 0;

        var restore = new Button
        {
            Content = Loc.Get("RestoreWorkspace"),
            MinWidth = 110,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        var delete = new Button
        {
            Content = Loc.Get("Delete"),
            MinWidth = 88,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        buttons.Children.Add(delete);
        buttons.Children.Add(restore);

        var dock = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(buttons);
        dock.Children.Add(list);

        var dialog = CreateToolDialog(Loc.Get("Workspaces"), 400, 340);
        dialog.Content = dock;

        string? Selected() =>
            list.SelectedItem is ListBoxItem sel ? sel.Tag as string : null;

        restore.Click += (_, _) =>
        {
            var name = Selected();
            dialog.Close();
            if (name != null) RestoreWorkspace(name);
        };
        delete.Click += (_, _) =>
        {
            var name = Selected();
            if (name == null) return;
            WorkspaceService.Delete(name);
            dialog.Close();
        };
        list.DoubleTapped += (_, _) =>
        {
            var name = Selected();
            dialog.Close();
            if (name != null) RestoreWorkspace(name);
        };

        _ = dialog.ShowDialog(this);
    }

    /// <param name="okLabel">
    /// What the accepting button says. Defaults to Save, which is wrong for a dialog that sends
    /// rather than stores.
    /// </param>
    private Task<string?> ShowTextInputDialog(string title, string watermark, string initial,
        string? okLabel = null)
    {
        var source = new TaskCompletionSource<string?>();

        var box = new TextBox
        {
            Text = initial,
            PlaceholderText = watermark,
            FontSize = 13,
            Padding = new Thickness(8, 6),
        };
        var ok = new Button
        {
            Content = okLabel ?? Loc.Get("Save"),
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

        var panel = new StackPanel { Spacing = 16, Margin = new Thickness(22, 20) };
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        var dialog = CreateToolDialog(title, 420, 0);
        dialog.SizeToContent = SizeToContent.Height;
        dialog.Content = panel;

        bool answered = false;
        void Accept()
        {
            answered = true;
            source.TrySetResult(box.Text);
            dialog.Close();
        }

        ok.Click += (_, _) => Accept();
        cancel.Click += (_, _) => { answered = true; source.TrySetResult(null); dialog.Close(); };
        box.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter) { Accept(); args.Handled = true; }
            else if (args.Key == Key.Escape) { answered = true; source.TrySetResult(null); dialog.Close(); }
        };
        dialog.Closed += (_, _) => { if (!answered) source.TrySetResult(null); };

        _ = dialog.ShowDialog(this);
        Dispatcher.UIThread.Post(() => { box.Focus(); box.SelectAll(); });
        return source.Task;
    }

    // ── Layout switching ──

    private void OnLayoutTile(object? sender, RoutedEventArgs e)
    {
        _layout = MdiLayout.Tile;
        ArrangeChildren();
    }

    private void OnLayoutCascade(object? sender, RoutedEventArgs e)
    {
        _layout = MdiLayout.Cascade;
        ArrangeChildren();
    }

    private void OnLayoutMaximize(object? sender, RoutedEventArgs e)
    {
        _layout = MdiLayout.Maximize;
        ArrangeChildren();
    }

    private void OnLayoutTileH(object? sender, RoutedEventArgs e)
    {
        _layout = MdiLayout.TileHorizontal;
        ArrangeChildren();
    }

    private void OnLayoutTileV(object? sender, RoutedEventArgs e)
    {
        _layout = MdiLayout.TileVertical;
        ArrangeChildren();
    }

    /// <summary>
    /// Terminal windows and floating editor windows are tracked in two independent lists
    /// (_children keeps its TerminalControl-specific indexing for command routing), but they
    /// share one MDI canvas and one set of layout modes - so layout math runs over both
    /// combined, in creation order across the two lists.
    /// </summary>
    private List<IMdiLayoutItem> AllLayoutItems()
    {
        var list = new List<IMdiLayoutItem>(_children.Count + _editorChildren.Count + _graphChildren.Count);
        list.AddRange(_children);
        list.AddRange(_editorChildren);
        list.AddRange(_graphChildren);
        return list;
    }

    /// <summary>
    /// Tile modes fill their slots in list order, so that order decides which side each window
    /// lands on. The rule the arrange buttons follow: side by side, terminals go on the right;
    /// stacked, terminals go on top. Creation order already puts terminals first, which is the
    /// top slot - only the left-right splits have to push them to the end.
    /// </summary>
    private static List<IMdiLayoutItem> TerminalsLast(List<IMdiLayoutItem> items)
    {
        var reordered = new List<IMdiLayoutItem>(items.Count);
        reordered.AddRange(items.Where(x => x is not MdiChildInfo));
        reordered.AddRange(items.Where(x => x is MdiChildInfo));
        return reordered;
    }

    private void ArrangeChildren()
    {
        double w = MdiContainer.Bounds.Width;
        double h = MdiContainer.Bounds.Height;
        if (w <= 0 || h <= 0) return;

        var items = AllLayoutItems();
        if (items.Count == 0) return;

        // A closed window leaves a stale front. Prefer the terminal CloseChild already picked as
        // the next active session, so closing a session still lands on its neighbour rather than
        // jumping to whichever file happens to be open.
        if (_activeLayoutItem == null || !items.Contains(_activeLayoutItem))
            _activeLayoutItem = _activeChildIndex >= 0 && _activeChildIndex < _children.Count
                ? _children[_activeChildIndex]
                : items[^1];

        switch (_layout)
        {
            case MdiLayout.Maximize:
                for (int i = 0; i < items.Count; i++)
                {
                    var c = items[i];
                    bool active = ReferenceEquals(c, _activeLayoutItem);
                    c.Container.IsVisible = active;
                    c.TitleBar.IsVisible = false;
                    if (active)
                    {
                        Canvas.SetLeft(c.Container, 0);
                        Canvas.SetTop(c.Container, 0);
                        c.Container.Width = w;
                        c.Container.Height = h;
                    }
                }
                break;

            case MdiLayout.Tile:
            {
                int count = items.Count;
                int cols = (int)Math.Ceiling(Math.Sqrt(count));
                int rows = (int)Math.Ceiling((double)count / cols);
                double cw = w / cols;
                double ch = h / rows;

                // A single-row grid is just a left-right split, so terminals belong on the right.
                // Taller grids keep them in the top row, where creation order already puts them.
                var tiled = rows == 1 ? TerminalsLast(items) : items;

                for (int i = 0; i < count; i++)
                {
                    var c = tiled[i];
                    c.Container.IsVisible = true;
                    c.TitleBar.IsVisible = false;
                    Canvas.SetLeft(c.Container, (i % cols) * cw);
                    Canvas.SetTop(c.Container, (i / cols) * ch);
                    c.Container.Width = cw;
                    c.Container.Height = ch;
                    c.Container.ZIndex = 0;
                }
                break;
            }

            case MdiLayout.TileHorizontal:
            {
                int count = items.Count;
                double ch = h / count;
                // Top-bottom split: terminals on top, which creation order already gives.
                for (int i = 0; i < count; i++)
                {
                    var c = items[i];
                    c.Container.IsVisible = true;
                    c.TitleBar.IsVisible = false;
                    Canvas.SetLeft(c.Container, 0);
                    Canvas.SetTop(c.Container, i * ch);
                    c.Container.Width = w;
                    c.Container.Height = ch;
                    c.Container.ZIndex = 0;
                }
                break;
            }

            case MdiLayout.TileVertical:
            {
                int count = items.Count;
                double cw = w / count;
                // Left-right split: editors on the left, terminals on the right.
                var tiled = TerminalsLast(items);
                for (int i = 0; i < count; i++)
                {
                    var c = tiled[i];
                    c.Container.IsVisible = true;
                    c.TitleBar.IsVisible = false;
                    Canvas.SetLeft(c.Container, i * cw);
                    Canvas.SetTop(c.Container, 0);
                    c.Container.Width = cw;
                    c.Container.Height = h;
                    c.Container.ZIndex = 0;
                }
                break;
            }

            case MdiLayout.Cascade:
            {
                double offset = 32;
                double cw = Math.Max(400, w * 0.75);
                double ch = Math.Max(300, h * 0.75);

                for (int i = 0; i < items.Count; i++)
                {
                    var c = items[i];
                    c.Container.IsVisible = true;
                    c.TitleBar.IsVisible = true;
                    Canvas.SetLeft(c.Container, i * offset);
                    Canvas.SetTop(c.Container, i * offset);
                    c.Container.Width = cw;
                    c.Container.Height = ch;
                    c.Container.ZIndex = i;
                }

                int activeIdx = items.FindIndex(x => ReferenceEquals(x, _activeLayoutItem));
                if (activeIdx >= 0)
                    items[activeIdx].Container.ZIndex = items.Count;
                break;
            }
        }

        UpdateStripSelection();
        RefreshWindowsPanel();
    }

    private void BringToFront(int index)
    {
        if (index < 0 || index >= _children.Count) return;
        _activeChildIndex = index;
        SetActiveLayoutItem(_children[index]);

        UpdateStripSelection();

        // The transcript readouts belong to the window being left behind. Repaint them from the
        // window taking over here rather than on the next poll, so the context meter cannot
        // spend a frame reporting the session the user just switched away from. Its monitor is
        // still parsed, so this is a swap rather than a re-read.
        _pendingModelLabel = null;   // a model picked in the old window says nothing about this one
        ApplySessionReadout();

        // Switch project context to match the active child
        var childFolder = _children[index].ProjectFolder;
        if (!string.Equals(childFolder, _projectFolder, StringComparison.OrdinalIgnoreCase))
        {
            _projectFolder = childFolder;
            _suppressFolderSelectionChanged = true;
            if (!string.IsNullOrEmpty(_projectFolder))
            {
                var items = CmbProjectFolder.ItemsSource as List<string>;
                if (items != null)
                {
                    int folderIdx = items.FindIndex(f => f.Equals(_projectFolder, StringComparison.OrdinalIgnoreCase));
                    if (folderIdx < 0)
                    {
                        // An isolated checkout is kept out of the recent list - it is scratch
                        // space belonging to a window, not a project anyone opened - but the box
                        // still has to name where the active window is working.
                        items.Insert(0, _projectFolder);
                        CmbProjectFolder.ItemsSource = null;
                        CmbProjectFolder.ItemsSource = items;
                        folderIdx = 0;
                    }
                    CmbProjectFolder.SelectedIndex = folderIdx;
                }
            }
            else
            {
                CmbProjectFolder.SelectedIndex = -1;
            }
            _suppressFolderSelectionChanged = false;

            RefreshGitInfo();
            RefreshSessionList();
            RefreshFileTree();
        }
    }

    private static readonly SolidColorBrush ActiveBorder = new(Color.FromRgb(0, 122, 255));   // Apple Blue
    private static readonly SolidColorBrush InactiveBorder = new(Color.FromArgb(40, 255, 255, 255));

    private void UpdateStripSelection()
    {
        for (int i = 0; i < _children.Count; i++)
        {
            var child = _children[i];
            // An editor window can hold the front, in which case no session tab is the active
            // one - _activeChildIndex still names the terminal commands are routed to.
            bool active = _activeLayoutItem == null
                ? i == _activeChildIndex
                : ReferenceEquals(child, _activeLayoutItem);
            PaintStripSelection(child.StripButton, child.Container, active);
        }

        foreach (var editor in _editorChildren)
            PaintStripSelection(editor.StripButton, editor.Container,
                ReferenceEquals(editor, _activeLayoutItem));

        foreach (var graph in _graphChildren)
            PaintStripSelection(graph.StripButton, graph.Container,
                ReferenceEquals(graph, _activeLayoutItem));
    }

    private static void PaintStripSelection(Button stripButton, Border container, bool active)
    {
        stripButton.Background = active
            ? new SolidColorBrush(Color.FromArgb(30, 0, 122, 255))
            : Brushes.Transparent;
        stripButton.BorderBrush = active
            ? new SolidColorBrush(Color.FromArgb(60, 0, 122, 255))
            : Brushes.Transparent;

        container.BorderBrush = active ? ActiveBorder : InactiveBorder;
        container.BorderThickness = active ? new Thickness(2) : new Thickness(1);
    }

    // ── Turn-end blink ──

    /// <summary>Border repaints in one blink: dark, blue, dark, blue.</summary>
    private const int BlinkPhases = 4;

    private DispatcherTimer? _blinkTimer;
    private Border? _blinkTarget;
    private int _blinkPhase;

    /// <summary>
    /// Blinks the active window's blue frame twice, to say the turn is over and the prompt is
    /// waiting. Only the brush changes: moving the border thickness would resize the terminal
    /// and push a resize down the pty.
    /// </summary>
    private void BlinkActiveFrame()
    {
        if (_activeChildIndex < 0 || _activeChildIndex >= _children.Count) return;

        _blinkTarget = _children[_activeChildIndex].Container;
        _blinkPhase = 0;

        if (_blinkTimer == null)
        {
            _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
            _blinkTimer.Tick += OnBlinkTick;
        }

        _blinkTimer.Stop();
        _blinkTimer.Start();
        OnBlinkTick(null, EventArgs.Empty);   // first phase now, not one interval late
    }

    private void OnBlinkTick(object? sender, EventArgs e)
    {
        // A window switch mid-blink leaves the old frame to UpdateStripSelection; drop out
        // rather than paint over whichever window is now selected.
        bool onTarget = _blinkTarget != null
                        && _activeChildIndex >= 0 && _activeChildIndex < _children.Count
                        && ReferenceEquals(_children[_activeChildIndex].Container, _blinkTarget);

        if (!onTarget || _blinkPhase >= BlinkPhases)
        {
            _blinkTimer?.Stop();
            _blinkTarget = null;
            UpdateStripSelection();   // hand the frame back to the selection colours
            return;
        }

        _blinkTarget!.BorderBrush = _blinkPhase % 2 == 0 ? InactiveBorder : ActiveBorder;
        _blinkPhase++;
    }

    // ── MDI Child management ──

    private void CreateNewChild(string command, string tabTitle, string? firstInput = null,
                                string? sessionId = null, WorktreeLease? worktree = null)
    {
        // An isolated session works in its checkout, and so does everything that follows the
        // active window: explorer, changed files, session list and the branch readout all
        // describe the tree the AI is actually editing.
        string? workFolder = worktree?.Path ?? _projectFolder;
        var terminal = new TerminalControl { IsDarkTheme = _isDark };
        ApplyProviderToTerminal(terminal);
        terminal.SetFont(_settings.FontFamily, _settings.FontSize);

        // --- Title bar ---
        var dot = new Ellipse
        {
            Width = 8, Height = 8,
            Fill = new SolidColorBrush(Color.FromRgb(48, 209, 88)),  // Apple Green
            VerticalAlignment = VerticalAlignment.Center
        };
        // Prefer firstInput (session summary) over generic tabTitle
        var initialTitle = !string.IsNullOrEmpty(firstInput) ? firstInput : tabTitle;
        var titleText = new TextBlock
        {
            Text = initialTitle,
            FontSize = 13,
            FontWeight = FontWeight.Normal,
            Foreground = new SolidColorBrush(Color.FromRgb(210, 210, 215)),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var closeBtn = new Button
        {
            Content = "\u00D7",
            FontSize = 14,
            Padding = new Thickness(6, 0),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(6),
            Cursor = new Cursor(StandardCursorType.Hand)
        };

        var titleLeft = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        titleLeft.Children.Add(dot);
        titleLeft.Children.Add(titleText);

        var titleGrid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        Grid.SetColumn(titleLeft, 0);
        Grid.SetColumn(closeBtn, 1);
        titleGrid.Children.Add(titleLeft);
        titleGrid.Children.Add(closeBtn);

        var titleBar = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(44, 44, 46)),  // Apple elevated surface
            Padding = new Thickness(0, 6),
            Child = titleGrid,
            Cursor = new Cursor(StandardCursorType.Hand),
            CornerRadius = new CornerRadius(0)
        };

        // --- Container ---
        var dockPanel = new DockPanel();
        DockPanel.SetDock(titleBar, Dock.Top);
        dockPanel.Children.Add(titleBar);
        dockPanel.Children.Add(terminal);

        var container = new Border
        {
            Child = dockPanel,
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            BorderThickness = new Thickness(0.5),
            CornerRadius = new CornerRadius(0),
            ClipToBounds = true,
            Background = new SolidColorBrush(Color.FromRgb(28, 28, 30))  // Apple systemBackground
        };

        // --- Window strip button ---
        var stripDot = new Ellipse
        {
            Width = 7, Height = 7,
            Fill = new SolidColorBrush(Color.FromRgb(48, 209, 88)),  // Apple Green
            VerticalAlignment = VerticalAlignment.Center
        };
        var stripText = new TextBlock
        {
            Text = initialTitle,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 120
        };
        var stripContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5
        };
        var stripCloseBtn = new Button
        {
            Content = "\u00D7",
            FontSize = 12,
            Padding = new Thickness(3, 0),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(3),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        stripContent.Children.Add(stripDot);
        stripContent.Children.Add(stripText);
        stripContent.Children.Add(stripCloseBtn);

        var stripButton = new Button
        {
            Content = stripContent,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 4),
            Cursor = new Cursor(StandardCursorType.Hand)
        };

        var entry = new MdiChildInfo(
            container, titleBar, titleText, dot, stripDot, terminal, stripButton, stripText
        )
        {
            ProjectFolder = workFolder,
            FirstInput = firstInput,
            SessionId = sessionId,
            Effort = StartingEffort(command),
            StartingModel = StartingModel(command),
            WorktreePath = worktree?.Path,
            WorktreeBranch = worktree?.Branch,
            WorktreeOrigin = worktree?.RepoRoot,
        };

        // Set FirstUserInput on terminal if provided (e.g. from resumed session)
        if (!string.IsNullOrEmpty(firstInput))
            terminal.FirstUserInput = firstInput;

        // --- Events ---
        closeBtn.Click += (_, _) => CloseChild(entry);
        stripCloseBtn.Click += (_, e) => { CloseChild(entry); e.Handled = true; };

        // Tab context menu (right-click)
        stripButton.ContextMenu = CreateTabContextMenu(entry);

        stripButton.Click += (_, _) =>
        {
            int idx = _children.IndexOf(entry);
            if (idx >= 0) BringToFront(idx);
        };

        // Double-click to rename tab
        stripButton.DoubleTapped += (_, ev) =>
        {
            var renameBg = new SolidColorBrush(_isDark ? Color.FromRgb(50, 50, 52) : Color.FromRgb(255, 255, 255));
            var renameFg = new SolidColorBrush(_isDark ? Color.FromRgb(220, 220, 225) : Color.FromRgb(28, 28, 30));
            var renameBorder = new SolidColorBrush(_isDark ? Color.FromRgb(80, 80, 85) : Color.FromRgb(180, 180, 185));
            var renameBox = new TextBox
            {
                Text = stripText.Text,
                FontSize = 11,
                MinWidth = 80,
                Padding = new Thickness(4, 2),
                Background = renameBg,
                Foreground = renameFg,
                BorderBrush = renameBorder,
                CaretBrush = renameFg,
                SelectionBrush = new SolidColorBrush(_isDark ? Color.FromArgb(90, 50, 120, 220) : Color.FromArgb(90, 0, 122, 255)),
                SelectionForegroundBrush = renameFg,
            };
            renameBox.KeyDown += (_, ke) =>
            {
                if (ke.Key == Key.Enter)
                {
                    var newName = renameBox.Text?.Trim();
                    if (!string.IsNullOrEmpty(newName))
                    {
                        stripText.Text = newName;
                        titleText.Text = newName;
                        terminal.IsManualTitle = true;
                    }
                    stripContent.Children.Remove(renameBox);
                    stripText.IsVisible = true;
                    ke.Handled = true;
                }
                else if (ke.Key == Key.Escape)
                {
                    stripContent.Children.Remove(renameBox);
                    stripText.IsVisible = true;
                    ke.Handled = true;
                }
            };
            renameBox.LostFocus += (_, _) =>
            {
                if (stripContent.Children.Contains(renameBox))
                {
                    stripContent.Children.Remove(renameBox);
                    stripText.IsVisible = true;
                }
            };
            stripText.IsVisible = false;
            stripContent.Children.Insert(1, renameBox);
            Dispatcher.UIThread.Post(() => { renameBox.Focus(); renameBox.SelectAll(); });
            ev.Handled = true;
        };

        container.PointerPressed += (_, _) =>
        {
            // Not "is this already the active session": an editor window can hold the front
            // while _activeChildIndex still names this terminal, and clicking it has to take
            // the front back.
            int idx = _children.IndexOf(entry);
            if (idx >= 0 && !ReferenceEquals(_activeLayoutItem, entry))
                BringToFront(idx);
        };

        // Drag on title bar (cascade mode)
        titleBar.PointerPressed += (_, e) =>
        {
            int idx = _children.IndexOf(entry);
            if (idx >= 0) BringToFront(idx);

            if (_layout == MdiLayout.Cascade)
            {
                _isDragging = true;
                _dragStart = e.GetPosition(MdiContainer);
                double left = Canvas.GetLeft(container);
                double top = Canvas.GetTop(container);
                _dragChildLeft = double.IsNaN(left) ? 0 : left;
                _dragChildTop = double.IsNaN(top) ? 0 : top;
                _dragChild = entry;
                e.Pointer.Capture(titleBar);
                e.Handled = true;
            }
        };
        titleBar.PointerMoved += (_, e) =>
        {
            if (_isDragging && _dragChild == entry)
            {
                var pos = e.GetPosition(MdiContainer);
                Canvas.SetLeft(container, _dragChildLeft + pos.X - _dragStart.X);
                Canvas.SetTop(container, _dragChildTop + pos.Y - _dragStart.Y);
                e.Handled = true;
            }
        };
        titleBar.PointerReleased += (_, e) =>
        {
            if (_isDragging && _dragChild == entry)
            {
                _isDragging = false;
                _dragChild = null;
                e.Pointer.Capture(null);
                e.Handled = true;
            }
        };

        // Snapshot the project just before each prompt, so it can be rolled back.
        terminal.PromptSubmitted += prompt => CaptureCheckpoint(entry, prompt);

        terminal.Clicked += () =>
        {
            // Not "is this already the active session": an editor window can hold the front
            // while _activeChildIndex still names this terminal, and clicking it has to take
            // the front back.
            int idx = _children.IndexOf(entry);
            if (idx >= 0 && !ReferenceEquals(_activeLayoutItem, entry))
                BringToFront(idx);
        };

        terminal.TitleChanged += title =>
        {
            if (terminal.IsManualTitle) return; // Manual title takes priority
            // Once the transcript has named the session, the screen stops being consulted -
            // otherwise an OSC title would drop the tab back to the opening prompt.
            if (!string.IsNullOrEmpty(entry.SessionTitle)) return;
            // Prefer session summary (FirstUserInput) over terminal OSC title
            var displayTitle = !string.IsNullOrEmpty(terminal.FirstUserInput)
                ? terminal.FirstUserInput
                : (string.IsNullOrWhiteSpace(title) ? tabTitle : title);
            titleText.Text = displayTitle;
            stripText.Text = displayTitle;
            RefreshWindowsPanel();
        };

        terminal.Exited += () =>
        {
            terminal.IsGenerating = false;
            PaintChildDots(entry);   // grey: the pty handle is already signalled here
            RefreshSessionList();
            // Flash the taskbar and raise a toast when the window is not focused
            if (!IsActive)
            {
                FlashTaskbar();
                if (_settings.NotifyOnComplete)
                {
                    _notifications.Notify(
                        NotifyKind.TaskComplete,
                        Loc.Get("TaskComplete"),
                        string.Format(Loc.Get("TaskCompleteFmt"), stripText.Text ?? _cli.Active.Name));
                }
            }
        };

        // Sync font size from Ctrl+Scroll zoom
        terminal.FontSizeChanged += newSize =>
        {
            _settings.FontSize = newSize;
            NumSettingsFontSize.Value = (decimal)newSize;
        };


        _children.Add(entry);
        _activeChildIndex = _children.Count - 1;
        MdiContainer.Children.Add(container);
        WindowStrip.Children.Add(stripButton);
        ArrangeChildren();
        SyncSessionSelection();

        // The explorer, changed files, session list and branch readout all follow the active
        // window. An isolated one works in a different tree, so they have to move with it.
        if (worktree != null) BringToFront(_children.Count - 1);

        Dispatcher.UIThread.Post(() =>
        {
            string cdPart = !string.IsNullOrEmpty(workFolder) && Directory.Exists(workFolder)
                ? $"cd /d \"{workFolder}\" && "
                : "";
            string fullCommand = $"cmd.exe /c chcp 65001 >nul && {cdPart}{command}";
            terminal.StartProcess(fullCommand, workFolder);
            terminal.FocusTerminal();
            // The new child is already active, so nothing else will refresh the status bar
            // for it: without this, Stop / Undo stay blank until the tab is clicked.

            if (string.IsNullOrEmpty(sessionId))
                _ = TrackSessionIdAsync(entry, DateTime.Now.AddSeconds(-2));
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// Learns the id of a session we started ourselves. The CLI only reveals it by creating its
    /// transcript, so the project's session folder is polled briefly after launch. Without this a
    /// saved workspace could only reopen blank sessions.
    /// </summary>
    private async Task TrackSessionIdAsync(MdiChildInfo entry, DateTime launchedAt)
    {
        var folder = entry.ProjectFolder;
        if (string.IsNullOrEmpty(folder) || !_cli.Features.SessionList) return;

        for (int i = 0; i < 20 && string.IsNullOrEmpty(entry.SessionId); i++)
        {
            await Task.Delay(1500);
            if (!_children.Contains(entry)) return;
            entry.SessionId = SessionService.FindSessionIdCreatedAfter(folder, launchedAt, TakenSessionIds(entry));
        }
    }

    /// <summary>
    /// Sessions the other windows are already running, so a window looking for its own does not
    /// settle on one of theirs. Two windows sharing an id would show each other's names.
    /// </summary>
    private HashSet<string> TakenSessionIds(MdiChildInfo except)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in _children)
            if (child != except && !string.IsNullOrEmpty(child.SessionId))
                taken.Add(child.SessionId!);
        return taken;
    }

    private async void CloseChild(MdiChildInfo entry)
    {
        // The wait below runs for up to three seconds, and the × stays clickable the whole time.
        // Without this guard a second click re-enters and tears the same window down twice.
        if (entry.IsClosing || !_children.Contains(entry)) return;
        entry.IsClosing = true;

        // Read while the window still knows which transcript is its own.
        SubagentMonitor.Forget(ResolveSessionPath(entry));

        // Asked before the teardown starts: the answer can be "keep the window", and by the
        // time the pty is gone that is no longer on offer. The removal itself has to wait -
        // the CLI's own process is sitting in that folder until it exits, and Windows will not
        // delete a directory that is some process's working directory.
        if (!await ConfirmWorktreeReleaseAsync(entry))
        {
            entry.IsClosing = false;
            return;
        }

        // Disposing the pty from under the wait surfaces here as ObjectDisposedException. This
        // method is async void, so letting it escape ends the process — the whole app disappears
        // while other windows are still open.
        try { await entry.Terminal.SendExitAndWaitAsync(); }
        catch (ObjectDisposedException) { }

        entry.Terminal.Dispose();
        MdiContainer.Children.Remove(entry.Container);
        WindowStrip.Children.Remove(entry.StripButton);

        // Resolve the position now rather than before the wait: windows open and close while a
        // slow /exit is in flight, so an index taken up front either drops the wrong window or
        // runs off the end of the list and takes the app down with it.
        int idx = _children.IndexOf(entry);
        if (idx < 0) return;
        _children.RemoveAt(idx);

        if (_children.Count == 0)
            _activeChildIndex = -1;
        else if (_activeChildIndex >= _children.Count)
            _activeChildIndex = _children.Count - 1;
        else if (idx <= _activeChildIndex && _activeChildIndex > 0)
            _activeChildIndex--;

        ArrangeChildren();
        // ArrangeChildren bails out when the last child is gone, so refresh separately or the
        // closed session's Stop / Undo linger in the status bar.

        // The checkout is only free once the terminal above has been disposed.
        await ReleaseWorktreeAsync(entry);
    }

    // ── Welcome Page ──

    private Border? _welcomeContainer;

    private async void ShowWelcomePage()
    {
        // Get recent project folders
        var recentFolders = await SessionService.GetRecentProjectFoldersAsync();

        // Theme-aware colors
        var titleFg = _isDark ? Color.FromRgb(220, 220, 225) : Color.FromRgb(30, 30, 35);
        var headerFg = _isDark ? Color.FromRgb(180, 180, 185) : Color.FromRgb(80, 80, 90);
        var pathFg = _isDark ? Color.FromArgb(140, 200, 200, 205) : Color.FromArgb(160, 80, 80, 90);
        var emptyFg = _isDark ? Color.FromArgb(100, 200, 200, 205) : Color.FromArgb(120, 80, 80, 90);
        var checkFg = _isDark ? Color.FromRgb(160, 160, 165) : Color.FromRgb(100, 100, 110);
        var pageBg = _isDark ? Color.FromRgb(30, 30, 32) : Color.FromRgb(246, 246, 248);
        var hoverBg = _isDark ? Color.FromArgb(30, 255, 255, 255) : Color.FromArgb(30, 0, 0, 0);

        // --- Build Welcome UI ---
        var titleText = new TextBlock
        {
            Text = Loc.Get("WelcomeTitle"),
            FontSize = 28,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(titleFg),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 24)
        };

        // "Start" section header
        var startHeader = new TextBlock
        {
            Text = Loc.Get("Start"),
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(headerFg),
            Margin = new Thickness(0, 0, 0, 8)
        };

        // New Project link
        var newProjectLink = CreateWelcomeLink(
            "M2 6C2 4.89 2.89 4 4 4H9L11 6H18C19.1 6 20 6.89 20 8V16C20 17.1 19.1 18 18 18H4C2.89 18 2 17.1 2 16V6Z",
            Loc.Get("NewProject"),
            Color.FromRgb(0, 122, 255));
        newProjectLink.PointerPressed += (_, _) => _ = PickNewProjectAsync();

        // Previous Project link
        var prevProjectLink = CreateWelcomeLink(
            "M12 4V1L8 5L12 9V6C15.31 6 18 8.69 18 12C18 13.01 17.75 13.97 17.3 14.8L18.76 16.26C19.54 15.03 20 13.57 20 12C20 7.58 16.42 4 12 4ZM12 18C8.69 18 6 15.31 6 12C6 10.99 6.25 10.03 6.7 9.2L5.24 7.74C4.46 8.97 4 10.43 4 12C4 16.42 7.58 20 12 20V23L16 19L12 15V18Z",
            Loc.Get("PreviousProject"),
            Color.FromRgb(48, 209, 88));
        if (!string.IsNullOrEmpty(_settings.ProjectFolder) && Directory.Exists(_settings.ProjectFolder))
        {
            prevProjectLink.PointerPressed += (_, _) => OpenProjectFromWelcome(_settings.ProjectFolder, true);
        }
        else
        {
            prevProjectLink.Opacity = 0.4;
            prevProjectLink.Cursor = Cursor.Default;
        }

        var startSection = new StackPanel { Spacing = 4 };
        startSection.Children.Add(startHeader);
        startSection.Children.Add(newProjectLink);
        startSection.Children.Add(prevProjectLink);

        // "Recent" section
        var recentHeader = new TextBlock
        {
            Text = Loc.Get("Recent"),
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(headerFg),
            Margin = new Thickness(0, 20, 0, 8)
        };

        var recentSection = new StackPanel { Spacing = 2 };
        recentSection.Children.Add(recentHeader);

        var count = 0;
        foreach (var folder in recentFolders)
        {
            if (count >= 10) break;
            if (!Directory.Exists(folder)) continue;

            var folderName = System.IO.Path.GetFileName(folder);
            var folderPath = folder;

            var nameText = new TextBlock
            {
                Text = folderName,
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(75, 156, 255)),
                VerticalAlignment = VerticalAlignment.Center
            };
            var pathText = new TextBlock
            {
                Text = folderPath,
                FontSize = 11,
                Foreground = new SolidColorBrush(pathFg),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var itemPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 0
            };
            itemPanel.Children.Add(nameText);
            itemPanel.Children.Add(pathText);

            var itemBorder = new Border
            {
                Child = itemPanel,
                Padding = new Thickness(8, 5),
                CornerRadius = new CornerRadius(4),
                Cursor = new Cursor(StandardCursorType.Hand),
                Background = Brushes.Transparent
            };
            AttachHoverEffect(itemBorder, hoverBg);

            var capturedPath = folder;
            itemBorder.PointerPressed += (_, _) => OpenProjectFromWelcome(capturedPath, true);

            recentSection.Children.Add(itemBorder);
            count++;
        }

        if (count == 0)
        {
            recentSection.Children.Add(new TextBlock
            {
                Text = "No recent projects",
                FontSize = 12,
                Foreground = new SolidColorBrush(emptyFg),
                Margin = new Thickness(8, 4)
            });
        }

        // Checkbox at bottom
        var showOnStartupCheck = new CheckBox
        {
            Content = Loc.Get("ShowWelcomeOnStartup"),
            IsChecked = _settings.ShowWelcomePage,
            Foreground = new SolidColorBrush(checkFg),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 30, 0, 0)
        };
        showOnStartupCheck.IsCheckedChanged += (_, _) =>
        {
            _settings.ShowWelcomePage = showOnStartupCheck.IsChecked == true;
            _settings.Save();
            // Sync with settings panel checkbox
            if (_settingsInitialized)
            {
                _suppressWelcomeCheckChanged = true;
                ChkShowWelcomePage.IsChecked = _settings.ShowWelcomePage;
                _suppressWelcomeCheckChanged = false;
            }
        };

        // Main content layout
        var contentPanel = new StackPanel
        {
            MaxWidth = 550,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(40)
        };
        contentPanel.Children.Add(titleText);
        contentPanel.Children.Add(startSection);
        contentPanel.Children.Add(recentSection);
        contentPanel.Children.Add(showOnStartupCheck);

        var scrollViewer = new ScrollViewer
        {
            Content = contentPanel,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };

        _welcomeContainer = new Border
        {
            Child = scrollViewer,
            Background = new SolidColorBrush(pageBg),
            ClipToBounds = true
        };

        MdiContainer.Children.Add(_welcomeContainer);

        // Fill the entire MDI area
        _welcomeContainer.SetValue(Canvas.LeftProperty, 0.0);
        _welcomeContainer.SetValue(Canvas.TopProperty, 0.0);
        _welcomeContainer.Width = MdiContainer.Bounds.Width;
        _welcomeContainer.Height = MdiContainer.Bounds.Height;
        MdiContainer.SizeChanged += WelcomePageResize;
    }

    private void WelcomePageResize(object? sender, SizeChangedEventArgs e)
    {
        if (_welcomeContainer != null)
        {
            _welcomeContainer.Width = MdiContainer.Bounds.Width;
            _welcomeContainer.Height = MdiContainer.Bounds.Height;
        }
    }

    private Border CreateWelcomeLink(string iconData, string text, Color iconColor)
    {
        var linkFg = _isDark ? Color.FromRgb(75, 156, 255) : Color.FromRgb(0, 100, 220);
        var hoverBg = _isDark ? Color.FromArgb(30, 255, 255, 255) : Color.FromArgb(30, 0, 0, 0);
        var icon = new PathIcon
        {
            Data = StreamGeometry.Parse(iconData),
            Width = 16,
            Height = 16,
            Foreground = new SolidColorBrush(iconColor)
        };
        var label = new TextBlock
        {
            Text = text,
            FontSize = 14,
            Foreground = new SolidColorBrush(linkFg),
            VerticalAlignment = VerticalAlignment.Center
        };
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10
        };
        panel.Children.Add(icon);
        panel.Children.Add(label);

        var border = new Border
        {
            Child = panel,
            Padding = new Thickness(8, 6),
            CornerRadius = new CornerRadius(4),
            Cursor = new Cursor(StandardCursorType.Hand),
            Background = Brushes.Transparent
        };
        AttachHoverEffect(border, hoverBg);

        return border;
    }

    /// <summary>
    /// Asks for a folder and opens it as the project. Shared by the welcome page's New Project
    /// link and the activity bar button, so the two cannot drift apart.
    /// </summary>
    private async Task PickNewProjectAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Loc.Get("SelectProjectFolder"),
            AllowMultiple = false
        });
        if (folders.Count > 0)
            OpenProjectFromWelcome(folders[0].Path.LocalPath);
    }

    private void OnActivityNewProject(object? sender, RoutedEventArgs e) => _ = PickNewProjectAsync();

    private async void OpenProjectFromWelcome(string folderPath, bool continueSession = false)
    {
        CloseWelcomePage();
        SetProjectFolder(folderPath);
        LoadRecentProjectFolders();
        if (continueSession)
        {
            var (command, resumed) = await BuildContinueLaunchAsync(folderPath);
            CreateNewChild(command, _cli.Active.Name, resumed?.DisplayTitle, resumed?.Id);
        }
        else
            LaunchClaudeWithInitialPrompt();
    }

    /// <summary>
    /// What a "continue" launch should actually run. <c>claude -c</c> continues the most recent
    /// session and exits on the spot when that one is already running elsewhere, so as soon as
    /// anything in this project is running, the newest free session is resumed by id instead.
    /// </summary>
    private async Task<(string Command, SessionInfo? Session)> BuildContinueLaunchAsync(string folderPath)
    {
        // Session summaries only exist for CLIs whose history Claucraft can read
        if (!_cli.Features.SessionList)
            return (_cli.BuildContinueCommand(ActiveLaunchProfile()), null);

        // "continue" picks up the most recently modified session: the top of the list
        var sessions = await SessionService.GetSessionsForProjectAsync(folderPath);
        var taken = RunningSessionService.LiveSessionIds(folderPath);

        // Nothing running, nothing to dodge: -c goes exactly where the CLI would take it.
        if (taken.Count == 0)
            return (_cli.BuildContinueCommand(ActiveLaunchProfile()), sessions.FirstOrDefault());

        // Otherwise the session is picked here rather than by -c, which walks past the ones
        // already running and can still land on one a background agent holds - a launch that dies
        // before its prompt. A window open here counts as taken even before the ledger says so.
        foreach (var child in _children)
            if (!string.IsNullOrEmpty(child.SessionId)) taken.Add(child.SessionId!);

        var free = sessions.FirstOrDefault(s => !taken.Contains(s.Id));
        if (free is null)
        {
            // Every session here is running somewhere, so a new one is all that can start.
            if (sessions.Count > 0)
                ShowMessageDialog(Loc.Get("SessionBusyTitle"), Loc.Get("SessionBusyAllBusy"));
            return (_cli.BuildNewCommand(null, ActiveLaunchProfile()), null);
        }

        // Skipping a session that is merely open in another window is routine and goes unsaid;
        // a background agent is the case worth explaining, since it is invisible from here.
        if (sessions.Count > 0 && RunningSessionService.IsHeldByAgent(sessions[0].Id, folderPath))
        {
            ShowMessageDialog(Loc.Get("SessionBusyTitle"), string.Format(
                Loc.Get("SessionBusyFallbackFmt"),
                string.IsNullOrEmpty(free.DisplayTitle) ? free.Id : free.DisplayTitle));
        }
        return (_cli.BuildResumeCommand(free.Id, ActiveLaunchProfile()), free);
    }

    private static void AttachHoverEffect(Border border, Color? hoverColor = null)
    {
        var hover = hoverColor ?? Color.FromArgb(30, 255, 255, 255);
        border.PointerEntered += (s, _) => ((Border)s!).Background = new SolidColorBrush(hover);
        border.PointerExited += (s, _) => ((Border)s!).Background = Brushes.Transparent;
    }

    private void CloseWelcomePage()
    {
        if (_welcomeContainer != null)
        {
            MdiContainer.Children.Remove(_welcomeContainer);
            MdiContainer.SizeChanged -= WelcomePageResize;
            _welcomeContainer = null;
        }
    }

    private async void LaunchClaudeWithInitialPrompt()
    {
        var worktree = await PrepareWorktreeAsync();
        CreateNewChild(
            _cli.BuildNewCommand(_settings.InitialPrompt, ActiveLaunchProfile()),
            _cli.Active.Name,
            worktree: worktree);
    }

    /// <summary>The launch profile new sessions start with, or null when the CLI defines none.</summary>
    private LaunchProfile? ActiveLaunchProfile() => _cli.FindProfile(_settings.ActiveProfileId);

    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        CloseWelcomePage();
        _settings.ProjectFolder = _projectFolder ?? "";
        _settings.Save();
        _snippetStore.Save();

        // Send /exit to all terminals and wait for graceful shutdown
        try
        {
            var exitTasks = _children.Select(c => c.Terminal.SendExitAndWaitAsync()).ToArray();
            await Task.WhenAll(exitTasks);
        }
        catch { }

        foreach (var child in _children)
        {
            child.Terminal.Dispose();
        }
        _usageTracker.Dispose();
        _rateLimits.Dispose();
        _fileWatcher?.Dispose();
        _notifications.Dispose();
        _checkpoints.Save();
    }
}
