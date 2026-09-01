using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Claucraft.Terminal;

public class TerminalControl : Control, IDisposable
{
    private TerminalBuffer _buffer;
    private VtParser _parser;
    private PseudoConsole? _pty;
    private double _cellWidth;
    private double _cellHeight;
    private Typeface _typeface;
    private double _fontSize = 14;
    private bool _disposed;
    private string? _workingDirectory;
    private bool _isDark = true;

    // Selection state
    private bool _isSelecting;
    private bool _hasSelection;
    private int _selStartRow, _selStartCol;
    private int _selEndRow, _selEndCol;

    // Input boundary tracking: records where editable input begins
    private bool _inputStartPending = true;
    private int _inputStartAbsRow;
    private int _inputStartCol;

    // Click-to-move caret: guards the arrow-key convergence loop against re-entry
    private bool _caretMoveInProgress;

    // Scrollbar drag state
    private bool _isScrollbarDragging;
    private double _scrollbarDragStartY;
    private int _scrollbarDragStartOffset;
    private const double ScrollbarWidth = 10;
    private const double ScrollbarThumbMinHeight = 20;

    // Input TextBox at bottom
    private readonly TextBox _inputTextBox;
    private readonly Button _expandButton;

    /// <summary>Interrupts the turn. One for each input layout; only one is ever on screen.</summary>
    private readonly Button _stopButton;
    private Button _expandedStopButton = null!;

    private const double InputBoxHeight = 28;
    private const double InputBoxMargin = 2;
    private const double ExpandButtonWidth = 32;

    // Indeterminate progress line pinned to the bottom edge of the input row
    private readonly Controls.MarqueeBar _marquee;

    // Expanded input panel
    private Border _expandedPanel = null!;
    private TextBox _expandedTextBox = null!;
    private Border _dragHandle = null!;
    private Button _collapseButton = null!;
    private Button _sendButton = null!;
    private Button _attachButton = null!;
    private bool _isExpanded;
    private double _expandedHeight; // absolute pixels
    private bool _isDragResizing;
    private double _dragResizeStartY;
    private double _dragResizeStartHeight;

    // Search bar state
    private Border? _searchBar;
    private TextBox? _searchTextBox;
    private TextBlock? _searchCountLabel;
    private bool _searchVisible;
    private string _searchTerm = "";
    private bool _searchRegex;
    private bool _searchCaseSensitive;
    private ToggleButton? _searchRegexToggle;
    private ToggleButton? _searchCaseToggle;
    private readonly List<(int absRow, int col, int length)> _searchMatches = new();
    private int _searchCurrentIndex = -1;

    // Prompt navigation state: tracks absolute row positions where user submitted input
    private readonly List<int> _userInputRows = new();
    private Border? _promptNavBar;
    private TextBlock? _promptNavLabel;
    private int _promptNavCurrentIndex = -1;

    // Chart/diagram rendering state
    private readonly CodeBlockDetector _codeBlockDetector = new();
    private DispatcherTimer? _codeBlockScanTimer;
    private bool _codeBlockScanPending;
    private int _lastCachedBlockCount;
    private readonly List<CodeBlockInfo> _cachedDiagrams = new();
    // Cache for parsed Excalidraw elements to survive terminal reflow on resize
    private List<System.Text.Json.JsonElement>? _excalidrawCacheDrawables;
    private double _excalidrawCacheMinX, _excalidrawCacheMinY, _excalidrawCacheMaxX, _excalidrawCacheMaxY;
    public bool EnableChartRendering { get; set; } = true;

    /// <summary>
    /// Whether to watch for the CLI's permission prompt and offer the Yes/Always/No overlay.
    /// The detection strings and the 1/2/3 replies are Claude Code specific, so other CLIs
    /// turn this off.
    /// </summary>
    public bool EnablePermissionOverlay { get; set; } = true;

    /// <summary>
    /// Command written to the PTY on shutdown, e.g. "/exit\r". Empty means terminate the
    /// process directly instead.
    /// </summary>
    public string ExitCommand { get; set; } = "/exit\r";

    // Document view mode state
    private bool _isDocumentView;
    private Controls.DocumentViewPanel? _docViewPanel;
    private string? _docViewSessionPath;
    private DispatcherTimer? _permissionCheckTimer;
    private Border? _permissionOverlay;
    public bool IsDocumentView => _isDocumentView;
    public event Action<bool>? DocumentViewChanged;

    public string TabTitle { get; private set; } = "Console";
    public bool IsManualTitle { get; set; }
    public string? FirstUserInput { get; set; }
    private bool _firstInputCaptured;
    private readonly System.Text.StringBuilder _firstInputBuffer = new();
    public event Action<string>? TitleChanged;
    public event Action? Exited;

    /// <summary>Raised whenever the user submits a prompt, carrying the text when it is known.</summary>
    public event Action<string?>? PromptSubmitted;

    /// <summary>True while the CLI process is alive.</summary>
    public bool IsProcessRunning => _pty?.IsRunning == true;
    public event Action? Clicked;
    public event Action<double>? FontSizeChanged;

    public bool IsDarkTheme
    {
        get => _isDark;
        set
        {
            _isDark = value;
            ApplyThemeColors();
        }
    }

    private void ApplyThemeColors()
    {
        var fg = _isDark ? Color.FromRgb(210, 210, 215) : Color.FromRgb(85, 87, 83);       // Light: Tango foreground
        var bg = _isDark ? Color.FromRgb(44, 44, 46) : Color.FromRgb(242, 242, 242);      // Light: Tango input bg
        var bgDeep = _isDark ? Color.FromRgb(34, 34, 36) : Color.FromRgb(255, 255, 255);  // Light: white
        var border = _isDark ? Color.FromRgb(56, 56, 58) : Color.FromRgb(198, 198, 200);
        var subtle = _isDark ? Color.FromRgb(160, 160, 165) : Color.FromRgb(100, 100, 105);

        _inputTextBox.Foreground = new SolidColorBrush(fg);
        _inputTextBox.Background = new SolidColorBrush(bg);
        _inputTextBox.BorderBrush = new SolidColorBrush(border);

        _expandButton.Background = new SolidColorBrush(bg);
        _expandButton.Foreground = new SolidColorBrush(subtle);
        _expandButton.BorderBrush = new SolidColorBrush(border);

        // Expanded panel
        _expandedPanel.Background = new SolidColorBrush(bgDeep);
        _expandedPanel.BorderBrush = new SolidColorBrush(border);
        _expandedTextBox.Background = new SolidColorBrush(bgDeep);
        _expandedTextBox.Foreground = new SolidColorBrush(fg);
        _expandedTextBox.CaretBrush = new SolidColorBrush(fg);
        _dragHandle.Background = new SolidColorBrush(border);
        _collapseButton.Background = new SolidColorBrush(bg);
        _collapseButton.Foreground = new SolidColorBrush(subtle);
        _sendButton.Background = new SolidColorBrush(Color.FromRgb(0, 122, 255));

        // Search bar
        if (_searchBar != null)
        {
            _searchBar.Background = new SolidColorBrush(_isDark ? Color.FromRgb(38, 38, 40) : Color.FromRgb(245, 245, 248));
            _searchBar.BorderBrush = new SolidColorBrush(border);
        }
        if (_searchTextBox != null)
        {
            _searchTextBox.Background = new SolidColorBrush(bg);
            _searchTextBox.Foreground = new SolidColorBrush(fg);
            _searchTextBox.BorderBrush = new SolidColorBrush(border);
        }

        // Document view theme
        _docViewPanel?.UpdateTheme(_isDark);

        InvalidateVisual();
    }

    // Terminal area height = total height - input area - expanded panel
    private double ExpandedPanelHeight => _isExpanded ? _expandedHeight : 0;
    private double InputAreaHeight => _isExpanded ? 0 : InputBoxHeight + InputBoxMargin;
    private double TerminalAreaHeight => Math.Max(0, Bounds.Height - InputAreaHeight - ExpandedPanelHeight);

    public void SetFont(string fontFamily, double fontSize)
    {
        _typeface = new Typeface(fontFamily + ", Consolas, Courier New");
        _fontSize = fontSize;
        _inputTextBox.FontFamily = new FontFamily(fontFamily + ", Consolas, Courier New");
        _inputTextBox.FontSize = fontSize;
        _docViewPanel?.SetFont(fontFamily, fontSize);
        MeasureCellSize();
        RecalcTerminalSize();
        InvalidateVisual();
    }

    public TerminalControl()
    {
        _typeface = new Typeface("Cascadia Mono, Consolas, Courier New, monospace");
        _buffer = new TerminalBuffer(24, 80);
        _parser = new VtParser(_buffer);
        _parser.TitleChanged += title =>
        {
            TabTitle = title;
            Dispatcher.UIThread.Post(() => TitleChanged?.Invoke(title));
        };

        ClipToBounds = true;

        // Built here but added to the visual tree last, so it paints over the input row
        _marquee = new Controls.MarqueeBar();

        // Create input TextBox at the bottom
        _inputTextBox = new TextBox
        {
            Background = new SolidColorBrush(Color.FromRgb(44, 44, 46)),   // Apple elevated surface
            Foreground = new SolidColorBrush(Color.FromRgb(210, 210, 215)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(56, 56, 58)),  // Apple separator
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(6, 4),
            FontSize = _fontSize,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New, monospace"),
            PlaceholderText = "IME input here — auto-sent on commit",
            Focusable = true,
            AcceptsReturn = false,
        };

        // Handle Enter key to send text to PTY
        _inputTextBox.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);

        // Intercept TextInput: half-width chars go directly to PTY,
        // full-width (IME committed) chars also go to PTY immediately
        _inputTextBox.AddHandler(TextInputEvent, OnInputTextInput, RoutingStrategies.Tunnel);

        // Forward click to activate MDI window
        _inputTextBox.PointerPressed += (s, e) => Clicked?.Invoke();

        // Expand button (▲)
        _expandButton = new Button
        {
            Content = "\u25B2",
            FontSize = 10,
            Background = new SolidColorBrush(Color.FromRgb(44, 44, 46)),
            Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 165)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(56, 56, 58)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(6, 0),
            Width = ExpandButtonWidth,
            CornerRadius = new CornerRadius(0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Focusable = false,
        };
        ToolTip.SetTip(_expandButton, "Expand input (multi-line)");
        _expandButton.Click += (_, _) => ToggleExpandedMode();

        // Stop, for the collapsed input row. Sits at the right end of the text box and only
        // while this session is mid-turn - it belongs to the window that is working, which is
        // not always the one the status bar is describing.
        // Stretch rather than a fixed height: the button then fills the arranged row exactly as
        // the expander beside it does, instead of sitting short with a gap above it.
        _stopButton = NewStopButton(new Thickness(8, 0));
        _stopButton.VerticalAlignment = VerticalAlignment.Stretch;
        _stopButton.CornerRadius = new CornerRadius(0);
        _stopButton.BorderThickness = new Thickness(0, 1, 0, 0);
        _stopButton.BorderBrush = new SolidColorBrush(Color.FromRgb(56, 56, 58));

        // Build expanded input panel
        BuildExpandedPanel();

        VisualChildren.Add(_inputTextBox);
        LogicalChildren.Add(_inputTextBox);
        VisualChildren.Add(_stopButton);
        LogicalChildren.Add(_stopButton);
        VisualChildren.Add(_expandButton);
        LogicalChildren.Add(_expandButton);
        VisualChildren.Add(_expandedPanel);
        LogicalChildren.Add(_expandedPanel);

        // Build search bar
        BuildSearchBar();

        // Visual children paint in the order they are added, so the marquee goes last to sit
        // on top of the input row rather than behind the text box's own background.
        VisualChildren.Add(_marquee);
        LogicalChildren.Add(_marquee);

        // Enable file drag & drop
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, OnFileDrop);
        AddHandler(DragDrop.DragOverEvent, OnFileDragOver);

        MeasureCellSize();
        ApplyThemeColors();

        _buffer.BufferChanged += () =>
        {
            Dispatcher.UIThread.Post(InvalidateVisual);
            ScheduleCodeBlockScan();
        };
    }

    private void ScheduleCodeBlockScan()
    {
        if (_codeBlockScanPending) return;
        _codeBlockScanPending = true;

        if (_codeBlockScanTimer == null)
        {
            _codeBlockScanTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _codeBlockScanTimer.Tick += (_, _) =>
            {
                _codeBlockScanTimer.Stop();
                _codeBlockScanPending = false;
                if (!_buffer.IsAltBuffer && EnableChartRendering)
                {
                    _codeBlockDetector.IncrementalScan(_buffer);
                    AutoCacheNewDiagrams();
                    InvalidateVisual();
                }
            };
        }

        _codeBlockScanTimer.Stop();
        _codeBlockScanTimer.Start();
    }

    private static bool IsHalfWidth(string text)
    {
        foreach (char c in text)
        {
            if (c > '\u007E') return false;
        }
        return true;
    }

    /// <summary>
    /// Set to true when IME text is committed via TextInput.
    /// On the next KeyDown, any TextBox remnants are force-cleared.
    /// </summary>
    private bool _imeJustCommitted;

    private void OnInputTextInput(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;

        // Filter out control characters (e.g., Backspace generates '\b' via TextInput
        // which would double-send with the KeyDown handler's \x7f)
        foreach (char c in e.Text)
        {
            if (c >= ' ') // Only process printable characters (U+0020 and above)
            {
                goto hasPrintable;
            }
        }
        return; // All control characters — ignore
    hasPrintable:

        // Document view mode: accumulate all text in the input box until Enter
        if (_isDocumentView)
        {
            // Let the TextBox handle the input naturally (don't send to PTY)
            // Text stays in the input box until Enter is pressed
            return;
        }

        // Track input start on first text input after prompt
        if (_inputStartPending)
        {
            _inputStartAbsRow = ScreenRowToAbsolute(_buffer.CursorRow);
            _inputStartCol = _buffer.CursorCol;
            _inputStartPending = false;
            System.Diagnostics.Debug.WriteLine($"[InputStart] recorded at ({_inputStartAbsRow},{_inputStartCol})");
        }

        // Capture first user input for tab title
        if (!_firstInputCaptured)
            _firstInputBuffer.Append(e.Text);

        // Printable text committed (half-width direct or IME confirmed) — send to PTY
        if (_hasSelection) ClearSelection();
        _pty?.WriteInput(e.Text);
        e.Handled = true;

        // Mark that we just committed, so next KeyDown clears remnants
        _imeJustCommitted = true;
        _inputTextBox.Text = "";
    }

    private async void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        // After IME commit, force-clear any preedit remnants that Avalonia
        // may have re-inserted after our clear in OnInputTextInput
        if (_imeJustCommitted && !_isDocumentView)
        {
            _imeJustCommitted = false;
            _inputTextBox.Text = "";
            _inputTextBox.CaretIndex = 0;
        }

        // In document view: text stays in input box, allow editing freely
        // Only intercept Enter, Escape, and Ctrl shortcuts
        if (_isDocumentView && !string.IsNullOrEmpty(_inputTextBox.Text))
        {
            if (e.Key == Key.Escape)
            {
                _inputTextBox.Text = "";
                e.Handled = true;
                return;
            }
            // Let Enter through to be handled below (sends accumulated text)
            if (e.Key == Key.Enter)
                goto handleKeys;
            // Let Ctrl shortcuts through
            bool isCtrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            if (!isCtrl)
                return; // Let TextBox handle normal editing (Backspace, arrows, etc.)
        }

        // If TextBox has text, IME composition is in progress.
        // Let TextBox handle keys (Backspace deletes preedit, etc.)
        // Exception: Ctrl+C/V/F must always work regardless of IME state
        if (!string.IsNullOrEmpty(_inputTextBox.Text))
        {
            if (e.Key == Key.Escape)
            {
                _inputTextBox.Text = "";
                e.Handled = true;
                return;
            }
            bool isCtrlShortcut = e.KeyModifiers.HasFlag(KeyModifiers.Control)
                && e.Key is Key.C or Key.V or Key.F or Key.Up or Key.Down;
            if (!isCtrlShortcut)
                return;
        }
        handleKeys:

        // Track input start: record cursor position on first interaction after prompt
        if (_inputStartPending)
        {
            _inputStartAbsRow = ScreenRowToAbsolute(_buffer.CursorRow);
            _inputStartCol = _buffer.CursorCol;
            _inputStartPending = false;
            System.Diagnostics.Debug.WriteLine($"[InputStart] recorded at ({_inputStartAbsRow},{_inputStartCol})");
        }

        // Ctrl+C: copy selection or send SIGINT
        if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (_hasSelection)
            {
                var text = GetSelectedText();
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard != null && !string.IsNullOrEmpty(text))
                    await clipboard.SetTextAsync(text);
                ClearSelection();
            }
            else
            {
                _inputStartPending = true;
                _pty?.WriteInput("\x03");
            }
            e.Handled = true;
            return;
        }

        // Ctrl+F: toggle search bar
        if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (_searchVisible) HideSearchBar(); else ShowSearchBar();
            e.Handled = true;
            return;
        }

        // Ctrl+Up/Down: navigate between user prompts
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.Key == Key.Up)
            {
                System.Diagnostics.Debug.WriteLine($"[PromptNav] Ctrl+Up pressed. _userInputRows={_userInputRows.Count}, scrollback={_buffer.Scrollback.Count}");
                NavigatePrompt(-1);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Down)
            {
                System.Diagnostics.Debug.WriteLine($"[PromptNav] Ctrl+Down pressed. _userInputRows={_userInputRows.Count}, scrollback={_buffer.Scrollback.Count}");
                NavigatePrompt(1);
                e.Handled = true;
                return;
            }
        }

        // Ctrl+0: reset font size to default
        if (e.Key == Key.D0 && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            SetFont(_typeface.FontFamily.Name, 14);
            FontSizeChanged?.Invoke(14);
            e.Handled = true;
            return;
        }

        // Ctrl+V: paste
        if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (_isDocumentView)
            {
                // Document view: paste into IME input box
                _ = PasteToInputBoxAsync();
            }
            else
            {
                // Terminal mode: paste directly to PTY
                _ = PasteFromClipboardAsync();
            }
            e.Handled = true;
            return;
        }

        // Shift+Enter: send newline (line feed) for multi-line input
        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            _pty?.WriteInput("\n");
            e.Handled = true;
            return;
        }

        // Enter: send text to PTY
        if (e.Key == Key.Enter)
        {
            // Document view mode: send accumulated text from input box, then \r
            if (_isDocumentView && !string.IsNullOrEmpty(_inputTextBox.Text))
            {
                var text = _inputTextBox.Text;
                PromptSubmitted?.Invoke(text);
                _pty?.WriteInput(text);
                _pty?.WriteInput("\r");
                _inputTextBox.Text = "";
                _inputTextBox.CaretIndex = 0;

                // Capture first input as tab title
                if (!_firstInputCaptured)
                {
                    _firstInputCaptured = true;
                    FirstUserInput = text.Trim();
                    var summary = FirstUserInput;
                    if (summary.Length > 30) summary = summary[..30] + "...";
                    if (!string.IsNullOrWhiteSpace(summary))
                        TitleChanged?.Invoke(summary);
                }
                e.Handled = true;
                return;
            }

            // Record input position for prompt navigation
            int submitRow = ScreenRowToAbsolute(_buffer.CursorRow);
            // Only record if it's a different position from the last recorded one
            if (_userInputRows.Count == 0 || Math.Abs(_userInputRows[^1] - _inputStartAbsRow) > 1)
                _userInputRows.Add(_inputStartAbsRow);

            // Capture first input as tab title
            if (!_firstInputCaptured && _firstInputBuffer.Length > 0)
            {
                _firstInputCaptured = true;
                FirstUserInput = _firstInputBuffer.ToString().Trim();
                var summary = FirstUserInput;
                if (summary.Length > 30) summary = summary[..30] + "...";
                if (!string.IsNullOrWhiteSpace(summary))
                    TitleChanged?.Invoke(summary);
            }
            PromptSubmitted?.Invoke(ReadSubmittedLine());
            _inputStartPending = true;
            _pty?.WriteInput("\r");
            e.Handled = true;
            return;
        }

        // Escape: collapse expanded panel if open, otherwise send to PTY
        if (e.Key == Key.Escape)
        {
            if (_isExpanded)
                CollapseInputPanel();
            else
                _pty?.WriteInput("\x1b");
            e.Handled = true;
            return;
        }

        // Backspace / Delete with selection: delete all selected characters
        if (_hasSelection && (e.Key == Key.Back || e.Key == Key.Delete))
        {
            DeleteSelectedChars();
            e.Handled = true;
            return;
        }

        // Forward navigation/editing keys directly to PTY
        {
            string? seq = e.Key switch
            {
                Key.Back => "\x7f",
                Key.Delete => "\x1b[3~",
                Key.Up => "\x1b[A",
                Key.Down => "\x1b[B",
                Key.Left => "\x1b[D",
                Key.Right => "\x1b[C",
                Key.Home => "\x1b[H",
                Key.End => "\x1b[F",
                Key.PageUp => "\x1b[5~",
                Key.PageDown => "\x1b[6~",
                Key.Tab => e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? "\x1b[Z" : "\t",
                Key.F1 => "\x1bOP",
                Key.F2 => "\x1bOQ",
                Key.F3 => "\x1bOR",
                Key.F4 => "\x1bOS",
                Key.F5 => "\x1b[15~",
                Key.F6 => "\x1b[17~",
                Key.F7 => "\x1b[18~",
                Key.F8 => "\x1b[19~",
                Key.F9 => "\x1b[20~",
                Key.F10 => "\x1b[21~",
                Key.F11 => "\x1b[23~",
                Key.F12 => "\x1b[24~",
                _ => null
            };

            if (seq != null)
            {
                _pty?.WriteInput(seq);
                e.Handled = true;
                return;
            }
        }

        // Ctrl+D: send EOF
        if (e.Key == Key.D && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _pty?.WriteInput("\x04");
            e.Handled = true;
            return;
        }

        // Ctrl+Z: send SIGTSTP
        if (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _pty?.WriteInput("\x1a");
            e.Handled = true;
            return;
        }

        // Ctrl+L: clear screen
        if (e.Key == Key.L && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _pty?.WriteInput("\x0c");
            e.Handled = true;
            return;
        }

        // Generic Ctrl+letter: send corresponding control character to PTY
        // (Ctrl+A=0x01, Ctrl+B=0x02, ..., Ctrl+O=0x0F, ..., Ctrl+Z=0x1A)
        // Only pure Ctrl (no Shift/Alt) to avoid hijacking Ctrl+Shift shortcuts
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt)) == KeyModifiers.Control
            && e.Key >= Key.A && e.Key <= Key.Z)
        {
            char controlChar = (char)(e.Key - Key.A + 1);
            _pty?.WriteInput(controlChar.ToString());
            e.Handled = true;
            return;
        }
    }

    private async Task PasteFromClipboardAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;

        // Check for image data in clipboard (same behavior as Claude Code CLI).
        // TryGetBitmapAsync covers every bitmap flavour Windows offers (PNG, CF_DIB, …),
        // so the format probing the old clipboard API needed is no longer necessary.
        try
        {
            var bitmap = await clipboard.TryGetBitmapAsync();
            if (bitmap != null)
            {
                var tempPath = SaveClipboardImage(bitmap);
                if (tempPath != null)
                {
                    var pathStr = tempPath.Contains(' ') ? $"\"{tempPath}\"" : tempPath;
                    _pty?.WriteInput(pathStr);
                    return;
                }
            }
        }
        catch { }

        // Fallback: paste text
        var text = await clipboard.TryGetTextAsync();
        if (!string.IsNullOrEmpty(text))
        {
            if (_buffer.BracketedPasteMode)
                _pty?.WriteInput("\x1b[200~" + text + "\x1b[201~");
            else
                _pty?.WriteInput(text);
        }
    }

    private static string? SaveClipboardImage(Avalonia.Media.Imaging.Bitmap image)
    {
        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "Claucraft");
            Directory.CreateDirectory(tempDir);
            var fileName = $"clipboard_{DateTime.Now:yyyyMMdd_HHmmss}.png";
            var filePath = Path.Combine(tempDir, fileName);
            image.Save(filePath, PngBitmapEncoderOptions.Default);
            return filePath;
        }
        catch
        {
            return null;
        }
    }

    // Scroll offset: 0 = bottom (live), >0 = scrolled up into history
    private int _scrollOffset;

    private void MeasureCellSize()
    {
        var ft = new FormattedText("M", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            _typeface, _fontSize, Brushes.White);
        _cellWidth = ft.Width;
        _cellHeight = ft.Height;
    }

    private void RecalcTerminalSize()
    {
        if (_isDocumentView) return; // Don't resize PTY while in document view
        if (_cellWidth <= 0 || _cellHeight <= 0 || Bounds.Width <= 0) return;
        double termH = TerminalAreaHeight;
        int newCols = Math.Max(10, (int)(Bounds.Width / _cellWidth));
        int newRows = Math.Max(5, (int)(termH / _cellHeight));
        if (newCols != _buffer.Cols || newRows != _buffer.Rows)
        {
            _buffer.Resize(newRows, newCols);
            _pty?.Resize(newCols, newRows);
        }
    }

    public void StartProcess(string command, string? workingDirectory = null)
    {
        _workingDirectory = workingDirectory;
        double termH = TerminalAreaHeight;
        int cols = Math.Max(10, (int)(Bounds.Width / _cellWidth));
        int rows = Math.Max(5, (int)(termH / _cellHeight));
        if (cols < 10) cols = 80;
        if (rows < 5) rows = 24;

        _buffer = new TerminalBuffer(rows, cols);
        _parser = new VtParser(_buffer);
        _parser.TitleChanged += title =>
        {
            TabTitle = title;
            Dispatcher.UIThread.Post(() => TitleChanged?.Invoke(title));
        };
        _buffer.BufferChanged += () =>
        {
            Dispatcher.UIThread.Post(InvalidateVisual);
            ScheduleCodeBlockScan();
        };

        _pty = new PseudoConsole();
        _pty.OutputReceived += data =>
        {
            _parser.Process(new ReadOnlySpan<byte>(data));
            _scrollOffset = 0;
            if (_promptNavBar is { IsVisible: true })
                Dispatcher.UIThread.Post(HidePromptNavBar);
        };
        _pty.ProcessExited += () =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                _parser.Process("\r\n[Process exited]\r\n");
                Exited?.Invoke();
            });
        };

        _pty.Start(command, workingDirectory, cols, rows);

        // Claude Code only — other CLIs prompt differently, and the flag is off for them.
        StartPermissionWatch();
    }

    /// <summary>
    /// Whether the CLI is mid-turn. Drives the progress line under the input row; the window
    /// does not have to be the active one for it to run.
    /// </summary>
    public bool IsGenerating
    {
        get => _marquee.IsActive;
        set
        {
            if (_marquee.IsActive == value) return;
            _marquee.IsActive = value;

            // Stop only exists while there is something to stop. The collapsed one takes width
            // from the text box, so the row has to be laid out again either way.
            _stopButton.IsVisible = value;
            _expandedStopButton.IsVisible = value;
            InvalidateMeasure();
            InvalidateArrange();
        }
    }

    /// <summary>
    /// Escape is what actually interrupts the CLI; this is the same key with a label on it, for
    /// anyone who does not know that. Focus goes back to the terminal so the next keystroke
    /// lands where the user expects rather than on the button.
    /// </summary>
    private Button NewStopButton(Thickness padding)
    {
        var button = new Button
        {
            Content = "■ " + Services.Loc.Get("StopTask", "Stop"),
            FontSize = 10,
            Padding = padding,
            Background = new SolidColorBrush(Color.FromArgb(36, 255, 69, 58)),
            Foreground = new SolidColorBrush(Color.FromRgb(255, 69, 58)),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
            // Avalonia's default leaves the label at the top of the button box, which reads as
            // the whole control sitting high in the input row.
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Focusable = false,
            IsVisible = false,
        };
        ToolTip.SetTip(button, Services.Loc.Get("StopTaskTooltip", "Stop what the AI is doing (Esc)"));
        button.Click += (_, _) =>
        {
            SendText("\x1b");
            FocusTerminal();
        };
        return button;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _stopButton.Measure(new Size(availableSize.Width, InputBoxHeight));
        double stopW = _stopButton.IsVisible ? _stopButton.DesiredSize.Width : 0;
        double tbW = Math.Max(0, availableSize.Width - ExpandButtonWidth - stopW);
        _inputTextBox.Measure(new Size(tbW, InputBoxHeight));
        _expandButton.Measure(new Size(ExpandButtonWidth, InputBoxHeight));
        _marquee.Measure(new Size(availableSize.Width, Controls.MarqueeBar.LineHeight));
        if (_isExpanded)
            _expandedPanel.Measure(new Size(availableSize.Width, _expandedHeight));
        _searchBar?.Measure(availableSize);
        if (_isDocumentView && _docViewPanel != null)
        {
            // Use Bounds for actual size (availableSize may be Infinity)
            double actualH = Bounds.Height > 0 ? Bounds.Height : availableSize.Height;
            double docH = Math.Max(0, actualH - InputAreaHeight - ExpandedPanelHeight);
            _docViewPanel.Measure(new Size(
                Bounds.Width > 0 ? Bounds.Width : availableSize.Width,
                docH));
        }
        _permissionOverlay?.Measure(availableSize);
        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_isExpanded)
        {
            // Expanded: panel at bottom, hide input row. Stop rides along in the panel's own
            // bottom-right button row, so the loose one is parked off-screen with the rest.
            double epY = finalSize.Height - _expandedHeight;
            _expandedPanel.Arrange(new Rect(0, epY, finalSize.Width, _expandedHeight));
            // Move input row off-screen
            _inputTextBox.Arrange(new Rect(0, finalSize.Height, 0, 0));
            _stopButton.Arrange(new Rect(0, finalSize.Height, 0, 0));
            _expandButton.Arrange(new Rect(0, finalSize.Height, 0, 0));
        }
        else
        {
            // Normal: input row at bottom, with Stop between the text box and the expander so
            // the expander keeps the far-right position it holds when nothing is running.
            double tbY = finalSize.Height - InputBoxHeight;
            double stopW = _stopButton.IsVisible ? _stopButton.DesiredSize.Width : 0;
            double tbW = Math.Max(0, finalSize.Width - ExpandButtonWidth - stopW);
            _inputTextBox.Arrange(new Rect(0, tbY, tbW, InputBoxHeight));
            _stopButton.Arrange(new Rect(tbW, tbY, stopW, InputBoxHeight));
            _expandButton.Arrange(new Rect(tbW + stopW, tbY, ExpandButtonWidth, InputBoxHeight));
        }

        // Always the bottom edge of the control, in both layouts. The input row is flush with
        // that edge either way, so the line stays inside it and never shifts when the input is
        // expanded or collapsed. It overlays rather than adds height, so the PTY is not resized.
        _marquee.Arrange(new Rect(
            0,
            Math.Max(0, finalSize.Height - Controls.MarqueeBar.LineHeight),
            finalSize.Width,
            Controls.MarqueeBar.LineHeight));

        // Position document view panel (fills terminal area)
        if (_docViewPanel != null)
        {
            if (_isDocumentView)
            {
                double docH = Math.Max(0, finalSize.Height - InputAreaHeight - ExpandedPanelHeight);
                _docViewPanel.Arrange(new Rect(0, 0, finalSize.Width, docH));
            }
            else
            {
                _docViewPanel.Arrange(new Rect(0, finalSize.Height, 0, 0));
            }
        }

        // Position permission overlay (centered, above input)
        if (_permissionOverlay != null)
        {
            double docH = Math.Max(0, finalSize.Height - InputAreaHeight - ExpandedPanelHeight);
            _permissionOverlay.Arrange(new Rect(0, 0, finalSize.Width, docH));
        }

        // Position search bar at top-right
        if (_searchBar != null && _searchVisible)
        {
            double sbW = Math.Min(_searchBar.DesiredSize.Width, finalSize.Width);
            _searchBar.Arrange(new Rect(finalSize.Width - sbW, 0, sbW, _searchBar.DesiredSize.Height));
        }

        return finalSize;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        RecalcTerminalSize();
        InvalidateVisual();
    }

    private bool IsOnScrollbar(Point pos)
    {
        return _buffer.Scrollback.Count > 0 && pos.X >= Bounds.Width - ScrollbarWidth && pos.Y < TerminalAreaHeight;
    }

    private (double y, double height) GetScrollbarThumb()
    {
        double termH = TerminalAreaHeight;
        double totalLines = _buffer.Scrollback.Count + _buffer.Rows;
        double viewportRatio = (double)_buffer.Rows / totalLines;
        double thumbH = Math.Max(ScrollbarThumbMinHeight, termH * viewportRatio);
        double trackH = termH - thumbH;
        double maxOffset = _buffer.Scrollback.Count;
        double thumbY = maxOffset > 0 ? trackH * (1.0 - (double)_scrollOffset / maxOffset) : trackH;
        return (thumbY, thumbH);
    }

    private (int row, int col) PointToCell(Point pos)
    {
        int row = Math.Clamp((int)(pos.Y / _cellHeight), 0, _buffer.Rows - 1);
        double x = 0;
        int col = 0;
        for (; col < _buffer.Cols; col++)
        {
            var cell = GetCellAt(row, col);
            if (cell.Attributes.HasFlag(CellAttributes.WideCharTrail))
                continue;
            bool isWide = TerminalBuffer.IsWideChar(cell.Character);
            double cellW = isWide ? _cellWidth * 2 : _cellWidth;
            if (x + cellW / 2 > pos.X) break;
            x += cellW;
        }
        return (row, Math.Clamp(col, 0, _buffer.Cols - 1));
    }

    private int ScreenRowToAbsolute(int screenRow)
    {
        return _buffer.Scrollback.Count - _scrollOffset + screenRow;
    }

    private int AbsoluteToScreenRow(int absRow)
    {
        return absRow - (_buffer.Scrollback.Count - _scrollOffset);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        // Right-click on diagram: show context menu
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            var pos = e.GetPosition(this);
            var diagramBlock = HitTestDiagram(pos);
            if (diagramBlock != null)
            {
                ShowDiagramContextMenu(diagramBlock, pos);
                e.Handled = true;
                return;
            }
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            Clicked?.Invoke();
            var pos = e.GetPosition(this);

            // Click in input box area - let TextBox handle it
            if (pos.Y >= TerminalAreaHeight)
            {
                _inputTextBox.Focus();
                return;
            }

            // Focus the input TextBox for keyboard input
            _inputTextBox.Focus();

            // Scrollbar hit test
            if (IsOnScrollbar(pos))
            {
                var (thumbY, thumbH) = GetScrollbarThumb();
                if (pos.Y >= thumbY && pos.Y <= thumbY + thumbH)
                {
                    _isScrollbarDragging = true;
                    _scrollbarDragStartY = pos.Y;
                    _scrollbarDragStartOffset = _scrollOffset;
                }
                else
                {
                    double trackH = TerminalAreaHeight - thumbH;
                    double ratio = Math.Clamp(pos.Y / (trackH > 0 ? trackH : 1), 0, 1);
                    _scrollOffset = (int)((1.0 - ratio) * _buffer.Scrollback.Count);
                    InvalidateVisual();
                }
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }

            // Text selection
            var (row, col) = PointToCell(pos);
            _selStartRow = ScreenRowToAbsolute(row);
            _selStartCol = col;
            _selEndRow = _selStartRow;
            _selEndCol = _selStartCol;
            _isSelecting = true;
            _hasSelection = false;
            e.Pointer.Capture(this);
            InvalidateVisual();
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_isScrollbarDragging)
        {
            var pos = e.GetPosition(this);
            var (_, thumbH) = GetScrollbarThumb();
            double trackH = TerminalAreaHeight - thumbH;
            if (trackH > 0)
            {
                double deltaY = pos.Y - _scrollbarDragStartY;
                double deltaRatio = deltaY / trackH;
                int newOffset = _scrollbarDragStartOffset - (int)(deltaRatio * _buffer.Scrollback.Count);
                _scrollOffset = Math.Clamp(newOffset, 0, _buffer.Scrollback.Count);
                InvalidateVisual();
            }
            e.Handled = true;
            return;
        }

        if (_isSelecting)
        {
            var (row, col) = PointToCell(e.GetPosition(this));
            _selEndRow = ScreenRowToAbsolute(row);
            _selEndCol = col;
            _hasSelection = (_selStartRow != _selEndRow || _selStartCol != _selEndCol);
            InvalidateVisual();
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_isScrollbarDragging)
        {
            _isScrollbarDragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }
        if (_isSelecting)
        {
            _isSelecting = false;
            e.Pointer.Capture(null);
            e.Handled = true;

            // A press and release on the same cell is a click, not a drag, so it
            // asks for the caret rather than for a selection.
            if (!_hasSelection)
                TryMoveCaretToClick(AbsoluteToScreenRow(_selEndRow), _selEndCol);
        }
    }

    private void GetOrderedSelection(out int startRow, out int startCol, out int endRow, out int endCol)
    {
        if (_selStartRow < _selEndRow || (_selStartRow == _selEndRow && _selStartCol <= _selEndCol))
        {
            startRow = _selStartRow; startCol = _selStartCol;
            endRow = _selEndRow; endCol = _selEndCol;
        }
        else
        {
            startRow = _selEndRow; startCol = _selEndCol;
            endRow = _selStartRow; endCol = _selStartCol;
        }
    }

    private bool IsCellSelected(int screenRow, int col)
    {
        if (!_hasSelection) return false;
        int absRow = ScreenRowToAbsolute(screenRow);
        GetOrderedSelection(out int sr, out int sc, out int er, out int ec);
        if (absRow < sr || absRow > er) return false;
        if (absRow == sr && absRow == er) return col >= sc && col <= ec;
        if (absRow == sr) return col >= sc;
        if (absRow == er) return col <= ec;
        return true;
    }

    private string GetSelectedText()
    {
        if (!_hasSelection) return "";
        GetOrderedSelection(out int sr, out int sc, out int er, out int ec);
        var sb = new System.Text.StringBuilder();
        for (int absRow = sr; absRow <= er; absRow++)
        {
            int colStart = (absRow == sr) ? sc : 0;
            int colEnd = (absRow == er) ? ec : _buffer.Cols - 1;
            for (int col = colStart; col <= colEnd && col < _buffer.Cols; col++)
            {
                var cell = GetCellAtAbs(absRow, col);
                // Skip wide-char trail cells (their content is '\0')
                if (cell.Attributes.HasFlag(CellAttributes.WideCharTrail))
                    continue;
                sb.Append(cell.Character == '\0' ? ' ' : cell.Character);
            }
            if (absRow < er)
            {
                // Use buffer's line-wrap tracking for accurate detection
                int sbCount = _buffer.Scrollback.Count;
                bool isWrapped;
                if (absRow < sbCount)
                    isWrapped = _buffer.IsScrollbackLineWrapped(absRow);
                else
                    isWrapped = _buffer.IsLineWrapped(absRow - sbCount);

                if (!isWrapped)
                {
                    // Real line break: trim trailing spaces and add newline
                    int len = sb.Length;
                    while (len > 0 && sb[len - 1] == ' ') len--;
                    sb.Length = len;
                    sb.AppendLine();
                }
                // Wrapped: text continues directly on next row (no trim, no newline)
            }
        }
        return sb.ToString().TrimEnd();
    }

    private void ClearSelection()
    {
        _hasSelection = false;
        _isSelecting = false;
        InvalidateVisual();
    }

    private TerminalCell GetCellAtAbs(int absRow, int col)
    {
        int scrollbackCount = _buffer.Scrollback.Count;
        if (absRow < scrollbackCount)
        {
            var line = _buffer.GetScrollbackLine(absRow);
            return (line != null && col < line.Length) ? line[col] : TerminalCell.Empty;
        }
        int bufRow = absRow - scrollbackCount;
        return (bufRow >= 0 && bufRow < _buffer.Rows && col >= 0 && col < _buffer.Cols)
            ? _buffer.GetCell(bufRow, col) : TerminalCell.Empty;
    }

    private int CountCharsInRange(int fromRow, int fromCol, int toRow, int toCol)
    {
        int count = 0;
        for (int row = fromRow; row <= toRow; row++)
        {
            int colStart = (row == fromRow) ? fromCol : 0;
            int colEnd = (row == toRow) ? toCol : _buffer.Cols - 1;
            for (int col = colStart; col <= colEnd; col++)
            {
                var cell = GetCellAtAbs(row, col);
                if (cell.Character != '\0' && !cell.Attributes.HasFlag(CellAttributes.WideCharTrail))
                    count++;
            }
        }
        return count;
    }


    private void DeleteSelectedChars()
    {
        GetOrderedSelection(out int sr, out int sc, out int er, out int ec);
        int scrollbackCount = _buffer.Scrollback.Count;
        int cursorAbsRow = ScreenRowToAbsolute(_buffer.CursorRow);
        int cursorCol = _buffer.CursorCol;

        System.Diagnostics.Debug.WriteLine($"[DeleteSelectedChars] sel=({sr},{sc})-({er},{ec}) cursorAbsRow={cursorAbsRow} cursorCol={cursorCol}");

        // Multi-row or off-cursor-row: send charCount backspaces from cursor position
        // (can't reliably move cursor to selection, but delete matching number of chars)
        if (sr != er || sr != cursorAbsRow)
        {
            int multiCharCount = CountCharsInRange(sr, sc, er, ec);
            ClearSelection();
            if (multiCharCount <= 0) multiCharCount = 1;
            System.Diagnostics.Debug.WriteLine($"[DeleteSelectedChars] multi-row/off-cursor: sending {multiCharCount} backspaces");
            var bsSeq = new System.Text.StringBuilder();
            for (int i = 0; i < multiCharCount; i++)
                bsSeq.Append('\x7f');
            _pty?.WriteInput(bsSeq.ToString());
            return;
        }

        int bufRow = cursorAbsRow - scrollbackCount;
        if (bufRow < 0 || bufRow >= _buffer.Rows)
        {
            ClearSelection();
            _pty?.WriteInput("\x7f");
            return;
        }

        // Find last non-empty cell in selection to avoid counting trailing empty cells
        int lastContent = sc - 1;
        for (int col = ec; col >= sc; col--)
        {
            if (_buffer.GetCell(bufRow, col).Character != '\0')
            {
                lastContent = col;
                break;
            }
        }

        ClearSelection();

        // If selection has no content, fall back to single backspace
        if (lastContent < sc)
        {
            _pty?.WriteInput("\x7f");
            return;
        }

        int effectiveEnd = Math.Min(ec, lastContent);
        int charCount = 0;
        for (int col = sc; col <= effectiveEnd; col++)
        {
            var cell = _buffer.GetCell(bufRow, col);
            if (!cell.Attributes.HasFlag(CellAttributes.WideCharTrail))
                charCount++;
        }

        if (charCount <= 0)
        {
            _pty?.WriteInput("\x7f");
            return;
        }

        int targetCol = effectiveEnd + 1;

        System.Diagnostics.Debug.WriteLine($"[DeleteSelectedChars] charCount={charCount} cursorCol={cursorCol} targetCol={targetCol}");

        // Move cursor to end of selection, then send backspaces
        var sb = new System.Text.StringBuilder();
        if (cursorCol != targetCol)
        {
            int moveCount = CountCharsBetweenCols(bufRow, cursorCol, targetCol);
            if (moveCount > 0)
                for (int i = 0; i < moveCount; i++) sb.Append("\x1b[C");
            else if (moveCount < 0)
                for (int i = 0; i < -moveCount; i++) sb.Append("\x1b[D");
        }
        for (int i = 0; i < charCount; i++)
            sb.Append('\x7f');

        _pty?.WriteInput(sb.ToString());
    }

    private int CountCharsBetweenCols(int row, int fromCol, int toCol)
    {
        if (fromCol == toCol) return 0;
        int startCol = Math.Min(fromCol, toCol);
        int endCol = Math.Max(fromCol, toCol);
        int count = 0;
        for (int col = startCol; col < endCol && col < _buffer.Cols; col++)
        {
            if (!_buffer.GetCell(row, col).Attributes.HasFlag(CellAttributes.WideCharTrail))
                count++;
        }
        return toCol > fromCol ? count : -count;
    }

    // ── Click-to-move caret ──

    // The CLI owns the cursor, so it can only be moved by sending it arrow keys.
    // Counting how many to send means reading the CLI's own input layout off the
    // grid, and that reading can be wrong — the wrap point of a soft-wrapped line
    // may or may not hold a character. Rather than trust one estimate, the move
    // runs as a short convergence loop: send the estimate, look at where the
    // cursor actually landed, send the remainder. The correction after the first
    // hop is almost always a same-row count, which is exact.
    private const int MaxCaretMoveChars = 400;
    private const int MaxInputBlockRows = 12;
    private const int CaretMoveAttempts = 4;

    private async void TryMoveCaretToClick(int screenRow, int targetCol)
    {
        if (_caretMoveInProgress || _pty == null) return;
        if (_scrollOffset != 0) return;      // scrolled back: the cursor is not on screen
        if (screenRow < 0 || screenRow >= _buffer.Rows) return;
        // The alternate buffer is no signal either way here: the Claude Code CLI
        // draws its prompt on it. Finding the prompt row is what tells us the
        // click landed on an editable line, and that check happens below.

        int blockTop, blockBottom, textLeft;
        if (!TryGetInputBlock(out blockTop, out blockBottom, out textLeft))
        {
            // No prompt row found. Same-row moves need no layout guesswork, so they
            // still work; anything crossing a row would be guessing and is dropped.
            if (screenRow != _buffer.CursorRow) return;
            blockTop = blockBottom = screenRow;
            textLeft = 0;
        }
        if (screenRow < blockTop || screenRow > blockBottom) return;

        targetCol = ClampToRowContent(screenRow, targetCol, textLeft);

        _caretMoveInProgress = true;
        try
        {
            int lastRow = -1, lastCol = -1;
            for (int attempt = 0; attempt < CaretMoveAttempts; attempt++)
            {
                int fromRow = _buffer.CursorRow;
                int fromCol = _buffer.CursorCol;
                if (fromRow == screenRow && fromCol == targetCol) return;

                // The previous batch moved nothing, so the caret is against an edge
                // the estimate does not know about. Stop rather than thrash.
                if (fromRow == lastRow && fromCol == lastCol) return;
                lastRow = fromRow;
                lastCol = fromCol;

                int delta = EstimateCaretDelta(fromRow, fromCol, screenRow, targetCol,
                                               blockTop, blockBottom, textLeft);
                if (delta == 0 || Math.Abs(delta) > MaxCaretMoveChars) return;

                string key = delta > 0 ? "\x1b[C" : "\x1b[D";
                var sb = new System.Text.StringBuilder(key.Length * Math.Abs(delta));
                for (int i = 0; i < Math.Abs(delta); i++) sb.Append(key);
                _pty.WriteInput(sb.ToString());

                await Task.Delay(70);
                if (_pty == null) return;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MoveCaret] {ex.Message}");
        }
        finally
        {
            _caretMoveInProgress = false;
        }
    }

    // Locates the CLI's input block: the prompt row, which carries the marker in
    // the first column, plus the wrapped rows under it, which the CLI indents to
    // line up after that marker. Returns false when the cursor is not sitting in
    // such a block, which is the signal to fall back to same-row moves only.
    private bool TryGetInputBlock(out int topRow, out int bottomRow, out int textLeft)
    {
        topRow = bottomRow = -1;
        textLeft = 0;

        int cursorRow = _buffer.CursorRow;
        int promptRow = -1;
        for (int r = cursorRow; r >= 0 && cursorRow - r < MaxInputBlockRows; r--)
        {
            int first = FirstNonBlankCol(r);
            if (first < 0) return false;                 // blank row: above the input
            char c = GetCellAt(r, first).Character;
            if (first == 0 && (c == '>' || c == '❯'))
            {
                promptRow = r;
                textLeft = first + 2;                    // past the marker and its space
                break;
            }
            if (first < 2) return false;                 // some other line: not the input
        }
        if (promptRow < 0) return false;

        bottomRow = promptRow;
        for (int r = promptRow + 1; r < _buffer.Rows && r - promptRow < MaxInputBlockRows; r++)
        {
            if (FirstNonBlankCol(r) < textLeft) break;   // dedents: past the input
            bottomRow = r;
        }
        topRow = promptRow;
        return cursorRow >= topRow && cursorRow <= bottomRow;
    }

    private int EstimateCaretDelta(int fromRow, int fromCol, int toRow, int toCol,
                                   int blockTop, int blockBottom, int textLeft)
    {
        if (fromRow == toRow) return CountCharsBetweenCols(fromRow, fromCol, toCol);

        bool forward = toRow > fromRow;
        int lo = forward ? fromRow : toRow;
        int hi = forward ? toRow : fromRow;
        int loCol = forward ? fromCol : toCol;
        int hiCol = forward ? toCol : fromCol;
        if (lo < blockTop || hi > blockBottom) return 0;

        int count = CountCharsBetweenCols(lo, loCol, RowContentEnd(lo, textLeft));
        for (int r = lo + 1; r < hi; r++)
            count += CountCharsBetweenCols(r, textLeft, RowContentEnd(r, textLeft));
        count += CountCharsBetweenCols(hi, textLeft, hiCol);
        return forward ? count : -count;
    }

    // One column past the row's last character — the caret position at end of line.
    private int RowContentEnd(int row, int textLeft)
    {
        return Math.Max(textLeft, LastNonBlankCol(row) + 1);
    }

    private int ClampToRowContent(int row, int col, int textLeft)
    {
        return Math.Clamp(col, textLeft, RowContentEnd(row, textLeft));
    }

    private int FirstNonBlankCol(int row)
    {
        for (int col = 0; col < _buffer.Cols; col++)
            if (GetCellAt(row, col).Character > ' ') return col;
        return -1;
    }

    private int LastNonBlankCol(int row)
    {
        for (int col = _buffer.Cols - 1; col >= 0; col--)
            if (GetCellAt(row, col).Character > ' ') return col;
        return -1;
    }

    // ── Expanded Input Panel ──

    private void BuildExpandedPanel()
    {
        // Drag handle bar at top
        _dragHandle = new Border
        {
            Height = 4,
            Background = new SolidColorBrush(Color.FromRgb(65, 65, 70)),
            Cursor = new Cursor(StandardCursorType.SizeNorthSouth),
        };
        _dragHandle.PointerPressed += OnDragHandlePressed;
        _dragHandle.PointerMoved += OnDragHandleMoved;
        _dragHandle.PointerReleased += OnDragHandleReleased;

        // Multi-line text box
        _expandedTextBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Background = new SolidColorBrush(Color.FromRgb(34, 34, 36)),
            Foreground = new SolidColorBrush(Color.FromRgb(210, 210, 215)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 6),
            FontSize = _fontSize,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New, monospace"),
            PlaceholderText = "Multi-line input (Enter=newline, Ctrl+Enter=send)",
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        _expandedTextBox.AddHandler(KeyDownEvent, OnExpandedKeyDown, RoutingStrategies.Tunnel);
        _expandedTextBox.TextChanged += (_, _) => OnExpandedTextChanged();

        // Collapse button (▼)
        _collapseButton = new Button
        {
            Content = "\u25BC", FontSize = 10,
            Padding = new Thickness(8, 4),
            Background = new SolidColorBrush(Color.FromRgb(50, 50, 52)),
            Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 185)),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Cursor = new Cursor(StandardCursorType.Hand),
            Margin = new Thickness(0, 0, 4, 0),
            Focusable = false,
        };
        ToolTip.SetTip(_collapseButton, "Collapse input (Escape)");
        _collapseButton.Click += (_, _) => CollapseInputPanel();

        // Send button (▶)
        _sendButton = new Button
        {
            Content = "\u25B6", FontSize = 10,
            Padding = new Thickness(8, 4),
            Background = new SolidColorBrush(Color.FromRgb(0, 122, 255)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Cursor = new Cursor(StandardCursorType.Hand),
            Focusable = false,
        };
        ToolTip.SetTip(_sendButton, "Send message (Ctrl+Enter)");
        _sendButton.Click += (_, _) => SendExpandedText();

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 4, 8, 4),
        };
        // Attach (+): the CLI takes files as paths on the prompt, so picking one only has to
        // write its path into the box - the same shape a pasted screenshot arrives in.
        _attachButton = new Button
        {
            Content = "+", FontSize = 13,
            Padding = new Thickness(9, 1),
            Background = new SolidColorBrush(Color.FromRgb(50, 50, 52)),
            Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 185)),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Cursor = new Cursor(StandardCursorType.Hand),
            Margin = new Thickness(0, 0, 4, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Focusable = false,
        };
        ToolTip.SetTip(_attachButton, Services.Loc.Get("AttachFiles", "Attach files"));
        _attachButton.Click += (_, _) => _ = AttachFilesAsync();
        buttonPanel.Children.Add(_attachButton);

        // Stop leads the row so Send keeps the far-right spot it has always had.
        _expandedStopButton = NewStopButton(new Thickness(8, 4));
        _expandedStopButton.Margin = new Thickness(0, 0, 4, 0);
        buttonPanel.Children.Add(_expandedStopButton);
        buttonPanel.Children.Add(_collapseButton);
        buttonPanel.Children.Add(_sendButton);

        var dock = new DockPanel();
        DockPanel.SetDock(_dragHandle, Dock.Top);
        DockPanel.SetDock(buttonPanel, Dock.Bottom);
        dock.Children.Add(_dragHandle);
        dock.Children.Add(buttonPanel);
        dock.Children.Add(_expandedTextBox);

        _expandedPanel = new Border
        {
            Child = dock,
            Background = new SolidColorBrush(Color.FromRgb(34, 34, 36)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(56, 56, 58)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            IsVisible = false,
        };
    }

    private void ToggleExpandedMode()
    {
        if (_isExpanded)
            CollapseInputPanel();
        else
            ExpandInputPanel();
    }

    private void ExpandInputPanel()
    {
        _isExpanded = true;
        _expandedHeight = Math.Max(80, Bounds.Height * 0.3);
        _expandedPanel.IsVisible = true;

        // Transfer text from IME input to expanded input (useful in document view mode)
        if (_isDocumentView && !string.IsNullOrEmpty(_inputTextBox.Text))
        {
            _expandedTextBox.Text = _inputTextBox.Text;
            _expandedTextBox.CaretIndex = _expandedTextBox.Text.Length;
            _inputTextBox.Text = "";
        }

        _inputTextBox.IsVisible = false;
        _expandButton.IsVisible = false;
        _expandedTextBox.Focus();
        RecalcTerminalSize();
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void CollapseInputPanel()
    {
        // Move text to normal input (send to PTY without submitting)
        var text = _expandedTextBox.Text;
        if (!string.IsNullOrEmpty(text))
        {
            // Normalize to single \n, then remove consecutive blank lines
            var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
            while (normalized.Contains("\n\n"))
                normalized = normalized.Replace("\n\n", "\n");
            _pty?.WriteInput(normalized);
            _expandedTextBox.Text = "";
        }

        _isExpanded = false;
        _expandedPanel.IsVisible = false;
        _inputTextBox.IsVisible = true;
        _expandButton.IsVisible = true;
        _inputTextBox.Focus();
        RecalcTerminalSize();
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void SendExpandedText()
    {
        var text = _expandedTextBox.Text;
        if (!string.IsNullOrEmpty(text))
        {
            // Record input position for prompt navigation
            int submitRow = _inputStartAbsRow;
            if (_userInputRows.Count == 0 || Math.Abs(_userInputRows[^1] - submitRow) > 1)
                _userInputRows.Add(submitRow);

            // Capture first input as tab title
            if (!_firstInputCaptured)
            {
                _firstInputCaptured = true;
                FirstUserInput = text.Replace("\r", " ").Replace("\n", " ").Trim();
                var summary = FirstUserInput;
                if (summary.Length > 30) summary = summary[..30] + "...";
                if (!string.IsNullOrWhiteSpace(summary))
                    TitleChanged?.Invoke(summary);
            }
            PromptSubmitted?.Invoke(text);
            _pty?.WriteInput(text + "\r");
            _expandedTextBox.Text = "";
        }
        _expandedTextBox.Focus();
    }

    private void OnExpandedKeyDown(object? sender, KeyEventArgs e)
    {
        // The completion list owns the keys it needs while it is up, so Enter picks a file
        // rather than breaking the line and Escape closes the list rather than the panel.
        if (_completionPopup is { IsOpen: true } && HandleCompletionKey(e))
        {
            e.Handled = true;
            return;
        }

        // Ctrl+Enter: send
        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            SendExpandedText();
            e.Handled = true;
            return;
        }
        // Escape: collapse
        if (e.Key == Key.Escape)
        {
            CollapseInputPanel();
            e.Handled = true;
            return;
        }
        // Enter without modifiers: just newline (AcceptsReturn handles it)
    }

    // ── @ completion ───────────────────────────────────────────────────

    private Popup? _completionPopup;
    private ListBox? _completionList;

    /// <summary>Where the '@' that opened the list sits in the text box.</summary>
    private int _completionAnchor = -1;

    /// <summary>Guards the text box's own change event while the completion rewrites it.</summary>
    private bool _completionInserting;

    /// <summary>
    /// Watches what is being typed and offers the project's files after an '@'. The trigger is
    /// the same one the CLI uses, so a completed reference is exactly what would have been
    /// typed by hand.
    /// </summary>
    private void OnExpandedTextChanged()
    {
        if (_completionInserting) return;

        var text = _expandedTextBox.Text ?? "";
        int caret = Math.Clamp(_expandedTextBox.CaretIndex, 0, text.Length);

        int at = FindCompletionAnchor(text, caret);
        if (at < 0)
        {
            CloseCompletion();
            return;
        }

        _completionAnchor = at;
        _ = ShowCompletionAsync(text.Substring(at + 1, caret - at - 1));
    }

    /// <summary>
    /// The '@' the caret is currently attached to, or -1. It has to start a word and there can
    /// be no whitespace between it and the caret - an '@' in an email address or one the user
    /// has already typed past is not an invitation to complete anything.
    /// </summary>
    private static int FindCompletionAnchor(string text, int caret)
    {
        for (int i = caret - 1; i >= 0; i--)
        {
            char c = text[i];
            if (c == '@')
                return i == 0 || char.IsWhiteSpace(text[i - 1]) ? i : -1;
            if (char.IsWhiteSpace(c)) return -1;
        }
        return -1;
    }

    private async Task ShowCompletionAsync(string query)
    {
        var files = await Services.ProjectFileIndex.ListAsync(_workingDirectory);
        if (files.Count == 0)
        {
            CloseCompletion();
            return;
        }

        // The caret has moved on while the walk was running - whatever it is doing now, this
        // answer is no longer about it.
        if (_completionAnchor < 0) return;

        var matches = Services.ProjectFileIndex.Rank(files, query, 12);
        if (matches.Count == 0)
        {
            CloseCompletion();
            return;
        }

        EnsureCompletionPopup();
        _completionList!.ItemsSource = matches;
        _completionList.SelectedIndex = 0;
        _completionPopup!.IsOpen = true;
    }

    private void EnsureCompletionPopup()
    {
        if (_completionPopup != null) return;

        _completionList = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            MaxHeight = 240,
            Focusable = false,
        };
        _completionList.DoubleTapped += (_, _) => AcceptCompletion();

        _completionPopup = new Popup
        {
            PlacementTarget = _expandedTextBox,
            Placement = PlacementMode.TopEdgeAlignedLeft,
            IsLightDismissEnabled = true,
            Child = new Border
            {
                Background = new SolidColorBrush(_isDark ? Color.FromRgb(40, 40, 42) : Color.FromRgb(250, 250, 252)),
                BorderBrush = new SolidColorBrush(_isDark ? Color.FromRgb(70, 70, 74) : Color.FromRgb(200, 200, 205)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(2),
                MinWidth = 320,
                Child = _completionList,
            },
        };
        _completionPopup.Closed += (_, _) => _completionAnchor = -1;

        VisualChildren.Add(_completionPopup);
        LogicalChildren.Add(_completionPopup);
    }

    /// <summary>Returns true when the key belonged to the completion list.</summary>
    private bool HandleCompletionKey(KeyEventArgs e)
    {
        var list = _completionList;
        if (list == null || list.ItemCount == 0) return false;

        switch (e.Key)
        {
            case Key.Down:
                list.SelectedIndex = (list.SelectedIndex + 1) % list.ItemCount;
                return true;
            case Key.Up:
                list.SelectedIndex = (list.SelectedIndex - 1 + list.ItemCount) % list.ItemCount;
                return true;
            case Key.Enter:
            case Key.Tab:
                AcceptCompletion();
                return true;
            case Key.Escape:
                CloseCompletion();
                return true;
            default:
                return false;
        }
    }

    private void AcceptCompletion()
    {
        var path = _completionList?.SelectedItem as string;
        if (path == null || _completionAnchor < 0)
        {
            CloseCompletion();
            return;
        }

        var text = _expandedTextBox.Text ?? "";
        int caret = Math.Clamp(_expandedTextBox.CaretIndex, 0, text.Length);
        int start = _completionAnchor;
        if (start >= text.Length || caret < start) { CloseCompletion(); return; }

        // A path with a space in it has to survive the CLI's own argument splitting.
        var inserted = "@" + (path.Contains(' ') ? "\"" + path + "\"" : path) + " ";

        _completionInserting = true;
        try
        {
            _expandedTextBox.Text = text[..start] + inserted + text[caret..];
            _expandedTextBox.CaretIndex = start + inserted.Length;
        }
        finally
        {
            _completionInserting = false;
        }

        CloseCompletion();
    }

    private void CloseCompletion()
    {
        _completionAnchor = -1;
        if (_completionPopup != null) _completionPopup.IsOpen = false;
    }

    /// <summary>
    /// Picks files to hand to the AI. The CLI reads them off the prompt as paths, which is the
    /// same thing a pasted screenshot ends up as, so this only has to put the path in the box.
    /// </summary>
    private async Task AttachFilesAsync()
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage == null) return;

        var options = new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = Services.Loc.Get("AttachFiles", "Attach files"),
            AllowMultiple = true,
        };

        if (!string.IsNullOrEmpty(_workingDirectory) && Directory.Exists(_workingDirectory))
        {
            try { options.SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(_workingDirectory); }
            catch { /* an unreachable folder just means the picker opens wherever it likes */ }
        }

        var picked = await storage.OpenFilePickerAsync(options);
        if (picked.Count == 0) return;

        var parts = new List<string>();
        foreach (var file in picked)
        {
            var path = file.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) continue;

            path = RelativeToWorkingDirectory(path);
            parts.Add(path.Contains(' ') ? "\"" + path + "\"" : path);
        }
        if (parts.Count == 0) return;

        InsertIntoExpandedInput(string.Join(" ", parts) + " ");
    }

    /// <summary>
    /// Names a file the way the session would: relative when it is inside the folder the CLI is
    /// running in, absolute when it is not. The session's own files then read the same whether
    /// they were picked or completed with an @.
    /// </summary>
    private string RelativeToWorkingDirectory(string path)
    {
        if (string.IsNullOrEmpty(_workingDirectory)) return path;

        try
        {
            var root = Path.GetFullPath(_workingDirectory);
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return path;

            var relative = Path.GetRelativePath(root, full);
            return relative.StartsWith("..", StringComparison.Ordinal)
                ? path
                : relative.Replace('\\', '/');
        }
        catch
        {
            return path;
        }
    }

    private void InsertIntoExpandedInput(string snippet)
    {
        var text = _expandedTextBox.Text ?? "";
        int caret = Math.Clamp(_expandedTextBox.CaretIndex, 0, text.Length);

        _completionInserting = true;
        try
        {
            _expandedTextBox.Text = text[..caret] + snippet + text[caret..];
            _expandedTextBox.CaretIndex = caret + snippet.Length;
        }
        finally
        {
            _completionInserting = false;
        }

        _expandedTextBox.Focus();
    }

    private void OnDragHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(_dragHandle).Properties.IsLeftButtonPressed)
        {
            _isDragResizing = true;
            _dragResizeStartY = e.GetPosition(this).Y;
            _dragResizeStartHeight = _expandedHeight;
            e.Pointer.Capture(_dragHandle);
            e.Handled = true;
        }
    }

    private void OnDragHandleMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragResizing) return;
        double currentY = e.GetPosition(this).Y;
        double delta = _dragResizeStartY - currentY;
        double newHeight = Math.Clamp(_dragResizeStartHeight + delta, 80, Bounds.Height * 0.7);
        _expandedHeight = newHeight;
        RecalcTerminalSize();
        InvalidateMeasure();
        InvalidateVisual();
        e.Handled = true;
    }

    private void OnDragHandleReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isDragResizing)
        {
            _isDragResizing = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    // ── Search Bar ──

    private void BuildSearchBar()
    {
        _searchTextBox = new TextBox
        {
            PlaceholderText = "Search...",
            FontSize = 12,
            MinWidth = 180,
            Padding = new Thickness(6, 3),
            Background = new SolidColorBrush(Color.FromRgb(50, 50, 52)),
            Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 225)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 85)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
        };
        _searchTextBox.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);
        _searchTextBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) OnSearchTextChanged();
        };

        _searchCountLabel = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 165)),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 50,
        };

        var prevBtn = new Button
        {
            Content = "\u25B2", FontSize = 10,
            Padding = new Thickness(6, 2),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 205)),
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        prevBtn.Click += (_, _) => SearchNavigate(-1);

        var nextBtn = new Button
        {
            Content = "\u25BC", FontSize = 10,
            Padding = new Thickness(6, 2),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 205)),
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        nextBtn.Click += (_, _) => SearchNavigate(1);

        var closeBtn = new Button
        {
            Content = "\u00D7", FontSize = 14,
            Padding = new Thickness(6, 0),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 205)),
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        closeBtn.Click += (_, _) => HideSearchBar();

        _searchRegexToggle = new ToggleButton
        {
            Content = ".*", FontSize = 10,
            Padding = new Thickness(4, 2),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 185)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 85)),
            CornerRadius = new CornerRadius(3),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(_searchRegexToggle, "Regex");
        _searchRegexToggle.IsCheckedChanged += (_, _) => { _searchRegex = _searchRegexToggle.IsChecked == true; UpdateSearchMatches(); };

        _searchCaseToggle = new ToggleButton
        {
            Content = "Aa", FontSize = 10,
            Padding = new Thickness(4, 2),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 185)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 85)),
            CornerRadius = new CornerRadius(3),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(_searchCaseToggle, "Match Case");
        _searchCaseToggle.IsCheckedChanged += (_, _) => { _searchCaseSensitive = _searchCaseToggle.IsChecked == true; UpdateSearchMatches(); };

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
        };
        panel.Children.Add(_searchTextBox);
        panel.Children.Add(_searchRegexToggle);
        panel.Children.Add(_searchCaseToggle);
        panel.Children.Add(_searchCountLabel);
        panel.Children.Add(prevBtn);
        panel.Children.Add(nextBtn);
        panel.Children.Add(closeBtn);

        _searchBar = new Border
        {
            Child = panel,
            Background = new SolidColorBrush(Color.FromRgb(38, 38, 40)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(65, 65, 70)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            IsVisible = false,
        };

        VisualChildren.Add(_searchBar);
        LogicalChildren.Add(_searchBar);
    }

    public void ShowSearchBar()
    {
        if (_searchBar == null) return;
        _searchVisible = true;
        _searchBar.IsVisible = true;
        _searchTextBox?.Focus();
        _searchTextBox?.SelectAll();
        InvalidateMeasure();
        InvalidateVisual();
    }

    public void HideSearchBar()
    {
        if (_searchBar == null) return;
        _searchVisible = false;
        _searchBar.IsVisible = false;
        _searchMatches.Clear();
        _searchCurrentIndex = -1;
        _searchTerm = "";
        _inputTextBox.Focus();
        InvalidateVisual();
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { HideSearchBar(); e.Handled = true; }
        else if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { SearchNavigate(-1); e.Handled = true; }
        else if (e.Key == Key.Enter) { SearchNavigate(1); e.Handled = true; }
    }

    private void OnSearchTextChanged()
    {
        var term = _searchTextBox?.Text ?? "";
        if (term == _searchTerm) return;
        _searchTerm = term;
        UpdateSearchMatches();
    }

    private void UpdateSearchMatches()
    {
        _searchMatches.Clear();
        _searchCurrentIndex = -1;

        if (string.IsNullOrEmpty(_searchTerm))
        {
            _searchCountLabel!.Text = "";
            InvalidateVisual();
            return;
        }

        var comparison = _searchCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        System.Text.RegularExpressions.Regex? regex = null;
        if (_searchRegex)
        {
            try
            {
                var opts = _searchCaseSensitive
                    ? System.Text.RegularExpressions.RegexOptions.None
                    : System.Text.RegularExpressions.RegexOptions.IgnoreCase;
                regex = new System.Text.RegularExpressions.Regex(_searchTerm, opts);
            }
            catch { /* invalid regex — skip */ _searchCountLabel!.Text = "!"; InvalidateVisual(); return; }
        }

        int totalRows = _buffer.Scrollback.Count + _buffer.Rows;
        for (int absRow = 0; absRow < totalRows; absRow++)
        {
            var rowText = GetRowText(absRow);
            if (regex != null)
            {
                foreach (System.Text.RegularExpressions.Match m in regex.Matches(rowText))
                    _searchMatches.Add((absRow, m.Index, m.Length));
            }
            else
            {
                int idx = 0;
                while ((idx = rowText.IndexOf(_searchTerm, idx, comparison)) >= 0)
                {
                    _searchMatches.Add((absRow, idx, _searchTerm.Length));
                    idx += _searchTerm.Length;
                }
            }
        }

        _searchCurrentIndex = _searchMatches.Count > 0 ? 0 : -1;
        UpdateSearchCountLabel();
        ScrollToCurrentMatch();
        InvalidateVisual();
    }

    /// <summary>
    /// Best-effort text of the line the user just submitted. Typing in the plain terminal goes
    /// straight to the PTY, so the only copy of it is what the CLI echoed into the cell grid;
    /// the CLI's own prompt decoration is trimmed off the front.
    /// </summary>
    private string? ReadSubmittedLine()
    {
        try
        {
            var text = GetRowText(_inputStartAbsRow).TrimEnd();
            text = text.TrimStart('│', '╭', '╰', '>', '❯', ' ');
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch { return null; }
    }

    private string GetRowText(int absRow)
    {
        var sb = new System.Text.StringBuilder();
        int scrollbackCount = _buffer.Scrollback.Count;
        for (int col = 0; col < _buffer.Cols; col++)
        {
            TerminalCell cell;
            if (absRow < scrollbackCount)
            {
                var line = _buffer.GetScrollbackLine(absRow);
                cell = (line != null && col < line.Length) ? line[col] : TerminalCell.Empty;
            }
            else
            {
                cell = _buffer.GetCell(absRow - scrollbackCount, col);
            }
            if (cell.Attributes.HasFlag(CellAttributes.WideCharTrail)) continue;
            sb.Append(cell.Character == '\0' ? ' ' : cell.Character);
        }
        return sb.ToString();
    }

    private void SearchNavigate(int direction)
    {
        if (_searchMatches.Count == 0) return;
        _searchCurrentIndex = (_searchCurrentIndex + direction + _searchMatches.Count) % _searchMatches.Count;
        UpdateSearchCountLabel();
        ScrollToCurrentMatch();
        InvalidateVisual();
    }

    private void UpdateSearchCountLabel()
    {
        if (_searchCountLabel == null) return;
        _searchCountLabel.Text = _searchMatches.Count > 0
            ? $"{_searchCurrentIndex + 1}/{_searchMatches.Count}"
            : "0";
    }

    private void ScrollToCurrentMatch()
    {
        if (_searchCurrentIndex < 0 || _searchCurrentIndex >= _searchMatches.Count) return;
        var (absRow, _, _) = _searchMatches[_searchCurrentIndex];
        int scrollbackCount = _buffer.Scrollback.Count;
        int screenRow = absRow - scrollbackCount + _scrollOffset;
        if (screenRow < 0 || screenRow >= _buffer.Rows)
        {
            _scrollOffset = Math.Clamp(scrollbackCount - absRow + _buffer.Rows / 2, 0, scrollbackCount);
        }
    }

    private bool IsCellSearchHighlighted(int absRow, int col, out bool isCurrent)
    {
        isCurrent = false;
        if (_searchMatches.Count == 0) return false;
        for (int i = 0; i < _searchMatches.Count; i++)
        {
            var (mRow, mCol, mLen) = _searchMatches[i];
            if (absRow == mRow && col >= mCol && col < mCol + mLen)
            {
                isCurrent = (i == _searchCurrentIndex);
                return true;
            }
        }
        return false;
    }

    // ── Prompt Navigation ──

    /// <summary>
    /// Scan the buffer for likely user prompt positions.
    /// Detects horizontal rule separators (─, ━, ═, ─── etc.) used by Claude Code CLI
    /// between Q&A turns, then marks the first non-blank line after as a prompt.
    /// Also detects prompt markers (❯, ❱) and Human:/User: labels.
    /// </summary>
    private List<int> ScanForPromptRows()
    {
        var prompts = new List<int>();
        int totalRows = _buffer.Scrollback.Count + _buffer.Rows;
        bool afterSeparator = false;

        for (int absRow = 0; absRow < totalRows; absRow++)
        {
            var text = GetRowText(absRow).TrimEnd();
            var trimmed = text.TrimStart();

            // Detect prompt markers (❯ ❱)
            if (trimmed.Length > 0 && (trimmed[0] == '\u276F' || trimmed[0] == '\u2771'))
            {
                prompts.Add(absRow);
                afterSeparator = false;
                continue;
            }

            // Detect horizontal rule separators:
            // Claude Code uses lines made of box-drawing chars (─ ━ ═ ╌ ╍ ┄ ┅ ┈ ┉)
            if (text.Length >= 4)
            {
                bool isSeparator = true;
                int ruleChars = 0;
                foreach (char c in text)
                {
                    if (c == ' ') continue;
                    if (c == '\u2500' || c == '\u2501' || c == '\u2550' ||  // ─ ━ ═
                        c == '\u254C' || c == '\u254D' || c == '\u2504' ||  // ╌ ╍ ┄
                        c == '\u2505' || c == '\u2508' || c == '\u2509' ||  // ┅ ┈ ┉
                        c == '-' || c == '\u2014' || c == '\u2013')          // - — –
                    {
                        ruleChars++;
                    }
                    else
                    {
                        isSeparator = false;
                        break;
                    }
                }
                if (isSeparator && ruleChars >= 4)
                {
                    afterSeparator = true;
                    continue;
                }
            }

            // Blank lines after separator: keep waiting
            if (afterSeparator && string.IsNullOrWhiteSpace(text))
                continue;

            // First non-blank line after separator = start of user prompt
            if (afterSeparator && !string.IsNullOrWhiteSpace(text))
            {
                prompts.Add(absRow);
                afterSeparator = false;
                continue;
            }

            afterSeparator = false;
        }

        System.Diagnostics.Debug.WriteLine($"[PromptNav] ScanForPromptRows found {prompts.Count} prompts in {totalRows} rows");
        return prompts;
    }

    /// <summary>Navigate to the previous (-1) or next (+1) user prompt.</summary>
    private void NavigatePrompt(int direction)
    {
        // Use tracked input rows if available, otherwise scan buffer
        var prompts = _userInputRows.Count > 0 ? _userInputRows : ScanForPromptRows();
        System.Diagnostics.Debug.WriteLine($"[PromptNav] NavigatePrompt({direction}): found {prompts.Count} prompts, currentIdx={_promptNavCurrentIndex}");
        if (prompts.Count == 0) return;

        int scrollbackCount = _buffer.Scrollback.Count;

        if (_promptNavCurrentIndex < 0 || _promptNavCurrentIndex >= prompts.Count)
        {
            // First navigation: find the prompt nearest to current viewport
            int currentAbsRow = scrollbackCount - _scrollOffset;
            _promptNavCurrentIndex = 0;
            for (int i = prompts.Count - 1; i >= 0; i--)
            {
                if (prompts[i] <= currentAbsRow)
                {
                    _promptNavCurrentIndex = i;
                    break;
                }
            }
        }

        // Move index by direction, clamping to valid range
        int newIndex = _promptNavCurrentIndex + direction;
        newIndex = Math.Clamp(newIndex, 0, prompts.Count - 1);
        _promptNavCurrentIndex = newIndex;

        int targetAbsRow = prompts[newIndex];

        // Scroll so the prompt is near the top of the viewport (2 rows margin)
        _scrollOffset = Math.Clamp(scrollbackCount - targetAbsRow + 2, 0, scrollbackCount);

        UpdatePromptNavLabel(newIndex + 1, prompts.Count);
        ShowPromptNavBar();
        InvalidateVisual();
    }

    private void ShowPromptNavBar()
    {
        if (_promptNavBar == null) CreatePromptNavBar();
        _promptNavBar!.IsVisible = true;
    }

    private void HidePromptNavBar()
    {
        if (_promptNavBar != null)
            _promptNavBar.IsVisible = false;
        _promptNavCurrentIndex = -1;
    }

    private void UpdatePromptNavLabel(int current, int total)
    {
        if (_promptNavLabel != null)
            _promptNavLabel.Text = $"Q {current}/{total}";
    }

    private void CreatePromptNavBar()
    {
        _promptNavLabel = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 165)),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 50,
        };

        var prevBtn = new Button
        {
            Content = "\u25B2", FontSize = 10,
            Padding = new Thickness(6, 2),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 205)),
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(prevBtn, "Previous prompt (Ctrl+\u2191)");
        prevBtn.Click += (_, _) => NavigatePrompt(-1);

        var nextBtn = new Button
        {
            Content = "\u25BC", FontSize = 10,
            Padding = new Thickness(6, 2),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 205)),
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(nextBtn, "Next prompt (Ctrl+\u2193)");
        nextBtn.Click += (_, _) => NavigatePrompt(1);

        var closeBtn = new Button
        {
            Content = "\u00D7", FontSize = 14,
            Padding = new Thickness(6, 0),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 205)),
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        closeBtn.Click += (_, _) => HidePromptNavBar();

        var label = new TextBlock
        {
            Text = "Prompt",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(130, 160, 220)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
        };

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
        };
        panel.Children.Add(label);
        panel.Children.Add(_promptNavLabel);
        panel.Children.Add(prevBtn);
        panel.Children.Add(nextBtn);
        panel.Children.Add(closeBtn);

        _promptNavBar = new Border
        {
            Child = panel,
            Background = new SolidColorBrush(Color.FromRgb(38, 38, 40)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(65, 65, 70)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsVisible = false,
        };

        // Position below search bar if present
        if (_searchBar != null)
            _promptNavBar.Margin = new Thickness(0, _searchBar.IsVisible ? 30 : 0, 0, 0);

        VisualChildren.Add(_promptNavBar);
        LogicalChildren.Add(_promptNavBar);
    }

    // ── Document View Mode ──

    public void SetDocumentViewSession(string? path)
    {
        _docViewSessionPath = path;
        if (_isDocumentView && _docViewPanel != null && path != null)
        {
            _docViewPanel.LoadSession(path);
            _docViewPanel.StartPolling();
        }
    }

    public void ToggleDocumentView()
    {
        _isDocumentView = !_isDocumentView;

        if (_isDocumentView)
        {
            // Create document view panel lazily
            if (_docViewPanel == null)
            {
                _docViewPanel = new Controls.DocumentViewPanel(_isDark, _typeface);
                VisualChildren.Add(_docViewPanel);
                LogicalChildren.Add(_docViewPanel);
            }

            _docViewPanel.IsVisible = true;

            if (_docViewSessionPath != null)
            {
                _docViewPanel.LoadSession(_docViewSessionPath);
                _docViewPanel.StartPolling();
            }

        }
        else
        {
            if (_docViewPanel != null)
            {
                _docViewPanel.IsVisible = false;
                _docViewPanel.StopPolling();
            }
        }

        InvalidateMeasure();
        InvalidateArrange();
        InvalidateVisual();
        DocumentViewChanged?.Invoke(_isDocumentView);
    }

    /// <summary>
    /// Runs for the life of the session, not just while the chat view is up. The overlay is a
    /// card floating above the input row in either view, so the terminal's own prompt stays
    /// readable behind it.
    /// </summary>
    private void StartPermissionWatch()
    {
        if (!EnablePermissionOverlay) return;
        if (_permissionCheckTimer == null)
        {
            _permissionCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _permissionCheckTimer.Tick += OnPermissionCheckTick;
        }
        _permissionCheckTimer.Start();
    }

    private void OnPermissionCheckTick(object? sender, EventArgs e)
    {
        if (!EnablePermissionOverlay) return;

        bool found = IsPermissionPromptOnScreen();

        if (found && _permissionOverlay == null)
        {
            ShowPermissionOverlay(ReadPermissionPromptText());
        }
        else if (!found && _permissionOverlay != null)
        {
            HidePermissionOverlay();
        }
    }

    /// <summary>
    /// True only when the CLI is sitting on a numbered permission prompt.
    ///
    /// The buttons write "1", "2" or "3" straight into the pty, so a wrong match does not just
    /// show a stray card - it answers whatever prompt is really there. Three things have to
    /// agree, and each rules out a prompt this used to be confused with:
    ///
    /// - Every permission prompt opens with "Do you want to " (…proceed? / …allow this
    ///   connection? / …&lt;verb&gt; &lt;file&gt;?). "Esc to cancel" used to count as well, which is
    ///   what put this card over an AskUserQuestion list - the CLI prints that under most of
    ///   its selectors.
    /// - Option 1 of a permission prompt is always Yes. A question's first option is anything.
    /// - "Enter to select" belongs to the multi-question selector, never to a permission
    ///   prompt, so seeing it settles the matter on its own.
    /// </summary>
    private bool IsPermissionPromptOnScreen()
    {
        int totalRows = _buffer.Scrollback.Count + _buffer.Rows;
        bool question = false, yesChoice = false;

        for (int i = Math.Max(0, totalRows - 14); i < totalRows; i++)
        {
            var text = GetRowText(i).TrimEnd();
            if (text.Length == 0) continue;

            if (text.Contains("Enter to select")) return false;
            if (text.Contains("Do you want")) question = true;

            // "❯ 1. Yes", "  1. Yes, and don't ask again", "1. Yes" — the marker is a leading
            // "1." once the box drawing and the selection caret are stripped off.
            var bare = text.TrimStart(' ', '│', '|', '❯', '>', '*');
            if ((bare.StartsWith("1.") || bare.StartsWith("1)")) && bare.Contains("Yes"))
                yesChoice = true;
        }

        return question && yesChoice;
    }

    /// <summary>Grabs the text of the permission prompt so it can be explained in plain words.</summary>
    private string ReadPermissionPromptText()
    {
        var sb = new System.Text.StringBuilder();
        int totalRows = _buffer.Scrollback.Count + _buffer.Rows;
        for (int i = Math.Max(0, totalRows - 30); i < totalRows; i++)
        {
            var text = GetRowText(i).TrimEnd();
            if (text.Length > 0) sb.AppendLine(text);
        }
        return sb.ToString();
    }

    private void ShowPermissionOverlay(string promptText)
    {
        if (_permissionOverlay != null) return;

        var yesBtn = new Button
        {
            Content = Services.Loc.Get("AllowAction", "Yes, allow"),
            Background = new SolidColorBrush(Color.FromRgb(0, 122, 255)),
            Foreground = Brushes.White,
            Padding = new Thickness(16, 6),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Cursor = new Cursor(StandardCursorType.Hand),
            Margin = new Thickness(4, 0),
        };
        yesBtn.Click += (_, _) => { _pty?.WriteInput("1\n"); HidePermissionOverlay(); };

        var alwaysBtn = new Button
        {
            Content = Services.Loc.Get("AlwaysAllow", "Always allow"),
            Background = new SolidColorBrush(Color.FromRgb(48, 209, 88)),
            Foreground = Brushes.White,
            Padding = new Thickness(16, 6),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Cursor = new Cursor(StandardCursorType.Hand),
            Margin = new Thickness(4, 0),
        };
        alwaysBtn.Click += (_, _) => { _pty?.WriteInput("2\n"); HidePermissionOverlay(); };

        var noBtn = new Button
        {
            Content = Services.Loc.Get("DenyAction", "No, deny"),
            Background = new SolidColorBrush(_isDark ? Color.FromRgb(60, 60, 65) : Color.FromRgb(200, 200, 205)),
            Foreground = new SolidColorBrush(_isDark ? Color.FromRgb(210, 210, 215) : Color.FromRgb(40, 40, 45)),
            Padding = new Thickness(16, 6),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Cursor = new Cursor(StandardCursorType.Hand),
            Margin = new Thickness(4, 0),
        };
        noBtn.Click += (_, _) => { _pty?.WriteInput("3\n"); HidePermissionOverlay(); };

        var label = new TextBlock
        {
            Text = Services.Loc.Get("PermissionRequired", "Permission Required"),
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(_isDark ? Color.FromRgb(220, 220, 225) : Color.FromRgb(28, 28, 30)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8),
        };

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        buttonPanel.Children.Add(yesBtn);
        buttonPanel.Children.Add(alwaysBtn);
        buttonPanel.Children.Add(noBtn);

        var content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 560,
        };
        content.Children.Add(label);

        // Say in plain words what the CLI is asking permission for, and how risky it is.
        var explanation = Services.CommandExplainer.Explain(promptText);
        if (explanation != null)
        {
            var riskColor = explanation.Risk switch
            {
                Services.RiskLevel.ReadOnly => Color.FromRgb(48, 209, 88),
                Services.RiskLevel.FileChange => Color.FromRgb(255, 214, 10),
                _ => Color.FromRgb(255, 69, 58),
            };

            var riskBadge = new Border
            {
                Background = new SolidColorBrush(riskColor, 0.18),
                BorderBrush = new SolidColorBrush(riskColor),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8, 2),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 6),
                Child = new TextBlock
                {
                    Text = explanation.RiskLabel,
                    FontSize = 11,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(riskColor),
                },
            };
            content.Children.Add(riskBadge);

            content.Children.Add(new TextBlock
            {
                Text = explanation.Title,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Foreground = new SolidColorBrush(_isDark ? Color.FromRgb(220, 220, 225) : Color.FromRgb(28, 28, 30)),
                Margin = new Thickness(0, 0, 0, 2),
            });

            if (!string.IsNullOrWhiteSpace(explanation.Detail))
            {
                content.Children.Add(new TextBlock
                {
                    Text = explanation.Detail,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    Foreground = new SolidColorBrush(_isDark ? Color.FromRgb(152, 152, 158) : Color.FromRgb(99, 99, 102)),
                    Margin = new Thickness(0, 0, 0, 10),
                });
            }
        }

        content.Children.Add(buttonPanel);

        _permissionOverlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(200, _isDark ? (byte)28 : (byte)240, _isDark ? (byte)28 : (byte)240, _isDark ? (byte)30 : (byte)245)),
            Child = content,
            Padding = new Thickness(20),
            CornerRadius = new CornerRadius(12),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 40),
            BoxShadow = new BoxShadows(new BoxShadow { OffsetY = 4, Blur = 16, Color = Color.FromArgb(80, 0, 0, 0) }),
        };

        VisualChildren.Add(_permissionOverlay);
        LogicalChildren.Add(_permissionOverlay);
        InvalidateMeasure();
        InvalidateArrange();
    }

    private void HidePermissionOverlay()
    {
        if (_permissionOverlay == null) return;
        VisualChildren.Remove(_permissionOverlay);
        LogicalChildren.Remove(_permissionOverlay);
        _permissionOverlay = null;
        InvalidateMeasure();
        InvalidateArrange();
    }

    // ── Diagram Cache ──

    private void AutoCacheNewDiagrams()
    {
        var blocks = _codeBlockDetector.DetectedBlocks;
        if (blocks.Count <= _lastCachedBlockCount) return;

        for (int i = _lastCachedBlockCount; i < blocks.Count; i++)
        {
            var block = blocks[i];
            if (block.Type == CodeBlockType.Excalidraw && block.Content.Length > 50)
            {
                Services.DiagramCache.Save(_workingDirectory ?? "", block);
            }
        }
        _lastCachedBlockCount = blocks.Count;
    }

    /// <summary>
    /// Load cached diagrams from disk for the current project folder.
    /// Called when a session is resumed or when the terminal starts.
    /// </summary>
    public void LoadCachedDiagrams()
    {
        if (string.IsNullOrEmpty(_workingDirectory)) return;
        _cachedDiagrams.Clear();
        _cachedDiagrams.AddRange(Services.DiagramCache.Load(_workingDirectory));
        if (_cachedDiagrams.Count > 0)
            InvalidateVisual();
    }

    /// <summary>
    /// Get all diagrams (detected + cached) for display.
    /// </summary>
    public IReadOnlyList<CodeBlockInfo> GetAllDiagrams()
    {
        var result = new List<CodeBlockInfo>(_cachedDiagrams);
        foreach (var block in _codeBlockDetector.DetectedBlocks)
        {
            if (block.Type == CodeBlockType.Excalidraw && block.Content.Length > 50)
                result.Add(block);
        }
        return result;
    }

    // ── Diagram Export ──

    private void ShowDiagramContextMenu(CodeBlockInfo block, Point pos)
    {
        var menu = new Avalonia.Controls.ContextMenu();

        var openItem = new Avalonia.Controls.MenuItem
        {
            Header = Services.Loc.Get("OpenInWindow"),
        };
        openItem.Click += (_, _) =>
        {
            var win = new DiagramWindow(block, _isDark, _typeface);
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is Window parentWindow)
                win.Show(parentWindow);
            else
                win.Show();
        };

        var artifactItem = new Avalonia.Controls.MenuItem
        {
            Header = Services.Loc.Get("SaveAsArtifact"),
        };
        artifactItem.Click += async (_, _) => await SaveAsArtifact(block);

        var saveItem = new Avalonia.Controls.MenuItem
        {
            Header = Services.Loc.Get("SaveImage"),
        };
        saveItem.Click += async (_, _) => await ExportDiagramAsPng(block);

        var copyItem = new Avalonia.Controls.MenuItem
        {
            Header = Services.Loc.Get("CopyImage"),
        };
        copyItem.Click += async (_, _) => await CopyDiagramToClipboard(block);

        menu.Items.Add(openItem);
        menu.Items.Add(new Avalonia.Controls.Separator());
        menu.Items.Add(artifactItem);
        menu.Items.Add(saveItem);
        menu.Items.Add(copyItem);
        menu.Open(this);
    }

    private async Task SaveAsArtifact(CodeBlockInfo block)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Services.Loc.Get("SaveAsArtifact"),
                DefaultExtension = "excalidraw",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Excalidraw") { Patterns = new[] { "*.excalidraw" } },
                    new FilePickerFileType("PNG Image") { Patterns = new[] { "*.png" } },
                    new FilePickerFileType("SVG Image") { Patterns = new[] { "*.svg" } },
                },
                SuggestedFileName = $"artifact_{DateTime.Now:yyyyMMdd_HHmmss}"
            });

            if (file == null) return;

            var path = file.Path.LocalPath;
            var ext = Path.GetExtension(path).ToLowerInvariant();

            if (ext == ".excalidraw")
            {
                // Save as Excalidraw native format
                var cleanJson = CleanJsonWhitespace(block.Content);
                var excalidrawDoc = $@"{{
  ""type"": ""excalidraw"",
  ""version"": 2,
  ""source"": ""Claucraft"",
  ""elements"": {cleanJson},
  ""appState"": {{
    ""viewBackgroundColor"": ""{(_isDark ? "#1e1e1e" : "#ffffff")}""
  }}
}}";
                await File.WriteAllTextAsync(path, excalidrawDoc);
            }
            else if (ext == ".svg")
            {
                // Save as SVG
                var svg = RenderDiagramToSvg(block);
                if (svg != null)
                    await File.WriteAllTextAsync(path, svg);
            }
            else
            {
                // Save as PNG
                var pngBytes = RenderDiagramToPng(block, 2400, 1200);
                if (pngBytes != null)
                    await File.WriteAllBytesAsync(path, pngBytes);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SaveAsArtifact error: {ex.Message}");
        }
    }

    private string? RenderDiagramToSvg(CodeBlockInfo block)
    {
        try
        {
            var cleanJson = CleanJsonWhitespace(block.Content);
            var elements = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(cleanJson);
            if (elements.ValueKind != System.Text.Json.JsonValueKind.Array) return null;

            var elementMap = new Dictionary<string, System.Text.Json.JsonElement>();
            foreach (var el in elements.EnumerateArray())
            {
                var type = el.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "cameraUpdate" || type == null) continue;
                if (type == "delete")
                {
                    if (el.TryGetProperty("ids", out var ids))
                        foreach (var id in (ids.GetString() ?? "").Split(','))
                            elementMap.Remove(id.Trim());
                    continue;
                }
                if (el.TryGetProperty("id", out var idProp))
                    elementMap[idProp.GetString() ?? ""] = el;
            }
            var drawables = new List<System.Text.Json.JsonElement>(elementMap.Values);

            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            foreach (var el in drawables)
            {
                double ex = el.TryGetProperty("x", out var xp) ? xp.GetDouble() : 0;
                double ey = el.TryGetProperty("y", out var yp) ? yp.GetDouble() : 0;
                double ew = el.TryGetProperty("width", out var wp) ? wp.GetDouble() : 0;
                double eh = el.TryGetProperty("height", out var hp) ? hp.GetDouble() : 0;
                minX = Math.Min(minX, ex); minY = Math.Min(minY, ey);
                maxX = Math.Max(maxX, ex + Math.Max(ew, 10));
                maxY = Math.Max(maxY, ey + Math.Max(eh, 10));
            }
            if (!double.IsFinite(minX)) return null;

            double pad = 20;
            double w = maxX - minX + pad * 2;
            double h = maxY - minY + pad * 2;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($@"<svg xmlns=""http://www.w3.org/2000/svg"" width=""{w:F0}"" height=""{h:F0}"" viewBox=""{minX - pad:F0} {minY - pad:F0} {w:F0} {h:F0}"">");
            sb.AppendLine($@"<rect x=""{minX - pad:F0}"" y=""{minY - pad:F0}"" width=""{w:F0}"" height=""{h:F0}"" fill=""{(_isDark ? "#1e1e1e" : "#ffffff")}""/>");

            foreach (var el in drawables)
            {
                var type = el.TryGetProperty("type", out var tp) ? tp.GetString() : "";
                double ex = el.TryGetProperty("x", out var xp) ? xp.GetDouble() : 0;
                double ey = el.TryGetProperty("y", out var yp) ? yp.GetDouble() : 0;
                double ew = el.TryGetProperty("width", out var wp) ? wp.GetDouble() : 0;
                double eh = el.TryGetProperty("height", out var hp) ? hp.GetDouble() : 0;
                var stroke = el.TryGetProperty("strokeColor", out var sc) ? sc.GetString() ?? "#1e1e1e" : "#1e1e1e";
                var fill = el.TryGetProperty("backgroundColor", out var bc) ? bc.GetString() ?? "none" : "none";
                if (fill == "transparent") fill = "none";
                double sw = el.TryGetProperty("strokeWidth", out var swp) ? swp.GetDouble() : 1;
                double opacity = el.TryGetProperty("opacity", out var op) ? op.GetDouble() / 100.0 : 1.0;
                bool rounded = el.TryGetProperty("roundness", out _);
                string rx = rounded ? @" rx=""6"" ry=""6""" : "";
                string opAttr = opacity < 1 ? $@" opacity=""{opacity:F2}""" : "";

                if (type == "rectangle")
                {
                    sb.AppendLine($@"<rect x=""{ex:F1}"" y=""{ey:F1}"" width=""{ew:F1}"" height=""{eh:F1}"" fill=""{fill}"" stroke=""{stroke}"" stroke-width=""{sw}""{rx}{opAttr}/>");
                    if (el.TryGetProperty("label", out var lbl) && lbl.TryGetProperty("text", out var lt))
                    {
                        double fs = lbl.TryGetProperty("fontSize", out var lf) ? lf.GetDouble() : 16;
                        sb.AppendLine($@"<text x=""{ex + ew / 2:F1}"" y=""{ey + eh / 2:F1}"" text-anchor=""middle"" dominant-baseline=""central"" font-size=""{fs}"" fill=""{stroke}"">{EscapeXml(lt.GetString() ?? "")}</text>");
                    }
                }
                else if (type == "text")
                {
                    var text = el.TryGetProperty("text", out var tt) ? tt.GetString() ?? "" : "";
                    double fs = el.TryGetProperty("fontSize", out var fsp) ? fsp.GetDouble() : 16;
                    double ty = ey + fs;
                    foreach (var line in text.Split('\n'))
                    {
                        sb.AppendLine($@"<text x=""{ex:F1}"" y=""{ty:F1}"" font-size=""{fs}"" fill=""{stroke}""{opAttr}>{EscapeXml(line)}</text>");
                        ty += fs * 1.3;
                    }
                }
                else if (type == "arrow" || type == "line")
                {
                    if (el.TryGetProperty("points", out var pts))
                    {
                        var points = new List<(double x, double y)>();
                        foreach (var pt in pts.EnumerateArray())
                        {
                            int idx = 0; double px = 0, py = 0;
                            foreach (var v in pt.EnumerateArray()) { if (idx == 0) px = v.GetDouble(); else if (idx == 1) py = v.GetDouble(); idx++; }
                            if (idx >= 2) points.Add((ex + px, ey + py));
                        }
                        if (points.Count >= 2)
                        {
                            var d = $"M {points[0].x:F1} {points[0].y:F1}";
                            for (int i = 1; i < points.Count; i++)
                                d += $" L {points[i].x:F1} {points[i].y:F1}";
                            string marker = "";
                            if (type == "arrow" && el.TryGetProperty("endArrowhead", out var ea) && ea.GetString() != null)
                                marker = @" marker-end=""url(#arrowhead)""";
                            sb.AppendLine($@"<path d=""{d}"" fill=""none"" stroke=""{stroke}"" stroke-width=""{sw}""{marker}{opAttr}/>");
                        }
                    }
                }
                else if (type == "ellipse")
                {
                    sb.AppendLine($@"<ellipse cx=""{ex + ew / 2:F1}"" cy=""{ey + eh / 2:F1}"" rx=""{ew / 2:F1}"" ry=""{eh / 2:F1}"" fill=""{fill}"" stroke=""{stroke}"" stroke-width=""{sw}""{opAttr}/>");
                }
            }

            sb.AppendLine(@"<defs><marker id=""arrowhead"" markerWidth=""10"" markerHeight=""7"" refX=""10"" refY=""3.5"" orient=""auto""><polygon points=""0 0, 10 3.5, 0 7"" fill=""#1e1e1e""/></marker></defs>");
            sb.AppendLine("</svg>");
            return sb.ToString();
        }
        catch { return null; }
    }

    private static string EscapeXml(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private CodeBlockInfo? HitTestDiagram(Point pos)
    {
        if (!EnableChartRendering) return null;
        int viewStart = ScreenRowToAbsolute(0);
        int viewEnd = ScreenRowToAbsolute(_buffer.Rows - 1);
        foreach (var block in _codeBlockDetector.GetVisibleBlocks(viewStart, viewEnd))
        {
            if (block.Type != CodeBlockType.Excalidraw || block.Content.Length <= 50) continue;
            int startScreen = AbsoluteToScreenRow(block.StartAbsRow);
            double drawY = Math.Max(0, startScreen * _cellHeight);
            double drawH = 300;
            if (pos.Y >= drawY && pos.Y <= drawY + drawH)
                return block;
        }
        return null;
    }

    private async Task ExportDiagramAsPng(CodeBlockInfo block)
    {
        try
        {
            var pngBytes = RenderDiagramToPng(block, 1200, 600);
            if (pngBytes == null) return;

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Services.Loc.Get("SaveImage"),
                DefaultExtension = "png",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("PNG Image") { Patterns = new[] { "*.png" } }
                },
                SuggestedFileName = $"diagram_{DateTime.Now:yyyyMMdd_HHmmss}.png"
            });

            if (file != null)
            {
                await using var stream = await file.OpenWriteAsync();
                await stream.WriteAsync(pngBytes);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ExportDiagramAsPng error: {ex.Message}");
        }
    }

    private async Task CopyDiagramToClipboard(CodeBlockInfo block)
    {
        try
        {
            var pngBytes = RenderDiagramToPng(block, 1200, 600);
            if (pngBytes == null) return;

            var tempPath = Path.Combine(Path.GetTempPath(), "Claucraft", $"diagram_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
            await File.WriteAllBytesAsync(tempPath, pngBytes);

            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
                await clipboard.SetTextAsync(tempPath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"CopyDiagramToClipboard error: {ex.Message}");
        }
    }

    private byte[]? RenderDiagramToPng(CodeBlockInfo block, int width, int height)
    {
        try
        {
            var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(width, height));
            using (var ctx = bitmap.CreateDrawingContext())
            {
                var bgDefault = _isDark ? Color.FromRgb(28, 28, 30) : Color.FromRgb(255, 255, 255);
                // Create a temporary block with adjusted coordinates for full-size render
                var fakeBlock = block with { StartAbsRow = 0, EndAbsRow = 0 };
                // Draw directly using the same method but with adjusted dimensions
                var bg = _isDark ? Color.FromRgb(30, 30, 34) : Color.FromRgb(252, 252, 255);
                ctx.FillRectangle(new SolidColorBrush(bg), new Rect(0, 0, width, height));

                var cleanJson = CleanJsonWhitespace(block.Content);
                var elements = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(cleanJson);
                if (elements.ValueKind != System.Text.Json.JsonValueKind.Array) return null;

                var elementMap = new Dictionary<string, System.Text.Json.JsonElement>();
                foreach (var el in elements.EnumerateArray())
                {
                    var type = el.TryGetProperty("type", out var t) ? t.GetString() : null;
                    if (type == "cameraUpdate" || type == null) continue;
                    if (type == "delete")
                    {
                        if (el.TryGetProperty("ids", out var ids))
                            foreach (var id in (ids.GetString() ?? "").Split(','))
                                elementMap.Remove(id.Trim());
                        continue;
                    }
                    if (el.TryGetProperty("id", out var idProp))
                        elementMap[idProp.GetString() ?? ""] = el;
                }
                var drawables = new List<System.Text.Json.JsonElement>(elementMap.Values);

                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;
                foreach (var el in drawables)
                {
                    double ex = el.TryGetProperty("x", out var xp) ? xp.GetDouble() : 0;
                    double ey = el.TryGetProperty("y", out var yp) ? yp.GetDouble() : 0;
                    double ew = el.TryGetProperty("width", out var wp) ? wp.GetDouble() : 0;
                    double eh = el.TryGetProperty("height", out var hp) ? hp.GetDouble() : 0;
                    double textW = 0;
                    if ((el.TryGetProperty("type", out var tp2) ? tp2.GetString() : "") == "text" && el.TryGetProperty("text", out var txt))
                        textW = (txt.GetString()?.Length ?? 0) * 8;
                    minX = Math.Min(minX, ex);
                    minY = Math.Min(minY, ey);
                    maxX = Math.Max(maxX, ex + Math.Max(ew, textW));
                    maxY = Math.Max(maxY, ey + Math.Max(eh, 20));
                }
                if (!double.IsFinite(minX)) return null;

                double contentW = maxX - minX + 40;
                double contentH = maxY - minY + 40;
                double scale = Math.Min((width - 40) / contentW, (height - 40) / contentH);
                double offsetX = 20 + ((width - 40) - contentW * scale) / 2 - minX * scale;
                double offsetY = 20 + ((height - 40) - contentH * scale) / 2 - minY * scale;

                foreach (var el in drawables)
                {
                    var type = el.TryGetProperty("type", out var tp) ? tp.GetString() : "";
                    double ex2 = (el.TryGetProperty("x", out var xp2) ? xp2.GetDouble() : 0) * scale + offsetX;
                    double ey2 = (el.TryGetProperty("y", out var yp2) ? yp2.GetDouble() : 0) * scale + offsetY;
                    double ew2 = (el.TryGetProperty("width", out var wp2) ? wp2.GetDouble() : 0) * scale;
                    double eh2 = (el.TryGetProperty("height", out var hp2) ? hp2.GetDouble() : 0) * scale;
                    var strokeStr = el.TryGetProperty("strokeColor", out var sc2) ? sc2.GetString() : "#1e1e1e";
                    var fillStr = el.TryGetProperty("backgroundColor", out var bc2) ? bc2.GetString() : "transparent";
                    double opacity = el.TryGetProperty("opacity", out var op2) ? op2.GetDouble() / 100.0 : 1.0;
                    double sw = (el.TryGetProperty("strokeWidth", out var swp2) ? swp2.GetDouble() : 1) * Math.Min(scale, 1.5);

                    Color strokeColor = ParseColor(strokeStr, Color.FromRgb(30, 30, 30));
                    Color fillColor = ParseColor(fillStr, Colors.Transparent);
                    if (opacity < 1)
                    {
                        strokeColor = Color.FromArgb((byte)(opacity * 255), strokeColor.R, strokeColor.G, strokeColor.B);
                        fillColor = Color.FromArgb((byte)(opacity * 255), fillColor.R, fillColor.G, fillColor.B);
                    }

                    if (type == "rectangle")
                    {
                        var rect = new Rect(ex2, ey2, Math.Max(1, ew2), Math.Max(1, eh2));
                        bool hasRoundness = el.TryGetProperty("roundness", out _);
                        if (fillColor.A > 0 && fillStr != "transparent")
                            ctx.FillRectangle(new SolidColorBrush(fillColor), rect, (float)(hasRoundness ? 6 : 0));
                        if (strokeStr != "transparent" && sw > 0)
                            ctx.DrawRectangle(null, new Pen(new SolidColorBrush(strokeColor), sw), rect, (float)(hasRoundness ? 6 : 0), (float)(hasRoundness ? 6 : 0));
                        if (el.TryGetProperty("label", out var label) && label.TryGetProperty("text", out var lt))
                        {
                            double lfs = (label.TryGetProperty("fontSize", out var lf) ? lf.GetDouble() : 16) * scale;
                            lfs = Math.Max(10, Math.Min(lfs, 36));
                            var ft = new FormattedText(lt.GetString() ?? "", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _typeface, lfs, new SolidColorBrush(strokeColor));
                            ctx.DrawText(ft, new Point(ex2 + (ew2 - ft.Width) / 2, ey2 + (eh2 - ft.Height) / 2));
                        }
                    }
                    else if (type == "text")
                    {
                        var text = el.TryGetProperty("text", out var tt) ? tt.GetString() ?? "" : "";
                        double fs = (el.TryGetProperty("fontSize", out var fsp) ? fsp.GetDouble() : 16) * scale;
                        fs = Math.Max(10, Math.Min(fs, 42));
                        double ty = ey2;
                        foreach (var line in text.Split('\n'))
                        {
                            var ft = new FormattedText(line, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _typeface, fs, new SolidColorBrush(strokeColor));
                            ctx.DrawText(ft, new Point(ex2, ty));
                            ty += fs * 1.3;
                        }
                    }
                    else if (type == "arrow" || type == "line")
                    {
                        var pen = new Pen(new SolidColorBrush(strokeColor), sw);
                        if (el.TryGetProperty("points", out var pts) && pts.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            var pointList = new List<Point>();
                            foreach (var pt in pts.EnumerateArray())
                            {
                                int idx = 0; double px = 0, py = 0;
                                foreach (var v in pt.EnumerateArray()) { if (idx == 0) px = v.GetDouble(); else if (idx == 1) py = v.GetDouble(); idx++; }
                                if (idx >= 2) pointList.Add(new Point(ex2 + px * scale, ey2 + py * scale));
                            }
                            for (int i = 0; i < pointList.Count - 1; i++) ctx.DrawLine(pen, pointList[i], pointList[i + 1]);
                            if (type == "arrow" && pointList.Count >= 2 && el.TryGetProperty("endArrowhead", out var ea2) && ea2.GetString() != null)
                            {
                                var last = pointList[^1]; var prev = pointList[^2];
                                double angle = Math.Atan2(last.Y - prev.Y, last.X - prev.X);
                                double arrLen = 10 * scale;
                                ctx.DrawLine(pen, last, new Point(last.X - arrLen * Math.Cos(angle - 0.4), last.Y - arrLen * Math.Sin(angle - 0.4)));
                                ctx.DrawLine(pen, last, new Point(last.X - arrLen * Math.Cos(angle + 0.4), last.Y - arrLen * Math.Sin(angle + 0.4)));
                            }
                        }
                    }
                    else if (type == "ellipse")
                    {
                        var geo = new EllipseGeometry(new Rect(ex2, ey2, Math.Max(1, ew2), Math.Max(1, eh2)));
                        if (fillColor.A > 0 && fillStr != "transparent") ctx.DrawGeometry(new SolidColorBrush(fillColor), null, geo);
                        if (strokeStr != "transparent" && sw > 0) ctx.DrawGeometry(null, new Pen(new SolidColorBrush(strokeColor), sw), geo);
                    }
                }
            }

            using var ms = new MemoryStream();
            bitmap.Save(ms, PngBitmapEncoderOptions.Default);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"RenderDiagramToPng error: {ex.Message}");
            return null;
        }
    }

    // ── Export ──

    /// <summary>
    /// Raw text of what is on screen right now, plus a little scrollback, so the status
    /// detectors can read the mode line, the spinner and any error the CLI just printed.
    /// </summary>
    public string GetScreenText(int extraScrollbackLines = 40)
    {
        var sb = new System.Text.StringBuilder();
        int scrollbackCount = _buffer.Scrollback.Count;
        int start = Math.Max(0, scrollbackCount - extraScrollbackLines);
        int totalRows = scrollbackCount + _buffer.Rows;
        for (int absRow = start; absRow < totalRows; absRow++)
            sb.AppendLine(GetRowText(absRow).TrimEnd());
        return sb.ToString();
    }

    public string GetPreviewText(int maxLines = 10)
    {
        int scrollbackCount = _buffer.Scrollback.Count;

        // Include scrollback + screen buffer, but exclude bottom rows
        // (status line, prompt, empty lines at bottom)
        // Find last meaningful content row in screen buffer by scanning upward from cursor
        int lastContentRow = _buffer.CursorRow - 1; // exclude cursor/prompt row
        // Skip status-like rows from bottom (typically contain | or are very short prompts)
        for (; lastContentRow >= 0; lastContentRow--)
        {
            var rowText = GetRowText(scrollbackCount + lastContentRow).TrimEnd();
            // Stop skipping if we find a substantial content line (not status/prompt)
            if (!string.IsNullOrWhiteSpace(rowText) && rowText.Length > 2
                && !rowText.StartsWith(">") && !rowText.Contains(" | "))
                break;
        }

        int totalRows = scrollbackCount + lastContentRow + 1;

        var lines = new List<string>();
        var current = new System.Text.StringBuilder();
        for (int absRow = 0; absRow < totalRows; absRow++)
        {
            var rowText = GetRowText(absRow).TrimEnd();
            bool isWrapped = absRow < scrollbackCount
                ? _buffer.IsScrollbackLineWrapped(absRow)
                : _buffer.IsLineWrapped(absRow - scrollbackCount);
            current.Append(rowText);
            if (!isWrapped)
            {
                lines.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0) lines.Add(current.ToString());

        // Take last N non-empty lines, excluding user input lines
        var result = new List<string>();
        for (int i = lines.Count - 1; i >= 0 && result.Count < maxLines; i--)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            // Skip user input prompts (Claude Code uses > or ❯ prefix)
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith(">") || trimmed.StartsWith("❯") || trimmed.StartsWith("$"))
                continue;
            result.Add(line);
        }
        result.Reverse();
        return string.Join("\n", result);
    }

    public string ExportAsText()
    {
        var sb = new System.Text.StringBuilder();
        int totalRows = _buffer.Scrollback.Count + _buffer.Rows;
        for (int absRow = 0; absRow < totalRows; absRow++)
        {
            var rowText = GetRowText(absRow).TrimEnd();
            int scrollbackCount = _buffer.Scrollback.Count;
            bool isWrapped = absRow < scrollbackCount
                ? _buffer.IsScrollbackLineWrapped(absRow)
                : _buffer.IsLineWrapped(absRow - scrollbackCount);
            sb.Append(rowText);
            if (!isWrapped) sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    // ── Scroll & Zoom ──

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        // Ctrl+Scroll: font zoom (works in both modes)
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            double newSize = _fontSize + (e.Delta.Y > 0 ? 1 : -1);
            newSize = Math.Clamp(newSize, 8, 32);
            if (newSize != _fontSize)
            {
                SetFont(_typeface.FontFamily.Name, newSize);
                FontSizeChanged?.Invoke(newSize);
            }
            e.Handled = true;
            return;
        }

        // Document view: let ScrollViewer inside DocumentViewPanel handle scrolling
        if (_isDocumentView)
        {
            // Don't handle - let the event bubble to the ScrollViewer
            return;
        }

        if (_buffer.IsAltBuffer)
        {
            if (e.Delta.Y > 0)
                _pty?.WriteInput("\x1b[5~");
            else
                _pty?.WriteInput("\x1b[6~");
            e.Handled = true;
            return;
        }

        int scrollLines = 3;
        int maxOffset = _buffer.Scrollback.Count;

        if (e.Delta.Y > 0)
            _scrollOffset = Math.Min(_scrollOffset + scrollLines, maxOffset);
        else
            _scrollOffset = Math.Max(_scrollOffset - scrollLines, 0);

        InvalidateVisual();
        e.Handled = true;
    }

    private TerminalCell GetCellAt(int screenRow, int col)
    {
        if (_scrollOffset == 0)
        {
            return _buffer.GetCell(screenRow, col);
        }

        int scrollbackCount = _buffer.Scrollback.Count;
        int historyRow = scrollbackCount - _scrollOffset + screenRow;

        if (historyRow < 0)
            return TerminalCell.Empty;
        if (historyRow < scrollbackCount)
        {
            var line = _buffer.GetScrollbackLine(historyRow);
            if (line != null && col < line.Length)
                return line[col];
            return TerminalCell.Empty;
        }

        int bufferRow = historyRow - scrollbackCount;
        return _buffer.GetCell(bufferRow, col);
    }

    public override void Render(DrawingContext context)
    {
        var bgDefault = _isDark ? Color.FromRgb(28, 28, 30) : Color.FromRgb(255, 255, 255);
        var fgDefault = _isDark ? Color.FromRgb(210, 210, 215) : Color.FromRgb(85, 87, 83);
        double termH = TerminalAreaHeight;

        // Draw entire control background
        var inputBg = _isDark ? Color.FromRgb(44, 44, 46) : Color.FromRgb(242, 242, 242);
        context.FillRectangle(new SolidColorBrush(inputBg), new Rect(0, 0, Bounds.Width, Bounds.Height));

        // Document view mode: skip all terminal cell rendering
        if (_isDocumentView)
        {
            // Draw background for document view area
            var docBg = _isDark ? Color.FromRgb(30, 30, 34) : Color.FromRgb(250, 250, 252);
            context.FillRectangle(new SolidColorBrush(docBg), new Rect(0, 0, Bounds.Width, termH));
            return;
        }

        // Draw terminal background
        context.FillRectangle(new SolidColorBrush(bgDefault), new Rect(0, 0, Bounds.Width, termH));

        // Draw separator line above input box
        var sepPen = new Pen(new SolidColorBrush(_isDark ? Color.FromRgb(56, 56, 58) : Color.FromRgb(198, 198, 200)), 0.5);
        context.DrawLine(sepPen, new Point(0, termH), new Point(Bounds.Width, termH));

        // Draw scrollbar
        if (_buffer.Scrollback.Count > 0)
        {
            var (thumbY, thumbH) = GetScrollbarThumb();
            double barX = Bounds.Width - ScrollbarWidth;

            byte scrollbarBase = _isDark ? (byte)255 : (byte)0;
            context.FillRectangle(new SolidColorBrush(Color.FromArgb(30, scrollbarBase, scrollbarBase, scrollbarBase)),
                new Rect(barX, 0, ScrollbarWidth, termH));

            byte thumbAlpha = _isScrollbarDragging ? (byte)160 : (_scrollOffset > 0 ? (byte)100 : (byte)50);
            context.FillRectangle(new SolidColorBrush(Color.FromArgb(thumbAlpha, scrollbarBase, scrollbarBase, scrollbarBase)),
                new Rect(barX + 2, thumbY, ScrollbarWidth - 4, thumbH));
        }

        bool focused = _inputTextBox.IsFocused;

        // Pre-compute screen rows covered by Excalidraw diagrams (to skip cell drawing there)
        var diagramRowRanges = new List<(int start, int end)>();
        if (EnableChartRendering)
        {
            int vStart = ScreenRowToAbsolute(0);
            int vEnd = ScreenRowToAbsolute(_buffer.Rows - 1);
            foreach (var block in _codeBlockDetector.GetVisibleBlocks(vStart, vEnd))
            {
                if (block.Type == CodeBlockType.Excalidraw && block.Content.Length > 50)
                {
                    int s = Math.Max(0, AbsoluteToScreenRow(block.StartAbsRow));
                    // Cover from block start to block end (includes checkpointId response)
                    int blockEnd = AbsoluteToScreenRow(block.EndAbsRow);
                    int diagRows = (int)(300 / _cellHeight);
                    int e = Math.Min(_buffer.Rows - 1, Math.Max(blockEnd, s + diagRows));
                    diagramRowRanges.Add((s, e));
                }
            }
        }

        // Draw cells
        for (int row = 0; row < _buffer.Rows; row++)
        {
            double y = row * _cellHeight;
            if (y + _cellHeight > termH) break; // Don't render beyond terminal area

            // Skip rows covered by inline diagrams
            bool skipRow = false;
            foreach (var (ds, de) in diagramRowRanges)
            {
                if (row >= ds && row <= de) { skipRow = true; break; }
            }
            if (skipRow) continue;

            double x = 0;
            for (int col = 0; col < _buffer.Cols; col++)
            {
                var cell = GetCellAt(row, col);

                // Skip wide-char trail cells (the lead cell already covers this space)
                if (cell.Attributes.HasFlag(CellAttributes.WideCharTrail))
                {
                    // Orphaned trail (no preceding wide lead) — treat as empty cell
                    if (col == 0 || !TerminalBuffer.IsWideChar(GetCellAt(row, col - 1).Character))
                        x += _cellWidth;
                    continue;
                }

                // Determine cell display width: wide chars use 2 cell widths
                bool isWide = TerminalBuffer.IsWideChar(cell.Character);
                double cellW = isWide ? _cellWidth * 2 : _cellWidth;

                var fg = ResolveColor(cell.Foreground, fgDefault, true);
                var bg = ResolveColor(cell.Background, bgDefault, false);

                if (cell.Attributes.HasFlag(CellAttributes.Bold) && cell.Foreground >= 0 && cell.Foreground < 8)
                    fg = GetAnsiColor(cell.Foreground + 8);

                if (cell.Attributes.HasFlag(CellAttributes.Dim))
                    fg = Color.FromArgb(180, fg.R, fg.G, fg.B);

                if (cell.Attributes.HasFlag(CellAttributes.Inverse))
                    (fg, bg) = (bg, fg);

                if (bg != bgDefault)
                    context.FillRectangle(new SolidColorBrush(bg), new Rect(x, y, cellW, _cellHeight));

                // The caret is an insert-mode bar sitting on the cell's leading edge.
                // It is drawn after the glyph (see below) so the character underneath
                // stays readable rather than being inverted out by a block.
                bool isCaretCell = _scrollOffset == 0 && row == _buffer.CursorRow && col == _buffer.CursorCol
                                   && _buffer.CursorVisible && focused;

                // Draw selection highlight
                if (IsCellSelected(row, col))
                    context.FillRectangle(new SolidColorBrush(Color.FromArgb(90, 50, 120, 220)),
                        new Rect(x, y, cellW, _cellHeight));

                // Draw search match highlight
                if (_searchMatches.Count > 0)
                {
                    int absRowForSearch = ScreenRowToAbsolute(row);
                    if (IsCellSearchHighlighted(absRowForSearch, col, out bool isCurrent))
                    {
                        var hlColor = isCurrent
                            ? Color.FromArgb(180, 230, 160, 0)   // current match: orange
                            : Color.FromArgb(100, 200, 200, 50); // other matches: yellow
                        context.FillRectangle(new SolidColorBrush(hlColor), new Rect(x, y, cellW, _cellHeight));
                    }
                }

                // Draw character
                if (cell.Character > ' ')
                {
                    // Render block element characters (U+2580-U+259F) programmatically
                    // to avoid font-dependent rendering issues in status line graphs
                    if (cell.Character >= '\u2580' && cell.Character <= '\u259F')
                    {
                        var fgBrush = new SolidColorBrush(fg);
                        DrawBlockElement(context, cell.Character, x, y, cellW, _cellHeight, fg, fgBrush);
                    }
                    else
                    {
                        var ft = new FormattedText(cell.Character.ToString(), CultureInfo.CurrentCulture,
                            FlowDirection.LeftToRight, _typeface, _fontSize, new SolidColorBrush(fg));
                        context.DrawText(ft, new Point(x, y));
                    }
                }

                // Draw underline
                if (cell.Attributes.HasFlag(CellAttributes.Underline))
                {
                    var pen = new Pen(new SolidColorBrush(fg), 1);
                    context.DrawLine(pen, new Point(x, y + _cellHeight - 1), new Point(x + cellW, y + _cellHeight - 1));
                }

                // Draw the caret last so it sits on top of the glyph. The default
                // foreground is used rather than the cell's own, which an inverse-video
                // run would have swapped to the background colour and made invisible.
                if (isCaretCell)
                {
                    double caretW = Math.Max(1.5, _cellWidth * 0.18);
                    context.FillRectangle(new SolidColorBrush(Color.FromArgb(230, fgDefault.R, fgDefault.G, fgDefault.B)),
                        new Rect(x, y, caretW, _cellHeight));
                }

                x += cellW;
            }
        }

        // Draw code block cards (overlay on detected renderable blocks)
        DrawCodeBlockCards(context, bgDefault, termH);
    }

    private void DrawCodeBlockCards(DrawingContext context, Color bgDefault, double termH)
    {
        if (!EnableChartRendering) return;

        int viewStart = ScreenRowToAbsolute(0);
        int viewEnd = ScreenRowToAbsolute(_buffer.Rows - 1);
        var visibleBlocks = _codeBlockDetector.GetVisibleBlocks(viewStart, viewEnd);

        foreach (var block in visibleBlocks)
        {
            if (block.Type == CodeBlockType.Excalidraw && block.Content.Length > 50)
            {
                DrawExcalidrawInline(context, block, bgDefault, termH);
            }
        }
    }

    private void DrawExcalidrawInline(DrawingContext context, CodeBlockInfo block, Color bgDefault, double termH)
    {
        // Calculate where to draw: at the start of the code block
        int startScreen = AbsoluteToScreenRow(block.StartAbsRow);
        if (startScreen >= _buffer.Rows) return;

        double drawY = Math.Max(0, startScreen * _cellHeight);
        double drawW = Math.Min(_buffer.Cols * _cellWidth, Bounds.Width - ScrollbarWidth) - 20;
        double drawH = Math.Min(300, termH - drawY);
        if (drawH < 50) return;

        var drawRect = new Rect(10, drawY, drawW, drawH);

        // Background
        var bg = _isDark ? Color.FromRgb(30, 30, 34) : Color.FromRgb(252, 252, 255);
        context.FillRectangle(new SolidColorBrush(bg), drawRect);

        // Border
        var borderPen = new Pen(new SolidColorBrush(_isDark
            ? Color.FromRgb(60, 60, 65) : Color.FromRgb(200, 200, 210)), 1);
        context.DrawRectangle(null, borderPen, drawRect);

        // Parse and render Excalidraw elements (with cache fallback for resize tolerance)
        List<System.Text.Json.JsonElement> drawables;
        double minX, minY, maxX, maxY;
        try
        {
            // Pre-process JSON: collapse whitespace and strip non-ASCII outside strings
            var cleanJson = CleanJsonWhitespace(block.Content);
            var elements = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(cleanJson);
            if (elements.ValueKind != System.Text.Json.JsonValueKind.Array) return;

            // Process delete operations and collect drawable elements
            var elementMap = new Dictionary<string, System.Text.Json.JsonElement>();
            foreach (var el in elements.EnumerateArray())
            {
                var type = el.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "cameraUpdate" || type == null) continue;
                if (type == "delete")
                {
                    if (el.TryGetProperty("ids", out var ids))
                        foreach (var id in (ids.GetString() ?? "").Split(','))
                            elementMap.Remove(id.Trim());
                    continue;
                }
                if (el.TryGetProperty("id", out var idProp))
                    elementMap[idProp.GetString() ?? ""] = el;
            }

            drawables = new List<System.Text.Json.JsonElement>(elementMap.Values);

            // Find bounding box
            minX = double.MaxValue; minY = double.MaxValue;
            maxX = double.MinValue; maxY = double.MinValue;
            foreach (var el in drawables)
            {
                double ex = el.TryGetProperty("x", out var xp) ? xp.GetDouble() : 0;
                double ey = el.TryGetProperty("y", out var yp) ? yp.GetDouble() : 0;
                double ew = el.TryGetProperty("width", out var wp) ? wp.GetDouble() : 0;
                double eh = el.TryGetProperty("height", out var hp) ? hp.GetDouble() : 0;
                var type = el.TryGetProperty("type", out var tp) ? tp.GetString() : "";
                double textW = 0;
                if (type == "text" && el.TryGetProperty("text", out var txt))
                    textW = (txt.GetString()?.Length ?? 0) * 8;
                minX = Math.Min(minX, ex);
                minY = Math.Min(minY, ey);
                maxX = Math.Max(maxX, ex + Math.Max(ew, textW));
                maxY = Math.Max(maxY, ey + Math.Max(eh, 20));
            }

            if (!double.IsFinite(minX) || drawables.Count == 0) return;

            // Cache successful parse for fallback after terminal reflow
            _excalidrawCacheDrawables = drawables;
            _excalidrawCacheMinX = minX; _excalidrawCacheMinY = minY;
            _excalidrawCacheMaxX = maxX; _excalidrawCacheMaxY = maxY;
        }
        catch
        {
            // Parse failed (e.g., after terminal reflow corrupted JSON) — use cached result
            if (_excalidrawCacheDrawables != null)
            {
                drawables = _excalidrawCacheDrawables;
                minX = _excalidrawCacheMinX; minY = _excalidrawCacheMinY;
                maxX = _excalidrawCacheMaxX; maxY = _excalidrawCacheMaxY;
            }
            else
                return; // No cache available, skip rendering
        }
        try
        {

            double contentW = maxX - minX + 40;
            double contentH = maxY - minY + 40;
            double scale = Math.Min((drawW - 20) / contentW, (drawH - 10) / contentH);
            scale = Math.Min(scale, 2);
            double offsetX = drawRect.X + 10 + ((drawW - 20) - contentW * scale) / 2 - minX * scale;
            double offsetY = drawRect.Y + 5 + ((drawH - 10) - contentH * scale) / 2 - minY * scale;

            // Clip to draw area
            using (context.PushClip(drawRect))
            {
                foreach (var el in drawables)
                {
                    var type = el.TryGetProperty("type", out var tp) ? tp.GetString() : "";
                    double ex = (el.TryGetProperty("x", out var xp) ? xp.GetDouble() : 0) * scale + offsetX;
                    double ey = (el.TryGetProperty("y", out var yp) ? yp.GetDouble() : 0) * scale + offsetY;
                    double ew = (el.TryGetProperty("width", out var wp) ? wp.GetDouble() : 0) * scale;
                    double eh = (el.TryGetProperty("height", out var hp) ? hp.GetDouble() : 0) * scale;
                    var strokeStr = el.TryGetProperty("strokeColor", out var sc) ? sc.GetString() : "#1e1e1e";
                    var fillStr = el.TryGetProperty("backgroundColor", out var bc) ? bc.GetString() : "transparent";
                    double opacity = el.TryGetProperty("opacity", out var op) ? op.GetDouble() / 100.0 : 1.0;
                    double sw = (el.TryGetProperty("strokeWidth", out var swp) ? swp.GetDouble() : 1) * Math.Min(scale, 1);

                    Color strokeColor = ParseColor(strokeStr, _isDark ? Color.FromRgb(210, 210, 215) : Color.FromRgb(30, 30, 30));
                    Color fillColor = ParseColor(fillStr, Colors.Transparent);

                    // Adjust text/stroke contrast for readability on diagram background
                    strokeColor = AdjustColorForContrast(strokeColor, _isDark);

                    if (opacity < 1)
                    {
                        strokeColor = Color.FromArgb((byte)(opacity * 255), strokeColor.R, strokeColor.G, strokeColor.B);
                        fillColor = Color.FromArgb((byte)(opacity * 255), fillColor.R, fillColor.G, fillColor.B);
                    }

                    if (type == "rectangle")
                    {
                        var rect = new Rect(ex, ey, Math.Max(1, ew), Math.Max(1, eh));
                        bool hasRoundness = el.TryGetProperty("roundness", out _);
                        if (fillColor.A > 0 && fillStr != "transparent")
                            context.FillRectangle(new SolidColorBrush(fillColor), rect, (float)(hasRoundness ? 6 : 0));
                        if (strokeStr != "transparent" && sw > 0)
                            context.DrawRectangle(null, new Pen(new SolidColorBrush(strokeColor), sw), rect, (float)(hasRoundness ? 6 : 0), (float)(hasRoundness ? 6 : 0));

                        // Label
                        if (el.TryGetProperty("label", out var label) && label.TryGetProperty("text", out var lt))
                        {
                            double lfs = (label.TryGetProperty("fontSize", out var lf) ? lf.GetDouble() : 16) * scale;
                            lfs = Math.Max(8, Math.Min(lfs, 24));
                            var ft = new FormattedText(lt.GetString() ?? "", CultureInfo.CurrentCulture,
                                FlowDirection.LeftToRight, _typeface, lfs, new SolidColorBrush(strokeColor));
                            context.DrawText(ft, new Point(ex + (ew - ft.Width) / 2, ey + (eh - ft.Height) / 2));
                        }
                    }
                    else if (type == "text")
                    {
                        var text = el.TryGetProperty("text", out var tt) ? tt.GetString() ?? "" : "";
                        double fs = (el.TryGetProperty("fontSize", out var fsp) ? fsp.GetDouble() : 16) * scale;
                        fs = Math.Max(8, Math.Min(fs, 28));
                        foreach (var line in text.Split('\n'))
                        {
                            var ft = new FormattedText(line, CultureInfo.CurrentCulture,
                                FlowDirection.LeftToRight, _typeface, fs, new SolidColorBrush(strokeColor));
                            context.DrawText(ft, new Point(ex, ey));
                            ey += fs * 1.3;
                        }
                    }
                    else if (type == "arrow" || type == "line")
                    {
                        var pen = new Pen(new SolidColorBrush(strokeColor), sw);
                        if (el.TryGetProperty("points", out var pts) && pts.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            var pointList = new List<Point>();
                            foreach (var pt in pts.EnumerateArray())
                            {
                                int idx = 0;
                                double px = 0, py = 0;
                                foreach (var v in pt.EnumerateArray())
                                {
                                    if (idx == 0) px = v.GetDouble();
                                    else if (idx == 1) py = v.GetDouble();
                                    idx++;
                                }
                                if (idx >= 2)
                                    pointList.Add(new Point(ex + px * scale, ey + py * scale));
                            }
                            for (int i = 0; i < pointList.Count - 1; i++)
                                context.DrawLine(pen, pointList[i], pointList[i + 1]);

                            // Arrowhead
                            if (type == "arrow" && pointList.Count >= 2 &&
                                el.TryGetProperty("endArrowhead", out var ea) && ea.GetString() != null)
                            {
                                var last = pointList[^1];
                                var prev = pointList[^2];
                                double angle = Math.Atan2(last.Y - prev.Y, last.X - prev.X);
                                double arrLen = 8 * scale;
                                context.DrawLine(pen, last,
                                    new Point(last.X - arrLen * Math.Cos(angle - 0.4), last.Y - arrLen * Math.Sin(angle - 0.4)));
                                context.DrawLine(pen, last,
                                    new Point(last.X - arrLen * Math.Cos(angle + 0.4), last.Y - arrLen * Math.Sin(angle + 0.4)));
                            }

                            // Arrow label
                            if (el.TryGetProperty("label", out var al) && al.TryGetProperty("text", out var alt) && pointList.Count >= 2)
                            {
                                var mid = pointList[pointList.Count / 2];
                                double lfs = (al.TryGetProperty("fontSize", out var alf) ? alf.GetDouble() : 14) * scale;
                                lfs = Math.Max(8, Math.Min(lfs, 20));
                                var ft = new FormattedText(alt.GetString() ?? "", CultureInfo.CurrentCulture,
                                    FlowDirection.LeftToRight, _typeface, lfs, new SolidColorBrush(strokeColor));
                                context.DrawText(ft, new Point(mid.X - ft.Width / 2, mid.Y - ft.Height - 4));
                            }
                        }
                    }
                    else if (type == "ellipse")
                    {
                        var center = new Point(ex + ew / 2, ey + eh / 2);
                        var geo = new EllipseGeometry(new Rect(ex, ey, Math.Max(1, ew), Math.Max(1, eh)));
                        if (fillColor.A > 0 && fillStr != "transparent")
                            context.DrawGeometry(new SolidColorBrush(fillColor), null, geo);
                        if (strokeStr != "transparent" && sw > 0)
                            context.DrawGeometry(null, new Pen(new SolidColorBrush(strokeColor), sw), geo);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DrawExcalidraw] Error: {ex.Message}");
            // Show error visually in the draw area (visible in Release mode too)
            var errText = new FormattedText($"Render Error: {ex.Message}",
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                _typeface, 11, new SolidColorBrush(Color.FromRgb(255, 100, 100)));
            context.DrawText(errText, new Point(drawRect.X + 10, drawRect.Y + 10));
        }
    }

    private static Color ParseColor(string? hex, Color defaultColor)
    {
        if (string.IsNullOrEmpty(hex) || hex == "transparent") return Colors.Transparent;
        try { return Color.Parse(hex); } catch { return defaultColor; }
    }

    /// <summary>
    /// Adjust stroke/text colors for readability on the diagram background.
    /// Dark mode bg ≈ #1e1e22, Light mode bg ≈ #fcfcff.
    /// Colors too close to the background are shifted for contrast.
    /// </summary>
    private static Color AdjustColorForContrast(Color c, bool isDark)
    {
        double brightness = (c.R * 0.299 + c.G * 0.587 + c.B * 0.114) / 255.0;
        if (isDark)
        {
            // Dark background: colors with brightness < 0.3 are too dark to read
            if (brightness < 0.3)
                return Color.FromRgb(
                    (byte)Math.Min(255, 255 - c.R + 40),
                    (byte)Math.Min(255, 255 - c.G + 40),
                    (byte)Math.Min(255, 255 - c.B + 40));
        }
        else
        {
            // Light background: colors with brightness > 0.7 are too light to read
            if (brightness > 0.7)
                return Color.FromRgb(
                    (byte)Math.Max(0, c.R - 180),
                    (byte)Math.Max(0, c.G - 180),
                    (byte)Math.Max(0, c.B - 180));
        }
        return c;
    }

    /// <summary>
    /// Clean JSON that has been extracted from terminal output.
    /// Terminal line wrapping inserts extra whitespace (spaces, newlines)
    /// into the JSON content, potentially breaking parsing.
    /// This method collapses runs of whitespace outside string values.
    /// </summary>
    /// <summary>
    /// Minify JSON by removing ALL whitespace outside string values.
    /// This fixes terminal line-wrapping artifacts where numbers get split
    /// across rows (e.g., "800" becomes "80 0" due to wrapped indentation).
    /// </summary>
    private static string CleanJsonWhitespace(string json)
    {
        var sb = new System.Text.StringBuilder(json.Length);
        bool inString = false;
        bool escape = false;

        for (int i = 0; i < json.Length; i++)
        {
            char c = json[i];

            if (escape) { sb.Append(c); escape = false; continue; }
            if (c == '\\' && inString) { sb.Append(c); escape = true; continue; }
            if (c == '"') { inString = !inString; sb.Append(c); continue; }

            if (inString)
            {
                sb.Append(c);
            }
            else if (c > ' ' && c <= '~')
            {
                // Outside strings: only keep printable ASCII (0x21-0x7E).
                // Strips whitespace AND non-ASCII characters (e.g., ⎿ ● from terminal formatting)
                // that can leak into JSON during terminal reflow after resize.
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static void DrawBlockElement(DrawingContext ctx, char c, double x, double y, double w, double h, Color fg, IBrush brush)
    {
        switch (c)
        {
            case '\u2580': ctx.FillRectangle(brush, new Rect(x, y, w, h / 2)); break;
            case '\u2581': ctx.FillRectangle(brush, new Rect(x, y + h * 7 / 8, w, h / 8)); break;
            case '\u2582': ctx.FillRectangle(brush, new Rect(x, y + h * 3 / 4, w, h / 4)); break;
            case '\u2583': ctx.FillRectangle(brush, new Rect(x, y + h * 5 / 8, w, h * 3 / 8)); break;
            case '\u2584': ctx.FillRectangle(brush, new Rect(x, y + h / 2, w, h / 2)); break;
            case '\u2585': ctx.FillRectangle(brush, new Rect(x, y + h * 3 / 8, w, h * 5 / 8)); break;
            case '\u2586': ctx.FillRectangle(brush, new Rect(x, y + h / 4, w, h * 3 / 4)); break;
            case '\u2587': ctx.FillRectangle(brush, new Rect(x, y + h / 8, w, h * 7 / 8)); break;
            case '\u2588': ctx.FillRectangle(brush, new Rect(x, y, w, h)); break;
            case '\u2589': ctx.FillRectangle(brush, new Rect(x, y, w * 7 / 8, h)); break;
            case '\u258A': ctx.FillRectangle(brush, new Rect(x, y, w * 3 / 4, h)); break;
            case '\u258B': ctx.FillRectangle(brush, new Rect(x, y, w * 5 / 8, h)); break;
            case '\u258C': ctx.FillRectangle(brush, new Rect(x, y, w / 2, h)); break;
            case '\u258D': ctx.FillRectangle(brush, new Rect(x, y, w * 3 / 8, h)); break;
            case '\u258E': ctx.FillRectangle(brush, new Rect(x, y, w / 4, h)); break;
            case '\u258F': ctx.FillRectangle(brush, new Rect(x, y, w / 8, h)); break;
            case '\u2590': ctx.FillRectangle(brush, new Rect(x + w / 2, y, w / 2, h)); break;
            case '\u2591': // ░ Light shade (25%)
                ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(64, fg.R, fg.G, fg.B)), new Rect(x, y, w, h)); break;
            case '\u2592': // ▒ Medium shade (50%)
                ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(128, fg.R, fg.G, fg.B)), new Rect(x, y, w, h)); break;
            case '\u2593': // ▓ Dark shade (75%)
                ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(192, fg.R, fg.G, fg.B)), new Rect(x, y, w, h)); break;
            case '\u2594': ctx.FillRectangle(brush, new Rect(x, y, w, h / 8)); break;
            case '\u2595': ctx.FillRectangle(brush, new Rect(x + w * 7 / 8, y, w / 8, h)); break;
            default: ctx.FillRectangle(brush, new Rect(x, y, w, h)); break;
        }
    }

    private Color ResolveColor(int colorIndex, Color defaultColor, bool isFg)
    {
        if (colorIndex == -1) return defaultColor;
        Color c;
        if ((colorIndex & 0x01000000) != 0)
        {
            c = Color.FromRgb(
                (byte)((colorIndex >> 16) & 0xFF),
                (byte)((colorIndex >> 8) & 0xFF),
                (byte)(colorIndex & 0xFF));
        }
        else if (colorIndex >= 0 && colorIndex < 256)
        {
            c = GetAnsiColor(colorIndex);
        }
        else
        {
            return defaultColor;
        }

        if (!_isDark)
        {
            double brightness = (c.R * 0.299 + c.G * 0.587 + c.B * 0.114) / 255.0;
            if (isFg)
            {
                // Light mode foreground: darken colors that are too bright to read on white
                if (brightness > 0.6)
                {
                    double factor = 0.45; // darken significantly
                    c = Color.FromRgb(
                        (byte)(c.R * factor),
                        (byte)(c.G * factor),
                        (byte)(c.B * factor));
                }
            }
            else
            {
                // Light mode background: lighten dark backgrounds
                if (brightness < 0.4)
                {
                    c = Color.FromRgb(
                        (byte)(c.R + (255 - c.R) * 0.80),
                        (byte)(c.G + (255 - c.G) * 0.80),
                        (byte)(c.B + (255 - c.B) * 0.80));
                }
            }
        }
        return c;
    }

    private static readonly Color[] DarkColors16 =
    {
        Color.FromRgb(0, 0, 0),
        Color.FromRgb(187, 0, 0),
        Color.FromRgb(0, 187, 0),
        Color.FromRgb(187, 187, 0),
        Color.FromRgb(0, 0, 187),
        Color.FromRgb(187, 0, 187),
        Color.FromRgb(0, 187, 187),
        Color.FromRgb(187, 187, 187),
        Color.FromRgb(85, 85, 85),
        Color.FromRgb(255, 85, 85),
        Color.FromRgb(85, 255, 85),
        Color.FromRgb(255, 255, 85),
        Color.FromRgb(85, 85, 255),
        Color.FromRgb(255, 85, 255),
        Color.FromRgb(85, 255, 255),
        Color.FromRgb(255, 255, 255),
    };

    // Tango Light color scheme (matches Windows Terminal)
    private static readonly Color[] LightColors16 =
    {
        Color.FromRgb(0, 0, 0),          // 0 Black
        Color.FromRgb(204, 0, 0),        // 1 Red
        Color.FromRgb(78, 154, 6),       // 2 Green
        Color.FromRgb(196, 160, 0),      // 3 Yellow
        Color.FromRgb(52, 101, 164),     // 4 Blue
        Color.FromRgb(117, 80, 123),     // 5 Magenta
        Color.FromRgb(6, 152, 154),      // 6 Cyan
        Color.FromRgb(211, 215, 207),    // 7 White
        Color.FromRgb(85, 87, 83),       // 8 Bright Black
        Color.FromRgb(239, 41, 41),      // 9 Bright Red
        Color.FromRgb(138, 226, 52),     // 10 Bright Green
        Color.FromRgb(252, 233, 79),     // 11 Bright Yellow
        Color.FromRgb(114, 159, 207),    // 12 Bright Blue
        Color.FromRgb(173, 127, 168),    // 13 Bright Magenta
        Color.FromRgb(52, 226, 226),     // 14 Bright Cyan
        Color.FromRgb(238, 238, 236),    // 15 Bright White
    };

    private Color GetAnsiColor(int index)
    {
        var colors16 = _isDark ? DarkColors16 : LightColors16;

        if (index < 16) return colors16[index];

        if (index < 232)
        {
            int i = index - 16;
            int r = (i / 36) * 51;
            int g = ((i / 6) % 6) * 51;
            int b = (i % 6) * 51;
            return Color.FromRgb((byte)r, (byte)g, (byte)b);
        }

        int gray = (index - 232) * 10 + 8;
        return Color.FromRgb((byte)gray, (byte)gray, (byte)gray);
    }

    public bool IsExpanded => _isExpanded;

    public void AppendToExpandedInput(string text)
    {
        _expandedTextBox.Text = (_expandedTextBox.Text ?? "") + text;
        _expandedTextBox.CaretIndex = _expandedTextBox.Text.Length;
        _expandedTextBox.Focus();
    }

    /// <summary>
    /// Opens the expanded input panel if needed and puts <paramref name="text"/> in it, replacing
    /// anything already there. For long text that the user must be able to read and edit before
    /// it goes anywhere - nothing is written to the PTY until they send it themselves.
    /// </summary>
    public void ShowInExpandedInput(string text)
    {
        if (!_isExpanded) ExpandInputPanel();
        _expandedTextBox.Text = text;
        _expandedTextBox.CaretIndex = _expandedTextBox.Text.Length;
        _expandedTextBox.Focus();
    }

    public void SendText(string text) => _pty?.WriteInput(text);

    private async Task PasteToInputBoxAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;
        var text = await clipboard.TryGetTextAsync();
        if (string.IsNullOrEmpty(text)) return;

        // Insert at caret position
        var current = _inputTextBox.Text ?? "";
        var caretIndex = _inputTextBox.CaretIndex;
        _inputTextBox.Text = current.Insert(caretIndex, text);
        _inputTextBox.CaretIndex = caretIndex + text.Length;
    }

    /// <summary>
    /// Set text in the IME input box (for document view mode where direct PTY send is hidden).
    /// </summary>
    public void SetInputText(string text)
    {
        _inputTextBox.Text = text;
        _inputTextBox.CaretIndex = text.Length;
        _inputTextBox.Focus();
    }

    /// <summary>
    /// Drag payload format used by the in-app Explorer tree: one full path per line.
    /// </summary>
    public static readonly DataFormat<string> ExplorerPathFormat =
        DataFormat.CreateStringApplicationFormat("Claucraft.FilePaths");

    private static bool HasDroppablePaths(IDataTransfer data)
        => data.Contains(DataFormat.File)
        || data.Contains(ExplorerPathFormat)
        || data.Contains(DataFormat.Text);

    private void OnFileDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = HasDroppablePaths(e.DataTransfer) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFileDrop(object? sender, DragEventArgs e)
    {
        var text = BuildDroppedText(e.DataTransfer);
        if (string.IsNullOrEmpty(text)) return;

        // Dropping onto an inactive MDI child should bring it to the front
        Clicked?.Invoke();
        ShowInInputArea(text);
        e.Handled = true;
    }

    private static string BuildDroppedText(IDataTransfer data)
    {
        var paths = new List<string>();

        var files = data.TryGetFiles();
        if (files != null)
        {
            foreach (var file in files)
            {
                var path = file.Path?.LocalPath;
                if (!string.IsNullOrEmpty(path))
                    paths.Add(TrimTrailingSeparator(path));
            }
        }

        // Explorer-tree payload (also the fallback when the storage lookup failed)
        if (paths.Count == 0 && data.TryGetValue(ExplorerPathFormat) is string raw)
        {
            foreach (var line in raw.Split('\n'))
            {
                var path = line.Trim();
                if (path.Length > 0)
                    paths.Add(TrimTrailingSeparator(path));
            }
        }

        if (paths.Count == 0)
            return (data.TryGetText() ?? "").Trim();

        var sb = new System.Text.StringBuilder();
        foreach (var path in paths)
        {
            if (sb.Length > 0)
                sb.Append(' ');

            // Quote paths containing spaces
            if (path.Contains(' '))
                sb.Append('"').Append(path).Append('"');
            else
                sb.Append(path);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Drop sources report folders with a trailing separator; it would escape
    /// the closing quote once the path gets quoted. Roots keep theirs.
    /// </summary>
    private static string TrimTrailingSeparator(string path)
        => path.Length > 3 && (path[^1] == '\\' || path[^1] == '/')
            ? path.TrimEnd('\\', '/')
            : path;

    /// <summary>
    /// Put dropped text into whichever input area is live: the IME box in
    /// document view, the CLI prompt itself otherwise.
    /// </summary>
    private void ShowInInputArea(string text)
    {
        if (_isDocumentView)
        {
            var current = _inputTextBox.Text ?? "";
            var caret = Math.Clamp(_inputTextBox.CaretIndex, 0, current.Length);
            var insert = (caret > 0 && current[caret - 1] != ' ' ? " " + text : text) + " ";
            _inputTextBox.Text = current.Insert(caret, insert);
            _inputTextBox.CaretIndex = caret + insert.Length;
        }
        else
        {
            // Trailing space keeps back-to-back drops from gluing together
            _pty?.WriteInput(text + " ");
        }

        _inputTextBox.Focus();
    }

    public void FocusTerminal()
    {
        _inputTextBox.Focus();
    }

    /// <summary>
    /// Send /exit command and wait for the process to exit gracefully.
    /// Returns true if process exited within timeout.
    /// </summary>
    public async Task<bool> SendExitAndWaitAsync(int timeoutMs = 3000)
    {
        if (_pty == null || !_pty.IsRunning) return true;

        // CLIs without a quit command are torn down by disposing the pseudo console
        if (string.IsNullOrEmpty(ExitCommand)) return false;

        _pty.WriteInput(ExitCommand);
        return await Task.Run(() => _pty.WaitForExitTimeout(timeoutMs));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _marquee.Stop();
        _permissionCheckTimer?.Stop();
        _pty?.Dispose();
    }
}
