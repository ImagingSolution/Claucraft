using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Claucraft.Services;

/// <summary>
/// One thing Claude Code has chosen to remember about a project: a single Markdown file under
/// <c>~/.claude/projects/&lt;project&gt;/memory/</c>, carrying a short YAML header and a body that
/// cross-references its neighbours with <c>[[wikilinks]]</c>.
/// </summary>
public sealed class MemoryNote
{
    /// <summary>Full path of the file this was read from.</summary>
    public string FilePath { get; init; } = "";

    /// <summary>File name without the extension. This is what a <c>[[wikilink]]</c> points at.</summary>
    public string Slug { get; init; } = "";

    /// <summary>The header's <c>name</c>, falling back to <see cref="Slug"/> when absent.</summary>
    public string Name { get; init; } = "";

    /// <summary>The header's <c>description</c> - the line Claude reads when deciding relevance.</summary>
    public string Description { get; init; } = "";

    /// <summary>The header's <c>metadata.type</c>: user, feedback, project or reference.</summary>
    public string Type { get; init; } = "";

    /// <summary>Everything after the header, as written.</summary>
    public string Body { get; init; } = "";

    public DateTime Modified { get; init; }

    /// <summary>Distinct <c>[[wikilink]]</c> targets in the body, in the order they appear.</summary>
    public IReadOnlyList<string> Links { get; init; } = Array.Empty<string>();

    /// <summary>What to show in a list: the description if there is one, else the opening line.</summary>
    public string Summary =>
        !string.IsNullOrWhiteSpace(Description) ? Description : FirstBodyLine();

    private string FirstBodyLine()
    {
        foreach (var raw in Body.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 0 && !line.StartsWith("#", StringComparison.Ordinal)) return line;
        }
        return "";
    }
}

/// <summary>
/// Every memory note in one directory, read whole, with the link graph between them resolved.
///
/// The set is small by design - a few dozen files of a few kilobytes each - so this reads all of
/// them on every load rather than maintaining an index. A note whose header cannot be parsed is
/// still kept: the body is the part worth reading, and a malformed header should not hide it.
/// </summary>
public sealed class MemoryStore
{
    /// <summary>Matches <c>[[target]]</c> and <c>[[target|shown text]]</c>.</summary>
    public static readonly Regex WikiLink = new(
        @"\[\[([^\[\]\|]+)(?:\|([^\[\]]+))?\]\]", RegexOptions.Compiled);

    /// <summary>The order the panel groups by; anything else is appended after these.</summary>
    private static readonly string[] TypeOrder = { "user", "feedback", "project", "reference" };

    /// <summary>The directory these notes were read from. Empty for an empty store.</summary>
    public string Directory { get; private init; } = "";

    /// <summary>The notes, excluding the index, ordered by type then name.</summary>
    public IReadOnlyList<MemoryNote> Notes { get; private init; } = Array.Empty<MemoryNote>();

    /// <summary><c>MEMORY.md</c>, the table of contents Claude loads every session. Null if absent.</summary>
    public MemoryNote? Index { get; private init; }

    private Dictionary<string, MemoryNote> _bySlug = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<MemoryNote>> _backlinks = new(StringComparer.OrdinalIgnoreCase);

    public bool IsEmpty => Notes.Count == 0 && Index == null;

    /// <summary>An empty store, for when no project is selected or the directory does not exist.</summary>
    public static MemoryStore Empty { get; } = new();

    /// <summary>
    /// Reads every <c>*.md</c> in <paramref name="memoryDir"/>. Returns an empty store rather than
    /// throwing when the directory is missing or unreadable - a project simply may not have one.
    /// </summary>
    public static MemoryStore Load(string memoryDir)
    {
        if (string.IsNullOrWhiteSpace(memoryDir) || !System.IO.Directory.Exists(memoryDir))
            return Empty;

        var notes = new List<MemoryNote>();
        MemoryNote? index = null;

        try
        {
            foreach (var path in System.IO.Directory.EnumerateFiles(memoryDir, "*.md"))
            {
                var note = ReadNote(path);
                if (note == null) continue;

                if (string.Equals(note.Slug, "MEMORY", StringComparison.OrdinalIgnoreCase))
                    index = note;
                else
                    notes.Add(note);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        notes.Sort(Compare);

        var store = new MemoryStore
        {
            Directory = memoryDir,
            Notes = notes,
            Index = index,
        };
        store.BuildGraph();
        return store;
    }

    private static int Compare(MemoryNote a, MemoryNote b)
    {
        int ta = TypeRank(a.Type), tb = TypeRank(b.Type);
        if (ta != tb) return ta.CompareTo(tb);
        return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Where a type sits in the grouping order. Unknown types sort last, alphabetically.</summary>
    public static int TypeRank(string type)
    {
        int i = Array.FindIndex(TypeOrder, t => string.Equals(t, type, StringComparison.OrdinalIgnoreCase));
        return i >= 0 ? i : TypeOrder.Length;
    }

    private void BuildGraph()
    {
        _bySlug = new Dictionary<string, MemoryNote>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in Notes) _bySlug[n.Slug] = n;
        if (Index != null) _bySlug[Index.Slug] = Index;

        _backlinks = new Dictionary<string, List<MemoryNote>>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in All())
        {
            foreach (var target in source.Links)
            {
                if (!_backlinks.TryGetValue(target, out var list))
                    _backlinks[target] = list = new List<MemoryNote>();
                if (!list.Contains(source)) list.Add(source);
            }
        }
    }

    /// <summary>Every note including the index.</summary>
    public IEnumerable<MemoryNote> All()
    {
        if (Index != null) yield return Index;
        foreach (var n in Notes) yield return n;
    }

    /// <summary>The note a <c>[[wikilink]]</c> resolves to, or null when nothing has that name.</summary>
    public MemoryNote? BySlug(string slug) =>
        !string.IsNullOrEmpty(slug) && _bySlug.TryGetValue(Normalize(slug), out var n) ? n : null;

    /// <summary>Whether a link target exists. What tells a live link from a dangling one.</summary>
    public bool Exists(string slug) => BySlug(slug) != null;

    /// <summary>The notes that link to this one, ordered as the list is.</summary>
    public IReadOnlyList<MemoryNote> BacklinksTo(string slug)
    {
        if (string.IsNullOrEmpty(slug)) return Array.Empty<MemoryNote>();
        if (!_backlinks.TryGetValue(Normalize(slug), out var list)) return Array.Empty<MemoryNote>();
        var sorted = new List<MemoryNote>(list);
        sorted.Sort(Compare);
        return sorted;
    }

    /// <summary>
    /// This note's links that point at nothing. Claude writes these deliberately - the memory
    /// format treats a link to a note that does not exist yet as a marker for a fact worth
    /// writing later - so they are worth showing rather than hiding as errors.
    /// </summary>
    public IReadOnlyList<string> DanglingIn(MemoryNote note) =>
        note.Links.Where(l => !Exists(l)).ToList();

    /// <summary>Every dangling target anywhere in the store, each listed once.</summary>
    public IReadOnlyList<string> AllDangling()
    {
        var seen = new List<string>();
        foreach (var note in All())
            foreach (var link in note.Links)
                if (!Exists(link) && !seen.Contains(link, StringComparer.OrdinalIgnoreCase))
                    seen.Add(link);
        seen.Sort(StringComparer.OrdinalIgnoreCase);
        return seen;
    }

    /// <summary>
    /// Notes matching <paramref name="query"/>, best first. A hit on the name outranks one on the
    /// description, which outranks one in the body; within the body, more mentions rank higher.
    /// An empty query returns the full list in its usual order.
    /// </summary>
    public List<MemoryNote> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return new List<MemoryNote>(Notes);

        var q = query.Trim();
        var scored = new List<(MemoryNote Note, int Score)>();

        foreach (var note in Notes)
        {
            int score = 0;
            if (note.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) score += 1000;
            if (note.Slug.Contains(q, StringComparison.OrdinalIgnoreCase)) score += 500;
            if (note.Description.Contains(q, StringComparison.OrdinalIgnoreCase)) score += 200;

            int body = CountOccurrences(note.Body, q);
            if (body > 0) score += 20 + Math.Min(body, 10);

            if (score > 0) scored.Add((note, score));
        }

        scored.Sort((a, b) => a.Score != b.Score ? b.Score.CompareTo(a.Score) : Compare(a.Note, b.Note));
        return scored.Select(s => s.Note).ToList();
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return 0;
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }

    // ── Reading one file ───────────────────────────────────────────────

    /// <summary>A link target as written may carry the extension; the slug never does.</summary>
    private static string Normalize(string slug)
    {
        slug = slug.Trim();
        if (slug.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) slug = slug[..^3];
        return slug;
    }

    private static MemoryNote? ReadNote(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }

        var slug = Path.GetFileNameWithoutExtension(path);
        var (header, body) = SplitFrontMatter(text);

        var links = new List<string>();
        foreach (Match m in WikiLink.Matches(body))
        {
            var target = Normalize(m.Groups[1].Value);
            if (target.Length > 0 && !links.Contains(target, StringComparer.OrdinalIgnoreCase))
                links.Add(target);
        }

        DateTime modified;
        try { modified = File.GetLastWriteTime(path); }
        catch (IOException) { modified = DateTime.MinValue; }

        var name = Field(header, "name");
        return new MemoryNote
        {
            FilePath = path,
            Slug = slug,
            Name = string.IsNullOrWhiteSpace(name) ? slug : name,
            Description = Field(header, "description"),
            Type = Field(header, "type"),
            Body = body,
            Modified = modified,
            Links = links,
        };
    }

    /// <summary>
    /// Splits the leading <c>---</c> fenced YAML header off the body. A file without one is all
    /// body. No YAML parser is pulled in for this: the three fields that matter are plain scalars.
    /// </summary>
    private static (Dictionary<string, string> Header, string Body) SplitFrontMatter(string text)
    {
        var header = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var normalized = text.Replace("\r\n", "\n");

        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
            return (header, normalized);

        int end = normalized.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) return (header, normalized);

        var block = normalized[4..end];
        int bodyStart = normalized.IndexOf('\n', end + 1);
        var body = bodyStart >= 0 ? normalized[(bodyStart + 1)..] : "";

        foreach (var raw in block.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;

            int colon = line.IndexOf(':');
            if (colon <= 0) continue;

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (key.Length == 0) continue;

            // Nested keys (metadata.type and friends) are stored under their own name. Nothing in
            // this format reuses a leaf name across blocks, so the nesting need not be tracked.
            if (value.Length == 0) continue;

            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
                value = value[1..^1];

            if (!header.ContainsKey(key)) header[key] = value;
        }

        return (header, body.TrimStart('\n'));
    }

    private static string Field(Dictionary<string, string> header, string key) =>
        header.TryGetValue(key, out var v) ? v : "";
}
