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

namespace Claucraft;

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
        var lookup = new Dictionary<string, (int messages, int toolCalls, HashSet<string> sessions)>();
        var today = DateTime.Today;
        var startDate = today.AddDays(-13);

        // Initialize 14-day slots
        for (int i = 0; i < 14; i++)
        {
            string dateStr = startDate.AddDays(i).ToString("yyyy-MM-dd");
            lookup[dateStr] = (0, 0, new HashSet<string>());
        }

        // First try stats-cache.json for dates it covers
        string? lastComputedDate = null;
        try
        {
            string cachePath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude", "stats-cache.json");
            if (File.Exists(cachePath))
            {
                string json = File.ReadAllText(cachePath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("lastComputedDate", out var lcd))
                    lastComputedDate = lcd.GetString();

                if (root.TryGetProperty("dailyActivity", out var arr))
                {
                    foreach (var item in arr.EnumerateArray())
                    {
                        string date = item.GetProperty("date").GetString() ?? "";
                        if (lookup.ContainsKey(date))
                        {
                            int mc = item.TryGetProperty("messageCount", out var mcv) ? mcv.GetInt32() : 0;
                            int tc = item.TryGetProperty("toolCallCount", out var tcv) ? tcv.GetInt32() : 0;
                            int sc = item.TryGetProperty("sessionCount", out var scv) ? scv.GetInt32() : 0;
                            var sessions = new HashSet<string>();
                            for (int s = 0; s < sc; s++) sessions.Add($"cache_{date}_{s}");
                            lookup[date] = (mc, tc, sessions);
                        }
                    }
                }
            }
        }
        catch { }

        // Determine which dates need to be filled from session JSONL files
        DateTime scanFrom = startDate;
        if (lastComputedDate != null &&
            DateTime.TryParseExact(lastComputedDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var lcd2))
        {
            // Only scan dates after lastComputedDate (cache covers earlier dates)
            if (lcd2 >= startDate)
                scanFrom = lcd2.AddDays(1);
        }

        // Scan session JSONL files for dates not covered by cache
        if (scanFrom <= today)
        {
            try
            {
                string projectsDir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".claude", "projects");
                if (Directory.Exists(projectsDir))
                {
                    foreach (var projDir in Directory.GetDirectories(projectsDir))
                    {
                        foreach (var jsonlFile in Directory.GetFiles(projDir, "*.jsonl"))
                        {
                            // Skip files older than our scan range
                            var fileInfo = new FileInfo(jsonlFile);
                            if (fileInfo.LastWriteTime.Date < scanFrom) continue;

                            string sessionId = System.IO.Path.GetFileNameWithoutExtension(jsonlFile);
                            try
                            {
                                using var reader = new StreamReader(jsonlFile, System.Text.Encoding.UTF8);
                                string? line;
                                while ((line = reader.ReadLine()) != null)
                                {
                                    if (line.Length < 10) continue;

                                    using var lineDoc = JsonDocument.Parse(line);
                                    var obj = lineDoc.RootElement;

                                    if (!obj.TryGetProperty("type", out var typeProp)) continue;
                                    string? type = typeProp.GetString();
                                    if (type != "user" && type != "assistant") continue;

                                    if (!obj.TryGetProperty("timestamp", out var tsProp)) continue;
                                    string? ts = tsProp.GetString();
                                    if (ts == null || ts.Length < 10) continue;

                                    string dateKey = ts[..10]; // yyyy-MM-dd
                                    if (!lookup.ContainsKey(dateKey)) continue;
                                    if (DateTime.TryParseExact(dateKey, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) && dt < scanFrom)
                                        continue;

                                    var entry = lookup[dateKey];

                                    if (type == "user")
                                    {
                                        entry.messages++;
                                        entry.sessions.Add(sessionId);
                                    }
                                    else if (type == "assistant")
                                    {
                                        // Count tool_use in content array
                                        if (obj.TryGetProperty("message", out var msg) &&
                                            msg.TryGetProperty("content", out var content) &&
                                            content.ValueKind == JsonValueKind.Array)
                                        {
                                            foreach (var c in content.EnumerateArray())
                                            {
                                                if (c.TryGetProperty("type", out var ct) && ct.GetString() == "tool_use")
                                                    entry.toolCalls++;
                                            }
                                        }
                                    }

                                    lookup[dateKey] = entry;
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }
        }

        // Build result
        var result = new List<DailyData>();
        for (int i = 0; i < 14; i++)
        {
            string dateStr = startDate.AddDays(i).ToString("yyyy-MM-dd");
            var entry = lookup[dateStr];
            result.Add(new DailyData(dateStr, entry.messages, entry.toolCalls, entry.sessions.Count));
        }
        return result;
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
