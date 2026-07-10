using System.Collections.Concurrent;

namespace VoxelMiner.World;

using VoxelMiner.Core;

/// Minecraft-style water flow. A falling column drops straight down through
/// open air immediately — real gravity isn't paced, so a shaft never shows a
/// half-fallen column disconnected from what's below it — but once it lands,
/// spreading sideways across a floor is paced one BFS layer per tick (falling
/// resets the spread budget), so mining into the ocean floods nearby caves
/// gradually without filling the whole map at once.
public sealed class Fluids
{
    const float TickInterval = 0.2f;
    const int MaxCellsPerTick = 96;
    const int MaxSideSteps = 7;

    readonly GameWorld _world;
    readonly Queue<(int X, int Y, int Z)> _active = new();
    readonly HashSet<(int, int, int)> _queued = new();
    // generated ocean water reads as 0; concurrent because mesh workers
    // query LevelAt while flow ticks write on the main thread
    readonly ConcurrentDictionary<(int, int, int), int> _flowDist = new();
    readonly HashSet<(int Cx, int Cz)> _changed = new();
    float _timer;

    public Fluids(GameWorld world) => _world = world;

    /// Call after a block was broken so adjacent water starts flowing.
    public void Wake(int x, int y, int z)
    {
        Activate(x, y + 1, z);
        Activate(x - 1, y, z);
        Activate(x + 1, y, z);
        Activate(x, y, z - 1);
        Activate(x, y, z + 1);
    }

    /// Queues a cell to tick if it currently holds water. No-ops otherwise
    /// (e.g. a cell that isn't water, or is already queued).
    public void Activate(int x, int y, int z)
    {
        if (_world.GetBlock(x, y, z) != BlockId.Water) return;
        if (_queued.Add((x, y, z))) _active.Enqueue((x, y, z));
    }

    public void Update(float dt)
    {
        if (_active.Count == 0) { _timer = 0; return; }
        _timer += dt;
        if (_timer < TickInterval) return;
        _timer = 0;

        _changed.Clear();
        int n = Math.Min(_active.Count, MaxCellsPerTick);
        for (int i = 0; i < n; i++)
        {
            var (x, y, z) = _active.Dequeue();
            _queued.Remove((x, y, z));
            if (_world.GetBlock(x, y, z) != BlockId.Water) continue;
            ProcessCell(x, y, z);
        }
        if (_changed.Count > 0) _world.NotifyChanged(_changed);
    }

    /// Drops straight down through any open air to its resting position in
    /// one shot, then — only from that final spot — spreads sideways one
    /// step (further spreading from the newly-filled cells stays paced,
    /// since each just gets queued for a later tick like any other cell).
    void ProcessCell(int x, int y, int z)
    {
        int fy = y;
        while (_world.GetBlock(x, fy - 1, z) == BlockId.Air)
        {
            Fill(x, fy - 1, z, 0);
            fy--;
        }

        if (_world.GetBlock(x, fy - 1, z) == BlockId.Water) return; // merged into water below
        int dist = _flowDist.GetValueOrDefault((x, fy, z), 0);
        if (dist >= MaxSideSteps) return;
        if (_world.GetBlock(x - 1, fy, z) == BlockId.Air) Fill(x - 1, fy, z, dist + 1);
        if (_world.GetBlock(x + 1, fy, z) == BlockId.Air) Fill(x + 1, fy, z, dist + 1);
        if (_world.GetBlock(x, fy, z - 1) == BlockId.Air) Fill(x, fy, z - 1, dist + 1);
        if (_world.GetBlock(x, fy, z + 1) == BlockId.Air) Fill(x, fy, z + 1, dist + 1);
    }

    void Fill(int x, int y, int z, int dist)
    {
        _world.SetBlockBatched(x, y, z, BlockId.Water, _changed);
        _flowDist[(x, y, z)] = dist;
        if (_queued.Add((x, y, z))) _active.Enqueue((x, y, z));
    }

    /// Spread distance for a water cell: 0 = source/falling column (full
    /// height), up to MaxSideSteps = the weakest, shallowest flow edge.
    /// Untracked cells (generated ocean/lake water) read as 0 — full height.
    public int LevelAt(int x, int y, int z) => _flowDist.GetValueOrDefault((x, y, z), 0);

    /// Flow distances worth persisting (0 is the default, so only spread
    /// water carries state).
    public IEnumerable<(int X, int Y, int Z, int Dist)> ExportFlow()
    {
        foreach (var ((x, y, z), d) in _flowDist)
            if (d > 0)
                yield return (x, y, z, d);
    }

    public void ImportFlow(int x, int y, int z, int dist) => _flowDist[(x, y, z)] = dist;
}
