using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Claucraft.Services;

/// <summary>Where the current branch stands against the remote it tracks.</summary>
public sealed record BranchState(string Current, int Ahead, int Behind, bool HasUpstream)
{
    public static readonly BranchState None = new("", 0, 0, false);
}

/// <summary>
/// The write half of the git integration: staging, committing, branching and pushing. Split from
/// <see cref="GitChangeService"/> so the read path stays plainly read-only, and every call here
/// reports what git said rather than swallowing it - a refused push is only useful with its
/// reason attached.
///
/// Nothing in here rewrites history or forces anything: the destructive verbs are deliberately
/// absent rather than merely unused.
/// </summary>
public static class GitWriteService
{
    /// <summary>Stages the given repository-relative paths. Untracked files included.</summary>
    public static Task<GitResult> StageAsync(string repoRoot, IReadOnlyList<string> paths)
        => RunOnPaths(repoRoot, paths, new[] { "add", "--" });

    /// <summary>
    /// Takes the given paths back out of the index, leaving the working tree alone.
    /// `restore --staged` is the safe half of the old `reset` - it cannot touch the file itself.
    /// </summary>
    public static Task<GitResult> UnstageAsync(string repoRoot, IReadOnlyList<string> paths)
        => RunOnPaths(repoRoot, paths, new[] { "restore", "--staged", "--" });

    /// <summary>
    /// Commits what is staged. The message goes in over stdin, so newlines, quotes and Japanese
    /// all survive - a message on the command line would have to get past both the shell and the
    /// console code page.
    /// </summary>
    public static Task<GitResult> CommitAsync(string repoRoot, string message)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return GitResult.Failed("not a repository");
            if (string.IsNullOrWhiteSpace(message)) return GitResult.Failed("empty message");

            return GitCli.Execute(repoRoot, message, "commit", "-F", "-");
        });
    }

    /// <summary>Local branch names, newest activity first, with the current one named separately.</summary>
    public static Task<(List<string> Branches, string Current)> GetBranchesAsync(string repoRoot)
    {
        return Task.Run(() =>
        {
            var branches = new List<string>();
            string current = "";
            if (!Usable(repoRoot)) return (branches, current);

            // %(refname:short) rather than `git branch`, whose output carries the "* " marker and
            // is decorated for a terminal.
            var output = GitCli.Run(repoRoot,
                "for-each-ref", "--sort=-committerdate", "--format=%(refname:short)", "refs/heads/");

            foreach (var line in output.Split('\n'))
            {
                var name = line.Trim();
                if (name.Length > 0) branches.Add(name);
            }

            current = GitCli.Run(repoRoot, "rev-parse", "--abbrev-ref", "HEAD").Trim();
            return (branches, current);
        });
    }

    /// <summary>Switches branches. Fails rather than discarding anything if the tree is dirty.</summary>
    public static Task<GitResult> CheckoutBranchAsync(string repoRoot, string branch)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return GitResult.Failed("not a repository");
            if (string.IsNullOrWhiteSpace(branch)) return GitResult.Failed("no branch given");

            // `switch` refuses on conflicting local changes; `checkout` would silently carry
            // them across. Refusing is the right default when the user has not asked to move
            // work between branches.
            return GitCli.Execute(repoRoot, null, "switch", "--", branch);
        });
    }

    /// <summary>Creates a branch at HEAD and switches to it.</summary>
    public static Task<GitResult> CreateBranchAsync(string repoRoot, string branch)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return GitResult.Failed("not a repository");
            if (string.IsNullOrWhiteSpace(branch)) return GitResult.Failed("no branch given");

            return GitCli.Execute(repoRoot, null, "switch", "-c", branch);
        });
    }

    /// <summary>
    /// Pushes the current branch. A branch with no upstream gets one; nothing is ever forced.
    /// </summary>
    public static Task<GitResult> PushAsync(string repoRoot, BranchState state)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return GitResult.Failed("not a repository");
            if (string.IsNullOrEmpty(state.Current)) return GitResult.Failed("no branch");

            return state.HasUpstream
                ? GitCli.Execute(repoRoot, null, "push")
                : GitCli.Execute(repoRoot, null, "push", "--set-upstream", "origin", state.Current);
        });
    }

    /// <summary>
    /// The current branch and how far it has drifted from its upstream. Reads only what is
    /// already local - no fetch, so this cannot stall on the network.
    /// </summary>
    public static Task<BranchState> GetBranchStateAsync(string repoRoot)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return BranchState.None;

            var current = GitCli.Run(repoRoot, "rev-parse", "--abbrev-ref", "HEAD").Trim();
            if (current.Length == 0 || current == "HEAD") return BranchState.None;

            var upstream = GitCli.Execute(repoRoot, null,
                "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}");
            if (!upstream.Ok) return new BranchState(current, 0, 0, false);

            // "<behind>\t<ahead>" - left is the upstream side, right is ours.
            var counts = GitCli.Run(repoRoot, "rev-list", "--left-right", "--count", "@{upstream}...HEAD");
            var parts = counts.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            int behind = parts.Length > 0 && int.TryParse(parts[0], out var b) ? b : 0;
            int ahead = parts.Length > 1 && int.TryParse(parts[1], out var a) ? a : 0;

            return new BranchState(current, ahead, behind, true);
        });
    }

    // ── Internals ──────────────────────────────────────────────────────

    private static bool Usable(string repoRoot)
        => !string.IsNullOrEmpty(repoRoot) && Directory.Exists(repoRoot);

    /// <summary>
    /// Runs one git command over a list of paths. Each goes over as a literal pathspec anchored
    /// to the top of the working tree, so a name holding '[', '*' or '?' is a name and not a
    /// pattern, and stops at the first failure so a half-applied batch is not reported as fine.
    /// </summary>
    private static Task<GitResult> RunOnPaths(string repoRoot, IReadOnlyList<string> paths, string[] verb)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return GitResult.Failed("not a repository");
            if (paths == null || paths.Count == 0) return GitResult.Failed("nothing selected");

            // Batched so a large selection cannot overrun the command line length limit.
            const int batchSize = 40;
            for (int start = 0; start < paths.Count; start += batchSize)
            {
                var args = new List<string>(verb);
                for (int i = start; i < Math.Min(start + batchSize, paths.Count); i++)
                    args.Add(GitCli.Pathspec(paths[i]));

                var result = GitCli.Execute(repoRoot, null, args.ToArray());
                if (!result.Ok) return result;
            }

            return new GitResult(0, "", "");
        });
    }
}
