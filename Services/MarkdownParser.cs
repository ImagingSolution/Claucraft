using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Claucraft.Services;

/// <summary>
/// Lightweight Markdown to Avalonia controls parser.
/// Handles the subset of Markdown that Claude commonly uses in responses.
/// </summary>
public static class MarkdownParser
{
    public static List<Control> Parse(string markdown, bool isDark, Typeface? codeTypeface = null, double baseFontSize = 13)
    {
        var controls = new List<Control>();
        if (string.IsNullOrEmpty(markdown)) return controls;

        var fg = isDark ? Color.FromRgb(220, 220, 225) : Color.FromRgb(28, 28, 30);
        var codeBg = isDark ? Color.FromRgb(40, 40, 44) : Color.FromRgb(240, 240, 245);
        var codeFg = isDark ? Color.FromRgb(190, 220, 255) : Color.FromRgb(30, 60, 120);
        var quoteBorder = isDark ? Color.FromRgb(0, 122, 255) : Color.FromRgb(0, 100, 200);
        var linkColor = isDark ? Color.FromRgb(100, 180, 255) : Color.FromRgb(0, 100, 200);
        var headingColor = isDark ? Color.FromRgb(240, 240, 245) : Color.FromRgb(20, 20, 24);
        var dimColor = isDark ? Color.FromRgb(140, 140, 145) : Color.FromRgb(120, 120, 125);
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
                SetInlineText(tb, headingMatch.Groups[2].Value.Trim(), fg, codeBg, codeFg, linkColor, codeFont);
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
                SetInlineText(quoteContent, quoteText, dimColor, codeBg, codeFg, linkColor, codeFont);

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
                    SetInlineText(itemContent, itemText, fg, codeBg, codeFg, linkColor, codeFont);

                    var itemPanel = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Margin = new Thickness(0, 1),
                    };
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
                    SetInlineText(itemContent, match.Groups[2].Value, fg, codeBg, codeFg, linkColor, codeFont);

                    var itemPanel = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Margin = new Thickness(0, 1),
                    };
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
                var table = CreateTable(tableRows, isDark, fg, codeBg, codeFg, linkColor, codeFont, baseFontSize);
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
                SetInlineText(tb, paraText, fg, codeBg, codeFg, linkColor, codeFont);
                controls.Add(tb);
            }
        }

        return controls;
    }

    /// <summary>
    /// Parse inline Markdown formatting (bold, italic, code, links) into TextBlock.Inlines.
    /// </summary>
    private static void SetInlineText(TextBlock tb, string text, Color fg, Color codeBg, Color codeFg, Color linkColor, Typeface codeFont)
    {
        if (string.IsNullOrEmpty(text))
        {
            tb.Text = "";
            return;
        }

        // Simple approach: use regex to find inline patterns and split
        // Patterns: **bold**, *italic*, `code`, [text](url)
        var pattern = @"(\*\*.*?\*\*)|(\*.*?\*)|(`[^`]+`)|(\[.*?\]\(.*?\))";
        var parts = Regex.Split(text, pattern);

        bool hasInlines = false;
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part)) continue;

            if (Regex.IsMatch(part, @"^\*\*.*\*\*$"))
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
                    tb.Inlines!.Add(new Avalonia.Controls.Documents.Run(linkMatch.Groups[1].Value)
                    {
                        Foreground = new SolidColorBrush(linkColor),
                        TextDecorations = TextDecorations.Underline,
                    });
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
                Foreground = new SolidColorBrush(isDark ? Color.FromRgb(140, 140, 145) : Color.FromRgb(100, 100, 105)),
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
            Foreground = new SolidColorBrush(isDark ? Color.FromRgb(140, 140, 145) : Color.FromRgb(100, 100, 105)),
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

    private static Control? CreateTable(List<string> rows, bool isDark, Color fg, Color codeBg, Color codeFg, Color linkColor, Typeface codeFont, double baseFontSize = 13)
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
                SetInlineText(cellText, parsedRows[r][c], fg, codeBg, codeFg, linkColor, codeFont);
                cellBorder.Child = cellText;
                Grid.SetRow(cellBorder, r);
                Grid.SetColumn(cellBorder, c);
                grid.Children.Add(cellBorder);
            }
        }

        return grid;
    }
}
