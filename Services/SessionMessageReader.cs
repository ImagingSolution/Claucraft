using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Claucraft.Services;

public enum MessageRole { User, Assistant, System }

public record AskUserOption(string Label, string Description);
public record AskUserQuestionItem(string Question, string Header, List<AskUserOption> Options, bool MultiSelect);
public record AskUserData(List<AskUserQuestionItem> Questions, Dictionary<string, string> Answers, Dictionary<string, string>? Notes);

/// <summary>One tool call inside an assistant turn, with the argument that best says what it did.</summary>
public record ToolCall(string Name, string? Detail);

/// <summary>An image attached to a user prompt: a file on disk or an inline base64 block.</summary>
public record ChatImage(string? Path, string? Base64);

/// <summary>Title and working folder of a session, for the chat view's header.</summary>
public record SessionMeta(string? Title, string? Cwd);

public record ConversationMessage(
    MessageRole Role,
    string Text,
    DateTime? Timestamp,
    string? ToolName,
    bool IsToolUse,
    bool IsThinking,
    AskUserData? AskUser = null,
    bool IsToolRejection = false,
    IReadOnlyList<ToolCall>? Tools = null,
    IReadOnlyList<ChatImage>? Images = null,
    // Set on an AskUserQuestion the CLI is still waiting on: the tool_use id it will answer
    string? PendingAskId = null
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
        var pendingAsks = new HashSet<string>();

        try
        {
            var toolUseIdToName = new Dictionary<string, string>();
            using var stream = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            while (!reader.EndOfStream)
            {
                var line = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(line)) continue;

                // Any tool_result for a question - answered, cancelled or rejected - settles it
                if (pendingAsks.Count > 0 && line.Contains("\"tool_result\""))
                    pendingAsks.RemoveWhere(id => line.Contains(id));

                var msg = ParseLine(line, toolUseIdToName);
                if (msg != null)
                {
                    messages.Add(msg);
                    if (msg.PendingAskId != null) pendingAsks.Add(msg.PendingAskId);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SessionMessageReader] ReadSession error: {ex.Message}");
        }

        var result = ConsolidateMessages(messages);
        // A question is only still open while nothing but status lines follows it; one left
        // behind by an interrupted session is never going to be answered
        bool laterContent = false;
        for (int k = result.Count - 1; k >= 0; k--)
        {
            var id = result[k].PendingAskId;
            if (id != null && (laterContent || !pendingAsks.Contains(id)))
                result.RemoveAt(k);
            else if (result[k].Role != MessageRole.System)
                laterContent = true;
        }
        return result;
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

            // AskUser messages: keep as-is (don't consolidate)
            if (msg.AskUser != null)
            {
                result.Add(msg);
                i++;
                continue;
            }

            // Tool rejection messages: keep as-is
            if (msg.IsToolRejection)
            {
                result.Add(msg);
                i++;
                continue;
            }

            // Consolidate consecutive assistant text messages into one. A tool call ends the
            // run, so a turn reads text / tools / text in the order it happened.
            if (msg.Role == MessageRole.Assistant && !msg.IsToolUse && !msg.IsThinking)
            {
                var textParts = new List<string> { msg.Text };
                var timestamp = msg.Timestamp;
                i++;

                while (i < messages.Count && messages[i].Role == MessageRole.Assistant
                    && !messages[i].IsToolUse && messages[i].AskUser == null)
                {
                    if (!messages[i].IsThinking && !string.IsNullOrWhiteSpace(messages[i].Text))
                        textParts.Add(messages[i].Text);
                    i++;
                }

                var consolidated = string.Join("\n\n", textParts);
                result.Add(new ConversationMessage(MessageRole.Assistant, consolidated, timestamp, null, false, false));
                continue;
            }

            // Consecutive tool calls fold into one group, the collapsed "ran N commands" line
            if (msg.IsToolUse && msg.Role == MessageRole.Assistant)
            {
                var tools = new List<ToolCall>();
                var timestamp = msg.Timestamp;
                while (i < messages.Count && messages[i].Role == MessageRole.Assistant
                    && messages[i].AskUser == null
                    && (messages[i].IsToolUse || messages[i].IsThinking))
                {
                    if (messages[i].Tools != null) tools.AddRange(messages[i].Tools!);
                    i++;
                }
                if (tools.Count > 0)
                    result.Add(new ConversationMessage(MessageRole.Assistant, $"[Tools: {tools.Count}]",
                        timestamp, tools[0].Name, true, false, Tools: tools));
                continue;
            }

            // Thinking on its own carries nothing readable (the CLI stores it redacted)
            if (msg.IsThinking)
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
            var toolUseIdToName = new Dictionary<string, string>();
            using var stream = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            int currentLine = 0;
            while (!reader.EndOfStream)
            {
                var line = reader.ReadLine();
                currentLine++;

                if (currentLine <= lastLineCount) continue;
                if (string.IsNullOrWhiteSpace(line)) continue;

                var msg = ParseLine(line, toolUseIdToName);
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

    private static ConversationMessage? ParseLine(string line, Dictionary<string, string> toolUseIdToName)
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
                // The summary written when the context is compacted is logged as a user turn,
                // but nobody typed it: show where it happened, not its text
                if (root.TryGetProperty("isCompactSummary", out var compactProp) && compactProp.ValueKind == JsonValueKind.True)
                    return new ConversationMessage(MessageRole.System, Loc.Get("ChatCompacted"), timestamp, null, false, false);

                // Check for toolUseResult (AskUserQuestion answer or tool rejection)
                if (root.TryGetProperty("toolUseResult", out var toolUseResultProp))
                {
                    if (toolUseResultProp.ValueKind == JsonValueKind.Object
                        && toolUseResultProp.TryGetProperty("questions", out _))
                    {
                        return ParseAskUserAnswer(toolUseResultProp, timestamp);
                    }
                    else if (toolUseResultProp.ValueKind == JsonValueKind.String
                        && toolUseResultProp.GetString() == "User rejected tool use")
                    {
                        return ParseToolRejection(root, timestamp, toolUseIdToName);
                    }
                }

                // Newer CLIs tag each prompt with who produced it: "human" is the person;
                // task-notification, peer, auto-continuation and the rest are the CLI or
                // another agent talking, and are not theirs to show as a prompt
                if (root.TryGetProperty("origin", out var originProp) && originProp.ValueKind == JsonValueKind.Object
                    && originProp.TryGetProperty("kind", out var kindProp) && kindProp.ValueKind == JsonValueKind.String
                    && kindProp.GetString() != "human")
                    return null;

                var userMsg = ParseUserMessage(root, timestamp);
                // Logs from before the origin tag: a background task's report is the one
                // non-human prompt that still gets through
                if (userMsg != null && userMsg.Text.TrimStart().StartsWith("<task-notification>", StringComparison.Ordinal))
                    return null;
                return userMsg;
            }
            else if (type == "assistant")
            {
                return ParseAssistantMessage(root, timestamp, toolUseIdToName);
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
        var images = new List<ChatImage>();

        if (msgProp.ValueKind == JsonValueKind.String)
        {
            text = msgProp.GetString();
        }
        else if (msgProp.ValueKind == JsonValueKind.Object && msgProp.TryGetProperty("content", out var contentProp))
        {
            text = ExtractAllTextContent(contentProp, skipToolResults: true);
            CollectInlineImages(contentProp, images);
        }

        text = string.IsNullOrWhiteSpace(text) ? "" : CleanMetadataTags(text);

        // Claucraft hands pasted images to the CLI as file paths, so the prompt text carries
        // them. Show those as thumbnails and keep the path out of the bubble.
        text = ImagePathPattern.Replace(text, m =>
        {
            var path = m.Groups["p"].Value;
            if (!File.Exists(path)) return m.Value;
            images.Add(new ChatImage(path, null));
            return "";
        }).Trim();

        if (string.IsNullOrWhiteSpace(text) && images.Count == 0) return null;

        return new ConversationMessage(MessageRole.User, text, timestamp, null, false, false,
            Images: images.Count > 0 ? images : null);
    }

    /// <summary>An absolute Windows image path, optionally @-prefixed and quoted, as Claucraft writes it.</summary>
    private static readonly Regex ImagePathPattern = new(
        @"@?""?(?<p>[A-Za-z]:\\[^""\r\n]*?\.(?:png|jpe?g|gif|bmp|webp))""?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static void CollectInlineImages(JsonElement content, List<ChatImage> images)
    {
        if (content.ValueKind != JsonValueKind.Array) return;
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!item.TryGetProperty("type", out var t) || t.GetString() != "image") continue;
            if (item.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.Object
                && src.TryGetProperty("data", out var data) && data.GetString() is string b64
                && b64.Length > 0)
                images.Add(new ChatImage(null, b64));
        }
    }

    /// <summary>
    /// The argument that best describes a tool call in one line: the command it ran, the file
    /// it touched, the pattern it searched for.
    /// </summary>
    private static string? DescribeToolInput(string? toolName, JsonElement item)
    {
        if (!item.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object)
            return null;

        if (toolName == "Skill" && input.TryGetProperty("skill", out var skill) && skill.GetString() is string s)
            return "/" + s;

        foreach (var key in new[] { "command", "file_path", "notebook_path", "path", "pattern", "url", "query", "description", "prompt" })
        {
            if (input.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                && v.GetString() is string value && !string.IsNullOrWhiteSpace(value))
            {
                value = value.Trim();
                int nl = value.IndexOf('\n');
                if (nl >= 0) value = value[..nl] + " …";
                return value.Length > 160 ? value[..160] + "…" : value;
            }
        }
        return null;
    }

    /// <summary>
    /// The session's title (renamed, AI-generated or summary, in that order) and its working
    /// folder, read from the same JSONL the transcript comes from.
    /// </summary>
    public static SessionMeta ReadSessionMeta(string jsonlPath)
    {
        string? customTitle = null, aiTitle = null, summary = null, cwd = null;
        try
        {
            using var stream = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                bool titleLine = line.Contains("\"custom-title\"") || line.Contains("\"ai-title\"")
                    || line.Contains("\"summary\"");
                if (!titleLine && cwd != null) continue;
                if (!titleLine && !line.Contains("\"cwd\"")) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    string? Str(string name) =>
                        root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                    switch (Str("type"))
                    {
                        case "custom-title": customTitle = Str("customTitle") ?? Str("title") ?? customTitle; break;
                        case "ai-title": aiTitle = Str("aiTitle") ?? aiTitle; break;
                        case "summary": summary = Str("summary") ?? summary; break;
                    }
                    cwd ??= Str("cwd");
                }
                catch { }
            }
        }
        catch { }
        return new SessionMeta(customTitle ?? aiTitle ?? summary, cwd);
    }

    private static ConversationMessage? ParseAssistantMessage(JsonElement root, DateTime? timestamp, Dictionary<string, string> toolUseIdToName)
    {
        if (!root.TryGetProperty("message", out var msgProp)) return null;
        if (!msgProp.TryGetProperty("content", out var contentProp)) return null;
        if (contentProp.ValueKind != JsonValueKind.Array) return null;

        var textParts = new List<string>();
        var tools = new List<ToolCall>();
        string? toolName = null;
        bool isToolUse = false;
        bool isThinking = false;
        ConversationMessage? pendingAsk = null;

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

                // Populate tool_use_id → name mapping for rejection lookup
                if (item.TryGetProperty("id", out var idEl))
                {
                    var id = idEl.GetString();
                    if (id != null && toolName != null)
                        toolUseIdToName[id] = toolName;
                }

                // AskUserQuestion shows up as its own question card; until it is answered the
                // card is the interactive one
                if (toolName == "AskUserQuestion")
                {
                    if (item.TryGetProperty("id", out var askId) && askId.GetString() is string askIdStr
                        && item.TryGetProperty("input", out var input)
                        && input.TryGetProperty("questions", out var qs))
                    {
                        var questions = ParseQuestions(qs);
                        if (questions.Count > 0)
                            pendingAsk = new ConversationMessage(MessageRole.Assistant, "", timestamp, toolName, false, false,
                                new AskUserData(questions, new Dictionary<string, string>(), null), PendingAskId: askIdStr);
                    }
                }
                else if (toolName != null)
                    tools.Add(new ToolCall(toolName, DescribeToolInput(toolName, item)));
            }
        }

        // If only thinking content, mark as thinking
        if (textParts.Count == 0 && !isToolUse) return null;

        // Suppress text accompanying tool_use (narration like "Now modify...", etc.)
        if (isToolUse)
        {
            if (tools.Count == 0) return pendingAsk;
            return new ConversationMessage(MessageRole.Assistant, $"[Tool: {toolName}]", timestamp, toolName, true, false,
                Tools: tools);
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

    private static List<AskUserQuestionItem> ParseQuestions(JsonElement questionsProp)
    {
        var questions = new List<AskUserQuestionItem>();
        if (questionsProp.ValueKind != JsonValueKind.Array) return questions;
        foreach (var q in questionsProp.EnumerateArray())
        {
            var question = q.TryGetProperty("question", out var qProp) ? qProp.GetString() ?? "" : "";
            var header = q.TryGetProperty("header", out var hProp) ? hProp.GetString() ?? "" : "";
            var multiSelect = q.TryGetProperty("multiSelect", out var msProp) && msProp.ValueKind == JsonValueKind.True;

            var options = new List<AskUserOption>();
            if (q.TryGetProperty("options", out var optsProp) && optsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var opt in optsProp.EnumerateArray())
                {
                    var label = opt.TryGetProperty("label", out var lProp) ? lProp.GetString() ?? "" : "";
                    var desc = opt.TryGetProperty("description", out var dProp) ? dProp.GetString() ?? "" : "";
                    options.Add(new AskUserOption(label, desc));
                }
            }
            questions.Add(new AskUserQuestionItem(question, header, options, multiSelect));
        }
        return questions;
    }

    private static ConversationMessage? ParseAskUserAnswer(JsonElement toolUseResult, DateTime? timestamp)
    {
        try
        {
            var questions = toolUseResult.TryGetProperty("questions", out var questionsProp)
                ? ParseQuestions(questionsProp)
                : new List<AskUserQuestionItem>();
            var answers = new Dictionary<string, string>();
            Dictionary<string, string>? notes = null;

            if (toolUseResult.TryGetProperty("answers", out var answersProp) && answersProp.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in answersProp.EnumerateObject())
                    answers[prop.Name] = prop.Value.GetString() ?? "";
            }

            if (toolUseResult.TryGetProperty("annotations", out var annotProp) && annotProp.ValueKind == JsonValueKind.Object)
            {
                notes = new Dictionary<string, string>();
                foreach (var prop in annotProp.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Object
                        && prop.Value.TryGetProperty("notes", out var notesProp))
                    {
                        notes[prop.Name] = notesProp.GetString() ?? "";
                    }
                }
            }

            if (questions.Count == 0) return null;

            var askUserData = new AskUserData(questions, answers, notes);
            return new ConversationMessage(
                MessageRole.Assistant, "", timestamp, "AskUserQuestion", false, false, askUserData);
        }
        catch
        {
            return null;
        }
    }

    private static ConversationMessage? ParseToolRejection(JsonElement root, DateTime? timestamp, Dictionary<string, string> toolUseIdToName)
    {
        string? toolName = null;
        if (root.TryGetProperty("message", out var msgProp)
            && msgProp.TryGetProperty("content", out var contentProp)
            && contentProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in contentProp.EnumerateArray())
            {
                if (item.TryGetProperty("tool_use_id", out var idProp))
                {
                    var id = idProp.GetString();
                    if (id != null && toolUseIdToName.TryGetValue(id, out var name))
                        toolName = name;
                }
            }
        }

        var rejectionText = toolName != null
            ? $"Tool rejected: {toolName}"
            : "Tool execution rejected";

        return new ConversationMessage(
            MessageRole.System, rejectionText, timestamp, toolName, false, false, null, true);
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

    /// <summary>
    /// Strips the wrapper tags Claude Code injects around prompts (system reminders, IDE
    /// selection, slash-command scaffolding) so only what the user actually typed remains.
    /// Shared with <see cref="HandoffBuilder"/> so both agree on what counts as user text.
    /// </summary>
    internal static string CleanMetadataTags(string text)
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
