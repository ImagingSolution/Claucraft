using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Claucraft.Services;

/// <summary>What a decoration on a commit points at.</summary>
public enum GitRefKind
{
    /// <summary>A detached HEAD, pointing at no branch.</summary>
    Head,
    LocalBranch,
    RemoteBranch,
    Tag,
}

/// <summary>A branch, tag, or HEAD marker sitting on a commit.</summary>
public sealed class GitRef
{
    /// <summary>Display name: "master", "origin/master", "v0.1.0", or "HEAD" when detached.</summary>
    public string Name { get; init; } = "";

    public GitRefKind Kind { get; init; }

    /// <summary>True when HEAD is at this ref, i.e. this is the checked-out branch.</summary>
    public bool IsHead { get; init; }
}

/// <summary>One file touched by a commit.</summary>
public sealed class GitFileChange
{
    /// <summary>Path relative to the repository root, using '/' separators.</summary>
    public string Path { get; init; } = "";

    /// <summary>Single-character status: "M" "A" "D" "R" "C" "T".</summary>
    public string StatusGlyph { get; init; } = "";

    /// <summary>For a rename or copy, where the file came from; otherwise null.</summary>
    public string? OldPath { get; init; }
}

/// <summary>One commit from the log, carrying everything the graph window shows.</summary>
public sealed class GitCommit : IGraphNode
{
    public string Hash { get; init; } = "";

    public IReadOnlyList<string> Parents { get; init; } = Array.Empty<string>();

    public string Author { get; init; } = "";

    public string AuthorEmail { get; init; } = "";

    public DateTimeOffset Date { get; init; }

    /// <summary>First line of the commit message.</summary>
    public string Subject { get; init; } = "";

    /// <summary>The message below the subject line, trimmed of trailing blank lines.</summary>
    public string Body { get; init; } = "";

    public IReadOnlyList<GitRef> Refs { get; init; } = Array.Empty<GitRef>();

    public string ShortHash => Hash.Length >= 7 ? Hash[..7] : Hash;

    public bool IsMerge => Parents.Count > 1;
}

/// <summary>
/// Reads commit history from a git working tree via the git CLI. Read-only: never mutates the
/// repository. Every public method is safe to call when git is missing or the folder is not a
/// repository -- it returns an empty result rather than throwing.
/// </summary>
public static class GitLogService
{
    /// <summary>Separates the fields inside one log record. Cannot occur in git's own output.</summary>
    public const char FieldSep = '';

    /// <summary>Separates log records, so a multi-line commit body stays in one piece.</summary>
    public const char RecordSep = '';

    private const int MaxDiffLines = 5000;

    /// <summary>
    /// Hash, parents, author, email, ISO date, decoration, subject, body -- in that order,
    /// wrapped in the separators <see cref="ParseLog"/> splits on.
    /// </summary>
    private const string LogFormat =
        "%x1f%H%x1f%P%x1f%an%x1f%ae%x1f%aI%x1f%D%x1f%s%x1f%b%x1e";

    /// <summary>
    /// Reads the most recent <paramref name="maxCount"/> commits reachable from any branch, tag,
    /// or remote branch, newest first. Stashes and the app's own checkpoint refs stay out of the
    /// listing because it asks for those ref namespaces specifically rather than for --all.
    /// </summary>
    public static Task<List<GitCommit>> GetLogAsync(string repoRoot, int maxCount)
    {
        return Task.Run(() =>
        {
            try
            {
                if (string.IsNullOrEmpty(repoRoot) || !Directory.Exists(repoRoot))
                    return new List<GitCommit>();

                var output = RunGit(repoRoot,
                    "-c", "core.quotepath=false", "log",
                    "--branches", "--tags", "--remotes", "HEAD",
                    "--decorate=full", "--date-order",
                    "--max-count=" + maxCount.ToString(CultureInfo.InvariantCulture),
                    "--format=" + LogFormat);

                return ParseLog(output);
            }
            catch
            {
                return new List<GitCommit>();
            }
        });
    }

    /// <summary>
    /// Lists the files a commit touched. A merge is compared against its first parent, which is
    /// what makes its own contribution -- the conflict resolution -- visible; without that a
    /// merge lists nothing at all.
    /// </summary>
    public static Task<List<GitFileChange>> GetCommitFilesAsync(string repoRoot, string hash)
    {
        return Task.Run(() =>
        {
            try
            {
                if (string.IsNullOrEmpty(repoRoot) || string.IsNullOrEmpty(hash) || !Directory.Exists(repoRoot))
                    return new List<GitFileChange>();

                var output = RunGit(repoRoot,
                    "-c", "core.quotepath=false", "show", "--format=",
                    "--name-status", "-m", "--first-parent", hash);

                return ParseNameStatus(output);
            }
            catch
            {
                return new List<GitFileChange>();
            }
        });
    }

    /// <summary>Produces the unified diff a single file received in one commit.</summary>
    public static Task<string> GetCommitFileDiffAsync(string repoRoot, string hash, string path)
    {
        return Task.Run(() =>
        {
            try
            {
                if (string.IsNullOrEmpty(repoRoot) || string.IsNullOrEmpty(hash)
                    || string.IsNullOrEmpty(path) || !Directory.Exists(repoRoot))
                    return "";

                var output = RunGit(repoRoot,
                    "-c", "core.quotepath=false", "show", "--format=",
                    "-m", "--first-parent", hash, "--", path);

                return Truncate(output);
            }
            catch
            {
                return "";
            }
        });
    }

    /// <summary>True when the working tree has staged, unstaged, or untracked changes.</summary>
    public static Task<bool> HasUncommittedChangesAsync(string repoRoot)
    {
        return Task.Run(() =>
        {
            try
            {
                if (string.IsNullOrEmpty(repoRoot) || !Directory.Exists(repoRoot))
                    return false;

                return !string.IsNullOrWhiteSpace(
                    RunGit(repoRoot, "-c", "core.quotepath=false", "status", "--porcelain"));
            }
            catch
            {
                return false;
            }
        });
    }

    /// <summary>
    /// Turns the output of a <see cref="LogFormat"/> log into commits. Records that are blank or
    /// short of fields -- a run truncated mid-write, say -- are skipped rather than failing the lot.
    /// </summary>
    public static List<GitCommit> ParseLog(string raw)
    {
        var commits = new List<GitCommit>();
        if (string.IsNullOrEmpty(raw)) return commits;

        foreach (var record in raw.Split(RecordSep))
        {
            if (string.IsNullOrWhiteSpace(record)) continue;

            try
            {
                // The format opens with a separator, so field 1 is the hash; field 0 holds
                // only the newline left behind by the previous record's terminator.
                var parts = record.Split(FieldSep);
                if (parts.Length < 9) continue;

                var hash = parts[1].Trim();
                if (hash.Length == 0) continue;

                DateTimeOffset.TryParse(parts[5], CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date);

                // A separator inside the body would be git's own bytes, not ours; keep them.
                var body = parts.Length == 9 ? parts[8] : string.Join(FieldSep, parts, 8, parts.Length - 8);

                commits.Add(new GitCommit
                {
                    Hash = hash,
                    Parents = parts[2].Split(' ', StringSplitOptions.RemoveEmptyEntries),
                    Author = parts[3],
                    AuthorEmail = parts[4],
                    Date = date,
                    Subject = parts[7],
                    Body = body.Replace("\r\n", "\n").Trim('\n'),
                    Refs = ParseRefs(parts[6]),
                });
            }
            catch
            {
                // Skip a malformed record rather than aborting the whole log.
            }
        }

        return commits;
    }

    /// <summary>
    /// Reads a `--decorate=full` decoration such as
    /// "HEAD -&gt; refs/heads/master, refs/remotes/origin/master, tag: refs/tags/v1.0".
    /// Refs outside branches, remotes, and tags -- the app's own checkpoints among them -- are
    /// left out, as is a remote's symbolic HEAD, which only ever restates another entry.
    /// </summary>
    public static List<GitRef> ParseRefs(string decoration)
    {
        var refs = new List<GitRef>();
        if (string.IsNullOrWhiteSpace(decoration)) return refs;

        foreach (var rawToken in decoration.Split(','))
        {
            var token = rawToken.Trim();
            if (token.Length == 0) continue;

            // "HEAD -> refs/heads/master" marks the checked-out branch.
            bool isHead = false;
            int arrow = token.IndexOf("->", StringComparison.Ordinal);
            if (arrow >= 0 && token[..arrow].Trim() == "HEAD")
            {
                isHead = true;
                token = token[(arrow + 2)..].Trim();
            }

            if (token == "HEAD")
            {
                refs.Add(new GitRef { Name = "HEAD", Kind = GitRefKind.Head, IsHead = true });
                continue;
            }

            if (token.StartsWith("tag: ", StringComparison.Ordinal))
                token = token[5..].Trim();

            if (token.StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                refs.Add(new GitRef
                {
                    Name = token["refs/heads/".Length..],
                    Kind = GitRefKind.LocalBranch,
                    IsHead = isHead,
                });
            }
            else if (token.StartsWith("refs/remotes/", StringComparison.Ordinal))
            {
                var name = token["refs/remotes/".Length..];
                if (name.EndsWith("/HEAD", StringComparison.Ordinal)) continue;
                refs.Add(new GitRef { Name = name, Kind = GitRefKind.RemoteBranch, IsHead = isHead });
            }
            else if (token.StartsWith("refs/tags/", StringComparison.Ordinal))
            {
                refs.Add(new GitRef
                {
                    Name = token["refs/tags/".Length..],
                    Kind = GitRefKind.Tag,
                    IsHead = isHead,
                });
            }
        }

        return refs;
    }

    /// <summary>
    /// Reads `--name-status` output. Renames and copies are reported at their new path, with
    /// where they came from kept in <see cref="GitFileChange.OldPath"/>.
    /// </summary>
    public static List<GitFileChange> ParseNameStatus(string raw)
    {
        var files = new List<GitFileChange>();
        if (string.IsNullOrEmpty(raw)) return files;

        foreach (var rawLine in raw.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;

            var parts = line.Split('\t');
            if (parts.Length < 2 || parts[0].Length == 0) continue;

            // The score after an R or C ("R100") is not something the list needs to show.
            char code = parts[0][0];
            bool moved = (code == 'R' || code == 'C') && parts.Length >= 3;

            files.Add(new GitFileChange
            {
                StatusGlyph = code.ToString(),
                Path = GitPath.Unquote(moved ? parts[2] : parts[1]).Replace('\\', '/'),
                OldPath = moved ? GitPath.Unquote(parts[1]).Replace('\\', '/') : null,
            });
        }

        return files;
    }

    // -- Internals ------------------------------------------------------

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

    /// <summary>Caps a diff so one enormous commit cannot stall the window that renders it.</summary>
    private static string Truncate(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var lines = text.Split('\n');
        if (lines.Length <= MaxDiffLines) return text;

        var sb = new StringBuilder();
        for (int i = 0; i < MaxDiffLines; i++)
            sb.Append(lines[i]).Append('\n');

        var fmt = Loc.Get("GitDiffTruncatedFmt", "... truncated ({0} more lines) ...");
        try { sb.Append(string.Format(CultureInfo.CurrentCulture, fmt, lines.Length - MaxDiffLines)); }
        catch { sb.Append("... truncated ..."); }

        return sb.ToString();
    }
}
