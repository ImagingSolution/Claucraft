using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

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
/// Two sources, cheapest first:
///   1. The cache a statusline script leaves in %TEMP%. Claude Code's own status line refreshes
///      it every five minutes, so when one is configured this costs nothing at all - no request,
///      no credential access.
///   2. The OAuth usage endpoint, using the token Claude Code stores. Only when the cache is
///      missing or stale; the result is written back to the same file, so the two take turns
///      keeping it warm.
///
/// Every failure path ends at null, and a null readout hides the display rather than reporting
/// something wrong. The endpoint is internal to Claude Code and carries no compatibility promise,
/// so "it quietly stops showing" is the intended behaviour if it ever changes shape.
/// </summary>
public sealed class RateLimitService : IDisposable
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    private Timer? _timer;
    private int _busy;

    public event Action<RateLimitInfo?>? Updated;

    private RateLimitInfo? _latest;
    public RateLimitInfo? Current => _latest;

    /// <summary>Where the status line script keeps its copy. Shared by design, not by accident.</summary>
    private static string CachePath => Path.Combine(Path.GetTempPath(), "claude_usage_cache.json");

    private static string CredentialsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    /// <summary>
    /// Begin polling. The tick is a minute but the cache is good for five, so a fetch that
    /// actually reaches the network happens once per cache expiry, not once per tick.
    /// </summary>
    public void Start()
    {
        if (_timer != null) return;
        _timer = new Timer(_ => _ = RefreshAsync(), null, TimeSpan.Zero, TimeSpan.FromMinutes(1));
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

    private async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;

        try
        {
            var json = ReadFreshCache() ?? await FetchAsync().ConfigureAwait(false);
            _latest = json is null ? null : Parse(json);
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

    /// <summary>The cached payload if someone refreshed it recently, otherwise null.</summary>
    private static string? ReadFreshCache()
    {
        try
        {
            var file = new FileInfo(CachePath);
            if (!file.Exists) return null;
            if (DateTime.UtcNow - file.LastWriteTimeUtc > CacheTtl) return null;
            return File.ReadAllText(CachePath);
        }
        catch
        {
            // An unreadable or half-written cache just means we fetch instead.
            return null;
        }
    }

    private static async Task<string?> FetchAsync()
    {
        var token = ReadAccessToken();
        if (string.IsNullOrEmpty(token)) return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await Http.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // 401 is the ordinary case: the stored token expired and Claude Code has not
                // refreshed it yet. Nothing to report and nothing to fix from here.
                Debug.WriteLine($"[RateLimitService] Usage endpoint returned {(int)response.StatusCode}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            WriteCache(json);
            return json;
        }
        catch (Exception ex)
        {
            // Never let the message carry the request: the token travels in a header.
            Debug.WriteLine($"[RateLimitService] Usage fetch failed: {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>
    /// The OAuth access token Claude Code stores for itself. Read on demand and never kept in a
    /// field - it is a live credential, and this process has no reason to hold one.
    /// </summary>
    private static string? ReadAccessToken()
    {
        try
        {
            if (!File.Exists(CredentialsPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(CredentialsPath));
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)) return null;
            if (!oauth.TryGetProperty("accessToken", out var token)) return null;
            return token.ValueKind == JsonValueKind.String ? token.GetString() : null;
        }
        catch
        {
            // No credentials file, a shape we do not recognise, or an API-key install.
            return null;
        }
    }

    private static void WriteCache(string json)
    {
        try
        {
            File.WriteAllText(CachePath, json);
        }
        catch
        {
            // The cache is an optimisation; failing to write one costs a request next tick.
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

    private static RateLimitWindow? ReadWindow(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object) return null;

        double utilization = 0;
        if (w.TryGetProperty("utilization", out var u) && u.ValueKind == JsonValueKind.Number)
            utilization = u.GetDouble();

        DateTimeOffset? resetsAt = null;
        if (w.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(r.GetString(), out var parsed))
            resetsAt = parsed;

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
