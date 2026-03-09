using System;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace ClaudeCodeMDI.Services;

public class UsageInfo
{
    public int TodayMessages { get; set; }
    public int TodaySessions { get; set; }
    public int TodayToolCalls { get; set; }
    public double Percentage { get; set; } // Estimated usage percentage
}

public class UsageTracker : IDisposable
{
    private Timer? _timer;
    private readonly string _statsCachePath;

    // Estimated daily message limit for Pro plan (approximate)
    private const int EstimatedDailyLimit = 1000;

    public event Action<UsageInfo>? UsageUpdated;

    public UsageTracker()
    {
        _statsCachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "stats-cache.json");
    }

    public void Start()
    {
        _timer = new Timer(_ => UpdateUsage(), null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
    }

    private void UpdateUsage()
    {
        var info = new UsageInfo();

        try
        {
            if (!File.Exists(_statsCachePath))
            {
                UsageUpdated?.Invoke(info);
                return;
            }

            string json = File.ReadAllText(_statsCachePath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string today = DateTime.Now.ToString("yyyy-MM-dd");

            if (root.TryGetProperty("dailyActivity", out var dailyArray)
                && dailyArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var day in dailyArray.EnumerateArray())
                {
                    if (day.TryGetProperty("date", out var dateProp)
                        && dateProp.GetString() == today)
                    {
                        if (day.TryGetProperty("messageCount", out var mc))
                            info.TodayMessages = mc.GetInt32();
                        if (day.TryGetProperty("sessionCount", out var sc))
                            info.TodaySessions = sc.GetInt32();
                        if (day.TryGetProperty("toolCallCount", out var tc))
                            info.TodayToolCalls = tc.GetInt32();
                        break;
                    }
                }
            }

            info.Percentage = Math.Min(100.0, (double)info.TodayMessages / EstimatedDailyLimit * 100.0);
        }
        catch
        {
            // Silently ignore errors
        }

        UsageUpdated?.Invoke(info);
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
