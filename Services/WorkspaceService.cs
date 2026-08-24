using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Claucraft.Services;

/// <summary>One saved arrangement of MDI windows, restorable down to the sessions they were showing.</summary>
public class WorkspaceInfo
{
    public string Name { get; set; } = DefaultName;
    public string Layout { get; set; } = "Maximize";
    public List<WorkspaceTab> Tabs { get; set; } = new();

    public const string DefaultName = "Default";
}

public class WorkspaceTab
{
    public string ProjectFolder { get; set; } = "";
    public string TabTitle { get; set; } = "";

    /// <summary>Transcript to resume. Empty means the tab reopens as a fresh session.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>CLI the tab was running, so a workspace survives switching the active provider.</summary>
    public string ProviderId { get; set; } = "";

    /// <summary>True when the user renamed the tab, so the restored title is not overwritten.</summary>
    public bool IsManualTitle { get; set; }

    // Child window geometry, only meaningful in the Cascade layout.
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

/// <summary>Root object persisted to workspace.json.</summary>
public class WorkspaceFileData
{
    public List<WorkspaceInfo> Workspaces { get; set; } = new();
}

public static class WorkspaceService
{
    private static readonly string WorkspaceFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Claucraft", "workspace.json");

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>Stores the workspace, replacing any existing one with the same name.</summary>
    public static void Save(WorkspaceInfo workspace)
    {
        try
        {
            var data = LoadFile();
            data.Workspaces.RemoveAll(w =>
                string.Equals(w.Name, workspace.Name, StringComparison.OrdinalIgnoreCase));
            data.Workspaces.Add(workspace);

            Directory.CreateDirectory(Path.GetDirectoryName(WorkspaceFile)!);
            File.WriteAllText(WorkspaceFile, JsonSerializer.Serialize(data, WriteOptions));
        }
        catch { }
    }

    public static WorkspaceInfo? Load(string? name = null)
    {
        name ??= WorkspaceInfo.DefaultName;
        return LoadFile().Workspaces
            .FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public static List<string> Names() => LoadFile().Workspaces.Select(w => w.Name).ToList();

    public static bool Delete(string name)
    {
        try
        {
            var data = LoadFile();
            if (data.Workspaces.RemoveAll(w =>
                    string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase)) == 0)
                return false;

            File.WriteAllText(WorkspaceFile, JsonSerializer.Serialize(data, WriteOptions));
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Reads workspace.json, upgrading the pre-0.2 shape ({ Layout, Tabs }) to the named-workspace
    /// list in place so older files keep working.
    /// </summary>
    private static WorkspaceFileData LoadFile()
    {
        try
        {
            if (!File.Exists(WorkspaceFile)) return new WorkspaceFileData();

            var json = File.ReadAllText(WorkspaceFile);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("Workspaces", out _))
                return JsonSerializer.Deserialize<WorkspaceFileData>(json) ?? new WorkspaceFileData();

            var legacy = JsonSerializer.Deserialize<WorkspaceInfo>(json);
            if (legacy == null) return new WorkspaceFileData();

            legacy.Name = WorkspaceInfo.DefaultName;
            return new WorkspaceFileData { Workspaces = { legacy } };
        }
        catch { return new WorkspaceFileData(); }
    }
}
