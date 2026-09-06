using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Claucraft.Services;

/// <summary>What the AI made of the currently staged diff.</summary>
public enum SecretScanVerdict { Safe, Risk }

/// <summary>
/// A verdict plus, when it is <see cref="SecretScanVerdict.Risk"/>, the AI's own words about what
/// it found and where.
/// </summary>
public sealed record SecretScanResult(SecretScanVerdict Verdict, string Detail);

/// <summary>
/// Asks whichever AI CLI is selected to read the staged diff and say whether it looks safe to
/// push - a second pair of eyes for the password, API key or personal data that git status alone
/// cannot tell apart from an ordinary line of code.
///
/// This runs in the background after every stage/unstage, so a result only ever surfaces when it
/// is worth interrupting for: no one-shot preset, nothing staged, a timed-out or unparseable
/// answer all come back as null, and the panel then does nothing rather than nag with a status
/// this scan cannot actually back up.
/// </summary>
public static class SecretScanService
{
    /// <summary>Diff sent to the model per file. A single file's own change is what this must cover, not the whole staged set.</summary>
    private const int MaxDiffChars = 8000;

    private const int TimeoutMs = 60_000;

    /// <summary>How many files are scanned at once. Staging many files at a time must not fire an unbounded burst of CLI subprocesses.</summary>
    private const int MaxConcurrentScans = 3;

    /// <summary>
    /// Checks every staged file's own diff separately and aggregates the verdicts, so a large
    /// file elsewhere in the stage can never crowd a smaller one's secret out of the truncation
    /// window applied to a single combined diff.
    /// </summary>
    public static async Task<SecretScanResult?> CheckStagedAsync(string repoRoot, CliProvider provider,
        string language, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(repoRoot) || provider == null) return null;
        if (string.IsNullOrWhiteSpace(provider.OneShotArgs)) return null;

        var files = await GitWriteService.GetStagedFilePathsAsync(repoRoot);
        if (files.Length == 0) return null;

        ct.ThrowIfCancellationRequested();

        using var gate = new SemaphoreSlim(MaxConcurrentScans);
        var checks = files.Select(async path =>
        {
            await gate.WaitAsync(ct);
            try { return (path, result: await CheckFileAsync(repoRoot, provider, language, path, ct)); }
            finally { gate.Release(); }
        });
        var results = await Task.WhenAll(checks);

        ct.ThrowIfCancellationRequested();

        var risky = results.Where(r => r.result is { Verdict: SecretScanVerdict.Risk }).ToList();
        if (risky.Count == 0) return null;

        var detail = string.Join("\n\n", risky.Select(r => $"{r.path}:\n{r.result!.Detail}"));
        return new SecretScanResult(SecretScanVerdict.Risk, detail);
    }

    /// <summary>One file's own staged diff, truncated and scanned on its own.</summary>
    private static async Task<SecretScanResult?> CheckFileAsync(string repoRoot, CliProvider provider,
        string language, string path, CancellationToken ct)
    {
        var diff = await GitWriteService.GetStagedFileDiffAsync(repoRoot, path);
        if (string.IsNullOrWhiteSpace(diff)) return null;

        var prompt = BuildPrompt(language, CommitMessageService.Truncate(diff, MaxDiffChars));
        var answer = await CliOneShotRunner.RunAsync(repoRoot, provider, prompt, TimeoutMs, ct);

        return string.IsNullOrWhiteSpace(answer) ? null : ParseVerdict(answer);
    }

    // ── Prompt ─────────────────────────────────────────────────────────

    internal static string BuildPrompt(string language, string diff)
    {
        var sb = new StringBuilder();

        if (CommitMessageService.ResolveLanguage(language) == "ja")
        {
            sb.Append("あなたはセキュリティレビュー担当です。次の git diff を読み、");
            sb.Append("このままGitHubなど公開/共有リポジトリにpushして問題ないか判定してください。\n");
            sb.Append("パスワード、APIキー、トークン、秘密鍵、接続文字列、個人情報（氏名・メール・電話番号・住所など）\n");
            sb.Append("といった機微な内容が追加された行にあるかどうかを見てください。\n");
            sb.Append("出力形式（これ以外は書かない）:\n");
            sb.Append("1行目: 該当があれば RISK、なければ SAFE\n");
            sb.Append("RISK の場合のみ、2行目以降に該当箇所（ファイル名と内容の要約）を箇条書きで日本語で書く\n");
        }
        else
        {
            sb.Append("You are a security reviewer. Read the git diff below and decide whether it is\n");
            sb.Append("safe to push as-is to a public or shared repository.\n");
            sb.Append("Look for added lines containing passwords, API keys, tokens, private keys,\n");
            sb.Append("connection strings, or personal data (names, emails, phone numbers, addresses).\n");
            sb.Append("Output format (nothing else):\n");
            sb.Append("Line 1: RISK if anything qualifies, otherwise SAFE\n");
            sb.Append("Only if RISK, follow with a bullet list naming the file and what was found\n");
        }

        sb.Append("\n--- git diff ---\n");
        sb.Append(diff);
        return sb.ToString();
    }

    // ── Output ─────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the first non-blank line as the verdict. Anything that is not clearly SAFE or RISK
    /// counts as no verdict at all - a scan this app cannot act on must not become a warning that
    /// says nothing, or one that silently swallows a real answer.
    /// </summary>
    internal static SecretScanResult? ParseVerdict(string answer)
    {
        var text = answer.Replace("\r\n", "\n").Trim();
        if (text.Length == 0) return null;

        var lines = text.Split('\n');
        int first = Array.FindIndex(lines, l => !string.IsNullOrWhiteSpace(l));
        if (first < 0) return null;

        var head = lines[first].Trim();
        var rest = string.Join("\n", lines[(first + 1)..]).Trim();

        if (head.StartsWith("SAFE", StringComparison.OrdinalIgnoreCase)) return new(SecretScanVerdict.Safe, "");
        if (head.StartsWith("RISK", StringComparison.OrdinalIgnoreCase))
            return new(SecretScanVerdict.Risk, rest.Length > 0 ? rest : head);

        return null;
    }
}
