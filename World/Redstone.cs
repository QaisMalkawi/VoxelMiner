namespace VoxelMiner.World;

using VoxelMiner.Core;
using static VoxelMiner.Core.Constants;

/// Java-style redstone simulation: dust networks with 15-level falloff,
/// strong vs weak block powering, repeaters (delay + locking), comparators
/// (compare/subtract, container reading), observers, pistons with slime/honey
/// push structures, and support-popping for flat components.
///
/// Runs on the main thread at 20 ticks/s (one redstone tick = 2 game ticks,
/// matching Java's 0.1 s). Deliberate simplifications vs Java: no
/// quasi-connectivity, pistons move blocks instantly (no animation), and
/// entities are not pushed.
public sealed class RedstoneSim
{
    const int PushLimit = 12;              // blocks one piston can move
    const int ButtonTicks = 20;            // 1 s press
    const int MaxDirtyPerTick = 512;
    const int MaxNetworksPerTick = 64;

    readonly GameWorld _world;
    readonly Func<int, int, int, int> _containerSignal; // comparator container reading (0..15)
    public event Action<int, int, int, int> ItemPopped; // (x, y, z, itemId)

    HashSet<(int X, int Y, int Z)> _dirty = new(), _processing = new();
    readonly Dictionary<(int X, int Y, int Z), long> _due = new();
    readonly Dictionary<(int X, int Y, int Z), int> _compOut = new();
    readonly HashSet<(int X, int Y, int Z)> _netDone = new();
    readonly HashSet<(int Cx, int Cz)> _affected = new();
    long _tick;
    float _acc;

    public RedstoneSim(GameWorld world, Func<int, int, int, int> containerSignal)
    {
        _world = world;
        _containerSignal = containerSignal;
        world.BlockChanged += OnBlockChanged;
    }

    // ------------------------------------------------------------- events

    void OnBlockChanged(int x, int y, int z, int oldId, int newId)
    {
        MarkNeighbours(x, y, z);
        _dirty.Add((x, y, z));
        // conduction: a change beside a full cube can change what that cube's
        // other neighbours read through it (lever -> block -> piston), so the
        // cube's neighbourhood re-evaluates too
        for (int f = 0; f < 6; f++)
        {
            var (dx, dy, dz) = BlockRegistry.Facing6(f);
            if (BlockRegistry.IsFullCube(_world.GetBlock(x + dx, y + dy, z + dz)))
                MarkNeighbours(x + dx, y + dy, z + dz);
        }
        // observers pulse when the block their face watches changes
        for (int f = 0; f < 6; f++)
        {
            var (dx, dy, dz) = BlockRegistry.Facing6(f);
            int ox = x + dx, oy = y + dy, oz = z + dz;
            int oid = _world.GetBlock(ox, oy, oz);
            if (BlockRegistry.ShapeOf(oid) == BlockShape.Observer
                && BlockRegistry.FacingOf(oid) == BlockRegistry.OppositeFacing(f)
                && !BlockRegistry.IsOpen(oid) && !_due.ContainsKey((ox, oy, oz)))
                _due[(ox, oy, oz)] = _tick + 1;
        }
    }

    void MarkNeighbours(int x, int y, int z)
    {
        for (int f = 0; f < 6; f++)
        {
            var (dx, dy, dz) = BlockRegistry.Facing6(f);
            _dirty.Add((x + dx, y + dy, z + dz));
        }
    }

    /// Re-evaluates every redstone component in a chunk — used after loading
    /// a save, since comparator strengths and pending timers aren't persisted.
    public void RefreshChunk(int cx, int cz)
    {
        if (!_world.Chunks.TryGetValue((cx, cz), out var data)) return;
        for (int i = 0; i < data.Length; i++)
            if (data[i] >= BlockId.RedstoneOre && data[i] <= BlockId.Slime)
                _dirty.Add((cx * ChunkSize + (i & 15), i >> 8, cz * ChunkSize + ((i >> 4) & 15)));
    }

    // ------------------------------------------------------------- ticking

    public void Update(float dt)
    {
        _acc += dt;
        while (_acc >= 0.05f)
        {
            _acc -= 0.05f;
            Step();
        }
    }

    void Step()
    {
        _tick++;
        _netDone.Clear();
        _affected.Clear();

        if (_due.Count > 0)
        {
            var fire = _due.Where(kv => kv.Value <= _tick).Select(kv => kv.Key).ToList();
            foreach (var pos in fire)
            {
                _due.Remove(pos);
                Act(pos);
            }
        }

        int budget = MaxDirtyPerTick;
        for (int round = 0; round < 4 && _dirty.Count > 0 && budget > 0; round++)
        {
            (_processing, _dirty) = (_dirty, _processing);
            _dirty.Clear();
            foreach (var pos in _processing)
            {
                if (budget-- <= 0) { _dirty.Add(pos); continue; }
                Evaluate(pos);
            }
            _processing.Clear();
        }

        if (_affected.Count > 0) _world.NotifyChanged(_affected.ToList());
    }

    void Set(int x, int y, int z, int id) => _world.SetBlockBatched(x, y, z, id, _affected);

    void Pop(int x, int y, int z, int id)
    {
        Set(x, y, z, BlockId.Air);
        int drop = BlockRegistry.BaseOf(id);
        if (drop != 0) ItemPopped?.Invoke(x, y, z, drop);
    }

    // ------------------------------------------------------------- evaluation

    void Evaluate((int X, int Y, int Z) pos)
    {
        var (x, y, z) = pos;
        int id = _world.GetBlock(x, y, z);
        var def = BlockRegistry.Blocks.TryGetValue(id, out var d) ? d : null;
        if (def == null) return;

        // flat components pop when their support disappears
        if (BlockRegistry.IsFlat(id) && !HasSupport(x, y, z, def))
        {
            Pop(x, y, z, id);
            return;
        }

        switch (def.Shape)
        {
            case BlockShape.Dust:
                RecomputeNetwork(x, y, z);
                break;

            case BlockShape.Button:
                if (def.Open && !_due.ContainsKey(pos)) _due[pos] = _tick + ButtonTicks;
                break;

            case BlockShape.Repeater:
            {
                if (IsLocked(x, y, z, def)) break;
                bool input = RepeaterInput(x, y, z, def) > 0;
                if (input != def.Open && !_due.ContainsKey(pos))
                    _due[pos] = _tick + def.Aux * 2; // delay setting, in redstone ticks
                break;
            }

            case BlockShape.Comparator:
            {
                int output = ComparatorOutput(x, y, z, def);
                if (output != ComparatorStrength(x, y, z) && !_due.ContainsKey(pos))
                    _due[pos] = _tick + 2;
                break;
            }

            case BlockShape.Piston:
            {
                bool want = PistonWantsExtend(x, y, z, def);
                if (want != def.Open && !_due.ContainsKey(pos))
                    _due[pos] = _tick + 2;
                break;
            }
        }
    }

    /// A component's scheduled action coming due: state flips re-check their
    /// inputs at fire time, like Java's tick scheduler.
    void Act((int X, int Y, int Z) pos)
    {
        var (x, y, z) = pos;
        int id = _world.GetBlock(x, y, z);
        if (!BlockRegistry.Blocks.TryGetValue(id, out var def)) return;

        switch (def.Shape)
        {
            case BlockShape.Button:
                if (def.Open) Set(x, y, z, BlockRegistry.ButtonVariant(def.Aux, pressed: false));
                break;

            case BlockShape.Repeater:
            {
                if (IsLocked(x, y, z, def)) break;
                bool powered = RepeaterInput(x, y, z, def) > 0;
                if (powered != def.Open)
                    Set(x, y, z, BlockRegistry.RepeaterVariant(def.Facing, def.Aux, powered));
                break;
            }

            case BlockShape.Comparator:
            {
                int output = ComparatorOutput(x, y, z, def);
                _compOut[pos] = output;
                Set(x, y, z, BlockRegistry.ComparatorVariant(def.Facing, def.Aux == 1, output > 0));
                MarkNeighbours(x, y, z); // strength can change without the id changing
                break;
            }

            case BlockShape.Observer:
                if (!def.Open)
                {
                    Set(x, y, z, BlockRegistry.ObserverVariant(def.Facing, powered: true));
                    _due[pos] = _tick + 2; // pulse length: one redstone tick
                }
                else
                {
                    Set(x, y, z, BlockRegistry.ObserverVariant(def.Facing, powered: false));
                }
                break;

            case BlockShape.Piston:
            {
                bool want = PistonWantsExtend(x, y, z, def);
                bool sticky = def.Aux == 1;
                if (want && !def.Open) TryExtend(x, y, z, def.Facing, sticky);
                else if (!want && def.Open) Retract(x, y, z, def.Facing, sticky);
                break;
            }
        }
    }

    bool HasSupport(int x, int y, int z, BlockDef def)
    {
        var (dx, dy, dz) = def.Shape is BlockShape.Lever or BlockShape.Button
            ? BlockRegistry.AttachDir(def.Aux)
            : (0, -1, 0);
        return BlockRegistry.IsFullCube(_world.GetBlock(x + dx, y + dy, z + dz));
    }

    // ------------------------------------------------------------- power queries

    int ComparatorStrength(int x, int y, int z) => _compOut.GetValueOrDefault((x, y, z));

    /// Signal a cell emits toward direction (tx,ty,tz). toDust restricts to
    /// what dust may read (dust-to-dust flow is the network's job).
    int EmitTo(int x, int y, int z, int tx, int ty, int tz, bool toDust)
    {
        int id = _world.GetBlock(x, y, z);
        if (!BlockRegistry.Blocks.TryGetValue(id, out var def)) return 0;
        switch (def.Shape)
        {
            case BlockShape.Lever:
            case BlockShape.Button:
                return def.Open ? 15 : 0;
            case BlockShape.Repeater:
            {
                var (fx, fz) = BlockRegistry.FacingDir(def.Facing);
                return def.Open && tx == fx && ty == 0 && tz == fz ? 15 : 0;
            }
            case BlockShape.Comparator:
            {
                var (fx, fz) = BlockRegistry.FacingDir(def.Facing);
                return def.Open && tx == fx && ty == 0 && tz == fz ? ComparatorStrength(x, y, z) : 0;
            }
            case BlockShape.Observer:
            {
                var (bx, by, bz) = BlockRegistry.Facing6(BlockRegistry.OppositeFacing(def.Facing));
                return def.Open && tx == bx && ty == by && tz == bz ? 15 : 0;
            }
            case BlockShape.Dust when !toDust:
            {
                int p = def.Aux;
                if (p == 0) return 0;
                if (ty == -1) return p;                       // dust powers the block below
                if (ty != 0) return 0;
                return DustConnected(x, y, z, FacingOfDir(tx, tz)) ? p : 0;
            }
            default:
                return 0;
        }
    }

    static int FacingOfDir(int dx, int dz) => dx == 1 ? 1 : dx == -1 ? 3 : dz == 1 ? 2 : 0;

    /// Power a full-cube block carries. Strong sources (attached levers and
    /// buttons, repeaters/comparators/observers pointing in) can be read by
    /// anything; weak power from dust is invisible to other dust.
    int BlockPower(int x, int y, int z, bool strongOnly)
    {
        int best = 0;
        for (int f = 0; f < 6 && best < 15; f++)
        {
            var (dx, dy, dz) = BlockRegistry.Facing6(f);
            int mx = x + dx, my = y + dy, mz = z + dz;
            int mid = _world.GetBlock(mx, my, mz);
            if (!BlockRegistry.Blocks.TryGetValue(mid, out var md)) continue;
            switch (md.Shape)
            {
                case BlockShape.Lever:
                case BlockShape.Button:
                {
                    var (ax, ay, az) = BlockRegistry.AttachDir(md.Aux);
                    if (md.Open && mx + ax == x && my + ay == y && mz + az == z) best = 15;
                    break;
                }
                case BlockShape.Repeater:
                {
                    var (fx, fz) = BlockRegistry.FacingDir(md.Facing);
                    if (md.Open && dy == 0 && fx == -dx && fz == -dz) best = 15;
                    break;
                }
                case BlockShape.Comparator:
                {
                    var (fx, fz) = BlockRegistry.FacingDir(md.Facing);
                    if (md.Open && dy == 0 && fx == -dx && fz == -dz)
                        best = Math.Max(best, ComparatorStrength(mx, my, mz));
                    break;
                }
                case BlockShape.Observer:
                {
                    var (bx, by, bz) = BlockRegistry.Facing6(BlockRegistry.OppositeFacing(md.Facing));
                    if (md.Open && bx == -dx && by == -dy && bz == -dz) best = 15;
                    break;
                }
                case BlockShape.Dust when !strongOnly:
                {
                    int p = md.Aux;
                    if (p == 0) break;
                    if (f == 4) best = Math.Max(best, p); // dust sitting on top
                    else if (dy == 0 && DustConnected(mx, my, mz, FacingOfDir(-dx, -dz)))
                        best = Math.Max(best, p);         // dust pointing at this block
                    break;
                }
            }
        }
        return best;
    }

    /// Signal entering a component from the neighbour in direction f.
    int InputSignal(int x, int y, int z, int f, bool forDust = false)
    {
        var (dx, dy, dz) = BlockRegistry.Facing6(f);
        int mx = x + dx, my = y + dy, mz = z + dz;
        int v = EmitTo(mx, my, mz, -dx, -dy, -dz, forDust);
        if (v < 15 && BlockRegistry.IsFullCube(_world.GetBlock(mx, my, mz)))
            v = Math.Max(v, BlockPower(mx, my, mz, strongOnly: forDust));
        return v;
    }

    int RepeaterInput(int x, int y, int z, BlockDef def)
    {
        var (fx, fz) = BlockRegistry.FacingDir(def.Facing);
        return InputSignal(x, y, z, FacingOfDir(-fx, -fz));
    }

    /// A repeater is locked while a powered repeater/comparator points into
    /// either of its sides.
    bool IsLocked(int x, int y, int z, BlockDef def)
    {
        foreach (int side in new[] { (def.Facing + 1) & 3, (def.Facing + 3) & 3 })
        {
            var (sx, sz) = BlockRegistry.FacingDir(side);
            int mid = _world.GetBlock(x + sx, y, z + sz);
            if (!BlockRegistry.Blocks.TryGetValue(mid, out var md)) continue;
            if (md.Shape is not (BlockShape.Repeater or BlockShape.Comparator) || !md.Open) continue;
            var (fx, fz) = BlockRegistry.FacingDir(md.Facing);
            if (fx == -sx && fz == -sz) return true;
        }
        return false;
    }

    int ComparatorOutput(int x, int y, int z, BlockDef def)
    {
        var (fx, fz) = BlockRegistry.FacingDir(def.Facing);
        int back;
        int bid = _world.GetBlock(x - fx, y, z - fz);
        if (BlockRegistry.ShapeOf(bid) is BlockShape.Chest or BlockShape.Furnace)
            back = _containerSignal?.Invoke(x - fx, y, z - fz) ?? 0;
        else
            back = InputSignal(x, y, z, FacingOfDir(-fx, -fz));

        int side = 0;
        foreach (int s in new[] { (def.Facing + 1) & 3, (def.Facing + 3) & 3 })
        {
            var (sx, sz) = BlockRegistry.FacingDir(s);
            int mid = _world.GetBlock(x + sx, y, z + sz);
            if (!BlockRegistry.Blocks.TryGetValue(mid, out var md)) continue;
            side = md.Shape switch
            {
                BlockShape.Dust => Math.Max(side, md.Aux),
                BlockShape.Repeater when md.Open && BlockRegistry.FacingDir(md.Facing) == (-sx, -sz) => 15,
                BlockShape.Comparator when md.Open && BlockRegistry.FacingDir(md.Facing) == (-sx, -sz) =>
                    Math.Max(side, ComparatorStrength(x + sx, y, z + sz)),
                _ => side,
            };
        }
        return def.Aux == 1 ? Math.Max(0, back - side) : (back >= side ? back : 0);
    }

    bool PistonWantsExtend(int x, int y, int z, BlockDef def)
    {
        for (int f = 0; f < 6; f++)
        {
            if (f == def.Facing) continue; // a piston can't be powered through its face
            if (InputSignal(x, y, z, f) > 0) return true;
        }
        return false;
    }

    // ------------------------------------------------------------- dust networks

    /// Whether a dust cell points toward facing f (shared with the mesher via
    /// GameWorld.DustMaskAt, which handles cross and line-through rules).
    bool DustConnected(int x, int y, int z, int f) => (_world.DustMaskAt(x, y, z) >> f & 1) != 0;

    void RecomputeNetwork(int x, int y, int z)
    {
        if (_netDone.Contains((x, y, z)) || _netDone.Count > MaxNetworksPerTick * 256) return;

        // collect the connected dust network
        var cells = new List<(int X, int Y, int Z)>();
        var seen = new HashSet<(int, int, int)>();
        var queue = new Queue<(int X, int Y, int Z)>();
        queue.Enqueue((x, y, z));
        seen.Add((x, y, z));
        while (queue.Count > 0 && cells.Count < 1500)
        {
            var c = queue.Dequeue();
            if (BlockRegistry.ShapeOf(_world.GetBlock(c.X, c.Y, c.Z)) != BlockShape.Dust) continue;
            cells.Add(c);
            _netDone.Add(c);
            for (int f = 0; f < 4; f++)
            {
                var (dx, dz) = BlockRegistry.FacingDir(f);
                for (int dy = -1; dy <= 1; dy++)
                {
                    var n = (c.X + dx, c.Y + dy, c.Z + dz);
                    if (seen.Add(n)) queue.Enqueue(n);
                }
            }
        }

        // injected power per cell: adjacent sources and strongly powered blocks
        var power = new Dictionary<(int, int, int), int>();
        var frontier = new List<(int X, int Y, int Z)>[16];
        for (int i = 0; i < 16; i++) frontier[i] = new List<(int, int, int)>();
        foreach (var c in cells)
        {
            int inj = 0;
            for (int f = 0; f < 6 && inj < 15; f++)
                inj = Math.Max(inj, InputSignal(c.X, c.Y, c.Z, f, forDust: true));
            power[c] = inj;
            if (inj > 0) frontier[inj].Add(c);
        }

        // max-propagation with -1 falloff per step over the network
        var cellSet = new HashSet<(int, int, int)>(cells.Select(c => ((int, int, int))c));
        for (int level = 15; level > 1; level--)
            foreach (var c in frontier[level])
            {
                if (power[(c.X, c.Y, c.Z)] != level) continue; // superseded
                for (int f = 0; f < 4; f++)
                {
                    var (dx, dz) = BlockRegistry.FacingDir(f);
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        var n = (c.X + dx, c.Y + dy, c.Z + dz);
                        if (!cellSet.Contains(n) || power[n] >= level - 1) continue;
                        power[n] = level - 1;
                        frontier[level - 1].Add((n.Item1, n.Item2, n.Item3));
                    }
                }
            }

        foreach (var c in cells)
        {
            int want = BlockRegistry.DustVariant(power[(c.X, c.Y, c.Z)]);
            if (_world.GetBlock(c.X, c.Y, c.Z) != want) Set(c.X, c.Y, c.Z, want);
        }
    }

    // ------------------------------------------------------------- pistons

    static bool Fragile(int id) =>
        BlockRegistry.IsFlat(id) || BlockRegistry.IsVegetation(id) || id == BlockId.Torch;

    bool Immovable(int id)
    {
        if (!BlockRegistry.Blocks.TryGetValue(id, out var d)) return false;
        if (double.IsInfinity(d.Hard)) return true;
        return d.Shape is BlockShape.Chest or BlockShape.Furnace or BlockShape.DoorLower
            or BlockShape.DoorUpper or BlockShape.PistonHead
            || (d.Shape == BlockShape.Piston && d.Open);
    }

    static bool Sticks(int a, int b) // slime and honey refuse to stick to each other
        => !((a == BlockId.Slime && b == BlockId.Honey) || (a == BlockId.Honey && b == BlockId.Slime));

    /// Gathers the set of blocks a piston move drags along: the push column,
    /// plus everything adjacent to slime/honey (recursively), respecting the
    /// 12-block limit. Returns false when an immovable block or the limit
    /// blocks the move. destroy lists fragile blocks in the way.
    bool CollectMoveSet(int fx, int fy, int fz, (int X, int Y, int Z) dir, (int X, int Y, int Z) basePos,
        out List<(int X, int Y, int Z)> set, out List<(int X, int Y, int Z)> destroy)
    {
        set = new List<(int, int, int)>();
        destroy = new List<(int, int, int)>();
        var seen = new HashSet<(int, int, int)>();
        var queue = new Queue<(int X, int Y, int Z)>();

        int firstId = _world.GetBlock(fx, fy, fz);
        if (firstId == BlockId.Air || firstId == BlockId.Water) return true;
        if (Fragile(firstId)) { destroy.Add((fx, fy, fz)); return true; }
        queue.Enqueue((fx, fy, fz));
        seen.Add((fx, fy, fz));

        while (queue.Count > 0)
        {
            var b = queue.Dequeue();
            int id = _world.GetBlock(b.X, b.Y, b.Z);
            if (id == BlockId.Air || id == BlockId.Water || Fragile(id)) continue;
            if (Immovable(id)) return false;
            set.Add(b);
            if (set.Count > PushLimit) return false;

            // the cell this block moves into must clear too
            var dest = (X: b.X + dir.X, Y: b.Y + dir.Y, Z: b.Z + dir.Z);
            if (!seen.Contains(dest))
            {
                int did = _world.GetBlock(dest.X, dest.Y, dest.Z);
                if (Fragile(did)) { destroy.Add(dest); seen.Add(dest); }
                else if (did != BlockId.Air && did != BlockId.Water)
                {
                    if (Immovable(did)) return false;
                    seen.Add(dest);
                    queue.Enqueue(dest);
                }
            }

            // slime/honey drag their movable neighbours
            if (id == BlockId.Slime || id == BlockId.Honey)
                for (int f = 0; f < 6; f++)
                {
                    var (dx, dy, dz) = BlockRegistry.Facing6(f);
                    var n = (X: b.X + dx, Y: b.Y + dy, Z: b.Z + dz);
                    if (seen.Contains(n) || n == basePos) continue;
                    int nid = _world.GetBlock(n.X, n.Y, n.Z);
                    if (nid == BlockId.Air || nid == BlockId.Water || Fragile(nid)) continue;
                    if (Immovable(nid) || !Sticks(id, nid)) continue; // doesn't drag, doesn't block
                    seen.Add(n);
                    queue.Enqueue(n);
                }
        }
        return true;
    }

    void MoveBlocks(List<(int X, int Y, int Z)> set, List<(int X, int Y, int Z)> destroy, (int X, int Y, int Z) dir)
    {
        foreach (var d in destroy)
            Pop(d.X, d.Y, d.Z, _world.GetBlock(d.X, d.Y, d.Z));

        // farthest along the push direction moves first so nothing overwrites
        set.Sort((a, b) => (b.X * dir.X + b.Y * dir.Y + b.Z * dir.Z)
            .CompareTo(a.X * dir.X + a.Y * dir.Y + a.Z * dir.Z));
        var occupied = new HashSet<(int, int, int)>(set.Select(s => ((int, int, int))s));
        foreach (var b in set)
            Set(b.X + dir.X, b.Y + dir.Y, b.Z + dir.Z, _world.GetBlock(b.X, b.Y, b.Z));
        foreach (var b in set)
            if (!occupied.Contains((b.X - dir.X, b.Y - dir.Y, b.Z - dir.Z)))
                Set(b.X, b.Y, b.Z, BlockId.Air);
    }

    void TryExtend(int x, int y, int z, int facing, bool sticky)
    {
        var (dx, dy, dz) = BlockRegistry.Facing6(facing);
        if (!CollectMoveSet(x + dx, y + dy, z + dz, (dx, dy, dz), (x, y, z), out var set, out var destroy))
            return; // blocked
        MoveBlocks(set, destroy, (dx, dy, dz));
        Set(x + dx, y + dy, z + dz, BlockRegistry.PistonHeadVariant(facing, sticky));
        Set(x, y, z, BlockRegistry.PistonVariant(facing, extended: true, sticky));
    }

    void Retract(int x, int y, int z, int facing, bool sticky)
    {
        var (dx, dy, dz) = BlockRegistry.Facing6(facing);
        int hx = x + dx, hy = y + dy, hz = z + dz;
        if (BlockRegistry.ShapeOf(_world.GetBlock(hx, hy, hz)) == BlockShape.PistonHead)
            Set(hx, hy, hz, BlockId.Air);
        Set(x, y, z, BlockRegistry.PistonVariant(facing, extended: false, sticky));

        if (!sticky) return;
        int tx = x + dx * 2, ty = y + dy * 2, tz = z + dz * 2;
        int tid = _world.GetBlock(tx, ty, tz);
        if (tid == BlockId.Air || tid == BlockId.Water || Fragile(tid)) return;
        // pull toward the piston; a failed pull just leaves the block behind
        if (CollectMoveSet(tx, ty, tz, (-dx, -dy, -dz), (x, y, z), out var set, out var destroy))
            MoveBlocks(set, destroy, (-dx, -dy, -dz));
    }
}
