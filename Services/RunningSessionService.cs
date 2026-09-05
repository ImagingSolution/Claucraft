using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Claucraft.Services;

/// <summary>
/// Which sessions a Claude Code process is already holding. A session only runs in one place at a
/// time, so a launch aimed at one that is taken dies before it draws a prompt - a background agent
/// makes it print "Your most recent conversation is running in the background (session ...)" and
/// exit, which reaches the terminal as a bare "[Process exited]".
///
/// Two ledgers under ~/.claude answer this, and both are only ever read:
/// <c>sessions/{pid}.json</c>, one file per live CLI process, and <c>daemon/roster.json</c>, the
/// subset parked in the background daemon - the list `claude agents` shows.
/// </summary>
public static class RunningSessionService
{
    /// <summary>The phrase the CLI prints when a launch collides with a background agent.</summary>
    public const string CollisionMarker = "running in the background";

    private readonly record struct Held(string SessionId, string? Cwd);

    // Both ledgers are small and the callers are user actions, so they are re-read rather than
    // watched. The TTL only keeps list refreshes off the disk.
    private static readonly object _gate = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(2);
    private static List<Held> _live = new();
    private static List<Held> _agents = new();
    private static DateTime _cachedAtUtc = DateTime.MinValue;

    /// <summary>
    /// Sessions a live CLI process holds, whether it runs in a window here, in another app or in
    /// a background agent. A null or empty <paramref name="projectFolder"/> means "any folder".
    /// </summary>
    public static HashSet<string> LiveSessionIds(string? projectFolder)
        => Select(Snapshot().Live, projectFolder);

    /// <summary>Sessions a background agent holds - the ones `claude agents` can attach to.</summary>
    public static HashSet<string> AgentSessionIds(string? projectFolder)
        => Select(Snapshot().Agents, projectFolder);

    /// <summary>Whether any live CLI process holds this session.</summary>
    public static bool IsLive(string? sessionId, string? projectFolder = null)
        => !string.IsNullOrEmpty(sessionId) && LiveSessionIds(projectFolder).Contains(sessionId!);

    /// <summary>Whether a background agent holds this session.</summary>
    public static bool IsHeldByAgent(string? sessionId, string? projectFolder = null)
        => !string.IsNullOrEmpty(sessionId) && AgentSessionIds(projectFolder).Contains(sessionId!);

    private static HashSet<string> Select(List<Held> held, string? projectFolder)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in held)
        {
            if (string.IsNullOrEmpty(projectFolder) || PathEquals(entry.Cwd, projectFolder))
                ids.Add(entry.SessionId);
        }
        return ids;
    }

    private static (List<Held> Live, List<Held> Agents) Snapshot()
    {
        lock (_gate)
        {
            if (DateTime.UtcNow - _cachedAtUtc >= CacheTtl)
            {
                _live = ReadSessionLedger();
                _agents = ReadDaemonRoster();
                _cachedAtUtc = DateTime.UtcNow;
            }
            return (_live, _agents);
        }
    }

    private static string ClaudeDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    /// <summary>~/.claude/sessions/{pid}.json - one file per live CLI process.</summary>
    private static List<Held> ReadSessionLedger()
    {
        var held = new List<Held>();
        try
        {
            string dir = Path.Combine(ClaudeDir, "sessions");
            if (!Directory.Exists(dir)) return held;

            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    if (ReadEntry(doc.RootElement) is { } entry) held.Add(entry);
                }
                catch { }   // a file being rewritten as it is read is not worth failing over
            }
        }
        catch { }
        return held;
    }

    /// <summary>~/.claude/daemon/roster.json - the sessions parked in the background daemon.</summary>
    private static List<Held> ReadDaemonRoster()
    {
        var held = new List<Held>();
        try
        {
            string path = Path.Combine(ClaudeDir, "daemon", "roster.json");
            if (!File.Exists(path)) return held;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("workers", out var workers) ||
                workers.ValueKind != JsonValueKind.Object)
                return held;

            foreach (var worker in workers.EnumerateObject())
            {
                if (ReadEntry(worker.Value) is { } entry) held.Add(entry);
            }
        }
        catch { }
        return held;
    }

    /// <summary>
    /// Both ledgers name a session the same way: sessionId, cwd, and the pid plus process start
    /// time of whatever holds it. Null when the record is malformed or its process is gone.
    /// </summary>
    private static Held? ReadEntry(JsonElement record)
    {
        if (record.ValueKind != JsonValueKind.Object) return null;

        if (!record.TryGetProperty("sessionId", out var idElement) ||
            idElement.ValueKind != JsonValueKind.String)
            return null;
        string? sessionId = idElement.GetString();
        if (string.IsNullOrEmpty(sessionId)) return null;

        // A record outlives the process that crashed out of it, so the pid decides. A record with
        // no pid at all cannot be checked and is taken at its word: assuming it is still held only
        // costs a launch the next session down the list.
        if (record.TryGetProperty("pid", out var pidElement) &&
            pidElement.TryGetInt32(out int pid))
        {
            record.TryGetProperty("procStart", out var startElement);
            if (!IsAlive(pid, startElement.ValueKind == JsonValueKind.String
                    ? startElement.GetString()
                    : null))
                return null;
        }

        record.TryGetProperty("cwd", out var cwdElement);
        return new Held(sessionId!, cwdElement.ValueKind == JsonValueKind.String
            ? cwdElement.GetString()
            : null);
    }

    private static bool IsAlive(int pid, string? procStart)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) return false;
            // Windows recycles pids. The recorded FILETIME pins the record to that exact process.
            if (long.TryParse(procStart, out long startedAt))
                return process.StartTime.ToFileTime() == startedAt;
            return true;
        }
        catch { return false; }
    }

    private static bool PathEquals(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
