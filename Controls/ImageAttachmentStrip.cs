using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Claucraft.Controls;

/// <summary>
/// Row of image thumbnails waiting to go out with the next prompt, the way the Claude
/// desktop app shows pasted images above its input. Clicking a thumbnail opens the image
/// in its own window; the × that appears on hover drops it. The strip only holds the
/// references — the owner decides when to hand them to the CLI (see <see cref="TakeReferences"/>).
/// </summary>
public class ImageAttachmentStrip : Border
{
    /// <summary>Height the owner reserves for the strip while it has items.</summary>
    public const double StripHeight = 92;

    private const double ThumbWidth = 112;
    private const double ThumbHeight = 76;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp",
    };

    private sealed record Attachment(string Path, string Reference, Control View, Bitmap? Thumb);

    private readonly List<Attachment> _items = new();
    private readonly StackPanel _row;
    private bool _isDark = true;

    /// <summary>Raised whenever an item is added or removed, so the owner can re-lay out.</summary>
    public event Action? ItemsChanged;

    public bool HasItems => _items.Count > 0;

    public ImageAttachmentStrip()
    {
        _row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Child = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = _row,
        };
        Padding = new Thickness(8, 6, 8, 4);
        BorderThickness = new Thickness(0, 1, 0, 0);
        IsVisible = false;
        UpdateTheme(true);
    }

    public static bool IsImageFile(string path)
        => ImageExtensions.Contains(System.IO.Path.GetExtension(path)) && File.Exists(path);

    /// <summary>
    /// Adds an image. <paramref name="reference"/> is the text the CLI gets for it on submit.
    /// The same file twice is kept once.
    /// </summary>
    public void Add(string path, string reference)
    {
        if (_items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)))
            return;

        Bitmap? thumb = null;
        try
        {
            using var stream = File.OpenRead(path);
            thumb = Bitmap.DecodeToWidth(stream, (int)(ThumbWidth * 2));
        }
        catch { }

        Attachment? item = null;
        var view = BuildThumbnail(path, thumb, () => { if (item != null) Remove(item); });
        item = new Attachment(path, reference, view, thumb);
        _items.Add(item);
        _row.Children.Add(view);
        IsVisible = true;
        ItemsChanged?.Invoke();
    }

    /// <summary>
    /// Returns the references of every item, space-separated, and empties the strip.
    /// Empty string when there is nothing attached.
    /// </summary>
    public string TakeReferences()
    {
        if (_items.Count == 0) return "";
        var text = string.Join(" ", _items.Select(i => i.Reference));
        Clear();
        return text;
    }

    public void Clear()
    {
        if (_items.Count == 0) return;
        foreach (var item in _items)
            item.Thumb?.Dispose();
        _items.Clear();
        _row.Children.Clear();
        IsVisible = false;
        ItemsChanged?.Invoke();
    }

    private void Remove(Attachment item)
    {
        if (!_items.Remove(item)) return;
        _row.Children.Remove(item.View);
        item.Thumb?.Dispose();
        IsVisible = _items.Count > 0;
        ItemsChanged?.Invoke();
    }

    public void UpdateTheme(bool isDark)
    {
        _isDark = isDark;
        Background = new SolidColorBrush(isDark ? Color.FromRgb(30, 30, 32) : Color.FromRgb(250, 250, 252));
        BorderBrush = new SolidColorBrush(isDark ? Color.FromRgb(56, 56, 58) : Color.FromRgb(198, 198, 200));
        foreach (var item in _items)
            if (item.View is Panel { Children: [Border frame, ..] })
                frame.BorderBrush = ThumbBorderBrush();
    }

    private IBrush ThumbBorderBrush()
        => new SolidColorBrush(_isDark ? Color.FromRgb(72, 72, 76) : Color.FromRgb(210, 210, 214));

    private Control BuildThumbnail(string path, Bitmap? thumb, Action remove)
    {
        var frame = new Border
        {
            Width = ThumbWidth,
            Height = ThumbHeight,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = ThumbBorderBrush(),
            Background = new SolidColorBrush(Color.FromRgb(20, 20, 22)),
            ClipToBounds = true,
            Cursor = new Cursor(StandardCursorType.Cross),
            Child = thumb != null
                ? new Image { Source = thumb, Stretch = Stretch.Uniform, Margin = new Thickness(4) }
                : new TextBlock
                {
                    Text = System.IO.Path.GetFileName(path),
                    Foreground = Brushes.Gray,
                    FontSize = 10,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(6),
                    VerticalAlignment = VerticalAlignment.Center,
                },
        };
        ToolTip.SetTip(frame, System.IO.Path.GetFileName(path));
        frame.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            ImageViewerWindow.Open(path, TopLevel.GetTopLevel(this) as Window);
            e.Handled = true;
        };

        var close = new Button
        {
            Content = "✕",
            Width = 20,
            Height = 20,
            Padding = new Thickness(0),
            FontSize = 10,
            CornerRadius = new CornerRadius(10),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, -6, -6, 0),
            Background = new SolidColorBrush(Color.FromRgb(250, 250, 250)),
            Foreground = new SolidColorBrush(Color.FromRgb(60, 60, 64)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(200, 200, 204)),
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand),
            IsVisible = false,
            Focusable = false,
        };
        close.Click += (_, e) => { remove(); e.Handled = true; };

        // The × overhangs the corner, so the cell leaves room for it
        var cell = new Panel { Margin = new Thickness(0, 6, 6, 0) };
        cell.Children.Add(frame);
        cell.Children.Add(close);
        cell.PointerEntered += (_, _) => close.IsVisible = true;
        cell.PointerExited += (_, _) => close.IsVisible = false;
        return cell;
    }
}

/// <summary>A plain window showing one image at up to 85% of the screen.</summary>
public class ImageViewerWindow : Window
{
    public static void Open(string path, Window? owner)
    {
        Bitmap bitmap;
        try { bitmap = new Bitmap(path); }
        catch { return; }

        var win = new ImageViewerWindow(path, bitmap, owner);
        if (owner != null) win.Show(owner); else win.Show();
    }

    private ImageViewerWindow(string path, Bitmap bitmap, Window? owner)
    {
        Title = System.IO.Path.GetFileName(path);
        Background = new SolidColorBrush(Color.FromRgb(24, 24, 26));
        WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;

        var screen = (owner?.Screens ?? Screens).ScreenFromWindow(owner ?? (WindowBase)this)
            ?? (owner?.Screens ?? Screens).Primary;
        double scale = screen?.Scaling ?? 1;
        double maxW = (screen?.WorkingArea.Width ?? 1600) / scale * 0.85;
        double maxH = (screen?.WorkingArea.Height ?? 900) / scale * 0.85;
        double imgW = bitmap.Size.Width, imgH = bitmap.Size.Height;
        double fit = Math.Min(1, Math.Min(maxW / imgW, maxH / imgH));
        Width = Math.Max(240, imgW * fit);
        Height = Math.Max(160, imgH * fit);

        Content = new Image { Source = bitmap, Stretch = Stretch.Uniform };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Closed += (_, _) => bitmap.Dispose();
    }
}
