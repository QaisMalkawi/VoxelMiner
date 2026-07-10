using System.Collections.Concurrent;

namespace VoxelMiner.World;

using VoxelMiner.Core;
using static VoxelMiner.Core.Constants;

/// Voxel data store. Owns chunk data, lighting, and edits; knows nothing about
/// rendering. Rendering subscribes to chunk changes via ChunkChanged.
/// All mutation happens on the main thread; the concurrent dictionary exists
/// so background mesh workers can read chunks while new ones are added.
public sealed class GameWorld
{
    readonly TerrainGenerator _generator;
    public readonly ConcurrentDictionary<(int Cx, int Cz), byte[]> Chunks = new();
    public readonly LightEngine Lighting;
    /// Chunks whose data diverged from pure generation — the only ones a
    /// save file needs to carry (the rest regenerate from the seed).
    public readonly HashSet<(int Cx, int Cz)> EditedChunks = new();
    public event Action<int, int> ChunkChanged;

    public GameWorld(TerrainGenerator generator)
    {
        _generator = generator;
        Lighting = new LightEngine(this);
    }

    public byte[] EnsureChunk(int cx, int cz)
    {
        if (!Chunks.TryGetValue((cx, cz), out var data))
        {
            data = _generator.Generate(cx, cz);
            Chunks[(cx, cz)] = data;
            Lighting.InitChunk(cx, cz);
        }
        return data;
    }

    /// Pure terrain generation — no shared state, safe to call off-thread.
    public byte[] GenerateData(int cx, int cz) => _generator.Generate(cx, cz);

    /// A safe spawn column near the world origin (dry, gentle, tree-free).
    public (int X, int Z) FindSpawn() => _generator.FindSpawn();

    /// Installs chunk data produced off-thread and lights it (main thread
    /// only). Returns false when the chunk already exists — e.g. it was
    /// generated synchronously meanwhile — so edited data is never replaced.
    public bool AdoptChunk(int cx, int cz, byte[] data)
    {
        if (!Chunks.TryAdd((cx, cz), data)) return false;
        Lighting.InitChunk(cx, cz);
        return true;
    }

    public bool HasChunk(int cx, int cz) => Chunks.ContainsKey((cx, cz));

    public int GetBlock(int gx, int gy, int gz)
    {
        if (gy < 0) return BlockId.Bedrock; // below-world reads as solid so bottom faces cull
        if (gy >= WorldHeight) return BlockId.Air;
        int cx = FloorDiv(gx, ChunkSize), cz = FloorDiv(gz, ChunkSize);
        if (!Chunks.TryGetValue((cx, cz), out var data)) return BlockId.Air;
        return data[BlockIndex(gx - cx * ChunkSize, gy, gz - cz * ChunkSize)];
    }

    public void SetBlock(int gx, int gy, int gz, int id)
    {
        var affected = new HashSet<(int, int)>();
        SetBlockBatched(gx, gy, gz, id, affected);
        NotifyChanged(affected);
    }

    /// Applies an edit and collects stale chunk keys instead of firing events.
    /// Callers batch many edits (fluid ticks) into one NotifyChanged.
    public void SetBlockBatched(int gx, int gy, int gz, int id, HashSet<(int, int)> affected)
    {
        if (gy < 0 || gy >= WorldHeight) return;
        int cx = FloorDiv(gx, ChunkSize), cz = FloorDiv(gz, ChunkSize);
        var data = EnsureChunk(cx, cz);
        int i = BlockIndex(gx - cx * ChunkSize, gy, gz - cz * ChunkSize);
        int old = data[i];
        if (old == id) return;
        data[i] = (byte)id;
        EditedChunks.Add((cx, cz));
        Lighting.OnBlockChanged(gx, gy, gz, old, id);

        affected.Add((cx, cz));
        // edits on a border also invalidate the neighbouring chunk's mesh
        int lx = gx - cx * ChunkSize, lz = gz - cz * ChunkSize;
        if (lx == 0) affected.Add((cx - 1, cz));
        if (lx == ChunkSize - 1) affected.Add((cx + 1, cz));
        if (lz == 0) affected.Add((cx, cz - 1));
        if (lz == ChunkSize - 1) affected.Add((cx, cz + 1));
    }

    public void NotifyChanged(IEnumerable<(int Cx, int Cz)> chunks)
    {
        foreach (var (cx, cz) in chunks) ChunkChanged?.Invoke(cx, cz);
    }

    public bool IsSolidAt(int gx, int gy, int gz) => BlockRegistry.IsSolid(GetBlock(gx, gy, gz));

    static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);
}
