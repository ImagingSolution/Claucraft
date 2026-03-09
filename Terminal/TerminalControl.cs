using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace ClaudeCodeMDI.Terminal;

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

    // Scrollbar drag state
    private bool _isScrollbarDragging;
    private double _scrollbarDragStartY;
    private int _scrollbarDragStartOffset;
    private const double ScrollbarWidth = 10;
    private const double ScrollbarThumbMinHeight = 20;

    // Input TextBox at bottom
    private readonly TextBox _inputTextBox;
    private const double InputBoxHeight = 28;
    private const double InputBoxMargin = 2;

    public string TabTitle { get; private set; } = "Console";
    public event Action<string>? TitleChanged;
    public event Action? Exited;
    public event Action? Clicked;

    public bool IsDarkTheme
    {
        get => _isDark;
        set
        {
            _isDark = value;
            _inputTextBox.Foreground = new SolidColorBrush(_isDark ? Color.FromRgb(204, 204, 204) : Color.FromRgb(30, 30, 30));
            _inputTextBox.Background = new SolidColorBrush(_isDark ? Color.FromRgb(40, 40, 50) : Color.FromRgb(240, 240, 245));
            InvalidateVisual();
        }
    }

    // Terminal area height = total height - input box area
    private double TerminalAreaHeight => Math.Max(0, Bounds.Height - InputBoxHeight - InputBoxMargin);

    public void SetFont(string fontFamily, double fontSize)
    {
        _typeface = new Typeface(fontFamily + ", Consolas, Courier New");
        _fontSize = fontSize;
        _inputTextBox.FontFamily = new FontFamily(fontFamily + ", Consolas, Courier New");
        _inputTextBox.FontSize = fontSize;
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

        // Create input TextBox at the bottom
        _inputTextBox = new TextBox
        {
            Background = new SolidColorBrush(Color.FromRgb(40, 40, 50)),
            Foreground = new SolidColorBrush(Color.FromRgb(204, 204, 204)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 80)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 2),
            FontSize = _fontSize,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New, monospace"),
            Watermark = "日本語入力 → Enter で送信 / Half-width keys go directly to console",
            Focusable = true,
            AcceptsReturn = false,
        };

        // Handle Enter key to send text to PTY
        _inputTextBox.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);

        // Intercept TextInput: half-width chars go directly to PTY
        _inputTextBox.AddHandler(TextInputEvent, OnInputTextInput, RoutingStrategies.Tunnel);

        // Forward click to activate MDI window
        _inputTextBox.PointerPressed += (s, e) => Clicked?.Invoke();

        VisualChildren.Add(_inputTextBox);
        LogicalChildren.Add(_inputTextBox);

        MeasureCellSize();

        _buffer.BufferChanged += () =>
        {
            Dispatcher.UIThread.Post(InvalidateVisual);
        };
    }

    private static bool IsHalfWidth(string text)
    {
        foreach (char c in text)
        {
            if (c > '\u007E') return false; // Beyond ASCII printable range
        }
        return true;
    }

    private void OnInputTextInput(object? sender, TextInputEventArgs e)
    {
        if (e.Text == null) return;

        // Half-width characters: send directly to PTY
        if (IsHalfWidth(e.Text))
        {
            if (_hasSelection) ClearSelection();
            _pty?.WriteInput(e.Text);
            e.Handled = true; // Prevent TextBox from receiving it
        }
        // Full-width (Japanese etc.): let TextBox handle it, user sends with Enter
    }

    private async void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
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
                _pty?.WriteInput("\x03");
            }
            e.Handled = true;
            return;
        }

        // Ctrl+V: paste to PTY
        if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                var text = await clipboard.GetTextAsync();
                if (!string.IsNullOrEmpty(text))
                {
                    // Insert pasted text into the TextBox at cursor position
                    // (let default TextBox paste handle this, don't intercept)
                }
            }
            return;
        }

        // Enter: send TextBox content to PTY and clear
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            var inputText = _inputTextBox.Text ?? "";
            _pty?.WriteInput(inputText + "\r");
            _inputTextBox.Text = "";
            e.Handled = true;
            return;
        }

        // Escape: send Escape to PTY
        if (e.Key == Key.Escape)
        {
            _pty?.WriteInput("\x1b");
            e.Handled = true;
            return;
        }

        // When TextBox is empty, forward navigation/editing keys directly to PTY
        bool tbEmpty = string.IsNullOrEmpty(_inputTextBox.Text);

        if (tbEmpty)
        {
            string? seq = e.Key switch
            {
                Key.Back => "\x7f",
                Key.Delete => "\x1b[3~",
                Key.Up => "\x1b[A",
                Key.Down => "\x1b[B",
                Key.Left => "\x1b[C",  // Note: Left/Right might seem swapped but this is correct for PTY
                Key.Right => "\x1b[C",
                Key.Home => "\x1b[H",
                Key.End => "\x1b[F",
                Key.PageUp => "\x1b[5~",
                Key.PageDown => "\x1b[6~",
                Key.Tab => "\t",
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

            // Fix Left/Right (above were both C, correct them)
            if (e.Key == Key.Left) seq = "\x1b[D";

            if (seq != null)
            {
                _pty?.WriteInput(seq);
                e.Handled = true;
                return;
            }
        }

        // Tab with text: send text + tab for completion, then clear
        if (e.Key == Key.Tab && !tbEmpty)
        {
            var inputText = _inputTextBox.Text ?? "";
            _pty?.WriteInput(inputText + "\t");
            _inputTextBox.Text = "";
            e.Handled = true;
            return;
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

    private double MeasureCharWidth(char c)
    {
        if (c <= ' ') return _cellWidth;
        var ft = new FormattedText(c.ToString(), CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, _typeface, _fontSize, Brushes.White);
        return ft.Width;
    }

    private void RecalcTerminalSize()
    {
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
        };

        _pty = new PseudoConsole();
        _pty.OutputReceived += data =>
        {
            _parser.Process(new ReadOnlySpan<byte>(data));
            _scrollOffset = 0;
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
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _inputTextBox.Measure(new Size(availableSize.Width, InputBoxHeight));
        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // Position TextBox at the bottom of the control
        double tbY = finalSize.Height - InputBoxHeight;
        _inputTextBox.Arrange(new Rect(0, tbY, finalSize.Width, InputBoxHeight));
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
            double charW = MeasureCharWidth(cell.Character);
            double cellW = Math.Max(charW, _cellWidth);
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
            int scrollbackCount = _buffer.Scrollback.Count;
            for (int col = colStart; col <= colEnd && col < _buffer.Cols; col++)
            {
                TerminalCell cell;
                if (absRow < scrollbackCount)
                {
                    var line = _buffer.GetScrollbackLine(absRow);
                    cell = (line != null && col < line.Length) ? line[col] : TerminalCell.Empty;
                }
                else
                {
                    int bufRow = absRow - scrollbackCount;
                    cell = (bufRow >= 0 && bufRow < _buffer.Rows)
                        ? _buffer.GetCell(bufRow, col) : TerminalCell.Empty;
                }
                sb.Append(cell.Character == '\0' ? ' ' : cell.Character);
            }
            if (absRow < er)
            {
                int len = sb.Length;
                while (len > 0 && sb[len - 1] == ' ') len--;
                sb.Length = len;
                sb.AppendLine();
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

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

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
        var bgDefault = _isDark ? Color.FromRgb(30, 30, 30) : Color.FromRgb(255, 255, 255);
        var fgDefault = _isDark ? Color.FromRgb(204, 204, 204) : Color.FromRgb(30, 30, 30);
        double termH = TerminalAreaHeight;

        // Draw terminal background
        context.FillRectangle(new SolidColorBrush(bgDefault), new Rect(0, 0, Bounds.Width, termH));

        // Draw separator line above input box
        var sepPen = new Pen(new SolidColorBrush(_isDark ? Color.FromRgb(60, 60, 80) : Color.FromRgb(200, 200, 210)), 1);
        context.DrawLine(sepPen, new Point(0, termH), new Point(Bounds.Width, termH));

        // Draw scrollbar
        if (_buffer.Scrollback.Count > 0)
        {
            var (thumbY, thumbH) = GetScrollbarThumb();
            double barX = Bounds.Width - ScrollbarWidth;

            context.FillRectangle(new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
                new Rect(barX, 0, ScrollbarWidth, termH));

            byte thumbAlpha = _isScrollbarDragging ? (byte)160 : (_scrollOffset > 0 ? (byte)100 : (byte)50);
            context.FillRectangle(new SolidColorBrush(Color.FromArgb(thumbAlpha, 255, 255, 255)),
                new Rect(barX + 2, thumbY, ScrollbarWidth - 4, thumbH));
        }

        bool focused = _inputTextBox.IsFocused;

        // Draw cells
        for (int row = 0; row < _buffer.Rows; row++)
        {
            double y = row * _cellHeight;
            if (y + _cellHeight > termH) break; // Don't render beyond terminal area

            double x = 0;
            for (int col = 0; col < _buffer.Cols; col++)
            {
                var cell = GetCellAt(row, col);
                double charW = MeasureCharWidth(cell.Character);
                double cellW = Math.Max(charW, _cellWidth);

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

                // Draw cursor
                if (_scrollOffset == 0 && row == _buffer.CursorRow && col == _buffer.CursorCol && _buffer.CursorVisible && focused)
                {
                    context.FillRectangle(new SolidColorBrush(Color.FromArgb(180, fg.R, fg.G, fg.B)),
                        new Rect(x, y, cellW, _cellHeight));
                    fg = bgDefault;
                }

                // Draw selection highlight
                if (IsCellSelected(row, col))
                    context.FillRectangle(new SolidColorBrush(Color.FromArgb(90, 50, 120, 220)),
                        new Rect(x, y, cellW, _cellHeight));

                // Draw character
                if (cell.Character > ' ')
                {
                    var ft = new FormattedText(cell.Character.ToString(), CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight, _typeface, _fontSize, new SolidColorBrush(fg));
                    double charOffset = (cellW - ft.Width) / 2;
                    context.DrawText(ft, new Point(x + charOffset, y));
                }

                // Draw underline
                if (cell.Attributes.HasFlag(CellAttributes.Underline))
                {
                    var pen = new Pen(new SolidColorBrush(fg), 1);
                    context.DrawLine(pen, new Point(x, y + _cellHeight - 1), new Point(x + cellW, y + _cellHeight - 1));
                }

                x += cellW;
            }
        }
    }

    private Color ResolveColor(int colorIndex, Color defaultColor, bool isFg)
    {
        if (colorIndex == -1) return defaultColor;
        if ((colorIndex & 0x01000000) != 0)
        {
            return Color.FromRgb(
                (byte)((colorIndex >> 16) & 0xFF),
                (byte)((colorIndex >> 8) & 0xFF),
                (byte)(colorIndex & 0xFF));
        }
        if (colorIndex >= 0 && colorIndex < 256)
            return GetAnsiColor(colorIndex);
        return defaultColor;
    }

    private static Color GetAnsiColor(int index)
    {
        Color[] colors16 =
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

    public void FocusTerminal()
    {
        _inputTextBox.Focus();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pty?.Dispose();
    }
}
