using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Claucraft.Services;

/// <summary>One subagent the CLI spawned, as the windows panel shows it.</summary>
public sealed record SubagentRun
{
    /// <summary>The CLI's task id - the same string its completion notice names.</summary>
    public string Id { get; init; } = "";
    /// <summary>The short description the parent gave the task.</summary>
    public string Label { get; init; } = "";
    public string? AgentType { get; init; }
    public string? Model { get; init; }
    public DateTime Started { get; init; }
    /// <summary>Agents an agent spawned sit deeper than 1.</summary>
    public int Depth { get; init; }
}

/// <summary>
/// Lists the subagents a session still has in flight.
///
/// The CLI writes one <c>agent-&lt;id&gt;.meta.json</c> per task beside the transcript, which is
/// where the description, type and model come from. Finishing is not recorded there, and it
/// cannot be read from the tool result either - a backgrounded task is answered the moment it
/// launches - so the end is taken from the completion notice the CLI posts back into the parent
/// transcript. That transcript is only ever read forward from where the last read stopped.
/// </summary>
public static class SubagentMonitor
{
    private static readonly Regex TaskId =
        new(@"<task-id>([A-Za-z0-9_-]{1,64})</task-id>", RegexOptions.Compiled);

    /// <summary>Where a transcript's forward scan stopped, and what it had seen finish.</summary>
    private sealed class Progress
    {
        public long Offset;
        public readonly HashSet<string> Finished = new(StringComparer.Ordinal);
    }

    private static readonly Dictionary<string, Progress> Scans = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How long a subagent may write nothing before this stops calling it running.
    ///
    /// A session that is killed mid-task leaves its agents with no completion notice at all -
    /// two of the twenty-six on this machine - and without a cutoff those would be reported as
    /// running forever the next time the session was resumed. Long enough that a real agent
    /// sitting inside one slow tool call is not written off.
    /// </summary>
    private static readonly TimeSpan Abandoned = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The subagents of <paramref name="transcriptPath"/> that have not reported back yet,
    /// oldest first. An empty list is the normal answer.
    /// </summary>
    public static List<SubagentRun> ReadRunning(string? transcriptPath)
    {
        var runs = new List<SubagentRun>();
        if (string.IsNullOrEmpty(transcriptPath) || !File.Exists(transcriptPath)) return runs;

        var dir = SubagentDirectory(transcriptPath);
        if (dir == null || !Directory.Exists(dir)) return runs;

        var finished = FinishedIds(transcriptPath);

        foreach (var meta in SafeFiles(dir, "agent-*.meta.json"))
        {
            var id = IdFromMetaPath(meta);
            if (id == null || finished.Contains(id)) continue;

            if (DateTime.Now - LastActivity(dir, id, meta) > Abandoned) continue;

            var run = ReadMeta(meta, id);
            if (run != null) runs.Add(run);
        }

        runs.Sort((a, b) => a.Started.CompareTo(b.Started));
        return runs;
    }

    /// <summary>Frees the scan state for a transcript nothing is watching any more.</summary>
    public static void Forget(string? transcriptPath)
    {
        if (transcriptPath != null) Scans.Remove(transcriptPath);
    }

    /// <summary>
    /// <c>.../projects/&lt;project&gt;/&lt;session&gt;.jsonl</c> keeps its subagents in
    /// <c>.../projects/&lt;project&gt;/&lt;session&gt;/subagents</c>.
    /// </summary>
    private static string? SubagentDirectory(string transcriptPath)
    {
        var parent = Path.GetDirectoryName(transcriptPath);
        var session = Path.GetFileNameWithoutExtension(transcriptPath);
        if (parent == null || session.Length == 0) return null;
        return Path.Combine(parent, session, "subagents");
    }

    private static string? IdFromMetaPath(string path)
    {
        var name = Path.GetFileName(path);
        const string prefix = "agent-";
        const string suffix = ".meta.json";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) ||
            !name.EndsWith(suffix, StringComparison.Ordinal)) return null;

        var id = name[prefix.Length..^suffix.Length];
        return id.Length == 0 ? null : id;
    }

    /// <summary>When the agent last wrote to its own transcript, or was handed the task.</summary>
    private static DateTime LastActivity(string dir, string id, string metaPath)
    {
        try
        {
            var transcript = Path.Combine(dir, "agent-" + id + ".jsonl");
            if (File.Exists(transcript)) return File.GetLastWriteTime(transcript);
            return File.GetLastWriteTime(metaPath);
        }
        catch { return DateTime.Now; }
    }

    private static SubagentRun? ReadMeta(string path, string id)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            // Creation time is when the task was handed out; the file is written once.
            var started = File.GetCreationTime(path);
            if (started == default) started = File.GetLastWriteTime(path);

            return new SubagentRun
            {
                Id = id,
                Label = Text(root, "description") ?? id,
                AgentType = Text(root, "agentType"),
                Model = Text(root, "model"),
                Started = started,
                Depth = root.TryGetProperty("spawnDepth", out var depth)
                        && depth.ValueKind == JsonValueKind.Number
                    ? depth.GetInt32()
                    : 1,
            };
        }
        catch { return null; }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Ids the parent transcript has already reported finished. Only the bytes appended since
    /// the last call are read; a transcript that shrank - a rewrite, or a different session at
    /// the same path - is read again from the start.
    /// </summary>
    private static HashSet<string> FinishedIds(string transcriptPath)
    {
        if (!Scans.TryGetValue(transcriptPath, out var scan))
        {
            scan = new Progress();
            Scans[transcriptPath] = scan;
        }

        try
        {
            using var stream = new FileStream(
                transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            if (stream.Length < scan.Offset)
            {
                scan.Offset = 0;
                scan.Finished.Clear();
            }
            stream.Position = scan.Offset;

            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                // Cheap reject first: the notice is a fraction of a percent of the lines.
                if (line.Contains("task-notification", StringComparison.Ordinal))
                    foreach (Match match in TaskId.Matches(line))
                        scan.Finished.Add(match.Groups[1].Value);
            }

            // ReadLine can stop mid-line if the CLI is still writing; resuming from the
            // stream's own position would then split a record. Committing the whole length
            // is wrong for the same reason, so the tail is re-read next time instead.
            scan.Offset = LastCompleteLineEnd(transcriptPath, stream.Length);
        }
        catch { }

        return scan.Finished;
    }

    /// <summary>
    /// The offset just past the file's last newline, so a record the CLI is still writing is
    /// left for the next pass rather than being consumed in halves.
    /// </summary>
    private static long LastCompleteLineEnd(string path, long length)
    {
        if (length == 0) return 0;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var window = (int)Math.Min(8192, length);
            stream.Position = length - window;

            var buffer = new byte[window];
            int read = stream.Read(buffer, 0, window);
            for (int i = read - 1; i >= 0; i--)
                if (buffer[i] == (byte)'\n') return length - window + i + 1;

            return length - window;
        }
        catch { return 0; }
    }

    private static IEnumerable<string> SafeFiles(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern); }
        catch { return Array.Empty<string>(); }
    }
}
