using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Claucraft.Services;

public enum ExtensionKind { Mcp, Skill, Plugin }

/// <summary>
/// One MCP server, skill or plugin, as the extensions panel shows it.
///
/// <see cref="Id"/> is what a toggle writes back, and it is only meaningful when
/// <see cref="CanToggle"/> is true. Everything Claucraft can see but not switch -
/// a server a plugin brings with it, a skill file on disk - is reported with
/// CanToggle false and a <see cref="Source"/> that says who owns it, so the panel
/// can point at the row that does own the switch instead of pretending it has one.
/// </summary>
public sealed record ExtensionItem
{
    public ExtensionKind Kind { get; init; }
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    /// <summary>Human-readable origin: "project", "user", or a plugin name.</summary>
    public string Source { get; init; } = "";
    /// <summary>File to open on click, when there is one.</summary>
    public string? Path { get; init; }
    public bool Enabled { get; init; }
    public bool CanToggle { get; init; }
    public string Id { get; init; } = "";
    /// <summary>Short right-hand annotation - what a plugin contributes, or a server's transport.</summary>
    public string? Detail { get; init; }
}

public sealed record ExtensionSnapshot
{
    public IReadOnlyList<ExtensionItem> Mcp { get; init; } = Array.Empty<ExtensionItem>();
    public IReadOnlyList<ExtensionItem> Skills { get; init; } = Array.Empty<ExtensionItem>();
    public IReadOnlyList<ExtensionItem> Plugins { get; init; } = Array.Empty<ExtensionItem>();
}

/// <summary>
/// Reads the MCP servers, skills and plugins a Claude Code session would load, and
/// flips the two switches that actually exist for them.
///
/// Two files hold enable state and neither is ours, so both writes go through
/// <see cref="Rewrite"/>: back up, edit the parsed tree so unknown keys survive, then
/// replace atomically. <c>~/.claude.json</c> is deliberately never written - Claude Code
/// rewrites it while sessions run, and a read-modify-write from here would lose whatever
/// it recorded in between. Servers that live only there are listed read-only.
/// </summary>
public static class ExtensionCatalog
{
    private const int MaxDescription = 220;

    public static string UserClaudeDir =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    private static string UserSettingsPath => System.IO.Path.Combine(UserClaudeDir, "settings.json");
    private static string InstalledPluginsPath =>
        System.IO.Path.Combine(UserClaudeDir, "plugins", "installed_plugins.json");
    private static string UserConfigPath =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");

    public static string ProjectSettingsPath(string projectFolder) =>
        System.IO.Path.Combine(projectFolder, ".claude", "settings.local.json");

    public static Task<ExtensionSnapshot> LoadAsync(string? projectFolder)
        => Task.Run(() => Load(projectFolder));

    private static ExtensionSnapshot Load(string? projectFolder)
    {
        var plugins = ReadPlugins();
        return new ExtensionSnapshot
        {
            Mcp = ReadMcp(projectFolder, plugins),
            Skills = ReadSkills(projectFolder, plugins),
            Plugins = plugins.Select(p => p.Item).ToList(),
        };
    }

    // ── Plugins ──

    private sealed record PluginInfo(string Id, string Name, string InstallPath, bool Enabled, ExtensionItem Item);

    private static List<PluginInfo> ReadPlugins()
    {
        var result = new List<PluginInfo>();
        var installed = ReadJson(InstalledPluginsPath)?["plugins"]?.AsObject();
        if (installed == null) return result;

        var enabledMap = ReadJson(UserSettingsPath)?["enabledPlugins"]?.AsObject();

        foreach (var entry in installed)
        {
            var id = entry.Key;
            var installPath = FirstInstallPath(entry.Value);
            if (installPath == null) continue;

            // enabledPlugins is a loose bag - the CLI keeps unrelated feature flags in it too.
            // Only a key naming an installed plugin means anything here.
            bool enabled = enabledMap?[id]?.GetValue<bool>() ?? false;

            var manifest = ReadJson(System.IO.Path.Combine(installPath, ".claude-plugin", "plugin.json"));
            var name = manifest?["name"]?.GetValue<string>() ?? id.Split('@')[0];
            var description = Trim(manifest?["description"]?.GetValue<string>());
            var version = manifest?["version"]?.GetValue<string>();

            result.Add(new PluginInfo(id, name, installPath, enabled, new ExtensionItem
            {
                Kind = ExtensionKind.Plugin,
                Name = name,
                Description = description,
                Source = id.Contains('@') ? id[(id.IndexOf('@') + 1)..] : "",
                Path = installPath,
                Enabled = enabled,
                CanToggle = true,
                Id = id,
                Detail = DescribeContents(installPath, version),
            }));
        }

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private static string? FirstInstallPath(JsonNode? value)
    {
        // The file stores a list per plugin id, one entry per scope.
        var path = value is JsonArray array
            ? array.FirstOrDefault()?["installPath"]?.GetValue<string>()
            : value?["installPath"]?.GetValue<string>();
        return path != null && Directory.Exists(path) ? path : null;
    }

    /// <summary>"v6.3.0 - 14 skills - 1 MCP" - what loading this plugin actually costs.</summary>
    private static string DescribeContents(string installPath, string? version)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(version) && version.Length <= 12) parts.Add("v" + version);

        int skills = CountSkillDirs(System.IO.Path.Combine(installPath, "skills"));
        if (skills > 0) parts.Add(string.Format(Loc.Get("NSkillsFmt"), skills));

        int commands = CountFiles(System.IO.Path.Combine(installPath, "commands"), "*.md");
        if (commands > 0) parts.Add(string.Format(Loc.Get("NCommandsFmt"), commands));

        int agents = CountFiles(System.IO.Path.Combine(installPath, "agents"), "*.md");
        if (agents > 0) parts.Add(string.Format(Loc.Get("NAgentsFmt"), agents));

        int mcp = ReadServerMap(System.IO.Path.Combine(installPath, ".mcp.json"))?.Count ?? 0;
        if (mcp > 0) parts.Add(string.Format(Loc.Get("NMcpFmt"), mcp));

        return string.Join("  ", parts);
    }

    private static int CountSkillDirs(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        try { return Directory.EnumerateFiles(dir, "SKILL.md", SearchOption.AllDirectories).Count(); }
        catch { return 0; }
    }

    private static int CountFiles(string dir, string pattern)
    {
        if (!Directory.Exists(dir)) return 0;
        try { return Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories).Count(); }
        catch { return 0; }
    }

    // ── MCP servers ──

    private static List<ExtensionItem> ReadMcp(string? projectFolder, List<PluginInfo> plugins)
    {
        var items = new List<ExtensionItem>();

        // Project .mcp.json - the one set Claucraft can switch, via .claude/settings.local.json.
        if (!string.IsNullOrEmpty(projectFolder))
        {
            var disabled = DisabledProjectServers(projectFolder);
            var servers = ReadServerMap(System.IO.Path.Combine(projectFolder, ".mcp.json"));
            if (servers != null)
            {
                foreach (var (name, node) in servers)
                {
                    items.Add(new ExtensionItem
                    {
                        Kind = ExtensionKind.Mcp,
                        Name = name,
                        Description = DescribeServer(node),
                        Source = "project",
                        Path = System.IO.Path.Combine(projectFolder, ".mcp.json"),
                        Enabled = !disabled.Contains(name),
                        CanToggle = true,
                        Id = name,
                    });
                }
            }
        }

        // ~/.claude.json - read-only on purpose, see the class comment.
        foreach (var (name, node, scope) in UserConfigServers(projectFolder))
        {
            items.Add(new ExtensionItem
            {
                Kind = ExtensionKind.Mcp,
                Name = name,
                Description = DescribeServer(node),
                Source = scope,
                Path = UserConfigPath,
                Enabled = true,
                CanToggle = false,
                Id = name,
            });
        }

        // Whatever the enabled plugins bring with them. The plugin row owns the switch.
        foreach (var plugin in plugins)
        {
            var servers = ReadServerMap(System.IO.Path.Combine(plugin.InstallPath, ".mcp.json"));
            if (servers == null) continue;
            foreach (var (name, node) in servers)
            {
                items.Add(new ExtensionItem
                {
                    Kind = ExtensionKind.Mcp,
                    Name = name,
                    Description = DescribeServer(node),
                    Source = plugin.Name,
                    Path = System.IO.Path.Combine(plugin.InstallPath, ".mcp.json"),
                    Enabled = plugin.Enabled,
                    CanToggle = false,
                    Id = plugin.Id,
                });
            }
        }

        return items;
    }

    private static IEnumerable<(string Name, JsonNode? Node, string Scope)> UserConfigServers(string? projectFolder)
    {
        var root = ReadJson(UserConfigPath);
        if (root == null) yield break;

        foreach (var (name, node) in Pairs(root["mcpServers"]?.AsObject()))
            yield return (name, node, "user");

        if (string.IsNullOrEmpty(projectFolder)) yield break;

        // The CLI keys projects by the path it was launched with, so try both separators.
        var projects = root["projects"]?.AsObject();
        if (projects == null) yield break;
        var entry = MatchProject(projects, projectFolder);
        foreach (var (name, node) in Pairs(entry?["mcpServers"]?.AsObject()))
            yield return (name, node, "user");
    }

    private static IEnumerable<(string, JsonNode?)> Pairs(JsonObject? obj)
    {
        if (obj == null) yield break;
        foreach (var kv in obj) yield return (kv.Key, kv.Value);
    }

    private static JsonNode? MatchProject(JsonObject projects, string projectFolder)
    {
        foreach (var kv in projects)
            if (SamePath(kv.Key, projectFolder)) return kv.Value;
        return null;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(a.Replace('\\', '/').TrimEnd('/'), b.Replace('\\', '/').TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Accepts both shapes in the wild: a project .mcp.json wraps its servers in
    /// "mcpServers", a plugin's .mcp.json is the bare map.
    /// </summary>
    private static Dictionary<string, JsonNode?>? ReadServerMap(string path)
    {
        var root = ReadJson(path);
        if (root == null) return null;
        var obj = root["mcpServers"]?.AsObject() ?? root.AsObject();
        var map = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var kv in obj)
            if (kv.Value is JsonObject) map[kv.Key] = kv.Value;
        return map;
    }

    private static string? DescribeServer(JsonNode? node)
    {
        if (node == null) return null;
        var type = node["type"]?.GetValue<string>();
        var url = node["url"]?.GetValue<string>();
        if (url != null) return (type ?? "http") + "  " + url;
        var command = node["command"]?.GetValue<string>();
        if (command == null) return type;
        var args = node["args"] as JsonArray;
        if (args is { Count: > 0 })
            command += " " + string.Join(" ", args.Select(a => a?.ToString() ?? ""));
        return Trim(command);
    }

    private static HashSet<string> DisabledProjectServers(string projectFolder)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var list = ReadJson(ProjectSettingsPath(projectFolder))?["disabledMcpjsonServers"] as JsonArray;
        if (list == null) return set;
        foreach (var entry in list)
            if (entry?.GetValue<string>() is { } name) set.Add(name);
        return set;
    }

    // ── Skills ──

    private static List<ExtensionItem> ReadSkills(string? projectFolder, List<PluginInfo> plugins)
    {
        var items = new List<ExtensionItem>();

        if (!string.IsNullOrEmpty(projectFolder))
            AddSkills(items, System.IO.Path.Combine(projectFolder, ".claude", "skills"), "project", true);

        AddSkills(items, System.IO.Path.Combine(UserClaudeDir, "skills"), "user", true);

        foreach (var plugin in plugins)
            AddSkills(items, System.IO.Path.Combine(plugin.InstallPath, "skills"), plugin.Name, plugin.Enabled);

        return items;
    }

    private static void AddSkills(List<ExtensionItem> items, string dir, string source, bool enabled)
    {
        if (!Directory.Exists(dir)) return;
        List<string> files;
        try { files = Directory.EnumerateFiles(dir, "SKILL.md", SearchOption.AllDirectories).ToList(); }
        catch { return; }
        files.Sort(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var (name, description) = ReadSkillHeader(file);
            items.Add(new ExtensionItem
            {
                Kind = ExtensionKind.Skill,
                Name = name ?? System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(file)) ?? "skill",
                Description = description,
                Source = source,
                Path = file,
                Enabled = enabled,
                CanToggle = false,
                Id = file,
            });
        }
    }

    /// <summary>Pulls name and description out of the YAML front matter.</summary>
    private static (string? Name, string? Description) ReadSkillHeader(string file)
    {
        var lines = new List<string>();
        try
        {
            using var reader = new StreamReader(file);
            if (reader.ReadLine()?.TrimEnd() != "---") return (null, null);

            for (int i = 0; i < 60; i++)
            {
                var line = reader.ReadLine();
                if (line == null || line.TrimEnd() == "---") break;
                lines.Add(line);
            }
        }
        catch { return (null, null); }

        return (FrontMatter(lines, "name:"), Trim(FrontMatter(lines, "description:")));
    }

    /// <summary>
    /// The value of one front-matter key. Enough YAML for the two keys we want and no more,
    /// except that a folded value ("description: &gt;") holds no text on its own line - for
    /// those the indented block underneath is what gets joined and returned.
    /// </summary>
    private static string? FrontMatter(List<string> lines, string key)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            if (!lines[i].StartsWith(key, StringComparison.Ordinal)) continue;

            var value = lines[i][key.Length..].Trim();
            if (value.Length > 0 && value[0] != '>' && value[0] != '|')
                return Unquote(value);

            var folded = new List<string>();
            for (int j = i + 1; j < lines.Count; j++)
            {
                if (lines[j].Length > 0 && !char.IsWhiteSpace(lines[j][0])) break;
                var text = lines[j].Trim();
                if (text.Length > 0) folded.Add(text);
            }
            return folded.Count > 0 ? string.Join(" ", folded) : null;
        }
        return null;
    }

    private static string? Unquote(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
            value = value[1..^1];
        return value.Length == 0 ? null : value;
    }

    private static string? Trim(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        return value.Length <= MaxDescription ? value : value[..MaxDescription] + "...";
    }

    // ── Writes ──

    /// <summary>Flips a plugin in ~/.claude/settings.json. Returns null on success, else the error.</summary>
    public static string? SetPluginEnabled(string id, bool enabled) =>
        Rewrite(UserSettingsPath, root =>
        {
            var map = root["enabledPlugins"]?.AsObject();
            if (map == null)
            {
                map = new JsonObject();
                root["enabledPlugins"] = map;
            }
            map[id] = enabled;
        });

    /// <summary>
    /// Flips a project .mcp.json server by keeping it in the project's
    /// disabledMcpjsonServers list. Returns null on success, else the error.
    /// </summary>
    public static string? SetProjectMcpEnabled(string projectFolder, string server, bool enabled) =>
        Rewrite(ProjectSettingsPath(projectFolder), root =>
        {
            var list = root["disabledMcpjsonServers"] as JsonArray;
            if (list == null)
            {
                list = new JsonArray();
                root["disabledMcpjsonServers"] = list;
            }

            for (int i = list.Count - 1; i >= 0; i--)
                if (list[i]?.GetValue<string>() == server) list.RemoveAt(i);
            if (!enabled) list.Add(server);

            // An empty allow-list is noise in a file the user also reads.
            if (list.Count == 0) root.Remove("disabledMcpjsonServers");
        });

    /// <summary>
    /// Back up, edit the parsed tree, write atomically. Editing the tree rather than
    /// re-serializing a model is what keeps every key we do not know about.
    /// </summary>
    private static string? Rewrite(string path, Action<JsonObject> edit)
    {
        try
        {
            var root = ReadJson(path)?.AsObject() ?? new JsonObject();

            if (File.Exists(path))
                File.Copy(path, path + ".claucraft-backup", overwrite: true);
            else
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

            edit(root);

            var temp = path + ".claucraft-tmp";
            using (var stream = File.Create(temp))
            {
                // Writing the node straight out keeps JsonSerializer - and the type resolver
                // it wants - out of this entirely.
                using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
                {
                    Indented = true,
                    // The files carry Japanese paths; escaping them would churn every line.
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });
                root.WriteTo(writer);
            }
            File.Move(temp, path, overwrite: true);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static JsonNode? ReadJson(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonNode.Parse(File.ReadAllText(path), null, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch { return null; }
    }
}
