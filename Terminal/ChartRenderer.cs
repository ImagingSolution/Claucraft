using System;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Claucraft.Terminal;

/// <summary>
/// Generates self-contained HTML pages for rendering Mermaid diagrams and Chart.js charts.
/// </summary>
public static class ChartRenderer
{
    public static string GenerateHtml(CodeBlockInfo block, bool isDark)
    {
        return block.Type switch
        {
            CodeBlockType.Mermaid => GenerateMermaidHtml(block.Content, isDark),
            CodeBlockType.Excalidraw => GenerateExcalidrawHtml(block.Content, isDark),
            CodeBlockType.BarChart or CodeBlockType.LineChart or CodeBlockType.PieChart
                => GenerateChartHtml(block.Content, block.Type, isDark),
            _ => GenerateFallbackHtml(block.Content, isDark)
        };
    }

    private static string GenerateMermaidHtml(string content, bool isDark)
    {
        var theme = isDark ? "dark" : "default";
        var bgColor = isDark ? "#1c1c1e" : "#ffffff";
        var escaped = EscapeForHtml(content);

        return $@"<!DOCTYPE html>
<html>
<head>
<meta charset=""utf-8"">
<script src=""https://cdn.jsdelivr.net/npm/mermaid@11/dist/mermaid.min.js""></script>
<style>
  body {{ background: {bgColor}; margin: 0; padding: 16px; display: flex; justify-content: center; align-items: flex-start; min-height: 100vh; font-family: sans-serif; }}
  .mermaid {{ max-width: 100%; }}
  .error {{ color: #ff6b6b; padding: 20px; font-family: monospace; white-space: pre-wrap; }}
</style>
</head>
<body>
<div class=""mermaid"">
{escaped}
</div>
<script>
  mermaid.initialize({{ startOnLoad: true, theme: '{theme}', securityLevel: 'loose' }});
  mermaid.run().catch(err => {{
    document.body.innerHTML = '<div class=""error"">Mermaid Error:\n' + err.message + '</div>';
  }});
</script>
</body>
</html>";
    }

    private static string GenerateChartHtml(string content, CodeBlockType type, bool isDark)
    {
        var bgColor = isDark ? "#1c1c1e" : "#ffffff";
        var textColor = isDark ? "#d2d2d7" : "#1c1c1e";
        var gridColor = isDark ? "rgba(255,255,255,0.1)" : "rgba(0,0,0,0.1)";

        var chartConfig = ParseChartData(content, type, isDark);

        return $@"<!DOCTYPE html>
<html>
<head>
<meta charset=""utf-8"">
<script src=""https://cdn.jsdelivr.net/npm/chart.js@4""></script>
<style>
  body {{ background: {bgColor}; margin: 0; padding: 16px; font-family: sans-serif; }}
  canvas {{ max-width: 100%; max-height: calc(100vh - 32px); }}
  .error {{ color: #ff6b6b; padding: 20px; font-family: monospace; white-space: pre-wrap; }}
</style>
</head>
<body>
<canvas id=""chart""></canvas>
<script>
try {{
  Chart.defaults.color = '{textColor}';
  Chart.defaults.borderColor = '{gridColor}';
  const ctx = document.getElementById('chart').getContext('2d');
  window._chart = new Chart(ctx, {chartConfig});
}} catch(e) {{
  document.body.innerHTML = '<div class=""error"">Chart Error:\n' + e.message + '</div>';
}}
</script>
</body>
</html>";
    }

    private static string GenerateExcalidrawHtml(string elementsJson, bool isDark)
    {
        var bgColor = isDark ? "#1c1c1e" : "#ffffff";
        var textColor = isDark ? "#d2d2d7" : "#1c1c1e";

        // Canvas-based renderer for Excalidraw elements
        // Supports: rectangle, text, arrow, ellipse, diamond, line
        return $@"<!DOCTYPE html>
<html>
<head>
<meta charset=""utf-8"">
<style>
  body {{ background: {bgColor}; margin: 0; padding: 0; overflow: hidden; }}
  canvas {{ display: block; }}
  .error {{ color: #ff6b6b; padding: 20px; font-family: monospace; white-space: pre-wrap; }}
</style>
</head>
<body>
<canvas id=""canvas""></canvas>
<script>
try {{
  const rawElements = {elementsJson};
  const canvas = document.getElementById('canvas');
  const ctx = canvas.getContext('2d');
  const isDark = {(isDark ? "true" : "false")};
  const defaultText = isDark ? '#d2d2d7' : '#1c1c1e';

  // Process delete operations: remove elements by their IDs
  const elementMap = new Map();
  for (const el of rawElements) {{
    if (el.type === 'cameraUpdate') continue;
    if (el.type === 'delete') {{
      // Remove elements listed in ids (comma-separated)
      if (el.ids) {{
        for (const id of el.ids.split(',')) {{
          elementMap.delete(id.trim());
        }}
      }}
      continue;
    }}
    if (el.id) {{
      elementMap.set(el.id, el);
    }}
  }}
  const drawables = Array.from(elementMap.values());

  // Find bounding box of remaining elements
  let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
  for (const el of drawables) {{
    const x = el.x || 0, y = el.y || 0;
    const w = el.width || 0, h = el.height || 0;
    const textLen = el.text ? el.text.length * (el.fontSize || 16) * 0.6 : 0;
    minX = Math.min(minX, x);
    minY = Math.min(minY, y);
    maxX = Math.max(maxX, x + Math.max(w, textLen, 10));
    maxY = Math.max(maxY, y + Math.max(h, 20));
  }}

  if (!isFinite(minX)) {{ minX=0; minY=0; maxX=400; maxY=300; }}

  const padding = 40;
  const contentW = maxX - minX + padding * 2;
  const contentH = maxY - minY + padding * 2;

  // Size canvas to fit content, scaled to viewport
  const scale = Math.min(window.innerWidth / contentW, window.innerHeight / contentH, 2);
  canvas.width = contentW * scale;
  canvas.height = contentH * scale;
  ctx.scale(scale, scale);
  ctx.translate(-minX + padding, -minY + padding);

  for (const el of drawables) {{
    const x = el.x || 0, y = el.y || 0;
    const w = el.width || 0, h = el.height || 0;
    const stroke = el.strokeColor || defaultText;
    const fill = el.backgroundColor || 'transparent';
    const sw = el.strokeWidth || 1;
    const opacity = (el.opacity != null ? el.opacity / 100 : 1);
    const fontSize = el.fontSize || 16;

    ctx.save();
    ctx.globalAlpha = opacity;
    ctx.lineWidth = sw;
    ctx.strokeStyle = stroke === 'transparent' ? 'transparent' : stroke;

    if (el.type === 'rectangle') {{
      const r = el.roundness ? Math.min(8, w/4, h/4) : 0;
      ctx.beginPath();
      if (r > 0) {{
        ctx.moveTo(x+r, y); ctx.lineTo(x+w-r, y);
        ctx.arcTo(x+w,y, x+w,y+r, r);
        ctx.lineTo(x+w, y+h-r); ctx.arcTo(x+w,y+h, x+w-r,y+h, r);
        ctx.lineTo(x+r, y+h); ctx.arcTo(x,y+h, x,y+h-r, r);
        ctx.lineTo(x, y+r); ctx.arcTo(x,y, x+r,y, r);
      }} else {{
        ctx.rect(x, y, w, h);
      }}
      ctx.closePath();
      if (fill !== 'transparent' && el.fillStyle === 'solid') {{
        ctx.fillStyle = fill;
        ctx.fill();
      }}
      if (stroke !== 'transparent') ctx.stroke();

      // Label inside rectangle
      if (el.label && el.label.text) {{
        ctx.fillStyle = el.label.color || stroke || defaultText;
        ctx.font = `${{el.label.fontSize || 16}}px sans-serif`;
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';
        const lines = el.label.text.split('\\n');
        const lh = (el.label.fontSize || 16) * 1.3;
        const startY = y + h/2 - (lines.length-1)*lh/2;
        lines.forEach((line, i) => ctx.fillText(line, x + w/2, startY + i*lh));
      }}
    }}

    else if (el.type === 'text') {{
      ctx.fillStyle = stroke || defaultText;
      ctx.font = `${{fontSize}}px sans-serif`;
      ctx.textAlign = 'left';
      ctx.textBaseline = 'top';
      const lines = (el.text || '').split('\\n');
      const lh = fontSize * 1.3;
      lines.forEach((line, i) => ctx.fillText(line, x, y + i*lh));
    }}

    else if (el.type === 'arrow' || el.type === 'line') {{
      const points = el.points || [[0,0],[w,0]];
      if (points.length >= 2) {{
        ctx.beginPath();
        ctx.moveTo(x + points[0][0], y + points[0][1]);
        for (let i = 1; i < points.length; i++)
          ctx.lineTo(x + points[i][0], y + points[i][1]);
        ctx.stroke();

        // Arrowhead
        if (el.type === 'arrow' && el.endArrowhead) {{
          const last = points[points.length-1];
          const prev = points[points.length-2];
          const angle = Math.atan2(last[1]-prev[1], last[0]-prev[0]);
          const arrLen = 10;
          const lx = x + last[0], ly = y + last[1];
          ctx.beginPath();
          ctx.moveTo(lx, ly);
          ctx.lineTo(lx - arrLen*Math.cos(angle-0.4), ly - arrLen*Math.sin(angle-0.4));
          ctx.moveTo(lx, ly);
          ctx.lineTo(lx - arrLen*Math.cos(angle+0.4), ly - arrLen*Math.sin(angle+0.4));
          ctx.stroke();
        }}

        // Arrow label
        if (el.label && el.label.text) {{
          const mid = points[Math.floor(points.length/2)];
          ctx.fillStyle = stroke || defaultText;
          ctx.font = `${{el.label.fontSize || 14}}px sans-serif`;
          ctx.textAlign = 'center';
          ctx.textBaseline = 'bottom';
          ctx.fillText(el.label.text, x + mid[0], y + mid[1] - 6);
        }}
      }}
    }}

    else if (el.type === 'ellipse') {{
      ctx.beginPath();
      ctx.ellipse(x + w/2, y + h/2, w/2, h/2, 0, 0, Math.PI*2);
      if (fill !== 'transparent' && el.fillStyle === 'solid') {{
        ctx.fillStyle = fill;
        ctx.fill();
      }}
      if (stroke !== 'transparent') ctx.stroke();
    }}

    ctx.restore();
  }}
}} catch(e) {{
  document.body.innerHTML = '<div class=""error"">Render Error:\\n' + e.message + '\\n' + e.stack + '</div>';
}}
</script>
</body>
</html>";
    }

    private static string GenerateFallbackHtml(string content, bool isDark)
    {
        var bgColor = isDark ? "#1c1c1e" : "#ffffff";
        var fgColor = isDark ? "#d2d2d7" : "#1c1c1e";
        var escaped = EscapeForHtml(content);

        return $@"<!DOCTYPE html>
<html>
<head><meta charset=""utf-8""></head>
<body style=""background:{bgColor};color:{fgColor};font-family:monospace;padding:16px;white-space:pre-wrap"">{escaped}</body>
</html>";
    }

    private static string ParseChartData(string content, CodeBlockType type, bool isDark)
    {
        // Try JSON format first
        content = content.Trim();
        if (content.StartsWith("{"))
        {
            try
            {
                // Validate JSON and use as-is
                JsonDocument.Parse(content);
                return content;
            }
            catch { }
        }

        // Parse simple YAML-like format:
        // type: bar
        // title: My Chart
        // data:
        //   Label1: 100
        //   Label2: 200
        var chartType = type switch
        {
            CodeBlockType.PieChart => "pie",
            CodeBlockType.LineChart => "line",
            _ => "bar"
        };

        string title = "";
        var labels = new System.Collections.Generic.List<string>();
        var values = new System.Collections.Generic.List<double>();
        bool inData = false;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            var typeMatch = Regex.Match(line, @"^type:\s*(\w+)", RegexOptions.IgnoreCase);
            if (typeMatch.Success)
            {
                chartType = typeMatch.Groups[1].Value.ToLower();
                continue;
            }

            var titleMatch = Regex.Match(line, @"^title:\s*(.+)", RegexOptions.IgnoreCase);
            if (titleMatch.Success)
            {
                title = titleMatch.Groups[1].Value.Trim();
                continue;
            }

            if (Regex.IsMatch(line, @"^data:\s*$", RegexOptions.IgnoreCase))
            {
                inData = true;
                continue;
            }

            if (inData)
            {
                var dataMatch = Regex.Match(line, @"^(.+?):\s*([\d.]+)");
                if (dataMatch.Success)
                {
                    labels.Add(dataMatch.Groups[1].Value.Trim());
                    if (double.TryParse(dataMatch.Groups[2].Value, out double val))
                        values.Add(val);
                }
            }
        }

        if (labels.Count == 0)
        {
            // Fallback: try simple "label value" format per line
            foreach (var rawLine in content.Split('\n'))
            {
                var line = rawLine.Trim();
                var m = Regex.Match(line, @"^(.+?)\s+([\d.]+)\s*$");
                if (m.Success)
                {
                    labels.Add(m.Groups[1].Value.Trim());
                    if (double.TryParse(m.Groups[2].Value, out double val))
                        values.Add(val);
                }
            }
        }

        var colors = GenerateColors(labels.Count, isDark);
        var labelsJson = JsonSerializer.Serialize(labels);
        var valuesJson = JsonSerializer.Serialize(values);
        var colorsJson = JsonSerializer.Serialize(colors);
        var titleJson = JsonSerializer.Serialize(title);

        return $@"{{
  type: '{chartType}',
  data: {{
    labels: {labelsJson},
    datasets: [{{
      label: {titleJson},
      data: {valuesJson},
      backgroundColor: {colorsJson},
      borderColor: {colorsJson},
      borderWidth: 1
    }}]
  }},
  options: {{
    responsive: true,
    plugins: {{
      title: {{ display: {(string.IsNullOrEmpty(title) ? "false" : "true")}, text: {titleJson} }},
      legend: {{ display: {(chartType == "pie" ? "true" : "false")} }}
    }}
  }}
}}";
    }

    private static string[] GenerateColors(int count, bool isDark)
    {
        // Palette matching Apple design aesthetic
        string[] palette = isDark
            ? new[] { "#0A84FF", "#30D158", "#FF9F0A", "#FF453A", "#BF5AF2", "#64D2FF", "#FFD60A", "#FF6482" }
            : new[] { "#007AFF", "#34C759", "#FF9500", "#FF3B30", "#AF52DE", "#5AC8FA", "#FFCC00", "#FF2D55" };

        var result = new string[count];
        for (int i = 0; i < count; i++)
            result[i] = palette[i % palette.Length];
        return result;
    }

    private static string EscapeForHtml(string s)
    {
        return s.Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;");
    }

    /// <summary>
    /// JavaScript code to capture the rendered content as a base64 PNG data URI.
    /// Call this via WebView ExecuteScriptAsync.
    /// </summary>
    public static string GetCaptureScript(CodeBlockType type)
    {
        if (type == CodeBlockType.Excalidraw)
        {
            return @"
(function() {
    var canvas = document.getElementById('canvas');
    if (!canvas) return '';
    return canvas.toDataURL('image/png');
})()";
        }

        if (type == CodeBlockType.Mermaid)
        {
            return @"
(function() {
    var svg = document.querySelector('.mermaid svg');
    if (!svg) return '';
    var svgData = new XMLSerializer().serializeToString(svg);
    var canvas = document.createElement('canvas');
    var bbox = svg.getBoundingClientRect();
    canvas.width = bbox.width * 2;
    canvas.height = bbox.height * 2;
    var ctx = canvas.getContext('2d');
    ctx.scale(2, 2);
    var img = new Image();
    return new Promise(function(resolve) {
        img.onload = function() {
            ctx.drawImage(img, 0, 0);
            resolve(canvas.toDataURL('image/png'));
        };
        img.src = 'data:image/svg+xml;base64,' + btoa(unescape(encodeURIComponent(svgData)));
    });
})()";
        }
        else
        {
            return @"
(function() {
    if (window._chart) {
        return window._chart.toBase64Image('image/png', 1);
    }
    return '';
})()";
        }
    }
}
