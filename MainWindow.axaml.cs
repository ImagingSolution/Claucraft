using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using ClaudeCodeMDI.Services;
using ClaudeCodeMDI.Terminal;

namespace ClaudeCodeMDI;

public partial class MainWindow : Window
{
    private enum MdiLayout { Maximize, Tile, Cascade }

    private string? _projectFolder;
    private readonly UsageTracker _usageTracker = new();
    private bool _isDark = true;
    private MdiLayout _layout = MdiLayout.Maximize;
    private int _activeChildIndex = -1;
    private readonly List<MdiChildInfo> _children = new();
    private readonly AppSettings _settings;

    private const string BrowseFolderItem = "📁 Browse folder...";
    private bool _suppressFolderSelectionChanged;

    // Drag state
    private bool _isDragging;
    private Point _dragStart;
    private double _dragChildLeft;
    private double _dragChildTop;
    private MdiChildInfo? _dragChild;

    private record MdiChildInfo(
        Border Container,
        Border TitleBar,
        TextBlock TitleText,
        Ellipse StatusDot,
        Ellipse StripDot,
        TerminalControl Terminal,
        Button StripButton,
        TextBlock StripText
    );

    public MainWindow()
    {
        InitializeComponent();

        _settings = AppSettings.Load();
        _isDark = _settings.IsDark;

        _usageTracker.UsageUpdated += info =>
        {
            Dispatcher.UIThread.Post(() => UpdateUsageDisplay(info));
        };
        _usageTracker.Start();

        _projectFolder = !string.IsNullOrEmpty(_settings.ProjectFolder) && Directory.Exists(_settings.ProjectFolder)
            ? _settings.ProjectFolder
            : Environment.CurrentDirectory;
        StatusFolder.Text = _projectFolder;
        LoadRecentProjectFolders();

        MdiContainer.SizeChanged += (_, _) =>
        {
            Dispatcher.UIThread.Post(ArrangeChildren, DispatcherPriority.Render);
        };

        // Apply saved theme
        if (!_isDark && Application.Current is App app)
        {
            app.SetTheme(false);
        }

        RefreshSessionList();

        // Auto-launch claude -c if project folder is valid
        if (!string.IsNullOrEmpty(_projectFolder) && Directory.Exists(_projectFolder))
        {
            Dispatcher.UIThread.Post(() => CreateNewChild("claude -c", "Claude"),
                DispatcherPriority.Background);
        }
    }

    private async void LoadRecentProjectFolders()
    {
        var recentFolders = await SessionService.GetRecentProjectFoldersAsync();
        var items = new List<string>();

        // Add current project folder at top if it's not in the list
        if (!string.IsNullOrEmpty(_projectFolder) && Directory.Exists(_projectFolder))
        {
            items.Add(_projectFolder);
            recentFolders.RemoveAll(f => f.Equals(_projectFolder, StringComparison.OrdinalIgnoreCase));
        }

        // Add recent folders (up to 10 total)
        foreach (var folder in recentFolders)
        {
            if (items.Count >= 10) break;
            items.Add(folder);
        }

        // Add browse option at the bottom
        items.Add(BrowseFolderItem);

        _suppressFolderSelectionChanged = true;
        CmbProjectFolder.ItemsSource = items;
        if (items.Count > 0 && !string.IsNullOrEmpty(_projectFolder))
        {
            CmbProjectFolder.SelectedIndex = 0;
        }
        _suppressFolderSelectionChanged = false;
    }

    private async void OnProjectFolderSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressFolderSelectionChanged) return;

        if (CmbProjectFolder.SelectedItem is string selected)
        {
            if (selected == BrowseFolderItem)
            {
                // Reset selection to current project folder
                _suppressFolderSelectionChanged = true;
                if (!string.IsNullOrEmpty(_projectFolder))
                {
                    var items = CmbProjectFolder.ItemsSource as List<string>;
                    int idx = items?.IndexOf(_projectFolder) ?? -1;
                    CmbProjectFolder.SelectedIndex = idx >= 0 ? idx : -1;
                }
                else
                {
                    CmbProjectFolder.SelectedIndex = -1;
                }
                _suppressFolderSelectionChanged = false;

                // Open folder picker
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
            else if (Directory.Exists(selected))
            {
                SetProjectFolder(selected);
            }
        }
    }

    private void SetProjectFolder(string path)
    {
        _projectFolder = path;
        StatusFolder.Text = path;
        RefreshSessionList();
    }

    private async void RefreshSessionList()
    {
        if (string.IsNullOrEmpty(_projectFolder) || !Directory.Exists(_projectFolder))
        {
            CmbSessions.ItemsSource = null;
            BtnResumeSession.IsEnabled = false;
            return;
        }

        var sessions = await SessionService.GetSessionsForProjectAsync(_projectFolder);
        CmbSessions.ItemsSource = sessions;
        CmbSessions.SelectedIndex = -1;
        BtnResumeSession.IsEnabled = false;
    }

    private void OnSessionSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        BtnResumeSession.IsEnabled = CmbSessions.SelectedItem is SessionInfo;
    }

    private void OnResumeSession(object? sender, RoutedEventArgs e)
    {
        if (CmbSessions.SelectedItem is SessionInfo session)
        {
            string cmd = SessionService.BuildResumeCommand(session.Id);
            string tabLabel = !string.IsNullOrEmpty(session.Summary)
                ? session.Summary[..Math.Min(20, session.Summary.Length)]
                : $"Session: {session.Id[..Math.Min(8, session.Id.Length)]}";
            CreateNewChild(cmd, tabLabel);
        }
    }

    private void OnOpenExplorer(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_projectFolder) && Directory.Exists(_projectFolder))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = _projectFolder,
                UseShellExecute = true
            });
        }
    }

    private void OnNewClaude(object? sender, RoutedEventArgs e)
    {
        CreateNewChild("claude", "Claude");
    }

    private void OnCloseTab(object? sender, RoutedEventArgs e)
    {
        if (_activeChildIndex >= 0 && _activeChildIndex < _children.Count)
        {
            CloseChild(_children[_activeChildIndex]);
        }
    }

    private void ApplyTheme(bool isDark)
    {
        _isDark = isDark;
        if (Application.Current is App app)
        {
            app.SetTheme(_isDark);
        }
        foreach (var child in _children)
        {
            child.Terminal.IsDarkTheme = _isDark;
        }
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

    private void ArrangeChildren()
    {
        double w = MdiContainer.Bounds.Width;
        double h = MdiContainer.Bounds.Height;
        if (w <= 0 || h <= 0) return;
        if (_children.Count == 0) return;

        // Clamp active index
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
    }

    private static readonly SolidColorBrush ActiveBorder = new(Color.FromRgb(13, 110, 253));
    private static readonly SolidColorBrush InactiveBorder = new(Color.FromArgb(50, 255, 255, 255));

    private void UpdateStripSelection()
    {
        for (int i = 0; i < _children.Count; i++)
        {
            var child = _children[i];
            bool active = i == _activeChildIndex;

            // Strip button highlight
            child.StripButton.Background = active
                ? new SolidColorBrush(Color.FromArgb(40, 13, 110, 253))
                : Brushes.Transparent;
            child.StripButton.BorderBrush = active
                ? new SolidColorBrush(Color.FromArgb(80, 13, 110, 253))
                : Brushes.Transparent;

            // Container border highlight
            child.Container.BorderBrush = active ? ActiveBorder : InactiveBorder;
            child.Container.BorderThickness = active ? new Thickness(2) : new Thickness(1);
        }
    }

    // ── MDI Child management ──

    private void CreateNewChild(string command, string tabTitle)
    {
        var terminal = new TerminalControl { IsDarkTheme = _isDark };
        terminal.SetFont(_settings.FontFamily, _settings.FontSize);

        // --- Title bar ---
        var dot = new Ellipse
        {
            Width = 7, Height = 7,
            Fill = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
            VerticalAlignment = VerticalAlignment.Center
        };
        var titleText = new TextBlock
        {
            Text = tabTitle,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var closeBtn = new Button
        {
            Content = "\u00D7",
            FontSize = 14,
            Padding = new Thickness(6, 0),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(4),
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
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 50)),
            Padding = new Thickness(0, 5),
            Child = titleGrid,
            Cursor = new Cursor(StandardCursorType.Hand),
            CornerRadius = new CornerRadius(6, 6, 0, 0)
        };

        // --- Container ---
        var dockPanel = new DockPanel();
        DockPanel.SetDock(titleBar, Dock.Top);
        dockPanel.Children.Add(titleBar);
        dockPanel.Children.Add(terminal);

        var container = new Border
        {
            Child = dockPanel,
            BorderBrush = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30))
        };

        // --- Window strip button ---
        var stripDot = new Ellipse
        {
            Width = 6, Height = 6,
            Fill = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
            VerticalAlignment = VerticalAlignment.Center
        };
        var stripText = new TextBlock
        {
            Text = tabTitle,
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
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 3),
            Cursor = new Cursor(StandardCursorType.Hand)
        };

        var entry = new MdiChildInfo(
            container, titleBar, titleText, dot, stripDot, terminal, stripButton, stripText
        );

        // --- Events ---
        closeBtn.Click += (_, _) => CloseChild(entry);
        stripCloseBtn.Click += (_, e) => { CloseChild(entry); e.Handled = true; };

        stripButton.Click += (_, _) =>
        {
            int idx = _children.IndexOf(entry);
            if (idx >= 0) BringToFront(idx);
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

        terminal.Clicked += () =>
        {
            int idx = _children.IndexOf(entry);
            if (idx >= 0 && _activeChildIndex != idx)
                BringToFront(idx);
        };

        terminal.TitleChanged += title =>
        {
            var displayTitle = string.IsNullOrWhiteSpace(title) ? tabTitle : title;
            titleText.Text = displayTitle;
            stripText.Text = displayTitle;
        };

        terminal.Exited += () =>
        {
            dot.Fill = new SolidColorBrush(Color.FromRgb(120, 120, 120));
            stripDot.Fill = new SolidColorBrush(Color.FromRgb(120, 120, 120));
            StatusProcess.Text = $"Exited: {titleText.Text}";
            RefreshSessionList();
        };

        _children.Add(entry);
        _activeChildIndex = _children.Count - 1;
        MdiContainer.Children.Add(container);
        WindowStrip.Children.Add(stripButton);
        UpdateTabCount();
        ArrangeChildren();

        Dispatcher.UIThread.Post(() =>
        {
            string cdPart = !string.IsNullOrEmpty(_projectFolder) && Directory.Exists(_projectFolder)
                ? $"cd /d \"{_projectFolder}\" && "
                : "";
            string fullCommand = $"cmd.exe /c chcp 65001 >nul && {cdPart}{command}";
            terminal.StartProcess(fullCommand, _projectFolder);
            StatusProcess.Text = $"Running: {command}";
            terminal.FocusTerminal();
        }, DispatcherPriority.Background);
    }

    private void CloseChild(MdiChildInfo entry)
    {
        int idx = _children.IndexOf(entry);
        if (idx < 0) return;

        entry.Terminal.Dispose();
        MdiContainer.Children.Remove(entry.Container);
        WindowStrip.Children.Remove(entry.StripButton);
        _children.RemoveAt(idx);

        if (_children.Count == 0)
            _activeChildIndex = -1;
        else if (_activeChildIndex >= _children.Count)
            _activeChildIndex = _children.Count - 1;
        else if (idx <= _activeChildIndex && _activeChildIndex > 0)
            _activeChildIndex--;

        UpdateTabCount();
        ArrangeChildren();
    }

    private async void OnUsageTapped(object? sender, TappedEventArgs e)
    {
        var chart = new UsageChartWindow();
        await chart.ShowDialog(this);
    }

    private void UpdateUsageDisplay(UsageInfo info)
    {
        double pct = Math.Clamp(info.Percentage, 0, 100);
        StatusUsagePercent.Text = $"{pct:F0}%";
        UsageBarFill.Width = 130.0 * pct / 100.0;

        if (pct < 50)
            UsageBarFill.Background = new SolidColorBrush(Color.FromRgb(76, 175, 80));
        else if (pct < 80)
            UsageBarFill.Background = new SolidColorBrush(Color.FromRgb(255, 193, 7));
        else
            UsageBarFill.Background = new SolidColorBrush(Color.FromRgb(244, 67, 54));

        StatusUsageDetail.Text = $"{info.TodayMessages} msgs / {info.TodaySessions} sessions";
    }

    private void UpdateTabCount()
    {
        StatusTabs.Text = $"{_children.Count} windows";
    }

    private async void OnSettings(object? sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_settings.FontFamily, _settings.FontSize, _isDark);
        await dlg.ShowDialog(this);

        if (dlg.Confirmed)
        {
            _settings.FontFamily = dlg.SelectedFontFamily;
            _settings.FontSize = dlg.SelectedFontSize;
            _settings.IsDark = dlg.SelectedIsDark;
            _settings.Save();

            foreach (var child in _children)
            {
                child.Terminal.SetFont(_settings.FontFamily, _settings.FontSize);
            }

            if (_isDark != dlg.SelectedIsDark)
            {
                ApplyTheme(dlg.SelectedIsDark);
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _settings.ProjectFolder = _projectFolder ?? "";
        _settings.Save();
        foreach (var child in _children)
        {
            child.Terminal.Dispose();
        }
        _usageTracker.Dispose();
    }
}
