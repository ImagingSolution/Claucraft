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
    private enum SidebarPanel { None, Explorer, Snippets, Settings, Windows, Changes, Slash }

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
        }
    }
    private string? _gitRepoUrl;
    private FileSystemWatcher? _fileWatcher;
    private DispatcherTimer? _fileWatcherDebounce;
    private DispatcherTimer? _gitInfoDebounce;
    private readonly UsageTracker _usageTracker = new();

    /// <summary>Marginal cost of the session in the active window. See SessionCostMonitor.</summary>
    private readonly SessionCostMonitor _costMonitor = new();

    /// <summary>The account's real 5-hour and 7-day limits. See RateLimitService.</summary>
    private readonly RateLimitService _rateLimits = new();

    /// <summary>
    /// The model the user just picked, held until the transcript confirms it. The name on the
    /// bar is read from the transcript, which only learns about a switch when the next reply
    /// lands - without this the bar would keep naming the old model until then.
    /// </summary>
    private string? _pendingModelAlias;
    private string? _pendingModelLabel;
    private List<LaunchProfile> _profiles = new();
    private bool _suppressProfileChange;
    private bool _wasWorking;
    private bool _costRefreshInFlight;

    /// <summary>Turn-end tracking for the frame blink. See NoteRunState.</summary>
    private bool _sawWorking;
    private int _idlePolls;

    /// <summary>Banner action that opens the hand-off flow rather than typing a slash command.</summary>
    private const string HandoffActionCommand = "claucraft:handoff";
    private bool _isDark = true;
    private MdiLayout _layout = MdiLayout.Maximize;
    private int _activeChildIndex = -1;
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
    private List<GitChange> _changes = new();
    private bool _changesLoading;

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

    private record MdiChildInfo(
        Border Container,
        Border TitleBar,
        TextBlock TitleText,
        Ellipse StatusDot,
        Ellipse StripDot,
        TerminalControl Terminal,
        Button StripButton,
        TextBlock StripText
    )
    {
        public string? ProjectFolder { get; set; }
        public string? FirstInput { get; set; }

        /// <summary>Session this window resumed, so the Session box can show it as selected.</summary>
        public string? SessionId { get; set; }

        /// <summary>
        /// Reasoning effort this window is running at. Effort never reaches the transcript, so
        /// this is the only record of it there is: seeded from the launch arguments, then moved
        /// by the status bar dropdown. A level typed straight into the terminal goes unseen.
        /// </summary>
        public string? Effort { get; set; }

        /// <summary>
        /// Set once CloseChild starts tearing this window down. The strip button lives on while
        /// the CLI takes its time quitting, so this is what keeps a second × click from running
        /// the teardown a second time.
        /// </summary>
        public bool IsClosing { get; set; }
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

        RefreshGitInfo();
        RefreshSessionList();
        RefreshFileTree();
        FileTree.SelectionChanged += OnFileTreeSelectionChanged;
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

        // Changed files, tokens & cost, and the live status readouts
        ToolTip.SetTip(BtnActivityChanges, Loc.Get("ChangesTooltip"));
        ToolTip.SetTip(StatusBranchName, Loc.Get("CommitGraphTooltip"));
        ToolTip.SetTip(BtnActivityCost, Loc.Get("CostTooltip"));
        ToolTip.SetTip(BtnRefreshChanges, Loc.Get("Refresh"));
        ToolTip.SetTip(StatusModeBadge, Loc.Get("ModeBadgeTooltip"));
        ToolTip.SetTip(StatusContextPanel, Loc.Get("ContextMeterTooltip"));
        ToolTip.SetTip(BtnBannerDismiss, Loc.Get("Dismiss"));
        LblLiveStatus.Text = Loc.Get("LiveStatus");
        ChkEnableLiveStatus.Content = Loc.Get("EnableLiveStatus");
        ChkEnableErrorBanner.Content = Loc.Get("EnableErrorBanner");
        LblPlanTier.Text = Loc.Get("PlanTier");
        LblOpenCostDashboard.Text = Loc.Get("CostDashboard");
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
        CmbSessions.IsVisible = features.SessionList;
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
    /// Window title, e.g. "[Claude Code] Claucraft Ver.0.1.12.244". Called from
    /// ApplyProviderUi() so it follows both a language change and an AI switch.
    /// </summary>
    private void UpdateWindowTitle()
    {
        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        var verStr = ver != null ? $"Ver.{ver.Major}.{ver.Minor}.{ver.Build}.{ver.Revision}" : "";
        Title = $"[{_cli.Active.Name}] {Loc.Get("AppTitle")} {verStr}";
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
        ChangesPanel.IsVisible = panel == SidebarPanel.Changes;
        SlashPanel.IsVisible = panel == SidebarPanel.Slash;
        SidePanelTitle.Text = panel switch
        {
            SidebarPanel.Explorer => Loc.Get("EXPLORER"),
            SidebarPanel.Settings => Loc.Get("SETTINGS"),
            SidebarPanel.Snippets => Loc.Get("SNIPPETS"),
            SidebarPanel.Windows => Loc.Get("WINDOWS"),
            SidebarPanel.Changes => Loc.Get("CHANGES"),
            SidebarPanel.Slash => Loc.Get("SLASH"),
            _ => ""
        };
        BtnBrowseFolder.IsVisible = panel == SidebarPanel.Explorer;
        if (panel == SidebarPanel.Windows)
            RefreshWindowsPanel();
        if (panel == SidebarPanel.Changes)
            RefreshChangesPanel();
        if (panel == SidebarPanel.Slash)
            RefreshSlashPanel();
    }

    private void UpdateActivityBarHighlight()
    {
        SetActivityButtonActive(BtnActivityExplorer, _activeSidePanel == SidebarPanel.Explorer);
        SetActivityButtonActive(BtnActivitySnippets, _activeSidePanel == SidebarPanel.Snippets);
        SetActivityButtonActive(BtnActivitySettings, _activeSidePanel == SidebarPanel.Settings);
        SetActivityButtonActive(BtnActivityWindows, _activeSidePanel == SidebarPanel.Windows);
        SetActivityButtonActive(BtnActivityChanges, _activeSidePanel == SidebarPanel.Changes);
        SetActivityButtonActive(BtnActivitySlash, _activeSidePanel == SidebarPanel.Slash);
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
            bool isActive = i == _activeChildIndex;
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

            // Show summary (FirstUserInput) if available, otherwise fall back to tab title
            var displayText = !string.IsNullOrEmpty(child.Terminal.FirstUserInput)
                ? child.Terminal.FirstUserInput
                : child.StripText.Text ?? _cli.Active.Name;

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
        }
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

    private void OnFileTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var node = FileTree.SelectedItem as FileTreeNode;
        if (node == null || node.IsDirectory)
        {
            FilePreviewBorder.IsVisible = false;
            return;
        }
        try
        {
            var ext = System.IO.Path.GetExtension(node.FullPath).ToLowerInvariant();
            var textExts = new HashSet<string> { ".cs", ".txt", ".md", ".json", ".xml", ".axaml", ".xaml",
                ".js", ".ts", ".tsx", ".jsx", ".html", ".css", ".py", ".go", ".rs", ".java", ".yml", ".yaml",
                ".toml", ".sh", ".bash", ".ps1", ".sql", ".gitignore", ".csproj", ".sln", ".config", ".log" };
            if (!textExts.Contains(ext)) { FilePreviewBorder.IsVisible = false; return; }

            var lines = System.IO.File.ReadLines(node.FullPath).Take(30);
            FilePreviewText.Text = string.Join("\n", lines);
            FilePreviewBorder.IsVisible = true;
        }
        catch { FilePreviewBorder.IsVisible = false; }
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

    /// <summary>Opens the commit graph for the active project, the way Git Graph does in VS Code.</summary>
    private void OnBranchNameDoubleTapped(object? sender, TappedEventArgs e)
    {
        e.Handled = true;

        if (string.IsNullOrEmpty(_projectFolder) || !GitChangeService.IsGitRepository(_projectFolder))
            return;

        var typeface = new Typeface(_settings.FontFamily + ", Consolas, Courier New");
        new Controls.CommitGraphWindow(_projectFolder, StatusRepoName.Text ?? "", _isDark, typeface).Show(this);
    }

    private void RefreshGitInfo()
    {
        StatusRepoName.Text = "";
        StatusBranchName.Text = "";
        _gitRepoUrl = null;

        if (string.IsNullOrEmpty(_projectFolder) || !Directory.Exists(_projectFolder))
            return;

        try
        {
            // Get remote origin URL -> extract repo name
            var remoteInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "remote get-url origin",
                WorkingDirectory = _projectFolder,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var remoteProc = Process.Start(remoteInfo);
            var remoteUrl = remoteProc?.StandardOutput.ReadToEnd().Trim() ?? "";
            remoteProc?.WaitForExit();

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
            var branchInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "rev-parse --abbrev-ref HEAD",
                WorkingDirectory = _projectFolder,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var branchProc = Process.Start(branchInfo);
            var branch = branchProc?.StandardOutput.ReadToEnd().Trim() ?? "";
            branchProc?.WaitForExit();

            if (!string.IsNullOrEmpty(branch))
                StatusBranchName.Text = branch;
        }
        catch { }

        // Keep the changed-files list on the project the status bar just switched to.
        if (ChangesPanel.IsVisible) RefreshChangesPanel();
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

        if (string.IsNullOrEmpty(_projectFolder) || !Directory.Exists(_projectFolder))
        {
            CmbSessions.ItemsSource = null;
            BtnResumeSession.IsEnabled = false;
            return;
        }

        CmbSessions.ItemsSource = await SessionService.GetSessionsForProjectAsync(_projectFolder);
        SyncSessionSelection();
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

    private void OnResumeSession(object? sender, RoutedEventArgs e)
    {
        // CLIs without a readable session index fall back to "continue most recent"
        if (!_cli.Features.SessionList)
        {
            CreateNewChild(_cli.BuildContinueCommand(), _cli.Active.Name);
            return;
        }

        if (CmbSessions.SelectedItem is SessionInfo session)
        {
            string cmd = _cli.BuildResumeCommand(session.Id);
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

    private void OnCloseTab(object? sender, RoutedEventArgs e)
    {
        if (_activeChildIndex >= 0 && _activeChildIndex < _children.Count)
        {
            CloseChild(_children[_activeChildIndex]);
        }
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
            if (_activeChildIndex >= 0 && _activeChildIndex < _children.Count)
                CloseChild(_children[_activeChildIndex]);
            e.Handled = true;
            return;
        }
        // Ctrl+Tab / Ctrl+Shift+Tab: Switch tabs
        if (e.Key == Key.Tab && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (_children.Count > 1)
            {
                int dir = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1;
                int next = (_activeChildIndex + dir + _children.Count) % _children.Count;
                BringToFront(next);
                _children[next].Terminal.FocusTerminal();
            }
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
            var terminal = _children[i].Terminal;
            try
            {
                terminal.IsGenerating = i == _activeChildIndex
                    ? _insight.IsWorking
                    : TerminalInsight.IsWorking(terminal.GetScreenText(0));
            }
            catch
            {
                // The pty read thread writes the buffer this walks - same race the active
                // child's read already lives with. Skip this child until the next poll.
            }
        }
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

    private void ClearLiveStatus()
    {
        // The badge stays. With the activity-bar button gone it is the only mode switch in the
        // UI, so it has to outlive the readout toggles as well as an unreadable mode name.
        ApplyModeBadge(null);
        StatusContextPanel.IsVisible = false;
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
    }

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
        var session = _costMonitor.Current;
        bool showContext = _cli.Features.CompactButton && session.HasData && session.ContextTokens > 0;
        StatusContextPanel.IsVisible = showContext;
        if (!showContext) return;

        // Reported as context used rather than context left. The two rate-limit meters beside
        // it fill as their window is spent, and a row of bars where one grows the opposite way
        // to its neighbours is read wrong at a glance however it is labelled.
        int used = SessionCostMonitor.ContextUsedPercent(session.ContextTokens, session.Model);
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
        else if (_settings.EnableLiveStatus && _cli.Features.CompactButton
                 && snap.ContextRemainingPercent is int left
                 && left <= _settings.HandoffBannerThreshold)
        {
            key = "compact";
            var cost = _costMonitor.Current;

            // Handing off is the cheaper of the two ways out, so it gets the button. /compact
            // stays one click away on the context meter itself.
            if (cost.HasData)
            {
                title = Loc.Get("HandoffTitle");
                detail = string.Format(Loc.Get("HandoffDetailFormat"), left, FormatUsd(cost.NextTurnUsd));
            }
            else
            {
                title = Loc.Get("ContextLowTitle");
                detail = string.Format(Loc.Get("ContextLowDetail"), left);
            }
            actionLabel = Loc.Get("HandoffAction");
            actionCommand = HandoffActionCommand;
            accent = Color.FromRgb(255, 214, 10);
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
        InfoBanner.Background = new SolidColorBrush(accent, 0.10);
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

    // ── Changed Files ──

    private void OnActivityChanges(object? sender, RoutedEventArgs e)
    {
        ToggleSidePanel(SidebarPanel.Changes);
    }

    private void OnRefreshChanges(object? sender, RoutedEventArgs e)
    {
        RefreshChangesPanel();
    }

    private async void RefreshChangesPanel()
    {
        if (_changesLoading || !ChangesPanel.IsVisible) return;

        var repo = _projectFolder;
        ChangesList.Children.Clear();

        if (string.IsNullOrEmpty(repo) || !GitChangeService.IsGitRepository(repo))
        {
            LblChangesSummary.Text = Loc.Get("NotAGitRepo");
            return;
        }

        _changesLoading = true;
        LblChangesSummary.Text = Loc.Get("LoadingChanges");
        try
        {
            _changes = await GitChangeService.GetChangesAsync(repo);
        }
        catch
        {
            _changes = new List<GitChange>();
        }
        finally
        {
            _changesLoading = false;
        }

        // The user may have switched panels or projects while git was running.
        if (!ChangesPanel.IsVisible || !string.Equals(repo, _projectFolder, StringComparison.OrdinalIgnoreCase))
            return;

        ChangesList.Children.Clear();
        LblChangesSummary.Text = _changes.Count == 0
            ? Loc.Get("NoChanges")
            : string.Format(Loc.Get("ChangedFilesCount"), _changes.Count);

        foreach (var change in _changes)
            ChangesList.Children.Add(BuildChangeRow(repo, change));
    }

    private Control BuildChangeRow(string repo, GitChange change)
    {
        var glyphColor = change.StatusGlyph switch
        {
            "A" => Color.FromRgb(48, 209, 88),
            "D" => Color.FromRgb(255, 69, 58),
            "R" => Color.FromRgb(10, 132, 255),
            "?" => Color.FromRgb(142, 142, 147),
            _ => Color.FromRgb(255, 214, 10),
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };

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
        Grid.SetColumn(glyph, 0);
        Grid.SetColumn(name, 1);
        Grid.SetColumn(dir, 2);
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
        row.PointerPressed += (_, _) => ShowDiff(repo, change);
        return row;
    }

    private async void ShowDiff(string repo, GitChange change)
    {
        string diff;
        try
        {
            diff = await GitChangeService.GetDiffAsync(repo, change);
        }
        catch
        {
            diff = "";
        }

        if (string.IsNullOrWhiteSpace(diff))
        {
            ShowMessageDialog(Loc.Get("Diff"), Loc.Get("DiffEmpty"));
            return;
        }

        var typeface = new Typeface(_settings.FontFamily + ", Consolas, Courier New");
        new Controls.DiffWindow(change.Path, diff, _isDark, typeface).Show(this);
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
        var commands = new List<(string Name, string Shortcut, Action Execute)>
        {
            ("New Session", "Ctrl+N", () => LaunchClaudeWithInitialPrompt()),
            ("Changed Files", "", () => ToggleSidePanel(SidebarPanel.Changes)),
            ("Tokens & Cost", "", () => new Controls.CostDashboardWindow(_isDark, _projectFolder).Show(this)),
            ("Close Tab", "Ctrl+W", () => { if (_activeChildIndex >= 0 && _activeChildIndex < _children.Count) CloseChild(_children[_activeChildIndex]); }),
            ("Next Tab", "Ctrl+Tab", () => { if (_children.Count > 1) BringToFront((_activeChildIndex + 1) % _children.Count); }),
            ("Previous Tab", "Ctrl+Shift+Tab", () => { if (_children.Count > 1) BringToFront((_activeChildIndex - 1 + _children.Count) % _children.Count); }),
            ("Toggle Explorer", "Ctrl+Shift+E", () => ToggleSidePanel(SidebarPanel.Explorer)),
            ("Toggle Snippets", "", () => ToggleSidePanel(SidebarPanel.Snippets)),
            ("Toggle Settings", "", () => ToggleSidePanel(SidebarPanel.Settings)),
            ("Tile Windows", "", () => { _layout = MdiLayout.Tile; ArrangeChildren(); }),
            ("Cascade Windows", "", () => { _layout = MdiLayout.Cascade; ArrangeChildren(); }),
            ("Tile Horizontally", "", () => { _layout = MdiLayout.TileHorizontal; ArrangeChildren(); }),
            ("Tile Vertically", "", () => { _layout = MdiLayout.TileVertical; ArrangeChildren(); }),
            ("Full View", "", () => { _layout = MdiLayout.Maximize; ArrangeChildren(); }),
            ("Compact (/compact)", "", () => OnActivityCompact(null, null!)),
            ("Switch Mode (Shift+Tab)", "", SendModeSwitch),
            ("Save Workspace", "", SaveWorkspace),
            ("Save Workspace As...", "", () => _ = PromptSaveWorkspaceAsync()),
            ("Restore Workspace", "", () => RestoreWorkspace()),
            ("Workspaces...", "", ShowWorkspaceList),
            ("Slash Commands", "Ctrl+/", ToggleSlashPanel),
            ("Checkpoints", "", ShowCheckpointList),
            ("Stop", "Esc", () => OnStopTask(null, null!)),
            ("Usage Chart", "", () => new UsageChartWindow().Show(this)),
            ("Setup Check", "", () => _ = ShowSetupDoctorAsync()),
            ("Keyboard Shortcuts", "F1", ShowShortcutSheet),
            ("Command Palette", "Ctrl+Shift+P", ShowCommandPalette),
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
                if (!string.IsNullOrEmpty(filter) &&
                    !cmd.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    continue;

                var nameText = new TextBlock { Text = cmd.Name, FontSize = 13, Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 225)) };
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
    /// Transcript backing the active window. Prefers the session the window actually resumed;
    /// falls back to the newest transcript in the project, which can belong to a sibling window
    /// when several share one folder and none has reported its id yet.
    /// </summary>
    private string? ResolveActiveSessionPath()
    {
        if (_activeChildIndex < 0 || _activeChildIndex >= _children.Count) return null;

        var child = _children[_activeChildIndex];
        var folder = string.IsNullOrEmpty(child.ProjectFolder) ? _projectFolder : child.ProjectFolder;
        if (string.IsNullOrEmpty(folder)) return null;

        if (!string.IsNullOrEmpty(child.SessionId))
        {
            var byId = SessionMessageReader.FindSessionFile(folder, child.SessionId!);
            if (byId != null) return byId;
        }

        return SessionMessageReader.FindMostRecentSession(folder);
    }

    /// <summary>
    /// Keeps the session readout - which model is answering, and how long the session has been
    /// running - in step with the active transcript. New usage only reaches the file when a turn
    /// ends, so it is re-read on that edge rather than every tick.
    /// </summary>
    private async void RefreshSessionReadout(TerminalSnapshot snap)
    {
        if (!_cli.Features.CompactButton)
        {
            ClearSessionReadout();
            return;
        }

        string? path = ResolveActiveSessionPath();
        if (path == null)
        {
            ClearSessionReadout();
            return;
        }

        bool attaching = !string.Equals(_costMonitor.Path, path, StringComparison.OrdinalIgnoreCase);
        if (attaching)
        {
            _pendingModelAlias = null;
            _pendingModelLabel = null;
        }
        _costMonitor.Track(path);

        bool turnEnded = _wasWorking && !snap.IsWorking;
        _wasWorking = snap.IsWorking;

        if (!attaching && !turnEnded)
        {
            ApplySessionReadout();
            return;
        }

        if (_costRefreshInFlight) return;
        _costRefreshInFlight = true;
        try { await _costMonitor.RefreshAsync(); }
        catch { /* a transcript that cannot be read just leaves the readout as it was */ }
        finally { _costRefreshInFlight = false; }

        ApplySessionReadout();
    }

    private void ClearSessionReadout()
    {
        StatusModelName.IsVisible = false;
        ApplyEffortReadout();
    }

    /// <summary>The model that is answering, and the effort it is answering at.</summary>
    private void ApplySessionReadout()
    {
        var session = _costMonitor.Current;

        var model = SessionCostMonitor.ModelDisplayName(session.Model);

        // A switch only reaches the transcript with the next reply, so the picked name stands
        // in until then. The alias is enough to recognise: every id in a line contains it
        // ("opus" in "claude-opus-5").
        if (_pendingModelAlias != null)
        {
            if (session.Model.Contains(_pendingModelAlias, StringComparison.OrdinalIgnoreCase))
            {
                _pendingModelAlias = null;
                _pendingModelLabel = null;
            }
            else if (_pendingModelLabel != null)
            {
                model = _pendingModelLabel;
            }
        }

        StatusModelName.IsVisible = model.Length > 0;
        if (model.Length > 0)
        {
            StatusModelText.Text = model;
            ToolTip.SetTip(StatusModelName, Loc.Get("ModelTooltip"));
        }

        ApplyEffortReadout();
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
        ("fable", "claude-fable-5"),
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

        if (alias == null || label == null) return;

        _pendingModelAlias = alias;
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

    private void RestoreWorkspace(string? name = null)
    {
        var ws = WorkspaceService.Load(name);
        if (ws == null || ws.Tabs.Count == 0) return;

        if (Enum.TryParse<MdiLayout>(ws.Layout, out var layout))
            _layout = layout;

        foreach (var tab in ws.Tabs)
        {
            if (!string.IsNullOrEmpty(tab.ProjectFolder) && Directory.Exists(tab.ProjectFolder))
                _projectFolder = tab.ProjectFolder;

            // Resume the exact transcript when the CLI can address one; otherwise open a new session.
            bool canResume = !string.IsNullOrEmpty(tab.SessionId)
                             && tab.ProviderId == _cli.ActiveId
                             && _cli.Features.SessionList;

            if (canResume)
                CreateNewChild(_cli.BuildResumeCommand(tab.SessionId), tab.TabTitle, tab.TabTitle, tab.SessionId);
            else
                CreateNewChild(_cli.BuildNewCommand(_settings.InitialPrompt, ActiveLaunchProfile()), tab.TabTitle);

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

    private Task<string?> ShowTextInputDialog(string title, string watermark, string initial)
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
            Content = Loc.Get("Save"),
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

    private void ArrangeChildren()
    {
        double w = MdiContainer.Bounds.Width;
        double h = MdiContainer.Bounds.Height;
        if (w <= 0 || h <= 0) return;
        if (_children.Count == 0) return;

        if (_activeChildIndex < 0 || _activeChildIndex >= _children.Count)
            _activeChildIndex = _children.Count - 1;

        switch (_layout)
        {
            case MdiLayout.Maximize:
                for (int i = 0; i < _children.Count; i++)
                {
                    var c = _children[i];
                    bool active = i == _activeChildIndex;
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
                int count = _children.Count;
                int cols = (int)Math.Ceiling(Math.Sqrt(count));
                int rows = (int)Math.Ceiling((double)count / cols);
                double cw = w / cols;
                double ch = h / rows;

                for (int i = 0; i < count; i++)
                {
                    var c = _children[i];
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
                int count = _children.Count;
                double ch = h / count;
                for (int i = 0; i < count; i++)
                {
                    var c = _children[i];
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
                int count = _children.Count;
                double cw = w / count;
                for (int i = 0; i < count; i++)
                {
                    var c = _children[i];
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

                for (int i = 0; i < _children.Count; i++)
                {
                    var c = _children[i];
                    c.Container.IsVisible = true;
                    c.TitleBar.IsVisible = true;
                    Canvas.SetLeft(c.Container, i * offset);
                    Canvas.SetTop(c.Container, i * offset);
                    c.Container.Width = cw;
                    c.Container.Height = ch;
                    c.Container.ZIndex = i;
                }

                if (_activeChildIndex >= 0 && _activeChildIndex < _children.Count)
                    _children[_activeChildIndex].Container.ZIndex = _children.Count;
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

        if (_layout == MdiLayout.Cascade)
        {
            for (int i = 0; i < _children.Count; i++)
                _children[i].Container.ZIndex = (i == index ? _children.Count : i);
        }
        else if (_layout == MdiLayout.Maximize)
        {
            ArrangeChildren();
        }

        UpdateStripSelection();
        ApplyEffortReadout();

        // The transcript readouts belong to the window that was active until now. Drop them here
        // rather than on the next poll, so the context meter cannot spend a frame reporting the
        // window the user just left.
        _costMonitor.Track(null);

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
                    CmbProjectFolder.SelectedIndex = folderIdx >= 0 ? folderIdx : -1;
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
            bool active = i == _activeChildIndex;

            child.StripButton.Background = active
                ? new SolidColorBrush(Color.FromArgb(30, 0, 122, 255))
                : Brushes.Transparent;
            child.StripButton.BorderBrush = active
                ? new SolidColorBrush(Color.FromArgb(60, 0, 122, 255))
                : Brushes.Transparent;

            child.Container.BorderBrush = active ? ActiveBorder : InactiveBorder;
            child.Container.BorderThickness = active ? new Thickness(2) : new Thickness(1);
        }
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

    private void CreateNewChild(string command, string tabTitle, string? firstInput = null, string? sessionId = null)
    {
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
            ProjectFolder = _projectFolder,
            FirstInput = firstInput,
            SessionId = sessionId,
            Effort = StartingEffort(command)
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
            int idx = _children.IndexOf(entry);
            if (idx >= 0 && _activeChildIndex != idx)
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
            int idx = _children.IndexOf(entry);
            if (idx >= 0 && _activeChildIndex != idx)
                BringToFront(idx);
        };

        terminal.TitleChanged += title =>
        {
            if (terminal.IsManualTitle) return; // Manual title takes priority
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
            dot.Fill = new SolidColorBrush(Color.FromRgb(142, 142, 147));   // Apple systemGray
            stripDot.Fill = new SolidColorBrush(Color.FromRgb(142, 142, 147));
            terminal.IsGenerating = false;
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

        Dispatcher.UIThread.Post(() =>
        {
            string cdPart = !string.IsNullOrEmpty(_projectFolder) && Directory.Exists(_projectFolder)
                ? $"cd /d \"{_projectFolder}\" && "
                : "";
            string fullCommand = $"cmd.exe /c chcp 65001 >nul && {cdPart}{command}";
            terminal.StartProcess(fullCommand, _projectFolder);
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
            entry.SessionId = SessionService.FindSessionIdCreatedAfter(folder, launchedAt);
        }
    }

    private async void CloseChild(MdiChildInfo entry)
    {
        // The wait below runs for up to three seconds, and the × stays clickable the whole time.
        // Without this guard a second click re-enters and tears the same window down twice.
        if (entry.IsClosing || !_children.Contains(entry)) return;
        entry.IsClosing = true;

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
        newProjectLink.PointerPressed += async (_, _) =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = Loc.Get("SelectProjectFolder"),
                AllowMultiple = false
            });
            if (folders.Count > 0)
                OpenProjectFromWelcome(folders[0].Path.LocalPath);
        };

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

    private async void OpenProjectFromWelcome(string folderPath, bool continueSession = false)
    {
        CloseWelcomePage();
        SetProjectFolder(folderPath);
        LoadRecentProjectFolders();
        if (continueSession)
        {
            // Session summaries only exist for CLIs whose history Claucraft can read
            SessionInfo? resumed = null;
            if (_cli.Features.SessionList)
            {
                // "continue" picks up the most recently modified session: the top of the list
                var sessions = await SessionService.GetSessionsForProjectAsync(folderPath);
                resumed = sessions.FirstOrDefault();
            }
            CreateNewChild(_cli.BuildContinueCommand(), _cli.Active.Name, resumed?.DisplayTitle, resumed?.Id);
        }
        else
            LaunchClaudeWithInitialPrompt();
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

    private void LaunchClaudeWithInitialPrompt()
    {
        CreateNewChild(_cli.BuildNewCommand(_settings.InitialPrompt, ActiveLaunchProfile()), _cli.Active.Name);
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
