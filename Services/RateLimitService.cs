using System;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace Claucraft.Services;

/// <summary>One rate-limit window: how much of it is spent, and when it starts over.</summary>
public sealed class RateLimitWindow
{
    /// <summary>0-100. Clamped on the way in, so a display can use it directly.</summary>
    public int UtilizationPercent { get; init; }

    public DateTimeOffset? ResetsAt { get; init; }

    /// <summary>Time left until the reset, as "3d4h" / "2h30m" / "45m" / "now".</summary>
    public string ResetsIn
    {
        get
        {
            if (ResetsAt is not { } at) return "";
            var left = at - DateTimeOffset.UtcNow;
            if (left <= TimeSpan.Zero) return "now";
            if (left.TotalDays >= 1) return $"{(int)left.TotalDays}d{left.Hours}h";
            if (left.TotalHours >= 1) return $"{(int)left.TotalHours}h{left.Minutes:00}m";
            return $"{Math.Max(1, (int)left.TotalMinutes)}m";
        }
    }
}

/// <summary>The two windows the plan is actually metered on.</summary>
public sealed class RateLimitInfo
{
    public RateLimitWindow? FiveHour { get; init; }
    public RateLimitWindow? SevenDay { get; init; }

    public bool HasData => FiveHour != null || SevenDay != null;
}

/// <summary>
/// The real rate-limit readout for the signed-in account: the 5-hour and 7-day windows, with
/// the utilisation and reset time the service itself reports.
///
/// This is a different number from <see cref="UsageTracker"/>. That one counts messages in the
/// local transcripts and measures them against an assumed daily cap, which is an approximation
/// on both halves. These are the values the limit is enforced on.
///
/// Two sources, first one fresh wins:
///  1. What <see cref="StatusLineRelay"/> saved from the rate_limits Claude Code itself hands
///     to its status line. Present once a session launched with the relay has had a reply.
///  2. The cache a user's own statusline script may leave in %TEMP%.
/// With neither, the readout simply stays hidden.
///
/// Claucraft deliberately never reads Claude Code's stored OAuth token or calls Anthropic's API
/// itself: Anthropic's terms reserve Claude.ai credentials for Claude Code and Anthropic's own
/// apps, and forbid third-party tools from collecting or using them. Both sources are data Claude
/// Code or the user's own script already produced; this process only reads a file.
///
/// Every failure path ends at null, and a null readout hides the display rather than reporting
/// something wrong. The payload shape carries no compatibility promise, so "it quietly stops
/// showing" is the intended behaviour if it ever changes.
/// </summary>
public sealed class RateLimitService : IDisposable
{
    /// <summary>
    /// How old a cache may be and still be shown. The status line only runs while a session is
    /// drawing, so an idle machine lets the file age; the reset countdown stays exact regardless,
    /// and only the percentage can drift, from use elsewhere.
    /// </summary>
    private static readonly TimeSpan LegacyCacheTtl = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The relay's copy lives longer: its windows carry exact reset times, so a window past its
    /// reset is dropped on its own. The cap only bounds drift from use outside Claucraft, one
    /// five-hour window's worth.
    /// </summary>
    private static readonly TimeSpan RelayCacheTtl = TimeSpan.FromHours(5);

    private Timer? _timer;
    private int _busy;

    public event Action<RateLimitInfo?>? Updated;

    private RateLimitInfo? _latest;
    public RateLimitInfo? Current => _latest;

    /// <summary>Where a user's own status line script keeps its copy. Shared by design, not by accident.</summary>
    private static string LegacyCachePath => Path.Combine(Path.GetTempPath(), "claude_usage_cache.json");

    /// <summary>Begin polling. Each tick is one small local file read.</summary>
    public void Start()
    {
        if (_timer != null) return;
        _timer = new Timer(_ => Refresh(), null, TimeSpan.Zero, TimeSpan.FromMinutes(1));
    }

    /// <summary>Stops polling and clears the readout. Used when the active CLI is not Claude.</summary>
    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        if (_latest == null) return;
        _latest = null;
        Updated?.Invoke(null);
    }

    private void Refresh()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;

        try
        {
            _latest = Load(StatusLineRelay.CachePath, RelayCacheTtl)
                      ?? Load(LegacyCachePath, LegacyCacheTtl);
        }
        catch
        {
            // Leave the previous readout in place for this tick rather than blanking the bar
            // on one failed poll.
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }

        Updated?.Invoke(_latest);
    }

    private static RateLimitInfo? Load(string path, TimeSpan ttl)
    {
        var json = ReadFreshCache(path, ttl);
        return json is null ? null : Parse(json);
    }

    /// <summary>The cached payload if someone refreshed it recently, otherwise null.</summary>
    private static string? ReadFreshCache(string path, TimeSpan ttl)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return null;
            if (DateTime.UtcNow - file.LastWriteTimeUtc > ttl) return null;
            return File.ReadAllText(path);
        }
        catch
        {
            // An unreadable or half-written cache just hides the readout until the next tick.
            return null;
        }
    }

    private static RateLimitInfo? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var info = new RateLimitInfo
            {
                FiveHour = ReadWindow(root, "five_hour"),
                SevenDay = ReadWindow(root, "seven_day"),
            };
            return info.HasData ? info : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads either shape: Claude Code's status line gives used_percentage and resets_at as Unix
    /// seconds, the usage endpoint's cache gives utilization and an ISO timestamp.
    /// </summary>
    private static RateLimitWindow? ReadWindow(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object) return null;

        double utilization = 0;
        if ((w.TryGetProperty("used_percentage", out var u) || w.TryGetProperty("utilization", out u))
            && u.ValueKind == JsonValueKind.Number)
            utilization = u.GetDouble();

        DateTimeOffset? resetsAt = null;
        if (w.TryGetProperty("resets_at", out var r))
        {
            if (r.ValueKind == JsonValueKind.Number && r.TryGetInt64(out var epoch))
                resetsAt = DateTimeOffset.FromUnixTimeSeconds(epoch);
            else if (r.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(r.GetString(), out var parsed))
                resetsAt = parsed;
        }

        // Past its reset the window has started over, and the saved percentage belongs to the old
        // one. Claude Code drops such a window from its own payload for the same reason.
        if (resetsAt is { } at && at <= DateTimeOffset.UtcNow) return null;

        return new RateLimitWindow
        {
            UtilizationPercent = (int)Math.Clamp(Math.Round(utilization), 0, 100),
            ResetsAt = resetsAt,
        };
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
