using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Claucraft.Terminal;

public enum CodeBlockType
{
    Unknown,
    Mermaid,
    BarChart,
    LineChart,
    PieChart,
    Excalidraw
}

public record CodeBlockInfo(
    int StartAbsRow,
    int EndAbsRow,
    string Language,
    string Content,
    CodeBlockType Type
);

/// <summary>
/// Detects renderable content from terminal buffer:
/// - Markdown code blocks (```mermaid, ```chart)
/// - Excalidraw MCP tool calls (create_view with elements JSON)
/// </summary>
public class CodeBlockDetector
{
    private static readonly Regex FenceOpenRegex = new(@"^\s*```(\w+)\s*$", RegexOptions.Compiled);
    private static readonly Regex FenceCloseRegex = new(@"^\s*```\s*$", RegexOptions.Compiled);

    // Matches REAL Excalidraw MCP tool call lines only.
    // Must start with ● (Claude Code tool call marker) to avoid false positives
    // from source code, debug logs, and comments.
    private static readonly Regex ExcalidrawToolRegex = new(
        @"^[\s●]*claude\.ai\s+Excalidraw.*create_view.*\(MCP\).*elements:",
        RegexOptions.Compiled);

    // Matches the checkpointId response that follows Excalidraw tool output
    // e.g. '⎿  { "checkpointId": "..." }'
    private static readonly Regex CheckpointIdRegex = new(
        @"""checkpointId""",
        RegexOptions.Compiled);

    private static readonly HashSet<string> RenderableLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "mermaid", "chart"
    };

    private readonly List<CodeBlockInfo> _detectedBlocks = new();
    private int _lastScannedRow;
    private int _pendingExcalidrawRow = -1; // Row where an incomplete Excalidraw block was found

    public IReadOnlyList<CodeBlockInfo> DetectedBlocks => _detectedBlocks;

    /// <summary>
    /// Full scan of the buffer for code blocks and MCP tool outputs.
    /// </summary>
    public void Scan(TerminalBuffer buffer)
    {
        _detectedBlocks.Clear();
        _lastScannedRow = 0;
        _pendingExcalidrawRow = -1;

        int totalRows = buffer.Scrollback.Count + buffer.Rows;
        ScanRange(buffer, 0, totalRows);
        _lastScannedRow = totalRows;

        System.Diagnostics.Debug.WriteLine($"[CodeBlockDetector] Scan complete: {totalRows} rows, {_detectedBlocks.Count} blocks found");
        foreach (var block in _detectedBlocks)
            System.Diagnostics.Debug.WriteLine($"  Block: {block.Type} rows {block.StartAbsRow}-{block.EndAbsRow} content[{block.Content.Length}chars]");
    }

    /// <summary>
    /// Incremental scan: re-scans from the earliest pending point.
    /// Always does a full rescan to avoid missing multi-line blocks
    /// that arrive incrementally (especially Excalidraw MCP tool calls).
    /// </summary>
    public void IncrementalScan(TerminalBuffer buffer)
    {
        // Always do a full rescan - the cost is low (10K rows max)
        // and it avoids bugs with incremental detection of multi-line blocks
        Scan(buffer);
    }

    private void ScanRange(TerminalBuffer buffer, int startRow, int endRow)
    {
        int openRow = -1;
        string openLang = "";

        for (int absRow = startRow; absRow < endRow; absRow++)
        {
            string line = GetRowText(buffer, absRow).TrimEnd();

            // === Excalidraw MCP tool detection ===
            if (ExcalidrawToolRegex.IsMatch(line))
            {
                System.Diagnostics.Debug.WriteLine($"[CodeBlockDetector] Excalidraw candidate at row {absRow}: {line.Substring(0, Math.Min(80, line.Length))}...");
                var excalidrawResult = TryExtractExcalidraw(buffer, absRow, endRow);
                if (excalidrawResult != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[CodeBlockDetector] Excalidraw block extracted: rows {excalidrawResult.StartAbsRow}-{excalidrawResult.EndAbsRow}, content length={excalidrawResult.Content.Length}");
                    _detectedBlocks.Add(excalidrawResult);
                    absRow = excalidrawResult.EndAbsRow;
                    continue;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[CodeBlockDetector] Excalidraw extraction FAILED at row {absRow}");
                }
            }

            // === Markdown fence detection ===
            if (openRow < 0)
            {
                var match = FenceOpenRegex.Match(line);
                if (match.Success)
                {
                    string lang = match.Groups[1].Value;
                    if (RenderableLanguages.Contains(lang))
                    {
                        openRow = absRow;
                        openLang = lang;
                    }
                }
            }
            else
            {
                if (FenceCloseRegex.IsMatch(line))
                {
                    string content = ExtractContent(buffer, openRow + 1, absRow);
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        var type = DetermineType(openLang, content);
                        _detectedBlocks.Add(new CodeBlockInfo(
                            openRow, absRow, openLang, content, type));
                    }
                    openRow = -1;
                    openLang = "";
                }
            }
        }
    }

    /// <summary>
    /// Try to extract Excalidraw elements from an MCP tool call.
    /// The terminal output looks like:
    ///   ● claude.ai Excalidraw - create_view (MCP)(elements: "[{...}]")
    ///   ⎿  { "checkpointId": "..." }
    /// The elements JSON may span many lines between the tool call and the response.
    /// </summary>
    private CodeBlockInfo? TryExtractExcalidraw(TerminalBuffer buffer, int toolCallRow, int endRow)
    {
        // Collect all text from the tool call row onward to find the elements JSON.
        // The elements JSON may span hundreds of terminal rows due to line wrapping.
        var sb = new StringBuilder();
        int lastRow = toolCallRow;
        bool foundCheckpoint = false;

        for (int row = toolCallRow; row < endRow && row < toolCallRow + 500; row++)
        {
            string line = GetRowText(buffer, row).TrimEnd();
            // For continuation rows, trim leading spaces to avoid
            // Claude Code's visual indentation breaking JSON (e.g., splitting \" across rows)
            if (row > toolCallRow)
                line = line.TrimStart();
            sb.Append(line);
            lastRow = row;

            // Check for end markers:
            // 1. checkpointId response
            // 2. ⎿ response marker followed by closing brace (several rows after the JSON)
            if (row > toolCallRow + 5)
            {
                if (CheckpointIdRegex.IsMatch(line))
                {
                    foundCheckpoint = true;
                    // Include a few more rows to capture the closing brace
                    for (int r2 = row + 1; r2 < endRow && r2 <= row + 3; r2++)
                    {
                        string extra = GetRowText(buffer, r2).TrimEnd();
                        sb.Append(extra);
                        lastRow = r2;
                        if (extra.Contains("}")) break;
                    }
                    break;
                }
            }
        }

        string fullText = sb.ToString();

        // Extract the JSON array from elements: "[...]"
        string? elementsJson = ExtractElementsJson(fullText);
        if (elementsJson == null)
            return null;

        return new CodeBlockInfo(
            toolCallRow, lastRow, "excalidraw", elementsJson, CodeBlockType.Excalidraw);
    }

    /// <summary>
    /// Extract the elements JSON array from the MCP tool call text.
    /// Handles the format: elements: "[{...}]"
    /// The JSON is wrapped in quotes and may contain escaped characters.
    /// </summary>
    private static string? ExtractElementsJson(string text)
    {
        // Find 'elements:' in the text
        int elemIdx = text.IndexOf("elements:", StringComparison.Ordinal);
        if (elemIdx < 0)
        {
            System.Diagnostics.Debug.WriteLine($"[ExtractElementsJson] 'elements:' not found in text of length {text.Length}");
            return null;
        }

        // Find the opening quote of the string value: elements: "..."
        int pos = elemIdx + "elements:".Length;
        while (pos < text.Length && text[pos] == ' ')
            pos++;

        if (pos >= text.Length || text[pos] != '"')
        {
            System.Diagnostics.Debug.WriteLine($"[ExtractElementsJson] No opening quote found after 'elements:'");
            return null;
        }

        int stringStart = pos + 1; // character after the opening quote

        // Find the closing quote of the elements string value.
        // The elements value is: "[...JSON array...]\n]")
        // We need to find ]") or ]"\n) which marks the real end.
        // Strategy: find all unescaped " positions, then pick the one after the last ]
        bool escape = false;
        int stringEnd = -1;

        // Find the pattern ]" followed by ) which marks the end of elements: "...]")
        // Or find the last occurrence of ]\n" pattern
        // We search backwards from the end for ]" pattern
        {
            // Method: find ]" in the raw text (before unescaping)
            // The ] before " is the last bracket of the JSON array
            // Search from end to avoid premature matching
            int searchEnd = Math.Min(text.Length, stringStart + 200000);
            int candidate = -1;

            for (int i = searchEnd - 1; i > stringStart; i--)
            {
                if (text[i] == '"' && i > 0)
                {
                    // Check if this " is preceded by ] (possibly with \n between)
                    int j = i - 1;
                    // Skip whitespace and \n between ] and "
                    while (j > stringStart && (text[j] == ' ' || text[j] == '\n' || text[j] == '\r'))
                        j--;
                    // Check for escaped \n: the character before " might be 'n' preceded by '\'
                    if (j > stringStart && text[j] == 'n' && j > 0 && text[j - 1] == '\\')
                        j -= 2;
                    while (j > stringStart && (text[j] == ' '))
                        j--;
                    if (j >= stringStart && text[j] == ']')
                    {
                        candidate = i;
                        break;
                    }
                }
            }

            if (candidate > 0)
            {
                stringEnd = candidate;
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[ExtractElementsJson] No ]\" pattern found. String start={stringStart}, text length={text.Length}");
                return null;
            }
        }

        // Extract the string content between quotes
        string rawContent = text.Substring(stringStart, stringEnd - stringStart);
        System.Diagnostics.Debug.WriteLine($"[ExtractElementsJson] Raw string content length: {rawContent.Length}");

        // Unescape the string: \" → ", \n → newline, \\ → \, \t → tab
        var sb = new StringBuilder(rawContent.Length);
        bool esc = false;
        for (int i = 0; i < rawContent.Length; i++)
        {
            char c = rawContent[i];
            if (esc)
            {
                esc = false;
                switch (c)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    default: sb.Append('\\'); sb.Append(c); break;
                }
                continue;
            }
            if (c == '\\') { esc = true; continue; }
            sb.Append(c);
        }
        string json = sb.ToString().Trim();

        // Validate: should start with [ and end with ], and have meaningful content
        if (!json.StartsWith("[") || !json.EndsWith("]") || json.Length < 50)
        {
            System.Diagnostics.Debug.WriteLine($"[ExtractElementsJson] Invalid JSON: doesn't start/end with []. First char: '{(json.Length > 0 ? json[0] : '?')}', last char: '{(json.Length > 0 ? json[^1] : '?')}'");
            return null;
        }

        System.Diagnostics.Debug.WriteLine($"[ExtractElementsJson] JSON extracted: length={json.Length}, first 100 chars: {json.Substring(0, Math.Min(100, json.Length))}");
        return json;
    }

    private static string ExtractContent(TerminalBuffer buffer, int startRow, int endRow)
    {
        var sb = new StringBuilder();
        for (int row = startRow; row < endRow; row++)
        {
            if (sb.Length > 0)
                sb.AppendLine();
            sb.Append(GetRowText(buffer, row).TrimEnd());
        }
        return sb.ToString();
    }

    private static string GetRowText(TerminalBuffer buffer, int absRow)
    {
        var sb = new StringBuilder();
        int scrollbackCount = buffer.Scrollback.Count;
        for (int col = 0; col < buffer.Cols; col++)
        {
            TerminalCell cell;
            if (absRow < scrollbackCount)
            {
                var line = buffer.GetScrollbackLine(absRow);
                cell = (line != null && col < line.Length) ? line[col] : TerminalCell.Empty;
            }
            else
            {
                cell = buffer.GetCell(absRow - scrollbackCount, col);
            }
            if (cell.Attributes.HasFlag(CellAttributes.WideCharTrail)) continue;
            sb.Append(cell.Character == '\0' ? ' ' : cell.Character);
        }
        return sb.ToString();
    }

    private static CodeBlockType DetermineType(string language, string content)
    {
        if (string.Equals(language, "mermaid", StringComparison.OrdinalIgnoreCase))
            return CodeBlockType.Mermaid;

        if (string.Equals(language, "chart", StringComparison.OrdinalIgnoreCase))
            return DetermineChartType(content);

        return CodeBlockType.Unknown;
    }

    private static CodeBlockType DetermineChartType(string content)
    {
        var lower = content.ToLowerInvariant();

        if (lower.Contains("\"type\"") || lower.Contains("type:"))
        {
            if (lower.Contains("\"pie\"") || lower.Contains("type: pie") || lower.Contains("type:pie"))
                return CodeBlockType.PieChart;
            if (lower.Contains("\"line\"") || lower.Contains("type: line") || lower.Contains("type:line"))
                return CodeBlockType.LineChart;
            if (lower.Contains("\"bar\"") || lower.Contains("type: bar") || lower.Contains("type:bar"))
                return CodeBlockType.BarChart;
        }

        return CodeBlockType.BarChart;
    }

    /// <summary>
    /// Find code blocks that overlap with the visible viewport.
    /// </summary>
    public List<CodeBlockInfo> GetVisibleBlocks(int viewStartAbsRow, int viewEndAbsRow)
    {
        var visible = new List<CodeBlockInfo>();
        foreach (var block in _detectedBlocks)
        {
            if (block.EndAbsRow >= viewStartAbsRow && block.StartAbsRow <= viewEndAbsRow)
                visible.Add(block);
        }
        return visible;
    }
}
