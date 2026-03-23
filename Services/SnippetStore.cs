using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Claucraft.Services;

public class SnippetItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Text { get; set; } = "";
    public int Order { get; set; }
}

public class SnippetStore
{
    public List<SnippetItem> Snippets { get; set; } = new();

    private static readonly string StoreDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Claucraft");

    private static readonly string StoreFile = Path.Combine(StoreDir, "snippets.json");

    public static SnippetStore Load()
    {
        try
        {
            if (File.Exists(StoreFile))
            {
                var json = File.ReadAllText(StoreFile);
                return JsonSerializer.Deserialize<SnippetStore>(json) ?? new SnippetStore();
            }
        }
        catch { }
        return new SnippetStore();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(StoreDir);
            for (int i = 0; i < Snippets.Count; i++)
                Snippets[i].Order = i;
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(StoreFile, json);
        }
        catch { }
    }
}
