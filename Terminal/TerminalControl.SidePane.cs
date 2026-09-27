using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Claucraft.Controls;

namespace Claucraft.Terminal;

// The pane to the right of Chat View. Its Terminal tab is this control's own grid, drawn into
// the pane's content area with the PTY sized to fit it, so the transcript and the live CLI can
// be read side by side. Keys still go to the composer; the grid there is for reading, selecting
// and scrolling.
public partial class TerminalControl
{
    private const double SidePaneSplitterWidth = 5;
    private const double ChatMinWidth = 260;
    private const double SidePaneMinWidth = 220;

    private ChatSidePane? _sidePane;
    private BrowserPreviewPanel? _previewView;
    private string? _lastLocalServerText;
    private string _outputTail = "";
    private Border? _sidePaneSplitter;
    private Button _sidePaneToggle = null!;
    private bool _sidePaneOpen;
    private SidePaneTab _sidePaneTab = SidePaneTab.Terminal;
    private double _sidePaneRatio = 0.45;
    private bool _splitterDragging;
    // The PTY's size before the pane took the terminal over, put back when it lets go
    private (int cols, int rows)? _sizeBeforePane;

    /// <summary>The open tab's name, or "" when the pane is closed. Saved with the workspace.</summary>
    public string SidePaneState
    {
        get => _sidePaneOpen ? _sidePaneTab.ToString() : "";
        set
        {
            bool open = Enum.TryParse<SidePaneTab>(value, out var tab);
            if (open) _sidePaneTab = tab;
            _sidePane?.SelectSilently(_sidePaneTab);
            _sidePaneOpen = open;
            OnSidePaneLayoutChanged();
        }
    }

    /// <summary>The share of the width the pane takes.</summary>
    public double SidePaneRatio
    {
        get => _sidePaneRatio;
        set
        {
            _sidePaneRatio = Math.Clamp(double.IsFinite(value) && value > 0 ? value : 0.45, 0.2, 0.8);
            OnSidePaneLayoutChanged();
        }
    }

    public ChatSidePane? SidePane => _sidePane;

    private bool SidePaneShown => _isDocumentView && _sidePaneOpen && _sidePane != null;

    private bool TerminalInSidePane => SidePaneShown && _sidePaneTab == SidePaneTab.Terminal;

    private double SidePaneWidth(double total)
    {
        double w = total * _sidePaneRatio;
        double max = total - ChatMinWidth - SidePaneSplitterWidth;
        return Math.Max(Math.Min(SidePaneMinWidth, total / 2), Math.Min(w, max));
    }

    /// <summary>The width Chat View's transcript and composer get, beside the pane when it is open.</summary>
    private double ChatWidth(double total) =>
        SidePaneShown ? Math.Max(0, total - SidePaneWidth(total) - SidePaneSplitterWidth) : total;

    /// <summary>Where the grid is drawn while the pane shows the terminal.</summary>
    private Rect SideTerminalRect
    {
        get
        {
            double total = Bounds.Width;
            double w = SidePaneWidth(total);
            // One pixel in from the pane's left border
            return new Rect(total - w + 1, ChatSidePane.StripHeight,
                Math.Max(0, w - 1), Math.Max(0, Bounds.Height - ChatSidePane.StripHeight));
        }
    }

    /// <summary>The width the grid is laid out in: the control, or the pane's content area.</summary>
    private double TermViewWidth => TerminalInSidePane ? SideTerminalRect.Width : Bounds.Width;

    /// <summary>A point on the control, in the grid's own coordinates.</summary>
    private Point TermPoint(Point p)
    {
        if (!TerminalInSidePane) return p;
        var r = SideTerminalRect;
        return new Point(p.X - r.X, p.Y - r.Y);
    }

    private void BuildSidePaneToggle()
    {
        _sidePaneToggle = new Button
        {
            Content = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse("M0.5,0.5 H13.5 V11.5 H0.5 Z M8.5,0.5 V11.5"),
                StrokeThickness = 1.3,
                Width = 14,
                Height = 12,
                Stretch = Stretch.Uniform,
            },
            Width = ChatButtonSize,
            Height = ChatButtonSize,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Focusable = false,
            IsVisible = false,
        };
        ToolTip.SetTip(_sidePaneToggle, Services.Loc.Get("SidePaneToggle"));
        _sidePaneToggle.Click += (_, _) =>
        {
            ToggleSidePane();
            _inputTextBox.Focus();
        };
        VisualChildren.Add(_sidePaneToggle);
        LogicalChildren.Add(_sidePaneToggle);
    }

    public void ToggleSidePane()
    {
        _sidePaneOpen = !_sidePaneOpen;
        OnSidePaneLayoutChanged();
    }

    /// <summary>Created with the chat view's panel, since the pane only ever sits beside it.</summary>
    private void EnsureSidePane()
    {
        if (_sidePane != null || _docViewPanel == null) return;

        _previewView = new BrowserPreviewPanel(_isDark);
        if (_lastLocalServerText != null) _previewView.OfferFromOutput(_lastLocalServerText);

        _sidePane = new ChatSidePane(_isDark);
        _sidePane.SetContent(SidePaneTab.Preview, _previewView);
        _sidePane.SelectSilently(_sidePaneTab);
        _sidePane.TabChanged += tab =>
        {
            _sidePaneTab = tab;
            OnSidePaneLayoutChanged();
        };
        _sidePane.CloseRequested += () =>
        {
            _sidePaneOpen = false;
            OnSidePaneLayoutChanged();
            _inputTextBox.Focus();
        };

        _sidePaneSplitter = new Border
        {
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.SizeWestEast),
        };
        _sidePaneSplitter.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            _splitterDragging = true;
            e.Pointer.Capture(_sidePaneSplitter);
            e.Handled = true;
        };
        _sidePaneSplitter.PointerMoved += (_, e) =>
        {
            if (!_splitterDragging || Bounds.Width <= 0) return;
            double x = e.GetPosition(this).X;
            _sidePaneRatio = Math.Clamp((Bounds.Width - x) / Bounds.Width, 0.2, 0.8);
            OnSidePaneLayoutChanged();
            e.Handled = true;
        };
        _sidePaneSplitter.PointerReleased += (_, e) =>
        {
            if (!_splitterDragging) return;
            _splitterDragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        };

        VisualChildren.Add(_sidePaneSplitter);
        LogicalChildren.Add(_sidePaneSplitter);
        VisualChildren.Add(_sidePane);
        LogicalChildren.Add(_sidePane);
    }

    /// <summary>
    /// Re-lays the view after the pane opened, closed, changed tab or was resized. The PTY
    /// follows the grid into the pane and goes back to its old size when the grid leaves it.
    /// </summary>
    private void OnSidePaneLayoutChanged()
    {
        if (TerminalInSidePane)
        {
            _sizeBeforePane ??= (_buffer.Cols, _buffer.Rows);
            RecalcTerminalSize();
        }
        else if (_sizeBeforePane is { } s)
        {
            _sizeBeforePane = null;
            if (_isDocumentView) ResizeGrid(s.cols, s.rows);
            else RecalcTerminalSize();
        }
        UpdateSidePaneToggleLook();
        InvalidateMeasure();
        InvalidateArrange();
        InvalidateVisual();
    }

    private static readonly System.Text.RegularExpressions.Regex AnsiEscape = new(
        @"\x1b(?:\[[0-9;?]*[ -/]*[@-~]|\][^\x07\x1b]*(?:\x07|\x1b\\)|[@-Z\\-_])",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Watches PTY output for a local dev server address so the Preview tab can offer it. Runs on
    /// the reader thread; a short tail carries an address split across two reads.
    /// </summary>
    private void ScanForLocalServer(byte[] data)
    {
        var text = _outputTail + System.Text.Encoding.UTF8.GetString(data);
        _outputTail = text.Length > 200 ? text[^200..] : text;
        if (text.IndexOf("://", StringComparison.Ordinal) < 0) return;
        var plain = AnsiEscape.Replace(text, "");
        if (plain.IndexOf("localhost", StringComparison.OrdinalIgnoreCase) < 0
            && plain.IndexOf("127.0.0.1", StringComparison.Ordinal) < 0
            && plain.IndexOf("0.0.0.0", StringComparison.Ordinal) < 0
            && plain.IndexOf("[::1]", StringComparison.Ordinal) < 0)
            return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _lastLocalServerText = plain;
            _previewView?.OfferFromOutput(plain);
        });
    }

    private void ResizeGrid(int cols, int rows)
    {
        if (cols == _buffer.Cols && rows == _buffer.Rows) return;
        _buffer.Resize(rows, cols);
        _pty?.Resize(cols, rows);
    }

    private void UpdateSidePaneToggleLook()
    {
        if (_sidePaneToggle == null) return;
        var pal = Services.MarkdownParser.ChatPalette.For(_isDark);
        if (_sidePaneToggle.Content is Avalonia.Controls.Shapes.Path icon)
            icon.Stroke = new SolidColorBrush(_sidePaneOpen ? ChatTheme.Accent : pal.Dim);
    }

    private void ApplySidePaneTheme()
    {
        _sidePane?.ApplyTheme(_isDark);
        _previewView?.ApplyTheme(_isDark);
        UpdateSidePaneToggleLook();
    }

    private void MeasureSidePane(Size available)
    {
        if (_sidePane == null || _sidePaneSplitter == null) return;
        if (!SidePaneShown)
        {
            _sidePane.Measure(default);
            _sidePaneSplitter.Measure(default);
            return;
        }
        double total = double.IsFinite(available.Width) ? available.Width : Bounds.Width;
        double h = double.IsFinite(available.Height) ? available.Height : Bounds.Height;
        _sidePane.Measure(new Size(SidePaneWidth(total), h));
        _sidePaneSplitter.Measure(new Size(SidePaneSplitterWidth, h));
    }

    private void ArrangeSidePane(Size finalSize)
    {
        if (_sidePane == null || _sidePaneSplitter == null) return;
        if (!SidePaneShown)
        {
            var hidden = new Rect(0, finalSize.Height, 0, 0);
            _sidePane.Arrange(hidden);
            _sidePaneSplitter.Arrange(hidden);
            _sidePane.IsVisible = false;
            return;
        }
        _sidePane.IsVisible = true;
        double w = SidePaneWidth(finalSize.Width);
        double chatW = ChatWidth(finalSize.Width);
        _sidePaneSplitter.Arrange(new Rect(chatW, 0, SidePaneSplitterWidth, finalSize.Height));
        _sidePane.Arrange(new Rect(finalSize.Width - w, 0, w, finalSize.Height));
    }
}
