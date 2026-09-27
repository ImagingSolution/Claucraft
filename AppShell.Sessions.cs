using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Shapes;
using Claucraft.Services;

namespace Claucraft;

/// <summary>
/// The session sidebar: every session of the current project under the open windows, with where
/// each one stands, a search box, names and an archive that live in Claucraft's own settings.
/// A click switches to the window running the session, or resumes it in a new one.
/// </summary>
internal partial class AppShell
{
    /// <summary>More rows than this and the search box is the better way in.</summary>
    private const int SidebarSessionLimit = 100;

    private List<SessionInfo> _sidebarSessions = new();
    private string? _sidebarFolder;
    private TextBlock? _sessionsHeader;
    private TextBox? _sessionsSearch;
    private Button? _sessionsArchiveToggle;
    private StackPanel? _sessionsList;
    private bool _showArchivedSessions;

    private static readonly SolidColorBrush DotElsewhereBrush = new(Color.FromRgb(10, 132, 255));
    private static readonly SolidColorBrush DotAskingBrush = new(Color.FromRgb(191, 90, 242));

    private void SetSidebarSessions(List<SessionInfo> sessions, string folder)
    {
        _sidebarSessions = sessions;
        _sidebarFolder = folder;
        RefreshSessionsSection();
    }

    /// <summary>
    /// Builds the header and search box once, so typing is not interrupted by the panel's
    /// frequent rebuilds; only the rows below them are redrawn.
    /// </summary>
    private void EnsureSessionsSection()
    {
        if (_sessionsList != null) return;

        _sessionsHeader = new TextBlock
        {
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            LetterSpacing = 1,
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _sessionsArchiveToggle = SmallLinkButton("");
        _sessionsArchiveToggle.Click += (_, _) =>
        {
            _showArchivedSessions = !_showArchivedSessions;
            RefreshSessionsSection();
        };
        var refresh = SmallLinkButton("↻");
        ToolTip.SetTip(refresh, Loc.Get("SessionsRefresh"));
        refresh.Click += (_, _) => RefreshSessionList();

        var header = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,Auto"), Margin = new Thickness(8, 6, 4, 2) };
        Grid.SetColumn(_sessionsArchiveToggle, 1);
        Grid.SetColumn(refresh, 2);
        header.Children.Add(_sessionsHeader);
        header.Children.Add(_sessionsArchiveToggle);
        header.Children.Add(refresh);

        _sessionsSearch = new TextBox
        {
            PlaceholderText = Loc.Get("SessionsSearch"),
            FontSize = 12,
            Padding = new Thickness(8, 4),
            Margin = new Thickness(6, 2, 6, 4),
            CornerRadius = new CornerRadius(4),
        };
        _sessionsSearch.TextChanged += (_, _) => RefreshSessionsSection();

        _sessionsList = new StackPanel { Spacing = 2 };

        SessionsSection.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(6, 4),
            Background = new SolidColorBrush(_isDark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(30, 0, 0, 0)),
        });
        SessionsSection.Children.Add(header);
        SessionsSection.Children.Add(_sessionsSearch);
        SessionsSection.Children.Add(_sessionsList);
    }

    private static Button SmallLinkButton(string text) => new()
    {
        Content = text,
        FontSize = 11,
        Padding = new Thickness(6, 1),
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Opacity = 0.75,
        Cursor = new Cursor(StandardCursorType.Hand),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private void RefreshSessionsSection()
    {
        if (!WindowsPanel.IsVisible) return;

        bool usable = _cli.Features.SessionList
            && string.Equals(_sidebarFolder, _projectFolder, StringComparison.OrdinalIgnoreCase);
        SessionsSection.IsVisible = _cli.Features.SessionList;
        if (!SessionsSection.IsVisible) return;

        EnsureSessionsSection();
        var sessions = usable ? _sidebarSessions : new List<SessionInfo>();
        var archived = new HashSet<string>(_settings.ArchivedSessions, StringComparer.OrdinalIgnoreCase);
        var query = _sessionsSearch!.Text?.Trim() ?? "";

        var visible = sessions
            .Where(s => _showArchivedSessions || !archived.Contains(s.Id))
            .Where(s => query.Length == 0 || MatchesSidebarSession(s, query))
            .ToList();

        _sessionsHeader!.Text = Loc.Get("SessionsHeader") + "  (" + visible.Count + ")";
        _sessionsArchiveToggle!.Content = Loc.Get(_showArchivedSessions ? "SessionsHideArchived" : "SessionsShowArchived");
        _sessionsArchiveToggle.IsVisible = archived.Count > 0 || _showArchivedSessions;

        _sessionsList!.Children.Clear();
        if (visible.Count == 0)
        {
            _sessionsList.Children.Add(new TextBlock
            {
                Text = Loc.Get("SessionsNone"),
                FontSize = 12,
                Opacity = 0.6,
                Margin = new Thickness(10, 4),
            });
            return;
        }

        var live = RunningSessionService.LiveSessionIds(_projectFolder);
        foreach (var session in visible.Take(SidebarSessionLimit))
            _sessionsList.Children.Add(BuildSessionRow(session, archived.Contains(session.Id), live));

        if (visible.Count > SidebarSessionLimit)
            _sessionsList.Children.Add(new TextBlock
            {
                Text = string.Format(Loc.Get("SessionsMore"), visible.Count - SidebarSessionLimit),
                FontSize = 11,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(10, 4),
            });
    }

    private string _sessionStateSignature = "";

    /// <summary>
    /// Redraws the session rows when a window here starts or finishes a turn, which is the only
    /// state they show that changes without the session list being read again.
    /// </summary>
    private void RefreshSessionStates()
    {
        var signature = string.Join("|", _children
            .Where(c => c.SessionId != null)
            .Select(c => c.SessionId + (c.Terminal.IsProcessRunning ? c.Terminal.IsGenerating ? "w" : "i" : "x")));
        if (signature == _sessionStateSignature) return;
        _sessionStateSignature = signature;
        RefreshSessionsSection();
    }

    private string SessionName(SessionInfo s) =>
        _settings.SessionNames.TryGetValue(s.Id, out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : s.DisplayTitle ?? s.Id;

    private bool MatchesSidebarSession(SessionInfo s, string query) =>
        SessionName(s).Contains(query, StringComparison.OrdinalIgnoreCase)
        || (s.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
        || (s.Summary?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
        || s.Id.StartsWith(query, StringComparison.OrdinalIgnoreCase);

    private MdiChildInfo? WindowRunning(string sessionId) =>
        _children.FirstOrDefault(c => c.Terminal.IsProcessRunning
            && string.Equals(c.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Where a session stands: a window here answers from its live state, a CLI elsewhere only
    /// says it is running, and a closed session answers from the end of its transcript.
    /// </summary>
    private (IBrush Brush, string Text) SessionState(SessionInfo s, MdiChildInfo? window, HashSet<string> live)
    {
        if (window != null)
            return window.Terminal.IsGenerating
                ? (DotBusyBrush, Loc.Get("SessionStateWorking"))
                : (DotIdleBrush, Loc.Get("SessionStateWaiting"));
        if (live.Contains(s.Id))
            return (DotElsewhereBrush, Loc.Get("SessionStateElsewhere"));
        return s.EndState switch
        {
            SessionEndState.Asking => (DotAskingBrush, Loc.Get("SessionStateAsking")),
            SessionEndState.MidTurn or SessionEndState.Interrupted => (DotExitedBrush, Loc.Get("SessionStateInterrupted")),
            SessionEndState.Done => (DotExitedBrush, Loc.Get("SessionStateDone")),
            _ => (DotExitedBrush, ""),
        };
    }

    private static string SessionTime(DateTime? t)
    {
        if (t is not DateTime time) return "";
        return time.Date == DateTime.Today ? time.ToString("HH:mm") : time.ToString("MM/dd HH:mm");
    }

    private Control BuildSessionRow(SessionInfo session, bool isArchived, HashSet<string> live)
    {
        var window = WindowRunning(session.Id);
        var (brush, stateText) = SessionState(session, window, live);
        bool isActive = window != null && ReferenceEquals(window, _activeChild);

        var dot = new Ellipse
        {
            Width = 8, Height = 8,
            Fill = brush,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 5, 8, 0),
        };
        var title = new TextBlock
        {
            Text = SessionName(session),
            FontSize = 13,
            FontFamily = TitleFontFamily,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Opacity = isArchived ? 0.55 : 1,
        };
        var parts = new List<string>();
        if (stateText.Length > 0) parts.Add(stateText);
        var time = SessionTime(session.Timestamp);
        if (time.Length > 0) parts.Add(time);
        if (isArchived) parts.Add(Loc.Get("SessionsArchivedTag"));
        var detail = new TextBlock
        {
            Text = string.Join(" · ", parts),
            FontSize = 11,
            Opacity = 0.6,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var text = new StackPanel { Spacing = 1 };
        text.Children.Add(title);
        text.Children.Add(detail);

        var grid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,*"), Margin = new Thickness(4, 0) };
        Grid.SetColumn(text, 1);
        grid.Children.Add(dot);
        grid.Children.Add(text);

        var idle = isActive ? new SolidColorBrush(Color.FromArgb(30, 0, 122, 255)) : (IBrush)Brushes.Transparent;
        var row = new Border
        {
            Child = grid,
            Padding = new Thickness(6, 4),
            CornerRadius = new CornerRadius(6),
            Background = idle,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        var tip = session.Summary is { Length: > 0 } summary && summary != title.Text
            ? summary + "\n" + session.Id
            : session.Id;
        ToolTip.SetTip(row, tip);
        ToolTip.SetShowDelay(row, 400);

        row.PointerEntered += (_, _) => { if (!isActive) row.Background = new SolidColorBrush(_isDark ? Color.FromArgb(20, 255, 255, 255) : Color.FromArgb(20, 0, 0, 0)); };
        row.PointerExited += (_, _) => row.Background = idle;
        row.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            OpenSidebarSession(session);
        };
        row.ContextMenu = BuildSessionMenu(session, isArchived, window != null);
        return row;
    }

    private void OpenSidebarSession(SessionInfo session)
    {
        var window = WindowRunning(session.Id);
        if (window != null)
        {
            ActivateTerminal(window);
            window.Terminal.FocusTerminal();
            return;
        }
        ResumeSession(session);
    }

    private ContextMenu BuildSessionMenu(SessionInfo session, bool isArchived, bool isOpen)
    {
        var menu = new ContextMenu();

        var open = new MenuItem { Header = Loc.Get(isOpen ? "SessionSwitch" : "SessionResume") };
        open.Click += (_, _) => OpenSidebarSession(session);
        menu.Items.Add(open);
        menu.Items.Add(new Separator());

        var rename = new MenuItem { Header = Loc.Get("SessionRename") };
        rename.Click += async (_, _) =>
        {
            var name = await ShowTextInputDialog(Loc.Get("SessionRenameTitle"),
                session.DisplayTitle ?? "", SessionName(session));
            if (name == null) return;
            if (string.IsNullOrWhiteSpace(name)) _settings.SessionNames.Remove(session.Id);
            else _settings.SessionNames[session.Id] = name.Trim();
            _settings.Save();
            RefreshSessionsSection();
        };
        menu.Items.Add(rename);

        if (_settings.SessionNames.ContainsKey(session.Id))
        {
            var reset = new MenuItem { Header = Loc.Get("SessionResetName") };
            reset.Click += (_, _) =>
            {
                _settings.SessionNames.Remove(session.Id);
                _settings.Save();
                RefreshSessionsSection();
            };
            menu.Items.Add(reset);
        }

        var archive = new MenuItem { Header = Loc.Get(isArchived ? "SessionUnarchive" : "SessionArchive") };
        archive.Click += (_, _) =>
        {
            _settings.ArchivedSessions.RemoveAll(id => id.Equals(session.Id, StringComparison.OrdinalIgnoreCase));
            if (!isArchived) _settings.ArchivedSessions.Add(session.Id);
            _settings.Save();
            RefreshSessionsSection();
        };
        menu.Items.Add(archive);

        // A panel rebuild while this is open would pull its row out from under it
        menu.Opened += (_, _) => _windowsMenuOpen = true;
        menu.Closed += (_, _) => _windowsMenuOpen = false;
        return menu;
    }
}
