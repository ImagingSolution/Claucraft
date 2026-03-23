using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Claucraft.Services;

public class WorkspaceInfo
{
    public string Layout { get; set; } = "Maximize";
    public List<WorkspaceTab> Tabs { get; set; } = new();
}

public class WorkspaceTab
{
    public string ProjectFolder { get; set; } = "";
    public string TabTitle { get; set; } = "Claude";
}

public static class WorkspaceService
{
    private static readonly string WorkspaceFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Claucraft", "workspace.json");

    public static void Save(WorkspaceInfo workspace)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(WorkspaceFile)!);
            var json = JsonSerializer.Serialize(workspace, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(WorkspaceFile, json);
        }
        catch { }
    }

    public static WorkspaceInfo? Load()
    {
        try
        {
            if (File.Exists(WorkspaceFile))
            {
                var json = File.ReadAllText(WorkspaceFile);
                return JsonSerializer.Deserialize<WorkspaceInfo>(json);
            }
        }
        catch { }
        return null;
    }
}
