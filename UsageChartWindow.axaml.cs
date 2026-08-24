using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Claucraft.Services;

namespace Claucraft;

public partial class UsageChartWindow : Window
{
    private record DailyData(string Date, int Messages, int ToolCalls, int Sessions);

    public UsageChartWindow()
    {
        InitializeComponent();
        Opened += async (_, _) => await DrawChartAsync();
    }

    /// <summary>
    /// Daily counts for the last 14 days, aggregated from the session transcripts.
    /// This used to read ~/.claude/stats-cache.json, which is not written on every install;
    /// when it was missing the chart drew nothing at all.
    /// </summary>
    private static async Task<List<DailyData>> LoadDataAsync()
    {
        var data = new List<DailyData>();
        try
        {
            var report = await CostAnalytics.BuildAsync(days: 14);

            // ByDay is pre-filled with one entry per calendar day, oldest first.
            foreach (var day in report.ByDay)
            {
                report.SessionsByDay.TryGetValue(day.Key, out int sessions);
                data.Add(new DailyData(
                    day.Key,
                    (int)Math.Min(int.MaxValue, day.Totals.Turns),
                    (int)Math.Min(int.MaxValue, day.Totals.ToolCalls),
                    sessions));
            }
        }
        catch { }

        return data;
    }

    private async Task DrawChartAsync()
    {
        ChartCanvas.Children.Clear();
        var data = await LoadDataAsync();
        if (data.Count == 0) return;

        double canvasW = ChartCanvas.Bounds.Width;
        double canvasH = ChartCanvas.Bounds.Height;
        if (canvasW <= 0 || canvasH <= 0)
        {
            canvasW = 550;
            canvasH = 280;
        }

        double marginBottom = 50;
        double marginLeft = 50;
        double marginTop = 10;
        double marginRight = 10;

        double chartW = canvasW - marginLeft - marginRight;
        double chartH = canvasH - marginTop - marginBottom;

        int maxVal = data.Max(d => d.Messages);
        if (maxVal == 0) maxVal = 1;

        // Round up to nice number
        int niceMax = (int)(Math.Ceiling(maxVal / 200.0) * 200);
        if (niceMax == 0) niceMax = 200;

        double barGroupWidth = chartW / data.Count;
        double barWidth = barGroupWidth * 0.3;
        double gap = barGroupWidth * 0.05;

        // Draw Y-axis gridlines
        int gridLines = 5;
        for (int i = 0; i <= gridLines; i++)
        {
            double val = niceMax * i / gridLines;
            double y = marginTop + chartH - (chartH * i / gridLines);

            var line = new Line
            {
                StartPoint = new Point(marginLeft, y),
                EndPoint = new Point(canvasW - marginRight, y),
                Stroke = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                StrokeThickness = 1
            };
            ChartCanvas.Children.Add(line);

            var label = new TextBlock
            {
                Text = $"{val:F0}",
                FontSize = 10,
                Foreground = Brushes.Gray
            };
            Canvas.SetLeft(label, 2);
            Canvas.SetTop(label, y - 7);
            ChartCanvas.Children.Add(label);
        }

        // Draw bars
        for (int i = 0; i < data.Count; i++)
        {
            double x = marginLeft + i * barGroupWidth;

            // Messages bar (green)
            double msgH = (double)data[i].Messages / niceMax * chartH;
            var msgBar = new Rectangle
            {
                Width = barWidth,
                Height = Math.Max(1, msgH),
                Fill = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                RadiusX = 2, RadiusY = 2
            };
            Canvas.SetLeft(msgBar, x + gap);
            Canvas.SetTop(msgBar, marginTop + chartH - msgH);
            ChartCanvas.Children.Add(msgBar);

            // Tool calls bar (blue)
            double tcH = (double)data[i].ToolCalls / niceMax * chartH;
            var tcBar = new Rectangle
            {
                Width = barWidth,
                Height = Math.Max(1, tcH),
                Fill = new SolidColorBrush(Color.FromRgb(33, 150, 243)),
                RadiusX = 2, RadiusY = 2
            };
            Canvas.SetLeft(tcBar, x + gap + barWidth);
            Canvas.SetTop(tcBar, marginTop + chartH - tcH);
            ChartCanvas.Children.Add(tcBar);

            // Sessions bar (orange) - scaled up for visibility
            double sessH = (double)data[i].Sessions / niceMax * chartH * 20;
            sessH = Math.Min(sessH, chartH);
            var sessBar = new Rectangle
            {
                Width = barWidth,
                Height = Math.Max(1, sessH),
                Fill = new SolidColorBrush(Color.FromRgb(255, 152, 0)),
                RadiusX = 2, RadiusY = 2
            };
            Canvas.SetLeft(sessBar, x + gap + barWidth * 2);
            Canvas.SetTop(sessBar, marginTop + chartH - sessH);
            ChartCanvas.Children.Add(sessBar);

            // Date label
            string dateLabel = data[i].Date.Length >= 10
                ? data[i].Date[5..] // MM-DD
                : data[i].Date;
            var dateTxt = new TextBlock
            {
                Text = dateLabel,
                FontSize = 9,
                Foreground = Brushes.Gray,
                RenderTransform = new RotateTransform(-45)
            };
            Canvas.SetLeft(dateTxt, x + barGroupWidth * 0.15);
            Canvas.SetTop(dateTxt, marginTop + chartH + 5);
            ChartCanvas.Children.Add(dateTxt);

            // Message count on top
            if (data[i].Messages > 0)
            {
                var countTxt = new TextBlock
                {
                    Text = $"{data[i].Messages}",
                    FontSize = 8,
                    Foreground = Brushes.Gray
                };
                Canvas.SetLeft(countTxt, x + gap);
                Canvas.SetTop(countTxt, marginTop + chartH - msgH - 12);
                ChartCanvas.Children.Add(countTxt);
            }
        }
    }
}
