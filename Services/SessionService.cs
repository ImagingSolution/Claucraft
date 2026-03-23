using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Claucraft.Services;

public record SessionInfo(string Id, string? Cwd, string? Summary, DateTime? Timestamp)
{
    /// <summary>AI-generated title from sessions-index.json (like Claude Desktop)</summary>
    public string? Title { get; init; }

    /// <summary>Returns Title if available, otherwise Summary (first user message)</summary>
    public string? DisplayTitle => !string.IsNullOrWhiteSpace(Title) ? Title : Summary;
}

public static class SessionService
{
    /// <summary>
    /// Get sessions for a specific project folder by reading sessions-index.json and JSONL files.
    /// </summary>
    public static Task<List<SessionInfo>> GetSessionsForProjectAsync(string projectFolder)
    {
        return Task.Run(() =>
        {
            var sessions = new List<SessionInfo>();
            try
            {
                string claudeProjectsDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".claude", "projects");

                if (!Directory.Exists(claudeProjectsDir))
                    return sessions;

                string normalizedTarget = NormalizeFolderName(projectFolder);
                var matchingDirs = Directory.GetDirectories(claudeProjectsDir)
                    .Where(d =>
                    {
                        string dirName = Path.GetFileName(d);
                        string normalizedDir = NormalizeFolderName(dirName);
                        return normalizedDir.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase);
                    })
                    .ToList();

                foreach (var dir in matchingDirs)
                {
                    // Load sessions-index.json for AI-generated titles
                    var indexTitles = LoadSessionsIndex(dir);

                    var jsonlFiles = Directory.GetFiles(dir, "*.jsonl");
                    foreach (var file in jsonlFiles)
                    {
                        try
                        {
                            string sessionId = Path.GetFileNameWithoutExtension(file);
                            var info = ParseSessionFile(file, sessionId);
                            if (info != null && !string.IsNullOrWhiteSpace(info.Summary))
                            {
                                // Attach AI title from index if available
                                if (indexTitles.TryGetValue(sessionId, out var titleInfo))
                                {
                                    info = info with { Title = titleInfo.title };
                                    if (info.Timestamp == null && titleInfo.modified != null)
                                        info = info with { Timestamp = titleInfo.modified };
                                }
                                sessions.Add(info);
                            }
                            else
                            {
                                try { File.Delete(file); }
                                catch { }
                            }
                        }
                        catch
                        {
                            try { File.Delete(file); }
                            catch { }
                        }
                    }

                    // Create or update sessions-index.json so CLI can populate AI summaries later
                    if (sessions.Count > 0)
                        WriteSessionsIndex(dir, sessions, projectFolder, indexTitles);
                }

                sessions.Sort((a, b) => (b.Timestamp ?? DateTime.MinValue).CompareTo(a.Timestamp ?? DateTime.MinValue));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to list sessions: {ex.Message}");
            }
            return sessions;
        });
    }

    /// <summary>Load AI-generated titles from sessions-index.json</summary>
    private static Dictionary<string, (string? title, DateTime? modified)> LoadSessionsIndex(string projectDir)
    {
        var result = new Dictionary<string, (string?, DateTime?)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var indexPath = Path.Combine(projectDir, "sessions-index.json");
            if (!File.Exists(indexPath)) return result;

            var json = File.ReadAllText(indexPath);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("entries", out var entries)
                && entries.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in entries.EnumerateArray())
                {
                    string? sessionId = null;
                    string? title = null;
                    DateTime? modified = null;

                    if (entry.TryGetProperty("sessionId", out var sidProp))
                        sessionId = sidProp.GetString();

                    // "summary" field = AI-generated title (like Claude Desktop)
                    if (entry.TryGetProperty("summary", out var sumProp))
                        title = sumProp.GetString();

                    // Fall back to firstPrompt if no summary
                    if (string.IsNullOrWhiteSpace(title) && entry.TryGetProperty("firstPrompt", out var fpProp))
                    {
                        title = CleanupPromptText(fpProp.GetString());
                    }

                    if (entry.TryGetProperty("modified", out var modProp))
                    {
                        var modStr = modProp.GetString();
                        if (modStr != null && DateTime.TryParse(modStr, out var dt))
                            modified = dt;
                    }

                    if (sessionId != null)
                        result[sessionId] = (title, modified);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load sessions-index.json: {ex.Message}");
        }
        return result;
    }

    /// <summary>
    /// Create or update sessions-index.json with current session data.
    /// Preserves existing AI-generated summary fields so CLI can populate them later.
    /// </summary>
    private static void WriteSessionsIndex(
        string projectDir,
        List<SessionInfo> sessions,
        string projectFolder,
        Dictionary<string, (string? title, DateTime? modified)> existingIndex)
    {
        try
        {
            var indexPath = Path.Combine(projectDir, "sessions-index.json");

            // Also load raw summaries from existing file to preserve them verbatim
            var existingSummaries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(indexPath))
            {
                try
                {
                    var existingJson = File.ReadAllText(indexPath);
                    using var existingDoc = JsonDocument.Parse(existingJson);
                    if (existingDoc.RootElement.TryGetProperty("entries", out var entries)
                        && entries.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var entry in entries.EnumerateArray())
                        {
                            if (entry.TryGetProperty("sessionId", out var sid))
                            {
                                var sidStr = sid.GetString();
                                if (sidStr == null) continue;
                                if (entry.TryGetProperty("summary", out var sum)
                                    && sum.ValueKind == JsonValueKind.String
                                    && !string.IsNullOrWhiteSpace(sum.GetString()))
                                {
                                    existingSummaries[sidStr] = sum.GetString()!;
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            using var ms = new MemoryStream();
            using var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true });

            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            writer.WriteStartArray("entries");

            foreach (var session in sessions)
            {
                var jsonlPath = Path.Combine(projectDir, session.Id + ".jsonl");
                var fileMtime = File.Exists(jsonlPath)
                    ? new DateTimeOffset(File.GetLastWriteTimeUtc(jsonlPath)).ToUnixTimeMilliseconds()
                    : new DateTimeOffset(session.Timestamp ?? DateTime.UtcNow).ToUnixTimeMilliseconds();

                writer.WriteStartObject();
                writer.WriteString("sessionId", session.Id);
                writer.WriteString("fullPath", jsonlPath.Replace('/', '\\'));
                writer.WriteNumber("fileMtime", fileMtime);
                writer.WriteString("firstPrompt", session.Summary ?? "No prompt");

                // Preserve existing AI-generated summary
                if (existingSummaries.TryGetValue(session.Id, out var summary))
                    writer.WriteString("summary", summary);

                writer.WriteString("created", (session.Timestamp ?? DateTime.UtcNow).ToUniversalTime().ToString("o"));
                writer.WriteString("modified", (session.Timestamp ?? DateTime.UtcNow).ToUniversalTime().ToString("o"));
                writer.WriteString("projectPath", projectFolder);
                writer.WriteBoolean("isSidechain", false);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteString("originalPath", projectFolder);
            writer.WriteEndObject();

            writer.Flush();
            File.WriteAllBytes(indexPath, ms.ToArray());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to write sessions-index.json: {ex.Message}");
        }
    }

    private static SessionInfo? ParseSessionFile(string filePath, string sessionId)
    {
        string? cwd = null;
        string? summary = null;
        DateTime? timestamp = null;
        string? queueContent = null;

        // Read lines to get metadata (scan more lines to skip file-history-snapshot entries)
        using var reader = new StreamReader(filePath, Encoding.UTF8);
        int lineCount = 0;
        while (!reader.EndOfStream && lineCount < 50)
        {
            string? line = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(line)) continue;
            lineCount++;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                // Get cwd from message entries
                if (cwd == null && root.TryGetProperty("cwd", out var cwdProp))
                {
                    cwd = cwdProp.GetString();
                }

                // Get timestamp
                if (root.TryGetProperty("timestamp", out var tsProp))
                {
                    var tsStr = tsProp.GetString();
                    if (tsStr != null && DateTime.TryParse(tsStr, out var dt))
                        timestamp = dt;
                }

                if (root.TryGetProperty("type", out var typeProp))
                {
                    string? type = typeProp.GetString();

                    // Extract from queue-operation as fallback
                    if (queueContent == null && type == "queue-operation"
                        && root.TryGetProperty("content", out var qcProp))
                    {
                        queueContent = qcProp.GetString();
                    }

                    // Try to get first real user message as summary
                    if (summary == null && type == "user")
                    {
                        // Skip metadata-only messages (isMeta: true)
                        bool isMeta = root.TryGetProperty("isMeta", out var metaProp)
                                      && metaProp.ValueKind == JsonValueKind.True;
                        if (!isMeta)
                        {
                            string? candidate = null;
                            if (root.TryGetProperty("message", out var msgProp))
                            {
                                if (msgProp.ValueKind == JsonValueKind.String)
                                    candidate = msgProp.GetString();
                                else if (msgProp.ValueKind == JsonValueKind.Object
                                         && msgProp.TryGetProperty("content", out var contentProp))
                                    candidate = ExtractTextContent(contentProp);
                            }

                            // Only accept as summary if it has meaningful text after cleanup
                            var cleaned = CleanupPromptText(candidate);
                            if (!string.IsNullOrWhiteSpace(cleaned))
                                summary = candidate; // Store original, cleanup at the end
                        }
                    }
                }

                // Also check for role-based messages
                if (summary == null && root.TryGetProperty("role", out var roleProp)
                    && roleProp.GetString() == "user"
                    && root.TryGetProperty("content", out var contentProp2))
                {
                    var candidate2 = ExtractTextContent(contentProp2);
                    var cleaned2 = CleanupPromptText(candidate2);
                    if (!string.IsNullOrWhiteSpace(cleaned2))
                        summary = candidate2;
                }

                // Stop scanning once we have both cwd and summary
                if (cwd != null && summary != null) break;
            }
            catch { }
        }

        // Use queue-operation content as fallback
        if (string.IsNullOrWhiteSpace(summary) && !string.IsNullOrWhiteSpace(queueContent))
            summary = queueContent;

        // Get file modification time as fallback timestamp
        if (timestamp == null)
        {
            timestamp = File.GetLastWriteTime(filePath);
        }

        // Clean up summary (strip IDE tags, whitespace, truncate)
        summary = CleanupPromptText(summary);

        return new SessionInfo(sessionId, cwd, summary, timestamp);
    }

    private static string? ExtractTextContent(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
            return element.GetString();

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                    return item.GetString();
                if (item.TryGetProperty("type", out var t) && t.GetString() == "text"
                    && item.TryGetProperty("text", out var text))
                    return text.GetString();
            }
        }

        return null;
    }

    /// <summary>
    /// Clean up prompt text for display: strip IDE/CLI metadata tags, normalize whitespace, truncate.
    /// </summary>
    private static string? CleanupPromptText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        // Strip known metadata XML tags and their content
        text = Regex.Replace(text,
            @"<(?:ide_selection|ide_opened_file|user-prompt-submit-hook|system-reminder|local-command-caveat|local-command-stdout|command-name|command-message|command-args)[^>]*>.*?</(?:ide_selection|ide_opened_file|user-prompt-submit-hook|system-reminder|local-command-caveat|local-command-stdout|command-name|command-message|command-args)>",
            "", RegexOptions.Singleline);

        // Strip self-closing or unclosed metadata tags
        text = Regex.Replace(text, @"<(?:ide_selection|ide_opened_file|user-prompt-submit-hook|system-reminder|local-command-caveat|local-command-stdout|command-name|command-message|command-args)[^>]*/?>", "");

        // Normalize whitespace: collapse multiple spaces/newlines into single space
        text = Regex.Replace(text, @"\s+", " ").Trim();

        // Skip "No prompt" placeholder
        if (text == "No prompt") return null;

        // Truncate to max 80 chars
        if (text.Length > 80)
            text = text[..80] + "...";

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>
    /// Normalize a path or folder name to a comparable form.
    /// Replaces all non-alphanumeric ASCII chars and non-ASCII chars with '-',
    /// then collapses consecutive '-' into one.
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

    /// <summary>
    /// Get the most recent project folders (up to 10) from ~/.claude/projects/ JSONL files.
    /// Returns actual folder paths extracted from session cwd fields, sorted by most recent first.
    /// </summary>
    public static Task<List<string>> GetRecentProjectFoldersAsync()
    {
        return Task.Run(() =>
        {
            var folderTimestamps = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string claudeProjectsDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".claude", "projects");

                if (!Directory.Exists(claudeProjectsDir))
                    return new List<string>();

                foreach (var dir in Directory.GetDirectories(claudeProjectsDir))
                {
                    var jsonlFiles = Directory.GetFiles(dir, "*.jsonl");
                    foreach (var file in jsonlFiles)
                    {
                        try
                        {
                            string? cwd = null;
                            DateTime? timestamp = null;

                            using var reader = new StreamReader(file, Encoding.UTF8);
                            int lineCount = 0;
                            while (!reader.EndOfStream && lineCount < 10)
                            {
                                string? line = reader.ReadLine();
                                if (string.IsNullOrWhiteSpace(line)) continue;
                                lineCount++;

                                try
                                {
                                    using var doc = JsonDocument.Parse(line);
                                    var root = doc.RootElement;

                                    if (cwd == null && root.TryGetProperty("cwd", out var cwdProp))
                                        cwd = cwdProp.GetString();

                                    if (root.TryGetProperty("timestamp", out var tsProp))
                                    {
                                        var tsStr = tsProp.GetString();
                                        if (tsStr != null && DateTime.TryParse(tsStr, out var dt))
                                            timestamp = dt;
                                    }
                                }
                                catch { }

                                if (cwd != null && timestamp != null) break;
                            }

                            if (cwd != null && Directory.Exists(cwd))
                            {
                                var ts = timestamp ?? File.GetLastWriteTime(file);
                                if (!folderTimestamps.ContainsKey(cwd) || folderTimestamps[cwd] < ts)
                                    folderTimestamps[cwd] = ts;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to get recent project folders: {ex.Message}");
            }

            return folderTimestamps
                .OrderByDescending(kv => kv.Value)
                .Take(10)
                .Select(kv => kv.Key)
                .ToList();
        });
    }

    public static string BuildResumeCommand(string sessionId)
    {
        return $"claude -r {sessionId}";
    }
}
