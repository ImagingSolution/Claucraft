using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Claucraft.Services;

namespace Claucraft.Controls;

/// <summary>
/// Document View panel that renders Claude session conversations as formatted Markdown.
/// Displays in the side panel area, reading from JSONL session files.
/// </summary>
public class DocumentViewPanel : Panel
{
    private readonly ScrollViewer _scrollViewer;
    private readonly StackPanel _messagesStack;
    private readonly TextBlock _statusLabel;
    private readonly Border _statusBar;
    private readonly DispatcherTimer _pollTimer;
    private string? _currentSessionPath;
    private int _lastLineCount;
    private bool _autoScroll = true;
    private bool _isDark;
    private Typeface _codeTypeface;
    private double _baseFontSize = 13;
    private string _fontFamily = "Cascadia Mono, Consolas, Courier New";

    public DocumentViewPanel(bool isDark, Typeface codeTypeface)
    {
        _isDark = isDark;
        _codeTypeface = codeTypeface;

        // Status bar at top
        _statusLabel = new TextBlock
        {
            Text = Loc.Get("NoSession", "No session loaded"),
            FontSize = 11,
            Foreground = new SolidColorBrush(isDark ? Color.FromRgb(140, 140, 145) : Color.FromRgb(120, 120, 125)),
            Margin = new Thickness(10, 6),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _statusBar = new Border
        {
            Child = _statusLabel,
            BorderBrush = new SolidColorBrush(isDark ? Color.FromRgb(56, 56, 58) : Color.FromRgb(200, 200, 205)),
            BorderThickness = new Thickness(0, 0, 0, 0.5),
        };
        Children.Add(_statusBar);

        // Messages area
        _messagesStack = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(8, 8),
        };

        _scrollViewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Visible,
            Content = _messagesStack,
        };
        _scrollViewer.ScrollChanged += OnScrollChanged;
        Children.Add(_scrollViewer);

        ClipToBounds = true;

        // Poll timer
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _pollTimer.Tick += OnPollTick;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _statusBar.Measure(new Size(availableSize.Width, 30));
        double statusH = _statusBar.DesiredSize.Height;
        double scrollH = Math.Max(0, availableSize.Height - statusH);
        _scrollViewer.Measure(new Size(availableSize.Width, scrollH));
        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double statusH = _statusBar.DesiredSize.Height;
        _statusBar.Arrange(new Rect(0, 0, finalSize.Width, statusH));
        double scrollH = Math.Max(0, finalSize.Height - statusH);
        _scrollViewer.MaxHeight = scrollH;
        _scrollViewer.Arrange(new Rect(0, statusH, finalSize.Width, scrollH));
        return finalSize;
    }

    public void LoadSession(string jsonlPath)
    {
        System.Diagnostics.Debug.WriteLine($"[DocView] LoadSession: {jsonlPath}");
        _currentSessionPath = jsonlPath;
        _lastLineCount = 0;
        _messagesStack.Children.Clear();
        _autoScroll = true;

        var fileName = System.IO.Path.GetFileNameWithoutExtension(jsonlPath);
        _statusLabel.Text = $"Session: {fileName[..Math.Min(20, fileName.Length)]}...";

        var messages = SessionMessageReader.ReadSession(jsonlPath);
        _lastLineCount = CountLines(jsonlPath);
        System.Diagnostics.Debug.WriteLine($"[DocView] Loaded {messages.Count} messages, {_lastLineCount} lines");
        foreach (var msg in messages)
        {
            System.Diagnostics.Debug.WriteLine($"[DocView] Message: {msg.Role} isThinking={msg.IsThinking} isTool={msg.IsToolUse} text={msg.Text[..Math.Min(80, msg.Text.Length)]}");
            AddMessageBubble(msg);
        }
        System.Diagnostics.Debug.WriteLine($"[DocView] Children count: {_messagesStack.Children.Count}");

        ScrollToBottom();
    }

    public void StartPolling()
    {
        _pollTimer.Start();
    }

    public void StopPolling()
    {
        _pollTimer.Stop();
    }

    public void Clear()
    {
        _messagesStack.Children.Clear();
        _currentSessionPath = null;
        _lastLineCount = 0;
        _statusLabel.Text = Loc.Get("NoSession", "No session loaded");
    }

    public void SetFont(string fontFamily, double fontSize)
    {
        _fontFamily = fontFamily + ", Consolas, Courier New";
        _codeTypeface = new Typeface(_fontFamily);
        _baseFontSize = fontSize;
        ReloadSession();
    }

    public void UpdateTheme(bool isDark)
    {
        _isDark = isDark;
        _statusLabel.Foreground = new SolidColorBrush(isDark ? Color.FromRgb(140, 140, 145) : Color.FromRgb(120, 120, 125));
        ReloadSession();
    }

    private void ReloadSession()
    {
        if (_currentSessionPath != null)
        {
            var path = _currentSessionPath;
            _lastLineCount = 0;
            _messagesStack.Children.Clear();
            var messages = SessionMessageReader.ReadSession(path);
            _lastLineCount = CountLines(path);
            foreach (var msg in messages)
                AddMessageBubble(msg);
        }
    }

    private void OnPollTick(object? sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(_currentSessionPath)) return;
        if (!System.IO.File.Exists(_currentSessionPath)) return;

        // Check if file has new content by comparing line count
        int currentLineCount = CountLines(_currentSessionPath);
        if (currentLineCount == _lastLineCount) return;

        // Reload all messages (with consolidation) to ensure proper merging
        var messages = SessionMessageReader.ReadSession(_currentSessionPath);
        _lastLineCount = currentLineCount;

        // Only update if message count changed
        if (messages.Count != _messagesStack.Children.Count)
        {
            _messagesStack.Children.Clear();
            foreach (var msg in messages)
                AddMessageBubble(msg);

            if (_autoScroll)
                ScrollToBottom();
        }
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // If user scrolls up, disable auto-scroll
        // If at the bottom, enable auto-scroll
        var sv = _scrollViewer;
        _autoScroll = sv.Offset.Y >= sv.Extent.Height - sv.Viewport.Height - 20;
    }

    private void AddMessageBubble(ConversationMessage msg)
    {
        var bubble = CreateMessageBubble(msg);
        if (bubble != null)
            _messagesStack.Children.Add(bubble);
    }

    private Control? CreateMessageBubble(ConversationMessage msg)
    {
        if (string.IsNullOrWhiteSpace(msg.Text)) return null;

        var fg = _isDark ? Color.FromRgb(220, 220, 225) : Color.FromRgb(28, 28, 30);

        if (msg.Role == MessageRole.User)
        {
            // User bubble: right-aligned, blue background
            var textBlock = new SelectableTextBlock
            {
                Text = msg.Text.Length > 500 ? msg.Text[..500] + "..." : msg.Text,
                FontSize = _baseFontSize,
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 600,
            };

            var bubble = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0, 122, 255)),
                CornerRadius = new CornerRadius(12, 12, 2, 12),
                Padding = new Thickness(12, 8),
                HorizontalAlignment = HorizontalAlignment.Right,
                MaxWidth = 640,
                Child = textBlock,
            };

            var container = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(40, 2, 0, 2),
            };

            // Timestamp
            if (msg.Timestamp.HasValue)
            {
                container.Children.Add(new TextBlock
                {
                    Text = msg.Timestamp.Value.ToString("HH:mm"),
                    FontSize = 10,
                    Foreground = new SolidColorBrush(_isDark ? Color.FromRgb(100, 100, 105) : Color.FromRgb(160, 160, 165)),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 0, 4, 2),
                });
            }
            container.Children.Add(bubble);
            return container;
        }

        // System/progress messages: compact display
        if (msg.Role == MessageRole.System)
        {
            var progressLabel = new TextBlock
            {
                Text = $"\u25B6 {msg.Text}",
                FontSize = 11,
                Foreground = new SolidColorBrush(_isDark ? Color.FromRgb(100, 180, 255) : Color.FromRgb(0, 100, 200)),
                Margin = new Thickness(4, 1),
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            };
            return progressLabel;
        }

        if (msg.IsToolUse && !msg.Text.Contains('\n'))
        {
            // Compact tool use indicator
            var toolLabel = new TextBlock
            {
                Text = $"\u2699 {msg.Text}",
                FontSize = 11,
                Foreground = new SolidColorBrush(_isDark ? Color.FromRgb(120, 120, 125) : Color.FromRgb(140, 140, 145)),
                FontStyle = FontStyle.Italic,
                Margin = new Thickness(0, 1),
            };
            return toolLabel;
        }

        if (msg.IsThinking)
        {
            // Thinking block: collapsed by default
            var expander = new Expander
            {
                Header = Loc.Get("Thinking", "Thinking..."),
                IsExpanded = false,
                FontSize = 12,
                Foreground = new SolidColorBrush(_isDark ? Color.FromRgb(120, 120, 125) : Color.FromRgb(140, 140, 145)),
                Margin = new Thickness(0, 2),
            };

            var thinkingContent = new TextBlock
            {
                Text = msg.Text.Length > 1000 ? msg.Text[..1000] + "..." : msg.Text,
                FontSize = 12,
                FontStyle = FontStyle.Italic,
                Foreground = new SolidColorBrush(_isDark ? Color.FromRgb(120, 120, 125) : Color.FromRgb(140, 140, 145)),
                TextWrapping = TextWrapping.Wrap,
            };
            expander.Content = thinkingContent;
            return expander;
        }

        // Assistant bubble: left-aligned, Markdown rendered
        {
            var mdControls = MarkdownParser.Parse(msg.Text, _isDark, _codeTypeface, _baseFontSize);

            var contentStack = new StackPanel { Spacing = 2 };
            foreach (var ctrl in mdControls)
                contentStack.Children.Add(ctrl);

            var bubble = new Border
            {
                Background = new SolidColorBrush(_isDark ? Color.FromRgb(44, 44, 48) : Color.FromRgb(242, 242, 247)),
                CornerRadius = new CornerRadius(12, 12, 12, 2),
                Padding = new Thickness(12, 8),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = contentStack,
            };

            var container = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 2, 40, 2),
            };

            // Timestamp
            if (msg.Timestamp.HasValue)
            {
                container.Children.Add(new TextBlock
                {
                    Text = msg.Timestamp.Value.ToString("HH:mm"),
                    FontSize = 10,
                    Foreground = new SolidColorBrush(_isDark ? Color.FromRgb(100, 100, 105) : Color.FromRgb(160, 160, 165)),
                    Margin = new Thickness(4, 0, 0, 2),
                });
            }
            container.Children.Add(bubble);
            return container;
        }
    }

    private void ScrollToBottom()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _scrollViewer.ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    private static int CountLines(string filePath)
    {
        try
        {
            using var stream = new System.IO.FileStream(filePath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
            using var reader = new System.IO.StreamReader(stream);
            int count = 0;
            while (reader.ReadLine() != null) count++;
            return count;
        }
        catch { return 0; }
    }
}
