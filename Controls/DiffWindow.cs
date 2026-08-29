using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Claucraft.Services;

namespace Claucraft.Controls;

/// <summary>
/// Standalone window that shows unified-diff-style text (as produced by
/// <see cref="GitChangeService.GetDiffAsync"/>) with basic +/-/@@ syntax coloring.
/// Renders each line as a virtualized row so large diffs stay responsive.
/// </summary>
public class DiffWindow : Window
{
    private readonly bool _isDark;
    private readonly Typeface _typeface;
    private readonly string _diffText;

    public DiffWindow(string title, string diffText, bool isDark, Typeface typeface)
    {
        _isDark = isDark;
        _typeface = typeface;
        _diffText = diffText ?? "";

        Title = title;
        Width = 900;
        Height = 700;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(isDark ? Color.FromRgb(28, 28, 30) : Color.FromRgb(255, 255, 255));

        var copyBtn = CreateToolButton(Loc.Get("Copy", "Copy"));
        copyBtn.Click += async (_, _) => await CopyDiffToClipboard();

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(10, 6),
        };
        toolbar.Children.Add(copyBtn);

        var toolbarBorder = new Border
        {
            Child = toolbar,
            BorderBrush = new SolidColorBrush(isDark ? Color.FromRgb(56, 56, 58) : Color.FromRgb(210, 210, 215)),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };

        var lines = _diffText.Replace("\r\n", "\n").Split('\n');

        var itemsControl = new ItemsControl
        {
            ItemsSource = lines,
            ItemTemplate = new FuncDataTemplate<string>((line, _) => BuildLineControl(line ?? ""), true),
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
            Background = new SolidColorBrush(isDark ? Color.FromRgb(24, 24, 26) : Color.FromRgb(250, 250, 252)),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(itemsControl, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(itemsControl, ScrollBarVisibility.Auto);

        var dock = new DockPanel();
        DockPanel.SetDock(toolbarBorder, Dock.Top);
        dock.Children.Add(toolbarBorder);
        dock.Children.Add(itemsControl);

        Content = dock;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }

    private Control BuildLineControl(string line)
    {
        return new TextBlock
        {
            Text = line.Length == 0 ? " " : line,
            FontFamily = _typeface.FontFamily,
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap,
            Foreground = new SolidColorBrush(LineColor(line)),
            Padding = new Thickness(8, 0, 24, 0),
        };
    }

    private Color LineColor(string line)
    {
        if (line.StartsWith("diff ") || line.StartsWith("index ") ||
            line.StartsWith("---") || line.StartsWith("+++"))
            return _isDark ? Color.FromRgb(140, 140, 145) : Color.FromRgb(150, 150, 155);

        if (line.StartsWith("@@"))
            return _isDark ? Color.FromRgb(100, 180, 255) : Color.FromRgb(0, 110, 220);

        if (line.StartsWith("+"))
            return Color.FromRgb(0x30, 0xD1, 0x58);

        if (line.StartsWith("-"))
            return Color.FromRgb(0xFF, 0x45, 0x3A);

        return _isDark ? Color.FromRgb(220, 220, 225) : Color.FromRgb(28, 28, 30);
    }

    private async System.Threading.Tasks.Task CopyDiffToClipboard()
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
                await clipboard.SetTextAsync(_diffText);
        }
        catch { }
    }

    private Button CreateToolButton(string text)
    {
        return new Button
        {
            Content = text,
            FontSize = 12,
            Padding = new Thickness(10, 4),
            Background = new SolidColorBrush(_isDark ? Color.FromRgb(50, 50, 52) : Color.FromRgb(230, 230, 235)),
            Foreground = new SolidColorBrush(_isDark ? Color.FromRgb(210, 210, 215) : Color.FromRgb(28, 28, 30)),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Cursor = new Cursor(StandardCursorType.Hand),
            Margin = new Thickness(0, 0, 6, 0),
        };
    }
}
