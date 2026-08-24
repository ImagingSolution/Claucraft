using System;
using System.Threading;
using System.Threading.Tasks;

namespace Claucraft.Services;

public class UsageInfo
{
    public int TodayMessages { get; set; }
    public int TodaySessions { get; set; }
    public int TodayToolCalls { get; set; }
    public double Percentage { get; set; } // Estimated usage percentage

    /// <summary>Tokens billed today across every project, all categories combined.</summary>
    public long TodayTokens { get; set; }

    /// <summary>Estimated USD spent today, using the same price table as the cost dashboard.</summary>
    public double TodayCostUsd { get; set; }

    /// <summary>Mean context re-read per turn today - the number that drives the bill.</summary>
    public long AvgContextPerTurn { get; set; }
}

/// <summary>
/// Today's usage readout, aggregated from the Claude Code session transcripts themselves.
/// This used to read ~/.claude/stats-cache.json, but that file is not present on every
/// install and the panel then silently reported zeros. The transcripts are the record of
/// what was actually billed, and CostAnalytics already parses them.
/// </summary>
public class UsageTracker : IDisposable
{
    private Timer? _timer;
    private int _busy;

    /// <summary>
    /// Messages per day the readout is measured against. Set from the plan chosen in settings
    /// (Pro / Max 5x / Max 20x); these are approximations, Anthropic does not publish exact caps.
    /// </summary>
    public static int DailyLimit { get; set; } = 1000;

    public event Action<UsageInfo>? UsageUpdated;
    public event Action? Updated;

    private UsageInfo? _latestInfo;
    public UsageInfo? GetTodayActivity() => _latestInfo;

    public void Start()
    {
        if (_timer != null) return;
        _timer = new Timer(_ => _ = UpdateUsageAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
    }

    /// <summary>Stops polling. Used when the active CLI keeps no transcripts to read.</summary>
    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private async Task UpdateUsageAsync()
    {
        // A scan slower than the tick interval must not let updates pile up on each other.
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;

        var info = new UsageInfo();
        try
        {
            var report = await CostAnalytics.BuildAsync(days: 1).ConfigureAwait(false);

            info.TodayMessages = (int)Math.Min(int.MaxValue, report.Grand.Turns);
            info.TodayToolCalls = (int)Math.Min(int.MaxValue, report.Grand.ToolCalls);
            // The window is a single day, so every session counted in it saw activity today.
            info.TodaySessions = report.SessionCount;
            info.TodayTokens = report.Grand.Total;
            info.TodayCostUsd = report.Grand.CostUsd;
            info.AvgContextPerTurn = report.Grand.AvgContextPerTurn;
            info.Percentage = Math.Min(100.0, (double)info.TodayMessages / Math.Max(1, DailyLimit) * 100.0);
        }
        catch
        {
            // Report zeros for this tick rather than tearing down the timer.
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }

        _latestInfo = info;
        UsageUpdated?.Invoke(info);
        Updated?.Invoke();
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
