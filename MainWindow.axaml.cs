using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
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
    private enum SidebarPanel { None, Explorer, Snippets, Settings }

    private string? _projectFolder;
    private string? _gitRepoUrl;
    private readonly UsageTracker _usageTracker = new();
    private bool _isDark = true;
    private MdiLayout _layout = MdiLayout.Maximize;
    private int _activeChildIndex = -1;
    private readonly List<MdiChildInfo> _children = new();
    private readonly AppSettings _settings;

    private bool _suppressFolderSelectionChanged;

    // Sidebar state
    private SidebarPanel _activeSidePanel = SidebarPanel.None;
    private double _sidePanelWidth = 250;
    private bool _settingsInitialized;
    private bool _snippetsInitialized;
    private readonly SnippetStore _snippetStore;

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

    private static readonly List<string> ThemeList = new() { "Dark", "Light" };
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
    );

    public MainWindow()
    {
        InitializeComponent();

        _settings = AppSettings.Load();
        _snippetStore = SnippetStore.Load();
        _isDark = _settings.IsDark;

        _usageTracker.Start();

        _projectFolder = !string.IsNullOrEmpty(_settings.ProjectFolder) && Directory.Exists(_settings.ProjectFolder)
            ? _settings.ProjectFolder
            : Environment.CurrentDirectory;
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

        // Apply saved language
        Loc.Language = _settings.Language;
        ApplyLocalization();

        RefreshGitInfo();
        RefreshSessionList();
        RefreshFileTree();

        // Auto-launch claude -c if project folder is valid
        if (!string.IsNullOrEmpty(_projectFolder) && Directory.Exists(_projectFolder))
        {
            Dispatcher.UIThread.Post(() => CreateNewChild("claude -c", "Claude"),
                DispatcherPriority.Background);
        }
    }

    // ── Localization ──

    private void ApplyLocalization()
    {
        // Toolbar
        LblProject.Text = Loc.Get("Project");
        CmbProjectFolder.PlaceholderText = Loc.Get("SelectProjectFolder");
        LblNewClaude.Text = Loc.Get("NewClaude");
        LblSession.Text = Loc.Get("Session");
        CmbSessions.PlaceholderText = Loc.Get("SelectSession");
        LblResume.Text = Loc.Get("Resume");

        // Status Bar - git info updated via RefreshGitInfo()

        // Explorer panel header
        ToolTip.SetTip(BtnBrowseFolder, Loc.Get("SelectProjectFolder"));

        // Activity Bar tooltips
        ToolTip.SetTip(BtnActivityExplorer, Loc.Get("ExplorerTooltip"));
        ToolTip.SetTip(BtnActivitySnippets, Loc.Get("SnippetsTooltip"));
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

        // Settings panel labels
        LblConsoleSettings.Text = Loc.Get("ConsoleSettings");
        LblLanguage.Text = Loc.Get("LanguageSetting");
        LblFontFamily.Text = Loc.Get("FontFamily");
        LblFontSize.Text = Loc.Get("FontSize");
        LblTheme.Text = Loc.Get("Theme");
        BtnApplySettings.Content = Loc.Get("Apply");
        LblOpenClaudeFolder.Text = Loc.Get("OpenClaudeFolder");

        // Snippets panel
        LblAddSnippet.Text = Loc.Get("AddSnippet");

        // Window strip tooltips
        ToolTip.SetTip(BtnLayoutTile, Loc.Get("TileWindows"));
        ToolTip.SetTip(BtnLayoutCascade, Loc.Get("CascadeWindows"));
        ToolTip.SetTip(BtnLayoutMaximize, Loc.Get("FullView"));

        // Window title
        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        var verStr = ver != null ? $"Ver.{ver.Major}.{ver.Minor}.{ver.Build}.{ver.Revision}" : "";
        Title = $"{Loc.Get("AppTitle")}  {verStr}";
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
        SidePanelTitle.Text = panel switch
        {
            SidebarPanel.Explorer => Loc.Get("EXPLORER"),
            SidebarPanel.Settings => Loc.Get("SETTINGS"),
            SidebarPanel.Snippets => Loc.Get("SNIPPETS"),
            _ => ""
        };
        BtnBrowseFolder.IsVisible = panel == SidebarPanel.Explorer;
    }

    private void UpdateActivityBarHighlight()
    {
        SetActivityButtonActive(BtnActivityExplorer, _activeSidePanel == SidebarPanel.Explorer);
        SetActivityButtonActive(BtnActivitySnippets, _activeSidePanel == SidebarPanel.Snippets);
        SetActivityButtonActive(BtnActivitySettings, _activeSidePanel == SidebarPanel.Settings);
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
        if (node == null) return;

        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
                await clipboard.SetTextAsync(node.FullPath);
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

    private static void OpenFileWith(string filePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "rundll32.exe",
                Arguments = $"shell32.dll,OpenAs_RunDLL \"{filePath}\"",
                UseShellExecute = false
            });
        }
        catch { }
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

        CmbSettingsTheme.ItemsSource = ThemeList;
        CmbSettingsTheme.SelectedItem = _isDark ? "Dark" : "Light";
    }

    private void OnApplySettings(object? sender, RoutedEventArgs e)
    {
        var language = CmbSettingsLanguage.SelectedItem as string ?? "English";
        var fontFamily = CmbSettingsFontFamily.SelectedItem as string ?? "Cascadia Mono";
        var fontSize = (double)(NumSettingsFontSize.Value ?? 14);
        var isDark = (CmbSettingsTheme.SelectedItem as string) == "Dark";

        _settings.Language = language;
        _settings.FontFamily = fontFamily;
        _settings.FontSize = fontSize;
        _settings.IsDark = isDark;
        _settings.Save();

        Loc.Language = language;
        ApplyLocalization();

        foreach (var child in _children)
        {
            child.Terminal.SetFont(_settings.FontFamily, _settings.FontSize);
        }

        if (_isDark != isDark)
        {
            ApplyTheme(isDark);
        }
    }

    private void OnOpenClaudeFolder(object? sender, RoutedEventArgs e)
    {
        var claudeDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        if (Directory.Exists(claudeDir))
        {
            Process.Start(new ProcessStartInfo { FileName = claudeDir, UseShellExecute = true });
        }
    }

    // ── Snippets Panel ──

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
            BorderBrush = new SolidColorBrush(Color.FromRgb(58, 58, 60)),
            Background = new SolidColorBrush(Color.FromRgb(28, 28, 30)),
            Foreground = new SolidColorBrush(Color.FromRgb(255, 255, 255)),
            Watermark = Loc.Get("EnterSnippetText"),
            Classes = { "snippet-text" }
        };

        // Drag handle (grip area on the left)
        var dragHandle = new Border
        {
            Width = 20,
            Background = new SolidColorBrush(Color.FromRgb(44, 44, 46)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(58, 58, 60)),
            BorderThickness = new Thickness(1, 1, 0, 1),
            CornerRadius = new CornerRadius(8, 0, 0, 8),
            Cursor = new Cursor(StandardCursorType.SizeNorthSouth),
            Child = new TextBlock
            {
                Text = "\u2847",  // braille dots as grip icon
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 105)),
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
            Background = new SolidColorBrush(Color.FromRgb(44, 44, 46)),
            Foreground = new SolidColorBrush(Color.FromRgb(48, 209, 88)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(58, 58, 60)),
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
                _children[_activeChildIndex].Terminal.SendText(textBox.Text);
                BringToFront(_activeChildIndex);
                _children[_activeChildIndex].Terminal.FocusTerminal();
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
                double itemH = b.Bounds.Height + 6; // 6 = StackPanel Spacing
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
                SyncSnippetOrder();
                // Restore foreground on all TextBoxes after visual tree re-insertion
                foreach (var child in SnippetsList.Children)
                {
                    if (child is Border cb && cb.Child is Grid cg)
                    {
                        var ctb = cg.Children.OfType<TextBox>().FirstOrDefault();
                        if (ctb != null)
                            ctb.Foreground = new SolidColorBrush(Color.FromRgb(255, 255, 255));
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
                            tb.Foreground = new SolidColorBrush(Color.FromRgb(255, 255, 255));
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

    // ── MDI Child management ──

    private void CreateNewChild(string command, string tabTitle)
    {
        var terminal = new TerminalControl { IsDarkTheme = _isDark };
        terminal.SetFont(_settings.FontFamily, _settings.FontSize);

        // --- Title bar ---
        var dot = new Ellipse
        {
            Width = 8, Height = 8,
            Fill = new SolidColorBrush(Color.FromRgb(48, 209, 88)),  // Apple Green
            VerticalAlignment = VerticalAlignment.Center
        };
        var titleText = new TextBlock
        {
            Text = tabTitle,
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
            CornerRadius = new CornerRadius(10, 10, 0, 0)
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
            CornerRadius = new CornerRadius(10),
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
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 4),
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
            dot.Fill = new SolidColorBrush(Color.FromRgb(142, 142, 147));   // Apple systemGray
            stripDot.Fill = new SolidColorBrush(Color.FromRgb(142, 142, 147));
            RefreshSessionList();
        };

        _children.Add(entry);
        _activeChildIndex = _children.Count - 1;
        MdiContainer.Children.Add(container);
        WindowStrip.Children.Add(stripButton);
        ArrangeChildren();

        Dispatcher.UIThread.Post(() =>
        {
            string cdPart = !string.IsNullOrEmpty(_projectFolder) && Directory.Exists(_projectFolder)
                ? $"cd /d \"{_projectFolder}\" && "
                : "";
            string fullCommand = $"cmd.exe /c chcp 65001 >nul && {cdPart}{command}";
            terminal.StartProcess(fullCommand, _projectFolder);
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

        ArrangeChildren();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _settings.ProjectFolder = _projectFolder ?? "";
        _settings.Save();
        _snippetStore.Save();
        foreach (var child in _children)
        {
            child.Terminal.Dispose();
        }
        _usageTracker.Dispose();
    }
}
