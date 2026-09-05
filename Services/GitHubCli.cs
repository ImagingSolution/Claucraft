using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Claucraft.Services;

/// <summary>One open pull request, as much of it as the panel shows.</summary>
public sealed record PullRequestInfo(
    int Number,
    string Title,
    string Author,
    string HeadBranch,
    string BaseBranch,
    bool IsDraft,
    string ReviewDecision,
    string Url);

/// <summary>
/// The pull-request half of the source-control panel, driven through GitHub's own `gh` CLI.
/// Going through gh rather than the REST API means the user's existing `gh auth login` is the
/// only credential involved: Claucraft never sees or stores a token.
///
/// Every method is safe to call when gh is missing or signed out - the panel asks
/// <see cref="IsReadyAsync"/> first and hides the whole section when the answer is no.
/// </summary>
public static class GitHubCli
{
    private const int TimeoutMs = 45_000;
    private const int ListLimit = 30;

    /// <summary>
    /// Whether gh is installed and signed in. Cached: `gh auth status` reaches the network, and
    /// the answer does not change while the app is open often enough to pay for that every
    /// refresh. Null means "not asked yet".
    /// </summary>
    private static bool? _ready;

    public static Task<bool> IsReadyAsync()
    {
        if (_ready.HasValue) return Task.FromResult(_ready.Value);

        return Task.Run(() =>
        {
            // gh prints the account summary on stderr and exits non-zero when signed out, so
            // the exit code alone is the whole answer.
            var result = ProcessRunner.Run("gh", Environment.CurrentDirectory, null, TimeoutMs, null,
                "auth", "status");
            _ready = result.Ok;
            return result.Ok;
        });
    }

    /// <summary>Forgets the cached answer, so a user who signs in mid-session is noticed.</summary>
    public static void Reset() => _ready = null;

    /// <summary>
    /// Whether this repository is even on GitHub. Signed in to gh is not enough on its own: a
    /// repository with no remote, or one hosted elsewhere, has no pull requests to show, and an
    /// empty "Pull requests (0)" there reads as "none open" rather than "not applicable".
    /// Answered from the local remote list, so it costs nothing and works offline.
    /// </summary>
    public static Task<bool> HasGitHubRemoteAsync(string repoRoot)
    {
        return Task.Run(() =>
        {
            if (string.IsNullOrEmpty(repoRoot)) return false;
            var remotes = GitCli.Run(repoRoot, "remote", "-v");
            return remotes.Contains("github.com", StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>Open pull requests on the repository, newest first. Empty on any failure.</summary>
    public static Task<List<PullRequestInfo>> ListAsync(string repoRoot)
    {
        return Task.Run(() =>
        {
            var list = new List<PullRequestInfo>();
            try
            {
                var result = ProcessRunner.Run("gh", repoRoot, null, TimeoutMs, null,
                    "pr", "list",
                    "--state", "open",
                    "--limit", ListLimit.ToString(),
                    "--json", "number,title,author,headRefName,baseRefName,isDraft,reviewDecision,url");
                if (!result.Ok || string.IsNullOrWhiteSpace(result.StdOut)) return list;

                var raw = JsonSerializer.Deserialize<List<PrDto>>(result.StdOut);
                if (raw == null) return list;

                foreach (var pr in raw)
                {
                    list.Add(new PullRequestInfo(
                        pr.Number,
                        pr.Title ?? "",
                        pr.Author?.Login ?? "",
                        pr.HeadRefName ?? "",
                        pr.BaseRefName ?? "",
                        pr.IsDraft,
                        pr.ReviewDecision ?? "",
                        pr.Url ?? ""));
                }
            }
            catch
            {
                return new List<PullRequestInfo>();
            }
            return list;
        });
    }

    /// <summary>
    /// Opens a pull request from the current branch. The body goes over stdin, so newlines and
    /// Japanese survive intact; gh works out the head branch from the checkout.
    /// </summary>
    public static Task<GitResult> CreateAsync(string repoRoot, string title, string body, string baseBranch)
    {
        return Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(title)) return GitResult.Failed("no title");

            var args = new List<string> { "pr", "create", "--title", title, "--body-file", "-" };
            if (!string.IsNullOrWhiteSpace(baseBranch))
            {
                args.Add("--base");
                args.Add(baseBranch);
            }

            return ProcessRunner.Run("gh", repoRoot, body ?? "", TimeoutMs, null, args.ToArray());
        });
    }

    /// <summary>
    /// Approves a pull request. GitHub refuses to let an author approve their own, and that
    /// refusal comes back as gh's message for the panel to show - it is the correct answer,
    /// not a bug to work around.
    /// </summary>
    public static Task<GitResult> ApproveAsync(string repoRoot, int number)
    {
        return Task.Run(() => ProcessRunner.Run("gh", repoRoot, null, TimeoutMs, null,
            "pr", "review", number.ToString(), "--approve"));
    }

    /// <summary>The branch a new pull request should merge into, as GitHub has it configured.</summary>
    public static Task<string> GetDefaultBranchAsync(string repoRoot)
    {
        return Task.Run(() =>
        {
            try
            {
                var result = ProcessRunner.Run("gh", repoRoot, null, TimeoutMs, null,
                    "repo", "view", "--json", "defaultBranchRef");
                if (!result.Ok || string.IsNullOrWhiteSpace(result.StdOut)) return "";

                using var doc = JsonDocument.Parse(result.StdOut);
                if (doc.RootElement.TryGetProperty("defaultBranchRef", out var refNode) &&
                    refNode.ValueKind == JsonValueKind.Object &&
                    refNode.TryGetProperty("name", out var name))
                    return name.GetString() ?? "";
            }
            catch
            {
                // A repository gh cannot see has no default branch to report.
            }
            return "";
        });
    }

    /// <summary>Pulls the pull-request URL out of whatever gh printed when it created one.</summary>
    public static string ExtractUrl(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return "";

        foreach (var line in output.Split('\n'))
        {
            var text = line.Trim();
            if (text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return text;
        }
        return "";
    }

    // ── Wire shapes ────────────────────────────────────────────────────

    private sealed class PrDto
    {
        [JsonPropertyName("number")] public int Number { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("author")] public AuthorDto? Author { get; set; }
        [JsonPropertyName("headRefName")] public string? HeadRefName { get; set; }
        [JsonPropertyName("baseRefName")] public string? BaseRefName { get; set; }
        [JsonPropertyName("isDraft")] public bool IsDraft { get; set; }
        [JsonPropertyName("reviewDecision")] public string? ReviewDecision { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
    }

    private sealed class AuthorDto
    {
        [JsonPropertyName("login")] public string? Login { get; set; }
    }
}
