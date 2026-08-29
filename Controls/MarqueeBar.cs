using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Claucraft.Controls;

/// <summary>
/// A thin indeterminate progress line: a band of accent colour that fades out at both ends and
/// sweeps left to right while the CLI is mid-turn. It is its own control rather than something
/// <see cref="Terminal.TerminalControl"/> draws, so a frame costs one repaint of a 3px strip
/// instead of a repaint of the whole terminal grid.
/// </summary>
public class MarqueeBar : Control
{
    /// <summary>Height of the line. The parent arranges the bar to this.</summary>
    public const double LineHeight = 3;

    /// <summary>How long the band takes to travel from off the left edge to off the right edge.</summary>
    private const double SweepSeconds = 1.2;

    /// <summary>Length of the visible band as a fraction of the bar's width.</summary>
    private const double BandFraction = 0.30;

    /// <summary>~30fps. Smooth enough for a 3px shimmer, and half the ticks of a 60fps timer.</summary>
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(33);

    /// <summary>The same blue as the active window frame (MainWindow's ActiveBorder).</summary>
    private static readonly Color Accent = Color.FromRgb(0, 122, 255);

    /// <summary>Both ends fade to fully transparent. The RGB stays blue so the fade cannot go grey.</summary>
    private static readonly Color AccentClear = Color.FromArgb(0, 0, 122, 255);

    private readonly DispatcherTimer _timer;
    private double _phase;
    private bool _isActive;

    public MarqueeBar()
    {
        IsVisible = false;
        IsHitTestVisible = false; // the input box and expand button sit underneath
        Height = LineHeight;

        _timer = new DispatcherTimer { Interval = FrameInterval };
        _timer.Tick += (_, _) =>
        {
            _phase += FrameInterval.TotalSeconds / SweepSeconds;
            if (_phase >= 1) _phase -= 1;
            InvalidateVisual();
        };
    }

    /// <summary>
    /// Whether the line is running. Setting it false stops the timer as well as hiding the bar,
    /// so an idle window costs nothing. The sweep restarts from the left each time it turns on.
    /// </summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            IsVisible = value;

            if (value)
            {
                _phase = 0;
                _timer.Start();
            }
            else
            {
                _timer.Stop();
            }

            InvalidateVisual();
        }
    }

    /// <summary>Stops the animation for good. Called when the owning terminal is disposed.</summary>
    public void Stop() => IsActive = false;

    public override void Render(DrawingContext context)
    {
        if (!_isActive) return;

        double w = Bounds.Width;
        double h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        double band = Math.Max(1, w * BandFraction);

        // The band starts fully off the left edge and ends fully off the right one, so the line
        // is empty for a moment between sweeps instead of snapping back into view.
        double head = _phase * (w + band) - band;

        // Outside the two stops the brush pads with the end colours, which are transparent, so
        // only the band itself paints - no clipping needed.
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(head, 0, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(head + band, 0, RelativeUnit.Absolute),
            GradientStops =
            {
                new GradientStop(AccentClear, 0),
                new GradientStop(Accent, 0.5),
                new GradientStop(AccentClear, 1),
            }
        };

        context.FillRectangle(brush, new Rect(0, 0, w, h));
    }
}
