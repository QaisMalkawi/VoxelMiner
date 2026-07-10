namespace VoxelMiner.Engine;

/// Shared unit meshes: shaded/textured cubes (animals, particles) and a
/// wireframe cube (block highlight).
public static class Primitives
{
    // unit cube faces, extents ±0.5; corners listed bottom, bottom, top, top
    static readonly (float[] A, float[] B, float[] C, float[] D, float Light)[] CubeFaces =
    {
        (new[] {-.5f,-.5f,-.5f}, new[] {-.5f,-.5f,.5f}, new[] {-.5f,.5f,.5f}, new[] {-.5f,.5f,-.5f}, 0.80f), // -X
        (new[] {.5f,-.5f,.5f}, new[] {.5f,-.5f,-.5f}, new[] {.5f,.5f,-.5f}, new[] {.5f,.5f,.5f}, 0.80f),     // +X (front)
        (new[] {-.5f,-.5f,-.5f}, new[] {.5f,-.5f,-.5f}, new[] {.5f,-.5f,.5f}, new[] {-.5f,-.5f,.5f}, 0.55f), // -Y
        (new[] {-.5f,.5f,.5f}, new[] {.5f,.5f,.5f}, new[] {.5f,.5f,-.5f}, new[] {-.5f,.5f,-.5f}, 1.00f),     // +Y
        (new[] {.5f,-.5f,-.5f}, new[] {-.5f,-.5f,-.5f}, new[] {-.5f,.5f,-.5f}, new[] {.5f,.5f,-.5f}, 0.68f), // -Z
        (new[] {-.5f,-.5f,.5f}, new[] {.5f,-.5f,.5f}, new[] {.5f,.5f,.5f}, new[] {-.5f,.5f,.5f}, 0.68f),     // +Z
    };
    const int FrontFaceIndex = 1; // +X, where heads point

    /// Unit cube with per-face shading in vertex color and uv = 0 (drawn untextured).
    public static Mesh CreateShadedCube(VulkanContext ctx) => BuildCube(ctx, null, 0, 1);

    /// Unit cube sampling one tile of a single-row atlas per face;
    /// the +X face uses frontTile (eyes/snout), the rest use sideTile.
    public static Mesh CreateTexturedCube(VulkanContext ctx, int sideTile, int frontTile, int tileCount) =>
        BuildCube(ctx, sideTile, frontTile, tileCount);

    static Mesh BuildCube(VulkanContext ctx, int? sideTile, int frontTile, int tileCount)
    {
        var verts = new List<float>();
        var indices = new List<uint>();
        for (int f = 0; f < CubeFaces.Length; f++)
        {
            var (a, b, c, d, light) = CubeFaces[f];
            float u0 = 0, v0 = 0, u1 = 0, v1 = 0;
            if (sideTile is { } side)
            {
                int tile = f == FrontFaceIndex ? frontTile : side;
                u0 = (tile + 0.02f) / tileCount;
                u1 = (tile + 0.98f) / tileCount;
                v0 = 0.02f;
                v1 = 0.98f;
            }
            uint baseIndex = (uint)(verts.Count / 8);
            var corners = new[] { (P: a, U: u0, V: v1), (P: b, U: u1, V: v1), (P: c, U: u1, V: v0), (P: d, U: u0, V: v0) };
            foreach (var (p, u, v) in corners)
                verts.AddRange(new[] { p[0], p[1], p[2], u, v, light, light, light });
            indices.AddRange(new[] { baseIndex, baseIndex + 1, baseIndex + 2, baseIndex, baseIndex + 2, baseIndex + 3 });
        }
        return Mesh.Create(ctx, verts.ToArray(), indices.ToArray());
    }

    /// Unit cube wireframe (0..1), 12 edges as a line list.
    public static Mesh CreateLineCube(VulkanContext ctx)
    {
        float[][] corners =
        {
            new[] {0f,0f,0f}, new[] {1f,0f,0f}, new[] {1f,0f,1f}, new[] {0f,0f,1f},
            new[] {0f,1f,0f}, new[] {1f,1f,0f}, new[] {1f,1f,1f}, new[] {0f,1f,1f},
        };
        int[] edges = { 0,1, 1,2, 2,3, 3,0, 4,5, 5,6, 6,7, 7,4, 0,4, 1,5, 2,6, 3,7 };
        var verts = new List<float>();
        foreach (var c in corners) verts.AddRange(new[] { c[0], c[1], c[2], 0f, 0f, 1f, 1f, 1f });
        return Mesh.Create(ctx, verts.ToArray(), edges.Select(e => (uint)e).ToArray());
    }
}

/// Lazily built cache of textured unit cubes keyed by (side, front) tile pair.
public sealed class CubeMeshCache : IDisposable
{
    readonly VulkanContext _ctx;
    readonly int _tileCount;
    readonly Dictionary<(int Side, int Front), Mesh> _cache = new();

    public CubeMeshCache(VulkanContext ctx, int tileCount)
    {
        _ctx = ctx;
        _tileCount = tileCount;
    }

    public Mesh Get(int sideTile, int frontTile)
    {
        if (!_cache.TryGetValue((sideTile, frontTile), out var mesh))
            _cache[(sideTile, frontTile)] = mesh = Primitives.CreateTexturedCube(_ctx, sideTile, frontTile, _tileCount);
        return mesh;
    }

    public void Dispose()
    {
        foreach (var mesh in _cache.Values) mesh.Dispose();
        _cache.Clear();
    }
}
