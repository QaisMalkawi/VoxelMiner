using System.Numerics;

namespace VoxelMiner.UI;

using VoxelMiner.Engine;
using VoxelMiner.Gameplay;
using VoxelMiner.World;
using static VoxelMiner.Core.Constants;
using BlockId = VoxelMiner.Core.BlockId;

/// A top-down 2D map of the world around the player, separate from the 3D
/// scene: rebuilt into a texture whenever it's opened. Columns in loaded
/// chunks show their actual top block (player edits, trees, village houses);
/// everywhere else falls back to the terrain generator's biome colors drawn
/// dimmer, reading as "unexplored". Both are shaded by height, water by
/// depth. North (-Z) is up.
public sealed class MapView : IDisposable
{
    public const int TexSize = 1024;      // map texture resolution
    public const int BlocksPerPixel = 2; // one pixel covers a 2x2 block area
    public const int SpanBlocks = TexSize * BlocksPerPixel;

    const float UnexploredDim = 0.72f;

    static readonly Vector4 White = new(1, 1, 1, 1);
    static readonly Vector4 Backdrop = new(0.04f, 0.06f, 0.1f, 0.88f);

    readonly GameWorld _world;
    readonly TerrainGenerator _terrain;
    readonly GpuTexture _texture;
    readonly TextureHandle _handle;
    readonly byte[] _pixels = new byte[TexSize * TexSize * 4];
    readonly List<(int X, int Z)> _villages = new();

    int _centerX, _centerZ;

    public MapView(Renderer renderer, GameWorld world, TerrainGenerator terrain)
    {
        _world = world;
        _terrain = terrain;
        _texture = new GpuTexture(renderer.Ctx, _pixels, TexSize, TexSize);
        _handle = renderer.RegisterTexture(_texture);
    }

    // ------------------------------------------------------------- build

    /// Recomputes the map texture centered on the player. Synchronous — a
    /// brief hitch when the map key is pressed.
    public void Rebuild(Vector3 playerPos)
    {
        _centerX = (int)MathF.Floor(playerPos.X);
        _centerZ = (int)MathF.Floor(playerPos.Z);
        for (int py = 0; py < TexSize; py++)
            for (int px = 0; px < TexSize; px++)
            {
                int gx = _centerX + (px - TexSize / 2) * BlocksPerPixel;
                int gz = _centerZ + (py - TexSize / 2) * BlocksPerPixel;
                var (r, g, b) = ColumnColor(gx, gz);
                int i = (py * TexSize + px) * 4;
                _pixels[i] = r;
                _pixels[i + 1] = g;
                _pixels[i + 2] = b;
                _pixels[i + 3] = 255;
            }
        _texture.Update(_pixels);

        _villages.Clear();
        int half = SpanBlocks / 2;
        _villages.AddRange(_terrain.VillagesIn(_centerX - half, _centerZ - half, _centerX + half, _centerZ + half));
    }

    (byte R, byte G, byte B) ColumnColor(int gx, int gz)
    {
        int cx = FloorDiv(gx, ChunkSize), cz = FloorDiv(gz, ChunkSize);
        if (_world.Chunks.TryGetValue((cx, cz), out var data))
        {
            int lx = gx - cx * ChunkSize, lz = gz - cz * ChunkSize;
            for (int y = WorldHeight - 1; y >= 0; y--)
            {
                int id = data[BlockIndex(lx, y, lz)];
                if (id == BlockId.Air) continue;
                if (id == BlockId.Water)
                {
                    int floor = y;
                    while (floor > 0 && data[BlockIndex(lx, floor - 1, lz)] == BlockId.Water) floor--;
                    return MapColors.Water(y - floor + 1);
                }
                var c = BlockRegistry.Blocks[id].ParticleColor * 255f;
                return MapColors.HeightShade(((byte)c.X, (byte)c.Y, (byte)c.Z), y, 1f);
            }
            return (0, 0, 0);
        }

        // ungenerated: the terrain generator's view, dimmed as unexplored
        return MapColors.Generated(_terrain, gx, gz, UnexploredDim);
    }

    // ------------------------------------------------------------- draw

    public void Draw(HudBatcher batch, FontAtlas font, TextureHandle white, float screenW, float screenH, Player player)
    {
        batch.SolidQuad(white, 0, 0, screenW, screenH, Backdrop);

        float size = MathF.Min(screenH, screenW) - 110;
        float x0 = (screenW - size) / 2, y0 = (screenH - size) / 2 + 12;
        batch.SolidQuad(white, x0 - 3, y0 - 3, size + 6, size + 6, new Vector4(1, 1, 1, 0.28f));
        batch.Quad(_handle, x0, y0, size, size, 0, 0, 1, 1, White);

        float scale = size / SpanBlocks; // screen pixels per world block
        float ToScreenX(float wx) => x0 + size / 2 + (wx - _centerX) * scale;
        float ToScreenY(float wz) => y0 + size / 2 + (wz - _centerZ) * scale;

        foreach (var (vx, vz) in _villages)
        {
            float mx = ToScreenX(vx), my = ToScreenY(vz);
            batch.SolidQuad(white, mx - 4, my - 4, 8, 8, new Vector4(0.35f, 0.22f, 0.1f, 1));
            batch.SolidQuad(white, mx - 3, my - 3, 6, 6, new Vector4(0.92f, 0.76f, 0.45f, 1));
        }

        // player: red dot with a small heading dot in the look direction
        float px = ToScreenX(player.Pos.X), py = ToScreenY(player.Pos.Z);
        batch.SolidQuad(white, px - 4, py - 4, 8, 8, White);
        batch.SolidQuad(white, px - 3, py - 3, 6, 6, new Vector4(0.85f, 0.15f, 0.15f, 1));
        float fx = -MathF.Sin(player.Yaw), fz = -MathF.Cos(player.Yaw);
        batch.SolidQuad(white, px + fx * 10 - 2, py + fz * 10 - 2, 4, 4, new Vector4(1, 0.9f, 0.3f, 1));

        font.Draw(batch, "World Map", x0, y0 - 26, White);
        string info = $"{SpanBlocks}x{SpanBlocks} blocks, north up";
        font.Draw(batch, info, x0 + size - font.Measure(info), y0 - 26, new Vector4(1, 1, 1, 0.7f));
        string hint = "M to close - dim terrain is ungenerated";
        font.Draw(batch, hint, x0 + (size - font.Measure(hint)) / 2, y0 + size + 10, new Vector4(1, 1, 1, 0.7f));
    }

    public void Dispose() => _texture.Dispose();

    static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);
}
