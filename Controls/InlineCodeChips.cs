using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Snipyard.Controls;

/// <summary>
/// Rounded inline-code chips, as the desktop app draws them. A Run's own Background can only
/// paint a hard rectangle, so a chip run instead carries <see cref="MarkerBrush"/> (fully
/// transparent) and its host text block paints the rounded, outlined chip underneath the text.
/// The text stays ordinary runs, so selection and copy are unaffected.
/// </summary>
public static class InlineCodeChips
{
    private static readonly Color Marker = Color.FromArgb(0, 1, 2, 3);

    /// <summary>Background for a Run that should be drawn as a chip.</summary>
    public static readonly IBrush MarkerBrush = new SolidColorBrush(Marker);

    public interface IHost
    {
        IBrush? ChipBackground { get; set; }
        IBrush? ChipBorder { get; set; }
    }

    internal static void Draw(TextBlock tb, IHost host, DrawingContext context, Point origin)
    {
        if (host.ChipBackground == null) return;
        var layout = tb.TextLayout;

        // Contiguous marker runs merge into one range, so a chip that font fallback split into
        // Latin and Japanese pieces still draws as a single shape.
        var ranges = new List<(int Start, int End, double Em)>();
        foreach (var line in layout.TextLines)
        {
            int pos = line.FirstTextSourceIndex;
            foreach (var run in line.TextRuns)
            {
                if (run.Properties?.BackgroundBrush is ISolidColorBrush b && b.Color == Marker)
                {
                    double em = run.Properties.FontRenderingEmSize;
                    if (ranges.Count > 0 && ranges[^1].End == pos)
                        ranges[^1] = (ranges[^1].Start, pos + run.Length, Math.Max(ranges[^1].Em, em));
                    else
                        ranges.Add((pos, pos + run.Length, em));
                }
                pos += run.Length;
            }
        }
        if (ranges.Count == 0) return;

        var pen = host.ChipBorder != null ? new Pen(host.ChipBorder, 1) : null;
        foreach (var (start, end, em) in ranges)
        {
            foreach (var r in layout.HitTestTextRange(start, end - start))
            {
                if (r.Width <= 0) continue;
                // Hug the glyphs rather than the full (LineHeight-stretched) line box.
                double h = Math.Min(r.Height, Math.Round(em * 1.45));
                var chip = new Rect(r.X + origin.X, r.Y + origin.Y + (r.Height - h) / 2, r.Width, h)
                    .Deflate(0.5);
                context.DrawRectangle(host.ChipBackground, pen, new RoundedRect(chip, 4));
            }
        }
    }
}

/// <summary>A SelectableTextBlock that can draw <see cref="InlineCodeChips"/>.</summary>
public sealed class ChipSelectableTextBlock : SelectableTextBlock, InlineCodeChips.IHost
{
    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);
    public IBrush? ChipBackground { get; set; }
    public IBrush? ChipBorder { get; set; }

    protected override void RenderTextLayout(DrawingContext context, Point origin)
    {
        InlineCodeChips.Draw(this, this, context, origin);
        base.RenderTextLayout(context, origin);
    }
}
