using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ClaudeCodeMDI.Services;

public record SessionInfo(string Id, string? Cwd, string? Summary, DateTime? Timestamp);

public static class SessionService
{
    /// <summary>
    /// Get sessions for a specific project folder by reading ~/.claude/projects/ JSONL files.
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

                // Find matching project folder(s)
                // Claude's encoding normalizes paths: special chars → '-'
                // We normalize both sides and compare to handle variations
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
                    var jsonlFiles = Directory.GetFiles(dir, "*.jsonl");
                    foreach (var file in jsonlFiles)
                    {
                        try
                        {
                            string sessionId = Path.GetFileNameWithoutExtension(file);
                            var info = ParseSessionFile(file, sessionId);
                            if (info != null && !string.IsNullOrWhiteSpace(info.Summary))
                            {
                                sessions.Add(info);
                            }
                            else
                            {
                                // Session has no user message — invalid/empty session, delete it
                                try { File.Delete(file); }
                                catch { }
                                Debug.WriteLine($"Deleted empty session file: {file}");
                            }
                        }
                        catch (Exception ex)
                        {
                            // Unparseable session file — delete it
                            try { File.Delete(file); }
                            catch { }
                            Debug.WriteLine($"Deleted corrupt session file {file}: {ex.Message}");
                        }
                    }
                }

                // Sort by timestamp descending (newest first)
                sessions.Sort((a, b) => (b.Timestamp ?? DateTime.MinValue).CompareTo(a.Timestamp ?? DateTime.MinValue));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to list sessions: {ex.Message}");
            }
            return sessions;
        });
    }

    private static SessionInfo? ParseSessionFile(string filePath, string sessionId)
    {
        string? cwd = null;
        string? summary = null;
        DateTime? timestamp = null;

        // Read first few lines to get metadata
        using var reader = new StreamReader(filePath, Encoding.UTF8);
        int lineCount = 0;
        while (!reader.EndOfStream && lineCount < 20)
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

                // Try to get first user message as summary
                if (summary == null && root.TryGetProperty("type", out var typeProp))
                {
                    string? type = typeProp.GetString();
                    if (type == "user" && root.TryGetProperty("message", out var msgProp))
                    {
                        if (msgProp.ValueKind == JsonValueKind.String)
                        {
                            summary = msgProp.GetString();
                        }
                        else if (msgProp.ValueKind == JsonValueKind.Object
                                 && msgProp.TryGetProperty("content", out var contentProp))
                        {
                            summary = ExtractTextContent(contentProp);
                        }
                    }
                }

                // Also check for role-based messages
                if (summary == null && root.TryGetProperty("role", out var roleProp)
                    && roleProp.GetString() == "user"
                    && root.TryGetProperty("content", out var contentProp2))
                {
                    summary = ExtractTextContent(contentProp2);
                }
            }
            catch { }
        }

        // Get file modification time as fallback timestamp
        if (timestamp == null)
        {
            timestamp = File.GetLastWriteTime(filePath);
        }

        // Truncate summary
        if (summary != null && summary.Length > 80)
            summary = summary[..80] + "...";

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
