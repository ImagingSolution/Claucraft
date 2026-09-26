using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Claucraft.Services;

namespace Claucraft.Controls;

/// <summary>
/// Colours and metrics of the chat view, modelled on the Claude desktop app. Shared with the
/// composer that <see cref="Terminal.TerminalControl"/> draws under the transcript.
/// </summary>
public static class ChatTheme
{
    /// <summary>Width of the reading column; the transcript and the composer both centre on it.</summary>
    public const double ColumnMaxWidth = 760;

    // Sampled from the desktop app's own dark and light themes
    public static Color Background(bool isDark) => isDark ? Color.FromRgb(21, 21, 21) : Color.FromRgb(252, 252, 251);
    public static Color UserBubble(bool isDark) => isDark ? Color.FromRgb(33, 33, 33) : Color.FromRgb(240, 240, 239);
    public static Color Surface(bool isDark) => isDark ? Color.FromRgb(32, 32, 31) : Color.FromRgb(255, 255, 255);
    public static Color Outline(bool isDark) => isDark ? Color.FromRgb(55, 55, 54) : Color.FromRgb(223, 223, 222);
    public static Color Hover(bool isDark) => isDark ? Color.FromRgb(40, 40, 40) : Color.FromRgb(240, 240, 239);
    public static readonly Color Accent = Color.FromRgb(217, 119, 87);

    /// <summary>
    /// Prose face. The app ships its own Anthropic Sans; Segoe UI stands in for the Latin and
    /// Yu Gothic UI is what it falls back to for Japanese on Windows.
    /// </summary>
    public static readonly FontFamily BodyFont = new("Segoe UI, Yu Gothic UI, Meiryo UI");

    /// <summary>
    /// Prose size: same as the terminal font size, so the chat follows the user's font-size setting.
    /// </summary>
    public static double BodySize(double terminalFontSize) => terminalFontSize;
}

/// <summary>
/// The chat view: a Claude session's JSONL transcript rendered the way the desktop app shows a
/// conversation - user prompts as grey bubbles on the right, Claude's replies as plain Markdown
/// in a centred reading column, tool calls folded into one-line summaries that expand.
/// </summary>
public class DocumentViewPanel : Panel
{
    private readonly Border _header;
    private readonly TextBlock _titleText;
    private readonly Border _projectChip;
    private readonly TextBlock _projectText;
    private readonly ScrollViewer _scrollViewer;
    private readonly StackPanel _messagesStack;
    private readonly TextBlock _emptyLabel;
    private readonly Button _scrollDownButton;
    private readonly DispatcherTimer _pollTimer;

    // What is on screen, one entry per message, so a poll only rebuilds the changed tail
    private readonly List<string> _keys = new();
    private readonly List<Control?> _views = new();
    private readonly HashSet<string> _expandedGroups = new();

    private string? _currentSessionPath;
    private int _lastLineCount;
    private bool _autoScroll = true;
    private bool _isDark;
    private Typeface _codeTypeface;
    private double _baseFontSize = ChatTheme.BodySize(14);
    private string _fontFamily = "Cascadia Mono, Consolas, Courier New";

    public DocumentViewPanel(bool isDark, Typeface codeTypeface)
    {
        _isDark = isDark;
        _codeTypeface = codeTypeface;
        // Every TextBlock below inherits the prose face; code sets its own
        SetValue(Avalonia.Controls.Documents.TextElement.FontFamilyProperty, ChatTheme.BodyFont);

        _titleText = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _projectText = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        _projectChip = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = _projectText,
            IsVisible = false,
        };
        var headerRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
        // The title may trim, the chip never does
        _titleText.MaxWidth = 520;
        Grid.SetColumn(_projectChip, 1);
        headerRow.Children.Add(_titleText);
        headerRow.Children.Add(_projectChip);
        _header = new Border
        {
            Padding = new Thickness(16, 9),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = headerRow,
        };
        Children.Add(_header);

        _messagesStack = new StackPanel
        {
            Spacing = 10,
            MaxWidth = ChatTheme.ColumnMaxWidth,
            Margin = new Thickness(24, 20, 24, 28),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _emptyLabel = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 60, 0, 0),
            FontSize = 13,
        };
        var scrollContent = new Panel();
        scrollContent.Children.Add(_emptyLabel);
        scrollContent.Children.Add(_messagesStack);

        _scrollViewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = scrollContent,
        };
        _scrollViewer.ScrollChanged += OnScrollChanged;
        Children.Add(_scrollViewer);

        // Round "jump to latest" button, shown once the reader has scrolled away from the end
        _scrollDownButton = new Button
        {
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            IsVisible = false,
            Focusable = false,
        };
        ToolTip.SetTip(_scrollDownButton, Loc.Get("ChatScrollToBottom"));
        _scrollDownButton.Click += (_, _) =>
        {
            _autoScroll = true;
            ScrollToBottom();
        };
        Children.Add(_scrollDownButton);

        ClipToBounds = true;
        ApplyChrome();
        SetEmptyState(Loc.Get("NoSession", "No session loaded"));

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _pollTimer.Tick += OnPollTick;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _header.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        double headerH = _header.DesiredSize.Height;
        _scrollViewer.Measure(new Size(availableSize.Width, Math.Max(0, availableSize.Height - headerH)));
        _scrollDownButton.Measure(new Size(32, 32));
        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double headerH = _header.DesiredSize.Height;
        _header.Arrange(new Rect(0, 0, finalSize.Width, headerH));
        double scrollH = Math.Max(0, finalSize.Height - headerH);
        _scrollViewer.MaxHeight = scrollH;
        _scrollViewer.Arrange(new Rect(0, headerH, finalSize.Width, scrollH));
        _scrollDownButton.Arrange(new Rect((finalSize.Width - 32) / 2, finalSize.Height - 32 - 12, 32, 32));
        return finalSize;
    }

    // ── Public API (driven by TerminalControl) ──

    public void LoadSession(string jsonlPath)
    {
        _currentSessionPath = jsonlPath;
        _autoScroll = true;
        ClearViews();
        Refresh(force: true);
        ScrollToBottom();
    }

    public void StartPolling() => _pollTimer.Start();

    public void StopPolling() => _pollTimer.Stop();

    public void Clear()
    {
        ClearViews();
        _currentSessionPath = null;
        _lastLineCount = 0;
        SetEmptyState(Loc.Get("NoSession", "No session loaded"));
        _titleText.Text = "";
        _projectChip.IsVisible = false;
    }

    public void SetFont(string fontFamily, double fontSize)
    {
        _fontFamily = fontFamily + ", Consolas, Courier New";
        _codeTypeface = new Typeface(_fontFamily);
        _baseFontSize = ChatTheme.BodySize(fontSize);
        Rebuild();
    }

    public void UpdateTheme(bool isDark)
    {
        _isDark = isDark;
        ApplyChrome();
        Rebuild();
    }

    // ── Loading ──

    private void Rebuild()
    {
        ClearViews();
        Refresh(force: true);
    }

    private void ClearViews()
    {
        _messagesStack.Children.Clear();
        _keys.Clear();
        _views.Clear();
    }

    private void OnPollTick(object? sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(_currentSessionPath)) return;
        if (!System.IO.File.Exists(_currentSessionPath)) return;
        if (CountLines(_currentSessionPath) == _lastLineCount) return;

        if (Refresh(force: false) && _autoScroll)
            ScrollToBottom();
    }

    /// <summary>Re-reads the transcript and brings the view in line; true if anything changed.</summary>
    private bool Refresh(bool force)
    {
        var path = _currentSessionPath;
        if (path == null) return false;

        var messages = SessionMessageReader.ReadSession(path);
        _lastLineCount = CountLines(path);
        UpdateHeader(path, messages);

        // Keep the longest prefix that is unchanged and rebuild only what follows it, so
        // expanded groups and the scroll position survive a new reply arriving.
        int common = 0;
        if (!force)
            while (common < _keys.Count && common < messages.Count && _keys[common] == KeyOf(messages[common]))
                common++;

        bool changed = common < _keys.Count || common < messages.Count;
        for (int k = _keys.Count - 1; k >= common; k--)
        {
            if (_views[k] != null) _messagesStack.Children.Remove(_views[k]!);
            _keys.RemoveAt(k);
            _views.RemoveAt(k);
        }
        for (int k = common; k < messages.Count; k++)
        {
            var key = KeyOf(messages[k]);
            var view = CreateMessageView(messages[k], key, k > 0 ? messages[k - 1] : null);
            if (view != null) _messagesStack.Children.Add(view);
            _keys.Add(key);
            _views.Add(view);
        }

        SetEmptyState(_messagesStack.Children.Count == 0 ? "" : null);
        return changed;
    }

    private static string KeyOf(ConversationMessage m)
    {
        var tools = m.Tools == null ? "" : string.Join("\u001F", m.Tools.Select(t => t.Name + ":" + t.Detail));
        var answers = m.AskUser == null ? "" : string.Join("\u001F", m.AskUser.Answers.Select(a => a.Key + "=" + a.Value));
        return $"{(int)m.Role}|{m.IsToolUse}|{m.IsToolRejection}|{m.Images?.Count ?? 0}|{tools}|{answers}|{m.Text}";
    }

    private void UpdateHeader(string path, List<ConversationMessage> messages)
    {
        var meta = SessionMessageReader.ReadSessionMeta(path);
        string? title = meta.Title;
        if (string.IsNullOrWhiteSpace(title))
        {
            var first = messages.FirstOrDefault(m => m.Role == MessageRole.User && !string.IsNullOrWhiteSpace(m.Text));
            title = first?.Text.Split('\n')[0].Trim();
        }
        if (string.IsNullOrWhiteSpace(title))
            title = System.IO.Path.GetFileNameWithoutExtension(path);
        _titleText.Text = title;
        ToolTip.SetTip(_titleText, title);

        var project = string.IsNullOrEmpty(meta.Cwd) ? null : System.IO.Path.GetFileName(meta.Cwd.TrimEnd('\\', '/'));
        _projectText.Text = project ?? "";
        _projectChip.IsVisible = !string.IsNullOrEmpty(project);
        ToolTip.SetTip(_projectChip, meta.Cwd);
    }

    private void SetEmptyState(string? text)
    {
        if (text != null) _emptyLabel.Text = text;
        _emptyLabel.IsVisible = !string.IsNullOrEmpty(text) && _messagesStack.Children.Count == 0;
    }

    // ── Theme ──

    private SolidColorBrush Brush(Color c) => new(c);
    private MarkdownParser.ChatPalette Palette => MarkdownParser.ChatPalette.For(_isDark);

    private void ApplyChrome()
    {
        var pal = Palette;
        Background = Brush(ChatTheme.Background(_isDark));
        _header.Background = Brush(ChatTheme.Background(_isDark));
        _header.BorderBrush = Brush(pal.Border);
        _titleText.Foreground = Brush(pal.Fg);
        _projectChip.Background = Brush(ChatTheme.UserBubble(_isDark));
        _projectText.Foreground = Brush(pal.Dim);
        _emptyLabel.Foreground = Brush(pal.Dim);

        _scrollDownButton.Background = Brush(ChatTheme.Surface(_isDark));
        _scrollDownButton.BorderBrush = Brush(ChatTheme.Outline(_isDark));
        _scrollDownButton.Content = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M1,1 L6,6 L11,1"),
            Stroke = Brush(pal.Dim),
            StrokeThickness = 1.6,
            Width = 11,
            Height = 6,
            Stretch = Stretch.Uniform,
        };
    }

    // ── Message views ──

    private Control? CreateMessageView(ConversationMessage msg, string key, ConversationMessage? previous)
    {
        if (msg.AskUser != null) return CreateAskUserView(msg);
        if (msg.IsToolRejection) return CreateToolRejectionLine(msg);
        if (msg.Role == MessageRole.User) return CreateUserView(msg, previous);
        if (msg.IsToolUse && msg.Tools is { Count: > 0 }) return CreateToolGroup(msg, key);
        if (string.IsNullOrWhiteSpace(msg.Text)) return null;
        if (msg.Role == MessageRole.System) return CreateStatusLine(msg.Text, Palette.Dim);
        return CreateAssistantView(msg);
    }

    private Control CreateUserView(ConversationMessage msg, ConversationMessage? previous)
    {
        var pal = Palette;
        var column = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 6,
            // A new prompt opens a new exchange: give it room above
            Margin = new Thickness(56, previous == null ? 0 : 14, 0, 4),
        };

        if (msg.Images is { Count: > 0 })
        {
            var thumbs = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            foreach (var img in msg.Images)
            {
                var thumb = CreateImageThumb(img);
                if (thumb != null) thumbs.Children.Add(thumb);
            }
            if (thumbs.Children.Count > 0) column.Children.Add(thumbs);
        }

        if (!string.IsNullOrWhiteSpace(msg.Text))
        {
            var bubble = new Border
            {
                Background = Brush(ChatTheme.UserBubble(_isDark)),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(14, 9),
                HorizontalAlignment = HorizontalAlignment.Right,
                MaxWidth = ChatTheme.ColumnMaxWidth * 0.8,
                Child = new SelectableTextBlock
                {
                    Text = msg.Text,
                    FontSize = _baseFontSize,
                    Foreground = Brush(pal.Fg),
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = Math.Round(_baseFontSize * 1.6),
                },
            };
            if (msg.Timestamp.HasValue)
                ToolTip.SetTip(bubble, msg.Timestamp.Value.ToLocalTime().ToString("yyyy/MM/dd HH:mm"));
            column.Children.Add(bubble);
        }
        return column;
    }

    private Control? CreateImageThumb(ChatImage img)
    {
        Bitmap? bitmap = null;
        try
        {
            if (img.Path != null)
            {
                using var fs = System.IO.File.OpenRead(img.Path);
                bitmap = Bitmap.DecodeToWidth(fs, 240);
            }
            else if (img.Base64 != null)
            {
                using var ms = new System.IO.MemoryStream(Convert.FromBase64String(img.Base64));
                bitmap = Bitmap.DecodeToWidth(ms, 240);
            }
        }
        catch { }
        if (bitmap == null) return null;

        var frame = new Border
        {
            Width = 120,
            Height = 120,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            BorderBrush = Brush(ChatTheme.Outline(_isDark)),
            Background = Brush(ChatTheme.UserBubble(_isDark)),
            ClipToBounds = true,
            Margin = new Thickness(6, 0, 0, 6),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new Image { Source = bitmap, Stretch = Stretch.UniformToFill },
        };
        frame.PointerPressed += (_, e) =>
        {
            var path = img.Path ?? SaveInlineImage(img.Base64!);
            if (path != null)
                ImageViewerWindow.Open(path, TopLevel.GetTopLevel(this) as Window);
            e.Handled = true;
        };
        return frame;
    }

    /// <summary>An inline image has no file behind it; the viewer needs one, so write it to temp.</summary>
    private static string? SaveInlineImage(string base64)
    {
        try
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Claucraft");
            System.IO.Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, $"chat_{(uint)base64.GetHashCode():x8}.png");
            if (!System.IO.File.Exists(path))
                System.IO.File.WriteAllBytes(path, Convert.FromBase64String(base64));
            return path;
        }
        catch { return null; }
    }

    private Control CreateAssistantView(ConversationMessage msg)
    {
        var content = new StackPanel { Spacing = 2 };
        foreach (var ctrl in MarkdownParser.Parse(msg.Text, _isDark, _codeTypeface, _baseFontSize, chatStyle: true))
            content.Children.Add(ctrl);

        // Copy action under the reply, revealed on hover as in the desktop app
        var copyIcon = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M5,5 H13 V13 H5 Z M3,10 H1 V1 H10 V3"),
            Stroke = Brush(Palette.Dim),
            StrokeThickness = 1.2,
            Width = 13,
            Height = 13,
            Stretch = Stretch.Uniform,
        };
        var copyButton = new Button
        {
            Content = copyIcon,
            Padding = new Thickness(6),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Opacity = 0,
            IsHitTestVisible = false,
            Focusable = false,
            // Hangs below the reply instead of adding a row, so replies stay tightly spaced
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(-6, 0, 0, -26),
        };
        ToolTip.SetTip(copyButton, Loc.Get("CopyCode", "Copy"));
        var text = msg.Text;
        copyButton.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(copyButton)?.Clipboard;
            if (clipboard != null) await clipboard.SetTextAsync(text);
            copyButton.Content = new TextBlock { Text = "✓", FontSize = 12, Foreground = Brush(Palette.Dim) };
            await System.Threading.Tasks.Task.Delay(1500);
            copyButton.Content = copyIcon;
        };

        var view = new Panel { Background = Brushes.Transparent };
        view.Children.Add(content);
        view.Children.Add(copyButton);
        view.PointerEntered += (_, _) => { copyButton.Opacity = 1; copyButton.IsHitTestVisible = true; };
        view.PointerExited += (_, _) => { copyButton.Opacity = 0; copyButton.IsHitTestVisible = false; };
        return view;
    }

    /// <summary>
    /// A run of tool calls as one quiet line - "ran 3 commands, read 2 files ›" - that opens
    /// into the individual calls.
    /// </summary>
    private Control CreateToolGroup(ConversationMessage msg, string key)
    {
        var pal = Palette;
        var tools = msg.Tools!;
        bool expanded = _expandedGroups.Contains(key);

        var chevron = new TextBlock
        {
            Text = expanded ? "⌄" : "›",
            FontSize = _baseFontSize,
            Foreground = Brush(pal.Dim),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var summary = new TextBlock
        {
            Text = SummarizeTools(tools),
            FontSize = _baseFontSize * 0.95,
            Foreground = Brush(pal.Dim),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var headerRow = new StackPanel { Orientation = Orientation.Horizontal };
        headerRow.Children.Add(summary);
        headerRow.Children.Add(chevron);
        var header = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 3),
            Margin = new Thickness(-6, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = headerRow,
        };

        var details = new StackPanel
        {
            Spacing = 3,
            Margin = new Thickness(2, 4, 0, 2),
            IsVisible = expanded,
        };
        foreach (var tool in tools)
        {
            var line = new SelectableTextBlock
            {
                FontSize = _baseFontSize * 0.88,
                Foreground = Brush(pal.Dim),
                TextWrapping = TextWrapping.Wrap,
            };
            line.Inlines!.Add(new Avalonia.Controls.Documents.Run(tool.Name)
            {
                FontWeight = FontWeight.SemiBold,
                Foreground = Brush(pal.Fg),
            });
            if (!string.IsNullOrEmpty(tool.Detail))
            {
                line.Inlines.Add(new Avalonia.Controls.Documents.Run("  "));
                line.Inlines.Add(new Avalonia.Controls.Documents.Run(tool.Detail)
                {
                    FontFamily = new FontFamily(_codeTypeface.FontFamily.Name),
                });
            }
            details.Children.Add(line);
        }
        var detailCard = new Border
        {
            BorderBrush = Brush(pal.Border),
            BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(10, 2, 0, 2),
            Margin = new Thickness(0, 2, 0, 0),
            Child = details,
            IsVisible = expanded,
        };
        details.IsVisible = true;

        header.PointerEntered += (_, _) => header.Background = Brush(ChatTheme.Hover(_isDark));
        header.PointerExited += (_, _) => header.Background = Brushes.Transparent;
        header.PointerPressed += (_, e) =>
        {
            bool open = !detailCard.IsVisible;
            detailCard.IsVisible = open;
            chevron.Text = open ? "⌄" : "›";
            if (open) _expandedGroups.Add(key); else _expandedGroups.Remove(key);
            e.Handled = true;
        };

        var group = new StackPanel();
        group.Children.Add(header);
        group.Children.Add(detailCard);
        return group;
    }

    /// <summary>"ran 2 commands, read a file" in the UI language, grouped by what the tools do.</summary>
    internal static string SummarizeTools(IReadOnlyList<ToolCall> tools)
    {
        int commands = 0, reads = 0, edits = 0, searches = 0, agents = 0, web = 0;
        var skills = new List<string>();
        var others = new List<string>();
        foreach (var t in tools)
        {
            switch (t.Name)
            {
                case "Bash": case "PowerShell": case "BashOutput": case "Monitor": commands++; break;
                case "Read": reads++; break;
                case "Edit": case "Write": case "MultiEdit": case "NotebookEdit": edits++; break;
                case "Grep": case "Glob": case "LSP": searches++; break;
                case "Agent": case "Task": agents++; break;
                case "WebFetch": case "WebSearch": web++; break;
                case "Skill": skills.Add(t.Detail ?? "Skill"); break;
                default:
                    var name = t.Name.StartsWith("mcp__", StringComparison.Ordinal) ? t.Name.Split("__")[^1] : t.Name;
                    if (!others.Contains(name)) others.Add(name);
                    break;
            }
        }

        var parts = new List<string>();
        void Add(string key, int n)
        {
            if (n > 0) parts.Add(string.Format(Loc.Get(key), n, n == 1 ? "" : "s", n == 1 ? "" : "es"));
        }
        foreach (var s in skills) parts.Add(string.Format(Loc.Get("ChatToolSkill"), s));
        Add("ChatToolCommands", commands);
        Add("ChatToolRead", reads);
        Add("ChatToolEdit", edits);
        Add("ChatToolSearch", searches);
        Add("ChatToolAgent", agents);
        Add("ChatToolWeb", web);
        if (others.Count > 0) parts.Add(string.Format(Loc.Get("ChatToolOther"), string.Join(", ", others)));

        var text = string.Join(Loc.Get("ChatToolJoin"), parts);
        return text.Length > 0 ? char.ToUpperInvariant(text[0]) + text[1..] : text;
    }

    private Control CreateStatusLine(string text, Color color) => new TextBlock
    {
        Text = text,
        FontSize = _baseFontSize * 0.9,
        Foreground = Brush(color),
        TextTrimming = TextTrimming.CharacterEllipsis,
        Margin = new Thickness(0, 1),
    };

    private Control CreateToolRejectionLine(ConversationMessage msg)
    {
        var color = _isDark ? Color.FromRgb(240, 120, 110) : Color.FromRgb(190, 60, 50);
        var text = msg.ToolName != null ? string.Format(Loc.Get("ChatToolRejected"), msg.ToolName) : msg.Text;
        return CreateStatusLine("⊘ " + text, color);
    }

    // ── AskUserQuestion ──

    private Control? CreateAskUserView(ConversationMessage msg)
    {
        if (msg.AskUser == null) return null;

        var container = new StackPanel { Spacing = 8, Margin = new Thickness(0, 2) };
        foreach (var question in msg.AskUser.Questions)
        {
            container.Children.Add(CreateQuestionCard(question, msg.AskUser.Answers, msg.AskUser.Notes));

            // The answer reads as the user's reply
            if (msg.AskUser.Answers.TryGetValue(question.Question, out var answer) && answer != null)
            {
                container.Children.Add(new Border
                {
                    Background = Brush(ChatTheme.UserBubble(_isDark)),
                    CornerRadius = new CornerRadius(14),
                    Padding = new Thickness(14, 9),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    MaxWidth = ChatTheme.ColumnMaxWidth * 0.8,
                    Margin = new Thickness(56, 0, 0, 0),
                    Child = new SelectableTextBlock
                    {
                        Text = answer,
                        FontSize = _baseFontSize,
                        Foreground = Brush(Palette.Fg),
                        TextWrapping = TextWrapping.Wrap,
                    },
                });
            }
        }
        return container;
    }

    private Control CreateQuestionCard(
        AskUserQuestionItem question,
        Dictionary<string, string> answers,
        Dictionary<string, string>? notes)
    {
        var pal = Palette;
        var accent = ChatTheme.Accent;
        var selectedBg = _isDark ? Color.FromArgb(40, 217, 119, 87) : Color.FromArgb(28, 217, 119, 87);

        var stack = new StackPanel { Spacing = 6 };
        if (!string.IsNullOrWhiteSpace(question.Header))
        {
            stack.Children.Add(new TextBlock
            {
                Text = question.Header,
                FontSize = _baseFontSize * 0.85,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brush(pal.Dim),
            });
        }
        stack.Children.Add(new SelectableTextBlock
        {
            Text = question.Question,
            FontSize = _baseFontSize,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush(pal.Fg),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
        });

        answers.TryGetValue(question.Question, out var selectedAnswer);
        bool answerMatchesOption = question.Options.Any(o => o.Label == selectedAnswer);

        foreach (var option in question.Options)
        {
            bool isSelected = selectedAnswer != null && selectedAnswer == option.Label;
            var labelRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            labelRow.Children.Add(new TextBlock
            {
                Text = isSelected ? "●" : "○",
                FontSize = _baseFontSize - 2,
                Foreground = Brush(isSelected ? accent : pal.Dim),
                VerticalAlignment = VerticalAlignment.Center,
            });
            labelRow.Children.Add(new TextBlock
            {
                Text = option.Label,
                FontSize = _baseFontSize * 0.95,
                Foreground = Brush(pal.Fg),
                FontWeight = isSelected ? FontWeight.SemiBold : FontWeight.Normal,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var optionStack = new StackPanel { Spacing = 1 };
            optionStack.Children.Add(labelRow);
            if (!string.IsNullOrWhiteSpace(option.Description))
            {
                optionStack.Children.Add(new TextBlock
                {
                    Text = option.Description,
                    FontSize = _baseFontSize * 0.85,
                    Foreground = Brush(pal.Dim),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(20, 0, 0, 0),
                });
            }
            stack.Children.Add(new Border
            {
                Background = isSelected ? Brush(selectedBg) : Brushes.Transparent,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 5),
                Child = optionStack,
            });
        }

        // A typed answer that is none of the options
        if (selectedAnswer != null && !answerMatchesOption)
        {
            stack.Children.Add(new Border
            {
                Background = Brush(selectedBg),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 5),
                Child = new SelectableTextBlock
                {
                    Text = "✎ " + selectedAnswer,
                    FontSize = _baseFontSize * 0.95,
                    Foreground = Brush(pal.Fg),
                    TextWrapping = TextWrapping.Wrap,
                },
            });
        }

        if (notes != null && notes.TryGetValue(question.Question, out var userNotes)
            && !string.IsNullOrWhiteSpace(userNotes))
        {
            stack.Children.Add(new Border
            {
                BorderBrush = Brush(pal.Border),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(0, 6, 0, 0),
                Margin = new Thickness(0, 2, 0, 0),
                Child = new TextBlock
                {
                    Text = "Note: " + userNotes,
                    FontSize = _baseFontSize * 0.85,
                    FontStyle = FontStyle.Italic,
                    Foreground = Brush(pal.Dim),
                    TextWrapping = TextWrapping.Wrap,
                },
            });
        }

        return new Border
        {
            Background = Brush(ChatTheme.Surface(_isDark)),
            BorderBrush = Brush(ChatTheme.Outline(_isDark)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 10),
            Margin = new Thickness(0, 0, 56, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = ChatTheme.ColumnMaxWidth * 0.85,
            Child = stack,
        };
    }

    // ── Scrolling ──

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        var sv = _scrollViewer;
        double fromBottom = sv.Extent.Height - sv.Viewport.Height - sv.Offset.Y;
        // Content growing under the reader is not the reader scrolling away
        if (e.ExtentDelta.Y == 0 || fromBottom < 20)
            _autoScroll = fromBottom < 20;
        _scrollDownButton.IsVisible = fromBottom > 80;
    }

    private void ScrollToBottom()
    {
        Dispatcher.UIThread.Post(() => _scrollViewer.ScrollToEnd(), DispatcherPriority.Background);
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
