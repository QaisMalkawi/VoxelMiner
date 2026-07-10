using System.Numerics;

namespace VoxelMiner.Entities;

using VoxelMiner.World;

/// Grid pathfinding over voxel standing-positions for ground creatures.
public static class Pathfinding
{
    /// A creature can stand at (x, y, z) if there's floor below and 2 blocks of air.
    public static bool IsStandable(GameWorld w, int x, int y, int z) =>
        w.IsSolidAt(x, y - 1, z) && !w.IsSolidAt(x, y, z) && !w.IsSolidAt(x, y + 1, z);

    static readonly (int Dx, int Dz)[] NeighborDirs = { (1, 0), (-1, 0), (0, 1), (0, -1) };
    static readonly int[] StepHeights = { 0, 1, -1, -2, -3 }; // flat first, then hop up, then drops
    const int MaxVisited = 400;

    sealed class Node
    {
        public int X, Y, Z;
        public double G, F;
        public Node Prev;
    }

    /// A*: hop up 1 block, drop up to 3. Returns node list or null.
    public static List<(int X, int Y, int Z)> FindPath(GameWorld w, int sx, int sy, int sz, int tx, int ty, int tz)
    {
        var open = new List<Node> { new() { X = sx, Y = sy, Z = sz } };
        var best = new Dictionary<(int, int, int), double> { [(sx, sy, sz)] = 0 };
        int visited = 0;
        while (open.Count > 0)
        {
            int mi = 0;
            for (int i = 1; i < open.Count; i++) if (open[i].F < open[mi].F) mi = i;
            var cur = open[mi];
            open.RemoveAt(mi);
            if (cur.X == tx && cur.Z == tz && Math.Abs(cur.Y - ty) <= 1)
            {
                var path = new List<(int, int, int)>();
                for (var n = cur; n != null; n = n.Prev) path.Insert(0, (n.X, n.Y, n.Z));
                return path;
            }
            if (++visited > MaxVisited) return null;
            foreach (var (dx, dz) in NeighborDirs)
            {
                int nx = cur.X + dx, nz = cur.Z + dz;
                foreach (int dy in StepHeights)
                {
                    int ny = cur.Y + dy;
                    if (dy == 1 && w.IsSolidAt(cur.X, cur.Y + 2, cur.Z)) continue; // no headroom to hop
                    if (dy < 0 && (w.IsSolidAt(nx, cur.Y, nz) || w.IsSolidAt(nx, cur.Y + 1, nz))) break; // wall
                    if (!IsStandable(w, nx, ny, nz)) continue;
                    double g = cur.G + 1 + Math.Abs(dy) * 0.4;
                    var key = (nx, ny, nz);
                    if (best.TryGetValue(key, out double bg) && bg <= g) break;
                    best[key] = g;
                    open.Add(new Node
                    {
                        X = nx, Y = ny, Z = nz, G = g,
                        F = g + Math.Abs(nx - tx) + Math.Abs(nz - tz) + Math.Abs(ny - ty) * 0.5,
                        Prev = cur,
                    });
                    break; // one landing spot per column
                }
            }
        }
        return null;
    }

    /// Height of the walking surface at/below a position, or null if none nearby.
    public static float? GroundHeightBelow(GameWorld w, Vector3 pos, int maxDrop = 8)
    {
        int bx = (int)MathF.Floor(pos.X), bz = (int)MathF.Floor(pos.Z);
        int top = (int)MathF.Floor(pos.Y + 0.1f);
        for (int by = top; by >= Math.Max(0, (int)MathF.Floor(pos.Y) - maxDrop); by--)
            if (w.IsSolidAt(bx, by, bz)) return by + 1;
        return null;
    }
}
