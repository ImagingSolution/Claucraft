using System;
using System.IO;
using System.Text.Json;

namespace Claucraft.Services;

public class AppSettings
{
    public string ProjectFolder { get; set; } = "";
    public string FontFamily { get; set; } = "Cascadia Mono";
    public double FontSize { get; set; } = 14;
    public bool IsDark { get; set; } = true;
    public string Language { get; set; } = "English";
    public string InitialPrompt { get; set; } = "";
    public bool ShowWelcomePage { get; set; } = true;
    public bool EnableChartRendering { get; set; } = true;
    public string CliProviderId { get; set; } = CliProviderService.ClaudeId;

    // ── Task completion notification ──

    /// <summary>Raise a tray toast when a terminal finishes while the window is in the background.</summary>
    public bool NotifyOnComplete { get; set; } = true;

    /// <summary>Play a system sound alongside the notification.</summary>
    public bool NotifySound { get; set; } = true;

    // ── Safety net ──

    /// <summary>Snapshot the working tree before each prompt so it can be rolled back.</summary>
    public bool EnableCheckpoints { get; set; } = true;

    /// <summary>Set once the setup diagnostics have been shown, so they only auto-open on first run.</summary>
    public bool SetupDoctorShown { get; set; }

    // ── Live status readouts ──

    /// <summary>Read the terminal screen to show mode, activity and context left in the status bar.</summary>
    public bool EnableLiveStatus { get; set; } = true;

    /// <summary>Surface a banner with a suggested fix when a known CLI error shows up in the output.</summary>
    public bool EnableErrorBanner { get; set; } = true;

    /// <summary>Plan the usage readout is measured against: Pro, Max5x or Max20x.</summary>
    public string PlanTier { get; set; } = "Pro";

    /// <summary>Launch profile applied to new sessions, matched against CliProvider.Profiles.</summary>
    public string ActiveProfileId { get; set; } = CliProviderService.StandardProfileId;

    // ── Source control ──

    /// <summary>
    /// Language the AI writes commit messages in: "auto" follows the UI language, "ja" and "en"
    /// pin it. Separate from the UI setting because a team often works in one language and
    /// writes its history in another.
    /// </summary>
    public string CommitMessageLanguage { get; set; } = "auto";

    /// <summary>Fetch from the remote every few minutes while the source-control panel is open.</summary>
    public bool GitAutoFetch { get; set; } = true;

    /// <summary>
    /// Context-remaining percentage at or below which the hand-off banner appears.
    /// Continuing past this point costs more per turn than starting fresh with a brief.
    /// </summary>
    public int HandoffBannerThreshold { get; set; } = 20;

    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Claucraft");

    private static readonly string SettingsFile = Path.Combine(SettingsDir, "appsettings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var json = File.ReadAllText(SettingsFile);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFile, json);
        }
        catch { }
    }
}
