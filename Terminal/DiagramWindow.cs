using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Claucraft.Services;

namespace Claucraft.Terminal;

/// <summary>
/// Separate window for viewing Excalidraw diagrams with zoom/pan support.
/// </summary>
public class DiagramWindow : Window
{
    private CodeBlockInfo _block;
    private readonly bool _isDark;
    private readonly Typeface _typeface;
    private DiagramCanvas _canvas;
    private readonly DockPanel _dock;
    private readonly TextBlock _zoomLabel;

    /// <summary>
    /// Open a .excalidraw file and show it in a new DiagramWindow.
    /// </summary>
    public static async System.Threading.Tasks.Task OpenFile(Window owner, bool isDark, Typeface typeface)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.Get("OpenArtifact"),
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Excalidraw") { Patterns = new[] { "*.excalidraw" } },
            }
        });

        if (files.Count == 0) return;

        var path = files[0].Path.LocalPath;
        var json = await File.ReadAllTextAsync(path);

        // Parse .excalidraw format: { "elements": [...], ... }
        string elementsJson;
        try
        {
            var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("elements", out var elProp))
                elementsJson = elProp.GetRawText();
            else
                elementsJson = json; // fallback: treat entire file as elements array
        }
        catch
        {
            elementsJson = json;
        }

        var block = new CodeBlockInfo(0, 0, "excalidraw", elementsJson, CodeBlockType.Excalidraw);
        var win = new DiagramWindow(block, isDark, typeface);
        win.Title = Path.GetFileName(path) + " - " + Loc.Get("ExcalidrawDiagram");
        win.Show(owner);
    }

    public DiagramWindow(CodeBlockInfo block, bool isDark, Typeface typeface)
    {
        _block = block;
        _isDark = isDark;
        _typeface = typeface;

        Title = block.Type switch
        {
            CodeBlockType.Mermaid => Loc.Get("MermaidDiagram"),
            CodeBlockType.Excalidraw => Loc.Get("ExcalidrawDiagram"),
            _ => Loc.Get("ChartPreview")
        };
        Width = 900;
        Height = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(isDark ? Color.FromRgb(28, 28, 30) : Color.FromRgb(255, 255, 255));

        _canvas = new DiagramCanvas(block, isDark, typeface);

        // Toolbar
        var openBtn = CreateToolButton(Loc.Get("OpenArtifact"), null);
        openBtn.Click += async (_, _) => await OpenArtifactFile();
        var zoomInBtn = CreateToolButton("+", Loc.Get("ZoomIn", "Zoom In"));
        zoomInBtn.Click += (_, _) => _canvas.Zoom(1.2);
        var zoomOutBtn = CreateToolButton("-", Loc.Get("ZoomOut", "Zoom Out"));
        zoomOutBtn.Click += (_, _) => _canvas.Zoom(1 / 1.2);
        var resetBtn = CreateToolButton("1:1", Loc.Get("ResetZoom", "Reset"));
        resetBtn.Click += (_, _) => _canvas.ResetView();
        var saveBtn = CreateToolButton(Loc.Get("SaveImage"), null);
        saveBtn.Click += async (_, _) => await SaveAsPng();
        var copyBtn = CreateToolButton(Loc.Get("CopyImage"), null);
        copyBtn.Click += async (_, _) => await CopyToClipboard();

        _zoomLabel = new TextBlock
        {
            Text = "100%",
            Foreground = new SolidColorBrush(isDark ? Color.FromRgb(160, 160, 165) : Color.FromRgb(100, 100, 105)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0),
            FontSize = 12,
        };
        _canvas.ZoomChanged += z => _zoomLabel.Text = $"{(int)(z * 100)}%";

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6),
        };
        toolbar.Children.Add(openBtn);
        toolbar.Children.Add(new Border { Width = 12 });
        toolbar.Children.Add(zoomOutBtn);
        toolbar.Children.Add(_zoomLabel);
        toolbar.Children.Add(zoomInBtn);
        toolbar.Children.Add(resetBtn);
        toolbar.Children.Add(new Border { Width = 12 });
        toolbar.Children.Add(saveBtn);
        toolbar.Children.Add(copyBtn);

        _dock = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        _dock.Children.Add(toolbar);
        _dock.Children.Add(_canvas);

        Content = _dock;

        // Forward all pointer events from window to canvas for zoom/pan anywhere
        PointerWheelChanged += (_, e) => { _canvas.HandleWheel(e); e.Handled = true; };
        PointerPressed += (_, e) => { _canvas.HandlePointerPressed(e); };
        PointerMoved += (_, e) => { _canvas.HandlePointerMoved(e); };
        PointerReleased += (_, e) => { _canvas.HandlePointerReleased(e); };

        // Support file drag & drop
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, OnFileDrop);
    }

    private async System.Threading.Tasks.Task OpenArtifactFile()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.Get("OpenArtifact"),
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Excalidraw") { Patterns = new[] { "*.excalidraw" } },
            }
        });
        if (files.Count == 0) return;
        await LoadFile(files[0].Path.LocalPath);
    }

    private async System.Threading.Tasks.Task LoadFile(string path)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path);
            string elementsJson;
            try
            {
                var doc = System.Text.Json.JsonDocument.Parse(json);
                elementsJson = doc.RootElement.TryGetProperty("elements", out var elProp)
                    ? elProp.GetRawText() : json;
            }
            catch { elementsJson = json; }

            _block = new CodeBlockInfo(0, 0, "excalidraw", elementsJson, CodeBlockType.Excalidraw);
            var newCanvas = new DiagramCanvas(_block, _isDark, _typeface);
            newCanvas.ZoomChanged += z => _zoomLabel.Text = $"{(int)(z * 100)}%";

            // Replace canvas
            _dock.Children.Remove(_canvas);
            _canvas = newCanvas;
            _dock.Children.Add(_canvas);

            Title = Path.GetFileName(path) + " - " + Loc.Get("ExcalidrawDiagram");
            _zoomLabel.Text = "100%";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LoadFile error: {ex.Message}");
        }
    }

    private async void OnFileDrop(object? sender, DragEventArgs e)
    {
        var files = e.Data.GetFiles();
        if (files == null) return;
        foreach (var file in files)
        {
            var path = file.Path.LocalPath;
            if (path.EndsWith(".excalidraw", StringComparison.OrdinalIgnoreCase))
            {
                await LoadFile(path);
                break;
            }
        }
    }

    private Button CreateToolButton(string text, string? tooltip)
    {
        var btn = new Button
        {
            Content = text,
            FontSize = 12,
            Padding = new Thickness(10, 4),
            Background = new SolidColorBrush(_isDark ? Color.FromRgb(50, 50, 52) : Color.FromRgb(230, 230, 235)),
            Foreground = new SolidColorBrush(_isDark ? Color.FromRgb(210, 210, 215) : Color.FromRgb(28, 28, 30)),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Cursor = new Cursor(StandardCursorType.Hand),
            Margin = new Thickness(3, 0),
        };
        if (tooltip != null) ToolTip.SetTip(btn, tooltip);
        return btn;
    }

    private async System.Threading.Tasks.Task SaveAsPng()
    {
        try
        {
            var pngBytes = _canvas.RenderToPng(2400, 1200);
            if (pngBytes == null) return;

            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Loc.Get("SaveImage"),
                DefaultExtension = "png",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("PNG Image") { Patterns = new[] { "*.png" } }
                },
                SuggestedFileName = $"diagram_{DateTime.Now:yyyyMMdd_HHmmss}.png"
            });
            if (file != null)
            {
                await using var stream = await file.OpenWriteAsync();
                await stream.WriteAsync(pngBytes);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DiagramWindow.SaveAsPng error: {ex.Message}");
        }
    }

    private async System.Threading.Tasks.Task CopyToClipboard()
    {
        try
        {
            var pngBytes = _canvas.RenderToPng(2400, 1200);
            if (pngBytes == null) return;

            var tempPath = Path.Combine(Path.GetTempPath(), "Claucraft", $"diagram_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
            await File.WriteAllBytesAsync(tempPath, pngBytes);

            if (Clipboard != null)
                await Clipboard.SetTextAsync(tempPath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DiagramWindow.CopyToClipboard error: {ex.Message}");
        }
    }
}

/// <summary>
/// Canvas control that renders Excalidraw elements with zoom/pan support.
/// </summary>
public class DiagramCanvas : Control
{
    private readonly CodeBlockInfo _block;
    private readonly bool _isDark;
    private readonly Typeface _typeface;

    private double _zoom = 1.0;
    private double _panX, _panY;
    private bool _isPanning;
    private Point _panStart;
    private double _panStartX, _panStartY;

    // Parsed elements cache
    private List<System.Text.Json.JsonElement>? _drawables;
    private double _minX, _minY, _maxX, _maxY;

    public event Action<double>? ZoomChanged;

    public DiagramCanvas(CodeBlockInfo block, bool isDark, Typeface typeface)
    {
        _block = block;
        _isDark = isDark;
        _typeface = typeface;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Hand);

        ParseElements();
    }

    private void ParseElements()
    {
        try
        {
            var cleanJson = CleanJsonWhitespace(_block.Content);
            var elements = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(cleanJson);
            if (elements.ValueKind != System.Text.Json.JsonValueKind.Array) return;

            var elementMap = new Dictionary<string, System.Text.Json.JsonElement>();
            foreach (var el in elements.EnumerateArray())
            {
                var type = el.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "cameraUpdate" || type == null) continue;
                if (type == "delete")
                {
                    if (el.TryGetProperty("ids", out var ids))
                        foreach (var id in (ids.GetString() ?? "").Split(','))
                            elementMap.Remove(id.Trim());
                    continue;
                }
                if (el.TryGetProperty("id", out var idProp))
                    elementMap[idProp.GetString() ?? ""] = el;
            }
            _drawables = new List<System.Text.Json.JsonElement>(elementMap.Values);

            _minX = double.MaxValue; _minY = double.MaxValue;
            _maxX = double.MinValue; _maxY = double.MinValue;
            foreach (var el in _drawables)
            {
                double ex = el.TryGetProperty("x", out var xp) ? xp.GetDouble() : 0;
                double ey = el.TryGetProperty("y", out var yp) ? yp.GetDouble() : 0;
                double ew = el.TryGetProperty("width", out var wp) ? wp.GetDouble() : 0;
                double eh = el.TryGetProperty("height", out var hp) ? hp.GetDouble() : 0;
                double textW = 0;
                if ((el.TryGetProperty("type", out var tp2) ? tp2.GetString() : "") == "text" && el.TryGetProperty("text", out var txt))
                    textW = (txt.GetString()?.Length ?? 0) * 8;
                _minX = Math.Min(_minX, ex); _minY = Math.Min(_minY, ey);
                _maxX = Math.Max(_maxX, ex + Math.Max(ew, textW));
                _maxY = Math.Max(_maxY, ey + Math.Max(eh, 20));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DiagramCanvas.ParseElements error: {ex.Message}");
        }
    }

    public void Zoom(double factor)
    {
        _zoom = Math.Clamp(_zoom * factor, 0.1, 10);
        ZoomChanged?.Invoke(_zoom);
        InvalidateVisual();
    }

    public void ResetView()
    {
        _zoom = 1.0;
        _panX = 0;
        _panY = 0;
        ZoomChanged?.Invoke(_zoom);
        InvalidateVisual();
    }

    public void HandleWheel(PointerWheelEventArgs e)
    {
        double factor = e.Delta.Y > 0 ? 1.15 : 1 / 1.15;
        double newZoom = Math.Clamp(_zoom * factor, 0.1, 10);
        double actualFactor = newZoom / _zoom;

        // Zoom centered on mouse position:
        // The point under the mouse should stay fixed after zoom.
        // Current world position under mouse: (mouseX - offsetX) / oldScale
        // After zoom with new scale, we adjust pan so the same world point stays under mouse.
        var mousePos = e.GetPosition(this);
        _panX = mousePos.X - (mousePos.X - _panX) * actualFactor;
        _panY = mousePos.Y - (mousePos.Y - _panY) * actualFactor;
        _zoom = newZoom;
        ZoomChanged?.Invoke(_zoom);
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        HandleWheel(e);
        e.Handled = true;
    }

    public void HandlePointerPressed(PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _isPanning = true;
            _panStart = e.GetPosition(this);
            _panStartX = _panX;
            _panStartY = _panY;
            Cursor = new Cursor(StandardCursorType.SizeAll);
        }
    }

    public void HandlePointerMoved(PointerEventArgs e)
    {
        if (_isPanning)
        {
            var pos = e.GetPosition(this);
            _panX = _panStartX + (pos.X - _panStart.X);
            _panY = _panStartY + (pos.Y - _panStart.Y);
            InvalidateVisual();
        }
    }

    public void HandlePointerReleased(PointerReleasedEventArgs e)
    {
        if (_isPanning)
        {
            _isPanning = false;
            Cursor = new Cursor(StandardCursorType.Hand);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        HandlePointerPressed(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        HandlePointerMoved(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        HandlePointerReleased(e);
    }

    public override void Render(DrawingContext context)
    {
        // Draw background
        var bgColor = _isDark ? Color.FromRgb(30, 30, 34) : Color.FromRgb(252, 252, 255);
        context.FillRectangle(new SolidColorBrush(bgColor), new Rect(Bounds.Size));

        if (_drawables == null || _drawables.Count == 0 || !double.IsFinite(_minX)) return;

        double contentW = _maxX - _minX + 40;
        double contentH = _maxY - _minY + 40;
        double fitScale = Math.Min((Bounds.Width - 20) / contentW, (Bounds.Height - 20) / contentH);
        double scale = fitScale * _zoom;

        // Base offset centers content, _panX/_panY are additive screen-space offsets
        double baseOffsetX = Bounds.Width / 2 - (_minX + contentW / 2 - 20) * fitScale;
        double baseOffsetY = Bounds.Height / 2 - (_minY + contentH / 2 - 20) * fitScale;

        // Apply zoom-adjusted pan: panX/panY store the cumulative offset in screen space
        double offsetX = baseOffsetX * (_zoom) + _panX;
        double offsetY = baseOffsetY * (_zoom) + _panY;

        // Recalculate: simpler approach - treat center as anchor, pan is screen offset
        double cx = (_minX + contentW / 2 - 20);
        double cy = (_minY + contentH / 2 - 20);
        offsetX = Bounds.Width / 2 - cx * scale + _panX;
        offsetY = Bounds.Height / 2 - cy * scale + _panY;

        RenderElements(context, _drawables, scale, offsetX, offsetY, _typeface, _isDark);
    }

    public byte[]? RenderToPng(int width, int height)
    {
        if (_drawables == null || _drawables.Count == 0 || !double.IsFinite(_minX)) return null;
        try
        {
            var bitmap = new RenderTargetBitmap(new PixelSize(width, height));
            using (var ctx = bitmap.CreateDrawingContext())
            {
                var bg = _isDark ? Color.FromRgb(30, 30, 34) : Color.FromRgb(252, 252, 255);
                ctx.FillRectangle(new SolidColorBrush(bg), new Rect(0, 0, width, height));

                double contentW = _maxX - _minX + 40;
                double contentH = _maxY - _minY + 40;
                double scale = Math.Min((width - 40) / contentW, (height - 40) / contentH);
                double offsetX = width / 2.0 - (_minX + contentW / 2 - 20) * scale;
                double offsetY = height / 2.0 - (_minY + contentH / 2 - 20) * scale;

                RenderElements(ctx, _drawables, scale, offsetX, offsetY, _typeface, _isDark);
            }
            using var ms = new MemoryStream();
            bitmap.Save(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    internal static void RenderElements(DrawingContext ctx, List<System.Text.Json.JsonElement> drawables,
        double scale, double offsetX, double offsetY, Typeface typeface, bool isDark)
    {
        var defaultTextColor = isDark ? Color.FromRgb(210, 210, 215) : Color.FromRgb(30, 30, 30);

        foreach (var el in drawables)
        {
            var type = el.TryGetProperty("type", out var tp) ? tp.GetString() : "";
            double ex = (el.TryGetProperty("x", out var xp) ? xp.GetDouble() : 0) * scale + offsetX;
            double ey = (el.TryGetProperty("y", out var yp) ? yp.GetDouble() : 0) * scale + offsetY;
            double ew = (el.TryGetProperty("width", out var wp) ? wp.GetDouble() : 0) * scale;
            double eh = (el.TryGetProperty("height", out var hp) ? hp.GetDouble() : 0) * scale;
            var strokeStr = el.TryGetProperty("strokeColor", out var sc) ? sc.GetString() : "#1e1e1e";
            var fillStr = el.TryGetProperty("backgroundColor", out var bc) ? bc.GetString() : "transparent";
            double opacity = el.TryGetProperty("opacity", out var op) ? op.GetDouble() / 100.0 : 1.0;
            double sw = (el.TryGetProperty("strokeWidth", out var swp) ? swp.GetDouble() : 1) * Math.Min(scale, 2);

            Color strokeColor = ParseColor(strokeStr, defaultTextColor);
            Color fillColor = ParseColor(fillStr, Colors.Transparent);

            // Adjust contrast for readability on diagram background
            double brightness = (strokeColor.R * 0.299 + strokeColor.G * 0.587 + strokeColor.B * 0.114) / 255.0;
            if (isDark && brightness < 0.3)
                strokeColor = Color.FromRgb(
                    (byte)Math.Min(255, 255 - strokeColor.R + 40),
                    (byte)Math.Min(255, 255 - strokeColor.G + 40),
                    (byte)Math.Min(255, 255 - strokeColor.B + 40));
            else if (!isDark && brightness > 0.7)
                strokeColor = Color.FromRgb(
                    (byte)Math.Max(0, strokeColor.R - 180),
                    (byte)Math.Max(0, strokeColor.G - 180),
                    (byte)Math.Max(0, strokeColor.B - 180));

            if (opacity < 1)
            {
                strokeColor = Color.FromArgb((byte)(opacity * 255), strokeColor.R, strokeColor.G, strokeColor.B);
                fillColor = Color.FromArgb((byte)(opacity * 255), fillColor.R, fillColor.G, fillColor.B);
            }

            if (type == "rectangle")
            {
                var rect = new Rect(ex, ey, Math.Max(1, ew), Math.Max(1, eh));
                bool hasRoundness = el.TryGetProperty("roundness", out _);
                float r = (float)(hasRoundness ? 6 * Math.Min(scale, 2) : 0);
                if (fillColor.A > 0 && fillStr != "transparent")
                    ctx.FillRectangle(new SolidColorBrush(fillColor), rect, r);
                if (strokeStr != "transparent" && sw > 0)
                    ctx.DrawRectangle(null, new Pen(new SolidColorBrush(strokeColor), sw), rect, r, r);

                if (el.TryGetProperty("label", out var label) && label.TryGetProperty("text", out var lt))
                {
                    double lfs = Math.Clamp((label.TryGetProperty("fontSize", out var lf) ? lf.GetDouble() : 16) * scale, 8, 48);
                    var ft = new FormattedText(lt.GetString() ?? "", CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight, typeface, lfs, new SolidColorBrush(strokeColor));
                    ctx.DrawText(ft, new Point(ex + (ew - ft.Width) / 2, ey + (eh - ft.Height) / 2));
                }
            }
            else if (type == "text")
            {
                var text = el.TryGetProperty("text", out var tt) ? tt.GetString() ?? "" : "";
                double fs = Math.Clamp((el.TryGetProperty("fontSize", out var fsp) ? fsp.GetDouble() : 16) * scale, 8, 48);
                double ty = ey;
                foreach (var line in text.Split('\n'))
                {
                    var ft = new FormattedText(line, CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight, typeface, fs, new SolidColorBrush(strokeColor));
                    ctx.DrawText(ft, new Point(ex, ty));
                    ty += fs * 1.3;
                }
            }
            else if (type == "arrow" || type == "line")
            {
                var pen = new Pen(new SolidColorBrush(strokeColor), sw);
                if (el.TryGetProperty("points", out var pts) && pts.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    var pointList = new List<Point>();
                    foreach (var pt in pts.EnumerateArray())
                    {
                        int idx = 0; double px = 0, py = 0;
                        foreach (var v in pt.EnumerateArray()) { if (idx == 0) px = v.GetDouble(); else if (idx == 1) py = v.GetDouble(); idx++; }
                        if (idx >= 2) pointList.Add(new Point(ex + px * scale, ey + py * scale));
                    }
                    for (int i = 0; i < pointList.Count - 1; i++)
                        ctx.DrawLine(pen, pointList[i], pointList[i + 1]);

                    if (type == "arrow" && pointList.Count >= 2 &&
                        el.TryGetProperty("endArrowhead", out var ea) && ea.GetString() != null)
                    {
                        var last = pointList[^1]; var prev = pointList[^2];
                        double angle = Math.Atan2(last.Y - prev.Y, last.X - prev.X);
                        double arrLen = 10 * Math.Min(scale, 2);
                        ctx.DrawLine(pen, last, new Point(last.X - arrLen * Math.Cos(angle - 0.4), last.Y - arrLen * Math.Sin(angle - 0.4)));
                        ctx.DrawLine(pen, last, new Point(last.X - arrLen * Math.Cos(angle + 0.4), last.Y - arrLen * Math.Sin(angle + 0.4)));
                    }

                    if (el.TryGetProperty("label", out var al) && al.TryGetProperty("text", out var alt) && pointList.Count >= 2)
                    {
                        var mid = pointList[pointList.Count / 2];
                        double lfs = Math.Clamp((al.TryGetProperty("fontSize", out var alf) ? alf.GetDouble() : 14) * scale, 8, 36);
                        var ft = new FormattedText(alt.GetString() ?? "", CultureInfo.CurrentCulture,
                            FlowDirection.LeftToRight, typeface, lfs, new SolidColorBrush(strokeColor));
                        ctx.DrawText(ft, new Point(mid.X - ft.Width / 2, mid.Y - ft.Height - 4));
                    }
                }
            }
            else if (type == "ellipse")
            {
                var geo = new EllipseGeometry(new Rect(ex, ey, Math.Max(1, ew), Math.Max(1, eh)));
                if (fillColor.A > 0 && fillStr != "transparent")
                    ctx.DrawGeometry(new SolidColorBrush(fillColor), null, geo);
                if (strokeStr != "transparent" && sw > 0)
                    ctx.DrawGeometry(null, new Pen(new SolidColorBrush(strokeColor), sw), geo);
            }
        }
    }

    private static Color ParseColor(string? hex, Color defaultColor)
    {
        if (string.IsNullOrEmpty(hex) || hex == "transparent") return Colors.Transparent;
        try { return Color.Parse(hex); } catch { return defaultColor; }
    }

    private static string CleanJsonWhitespace(string json)
    {
        var sb = new System.Text.StringBuilder(json.Length);
        bool inString = false;
        bool escape = false;
        for (int i = 0; i < json.Length; i++)
        {
            char c = json[i];
            if (escape) { sb.Append(c); escape = false; continue; }
            if (c == '\\' && inString) { sb.Append(c); escape = true; continue; }
            if (c == '"') { inString = !inString; sb.Append(c); continue; }
            if (inString) sb.Append(c);
            else if (c > ' ' && c <= '~') sb.Append(c);
        }
        return sb.ToString();
    }
}
