using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Snipyard.Services;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScheduleKind { Once, Daily, Interval }

/// <summary>A prompt that opens a new session on its own at the time it is set for.</summary>
public class ScheduledTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public string ProjectFolder { get; set; } = "";
    public string Prompt { get; set; } = "";
    public ScheduleKind Kind { get; set; } = ScheduleKind.Daily;

    /// <summary>The moment for Once; only the time of day counts for Daily.</summary>
    public DateTime At { get; set; } = DateTime.Now;

    public int IntervalMinutes { get; set; } = 60;
    public bool Enabled { get; set; } = true;
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime? LastRun { get; set; }

    /// <summary>When it next fires, or null once a one-off has had its turn.</summary>
    public DateTime? NextRun()
    {
        var since = LastRun ?? Created;
        switch (Kind)
        {
            case ScheduleKind.Once:
                return LastRun == null ? At : null;
            case ScheduleKind.Interval:
                return since.AddMinutes(Math.Max(1, IntervalMinutes));
            default:
                var next = since.Date + At.TimeOfDay;
                return next > since ? next : next.AddDays(1);
        }
    }
}

/// <summary>
/// The scheduled prompts, kept in %APPDATA%\Snipyard\schedules.json. They only fire while the
/// application is running; a slot missed by more than <see cref="Grace"/> is skipped rather than
/// caught up, so opening the app in the morning does not set off last night's runs.
/// </summary>
public class ScheduleStore
{
    public List<ScheduledTask> Tasks { get; set; } = new();

    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(30);

    private static readonly string StoreFile = Path.Combine(AppPaths.Roaming, "schedules.json");

    public static ScheduleStore Shared { get; } = Load();

    public static ScheduleStore Load()
    {
        try
        {
            if (File.Exists(StoreFile))
                return JsonSerializer.Deserialize<ScheduleStore>(File.ReadAllText(StoreFile)) ?? new();
        }
        catch { }
        return new ScheduleStore();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StoreFile)!);
            File.WriteAllText(StoreFile, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    /// <summary>
    /// The tasks whose time has come. Every task that was due is marked as run, including the
    /// ones skipped for being too late, so none of them comes round again until its next slot.
    /// </summary>
    public List<ScheduledTask> TakeDue(DateTime now)
    {
        var due = new List<ScheduledTask>();
        bool changed = false;
        foreach (var task in Tasks.Where(t => t.Enabled))
        {
            var next = task.NextRun();
            if (next == null || next > now) continue;
            task.LastRun = now;
            changed = true;
            if (now - next.Value <= Grace) due.Add(task);
        }
        if (changed) Save();
        return due;
    }
}
