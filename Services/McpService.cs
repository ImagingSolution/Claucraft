using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Claucraft.Services;

public enum McpStatus { Connected, NeedsAuth, Failed, Unknown }

/// <summary>One row of <c>claude mcp list</c>.</summary>
public sealed record McpServer(string Name, string Target, McpStatus Status, string StatusText)
{
    /// <summary>
    /// Connectors from claude.ai and servers a plugin brings are not in any config
    /// <c>claude mcp remove</c> can touch; they are managed where they came from.
    /// </summary>
    public bool Managed => Name.StartsWith("claude.ai ", StringComparison.Ordinal)
                        || Name.StartsWith("plugin:", StringComparison.Ordinal);
}

/// <summary>What <c>claude mcp add</c> needs for one server.</summary>
public sealed record McpAddRequest(
    string Name, string Transport, string Scope, string CommandOrUrl,
    IReadOnlyList<string> Args, IReadOnlyList<string> Env, IReadOnlyList<string> Headers);

/// <summary>
/// MCP servers through the CLI's own <c>claude mcp</c> subcommands, so the CLI stays the one
/// writer of ~/.claude.json and .mcp.json. Every call passes its values as separate arguments.
/// </summary>
public static class McpService
{
    private const int ListTimeoutMs = 90_000; // list starts every server to check its health
    private const int EditTimeoutMs = 30_000;

    private static readonly Dictionary<string, string> Env = new(StringComparer.OrdinalIgnoreCase)
    {
        // A CLI that inherits these joins the session Claucraft was started from
        ["CLAUDECODE"] = "", ["CLAUDE_CODE_ENTRYPOINT"] = "", ["CLAUDE_CODE_SESSION_ID"] = "",
        ["CLAUDE_CODE_CHILD_SESSION"] = "", ["CLAUDE_PID"] = "",
    };

    private static Task<GitResult> Run(CliProvider cli, string folder, int timeout, params string[] args)
    {
        var exe = string.IsNullOrEmpty(cli.ResolvedPath) ? cli.Exe : cli.ResolvedPath!;
        return Task.Run(() => ProcessRunner.Run(exe, folder, null, timeout, Env, args));
    }

    public static async Task<(List<McpServer> Servers, string? Error)> ListAsync(CliProvider cli, string folder)
    {
        var r = await Run(cli, folder, ListTimeoutMs, "mcp", "list");
        return r.Ok ? (ParseList(r.StdOut), null) : (new List<McpServer>(), r.Message);
    }

    public static Task<GitResult> GetAsync(CliProvider cli, string folder, string name)
        => Run(cli, folder, EditTimeoutMs, "mcp", "get", name);

    /// <summary>Without -s the CLI removes it from whichever scope holds it.</summary>
    public static Task<GitResult> RemoveAsync(CliProvider cli, string folder, string name)
        => Run(cli, folder, EditTimeoutMs, "mcp", "remove", name);

    public static Task<GitResult> AddAsync(CliProvider cli, string folder, McpAddRequest req)
        => Run(cli, folder, EditTimeoutMs, BuildAddArgs(req).ToArray());

    /// <summary>
    /// The argv for <c>claude mcp add</c>. -e and -H take any number of values, so they go last:
    /// for stdio "--" then ends them before the command, and for http/sse nothing follows them.
    /// </summary>
    internal static List<string> BuildAddArgs(McpAddRequest req)
    {
        var args = new List<string> { "mcp", "add", "-s", req.Scope, "-t", req.Transport, req.Name };
        if (req.Transport == "stdio")
        {
            foreach (var e in req.Env) { args.Add("-e"); args.Add(e); }
            args.Add("--");
            args.Add(req.CommandOrUrl);
            args.AddRange(req.Args);
        }
        else
        {
            args.Add(req.CommandOrUrl);
            foreach (var h in req.Headers) { args.Add("-H"); args.Add(h); }
        }
        return args;
    }

    /// <summary>Just a name the CLI and its config keys accept; it goes in as a positional argument.</summary>
    public static bool IsValidName(string name) => Regex.IsMatch(name, "^[A-Za-z0-9_][A-Za-z0-9_.-]*$");

    // "name: target - ✓ Connected". The name can hold colons (plugin:github:github) and the
    // target dashes, so the name ends at the first ": " and the status starts at the last " - ".
    private static readonly Regex Line = new(@"^(?<name>.+?): (?<target>.*) - (?<glyph>\S) (?<status>.+)$");

    internal static List<McpServer> ParseList(string output)
    {
        var list = new List<McpServer>();
        foreach (var raw in output.Replace("\r\n", "\n").Split('\n'))
        {
            var m = Line.Match(raw.Trim());
            if (!m.Success) continue;
            var status = m.Groups["status"].Value.Trim();
            var kind = status.StartsWith("Connected", StringComparison.OrdinalIgnoreCase) ? McpStatus.Connected
                : status.Contains("auth", StringComparison.OrdinalIgnoreCase) ? McpStatus.NeedsAuth
                : status.StartsWith("Failed", StringComparison.OrdinalIgnoreCase) ? McpStatus.Failed
                : McpStatus.Unknown;
            list.Add(new McpServer(m.Groups["name"].Value, m.Groups["target"].Value.Trim(), kind, status));
        }
        return list;
    }

    internal static List<string> Lines(string? text) =>
        (text ?? "").Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
}
