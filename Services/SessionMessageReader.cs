using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Claucraft.Services;

public enum MessageRole { User, Assistant, System }

public record ConversationMessage(
    MessageRole Role,
    string Text,
    DateTime? Timestamp,
    string? ToolName,
    bool IsToolUse,
    bool IsThinking
);

/// <summary>
/// Reads Claude Code session JSONL files and converts them into structured conversation messages.
/// </summary>
public static class SessionMessageReader
{
    /// <summary>
    /// Read all conversation messages from a JSONL session file.
    /// </summary>
    public static List<ConversationMessage> ReadSession(string jsonlPath)
    {
        var messages = new List<ConversationMessage>();
        if (!File.Exists(jsonlPath)) return messages;

        try
        {
            using var stream = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            while (!reader.EndOfStream)
            {
                var line = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(line)) continue;

                var msg = ParseLine(line);
                if (msg != null)
                    messages.Add(msg);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SessionMessageReader] ReadSession error: {ex.Message}");
        }

        return ConsolidateMessages(messages);
    }

    /// <summary>
    /// Consolidate consecutive assistant messages into a single message per response.
    /// Also merges consecutive progress/tool messages between user messages.
    /// </summary>
    private static List<ConversationMessage> ConsolidateMessages(List<ConversationMessage> messages)
    {
        var result = new List<ConversationMessage>();
        int i = 0;
        while (i < messages.Count)
        {
            var msg = messages[i];

            // User messages: keep as-is
            if (msg.Role == MessageRole.User)
            {
                result.Add(msg);
                i++;
                continue;
            }

            // Consolidate consecutive assistant text messages into one
            if (msg.Role == MessageRole.Assistant && !msg.IsToolUse && !msg.IsThinking)
            {
                var textParts = new List<string> { msg.Text };
                var timestamp = msg.Timestamp;
                i++;

                while (i < messages.Count && messages[i].Role == MessageRole.Assistant
                    && !messages[i].IsThinking)
                {
                    if (messages[i].IsToolUse && !messages[i].Text.Contains('\n'))
                    {
                        // Skip compact tool use markers within a response
                        i++;
                        continue;
                    }
                    if (!string.IsNullOrWhiteSpace(messages[i].Text) && !messages[i].IsToolUse)
                        textParts.Add(messages[i].Text);
                    i++;
                }

                var consolidated = string.Join("\n\n", textParts);
                result.Add(new ConversationMessage(MessageRole.Assistant, consolidated, timestamp, null, false, false));
                continue;
            }

            // Skip thinking blocks and tool-use-only messages (they'll show as part of progress)
            if (msg.IsThinking || (msg.IsToolUse && !msg.Text.Contains('\n')))
            {
                i++;
                continue;
            }

            // System/progress: keep but skip consecutive duplicates
            if (msg.Role == MessageRole.System)
            {
                result.Add(msg);
                i++;
                continue;
            }

            result.Add(msg);
            i++;
        }
        return result;
    }

    /// <summary>
    /// Read only new messages since lastLineCount (for polling).
    /// </summary>
    public static List<ConversationMessage> ReadNewMessages(string jsonlPath, ref int lastLineCount)
    {
        var newMessages = new List<ConversationMessage>();
        if (!File.Exists(jsonlPath)) return newMessages;

        try
        {
            using var stream = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            int currentLine = 0;
            while (!reader.EndOfStream)
            {
                var line = reader.ReadLine();
                currentLine++;

                if (currentLine <= lastLineCount) continue;
                if (string.IsNullOrWhiteSpace(line)) continue;

                var msg = ParseLine(line);
                if (msg != null)
                    newMessages.Add(msg);
            }
            lastLineCount = currentLine;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SessionMessageReader] ReadNewMessages error: {ex.Message}");
        }

        return newMessages;
    }

    /// <summary>
    /// Find the most recently modified JSONL file for a project folder.
    /// </summary>
    public static string? FindMostRecentSession(string projectFolder)
    {
        if (string.IsNullOrEmpty(projectFolder)) return null;

        try
        {
            var baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude", "projects");

            if (!Directory.Exists(baseDir)) return null;

            var normalized = NormalizeFolderName(projectFolder);

            foreach (var dir in Directory.GetDirectories(baseDir))
            {
                var dirName = Path.GetFileName(dir);
                var normalizedDir = NormalizeFolderName(dirName);
                if (normalizedDir.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                {
                    string? mostRecent = null;
                    DateTime mostRecentTime = DateTime.MinValue;

                    foreach (var file in Directory.GetFiles(dir, "*.jsonl"))
                    {
                        var lastWrite = File.GetLastWriteTime(file);
                        if (lastWrite > mostRecentTime)
                        {
                            mostRecentTime = lastWrite;
                            mostRecent = file;
                        }
                    }
                    return mostRecent;
                }
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Find JSONL file by session ID.
    /// </summary>
    public static string? FindSessionFile(string projectFolder, string sessionId)
    {
        if (string.IsNullOrEmpty(projectFolder) || string.IsNullOrEmpty(sessionId)) return null;

        try
        {
            var baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude", "projects");

            if (!Directory.Exists(baseDir)) return null;

            var normalized = NormalizeFolderName(projectFolder);

            foreach (var dir in Directory.GetDirectories(baseDir))
            {
                var dirName = Path.GetFileName(dir);
                var normalizedDir = NormalizeFolderName(dirName);
                if (normalizedDir.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                {
                    var filePath = Path.Combine(dir, $"{sessionId}.jsonl");
                    return File.Exists(filePath) ? filePath : null;
                }
            }
        }
        catch { }

        return null;
    }

    private static ConversationMessage? ParseLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            var type = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;

            // Skip non-message types
            if (type is "file-history-snapshot" or "custom-title" or "agent-name" or "last-prompt" or "progress")
                return null;

            // Skip metadata messages
            if (root.TryGetProperty("isMeta", out var metaProp) && metaProp.ValueKind == JsonValueKind.True)
                return null;

            // Parse timestamp
            DateTime? timestamp = null;
            if (root.TryGetProperty("timestamp", out var tsProp) && tsProp.GetString() is string tsStr)
            {
                if (DateTime.TryParse(tsStr, out var dt))
                    timestamp = dt;
            }

            if (type == "user")
            {
                return ParseUserMessage(root, timestamp);
            }
            else if (type == "assistant")
            {
                return ParseAssistantMessage(root, timestamp);
            }
            else if (type == "progress")
            {
                return ParseProgressMessage(root, timestamp);
            }
            else if (type == "system")
            {
                return null; // Skip system messages
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static ConversationMessage? ParseUserMessage(JsonElement root, DateTime? timestamp)
    {
        if (!root.TryGetProperty("message", out var msgProp)) return null;

        string? text = null;

        if (msgProp.ValueKind == JsonValueKind.String)
        {
            text = msgProp.GetString();
        }
        else if (msgProp.ValueKind == JsonValueKind.Object && msgProp.TryGetProperty("content", out var contentProp))
        {
            text = ExtractAllTextContent(contentProp, skipToolResults: true);
        }

        if (string.IsNullOrWhiteSpace(text)) return null;

        // Clean up metadata tags
        text = CleanMetadataTags(text);
        if (string.IsNullOrWhiteSpace(text)) return null;

        return new ConversationMessage(MessageRole.User, text, timestamp, null, false, false);
    }

    private static ConversationMessage? ParseAssistantMessage(JsonElement root, DateTime? timestamp)
    {
        if (!root.TryGetProperty("message", out var msgProp)) return null;
        if (!msgProp.TryGetProperty("content", out var contentProp)) return null;
        if (contentProp.ValueKind != JsonValueKind.Array) return null;

        var textParts = new List<string>();
        string? toolName = null;
        bool isToolUse = false;
        bool isThinking = false;

        foreach (var item in contentProp.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrEmpty(s)) textParts.Add(s);
                continue;
            }

            if (!item.TryGetProperty("type", out var itemType)) continue;
            var itemTypeStr = itemType.GetString();

            if (itemTypeStr == "text")
            {
                if (item.TryGetProperty("text", out var textEl))
                {
                    var t = textEl.GetString();
                    if (!string.IsNullOrEmpty(t))
                        textParts.Add(t);
                }
            }
            else if (itemTypeStr == "thinking")
            {
                isThinking = true;
                if (item.TryGetProperty("thinking", out var thinkEl))
                {
                    var t = thinkEl.GetString();
                    if (!string.IsNullOrEmpty(t))
                        textParts.Add(t);
                }
            }
            else if (itemTypeStr == "tool_use")
            {
                isToolUse = true;
                if (item.TryGetProperty("name", out var nameEl))
                    toolName = nameEl.GetString();
            }
        }

        // If only thinking content, mark as thinking
        if (textParts.Count == 0 && !isToolUse) return null;

        // For tool use without text, create a compact message
        if (textParts.Count == 0 && isToolUse)
        {
            return new ConversationMessage(MessageRole.Assistant, $"[Tool: {toolName}]", timestamp, toolName, true, false);
        }

        var fullText = string.Join("\n", textParts);
        return new ConversationMessage(
            MessageRole.Assistant, fullText, timestamp, toolName, isToolUse, isThinking);
    }

    private static ConversationMessage? ParseProgressMessage(JsonElement root, DateTime? timestamp)
    {
        // Progress entries have: type="progress", data={...}, toolUseID, parentToolUseID
        if (!root.TryGetProperty("data", out var dataProp)) return null;

        string progressText = "";
        if (dataProp.ValueKind == JsonValueKind.Object)
        {
            // data may contain tool name, status, content etc.
            if (dataProp.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == JsonValueKind.String)
                progressText = contentProp.GetString() ?? "";
            else if (dataProp.TryGetProperty("toolName", out var tn))
                progressText = $"● {tn.GetString()}";
        }
        else if (dataProp.ValueKind == JsonValueKind.String)
        {
            progressText = dataProp.GetString() ?? "";
        }

        if (string.IsNullOrWhiteSpace(progressText)) return null;

        // Truncate very long progress messages
        if (progressText.Length > 200)
            progressText = progressText[..200] + "...";

        return new ConversationMessage(MessageRole.System, progressText, timestamp, null, true, false);
    }

    private static string? ExtractAllTextContent(JsonElement element, bool skipToolResults = false)
    {
        if (element.ValueKind == JsonValueKind.String)
            return element.GetString();

        if (element.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var s = item.GetString();
                    if (!string.IsNullOrEmpty(s)) parts.Add(s);
                    continue;
                }
                if (item.TryGetProperty("type", out var t))
                {
                    var typeStr = t.GetString();
                    if (typeStr == "tool_result" && skipToolResults) continue;
                    if (typeStr == "text" && item.TryGetProperty("text", out var text))
                    {
                        var s = text.GetString();
                        if (!string.IsNullOrEmpty(s)) parts.Add(s);
                    }
                }
            }
            return parts.Count > 0 ? string.Join("\n", parts) : null;
        }

        return null;
    }

    private static string CleanMetadataTags(string text)
    {
        // Strip known metadata XML tags and their content
        text = Regex.Replace(text,
            @"<(?:ide_selection|ide_opened_file|user-prompt-submit-hook|system-reminder|local-command-caveat|local-command-stdout|command-name|command-message|command-args|available-deferred-tools|fast_mode_info|antml_thinking|antml_function_calls)[^>]*>.*?</(?:ide_selection|ide_opened_file|user-prompt-submit-hook|system-reminder|local-command-caveat|local-command-stdout|command-name|command-message|command-args|available-deferred-tools|fast_mode_info|antml_thinking|antml_function_calls)>",
            "", RegexOptions.Singleline);

        // Strip self-closing or unclosed metadata tags
        text = Regex.Replace(text, @"<(?:ide_selection|ide_opened_file|user-prompt-submit-hook|system-reminder|local-command-caveat|local-command-stdout|command-name|command-message|command-args|available-deferred-tools)[^>]*/?>", "");

        return text.Trim();
    }

    /// <summary>
    /// Normalize a path to match SessionService's folder name normalization.
    /// Must match the logic in SessionService.NormalizeFolderName exactly.
    /// </summary>
    private static string NormalizeFolderName(string path)
    {
        path = path.Replace('/', '\\').TrimEnd('\\');
        var sb = new StringBuilder(path.Length);
        bool lastWasDash = false;
        foreach (char c in path)
        {
            if (char.IsLetterOrDigit(c) && c <= 127)
            {
                sb.Append(c);
                lastWasDash = false;
            }
            else
            {
                if (!lastWasDash)
                    sb.Append('-');
                lastWasDash = true;
            }
        }
        return sb.ToString().Trim('-');
    }
}
