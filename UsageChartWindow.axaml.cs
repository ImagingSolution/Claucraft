using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace ClaudeCodeMDI;

public partial class UsageChartWindow : Window
{
    private record DailyData(string Date, int Messages, int ToolCalls, int Sessions);

    public UsageChartWindow()
    {
        InitializeComponent();
        Opened += (_, _) => DrawChart();
    }

    private List<DailyData> LoadData()
    {
        var data = new List<DailyData>();
        try
        {
            string path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude", "stats-cache.json");
            if (!System.IO.File.Exists(path)) return data;

            string json = System.IO.File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("dailyActivity", out var arr)) return data;

            foreach (var item in arr.EnumerateArray())
            {
                data.Add(new DailyData(
                    item.GetProperty("date").GetString() ?? "",
                    item.TryGetProperty("messageCount", out var mc) ? mc.GetInt32() : 0,
                    item.TryGetProperty("toolCallCount", out var tc) ? tc.GetInt32() : 0,
                    item.TryGetProperty("sessionCount", out var sc) ? sc.GetInt32() : 0
                ));
            }
        }
        catch { }

        // Last 14 days
        return data.TakeLast(14).ToList();
    }

    private void DrawChart()
    {
        ChartCanvas.Children.Clear();
        var data = LoadData();
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
