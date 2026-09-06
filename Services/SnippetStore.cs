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

    /// <summary>Set once the starter templates have been added, so clearing the list keeps it clear.</summary>
    public bool Seeded { get; set; }

    private static readonly string StoreDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Claucraft");

    private static readonly string StoreFile = Path.Combine(StoreDir, "snippets.json");

    /// <summary>The one snippet store in the process, shared for the same reason as settings.</summary>
    public static SnippetStore Shared { get; } = Load();

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

    /// <summary>
    /// Fills an untouched store with starter prompts so the panel is not empty on first run.
    /// Prompt templates are left unsent (no trailing \r) so the user can append context;
    /// slash commands carry the \r because they are complete on their own.
    /// </summary>
    public bool SeedDefaultsIfEmpty(string language)
    {
        if (Seeded || Snippets.Count > 0) return false;

        var texts = language == "日本語" ? JapaneseStarters : EnglishStarters;
        foreach (var text in texts)
            Snippets.Add(new SnippetItem { Text = text, Order = Snippets.Count });

        Seeded = true;
        Save();
        return true;
    }

    private static readonly string[] EnglishStarters =
    {
        "Explain what this code does",
        "Find the bug and tell me the root cause before fixing it",
        "Write tests for this",
        "Refactor this without changing behavior",
        "Review this and list every problem you find",
        "Investigate this error and explain why it happens",
        "Document how to use this",
        "/compact\r",
        "/clear\r",
    };

    private static readonly string[] JapaneseStarters =
    {
        "このコードが何をしているか説明して",
        "バグを探して。直す前に原因を教えて",
        "テストを書いて",
        "動作を変えずにリファクタリングして",
        "レビューして。見つかった問題を全部挙げて",
        "このエラーの原因を調べて、なぜ起きるか説明して",
        "使い方のドキュメントを書いて",
        "/compact\r",
        "/clear\r",
    };
}
