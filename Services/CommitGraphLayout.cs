using System;
using System.Collections.Generic;

namespace Claucraft.Services;

/// <summary>
/// The bare shape the graph layout needs from a commit: its hash and its parents'.
/// Keeping the layout behind this interface leaves it free of any git or UI dependency.
/// </summary>
public interface IGraphNode
{
    string Hash { get; }

    IReadOnlyList<string> Parents { get; }
}

/// <summary>One commit placed on the graph: which row it sits on and which lane it occupies.</summary>
public sealed class GraphRow
{
    public IGraphNode Node { get; init; } = null!;

    /// <summary>Zero-based row index, matching the order the log returned.</summary>
    public int Row { get; init; }

    /// <summary>Zero-based lane (column) the commit's dot is drawn in.</summary>
    public int Lane { get; init; }
}

/// <summary>A line from a commit down to one of its parents.</summary>
public sealed class GraphEdge
{
    /// <summary>Row of the child commit.</summary>
    public int FromRow { get; init; }

    /// <summary>Lane of the child commit.</summary>
    public int FromLane { get; init; }

    /// <summary>
    /// Lane the line runs down between the two commits, which is neither end's lane when a
    /// second parent has to step aside around the chain its first parent occupies. Keeping it
    /// separate from <see cref="ToLane"/> is what stops such a line being drawn straight on top
    /// of that chain once the parent settles.
    /// </summary>
    public int TravelLane { get; init; }

    /// <summary>Row of the parent, or -1 when the parent falls outside the loaded range.</summary>
    public int ToRow { get; set; } = -1;

    /// <summary>
    /// Lane the edge ends in: the parent's own lane once the parent has been placed, and until
    /// then the travel lane, so a dangling line simply runs off the bottom where it is.
    /// </summary>
    public int ToLane { get; set; }

    /// <summary>
    /// Lane whose colour the edge takes. The outermost lane it touches, so that a branch keeps
    /// its own colour both where it forks off and where it merges back.
    /// </summary>
    public int ColorLane => Math.Max(TravelLane, Math.Max(FromLane, ToLane));
}

/// <summary>The laid-out graph: one row per commit, plus the lines running between them.</summary>
public sealed class CommitGraph
{
    public List<GraphRow> Rows { get; } = new();

    public List<GraphEdge> Edges { get; } = new();

    /// <summary>How many lanes wide the graph is, i.e. the highest lane used plus one.</summary>
    public int LaneCount { get; set; }
}

/// <summary>
/// Assigns each commit a lane so the history can be drawn as a graph, in the same
/// single downward pass a `git log` listing arrives in.
/// </summary>
public static class CommitGraphLayout
{
    public static CommitGraph Build(IReadOnlyList<IGraphNode> nodes)
    {
        var graph = new CommitGraph();
        if (nodes == null || nodes.Count == 0)
            return graph;

        // lanes[i] holds the hash lane i is currently waiting to reach, or null if the lane is free.
        var lanes = new List<string?>();

        // Rows are discovered before the parents they point at, so edges start with ToRow = -1
        // and get filled in when the parent turns up. Anything still waiting at the end had its
        // parent fall outside the loaded range, and stays dangling.
        var awaitingParent = new Dictionary<string, List<GraphEdge>>(StringComparer.Ordinal);
        var placed = new HashSet<string>(StringComparer.Ordinal);

        int maxLane = 0;

        foreach (var node in nodes)
        {
            if (node == null || string.IsNullOrEmpty(node.Hash)) continue;
            if (!placed.Add(node.Hash)) continue;

            int row = graph.Rows.Count;

            // Several lines can be heading for the same commit. It settles in the leftmost of
            // them and the others end here, which is what pulls a shared parent back onto the
            // trunk instead of leaving it wherever the first line to reach for it reserved it.
            int lane = lanes.IndexOf(node.Hash);
            if (lane < 0) lane = TakeFreeLane(lanes);

            for (int i = 0; i < lanes.Count; i++)
            {
                if (lanes[i] == node.Hash) lanes[i] = null;
            }

            // Only now are both ends of the incoming lines known: the row this commit landed
            // on and the lane it landed in. Each line keeps the lane it travelled down, so one
            // that stepped aside on the way here is still drawn stepping back in.
            if (awaitingParent.Remove(node.Hash, out var arrivals))
            {
                foreach (var edge in arrivals)
                {
                    edge.ToRow = row;
                    edge.ToLane = lane;
                }
            }

            graph.Rows.Add(new GraphRow { Node = node, Row = row, Lane = lane });
            if (lane > maxLane) maxLane = lane;

            var parents = node.Parents;
            for (int i = 0; parents != null && i < parents.Count; i++)
            {
                var parent = parents[i];
                if (string.IsNullOrEmpty(parent)) continue;

                int travelLane = ReserveLane(lanes, parent, lane, isFirstParent: i == 0);
                lanes[travelLane] = parent;

                // ToLane is the travel lane until the parent turns up and names its own.
                var edge = new GraphEdge
                {
                    FromRow = row,
                    FromLane = lane,
                    TravelLane = travelLane,
                    ToLane = travelLane,
                };
                graph.Edges.Add(edge);
                if (travelLane > maxLane) maxLane = travelLane;

                if (!awaitingParent.TryGetValue(parent, out var waiting))
                    awaitingParent[parent] = waiting = new List<GraphEdge>();
                waiting.Add(edge);
            }
        }

        graph.LaneCount = maxLane + 1;
        return graph;
    }

    /// <summary>
    /// Picks the lane a line to <paramref name="parent"/> travels down. A line joins one already
    /// heading for the same parent rather than opening a lane of its own, which is what keeps a
    /// commit a dozen branches share two lanes wide instead of a dozen.
    /// </summary>
    private static int ReserveLane(List<string?> lanes, string parent, int commitLane, bool isFirstParent)
    {
        int existing = lanes.IndexOf(parent);

        // The first parent carries on down this commit's own lane, and gives it up only to move
        // further left, towards the trunk. A reservation further right is left where it is: the
        // parent settles in the leftmost lane reaching it, which is this one, and the line that
        // made that reservation is drawn turning in when it gets there.
        if (isFirstParent && (existing < 0 || existing > commitLane))
            return commitLane;

        return existing >= 0 ? existing : TakeFreeLane(lanes);
    }

    /// <summary>Claims the leftmost free lane, widening the graph only when none is free.</summary>
    private static int TakeFreeLane(List<string?> lanes)
    {
        for (int i = 0; i < lanes.Count; i++)
        {
            if (lanes[i] == null) return i;
        }
        lanes.Add(null);
        return lanes.Count - 1;
    }
}
