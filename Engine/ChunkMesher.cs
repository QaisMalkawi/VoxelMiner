namespace VoxelMiner.Engine;

using VoxelMiner.World;
using static VoxelMiner.Core.Constants;

/// Builds merged geometry for one chunk: hidden-face culling, per-face
/// directional shade, corner ambient occlusion, and smooth sky/torch light
/// baked as vertex color (warm tint for torch light, neutral for sky).
/// Water goes into a separate translucent submesh with a Minecraft-style
/// surface: height falls off with distance from its source/falling column,
/// a cell under another water cell is forced to full height, and shared
/// vertices are averaged across neighbours so the surface has no seams.
/// Torches are little posts lit by their own cell.
/// Vertex layout: pos3, uv2, color3.
public sealed class ChunkMesher
{
    public const int AtlasTiles = 32;
    public const int TorchTile = 14;
    public const int WaterTile = 15;
    public const int TallGrassTile = 16;
    public const int FlowerYellowTile = 17;
    public const int FlowerRedTile = 18;

    // dir, tile index into a block's [top, bottom, side], corners: x y z u v
    static readonly (int[] Dir, int Face, int[][] Corners)[] Faces =
    {
        (new[] {-1, 0, 0}, 2, new[] { new[] {0,1,0,0,1}, new[] {0,0,0,0,0}, new[] {0,1,1,1,1}, new[] {0,0,1,1,0} }),
        (new[] { 1, 0, 0}, 2, new[] { new[] {1,1,1,0,1}, new[] {1,0,1,0,0}, new[] {1,1,0,1,1}, new[] {1,0,0,1,0} }),
        (new[] { 0,-1, 0}, 1, new[] { new[] {1,0,1,1,0}, new[] {0,0,1,0,0}, new[] {1,0,0,1,1}, new[] {0,0,0,0,1} }),
        (new[] { 0, 1, 0}, 0, new[] { new[] {0,1,1,1,1}, new[] {1,1,1,0,1}, new[] {0,1,0,1,0}, new[] {1,1,0,0,0} }),
        (new[] { 0, 0,-1}, 2, new[] { new[] {1,0,0,0,0}, new[] {0,0,0,1,0}, new[] {1,1,0,0,1}, new[] {0,1,0,1,1} }),
        (new[] { 0, 0, 1}, 2, new[] { new[] {0,0,1,0,0}, new[] {1,0,1,1,0}, new[] {0,1,1,0,1}, new[] {1,1,1,1,1} }),
    };
    static readonly float[] FaceShade = { 0.80f, 0.80f, 0.55f, 1.00f, 0.68f, 0.68f };
    static readonly float[] AoCurve = { 1.0f, 0.78f, 0.6f, 0.46f };

    readonly GameWorld _world;
    readonly Fluids _fluids;

    public ChunkMesher(GameWorld world, Fluids fluids)
    {
        _world = world;
        _fluids = fluids;
    }

    public (float[] Vertices, uint[] Indices, float[] WaterVertices, uint[] WaterIndices) Build(int cx, int cz)
    {
        var data = _world.Chunks[(cx, cz)];
        var verts = new List<float>(16384);
        var indices = new List<uint>(8192);
        var waterVerts = new List<float>(2048);
        var waterIndices = new List<uint>(1024);

        for (int y = 0; y < WorldHeight; y++)
            for (int lz = 0; lz < ChunkSize; lz++)
                for (int lx = 0; lx < ChunkSize; lx++)
                {
                    int b = data[BlockIndex(lx, y, lz)];
                    if (b == Core.BlockId.Air) continue;
                    int gx = cx * ChunkSize + lx, gz = cz * ChunkSize + lz;
                    if (b == Core.BlockId.Water) { AddWater(waterVerts, waterIndices, gx, y, gz, lx, lz); continue; }
                    if (b == Core.BlockId.Torch) { AddTorch(verts, indices, gx, y, gz, lx, lz); continue; }
                    if (BlockRegistry.IsVegetation(b)) { AddCross(verts, indices, VegetationTile(b), gx, y, gz, lx, lz); continue; }

                    var def = BlockRegistry.Blocks[b];
                    if (def.Shape != BlockShape.Cube && def.Shape != BlockShape.Furnace)
                    {
                        AddShaped(verts, indices, b, def, gx, y, gz, lx, lz);
                        continue;
                    }
                    for (int f = 0; f < 6; f++)
                    {
                        var (dir, faceIdx, corners) = Faces[f];
                        int nb = _world.GetBlock(gx + dir[0], y + dir[1], gz + dir[2]);
                        if (BlockRegistry.IsOpaque(nb)) continue;
                        // two leaf blocks would emit coplanar quads at their shared
                        // face — identical texture, but they'd z-fight; cull like
                        // water does against water
                        if (b == Core.BlockId.Leaves && nb == Core.BlockId.Leaves) continue;
                        // furnaces carry a 4th tile for their front face
                        int tile = def.Tiles.Length > 3 && f == FaceOfFacing(def.Facing) ? def.Tiles[3] : def.Tiles[faceIdx];
                        uint baseIndex = (uint)(verts.Count / 8);
                        for (int ci = 0; ci < 4; ci++)
                        {
                            var c = corners[ci];
                            verts.Add(lx + c[0]);
                            verts.Add(y + c[1]);
                            verts.Add(lz + c[2]);
                            verts.Add((tile + 0.02f + c[3] * 0.96f) / AtlasTiles);
                            // JS/WebGL UVs have v=0 at the bottom; Vulkan samples v=0 at the top
                            verts.Add(1f - (0.02f + c[4] * 0.96f));
                            var (r, g, bl) = VertexLight(gx, y, gz, dir, c, FaceShade[f]);
                            verts.Add(r);
                            verts.Add(g);
                            verts.Add(bl);
                        }
                        AddQuadIndices(indices, baseIndex);
                    }
                }
        return (verts.ToArray(), indices.ToArray(), waterVerts.ToArray(), waterIndices.ToArray());
    }

    static void AddQuadIndices(List<uint> indices, uint baseIndex)
    {
        indices.Add(baseIndex);
        indices.Add(baseIndex + 1);
        indices.Add(baseIndex + 2);
        indices.Add(baseIndex + 2);
        indices.Add(baseIndex + 1);
        indices.Add(baseIndex + 3);
    }
    static void AddQuadIndicesFlipped(List<uint> indices, uint baseIndex)
    {
        indices.Add(baseIndex);
        indices.Add(baseIndex + 2);
        indices.Add(baseIndex + 1);
        indices.Add(baseIndex + 1);
        indices.Add(baseIndex + 2);
        indices.Add(baseIndex + 3);
    }

    // ------------------------------------------------------------- water

    void AddWater(List<float> verts, List<uint> indices, int gx, int y, int gz, int lx, int lz)
    {
        for (int f = 0; f < 6; f++)
        {
            var (dir, _, corners) = Faces[f];
            int nb = _world.GetBlock(gx + dir[0], y + dir[1], gz + dir[2]);
            if (nb == Core.BlockId.Water || BlockRegistry.IsOpaque(nb)) continue; // draw only against air/torch

            uint baseIndex = (uint)(verts.Count / 8);
            for (int ci = 0; ci < 4; ci++)
            {
                var c = corners[ci];
                // only the top corners of a face taper; bottom stays flush
                float cy = c[1] == 1 ? VertexHeight(y, gx + c[0], gz + c[2]) : 0f;
                verts.Add(lx + c[0]);
                verts.Add(y + cy);
                verts.Add(lz + c[2]);
                verts.Add((WaterTile + 0.02f + c[3] * 0.96f) / AtlasTiles);
                verts.Add(1f - (0.02f + c[4] * 0.96f));
                var (r, g, bl) = VertexLight(gx, y, gz, dir, c, FaceShade[f]);
                verts.Add(r);
                verts.Add(g);
                verts.Add(bl);
            }
            AddQuadIndices(indices, baseIndex);
        }
    }

    /// Height (0..1) a single water cell would render at, ignoring neighbours:
    /// full whenever the cell above it leaves no room to taper — another
    /// water cell (keeps a column connected top-to-bottom) or any solid
    /// block (flush against a ceiling, like Minecraft's capped water) —
    /// otherwise falls off with flow distance.
    float RawWaterHeight(int gx, int y, int gz)
    {
        int above = _world.GetBlock(gx, y + 1, gz);
        if (above == Core.BlockId.Water || BlockRegistry.IsOpaque(above)) return 1f;
        int level = Math.Clamp(_fluids.LevelAt(gx, y, gz), 0, 7);
        return (8f - level) / 9f;
    }

    /// Averages RawWaterHeight over the (up to 4) water cells that share a
    /// given top-face grid vertex. Using absolute grid coordinates means any
    /// of those cells computes the same value for this vertex, so adjoining
    /// quads (including the top/side seam of a single cell) always match up
    /// — no cracks between differently-tapered neighbours.
    float VertexHeight(int y, int vx, int vz)
    {
        float sum = 0;
        int count = 0;
        for (int dx = -1; dx <= 0; dx++)
            for (int dz = -1; dz <= 0; dz++)
            {
                int cx = vx + dx, cz = vz + dz;
                if (_world.GetBlock(cx, y, cz) != Core.BlockId.Water) continue;
                sum += RawWaterHeight(cx, y, cz);
                count++;
            }
        return count > 0 ? sum / count : 1f;
    }

    // ------------------------------------------------------------- torch

    void AddTorch(List<float> verts, List<uint> indices, int gx, int y, int gz, int lx, int lz)
    {
        const float lo = 7f / 16f, hi = 9f / 16f, height = 10f / 16f;
        // a torch is lit by its own cell (which holds its emission)
        var (r, g, b) = CombineLight(
            _world.Lighting.GetSky(gx, y, gz),
            _world.Lighting.GetBlockLight(gx, y, gz));

        for (int f = 0; f < 6; f++)
        {
            var (_, _, corners) = Faces[f];
            uint baseIndex = (uint)(verts.Count / 8);
            for (int ci = 0; ci < 4; ci++)
            {
                var c = corners[ci];
                verts.Add(lx + lo + c[0] * (hi - lo));
                verts.Add(y + c[1] * height);
                verts.Add(lz + lo + c[2] * (hi - lo));
                // sides sample the 2px stick strip; top the ember; bottom the base
                float uFrac = (7f + c[3] * 2f) / 16f;
                float vFrac = f switch
                {
                    3 => (6f + (1 - c[4]) * 2f) / 16f,  // top face: ember rows
                    2 => (14f + (1 - c[4]) * 2f) / 16f, // bottom face: base rows
                    _ => 1f - c[4] * height,            // sides: full strip
                };
                verts.Add((TorchTile + uFrac) / AtlasTiles);
                verts.Add(vFrac);
                float shade = FaceShade[f];
                verts.Add(r * shade);
                verts.Add(g * shade);
                verts.Add(b * shade);
            }
            AddQuadIndices(indices, baseIndex);
        }
    }

    // ------------------------------------------------------------- shaped blocks

    /// Face index (into Faces) that looks along a facing direction
    /// (0=N/-Z, 1=E/+X, 2=S/+Z, 3=W/-X).
    static int FaceOfFacing(int facing) => facing switch { 0 => 4, 1 => 1, 2 => 5, _ => 0 };

    /// Spatial position on a face to tile-local UV, so partial faces sample
    /// the matching sub-rectangle of their texture instead of stretching it.
    static (float U, float V) FaceUv(int f, float x, float y, float z) => f switch
    {
        0 => (z, y),
        1 => (1 - z, y),
        2 => (x, 1 - z),
        3 => (1 - x, z),
        4 => (1 - x, y),
        _ => (x, y),
    };

    /// Stairs, slabs, doors, trapdoors, chests: meshed straight from their
    /// collision boxes, lit by their own cell (partial blocks don't absorb
    /// light, so the cell always has a valid value).
    void AddShaped(List<float> verts, List<uint> indices, int b, BlockDef def, int gx, int y, int gz, int lx, int lz)
    {
        var boxes = BlockRegistry.CollisionBoxes(b);
        var (r, g, bl) = CombineLight(_world.Lighting.GetSky(gx, y, gz), _world.Lighting.GetBlockLight(gx, y, gz));
        for (int i = 0; i < boxes.Length; i++)
        {
            // a stair's upper box sits on the lower slab; its bottom face would
            // z-fight with the slab's top face, so drop it
            int skipMask = def.Shape == BlockShape.Stairs && i == 1 ? 1 << 2 : 0;
            AddBox(verts, indices, def, boxes[i], gx, y, gz, lx, lz, r, g, bl, skipMask);
        }
    }

    void AddBox(List<float> verts, List<uint> indices, BlockDef def, Box box,
        int gx, int y, int gz, int lx, int lz, float lr, float lg, float lb, int skipMask)
    {
        Span<float> min = stackalloc float[] { box.X0, box.Y0, box.Z0 };
        Span<float> max = stackalloc float[] { box.X1, box.Y1, box.Z1 };
        for (int f = 0; f < 6; f++)
        {
            if ((skipMask & (1 << f)) != 0) continue;
            var (dir, faceIdx, corners) = Faces[f];
            // faces flush with the cell boundary cull like cube faces
            int axis = dir[0] != 0 ? 0 : dir[1] != 0 ? 1 : 2;
            bool flush = dir[axis] > 0 ? max[axis] >= 1f - 1e-4f : min[axis] <= 1e-4f;
            if (flush && BlockRegistry.IsOpaque(_world.GetBlock(gx + dir[0], y + dir[1], gz + dir[2]))) continue;

            int tile = def.Tiles.Length > 3 && f == FaceOfFacing(def.Facing) ? def.Tiles[3] : def.Tiles[faceIdx];
            uint baseIndex = (uint)(verts.Count / 8);
            for (int ci = 0; ci < 4; ci++)
            {
                var c = corners[ci];
                float px = c[0] == 0 ? min[0] : max[0];
                float py = c[1] == 0 ? min[1] : max[1];
                float pz = c[2] == 0 ? min[2] : max[2];
                var (uf, vf) = FaceUv(f, px, py, pz);
                verts.Add(lx + px);
                verts.Add(y + py);
                verts.Add(lz + pz);
                verts.Add((tile + 0.02f + uf * 0.96f) / AtlasTiles);
                verts.Add(1f - (0.02f + vf * 0.96f));
                float shade = FaceShade[f];
                verts.Add(lr * shade);
                verts.Add(lg * shade);
                verts.Add(lb * shade);
            }
            AddQuadIndices(indices, baseIndex);
        }
    }

    // ------------------------------------------------------------- vegetation

    static int VegetationTile(int id) => id switch
    {
        Core.BlockId.TallGrass => TallGrassTile,
        Core.BlockId.FlowerYellow => FlowerYellowTile,
        _ => FlowerRedTile,
    };

    // two quads crossed in an X, corners: x y z u v (full block footprint, diagonal)
    static readonly int[][][] CrossQuads =
    {
        new[] { new[] {0,1,0,0,1}, new[] {0,0,0,0,0}, new[] {1,1,1,1,1}, new[] {1,0,1,1,0} },
        new[] { new[] {1,1,0,0,1}, new[] {1,0,0,0,0}, new[] {0,1,1,1,1}, new[] {0,0,1,1,0} },
    };

    /// Grass tufts and flowers: a lit, non-solid billboard cross textured with
    /// a cutout tile (transparent background, discarded in the shader). The
    /// world pipeline culls no faces, so one quad per plane is visible from
    /// both sides already.
    void AddCross(List<float> verts, List<uint> indices, int tile, int gx, int y, int gz, int lx, int lz)
    {
        var (r, g, b) = CombineLight(_world.Lighting.GetSky(gx, y, gz), _world.Lighting.GetBlockLight(gx, y, gz));
        foreach (var quad in CrossQuads)
        {
            uint baseIndex = (uint)(verts.Count / 8);
            foreach (var c in quad)
            {
                verts.Add(lx + c[0]);
                verts.Add(y + c[1]);
                verts.Add(lz + c[2]);
                verts.Add((tile + 0.02f + c[3] * 0.96f) / AtlasTiles);
                verts.Add(1f - (0.02f + c[4] * 0.96f));
                verts.Add(r);
                verts.Add(g);
                verts.Add(b);
            }
            AddQuadIndices(indices, baseIndex);
            AddQuadIndicesFlipped(indices, baseIndex);
        }
    }

    // ------------------------------------------------------------- lighting

    /// Smooth per-vertex light: averages sky/block light of the 4 cells that
    /// share this vertex on the face's side (the same cells AO samples), then
    /// applies AO and the directional face shade.
    (float R, float G, float B) VertexLight(int gx, int gy, int gz, int[] n, int[] corner, float shade)
    {
        Span<int> axes = stackalloc int[2];
        int ai = 0;
        for (int i = 0; i < 3; i++) if (n[i] == 0) axes[ai++] = i;
        Span<int> s = stackalloc int[3];
        Span<int> t = stackalloc int[3];
        s[axes[0]] = corner[axes[0]] == 1 ? 1 : -1;
        t[axes[1]] = corner[axes[1]] == 1 ? 1 : -1;
        int bx = gx + n[0], by = gy + n[1], bz = gz + n[2];

        bool o1 = _world.IsSolidAt(bx + s[0], by + s[1], bz + s[2]);
        bool o2 = _world.IsSolidAt(bx + t[0], by + t[1], bz + t[2]);
        bool o3 = _world.IsSolidAt(bx + s[0] + t[0], by + s[1] + t[1], bz + s[2] + t[2]);
        float ao = AoCurve[o1 && o2 ? 3 : (o1 ? 1 : 0) + (o2 ? 1 : 0) + (o3 ? 1 : 0)];

        var light = _world.Lighting;
        float sky = light.GetSky(bx, by, bz), blk = light.GetBlockLight(bx, by, bz);
        int count = 1;
        if (!o1) { sky += light.GetSky(bx + s[0], by + s[1], bz + s[2]); blk += light.GetBlockLight(bx + s[0], by + s[1], bz + s[2]); count++; }
        if (!o2) { sky += light.GetSky(bx + t[0], by + t[1], bz + t[2]); blk += light.GetBlockLight(bx + t[0], by + t[1], bz + t[2]); count++; }
        if (!(o1 && o2) && !o3)
        {
            sky += light.GetSky(bx + s[0] + t[0], by + s[1] + t[1], bz + s[2] + t[2]);
            blk += light.GetBlockLight(bx + s[0] + t[0], by + s[1] + t[1], bz + s[2] + t[2]);
            count++;
        }
        var (r, g, b) = CombineLight(sky / count, blk / count);
        float l = shade * ao;
        return (r * l, g * l, b * l);
    }

    /// Maps sky/block light levels to an RGB multiplier. Sky light is neutral;
    /// block light is warm, so torches tint their surroundings orange.
    static (float R, float G, float B) CombineLight(float sky, float blk)
    {
        float cs = Curve(sky), cb = Curve(blk);
        return (MathF.Max(cs, cb),
                MathF.Max(cs, cb * 0.85f),
                MathF.Max(cs, cb * 0.62f));
    }

    static float Curve(float level) => 0.02f + 0.98f * MathF.Pow(level / MaxLight, 1.5f);
}
