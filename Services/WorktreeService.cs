using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Claucraft.Services;

/// <summary>One entry from `git worktree list`.</summary>
public sealed record WorktreeInfo(string Path, string Branch);

/// <summary>
/// Gives a session its own checkout so two windows on one repository can work at once. Without
/// this the MDI can open any number of sessions and they all edit the same files, which is the
/// one thing parallel sessions must not do.
///
/// Worktrees live outside the repository, under the user's local app data: a checkout nested in
/// the project would be picked up by the file watcher, walked by the explorer, and offered to
/// the AI as part of its own source.
/// </summary>
public static class WorktreeService
{
    /// <summary>Branch names created here all carry this prefix, so they are recognisable later.</summary>
    public const string BranchPrefix = "claucraft/";

    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Claucraft", "worktrees");

    /// <summary>True when the folder is one of ours, so callers can keep it out of project lists.</summary>
    public static bool IsWorktreePath(string? folder)
    {
        if (string.IsNullOrEmpty(folder)) return false;
        try
        {
            var full = Path.GetFullPath(folder);
            return full.StartsWith(Root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Adds a worktree on a fresh branch cut from HEAD. The name is probed until one is free, so
    /// opening a fourth session while three are running does not collide with any of them.
    /// </summary>
    public static Task<(string? Path, GitResult Result)> CreateAsync(string repoRoot)
    {
        return Task.Run<(string?, GitResult)>(() =>
        {
            if (string.IsNullOrEmpty(repoRoot) || !Directory.Exists(repoRoot))
                return (null, GitResult.Failed("not a repository"));

            try
            {
                var repoName = Sanitize(new DirectoryInfo(repoRoot.TrimEnd(Path.DirectorySeparatorChar)).Name);
                var baseDir = Path.Combine(Root, repoName);
                Directory.CreateDirectory(baseDir);

                var existingBranches = new HashSet<string>(
                    GitCli.Run(repoRoot, "for-each-ref", "--format=%(refname:short)", "refs/heads/")
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(b => b.Trim()),
                    StringComparer.OrdinalIgnoreCase);

                string name = "", path = "";
                for (int n = 1; n <= 200; n++)
                {
                    name = "session" + (n == 1 ? "" : "-" + n);
                    path = Path.Combine(baseDir, name);
                    if (!Directory.Exists(path) && !existingBranches.Contains(BranchPrefix + name))
                        break;
                    path = "";
                }
                if (path.Length == 0)
                    return (null, GitResult.Failed("no free worktree name"));

                var result = GitCli.Execute(repoRoot, null, "worktree", "add", "-b", BranchPrefix + name, path);
                return result.Ok ? (path, result) : ((string?)null, result);
            }
            catch (Exception ex)
            {
                return (null, GitResult.Failed(ex.Message));
            }
        });
    }

    /// <summary>
    /// Re-attaches a worktree whose folder has gone but whose branch is still there - what a
    /// restored workspace needs. Returns the path when it is usable, null when it is not.
    /// </summary>
    public static Task<string?> ReattachAsync(string repoRoot, string worktreePath, string branch)
    {
        return Task.Run<string?>(() =>
        {
            if (string.IsNullOrEmpty(repoRoot) || string.IsNullOrEmpty(worktreePath)) return null;
            if (Directory.Exists(worktreePath)) return worktreePath;
            if (string.IsNullOrEmpty(branch)) return null;

            try
            {
                var known = GitCli.Run(repoRoot, "for-each-ref", "--format=%(refname:short)", "refs/heads/")
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Any(b => string.Equals(b.Trim(), branch, StringComparison.OrdinalIgnoreCase));
                if (!known) return null;

                // The registry can still hold the old entry for a folder someone deleted by hand.
                GitCli.Execute(repoRoot, null, "worktree", "prune");

                var result = GitCli.Execute(repoRoot, null, "worktree", "add", worktreePath, branch);
                return result.Ok ? worktreePath : null;
            }
            catch
            {
                return null;
            }
        });
    }

    /// <summary>
    /// Removes a worktree and, if nothing was left behind on its branch, the branch too.
    /// The branch is deleted with `-d`, which refuses when it holds unmerged commits - so work
    /// the user committed in an isolated session outlives the window it was done in.
    /// </summary>
    public static Task<GitResult> RemoveAsync(string repoRoot, string worktreePath, string? branch)
    {
        return Task.Run(() =>
        {
            if (string.IsNullOrEmpty(repoRoot) || string.IsNullOrEmpty(worktreePath))
                return GitResult.Failed("nothing to remove");

            // The CLI process has just been killed and Windows can hold its working directory
            // for a moment longer, which makes the first removal fail on a folder that is on
            // its way out. A couple of retries is the difference between a checkout that is
            // cleaned up and one that is left behind.
            GitResult result = GitResult.Failed("");
            for (int attempt = 0; attempt < 4; attempt++)
            {
                result = GitCli.Execute(repoRoot, null, "worktree", "remove", "--force", worktreePath);
                if (result.Ok || !Directory.Exists(worktreePath)) break;
                System.Threading.Thread.Sleep(250);
            }
            GitCli.Execute(repoRoot, null, "worktree", "prune");

            if (result.Ok && !string.IsNullOrEmpty(branch))
                GitCli.Execute(repoRoot, null, "branch", "-d", branch);

            return result;
        });
    }

    /// <summary>Whether the worktree has anything uncommitted, so closing it can say so.</summary>
    public static Task<bool> HasUncommittedChangesAsync(string worktreePath)
    {
        return Task.Run(() =>
        {
            if (string.IsNullOrEmpty(worktreePath) || !Directory.Exists(worktreePath)) return false;
            return GitCli.Run(worktreePath, "status", "--porcelain").Trim().Length > 0;
        });
    }

    /// <summary>Every worktree the repository knows about, the main checkout included.</summary>
    public static Task<List<WorktreeInfo>> ListAsync(string repoRoot)
    {
        return Task.Run(() =>
        {
            var list = new List<WorktreeInfo>();
            if (string.IsNullOrEmpty(repoRoot)) return list;

            string path = "", branch = "";
            foreach (var raw in GitCli.Run(repoRoot, "worktree", "list", "--porcelain").Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.StartsWith("worktree ", StringComparison.Ordinal))
                {
                    path = line[9..].Trim();
                    branch = "";
                }
                else if (line.StartsWith("branch ", StringComparison.Ordinal))
                {
                    branch = line[7..].Trim();
                    if (branch.StartsWith("refs/heads/", StringComparison.Ordinal))
                        branch = branch["refs/heads/".Length..];
                }
                else if (line.Length == 0 && path.Length > 0)
                {
                    list.Add(new WorktreeInfo(path, branch));
                    path = "";
                }
            }
            if (path.Length > 0) list.Add(new WorktreeInfo(path, branch));

            return list;
        });
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "repo" : cleaned;
    }
}
