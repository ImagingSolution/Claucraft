using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Claucraft.Services;

/// <summary>
/// What one git invocation did: whether it succeeded, and whatever it had to say on either
/// stream. Writes need all of it - a failed commit or push is only useful to the user if the
/// reason git gave comes back with it.
/// </summary>
public sealed record GitResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;

    /// <summary>git's own words, for showing when something failed. Falls back to stdout.</summary>
    public string Message =>
        !string.IsNullOrWhiteSpace(StdErr) ? StdErr.Trim()
        : !string.IsNullOrWhiteSpace(StdOut) ? StdOut.Trim()
        : "";

    public static GitResult Failed(string message) => new(-1, "", message);
}

/// <summary>
/// The plumbing the git services share: launching the CLI, finding the top of a working tree,
/// naming a path so git cannot mistake it for a pattern, and capping diff text.
/// </summary>
public static class GitCli
{
    /// <summary>
    /// How long one git invocation may take before it is abandoned. Generous enough for a large
    /// log on a cold object cache, short enough that a wedged git cannot strand the window that
    /// is waiting on it.
    /// </summary>
    private const int TimeoutMs = 60_000;

    private const int MaxDiffLines = 5000;

    /// <summary>Runs git with the given arguments (no shell quoting needed) and returns stdout.</summary>
    public static string Run(string workingDirectory, params string[] args)
        => Execute(workingDirectory, null, args).StdOut;

    /// <summary>
    /// Runs git and reports how it went. <paramref name="stdin"/> is written to the process and
    /// the stream closed - that is how a commit message travels, so it never has to survive
    /// command-line quoting or the console code page.
    /// </summary>
    public static GitResult Execute(string workingDirectory, string? stdin, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                RedirectStandardInput = stdin != null,
            };
            if (stdin != null)
                psi.StandardInputEncoding = new UTF8Encoding(false);
            foreach (var a in args)
                psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc == null) return GitResult.Failed("git could not be started");

            if (stdin != null)
            {
                proc.StandardInput.Write(stdin);
                proc.StandardInput.Close();
            }

            // Both pipes have to be drained at once. Reading one to the end while the other
            // fills its buffer wedges git on its next write and this thread on a read that
            // never returns -- git only has to be chatty on stderr, which a repository with
            // ambiguous refs or a stale lock manages easily.
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit(TimeoutMs) || !Task.WaitAll(new Task[] { stdout, stderr }, TimeoutMs))
            {
                try
                {
                    if (!proc.HasExited) proc.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Nothing further can be done about a process that will not die.
                }
                return GitResult.Failed("git timed out");
            }

            return new GitResult(proc.ExitCode, stdout.Result, stderr.Result);
        }
        catch (Exception ex)
        {
            return GitResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// The top of the working tree <paramref name="folder"/> sits in, or null when it is not in
    /// one. Git reports paths relative to this and resolves pathspecs against the current
    /// directory, so a service handed a subfolder has to climb here first for the two to agree.
    /// </summary>
    public static string? FindRepoRoot(string folder)
    {
        try
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;

            var top = Run(folder, "rev-parse", "--show-toplevel").Trim();
            if (top.Length == 0) return null;

            return Path.GetFullPath(top.Replace('/', Path.DirectorySeparatorChar));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Wraps a repository-relative path as a pathspec git reads literally and from the top of
    /// the working tree. Without the magic it would be resolved against the process's current
    /// directory, and its glob characters -- '[', '*', '?' -- would be honoured as a pattern.
    /// </summary>
    public static string Pathspec(string path) => ":(top,literal)" + (path ?? "");

    /// <summary>Caps a diff so one enormous commit cannot stall the window that renders it.</summary>
    public static string TruncateDiff(string text)
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
