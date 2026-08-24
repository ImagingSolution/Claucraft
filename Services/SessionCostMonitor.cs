using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Claucraft.Services;

/// <summary>What the session has cost so far and what continuing it will cost.</summary>
public sealed class TurnCost
{
    /// <summary>Billed cost of the most recent main-thread turn.</summary>
    public double LastTurnUsd { get; set; }

    /// <summary>Everything billed to this session, subagent turns included.</summary>
    public double SessionUsd { get; set; }

    /// <summary>Size of the conversation prefix as of the latest main-thread turn.</summary>
    public long ContextTokens { get; set; }

    /// <summary>Cost of re-reading that prefix once more, before any new output.</summary>
    public double NextTurnUsd { get; set; }

    /// <summary>Main-thread turns seen in this session.</summary>
    public long Turns { get; set; }

    public string Model { get; set; } = "";

    public bool HasData => Turns > 0;
}

/// <summary>
/// Tracks the marginal cost of the session attached to one terminal.
///
/// The cost dashboard reports totals after the fact; this reports the number that changes a
/// decision in the moment. Every turn re-reads the whole conversation prefix at the cache-read
/// rate, so a long session keeps paying for its own history: at 250k of context each further
/// turn costs roughly ten times what it did at 25k. Surfacing that is what makes compacting
/// or handing off to a fresh session an obvious choice rather than a guess.
///
/// Reads are incremental - only lines appended since the last call are parsed.
/// </summary>
public sealed class SessionCostMonitor
{
    private string? _path;
    private long _lastOffset;
    private TurnCost _state = new();

    public TurnCost Current => _state;

    /// <summary>The transcript currently being watched, or null when nothing is attached.</summary>
    public string? Path => _path;

    /// <summary>
    /// Point the monitor at a session transcript, discarding any previous state. The next
    /// refresh reads the file from the top so an already-running session reports a true total.
    /// </summary>
    public void Track(string? jsonlPath)
    {
        if (string.Equals(_path, jsonlPath, StringComparison.OrdinalIgnoreCase)) return;
        _path = jsonlPath;
        _lastOffset = 0;
        _state = new TurnCost();
    }

    /// <summary>
    /// Parse whatever has been appended since the last call. Returns true when the readout
    /// changed. Runs off the calling thread; safe to await from the UI.
    /// </summary>
    public Task<bool> RefreshAsync() => Task.Run(Refresh);

    private bool Refresh()
    {
        var path = _path;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;

        bool changed = false;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            // The transcript is append-only, so resume from where the last read stopped rather
            // than re-scanning the whole file. At a 700ms poll on a multi-megabyte transcript,
            // re-reading from the top would cost more than the readout is worth.
            if (stream.Length < _lastOffset) _lastOffset = 0; // truncated or replaced
            if (stream.Length == _lastOffset) return false;

            stream.Seek(_lastOffset, SeekOrigin.Begin);

            int pending = (int)Math.Min(int.MaxValue, stream.Length - _lastOffset);
            var buffer = new byte[pending];
            int read = stream.Read(buffer, 0, pending);
            if (read <= 0) return false;

            // Stop at the last complete line: the tail may be a record still being written,
            // and consuming it half-formed would lose that turn permanently.
            int lastNewline = Array.LastIndexOf(buffer, (byte)'\n', read - 1);
            if (lastNewline < 0) return false;

            var text = Encoding.UTF8.GetString(buffer, 0, lastNewline + 1);
            _lastOffset += lastNewline + 1;

            foreach (var line in text.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    if (ProcessLine(line)) changed = true;
                }
                catch
                {
                    // One malformed line must not stop the rest of the batch.
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SessionCostMonitor] Failed to read {path}: {ex.Message}");
            return false;
        }

        return changed;
    }

    private bool ProcessLine(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return false;

        if (!root.TryGetProperty("type", out var typeProp) || typeProp.GetString() != "assistant") return false;
        if (!root.TryGetProperty("message", out var msgProp) || msgProp.ValueKind != JsonValueKind.Object) return false;
        if (!msgProp.TryGetProperty("usage", out var usageProp) || usageProp.ValueKind != JsonValueKind.Object) return false;

        long input = ReadLong(usageProp, "input_tokens");
        long output = ReadLong(usageProp, "output_tokens");
        long cacheRead = ReadLong(usageProp, "cache_read_input_tokens");
        long cacheCreation = ReadLong(usageProp, "cache_creation_input_tokens");
        if (input == 0 && output == 0 && cacheRead == 0 && cacheCreation == 0) return false;

        string model = msgProp.TryGetProperty("model", out var modelProp) && modelProp.ValueKind == JsonValueKind.String
            ? modelProp.GetString() ?? "unknown"
            : "unknown";

        double cost = CostAnalytics.EstimateCostUsd(model, input, output, cacheRead, cacheCreation);

        // Subagent turns are billed to the session, so they count toward the total. They run on
        // their own much smaller context though, so letting one set the context readout would
        // make it collapse and rebound between turns.
        bool isSidechain = root.TryGetProperty("isSidechain", out var sideProp)
                           && sideProp.ValueKind == JsonValueKind.True;

        _state.SessionUsd += cost;

        if (isSidechain) return true;

        _state.Turns++;
        _state.Model = model;
        _state.LastTurnUsd = cost;
        _state.ContextTokens = input + cacheRead + cacheCreation;
        _state.NextTurnUsd = CostAnalytics.EstimateNextTurnCostUsd(model, _state.ContextTokens);
        return true;
    }

    private static long ReadLong(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var prop)) return 0;
        return prop.ValueKind switch
        {
            JsonValueKind.Number => prop.TryGetInt64(out var v) ? v : (long)prop.GetDouble(),
            _ => 0,
        };
    }
}
