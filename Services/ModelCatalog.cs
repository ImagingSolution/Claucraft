using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Claucraft.Services;

/// <summary>
/// The models the status-bar dropdown offers, one per line (fable, opus, ...), each named after
/// the newest version of that line seen so far. The alias sent to the CLI already resolves to
/// the newest release by itself; what this keeps current is the name on the entry, learnt from
/// the model ids the transcripts record and from the CLI's own "Set model to X" banner, so a
/// new release shows up without a new build.
/// </summary>
public static class ModelCatalog
{
    /// <summary>The lines offered, in menu order, with the id each resolved to when this was
    /// written - the floor until something newer is seen.</summary>
    private static readonly (string Alias, string ModelId)[] Baseline =
    {
        ("fable", "claude-fable-5-1"),
        ("opus", "claude-opus-5"),
        ("sonnet", "claude-sonnet-5"),
        ("haiku", "claude-haiku-4-5"),
    };

    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> Latest =
        Baseline.ToDictionary(b => b.Alias, b => b.ModelId, StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised, on whatever thread observed it, when a line's newest version moved.</summary>
    public static event Action? Changed;

    /// <summary>The dropdown's entries as (alias to send, newest id known for it).</summary>
    public static IReadOnlyList<(string Alias, string ModelId)> Switchable
    {
        get
        {
            lock (Gate)
                return Baseline.Select(b => (b.Alias, Latest[b.Alias])).ToList();
        }
    }

    /// <summary>The newest id known for an alias, or null for one that is not a line here.</summary>
    public static string? IdForAlias(string alias)
    {
        lock (Gate)
            return Latest.TryGetValue(alias, out var id) ? id : null;
    }

    // claude-opus-5-5, claude-haiku-4-5-20251001, claude-opus-4-20250514 - the minor is one or
    // two digits, so the eight-digit date that may follow is never taken for one.
    private static readonly Regex IdRegex = new(
        @"^claude-([a-z]+)-(\d+)(?:-(\d{1,2}))?(?:-\d{8})?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // "Opus 5.5", "Fable 5.1", "Opus 5" - the name the CLI's banner and the status bar use.
    private static readonly Regex NameRegex = new(
        @"^([A-Za-z]+)\s+(\d+)(?:\.(\d+))?$",
        RegexOptions.Compiled);

    /// <summary>
    /// The name a model id goes by ("claude-opus-5-5" → "Opus 5.5"), or null for an id that is
    /// not in the family-major-minor shape. A context-window suffix ("[1m]") is read past.
    /// </summary>
    public static string? DisplayName(string modelId)
    {
        if (!TryParseId(modelId, out var family, out var major, out var minor)) return null;
        var name = char.ToUpperInvariant(family[0]) + family[1..].ToLowerInvariant() + " " + major;
        return minor != null ? name + "." + minor : name;
    }

    /// <summary>Takes note of a model id seen in a transcript. True when it moved a line forward.</summary>
    public static bool ObserveId(string? modelId)
    {
        if (!Record(modelId)) return false;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Takes note of a display name ("Opus 5.5") the CLI confirmed a switch to.</summary>
    public static bool ObserveName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var m = NameRegex.Match(name.Trim());
        if (!m.Success) return false;

        var id = "claude-" + m.Groups[1].Value.ToLowerInvariant() + "-" + m.Groups[2].Value;
        if (m.Groups[3].Success) id += "-" + m.Groups[3].Value;
        return ObserveId(id);
    }

    /// <summary>How many of the newest transcripts the startup scan reads, and how much of each.</summary>
    private const int ScanFileCount = 30;
    private const int ScanTailBytes = 64 * 1024;

    private static readonly Regex TranscriptModelRegex = new(
        "\"model\":\"(claude-[A-Za-z0-9-]+)\"", RegexOptions.Compiled);

    /// <summary>
    /// Learns the newest versions from the tails of the most recently written transcripts under
    /// ~/.claude/projects. Only the tail is read: the model a session is answering on is at its
    /// end, and a large transcript would otherwise cost a full parse at every launch.
    /// </summary>
    public static Task ScanRecentTranscriptsAsync() => Task.Run(() =>
    {
        bool changed = false;
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
            if (!Directory.Exists(root)) return;

            var files = new DirectoryInfo(root)
                .EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(ScanFileCount);

            foreach (var file in files)
            {
                foreach (Match m in TranscriptModelRegex.Matches(ReadTail(file.FullName)))
                    changed |= Record(m.Groups[1].Value);
            }
        }
        catch { /* an unreadable folder just leaves the baseline names */ }

        if (changed) Changed?.Invoke();
    });

    private static string ReadTail(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, stream.Length - ScanTailBytes);
            stream.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[stream.Length - start];
            int read = 0;
            while (read < buffer.Length)
            {
                int n = stream.Read(buffer, read, buffer.Length - read);
                if (n == 0) break;
                read += n;
            }
            return Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch { return ""; }
    }

    private static bool Record(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return false;
        if (!TryParseId(modelId, out var family, out var major, out var minor)) return false;

        lock (Gate)
        {
            if (!Latest.TryGetValue(family, out var current)) return false;
            if (!TryParseId(current, out _, out var curMajor, out var curMinor)) return false;

            var seen = new Version(major, minor ?? 0);
            if (seen <= new Version(curMajor, curMinor ?? 0)) return false;

            // Stored without the date, so the name and the settings key stay the short form
            var id = "claude-" + family + "-" + major + (minor != null ? "-" + minor : "");
            Latest[family] = id;
            return true;
        }
    }

    private static bool TryParseId(string modelId, out string family, out int major, out int? minor)
    {
        family = "";
        major = 0;
        minor = null;

        var id = modelId.Trim();
        int suffix = id.IndexOf('[');
        if (suffix > 0) id = id[..suffix];

        var m = IdRegex.Match(id);
        if (!m.Success) return false;

        family = m.Groups[1].Value.ToLowerInvariant();
        major = int.Parse(m.Groups[2].Value);
        if (m.Groups[3].Success) minor = int.Parse(m.Groups[3].Value);
        return true;
    }
}
