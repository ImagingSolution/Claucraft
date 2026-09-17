using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;

namespace Claucraft.Services;

/// <summary>
/// Turns the links in a note from inert text into something the reader can follow: both
/// <c>[[wikilink]]</c> and the ordinary <c>[title](slug.md)</c> form that MEMORY.md writes its index
/// with. Passed only by callers that render a linked set of notes; everything else leaves it null
/// and gets the old behaviour, where a link is coloured text and nothing more.
/// </summary>
/// <param name="Navigate">Called with the link target when the reader clicks it.</param>
/// <param name="Exists">
/// Whether that target resolves to anything. A link that does not is still drawn and still
/// clickable - it is marked in a different colour rather than hidden, because in Claude's memory
/// format a link to a note that does not exist yet is a deliberate note-to-self.
/// </param>
/// <param name="OpenUrl">
/// Called with an <c>http(s)</c> address the reader clicks. A caller with nowhere to send one
/// leaves it null, and those links stay inert.
/// </param>
public sealed record WikiLinkOptions(Action<string> Navigate, Func<string, bool> Exists,
    Action<string>? OpenUrl = null);

/// <summary>
/// Lightweight Markdown to Avalonia controls parser.
/// Handles the subset of Markdown that Claude commonly uses in responses.
/// </summary>
public static class MarkdownParser
{
    public static List<Control> Parse(string markdown, bool isDark, Typeface? codeTypeface = null,
        double baseFontSize = 13, WikiLinkOptions? wiki = null)
    {
        var controls = new List<Control>();
        if (string.IsNullOrEmpty(markdown)) return controls;

        var fg = isDark ? Color.FromRgb(220, 220, 225) : Color.FromRgb(28, 28, 30);
        var codeBg = isDark ? Color.FromRgb(40, 40, 44) : Color.FromRgb(240, 240, 245);
        var codeFg = isDark ? Color.FromRgb(190, 220, 255) : Color.FromRgb(30, 60, 120);
        var quoteBorder = isDark ? Color.FromRgb(0, 122, 255) : Color.FromRgb(0, 100, 200);
        var linkColor = isDark ? Color.FromRgb(100, 180, 255) : Color.FromRgb(0, 100, 200);
        // A wikilink pointing at a note that has not been written yet.
        var brokenLinkColor = isDark ? Color.FromRgb(230, 150, 110) : Color.FromRgb(175, 85, 20);
        var headingColor = isDark ? Color.FromRgb(240, 240, 245) : Color.FromRgb(20, 20, 24);
        var dimColor = isDark ? Color.FromRgb(140, 140, 145) : Color.FromRgb(88, 88, 96);
        var codeFont = codeTypeface ?? new Typeface("Cascadia Mono, Consolas, Courier New");

        var lines = markdown.Split('\n');
        int i = 0;

        while (i < lines.Length)
        {
            var line = lines[i];

            // Empty line → spacer
            if (string.IsNullOrWhiteSpace(line))
            {
                controls.Add(new Border { Height = 6 });
                i++;
                continue;
            }

            // Heading: # ## ### ####
            var headingMatch = Regex.Match(line, @"^(#{1,4})\s+(.+)$");
            if (headingMatch.Success)
            {
                int level = headingMatch.Groups[1].Value.Length;
                double fontSize = level switch { 1 => baseFontSize * 1.7, 2 => baseFontSize * 1.4, 3 => baseFontSize * 1.2, _ => baseFontSize * 1.1 };
                var fontWeight = level <= 2 ? FontWeight.Bold : FontWeight.SemiBold;

                var tb = new SelectableTextBlock
                {
                    FontSize = fontSize,
                    FontWeight = fontWeight,
                    Foreground = new SolidColorBrush(headingColor),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, level <= 2 ? 10 : 6, 0, 4),
                };
                SetInlineText(tb, headingMatch.Groups[2].Value.Trim(), fg, codeBg, codeFg, linkColor, brokenLinkColor, codeFont, wiki);
                controls.Add(tb);
                i++;
                continue;
            }

            // Code block: ```
            if (line.TrimStart().StartsWith("```"))
            {
                var lang = line.TrimStart().Length > 3 ? line.TrimStart()[3..].Trim() : "";
                var codeLines = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```"))
                {
                    codeLines.Add(lines[i]);
                    i++;
                }
                if (i < lines.Length) i++; // skip closing ```

                var codeText = string.Join("\n", codeLines);
                var codeBlock = CreateCodeBlock(codeText, lang, isDark, codeBg, codeFg, codeFont, baseFontSize);
                controls.Add(codeBlock);
                continue;
            }

            // Blockquote: >
            if (line.TrimStart().StartsWith(">"))
            {
                var quoteLines = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith(">"))
                {
                    var ql = Regex.Replace(lines[i], @"^>\s?", "");
                    quoteLines.Add(ql);
                    i++;
                }
                var quoteText = string.Join("\n", quoteLines);

                var quoteContent = new SelectableTextBlock
                {
                    FontSize = baseFontSize,
                    FontStyle = FontStyle.Italic,
                    Foreground = new SolidColorBrush(dimColor),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(8, 4),
                };
                SetInlineText(quoteContent, quoteText, dimColor, codeBg, codeFg, linkColor, brokenLinkColor, codeFont, wiki);

                var quoteBorderCtrl = new Border
                {
                    BorderBrush = new SolidColorBrush(quoteBorder),
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Background = new SolidColorBrush(isDark ? Color.FromArgb(20, 100, 160, 255) : Color.FromArgb(15, 0, 100, 200)),
                    Padding = new Thickness(8, 4),
                    Margin = new Thickness(0, 4),
                    Child = quoteContent
                };
                controls.Add(quoteBorderCtrl);
                continue;
            }

            // Horizontal rule: --- or ***
            if (Regex.IsMatch(line.Trim(), @"^[-*_]{3,}$"))
            {
                controls.Add(new Border
                {
                    Height = 1,
                    Background = new SolidColorBrush(isDark ? Color.FromRgb(60, 60, 65) : Color.FromRgb(200, 200, 205)),
                    Margin = new Thickness(0, 8),
                });
                i++;
                continue;
            }

            // Unordered list: - or *
            if (Regex.IsMatch(line, @"^\s*[-*+]\s"))
            {
                while (i < lines.Length && Regex.IsMatch(lines[i], @"^\s*[-*+]\s"))
                {
                    var itemText = Regex.Replace(lines[i], @"^\s*[-*+]\s", "");
                    int indent = lines[i].Length - lines[i].TrimStart().Length;

                    var bullet = new TextBlock
                    {
                        Text = "\u2022",
                        Foreground = new SolidColorBrush(isDark ? Color.FromRgb(0, 122, 255) : Color.FromRgb(0, 100, 200)),
                        FontSize = baseFontSize,
                        Margin = new Thickness(indent * 8 + 4, 0, 6, 0),
                        VerticalAlignment = VerticalAlignment.Top,
                    };
                    var itemContent = new TextBlock
                    {
                        FontSize = baseFontSize,
                        Foreground = new SolidColorBrush(fg),
                        TextWrapping = TextWrapping.Wrap,
                        VerticalAlignment = VerticalAlignment.Top,
                    };
                    SetInlineText(itemContent, itemText, fg, codeBg, codeFg, linkColor, brokenLinkColor, codeFont, wiki);

                    // A horizontal StackPanel measures its children with unbounded width, so a
                    // wrapping item would run off the edge. The grid gives the text a real width.
                    var itemPanel = new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                        Margin = new Thickness(0, 1),
                    };
                    Grid.SetColumn(itemContent, 1);
                    itemPanel.Children.Add(bullet);
                    itemPanel.Children.Add(itemContent);
                    controls.Add(itemPanel);
                    i++;
                }
                continue;
            }

            // Ordered list: 1. 2. 3.
            if (Regex.IsMatch(line, @"^\s*\d+\.\s"))
            {
                while (i < lines.Length && Regex.IsMatch(lines[i], @"^\s*\d+\.\s"))
                {
                    var match = Regex.Match(lines[i], @"^\s*(\d+)\.\s(.*)$");
                    if (!match.Success) { i++; continue; }

                    var num = new TextBlock
                    {
                        Text = match.Groups[1].Value + ".",
                        Foreground = new SolidColorBrush(isDark ? Color.FromRgb(0, 122, 255) : Color.FromRgb(0, 100, 200)),
                        FontSize = baseFontSize,
                        Width = 24,
                        TextAlignment = TextAlignment.Right,
                        Margin = new Thickness(4, 0, 6, 0),
                        VerticalAlignment = VerticalAlignment.Top,
                    };
                    var itemContent = new TextBlock
                    {
                        FontSize = baseFontSize,
                        Foreground = new SolidColorBrush(fg),
                        TextWrapping = TextWrapping.Wrap,
                        VerticalAlignment = VerticalAlignment.Top,
                    };
                    SetInlineText(itemContent, match.Groups[2].Value, fg, codeBg, codeFg, linkColor, brokenLinkColor, codeFont, wiki);

                    var itemPanel = new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                        Margin = new Thickness(0, 1),
                    };
                    Grid.SetColumn(itemContent, 1);
                    itemPanel.Children.Add(num);
                    itemPanel.Children.Add(itemContent);
                    controls.Add(itemPanel);
                    i++;
                }
                continue;
            }

            // Table: | ... |
            if (line.TrimStart().StartsWith("|"))
            {
                var tableRows = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith("|"))
                {
                    tableRows.Add(lines[i]);
                    i++;
                }
                var table = CreateTable(tableRows, isDark, fg, codeBg, codeFg, linkColor, brokenLinkColor, codeFont, wiki, baseFontSize);
                if (table != null)
                    controls.Add(table);
                continue;
            }

            // Regular paragraph
            {
                var paraLines = new List<string>();
                while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i])
                    && !lines[i].TrimStart().StartsWith("#")
                    && !lines[i].TrimStart().StartsWith("```")
                    && !lines[i].TrimStart().StartsWith(">")
                    && !Regex.IsMatch(lines[i], @"^\s*[-*+]\s")
                    && !Regex.IsMatch(lines[i], @"^\s*\d+\.\s")
                    && !Regex.IsMatch(lines[i].Trim(), @"^[-*_]{3,}$")
                    && !lines[i].TrimStart().StartsWith("|"))
                {
                    paraLines.Add(lines[i]);
                    i++;
                }

                var paraText = string.Join(" ", paraLines);
                var tb = new SelectableTextBlock
                {
                    FontSize = baseFontSize,
                    Foreground = new SolidColorBrush(fg),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2),
                    LineHeight = 20,
                };
                SetInlineText(tb, paraText, fg, codeBg, codeFg, linkColor, brokenLinkColor, codeFont, wiki);
                controls.Add(tb);
            }
        }

        return controls;
    }

    /// <summary>
    /// Parse inline Markdown formatting (bold, italic, code, links) into TextBlock.Inlines.
    /// </summary>
    private static void SetInlineText(TextBlock tb, string text, Color fg, Color codeBg, Color codeFg,
        Color linkColor, Color brokenLinkColor, Typeface codeFont, WikiLinkOptions? wiki)
    {
        if (string.IsNullOrEmpty(text))
        {
            tb.Text = "";
            return;
        }

        // Simple approach: use regex to find inline patterns and split
        // Patterns: **bold**, *italic*, `code`, [text](url)
        // The wikilink alternative is added only for a caller that can follow one, and it comes
        // first so [[a]] is never mistaken for the [text](url) form. Without it the pattern is
        // character-for-character what it always was, so those callers split exactly as before.
        var pattern = wiki != null
            ? @"(\[\[[^\[\]]+\]\])|(\*\*.*?\*\*)|(\*.*?\*)|(`[^`]+`)|(\[.*?\]\(.*?\))"
            : @"(\*\*.*?\*\*)|(\*.*?\*)|(`[^`]+`)|(\[.*?\]\(.*?\))";
        var parts = Regex.Split(text, pattern);

        bool hasInlines = false;
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part)) continue;

            if (wiki != null && part.Length > 4 &&
                part.StartsWith("[[", StringComparison.Ordinal) && part.EndsWith("]]", StringComparison.Ordinal))
            {
                // Wikilink [[target]] or [[target|shown text]]
                var inner = part[2..^2];
                int bar = inner.IndexOf('|');
                var target = (bar >= 0 ? inner[..bar] : inner).Trim();
                var shown = (bar >= 0 ? inner[(bar + 1)..] : inner).Trim();
                if (target.Length == 0)
                {
                    tb.Inlines!.Add(new Avalonia.Controls.Documents.Run(part) { Foreground = new SolidColorBrush(fg) });
                    hasInlines = true;
                    continue;
                }

                bool resolves = wiki.Exists(target);
                var navigate = wiki.Navigate;
                AddLink(tb, shown, resolves ? linkColor : brokenLinkColor,
                    resolves ? target : $"{target} — {Loc.Get("MemoryNoteMissing")}",
                    () => navigate(target));
                hasInlines = true;
            }
            else if (Regex.IsMatch(part, @"^\*\*.*\*\*$"))
            {
                // Bold
                var content = part[2..^2];
                tb.Inlines!.Add(new Avalonia.Controls.Documents.Run(content)
                {
                    FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(fg),
                });
                hasInlines = true;
            }
            else if (Regex.IsMatch(part, @"^\*.*\*$") && !part.StartsWith("**"))
            {
                // Italic
                var content = part[1..^1];
                tb.Inlines!.Add(new Avalonia.Controls.Documents.Run(content)
                {
                    FontStyle = FontStyle.Italic,
                    Foreground = new SolidColorBrush(fg),
                });
                hasInlines = true;
            }
            else if (Regex.IsMatch(part, @"^`[^`]+`$"))
            {
                // Inline code
                var content = part[1..^1];
                tb.Inlines!.Add(new Avalonia.Controls.Documents.Run(content)
                {
                    FontFamily = new FontFamily(codeFont.FontFamily.Name),
                    Foreground = new SolidColorBrush(codeFg),
                    // Note: Run doesn't support Background directly in Avalonia
                });
                hasInlines = true;
            }
            else if (Regex.IsMatch(part, @"^\[.*?\]\(.*?\)$"))
            {
                // Link [text](url)
                var linkMatch = Regex.Match(part, @"^\[(.*?)\]\((.*?)\)$");
                if (linkMatch.Success)
                {
                    var shown = linkMatch.Groups[1].Value;
                    var target = linkMatch.Groups[2].Value.Trim();
                    var slug = wiki != null && shown.Length > 0 ? NoteTargetOf(target) : null;

                    if (slug != null)
                    {
                        // A note beside this one, written as an ordinary link rather than a
                        // wikilink. It follows the same way, and is marked the same way when the
                        // note it names has not been written.
                        bool resolves = wiki!.Exists(slug);
                        var navigate = wiki.Navigate;
                        AddLink(tb, shown, resolves ? linkColor : brokenLinkColor,
                            resolves ? target : $"{target} — {Loc.Get("MemoryNoteMissing")}",
                            () => navigate(slug));
                    }
                    else if (shown.Length > 0 && wiki?.OpenUrl != null && IsWebUrl(target))
                    {
                        var open = wiki.OpenUrl;
                        AddLink(tb, shown, linkColor, target, () => open(target));
                    }
                    else
                    {
                        tb.Inlines!.Add(new Avalonia.Controls.Documents.Run(shown)
                        {
                            Foreground = new SolidColorBrush(linkColor),
                            TextDecorations = TextDecorations.Underline,
                        });
                    }
                    hasInlines = true;
                }
            }
            else
            {
                // Plain text
                if (hasInlines || parts.Length > 1)
                {
                    tb.Inlines!.Add(new Avalonia.Controls.Documents.Run(part)
                    {
                        Foreground = new SolidColorBrush(fg),
                    });
                    hasInlines = true;
                }
                else
                {
                    tb.Text = text;
                    return;
                }
            }
        }

        if (!hasInlines)
            tb.Text = text;
    }

    /// <summary>
    /// Adds a clickable link to a paragraph. An <c>Inline</c> cannot take pointer events, so the
    /// clickable part is a control the text flows around rather than a Run.
    /// </summary>
    private static void AddLink(TextBlock tb, string shown, Color color, string? tip, Action onClick)
    {
        var link = new TextBlock
        {
            Text = shown,
            Foreground = new SolidColorBrush(color),
            TextDecorations = TextDecorations.Underline,
            FontSize = tb.FontSize,
            FontWeight = tb.FontWeight,
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (tip != null) ToolTip.SetTip(link, tip);

        link.PointerPressed += (_, e) =>
        {
            onClick();
            e.Handled = true;
        };

        tb.Inlines!.Add(new Avalonia.Controls.Documents.InlineUIContainer(link));
    }

    /// <summary>
    /// The note a Markdown link names, or null if it points anywhere else. MEMORY.md writes its
    /// index as <c>[title](slug.md)</c> rather than as wikilinks, so those rows have to reach the
    /// note beside them the same way. A target carrying a directory, a scheme or another extension
    /// leads out of the memory folder and is left as plain coloured text.
    /// </summary>
    private static string? NoteTargetOf(string target)
    {
        var t = target;
        int hash = t.IndexOf('#');
        if (hash >= 0) t = t[..hash];
        if (t.StartsWith("./", StringComparison.Ordinal)) t = t[2..];
        if (t.Length == 0 || t.IndexOfAny(Pathish) >= 0) return null;
        return t.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? t[..^3] : null;
    }

    /// <summary>Characters that mean a link target is a path or a URL, not a note beside this one.</summary>
    private static readonly char[] Pathish = { '/', '\\', ':' };

    private static bool IsWebUrl(string target) =>
        target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        target.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static Border CreateCodeBlock(string code, string language, bool isDark,
        Color codeBg, Color codeFg, Typeface codeFont, double baseFontSize = 13)
    {
        var headerPanel = new DockPanel
        {
            Margin = new Thickness(0, 0, 0, 0),
        };

        if (!string.IsNullOrEmpty(language))
        {
            var langLabel = new TextBlock
            {
                Text = language,
                FontSize = 11,
                Foreground = new SolidColorBrush(isDark ? Color.FromRgb(140, 140, 145) : Color.FromRgb(84, 84, 92)),
                Margin = new Thickness(10, 4, 0, 0),
            };
            DockPanel.SetDock(langLabel, Avalonia.Controls.Dock.Left);
            headerPanel.Children.Add(langLabel);
        }

        var copyBtn = new Button
        {
            Content = Loc.Get("CopyCode", "Copy"),
            FontSize = 10,
            Padding = new Thickness(6, 2),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(isDark ? Color.FromRgb(140, 140, 145) : Color.FromRgb(84, 84, 92)),
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 2, 6, 0),
        };
        var codeForCopy = code; // capture for closure
        copyBtn.Click += async (_, _) =>
        {
            var topLevel = TopLevel.GetTopLevel(copyBtn);
            if (topLevel?.Clipboard != null)
                await topLevel.Clipboard.SetTextAsync(codeForCopy);
            copyBtn.Content = "\u2713"; // checkmark
            await System.Threading.Tasks.Task.Delay(1500);
            copyBtn.Content = Loc.Get("CopyCode", "Copy");
        };
        DockPanel.SetDock(copyBtn, Avalonia.Controls.Dock.Right);
        headerPanel.Children.Add(copyBtn);
        headerPanel.Children.Add(new Border()); // filler

        var codeTextBlock = new SelectableTextBlock
        {
            Text = code,
            FontSize = baseFontSize * 0.92,
            FontFamily = new FontFamily(codeFont.FontFamily.Name),
            Foreground = new SolidColorBrush(codeFg),
            Margin = new Thickness(10, 2, 10, 8),
            TextWrapping = TextWrapping.Wrap,
        };

        var codeStack = new StackPanel();
        codeStack.Children.Add(headerPanel);
        codeStack.Children.Add(codeTextBlock);

        return new Border
        {
            Background = new SolidColorBrush(codeBg),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 4),
            Child = codeStack,
        };
    }

    private static Control? CreateTable(List<string> rows, bool isDark, Color fg, Color codeBg, Color codeFg, Color linkColor, Color brokenLinkColor, Typeface codeFont, WikiLinkOptions? wiki, double baseFontSize = 13)
    {
        if (rows.Count < 2) return null;

        var parsedRows = new List<string[]>();
        foreach (var row in rows)
        {
            var cells = row.Trim().Trim('|').Split('|');
            for (int j = 0; j < cells.Length; j++)
                cells[j] = cells[j].Trim();
            // Skip separator rows (--- | --- | ---)
            if (cells.Length > 0 && Regex.IsMatch(cells[0], @"^[-:]+$"))
                continue;
            parsedRows.Add(cells);
        }

        if (parsedRows.Count == 0) return null;
        int colCount = parsedRows[0].Length;

        var grid = new Grid
        {
            Margin = new Thickness(0, 4),
        };

        for (int c = 0; c < colCount; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        for (int r = 0; r < parsedRows.Count; r++)
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        for (int r = 0; r < parsedRows.Count; r++)
        {
            for (int c = 0; c < Math.Min(parsedRows[r].Length, colCount); c++)
            {
                var cellBorder = new Border
                {
                    BorderBrush = new SolidColorBrush(isDark ? Color.FromRgb(60, 60, 65) : Color.FromRgb(200, 200, 205)),
                    BorderThickness = new Thickness(0.5),
                    Background = r == 0
                        ? new SolidColorBrush(codeBg)
                        : Brushes.Transparent,
                    Padding = new Thickness(6, 3),
                };
                var cellText = new SelectableTextBlock
                {
                    FontSize = baseFontSize * 0.92,
                    FontWeight = r == 0 ? FontWeight.SemiBold : FontWeight.Normal,
                    Foreground = new SolidColorBrush(fg),
                    TextWrapping = TextWrapping.Wrap,
                };
                SetInlineText(cellText, parsedRows[r][c], fg, codeBg, codeFg, linkColor, brokenLinkColor, codeFont, wiki);
                cellBorder.Child = cellText;
                Grid.SetRow(cellBorder, r);
                Grid.SetColumn(cellBorder, c);
                grid.Children.Add(cellBorder);
            }
        }

        return grid;
    }
}
