using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Claucraft.Services;

namespace Claucraft.Controls;

/// <summary>
/// Dashboard window showing token usage and estimated cost, aggregated from Claude Code
/// session transcripts via <see cref="CostAnalytics"/>.
/// </summary>
public class CostDashboardWindow : Window
{
    private readonly bool _isDark;
    private readonly string? _projectFolder;

    private int _selectedDays = 30;
    private bool _projectOnly;
    private CancellationTokenSource? _cts;

    private readonly Button _btn7;
    private readonly Button _btn30;
    private readonly Button _btn90;
    private readonly CheckBox _scopeCheck;
    private readonly Button _exportBtn;

    private readonly TextBlock _loadingText;
    private readonly TextBlock _totalCostText;
    private readonly TextBlock _tokenBreakdownText;
    private readonly Canvas _chartCanvas;
    private readonly StackPanel _modelRows;
    private readonly StackPanel _projectRows;
    private readonly StackPanel _sessionRows;

    private CostReport? _lastReport;

    public CostDashboardWindow(bool isDark, string? projectFolder)
    {
        _isDark = isDark;
        _projectFolder = projectFolder;
        _projectOnly = projectFolder != null;

        Title = Loc.Get("CostDashboardTitle", "Token / Cost Dashboard");
        Width = 1000;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Bg());

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        // ── Toolbar ──
        _btn7 = CreatePeriodButton("7" + Loc.Get("CostDaysSuffix", "d"), 7);
        _btn30 = CreatePeriodButton("30" + Loc.Get("CostDaysSuffix", "d"), 30);
        _btn90 = CreatePeriodButton("90" + Loc.Get("CostDaysSuffix", "d"), 90);

        _scopeCheck = new CheckBox
        {
            Content = Loc.Get("CostScopeProjectOnly", "This project only"),
            IsChecked = _projectOnly,
            IsEnabled = projectFolder != null,
            Foreground = new SolidColorBrush(TextColor()),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0),
        };
        _scopeCheck.IsCheckedChanged += (_, _) =>
        {
            _projectOnly = _scopeCheck.IsChecked == true && projectFolder != null;
            _ = ReloadAsync();
        };

        _exportBtn = CreateToolButton(Loc.Get("CostExportCsv", "Export CSV"));
        _exportBtn.Margin = new Thickness(16, 0, 0, 0);
        _exportBtn.Click += async (_, _) => await ExportCsvAsync();

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(16, 12, 16, 4),
        };
        toolbar.Children.Add(_btn7);
        toolbar.Children.Add(_btn30);
        toolbar.Children.Add(_btn90);
        toolbar.Children.Add(_scopeCheck);
        toolbar.Children.Add(_exportBtn);
        UpdatePeriodButtons();

        // ── Summary row ──
        _loadingText = new TextBlock
        {
            Text = Loc.Get("CostLoading", "Loading…"),
            FontSize = 13,
            Foreground = new SolidColorBrush(SubtleColor()),
            Margin = new Thickness(16, 4, 16, 4),
        };

        _totalCostText = new TextBlock
        {
            Text = "$0.00",
            FontSize = 26,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(TextColor()),
        };
        _tokenBreakdownText = new TextBlock
        {
            Text = "",
            FontSize = 12,
            Foreground = new SolidColorBrush(SubtleColor()),
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
        var summaryPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(16, 4, 16, 8),
        };
        summaryPanel.Children.Add(_totalCostText);
        summaryPanel.Children.Add(_tokenBreakdownText);

        // ── Chart ──
        _chartCanvas = new Canvas
        {
            Height = 160,
            Margin = new Thickness(16, 4, 16, 8),
            Background = new SolidColorBrush(PanelBg()),
        };

        // ── Breakdown lists ──
        _modelRows = new StackPanel();
        _projectRows = new StackPanel();
        _sessionRows = new StackPanel();

        var listsGrid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,*,*") };
        var modelPanel = BuildListPanel(Loc.Get("CostByModel", "By Model"), _modelRows);
        var projectPanel = BuildListPanel(Loc.Get("CostByProject", "By Project"), _projectRows);
        var sessionPanel = BuildListPanel(Loc.Get("CostBySession", "By Session (top 50)"), _sessionRows);
        Grid.SetColumn(modelPanel, 0);
        Grid.SetColumn(projectPanel, 1);
        Grid.SetColumn(sessionPanel, 2);
        modelPanel.Margin = new Thickness(16, 0, 4, 16);
        projectPanel.Margin = new Thickness(4, 0, 4, 16);
        sessionPanel.Margin = new Thickness(4, 0, 16, 16);
        listsGrid.Children.Add(modelPanel);
        listsGrid.Children.Add(projectPanel);
        listsGrid.Children.Add(sessionPanel);

        var root = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(_loadingText, Dock.Top);
        DockPanel.SetDock(summaryPanel, Dock.Top);
        DockPanel.SetDock(_chartCanvas, Dock.Top);
        root.Children.Add(toolbar);
        root.Children.Add(_loadingText);
        root.Children.Add(summaryPanel);
        root.Children.Add(_chartCanvas);
        root.Children.Add(listsGrid);

        Content = root;

        Opened += (_, _) => _ = ReloadAsync();
        Closed += (_, _) => _cts?.Cancel();
    }

    // ── Colors ──
    private Color Bg() => _isDark ? Color.FromRgb(28, 28, 30) : Color.FromRgb(255, 255, 255);
    private Color PanelBg() => _isDark ? Color.FromRgb(38, 38, 41) : Color.FromRgb(245, 245, 248);
    private Color TextColor() => _isDark ? Color.FromRgb(230, 230, 235) : Color.FromRgb(28, 28, 30);
    private Color SubtleColor() => _isDark ? Color.FromRgb(160, 160, 165) : Color.FromRgb(100, 100, 105);
    private static readonly Color AccentColor = Color.FromRgb(0, 122, 255); // Apple Blue

    private Button CreatePeriodButton(string text, int days)
    {
        var btn = new Button
        {
            Content = text,
            FontSize = 12,
            Padding = new Thickness(12, 5),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Cursor = new Cursor(StandardCursorType.Hand),
            Margin = new Thickness(0, 0, 4, 0),
            Tag = days,
        };
        btn.Click += (_, _) =>
        {
            if (_selectedDays == days) return;
            _selectedDays = days;
            UpdatePeriodButtons();
            _ = ReloadAsync();
        };
        return btn;
    }

    private Button CreateToolButton(string text)
    {
        return new Button
        {
            Content = text,
            FontSize = 12,
            Padding = new Thickness(12, 5),
            Background = new SolidColorBrush(_isDark ? Color.FromRgb(50, 50, 52) : Color.FromRgb(230, 230, 235)),
            Foreground = new SolidColorBrush(TextColor()),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
    }

    private void UpdatePeriodButtons()
    {
        foreach (var btn in new[] { _btn7, _btn30, _btn90 })
        {
            bool selected = btn.Tag is int d && d == _selectedDays;
            btn.Background = new SolidColorBrush(selected
                ? AccentColor
                : (_isDark ? Color.FromRgb(50, 50, 52) : Color.FromRgb(230, 230, 235)));
            btn.Foreground = new SolidColorBrush(selected ? Colors.White : TextColor());
        }
    }

    private Border BuildListPanel(string title, StackPanel rowsHost)
    {
        var header = new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(TextColor()),
            Margin = new Thickness(10, 8, 10, 4),
        };

        var columnsHeader = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,Auto"), Margin = new Thickness(10, 0, 10, 2) };
        columnsHeader.Children.Add(HeaderCell(Loc.Get("CostColLabel", "Label"), 0, HorizontalAlignment.Left));
        columnsHeader.Children.Add(HeaderCell(Loc.Get("CostColTokens", "Tokens"), 1, HorizontalAlignment.Right));
        columnsHeader.Children.Add(HeaderCell(Loc.Get("CostColCost", "Cost"), 2, HorizontalAlignment.Right));

        var scroller = new ScrollViewer
        {
            Content = rowsHost,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };

        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(columnsHeader, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(columnsHeader);
        dock.Children.Add(scroller);

        return new Border
        {
            Background = new SolidColorBrush(PanelBg()),
            CornerRadius = new CornerRadius(6),
            Child = dock,
        };
    }

    private TextBlock HeaderCell(string text, int col, HorizontalAlignment align)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = new SolidColorBrush(SubtleColor()),
            HorizontalAlignment = align,
            Margin = col == 0 ? new Thickness(0) : new Thickness(12, 0, 0, 0),
        };
        Grid.SetColumn(tb, col);
        return tb;
    }

    private async Task ReloadAsync()
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        var token = cts.Token;

        _loadingText.IsVisible = true;
        _loadingText.Text = Loc.Get("CostLoading", "Loading…");

        string? scope = _projectOnly ? _projectFolder : null;

        try
        {
            var report = await CostAnalytics.BuildAsync(_selectedDays, scope, token);
            if (token.IsCancellationRequested) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (token.IsCancellationRequested) return;
                _lastReport = report;
                RenderReport(report);
                _loadingText.IsVisible = false;
            });
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer reload
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _loadingText.Text = Loc.Get("CostLoadError", "Failed to load usage data") + ": " + ex.Message;
            });
        }
    }

    private void RenderReport(CostReport report)
    {
        _totalCostText.Text = $"${report.Grand.CostUsd:F2}";
        _tokenBreakdownText.Text =
            $"{Loc.Get("CostInput", "Input")}: {HumanCount(report.Grand.Input)}   " +
            $"{Loc.Get("CostOutput", "Output")}: {HumanCount(report.Grand.Output)}   " +
            $"{Loc.Get("CostCacheRead", "Cache Read")}: {HumanCount(report.Grand.CacheRead)}   " +
            $"{Loc.Get("CostCacheCreation", "Cache Creation")}: {HumanCount(report.Grand.CacheCreation)}   " +
            $"{Loc.Get("CostTotalTokens", "Total")}: {HumanCount(report.Grand.Total)}";

        DrawChart(report.ByDay);

        FillRows(_modelRows, report.ByModel);
        FillRows(_projectRows, report.ByProject);
        FillRows(_sessionRows, report.BySession);
    }

    private void FillRows(StackPanel host, List<CostBucket> buckets)
    {
        host.Children.Clear();
        if (buckets.Count == 0)
        {
            host.Children.Add(new TextBlock
            {
                Text = Loc.Get("CostNoData", "No data"),
                FontSize = 12,
                Foreground = new SolidColorBrush(SubtleColor()),
                Margin = new Thickness(10, 6),
            });
            return;
        }

        foreach (var b in buckets)
        {
            var row = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,Auto"), Margin = new Thickness(10, 3, 10, 3) };

            var label = new TextBlock
            {
                Text = b.Label,
                FontSize = 12,
                Foreground = new SolidColorBrush(TextColor()),
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            };
            ToolTip.SetTip(label, b.Label);
            Grid.SetColumn(label, 0);

            var tokens = new TextBlock
            {
                Text = HumanCount(b.Totals.Total),
                FontSize = 12,
                Foreground = new SolidColorBrush(SubtleColor()),
                Margin = new Thickness(8, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            Grid.SetColumn(tokens, 1);

            var cost = new TextBlock
            {
                Text = $"${b.Totals.CostUsd:F2}",
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(TextColor()),
                HorizontalAlignment = HorizontalAlignment.Right,
                MinWidth = 56,
            };
            Grid.SetColumn(cost, 2);

            row.Children.Add(label);
            row.Children.Add(tokens);
            row.Children.Add(cost);
            host.Children.Add(row);
        }
    }

    private void DrawChart(List<CostBucket> byDay)
    {
        _chartCanvas.Children.Clear();
        if (byDay.Count == 0) return;

        double canvasW = _chartCanvas.Bounds.Width;
        double canvasH = _chartCanvas.Bounds.Height;
        if (canvasW <= 0) canvasW = 960;
        if (canvasH <= 0) canvasH = 160;

        const double marginBottom = 18;
        const double marginTop = 6;
        double chartH = canvasH - marginTop - marginBottom;

        double maxCost = byDay.Max(d => d.Totals.CostUsd);
        if (maxCost <= 0) maxCost = 1;

        double groupW = canvasW / byDay.Count;
        double barW = Math.Max(1, groupW * 0.6);

        // Show a date label roughly every N bars so labels do not overlap.
        int labelStride = Math.Max(1, (int)Math.Ceiling(byDay.Count * 9.0 / Math.Max(canvasW, 1)));

        for (int i = 0; i < byDay.Count; i++)
        {
            var bucket = byDay[i];
            double barH = Math.Max(1, bucket.Totals.CostUsd / maxCost * chartH);
            double x = i * groupW + (groupW - barW) / 2;
            double y = marginTop + chartH - barH;

            var rect = new Rectangle
            {
                Width = barW,
                Height = barH,
                Fill = new SolidColorBrush(bucket.Totals.CostUsd > 0 ? AccentColor : SubtleColor()),
                RadiusX = 1.5,
                RadiusY = 1.5,
                Opacity = bucket.Totals.CostUsd > 0 ? 1.0 : 0.25,
            };
            ToolTip.SetTip(rect, $"{bucket.Label}: ${bucket.Totals.CostUsd:F2} ({HumanCount(bucket.Totals.Total)} tok)");
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y);
            _chartCanvas.Children.Add(rect);

            if (i % labelStride == 0 || i == byDay.Count - 1)
            {
                string shortLabel = bucket.Label.Length >= 10 ? bucket.Label[5..] : bucket.Label;
                var dateTxt = new TextBlock
                {
                    Text = shortLabel,
                    FontSize = 8,
                    Foreground = new SolidColorBrush(SubtleColor()),
                };
                Canvas.SetLeft(dateTxt, x - 4);
                Canvas.SetTop(dateTxt, marginTop + chartH + 3);
                _chartCanvas.Children.Add(dateTxt);
            }
        }
    }

    private async Task ExportCsvAsync()
    {
        var report = _lastReport;
        if (report == null) return;

        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Loc.Get("CostExportCsv", "Export CSV"),
                DefaultExtension = "csv",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("CSV") { Patterns = new[] { "*.csv" } }
                },
                SuggestedFileName = $"cost-report_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            });
            if (file == null) return;

            string csv = CostAnalytics.ToCsv(report);
            var bytes = new UTF8Encoding(false).GetBytes(csv);
            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(bytes);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"CostDashboardWindow.ExportCsvAsync error: {ex.Message}");
        }
    }

    private static string HumanCount(long n)
    {
        double abs = Math.Abs(n);
        if (abs >= 1_000_000_000) return (n / 1_000_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "B";
        if (abs >= 1_000_000) return (n / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M";
        if (abs >= 1_000) return (n / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k";
        return n.ToString(CultureInfo.InvariantCulture);
    }
}
