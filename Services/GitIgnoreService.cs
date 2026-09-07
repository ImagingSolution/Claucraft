using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Claucraft.Services;

/// <summary>How one ignored entry is kept out of git's way.</summary>
public enum IgnoreKind
{
    /// <summary>
    /// A pattern in <c>.git/info/exclude</c>. Only reaches a file git does not track, and is
    /// the whole point of the mechanism: git stops listing the file at all.
    /// </summary>
    Exclude,

    /// <summary>
    /// A tracked file marked <c>skip-worktree</c>. The file stays in the repository and in every
    /// clone; what git stops reporting is this machine's edits to it.
    /// </summary>
    SkipWorktree,
}

/// <summary>One line of the ignore list: the pattern or path, and how it is being ignored.</summary>
public sealed record IgnoreEntry(string Value, IgnoreKind Kind);

/// <summary>
/// The "never show me this file again" list, kept where git itself will honour it.
///
/// A file the user does not mean to commit has to disappear from <c>git status</c>, not merely
/// from this app's own list: a view filter would still leave the file to be swept up by a
/// <c>git add -A</c> typed into the terminal. So nothing here is a display setting. Untracked
/// files get a pattern in <c>.git/info/exclude</c>; a tracked file, which no ignore pattern can
/// reach, gets the <c>skip-worktree</c> bit instead.
///
/// Both live inside <c>.git</c>, so neither is ever committed and neither reaches anyone else's
/// clone - deliberately, because "I keep a build of this in the repo root" is one machine's
/// habit, not a fact about the project. Both are also reversible from the same list.
///
/// Only the block between <see cref="BlockStart"/> and <see cref="BlockEnd"/> is ever read or
/// rewritten, so anything the user wrote in the exclude file by hand is left exactly as it was.
/// </summary>
public static class GitIgnoreService
{
    private const string BlockStart = "# >>> Claucraft ignore >>>";
    private const string BlockEnd = "# <<< Claucraft ignore <<<";

    /// <summary>
    /// Everything currently ignored: the patterns in the managed block, then the tracked files
    /// carrying the skip-worktree bit. Empty for a folder that is not a repository, or when git
    /// is unavailable.
    /// </summary>
    public static Task<List<IgnoreEntry>> ListAsync(string repoRoot) =>
        Task.Run(() => List(repoRoot));

    private static List<IgnoreEntry> List(string repoRoot)
    {
        var entries = new List<IgnoreEntry>();
        if (string.IsNullOrEmpty(repoRoot)) return entries;

        foreach (var pattern in ReadBlock(repoRoot))
            entries.Add(new IgnoreEntry(pattern, IgnoreKind.Exclude));

        try
        {
            // "ls-files -v" prefixes each index entry with a status letter; "S" is the
            // skip-worktree bit. There is no pathspec that selects only those, so the whole
            // index comes back and is filtered here.
            var output = GitCli.Run(repoRoot, "-c", "core.quotepath=false", "ls-files", "-v");
            foreach (var raw in output.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length < 3 || line[0] != 'S' || line[1] != ' ') continue;

                var path = GitPath.Unquote(line[2..]).Replace('\\', '/').Trim();
                if (path.Length > 0) entries.Add(new IgnoreEntry(path, IgnoreKind.SkipWorktree));
            }
        }
        catch
        {
            // A repository git cannot read the index of simply reports the exclude half.
        }

        return entries;
    }

    /// <summary>
    /// Adds <paramref name="paths"/> to the ignore list, choosing the mechanism per path.
    ///
    /// Anything staged is unstaged first. That is not a side effect to apologise for: the paths
    /// arriving here are the ones the user just said must not be committed, and a staged
    /// addition left in the index would be committed no matter what any ignore rule said. It
    /// also settles the classification - once the additions are out of the index, whatever git
    /// still tracks is genuinely tracked and takes the skip-worktree route.
    /// </summary>
    public static Task<GitResult> AddAsync(string repoRoot, IReadOnlyList<string> paths) =>
        Task.Run(() => Add(repoRoot, paths));

    private static GitResult Add(string repoRoot, IReadOnlyList<string> paths)
    {
        if (string.IsNullOrEmpty(repoRoot) || paths == null || paths.Count == 0)
            return new GitResult(0, "", "");

        var wanted = Normalize(paths);
        if (wanted.Count == 0) return new GitResult(0, "", "");

        try
        {
            Unstage(repoRoot, wanted);

            var tracked = TrackedAmong(repoRoot, wanted);
            var untracked = wanted.Where(p => !tracked.Contains(p)).ToList();

            if (tracked.Count > 0)
            {
                // Plain paths, not pathspecs: update-index takes file names and quietly does
                // nothing at all - printing "Ignoring path" and still exiting 0 - when handed
                // pathspec magic. Running from the top of the working tree is what makes the
                // repository-relative names resolve.
                var args = new List<string> { "update-index", "--skip-worktree", "--" };
                args.AddRange(tracked);

                var result = GitCli.Execute(repoRoot, null, args.ToArray());
                if (!result.Ok) return result;
            }

            if (untracked.Count > 0)
            {
                var patterns = ReadBlock(repoRoot).ToList();
                foreach (var path in untracked)
                {
                    var pattern = PatternFor(repoRoot, path);
                    if (!patterns.Contains(pattern, StringComparer.Ordinal)) patterns.Add(pattern);
                }

                if (!WriteBlock(repoRoot, patterns))
                    return GitResult.Failed(Loc.Get("IgnoreWriteFailed",
                        "Could not write .git/info/exclude."));
            }

            return new GitResult(0, "", "");
        }
        catch (Exception ex)
        {
            return GitResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Undoes one entry: the pattern leaves the managed block, or the skip-worktree bit is
    /// cleared and git resumes reporting that file's edits.
    /// </summary>
    public static Task<GitResult> RemoveAsync(string repoRoot, IgnoreEntry entry) =>
        Task.Run(() => Remove(repoRoot, entry));

    private static GitResult Remove(string repoRoot, IgnoreEntry entry)
    {
        if (string.IsNullOrEmpty(repoRoot) || entry == null || entry.Value.Length == 0)
            return new GitResult(0, "", "");

        try
        {
            if (entry.Kind == IgnoreKind.SkipWorktree)
                return GitCli.Execute(repoRoot, null,
                    "update-index", "--no-skip-worktree", "--", entry.Value);

            var patterns = ReadBlock(repoRoot)
                .Where(p => !string.Equals(p, entry.Value, StringComparison.Ordinal))
                .ToList();

            return WriteBlock(repoRoot, patterns)
                ? new GitResult(0, "", "")
                : GitResult.Failed(Loc.Get("IgnoreWriteFailed", "Could not write .git/info/exclude."));
        }
        catch (Exception ex)
        {
            return GitResult.Failed(ex.Message);
        }
    }

    // ── Index ──────────────────────────────────────────────────────────

    /// <summary>
    /// Takes the given paths out of the index, leaving the working tree untouched. On a
    /// repository with no commit yet there is no HEAD to reset to, so the equivalent is to drop
    /// the entries with <c>rm --cached</c> - which for a first commit is the same thing, since
    /// everything in that index is an addition.
    /// </summary>
    private static void Unstage(string repoRoot, IReadOnlyList<string> paths)
    {
        var specs = paths.Select(GitCli.Pathspec).ToArray();
        bool hasHead = GitCli.Execute(repoRoot, null, "rev-parse", "--verify", "-q", "HEAD").Ok;

        var args = new List<string>();
        if (hasHead) args.AddRange(new[] { "reset", "-q", "HEAD", "--" });
        else args.AddRange(new[] { "rm", "-q", "--cached", "--ignore-unmatch", "--" });
        args.AddRange(specs);

        // A path that was never staged makes this a no-op, so the result is not worth checking:
        // failing here must not stop the exclude half from being written.
        GitCli.Execute(repoRoot, null, args.ToArray());
    }

    /// <summary>
    /// Which of <paramref name="paths"/> git tracks. A path may name a folder - "git status"
    /// collapses an untracked folder into one row - so a path counts as tracked when the index
    /// holds it or anything beneath it.
    /// </summary>
    private static HashSet<string> TrackedAmong(string repoRoot, IReadOnlyList<string> paths)
    {
        var tracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var args = new List<string> { "-c", "core.quotepath=false", "ls-files", "--" };
        args.AddRange(paths.Select(GitCli.Pathspec));

        string output;
        try { output = GitCli.Run(repoRoot, args.ToArray()); }
        catch { return tracked; }

        var listed = output.Split('\n')
            .Select(l => GitPath.Unquote(l.TrimEnd('\r')).Replace('\\', '/').Trim())
            .Where(l => l.Length > 0)
            .ToList();

        foreach (var path in paths)
        {
            var prefix = path + "/";
            if (listed.Any(l => l.Equals(path, StringComparison.OrdinalIgnoreCase)
                    || l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                tracked.Add(path);
        }

        return tracked;
    }

    // ── The exclude file ───────────────────────────────────────────────

    /// <summary>
    /// Where this repository's exclude file is. Asked of git rather than assembled by hand,
    /// because in a worktree or a repository with a <c>.git</c> file rather than a folder the
    /// path is not the obvious one.
    /// </summary>
    private static string? ExcludePath(string repoRoot)
    {
        try
        {
            var relative = GitCli.Run(repoRoot, "rev-parse", "--git-path", "info/exclude").Trim();
            if (relative.Length == 0) return null;

            return Path.GetFullPath(Path.Combine(repoRoot,
                relative.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The patterns inside the managed block, in file order.</summary>
    private static IReadOnlyList<string> ReadBlock(string repoRoot)
    {
        var patterns = new List<string>();

        try
        {
            var file = ExcludePath(repoRoot);
            if (file == null || !File.Exists(file)) return patterns;

            bool inside = false;
            foreach (var raw in File.ReadAllLines(file))
            {
                var line = raw.Trim();
                if (line == BlockStart) { inside = true; continue; }
                if (line == BlockEnd) { inside = false; continue; }
                if (inside && line.Length > 0 && !line.StartsWith('#')) patterns.Add(line);
            }
        }
        catch
        {
            // An exclude file this process cannot read is reported as empty rather than throwing;
            // the caller's next write would fail visibly anyway.
        }

        return patterns;
    }

    /// <summary>
    /// Replaces the managed block with <paramref name="patterns"/>, leaving every other line of
    /// the file alone. An empty list removes the block and its markers rather than leaving an
    /// empty one behind.
    /// </summary>
    private static bool WriteBlock(string repoRoot, IReadOnlyList<string> patterns)
    {
        try
        {
            var file = ExcludePath(repoRoot);
            if (file == null) return false;

            var existing = File.Exists(file) ? File.ReadAllText(file) : "";
            var newline = existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

            var lines = existing.Length == 0
                ? new List<string>()
                : existing.Replace("\r\n", "\n").Split('\n').ToList();

            // A trailing newline leaves one empty element behind; dropping it here and adding the
            // newline back at the end keeps the file from growing a blank line per write.
            if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);

            int start = lines.FindIndex(l => l.Trim() == BlockStart);
            int end = lines.FindIndex(l => l.Trim() == BlockEnd);

            if (start >= 0 && end > start) lines.RemoveRange(start, end - start + 1);
            else if (start >= 0) lines.RemoveAt(start);
            else start = lines.Count;

            if (patterns.Count > 0)
            {
                int at = Math.Min(Math.Max(start, 0), lines.Count);

                var block = new List<string>();
                // One blank line off whatever the block now follows, so a hand-written exclude
                // file does not end up with the markers welded onto its last rule.
                if (at > 0 && lines[at - 1].Length > 0) block.Add("");

                block.Add(BlockStart);
                block.AddRange(patterns);
                block.Add(BlockEnd);

                lines.InsertRange(at, block);
            }
            else
            {
                // The separator the block was written with belongs to the block, so it goes out
                // with it. Otherwise a file that has been ignored and un-ignored a few times ends
                // up carrying one blank line per round trip.
                while (lines.Count > 0 && lines[^1].Trim().Length == 0)
                    lines.RemoveAt(lines.Count - 1);
            }

            var directory = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var text = lines.Count == 0 ? "" : string.Join(newline, lines) + newline;
            File.WriteAllText(file, text, new UTF8Encoding(false));
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ── Patterns ───────────────────────────────────────────────────────

    /// <summary>
    /// The exclude pattern for one repository-relative path. Anchored with a leading slash so
    /// "Claucraft.exe" in the root cannot also silence a file of that name in a subfolder, and
    /// closed with a trailing slash for a folder so it reads as one.
    /// </summary>
    private static string PatternFor(string repoRoot, string path)
    {
        bool isDirectory = false;
        try
        {
            isDirectory = Directory.Exists(Path.Combine(repoRoot,
                path.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch
        {
            // Judged as a file, which is the narrower of the two readings.
        }

        return "/" + EscapePattern(path) + (isDirectory ? "/" : "");
    }

    /// <summary>
    /// Escapes the characters gitignore reads as a pattern, so a file whose name contains '[' or
    /// '*' is matched literally. Trailing whitespace is escaped too: git drops it otherwise.
    /// </summary>
    private static string EscapePattern(string path)
    {
        var sb = new StringBuilder(path.Length + 4);
        foreach (var ch in path)
        {
            if (ch is '\\' or '*' or '?' or '[' or ']') sb.Append('\\');
            sb.Append(ch);
        }

        if (sb.Length > 0 && sb[^1] == ' ') sb.Insert(sb.Length - 1, '\\');
        return sb.ToString();
    }

    private static List<string> Normalize(IReadOnlyList<string> paths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var path = raw.Replace('\\', '/').Trim().Trim('/');
            if (path.Length == 0 || !seen.Add(path)) continue;
            result.Add(path);
        }

        return result;
    }
}
