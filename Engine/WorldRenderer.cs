using System.Collections.Concurrent;
using System.Numerics;

namespace VoxelMiner.Engine;

using VoxelMiner.World;
using static VoxelMiner.Core.Constants;

/// Owns chunk meshes and the streaming pipeline. Terrain generation and mesh
/// geometry run on background tasks so the render loop never stalls on
/// worldgen; each frame the main thread only adopts finished chunk data
/// (plus its light init) and uploads finished geometry to the GPU, a few per
/// frame. Block edits still remesh synchronously for zero-latency feedback;
/// a per-chunk version counter discards any in-flight result they supersede.
/// Water lives in separate translucent meshes drawn after everything opaque.
public sealed class WorldRenderer
{
    const int MaxGenJobs = 6;            // terrain generations in flight
    const int MaxMeshJobs = 2;           // mesh builds in flight
    const int AdoptBudgetPerFrame = 2;   // chunk adoptions (light init) per frame
    const int UploadBudgetPerFrame = 4;  // GPU mesh uploads per frame
    static readonly Vector4 WaterTint = new(1f, 1f, 1f, 0.62f);

    readonly Renderer _renderer;
    readonly GameWorld _world;
    readonly ChunkMesher _mesher;
    readonly TextureHandle _atlas;
    public readonly Dictionary<(int Cx, int Cz), Mesh> Meshes = new();
    public readonly Dictionary<(int Cx, int Cz), Mesh> WaterMeshes = new();
    readonly HashSet<(int Cx, int Cz)> _lightDirty = new();

    // results cross threads via these queues; all other bookkeeping
    // (pending sets, versions) is touched by the main thread only
    readonly ConcurrentQueue<((int Cx, int Cz) Key, byte[] Data)> _genDone = new();
    readonly ConcurrentQueue<((int Cx, int Cz) Key, long Version,
        float[] Verts, uint[] Indices, float[] WaterVerts, uint[] WaterIndices)> _meshDone = new();
    readonly HashSet<(int Cx, int Cz)> _genPending = new();
    readonly HashSet<(int Cx, int Cz)> _meshPending = new();
    readonly Dictionary<(int Cx, int Cz), long> _version = new();
    readonly List<(int Cx, int Cz, int DistSq)> _scratch = new();

    public WorldRenderer(Renderer renderer, GameWorld world, ChunkMesher mesher, TextureHandle atlas)
    {
        _renderer = renderer;
        _world = world;
        _mesher = mesher;
        _atlas = atlas;
        world.ChunkChanged += RebuildChunk;
        world.Lighting.LightChanged += (cx, cz) =>
        {
            if (Meshes.ContainsKey((cx, cz))) _lightDirty.Add((cx, cz));
        };
    }

    /// Synchronous rebuild: block edits and startup. Bumping the version
    /// makes any in-flight background build of this chunk land stale and
    /// get discarded, so an async result can never overwrite a fresh edit.
    public void RebuildChunk(int cx, int cz)
    {
        if (!_world.HasChunk(cx, cz)) return;
        // neighbour data must exist so border faces cull and AO samples correctly
        for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
                _world.EnsureChunk(cx + dx, cz + dz);

        _version[(cx, cz)] = _version.GetValueOrDefault((cx, cz)) + 1;
        var (verts, indices, waterVerts, waterIndices) = _mesher.Build(cx, cz);
        Upload(cx, cz, verts, indices, waterVerts, waterIndices);
        _lightDirty.Remove((cx, cz));
    }

    void Upload(int cx, int cz, float[] verts, uint[] indices, float[] waterVerts, uint[] waterIndices)
    {
        if (Meshes.TryGetValue((cx, cz), out var old)) _renderer.DeferDispose(old);
        Meshes[(cx, cz)] = Mesh.Create(_renderer.Ctx, verts, indices);

        if (WaterMeshes.TryGetValue((cx, cz), out var oldWater))
        {
            _renderer.DeferDispose(oldWater);
            WaterMeshes.Remove((cx, cz));
        }
        if (waterIndices.Length > 0)
            WaterMeshes[(cx, cz)] = Mesh.Create(_renderer.Ctx, waterVerts, waterIndices);
    }

    /// Synchronous: startup spawn area and autotest scenes.
    public void BuildAround(int cx, int cz, int radius)
    {
        for (int dz = -radius; dz <= radius; dz++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                _world.EnsureChunk(cx + dx, cz + dz);
                RebuildChunk(cx + dx, cz + dz);
            }
    }

    public void Update(Vector3 playerPos)
    {
        int ccx = (int)MathF.Floor(playerPos.X / ChunkSize), ccz = (int)MathF.Floor(playerPos.Z / ChunkSize);

        AdoptGenerated();
        RequestGeneration(ccx, ccz);
        SubmitMeshJobs(ccx, ccz);
        UploadFinishedMeshes(ccx, ccz);
        UnloadFar(ccx, ccz);
    }

    /// Finished terrain results: install data + run the light init, a couple
    /// per frame (lighting is the main-thread part of the cost).
    void AdoptGenerated()
    {
        int budget = AdoptBudgetPerFrame;
        while (budget > 0 && _genDone.TryDequeue(out var gen))
        {
            _genPending.Remove(gen.Key);
            if (_world.AdoptChunk(gen.Key.Cx, gen.Key.Cz, gen.Data)) budget--;
        }
    }

    /// Missing chunks are generated one ring beyond the view radius so any
    /// chunk we want to mesh already has all its neighbour data.
    void RequestGeneration(int ccx, int ccz)
    {
        if (_genPending.Count >= MaxGenJobs) return;
        _scratch.Clear();
        int r = ViewRadius + 1;
        for (int dz = -r; dz <= r; dz++)
            for (int dx = -r; dx <= r; dx++)
            {
                int cx = ccx + dx, cz = ccz + dz;
                if (!_world.HasChunk(cx, cz) && !_genPending.Contains((cx, cz)))
                    _scratch.Add((cx, cz, dx * dx + dz * dz));
            }
        _scratch.Sort((a, b) => a.DistSq.CompareTo(b.DistSq));
        foreach (var (cx, cz, _) in _scratch)
        {
            if (_genPending.Count >= MaxGenJobs) break;
            (int Cx, int Cz) key = (cx, cz);
            _genPending.Add(key);
            Task.Run(() => _genDone.Enqueue((key, _world.GenerateData(key.Cx, key.Cz))));
        }
    }

    /// Chunks that need a mesh (new, or light-stale) and whose 3x3 data is
    /// ready go to background mesh builds, nearest first.
    void SubmitMeshJobs(int ccx, int ccz)
    {
        if (_meshPending.Count >= MaxMeshJobs) return;
        _scratch.Clear();
        for (int dz = -ViewRadius; dz <= ViewRadius; dz++)
            for (int dx = -ViewRadius; dx <= ViewRadius; dx++)
            {
                int cx = ccx + dx, cz = ccz + dz;
                var key = (cx, cz);
                if (_meshPending.Contains(key)) continue;
                if (Meshes.ContainsKey(key) && !_lightDirty.Contains(key)) continue;
                if (!NeighbourhoodReady(cx, cz)) continue;
                _scratch.Add((cx, cz, dx * dx + dz * dz));
            }
        _scratch.Sort((a, b) => a.DistSq.CompareTo(b.DistSq));
        foreach (var (cx, cz, _) in _scratch)
        {
            if (_meshPending.Count >= MaxMeshJobs) break;
            (int Cx, int Cz) key = (cx, cz);
            _meshPending.Add(key);
            _lightDirty.Remove(key);
            long version = _version.GetValueOrDefault(key);
            Task.Run(() =>
            {
                var (verts, indices, waterVerts, waterIndices) = _mesher.Build(key.Cx, key.Cz);
                _meshDone.Enqueue((key, version, verts, indices, waterVerts, waterIndices));
            });
        }
    }

    bool NeighbourhoodReady(int cx, int cz)
    {
        for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
                if (!_world.HasChunk(cx + dx, cz + dz)) return false;
        return true;
    }

    void UploadFinishedMeshes(int ccx, int ccz)
    {
        int budget = UploadBudgetPerFrame;
        while (budget-- > 0 && _meshDone.TryDequeue(out var m))
        {
            _meshPending.Remove(m.Key);
            // superseded by a synchronous edit rebuild, or scrolled far away
            if (m.Version != _version.GetValueOrDefault(m.Key)) continue;
            if (Math.Max(Math.Abs(m.Key.Cx - ccx), Math.Abs(m.Key.Cz - ccz)) > ViewRadius + 1) continue;
            Upload(m.Key.Cx, m.Key.Cz, m.Verts, m.Indices, m.WaterVerts, m.WaterIndices);
        }
    }

    void UnloadFar(int ccx, int ccz)
    {
        foreach (var key in Meshes.Keys.ToList())
        {
            if (Math.Max(Math.Abs(key.Cx - ccx), Math.Abs(key.Cz - ccz)) > ViewRadius + 1)
            {
                _renderer.DeferDispose(Meshes[key]);
                Meshes.Remove(key);
                if (WaterMeshes.TryGetValue(key, out var water))
                {
                    _renderer.DeferDispose(water);
                    WaterMeshes.Remove(key);
                }
                _lightDirty.Remove(key);
                _version.Remove(key);
            }
        }
    }

    // ------------------------------------------------------------- drawing
    // Frustum culling: at large render distances thousands of chunk meshes
    // are resident, but only the ones inside the view cone must be submitted.
    // Without this a single frame's GPU work can exceed the OS watchdog
    // (TDR) and kill the device.

    Frustum _frustum;

    bool ChunkVisible(int cx, int cz) => _frustum.BoxVisible(
        new Vector3(cx * ChunkSize, 0, cz * ChunkSize),
        new Vector3(cx * ChunkSize + ChunkSize, WorldHeight, cz * ChunkSize + ChunkSize));

    public void Draw(Frustum frustum)
    {
        _frustum = frustum;
        foreach (var ((cx, cz), mesh) in Meshes)
        {
            if (mesh.IndexCount == 0 || !ChunkVisible(cx, cz)) continue;
            var model = Matrix4x4.CreateTranslation(cx * ChunkSize, 0, cz * ChunkSize);
            _renderer.DrawMesh(mesh, model, Vector4.One, textured: true, _atlas, lightmap: true);
        }
    }

    /// Translucent pass — call after all opaque geometry so blending is
    /// correct. Reuses the frustum passed to Draw this frame.
    public void DrawWater()
    {
        foreach (var ((cx, cz), mesh) in WaterMeshes)
        {
            if (!ChunkVisible(cx, cz)) continue;
            var model = Matrix4x4.CreateTranslation(cx * ChunkSize, 0, cz * ChunkSize);
            _renderer.DrawMesh(mesh, model, WaterTint, textured: true, _atlas, lightmap: true);
        }
    }
}
