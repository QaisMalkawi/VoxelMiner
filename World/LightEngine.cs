using System.Collections.Concurrent;

namespace VoxelMiner.World;

using VoxelMiner.Core;
using static VoxelMiner.Core.Constants;

/// Minecraft-style voxel lighting. Each chunk stores a byte per cell:
/// sky light in the high nibble, block (torch) light in the low nibble.
/// Sky light is 15 under open sky and travels downward for free; both
/// channels flood-fill sideways losing at least 1 per step. Edits update
/// incrementally with the classic two-queue add/remove BFS.
public sealed class LightEngine
{
    static readonly int[] DX = { -1, 1, 0, 0, 0, 0 };
    static readonly int[] DY = { 0, 0, -1, 1, 0, 0 };
    static readonly int[] DZ = { 0, 0, 0, 0, -1, 1 };
    const int Down = 2; // index into the tables above

    readonly GameWorld _world;
    // mutated on the main thread only; concurrent so mesh workers can read
    readonly ConcurrentDictionary<(int Cx, int Cz), byte[]> _maps = new();
    readonly Queue<(int X, int Y, int Z)> _spread = new();
    readonly Queue<(int X, int Y, int Z, int Level)> _unspread = new();
    readonly HashSet<(int Cx, int Cz)> _touched = new();

    /// Raised for every chunk whose light values changed (its mesh is stale).
    public event Action<int, int> LightChanged;

    public LightEngine(GameWorld world) => _world = world;

    // ------------------------------------------------------------- access

    public int GetSky(int gx, int gy, int gz)
    {
        if (gy >= WorldHeight) return MaxLight;
        if (gy < 0) return 0;
        int cx = FloorDiv(gx, ChunkSize), cz = FloorDiv(gz, ChunkSize);
        if (!_maps.TryGetValue((cx, cz), out var m)) return MaxLight; // unloaded reads as daylight
        return m[BlockIndex(gx - cx * ChunkSize, gy, gz - cz * ChunkSize)] >> 4;
    }

    public int GetBlockLight(int gx, int gy, int gz)
    {
        if (gy < 0 || gy >= WorldHeight) return 0;
        int cx = FloorDiv(gx, ChunkSize), cz = FloorDiv(gz, ChunkSize);
        if (!_maps.TryGetValue((cx, cz), out var m)) return 0;
        return m[BlockIndex(gx - cx * ChunkSize, gy, gz - cz * ChunkSize)] & 0xF;
    }

    int Get(bool sky, int x, int y, int z) => sky ? GetSky(x, y, z) : GetBlockLight(x, y, z);

    bool Loaded(int gx, int gz) => _maps.ContainsKey((FloorDiv(gx, ChunkSize), FloorDiv(gz, ChunkSize)));

    /// Writes a light value; returns false when the chunk isn't loaded.
    /// Marks the owning chunk (and border neighbours) stale.
    bool Set(bool sky, int gx, int gy, int gz, int v)
    {
        if (gy < 0 || gy >= WorldHeight) return false;
        int cx = FloorDiv(gx, ChunkSize), cz = FloorDiv(gz, ChunkSize);
        if (!_maps.TryGetValue((cx, cz), out var m)) return false;
        int lx = gx - cx * ChunkSize, lz = gz - cz * ChunkSize;
        int i = BlockIndex(lx, gy, lz);
        byte old = m[i];
        byte next = sky ? (byte)((old & 0x0F) | (v << 4)) : (byte)((old & 0xF0) | v);
        if (next == old) return true;
        m[i] = next;

        _touched.Add((cx, cz));
        // meshes sample light up to 1 cell across borders (smooth lighting)
        if (lx == 0) _touched.Add((cx - 1, cz));
        if (lx == ChunkSize - 1) _touched.Add((cx + 1, cz));
        if (lz == 0) _touched.Add((cx, cz - 1));
        if (lz == ChunkSize - 1) _touched.Add((cx, cz + 1));
        return true;
    }

    // ------------------------------------------------------------- chunk init

    /// Seeds and propagates light for a freshly generated chunk, pulling in
    /// light from any already-loaded neighbours (and pushing back into them).
    /// Runs on the main thread during chunk adoption, so it seeds the BFS
    /// sparsely: naively enqueueing every open-sky cell (~40k per chunk) made
    /// streaming stutter; only cells that can actually push light somewhere
    /// dim enter the queue.
    public void InitChunk(int cx, int cz)
    {
        var map = new byte[ChunkSize * ChunkSize * WorldHeight];
        _maps[(cx, cz)] = map;
        var data = _world.Chunks[(cx, cz)];
        _touched.Clear();

        int bx = cx * ChunkSize, bz = cz * ChunkSize;

        // per-column y of the first light-absorbing cell from the top
        // (-1 = column fully open); everything strictly above is direct sky
        var blocked = new int[ChunkSize * ChunkSize];
        for (int lz = 0; lz < ChunkSize; lz++)
            for (int lx = 0; lx < ChunkSize; lx++)
            {
                int y = WorldHeight - 1;
                for (; y >= 0 && Opacity[data[BlockIndex(lx, y, lz)]] == 0; y--)
                    map[BlockIndex(lx, y, lz)] = MaxLight << 4;
                blocked[lz * ChunkSize + lx] = y;
            }

        // A direct-sky cell only matters as a BFS source if a horizontally
        // adjacent column is blocked at or above it (that neighbour cell is
        // dim); compute each column's exposure ceiling from the 4-neighbour
        // blocked heights, looking across the border into loaded chunks.
        var exposed = new int[ChunkSize * ChunkSize];
        for (int lz = 0; lz < ChunkSize; lz++)
            for (int lx = 0; lx < ChunkSize; lx++)
            {
                int e = -1;
                if (lx > 0) e = Math.Max(e, blocked[lz * ChunkSize + lx - 1]);
                if (lx < ChunkSize - 1) e = Math.Max(e, blocked[lz * ChunkSize + lx + 1]);
                if (lz > 0) e = Math.Max(e, blocked[(lz - 1) * ChunkSize + lx]);
                if (lz < ChunkSize - 1) e = Math.Max(e, blocked[(lz + 1) * ChunkSize + lx]);
                exposed[lz * ChunkSize + lx] = e;
            }
        void Ring(int ncx, int ncz, bool xEdge, int nEdge, int myEdge)
        {
            if (!_world.Chunks.TryGetValue((ncx, ncz), out var nd)) return;
            for (int i = 0; i < ChunkSize; i++)
            {
                int nlx = xEdge ? nEdge : i, nlz = xEdge ? i : nEdge;
                int y = WorldHeight - 1;
                while (y >= 0 && Opacity[nd[BlockIndex(nlx, y, nlz)]] == 0) y--;
                int idx = xEdge ? i * ChunkSize + myEdge : myEdge * ChunkSize + i;
                exposed[idx] = Math.Max(exposed[idx], y);
            }
        }
        Ring(cx - 1, cz, xEdge: true, ChunkSize - 1, 0);
        Ring(cx + 1, cz, xEdge: true, 0, ChunkSize - 1);
        Ring(cx, cz - 1, xEdge: false, ChunkSize - 1, 0);
        Ring(cx, cz + 1, xEdge: false, 0, ChunkSize - 1);

        // seed the exposed band of each column, plus its lowest sky cell so
        // light keeps pushing down through leaves and water
        for (int lz = 0; lz < ChunkSize; lz++)
            for (int lx = 0; lx < ChunkSize; lx++)
            {
                int b = blocked[lz * ChunkSize + lx];
                if (b + 1 >= WorldHeight) continue; // blocked at the very top: no sky cells
                _spread.Enqueue((bx + lx, b + 1, bz + lz));
                int top = Math.Min(exposed[lz * ChunkSize + lx], WorldHeight - 1);
                for (int y = b + 2; y <= top; y++)
                    _spread.Enqueue((bx + lx, y, bz + lz));
            }
        Propagate(sky: true);
        SeedBorders(cx, cz, sky: true);
        Propagate(sky: true);

        // chunks restored from a save can carry emitters (torches, lit
        // furnaces); light maps aren't persisted, so re-seed them here
        for (int i = 0; i < data.Length; i++)
        {
            int em = Emission[data[i]];
            if (em == 0) continue;
            map[i] = (byte)((map[i] & 0xF0) | em);
            _spread.Enqueue((bx + (i & 15), i >> 8, bz + ((i >> 4) & 15)));
        }
        SeedBorders(cx, cz, sky: false);
        Propagate(sky: false);

        Flush(exclude: (cx, cz)); // this chunk has no mesh yet
    }

    /// LightEmission/LightOpacity per block id, tabulated once — InitChunk
    /// and the BFS consult them for huge numbers of cells, where the
    /// registry's per-call dictionary lookups would add up.
    static readonly byte[] Emission = BuildTable(BlockRegistry.LightEmission);
    static readonly byte[] Opacity = BuildTable(BlockRegistry.LightOpacity);

    static byte[] BuildTable(Func<int, int> f)
    {
        var t = new byte[256];
        for (int id = 0; id < 256; id++) t[id] = (byte)f(id);
        return t;
    }

    /// Enqueues lit border cells of loaded neighbours so their light flows
    /// in — but only where the receiving cell is dimmer than what the border
    /// cell could give it, so open-sky borders (both sides 15) cost nothing.
    void SeedBorders(int cx, int cz, bool sky)
    {
        void SeedColumnStrip(int ncx, int ncz, bool xEdge, int edge, int dx, int dz)
        {
            if (!_maps.ContainsKey((ncx, ncz))) return;
            int nbx = ncx * ChunkSize, nbz = ncz * ChunkSize;
            for (int i = 0; i < ChunkSize; i++)
                for (int y = 0; y < WorldHeight; y++)
                {
                    int gx = xEdge ? nbx + edge : nbx + i;
                    int gz = xEdge ? nbz + i : nbz + edge;
                    int l = Get(sky, gx, y, gz);
                    if (l <= 1) continue;
                    if (Get(sky, gx + dx, y, gz + dz) >= l - 1) continue; // visit would be a no-op
                    _spread.Enqueue((gx, y, gz));
                }
        }
        SeedColumnStrip(cx - 1, cz, xEdge: true, ChunkSize - 1, +1, 0);
        SeedColumnStrip(cx + 1, cz, xEdge: true, 0, -1, 0);
        SeedColumnStrip(cx, cz - 1, xEdge: false, ChunkSize - 1, 0, +1);
        SeedColumnStrip(cx, cz + 1, xEdge: false, 0, 0, -1);
    }

    // ------------------------------------------------------------- edits

    /// Incrementally relights the world after a single block change.
    public void OnBlockChanged(int gx, int gy, int gz, int oldId, int newId)
    {
        // light only sees opacity and emission; swaps that keep both (dust
        // power levels, lever flips, repeater states) cost nothing
        if (Opacity[oldId & 0xFF] == Opacity[newId & 0xFF] && Emission[oldId & 0xFF] == Emission[newId & 0xFF]) return;
        int cx = FloorDiv(gx, ChunkSize), cz = FloorDiv(gz, ChunkSize);
        if (!_maps.ContainsKey((cx, cz))) return;
        _touched.Clear();

        int oldOp = BlockRegistry.LightOpacity(oldId), newOp = BlockRegistry.LightOpacity(newId);
        int newEm = BlockRegistry.LightEmission(newId);

        // sky channel: strip light that flowed through the old block, then
        // let the sky and neighbours flood back through the new one
        if (newOp != oldOp)
        {
            int cur = GetSky(gx, gy, gz);
            if (cur > 0)
            {
                Set(true, gx, gy, gz, 0);
                _unspread.Enqueue((gx, gy, gz, cur));
                Unpropagate(sky: true);
            }
            if (newOp < 15)
            {
                SeedNeighbours(gx, gy, gz, sky: true);
                Propagate(sky: true);
            }
        }

        // block channel
        int curB = GetBlockLight(gx, gy, gz);
        if (curB > newEm)
        {
            Set(false, gx, gy, gz, 0);
            _unspread.Enqueue((gx, gy, gz, curB));
            Unpropagate(sky: false);
        }
        if (newEm > 0)
        {
            Set(false, gx, gy, gz, newEm);
            _spread.Enqueue((gx, gy, gz));
            Propagate(sky: false);
        }
        else if (newOp < oldOp)
        {
            SeedNeighbours(gx, gy, gz, sky: false);
            Propagate(sky: false);
        }

        Flush(exclude: null);
    }

    void SeedNeighbours(int gx, int gy, int gz, bool sky)
    {
        for (int d = 0; d < 6; d++)
            if (Loaded(gx + DX[d], gz + DZ[d]) && Get(sky, gx + DX[d], gy + DY[d], gz + DZ[d]) > 1)
                _spread.Enqueue((gx + DX[d], gy + DY[d], gz + DZ[d]));
    }

    // ------------------------------------------------------------- BFS

    void Propagate(bool sky)
    {
        while (_spread.Count > 0)
        {
            var (x, y, z) = _spread.Dequeue();
            int l = Get(sky, x, y, z);
            if (l <= 1) continue;
            for (int d = 0; d < 6; d++)
            {
                int nx = x + DX[d], ny = y + DY[d], nz = z + DZ[d];
                if (ny < 0 || ny >= WorldHeight) continue;
                int op = Opacity[_world.GetBlock(nx, ny, nz)];
                if (op >= 15) continue;
                int nl = sky && d == Down && l == MaxLight && op == 0
                    ? MaxLight
                    : l - Math.Max(1, op);
                if (nl > Get(sky, nx, ny, nz) && Set(sky, nx, ny, nz, nl))
                    _spread.Enqueue((nx, ny, nz));
            }
        }
    }

    void Unpropagate(bool sky)
    {
        while (_unspread.Count > 0)
        {
            var (x, y, z, l) = _unspread.Dequeue();
            for (int d = 0; d < 6; d++)
            {
                int nx = x + DX[d], ny = y + DY[d], nz = z + DZ[d];
                if (!Loaded(nx, nz)) continue; // never read phantom light from unloaded chunks
                int nl = Get(sky, nx, ny, nz);
                if (nl == 0) continue;
                // the neighbour's light was derived from the removed cell if it
                // is dimmer, or if it's a free-falling 15 sky column below it
                if (nl < l || (sky && d == Down && l == MaxLight && nl == MaxLight))
                {
                    if (Set(sky, nx, ny, nz, 0))
                        _unspread.Enqueue((nx, ny, nz, nl));
                }
                else
                {
                    _spread.Enqueue((nx, ny, nz)); // unaffected light re-floods the hole
                }
            }
        }
        Propagate(sky);
    }

    void Flush((int, int)? exclude)
    {
        foreach (var key in _touched)
            if (key != exclude)
                LightChanged?.Invoke(key.Item1, key.Item2);
        _touched.Clear();
    }

    static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);
}
