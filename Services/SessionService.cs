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
                            if (info != null)
                                sessions.Add(info);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Failed to parse session file {file}: {ex.Message}");
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

    public static string BuildResumeCommand(string sessionId)
    {
        return $"claude -r {sessionId}";
    }
}
