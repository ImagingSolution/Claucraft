using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Claucraft.Services;

/// <summary>What a line of a diff is: unchanged, added, removed, a hunk header or git's "\ No newline" note.</summary>
public enum DiffLineKind { Context, Added, Removed, Hunk, Note }

/// <summary>One line of a file's diff, with its number on the old side, the new side, or both.</summary>
public sealed record DiffLine(DiffLineKind Kind, int? OldNo, int? NewNo, string Text);

/// <summary>One file's part of a diff.</summary>
public sealed class DiffFile
{
    public string Path { get; set; } = "";
    public string? OldPath { get; set; }
    public List<DiffLine> Lines { get; } = new();
    public int Added { get; set; }
    public int Removed { get; set; }
    public bool IsBinary { get; set; }
    public bool IsNew { get; set; }
    public bool IsDeleted { get; set; }
    /// <summary>Not known to git at all; its whole content is shown as added.</summary>
    public bool IsUntracked { get; set; }
    /// <summary>Some of its change is in the index.</summary>
    public bool IsStaged { get; set; }
}

/// <summary>Splits `git diff` output into files, hunks and numbered lines.</summary>
public static class UnifiedDiffParser
{
    private static readonly Regex HunkHeader = new(@"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@", RegexOptions.Compiled);

    public static List<DiffFile> Parse(string text)
    {
        var files = new List<DiffFile>();
        if (string.IsNullOrEmpty(text)) return files;

        DiffFile? file = null;
        bool inHunk = false;
        int oldNo = 0, newNo = 0;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw;
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                file = new DiffFile { Path = PathFromHeader(line) };
                files.Add(file);
                inHunk = false;
                continue;
            }
            if (file == null) continue;

            if (!inHunk)
            {
                if (line.StartsWith("new file mode", StringComparison.Ordinal)) file.IsNew = true;
                else if (line.StartsWith("deleted file mode", StringComparison.Ordinal)) file.IsDeleted = true;
                else if (line.StartsWith("rename from ", StringComparison.Ordinal)) file.OldPath = line[12..];
                else if (line.StartsWith("rename to ", StringComparison.Ordinal)) file.Path = line[10..];
                else if (line.StartsWith("Binary files ", StringComparison.Ordinal)) file.IsBinary = true;
                else if (line.StartsWith("+++ ", StringComparison.Ordinal))
                {
                    var p = StripPrefix(line[4..]);
                    if (p != null) file.Path = p;
                }
            }

            var m = HunkHeader.Match(line);
            if (m.Success)
            {
                inHunk = true;
                oldNo = int.Parse(m.Groups[1].Value);
                newNo = int.Parse(m.Groups[2].Value);
                file.Lines.Add(new DiffLine(DiffLineKind.Hunk, null, null, line));
                continue;
            }
            // git prefixes even an empty context line with a space, so a bare empty line is
            // only ever the one after the final newline
            if (!inHunk || line.Length == 0) continue;

            char c = line[0];
            string body = line[1..];
            switch (c)
            {
                case '+':
                    file.Lines.Add(new DiffLine(DiffLineKind.Added, null, newNo++, body));
                    file.Added++;
                    break;
                case '-':
                    file.Lines.Add(new DiffLine(DiffLineKind.Removed, oldNo++, null, body));
                    file.Removed++;
                    break;
                case '\\':
                    file.Lines.Add(new DiffLine(DiffLineKind.Note, null, null, line));
                    break;
                case ' ':
                    file.Lines.Add(new DiffLine(DiffLineKind.Context, oldNo++, newNo++, body));
                    break;
                default:
                    // Anything else ends the hunk (the next file's header is caught above)
                    inHunk = false;
                    break;
            }
        }
        return files;
    }

    /// <summary>An untracked file, shown as a new file whose every line was added.</summary>
    public static DiffFile ForUntracked(string path, string? content)
    {
        var f = new DiffFile { Path = path, IsNew = true, IsUntracked = true };
        if (content == null) { f.IsBinary = true; return f; }
        var lines = content.Replace("\r\n", "\n").Split('\n');
        int count = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
        if (count > 0) f.Lines.Add(new DiffLine(DiffLineKind.Hunk, null, null, $"@@ -0,0 +1,{count} @@"));
        for (int i = 0; i < count; i++)
            f.Lines.Add(new DiffLine(DiffLineKind.Added, null, i + 1, lines[i]));
        f.Added = count;
        return f;
    }

    private static string PathFromHeader(string header)
    {
        // "diff --git a/x b/x": the b/ side, which is the path after a rename too
        int b = header.LastIndexOf(" b/", StringComparison.Ordinal);
        if (b >= 0) return header[(b + 3)..].Trim('"');
        return header["diff --git ".Length..];
    }

    private static string? StripPrefix(string p)
    {
        p = p.Trim().Trim('"');
        if (p == "/dev/null") return null;
        return p.StartsWith("b/", StringComparison.Ordinal) || p.StartsWith("a/", StringComparison.Ordinal) ? p[2..] : p;
    }
}
