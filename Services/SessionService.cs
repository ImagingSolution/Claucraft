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
    /// <summary>Session title: a name set with /rename, otherwise the generated one.</summary>
    public string? Title { get; init; }

    /// <summary>What the /resume picker shows for the session, down to its "No prompt" fallback.</summary>
    public string? DisplayTitle =>
        !string.IsNullOrWhiteSpace(Title) ? Title
        : !string.IsNullOrWhiteSpace(Summary) ? Summary
        : "No prompt";
}

public static class SessionService
{
    /// <summary>
    /// Get the sessions of a project folder, listing what Claude Code's /resume picker lists:
    /// every non-sidechain transcript in ~/.claude/projects/{folder}/ that holds at least one
    /// message, most recently modified first. Transcripts are only ever read, never written.
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
                    // Top level only - subagent transcripts live under subagents/ and /resume skips them.
                    foreach (var file in Directory.GetFiles(dir, "*.jsonl"))
                    {
                        try
                        {
                            var info = ParseSessionFile(file, Path.GetFileNameWithoutExtension(file));
                            if (info != null)
                                sessions.Add(info);
                        }
                        catch (Exception ex)
                        {
                            // A transcript we cannot read is skipped, never deleted.
                            Debug.WriteLine($"Failed to read session {file}: {ex.Message}");
                        }
                    }
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

    /// <summary>What a single pass over a transcript collects.</summary>
    private sealed class SessionScan
    {
        public string? Cwd;
        public string? FirstPrompt;
        public string? CustomTitle;    // set by /rename or --name
        public string? AiTitle;        // generated title, re-emitted as the session grows
        public string? SummaryRecord;  // older {"type":"summary"} entry
        public bool IsSidechain;
        public bool SeenFirstMessage;
        public bool HasMessages;
    }

    /// <summary>Lines read from the head of a transcript before giving up on finding the first prompt.</summary>
    private const int HeadScanLines = 200;

    /// <summary>Bytes read from the tail of a transcript to pick up the newest title entries.</summary>
    private const long TailScanBytes = 256 * 1024;

    private static SessionInfo? ParseSessionFile(string filePath, string sessionId)
    {
        var scan = new SessionScan();

        using (var stream = OpenShared(filePath))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            int lineCount = 0;
            while (!reader.EndOfStream && lineCount < HeadScanLines)
            {
                string? line = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(line)) continue;
                lineCount++;

                ScanLine(line, scan, captureFirstPrompt: true);

                if (scan.Cwd != null && scan.FirstPrompt != null && scan.SeenFirstMessage)
                    break;
            }
        }

        // Titles are appended as the session grows, so the current one sits at the end of the file.
        ScanTail(filePath, scan);

        // /resume lists neither subagent transcripts nor sessions that never received a message.
        if (scan.IsSidechain || !scan.HasMessages)
            return null;

        string? title = scan.CustomTitle ?? scan.AiTitle ?? scan.SummaryRecord;

        return new SessionInfo(sessionId, scan.Cwd, CleanupPromptText(scan.FirstPrompt),
                               File.GetLastWriteTime(filePath))
        {
            Title = CleanupPromptText(title)
        };
    }

    /// <summary>Open a transcript in a way that tolerates the CLI writing to it at the same time.</summary>
    private static FileStream OpenShared(string filePath)
        => new FileStream(filePath, FileMode.Open, FileAccess.Read,
                          FileShare.ReadWrite | FileShare.Delete);

    private static void ScanTail(string filePath, SessionScan scan)
    {
        try
        {
            using var stream = OpenShared(filePath);
            bool startsMidFile = stream.Length > TailScanBytes;
            if (startsMidFile)
                stream.Seek(stream.Length - TailScanBytes, SeekOrigin.Begin);

            using var reader = new StreamReader(stream, Encoding.UTF8);
            if (startsMidFile)
                reader.ReadLine(); // the window opens mid-line; drop the fragment

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                // Only a scan that started at byte 0 can tell which user message came first.
                ScanLine(line, scan, captureFirstPrompt: !startsMidFile);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to scan tail of {filePath}: {ex.Message}");
        }
    }

    private static void ScanLine(string line, SessionScan scan, bool captureFirstPrompt)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            if (scan.Cwd == null && root.TryGetProperty("cwd", out var cwdProp))
                scan.Cwd = cwdProp.GetString();

            string? type = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;

            // Older transcripts write bare {"role":"user","content":...} entries.
            if (type == null && root.TryGetProperty("role", out var roleProp) && roleProp.GetString() == "user")
                type = "user";

            switch (type)
            {
                case "custom-title":
                    scan.CustomTitle = ReadString(root, "customTitle") ?? ReadString(root, "title") ?? scan.CustomTitle;
                    return;
                case "ai-title":
                    scan.AiTitle = ReadString(root, "aiTitle") ?? scan.AiTitle;
                    return;
                case "summary":
                    scan.SummaryRecord = ReadString(root, "summary") ?? scan.SummaryRecord;
                    return;
                case "user":
                case "assistant":
                    break;
                default:
                    return;
            }

            scan.HasMessages = true;

            // A transcript counts as a subagent one when its very first message is a sidechain message.
            if (!scan.SeenFirstMessage)
            {
                scan.SeenFirstMessage = true;
                scan.IsSidechain = root.TryGetProperty("isSidechain", out var sideProp)
                                   && sideProp.ValueKind == JsonValueKind.True;
            }

            if (!captureFirstPrompt || scan.FirstPrompt != null || type != "user") return;

            // Skip metadata-only messages (isMeta: true)
            if (root.TryGetProperty("isMeta", out var metaProp) && metaProp.ValueKind == JsonValueKind.True) return;

            string? candidate = null;
            if (root.TryGetProperty("message", out var msgProp))
            {
                if (msgProp.ValueKind == JsonValueKind.String)
                    candidate = msgProp.GetString();
                else if (msgProp.ValueKind == JsonValueKind.Object
                         && msgProp.TryGetProperty("content", out var contentProp))
                    candidate = ExtractTextContent(contentProp);
            }
            else if (root.TryGetProperty("content", out var contentProp2))
            {
                candidate = ExtractTextContent(contentProp2);
            }

            // Only accept as the first prompt if meaningful text survives cleanup
            if (!string.IsNullOrWhiteSpace(CleanupPromptText(candidate)))
                scan.FirstPrompt = candidate; // store original, cleanup happens once at the end
        }
        catch { }
    }

    private static string? ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
            return null;
        var value = prop.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
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
    /// Tool-generated working folders (e.g. ~/.claude, ~/.claude-mem/observer-sessions) also
    /// appear under ~/.claude/projects, but they are not projects the user created.
    /// Treat any path containing a dot-prefixed segment as one of those and hide it.
    /// </summary>
    private static bool IsUserProjectFolder(string path)
    {
        foreach (var segment in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment.Length > 1 && segment[0] == '.' && segment != "..")
                return false;
        }
        return true;
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
                .Where(kv => IsUserProjectFolder(kv.Key))
                .OrderByDescending(kv => kv.Value)
                .Take(10)
                .Select(kv => kv.Key)
                .ToList();
        });
    }

}
