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

    /// <summary>Row of the parent, or -1 when the parent falls outside the loaded range.</summary>
    public int ToRow { get; set; } = -1;

    /// <summary>Lane the edge settles into on its way down to the parent.</summary>
    public int ToLane { get; init; }

    /// <summary>
    /// Lane whose colour the edge takes. The outermost of the two lanes, so that a branch
    /// keeps its own colour both where it forks off and where it merges back.
    /// </summary>
    public int ColorLane => FromLane > ToLane ? FromLane : ToLane;
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

            // The commit sits in whichever lane was waiting for it; a tip nothing waits for
            // starts a lane of its own.
            int lane = lanes.IndexOf(node.Hash);
            if (lane < 0) lane = TakeFreeLane(lanes);

            // Release the claim before walking the parents, so the first parent can inherit it.
            lanes[lane] = null;

            if (awaitingParent.Remove(node.Hash, out var arrivals))
            {
                foreach (var edge in arrivals)
                    edge.ToRow = row;
            }

            graph.Rows.Add(new GraphRow { Node = node, Row = row, Lane = lane });
            if (lane > maxLane) maxLane = lane;

            var parents = node.Parents;
            for (int i = 0; parents != null && i < parents.Count; i++)
            {
                var parent = parents[i];
                if (string.IsNullOrEmpty(parent)) continue;

                int toLane = lanes.IndexOf(parent);
                if (toLane < 0)
                {
                    // The first parent continues this commit's line; the rest branch out sideways.
                    toLane = i == 0 && lanes[lane] == null ? lane : TakeFreeLane(lanes);
                    lanes[toLane] = parent;
                }

                var edge = new GraphEdge { FromRow = row, FromLane = lane, ToLane = toLane };
                graph.Edges.Add(edge);
                if (toLane > maxLane) maxLane = toLane;

                if (!awaitingParent.TryGetValue(parent, out var waiting))
                    awaitingParent[parent] = waiting = new List<GraphEdge>();
                waiting.Add(edge);
            }
        }

        graph.LaneCount = maxLane + 1;
        return graph;
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
