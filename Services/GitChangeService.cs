using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Claucraft.Services;

/// <summary>
/// One entry from `git status --porcelain`: a single changed, staged, or untracked file.
/// </summary>
public sealed class GitChange
{
    /// <summary>Path relative to the repository root, using '/' separators.</summary>
    public string Path { get; init; } = "";

    /// <summary>The raw two-character porcelain status code (XY), or "??" for untracked files.</summary>
    public string StatusCode { get; init; } = "";

    public bool Untracked { get; init; }

    public bool Staged { get; init; }

    /// <summary>File name only (last path segment).</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>Parent directory relative to the repository root, or "" if the file is at the root.</summary>
    public string DisplayDir { get; init; } = "";

    /// <summary>Single-character glyph for the status: "M" "A" "D" "R" "C" "U" "T" "?" etc.</summary>
    public string StatusGlyph { get; init; } = "";

    /// <summary>Localized display text for the status (e.g. "Modified").</summary>
    public string StatusLabel { get; init; } = "";
}

/// <summary>
/// Reads changed-file information from a git working tree via the git CLI, and produces
/// unified-diff-like text for a single file (tracked or untracked). Read-only: never mutates
/// the repository. All public methods are safe to call even when git is unavailable or the
/// folder is not a repository -- they return empty results rather than throwing.
/// </summary>
public static class GitChangeService
{
    private const int MaxDiffLines = 5000;
    private const long MaxBinaryCheckSize = 2 * 1024 * 1024; // 2 MB

    /// <summary>
    /// Lists all changed files (staged, unstaged, and untracked) in the given repository.
    /// </summary>
    public static Task<List<GitChange>> GetChangesAsync(string repoRoot)
    {
        return Task.Run(() =>
        {
            var result = new List<GitChange>();
            try
            {
                if (string.IsNullOrEmpty(repoRoot) || !Directory.Exists(repoRoot))
                    return result;

                var output = RunGit(repoRoot, "-c", "core.quotepath=false", "status", "--porcelain");
                if (string.IsNullOrEmpty(output))
                    return result;

                foreach (var rawLine in output.Split('\n'))
                {
                    var line = rawLine.TrimEnd('\r');
                    if (string.IsNullOrEmpty(line) || line.Length < 4) continue;

                    try
                    {
                        string xy = line.Substring(0, 2);
                        string rest = line.Substring(3);

                        // Renames/copies look like "old -> new"; keep the new-side path.
                        string rawPath = rest;
                        int arrowIdx = rest.IndexOf(" -> ", StringComparison.Ordinal);
                        if (arrowIdx >= 0)
                            rawPath = rest[(arrowIdx + 4)..];

                        string path = GitPath.Unquote(rawPath).Replace('\\', '/').Trim();
                        if (string.IsNullOrEmpty(path)) continue;

                        bool untracked = xy == "??";
                        bool staged = !untracked && xy[0] != ' ' && xy[0] != '?';

                        char effective = xy[0] != ' ' ? xy[0] : xy[1];
                        var (glyph, label) = DescribeStatus(effective, untracked);

                        int slash = path.LastIndexOf('/');
                        string name = slash >= 0 ? path[(slash + 1)..] : path;
                        string dir = slash >= 0 ? path[..slash] : "";

                        result.Add(new GitChange
                        {
                            Path = path,
                            StatusCode = xy,
                            Untracked = untracked,
                            Staged = staged,
                            DisplayName = name,
                            DisplayDir = dir,
                            StatusGlyph = glyph,
                            StatusLabel = label,
                        });
                    }
                    catch
                    {
                        // Skip a malformed line rather than aborting the whole listing.
                    }
                }
            }
            catch
            {
                return new List<GitChange>();
            }
            return result;
        });
    }

    /// <summary>
    /// Produces diff text for a single changed file. Tracked files combine the staged and
    /// unstaged diffs (with section headers when both are present); untracked files get a
    /// synthesized all-"+" pseudo diff. Binary files return a one-line description instead.
    /// </summary>
    public static Task<string> GetDiffAsync(string repoRoot, GitChange change)
    {
        return Task.Run(() =>
        {
            try
            {
                if (string.IsNullOrEmpty(repoRoot) || change == null || string.IsNullOrEmpty(change.Path))
                    return "";

                var fullPath = Path.Combine(repoRoot, change.Path.Replace('/', Path.DirectorySeparatorChar));

                if (change.Untracked)
                    return BuildUntrackedDiff(fullPath, change.Path);

                if (File.Exists(fullPath) && IsBinaryFile(fullPath))
                    return Loc.Get("GitDiffBinaryFile", "Binary file (diff not shown)");

                var staged = RunGit(repoRoot, "-c", "core.quotepath=false", "diff", "--cached", "--", change.Path);
                var unstaged = RunGit(repoRoot, "-c", "core.quotepath=false", "diff", "--", change.Path);

                bool hasStaged = !string.IsNullOrWhiteSpace(staged);
                bool hasUnstaged = !string.IsNullOrWhiteSpace(unstaged);

                string combined;
                if (hasStaged && hasUnstaged)
                {
                    combined = Loc.Get("GitDiffStagedHeader", "--- staged ---") + "\n"
                        + staged.TrimEnd('\n', '\r') + "\n\n"
                        + Loc.Get("GitDiffUnstagedHeader", "--- unstaged ---") + "\n"
                        + unstaged.TrimEnd('\n', '\r') + "\n";
                }
                else if (hasStaged)
                {
                    combined = staged;
                }
                else if (hasUnstaged)
                {
                    combined = unstaged;
                }
                else
                {
                    combined = "";
                }

                return TruncateDiff(combined);
            }
            catch
            {
                return "";
            }
        });
    }

    /// <summary>Returns the number of changed (staged + unstaged + untracked) files.</summary>
    public static Task<int> GetChangedCountAsync(string repoRoot)
    {
        return Task.Run(() =>
        {
            try
            {
                if (string.IsNullOrEmpty(repoRoot) || !Directory.Exists(repoRoot))
                    return 0;

                var output = RunGit(repoRoot, "-c", "core.quotepath=false", "status", "--porcelain");
                if (string.IsNullOrEmpty(output))
                    return 0;

                return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            }
            catch
            {
                return 0;
            }
        });
    }

    /// <summary>Fast, synchronous check for whether a folder is inside a git working tree.</summary>
    public static bool IsGitRepository(string folder)
    {
        try
        {
            if (string.IsNullOrEmpty(folder)) return false;
            var dir = new DirectoryInfo(folder);
            while (dir != null)
            {
                var gitEntry = Path.Combine(dir.FullName, ".git");
                if (Directory.Exists(gitEntry) || File.Exists(gitEntry))
                    return true;
                dir = dir.Parent;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    // ── Internals ──────────────────────────────────────────────────────

    private static (string Glyph, string Label) DescribeStatus(char code, bool untracked)
    {
        if (untracked)
            return ("?", Loc.Get("GitStatusUntracked", "Untracked"));

        return code switch
        {
            'M' => ("M", Loc.Get("GitStatusModified", "Modified")),
            'A' => ("A", Loc.Get("GitStatusAdded", "Added")),
            'D' => ("D", Loc.Get("GitStatusDeleted", "Deleted")),
            'R' => ("R", Loc.Get("GitStatusRenamed", "Renamed")),
            'C' => ("C", Loc.Get("GitStatusCopied", "Copied")),
            'U' => ("U", Loc.Get("GitStatusConflict", "Conflict")),
            'T' => ("T", Loc.Get("GitStatusTypeChanged", "Type Changed")),
            '!' => ("I", Loc.Get("GitStatusIgnored", "Ignored")),
            _ => ("M", Loc.Get("GitStatusModified", "Modified")),
        };
    }

    private static string BuildUntrackedDiff(string fullPath, string relPath)
    {
        try
        {
            if (!File.Exists(fullPath))
                return Loc.Get("GitDiffFileNotFound", "File not found");

            if (IsBinaryFile(fullPath))
                return Loc.Get("GitDiffBinaryFile", "Binary file (diff not shown)");

            var lines = File.ReadAllLines(fullPath, Encoding.UTF8);
            var sb = new StringBuilder();
            sb.Append("--- /dev/null\n");
            sb.Append("+++ b/").Append(relPath).Append('\n');
            sb.Append("@@ -0,0 +1,").Append(lines.Length).Append(" @@\n");
            foreach (var line in lines)
                sb.Append('+').Append(line).Append('\n');

            return TruncateDiff(sb.ToString());
        }
        catch
        {
            return "";
        }
    }

    private static bool IsBinaryFile(string fullPath)
    {
        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists) return false;
            if (info.Length > MaxBinaryCheckSize) return true;

            using var stream = File.OpenRead(fullPath);
            var buffer = new byte[8192];
            int read = stream.Read(buffer, 0, buffer.Length);
            for (int i = 0; i < read; i++)
            {
                if (buffer[i] == 0) return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static string TruncateDiff(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var lines = text.Split('\n');
        if (lines.Length <= MaxDiffLines) return text;

        int remaining = lines.Length - MaxDiffLines;
        var sb = new StringBuilder();
        for (int i = 0; i < MaxDiffLines; i++)
            sb.Append(lines[i]).Append('\n');

        var fmt = Loc.Get("GitDiffTruncatedFmt", "... truncated ({0} more lines) ...");
        try { sb.Append(string.Format(fmt, remaining)); }
        catch { sb.Append("... truncated ..."); }

        return sb.ToString();
    }

    /// <summary>Runs git with the given arguments (no shell quoting needed) and returns stdout.</summary>
    private static string RunGit(string repoRoot, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc == null) return "";
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            return output;
        }
        catch
        {
            return "";
        }
    }
}
