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
    };

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

        // Apply saved language
        Loc.Language = _settings.Language;
        ApplyLocalization();

        RefreshGitInfo();
        RefreshSessionList();
        RefreshFileTree();

        // Show welcome page or auto-launch
        if (_settings.ShowWelcomePage)
        {
            Dispatcher.UIThread.Post(ShowWelcomePage, DispatcherPriority.Background);
        }
        else if (!string.IsNullOrEmpty(_projectFolder) && Directory.Exists(_projectFolder))
        {
            Dispatcher.UIThread.Post(LaunchClaudeWithInitialPrompt, DispatcherPriority.Background);
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
        LblInitialPrompt.Text = Loc.Get("InitialPrompt");
        LblApplySettings.Text = Loc.Get("Apply");
        LblOpenClaudeFolder.Text = Loc.Get("OpenClaudeFolder");
        ChkShowWelcomePage.Content = Loc.Get("ShowWelcomePage");

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

    private void OnActivityModeSwitch(object? sender, RoutedEventArgs e)
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

        TxtInitialPrompt.Text = _settings.InitialPrompt;
        TxtInitialPrompt.LostFocus += (_, _) =>
        {
            _settings.InitialPrompt = TxtInitialPrompt.Text?.Trim() ?? "-c";
            _settings.Save();
        };

        ChkShowWelcomePage.IsChecked = _settings.ShowWelcomePage;
    }

    private bool _suppressWelcomeCheckChanged;

    private void OnShowWelcomePageChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressWelcomeCheckChanged) return;
        _settings.ShowWelcomePage = ChkShowWelcomePage.IsChecked == true;
        _settings.Save();
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
                _children[_activeChildIndex].Terminal.SendText(textBox.Text.Replace("\\r", "\r"));
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
        LaunchClaudeWithInitialPrompt();
    }

    private void OnCloseTab(object? sender, RoutedEventArgs e)
    {
        if (_activeChildIndex >= 0 && _activeChildIndex < _children.Count)
        {
            CloseChild(_children[_activeChildIndex]);
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
        )
        {
            ProjectFolder = _projectFolder
        };

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

    // ── Welcome Page ──

    private Border? _welcomeContainer;

    private async void ShowWelcomePage()
    {
        // Get recent project folders
        var recentFolders = await SessionService.GetRecentProjectFoldersAsync();

        // --- Build Welcome UI ---
        var titleText = new TextBlock
        {
            Text = Loc.Get("WelcomeTitle"),
            FontSize = 28,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 225)),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 24)
        };

        // "Start" section header
        var startHeader = new TextBlock
        {
            Text = Loc.Get("Start"),
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 185)),
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
            Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 185)),
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
                Foreground = new SolidColorBrush(Color.FromArgb(140, 200, 200, 205)),
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
            AttachHoverEffect(itemBorder);

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
                Foreground = new SolidColorBrush(Color.FromArgb(100, 200, 200, 205)),
                Margin = new Thickness(8, 4)
            });
        }

        // Checkbox at bottom
        var showOnStartupCheck = new CheckBox
        {
            Content = Loc.Get("ShowWelcomeOnStartup"),
            IsChecked = _settings.ShowWelcomePage,
            Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 165)),
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
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 32)),
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

    private static Border CreateWelcomeLink(string iconData, string text, Color iconColor)
    {
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
            Foreground = new SolidColorBrush(Color.FromRgb(75, 156, 255)),
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
        AttachHoverEffect(border);

        return border;
    }

    private void OpenProjectFromWelcome(string folderPath, bool continueSession = false)
    {
        CloseWelcomePage();
        SetProjectFolder(folderPath);
        LoadRecentProjectFolders();
        if (continueSession)
            CreateNewChild("claude -c", "Claude");
        else
            LaunchClaudeWithInitialPrompt();
    }

    private static void AttachHoverEffect(Border border)
    {
        border.PointerEntered += (s, _) => ((Border)s!).Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255));
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
        var prompt = _settings.InitialPrompt.Trim();
        var cmd = string.IsNullOrEmpty(prompt) ? "claude" : $"claude {prompt}";
        CreateNewChild(cmd, "Claude");
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        CloseWelcomePage();
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
