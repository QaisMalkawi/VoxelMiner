namespace VoxelMiner.Engine;

/// Builds the runtime texture atlases by loading one PNG per texture from the
/// assets tree and packing them together. Every block tile, item icon, mob
/// tile, and HUD icon lives in its own file (assets/textures/blocks,
/// assets/icons, assets/textures/mobs), so swapping any texture is just
/// replacing a PNG. Files absent on disk are regenerated from the procedural
/// source (TextureGen) and written back, so a deleted or fresh checkout
/// self-heals on first run.
public static class TextureFiles
{
    public const int TileSize = 16;

    /// Loads dir/name.png (rescaled to `size` if needed); if it's missing,
    /// runs `gen` to produce the pixels and writes the PNG so it exists next
    /// time. A null gen with no file yields a magenta "missing" placeholder.
    public static byte[] LoadOrBake(string dir, string name, Func<byte[]> gen, int size = TileSize)
    {
        string path = Path.Combine(dir, name + ".png");
        if (File.Exists(path))
        {
            var (rgba, w, h) = PngLoader.Load(path);
            return w == size && h == size ? rgba : Scale(rgba, w, h, size);
        }
        var baked = gen?.Invoke() ?? Missing(size);
        if (gen != null) PngLoader.Save(path, baked, size, size);
        return baked;
    }

    /// Writes name.png (overwriting), used by the bake tool to (re)emit the
    /// canonical file for a generated texture.
    public static void Write(string dir, string name, byte[] rgba, int size = TileSize) =>
        PngLoader.Save(Path.Combine(dir, name + ".png"), rgba, size, size);

    /// Crops one `size`-square tile out of a single-row sheet at tile index i.
    public static byte[] CropTile(byte[] sheet, int sheetW, int i, int size = TileSize)
    {
        var tile = new byte[size * size * 4];
        int x0 = i * size;
        for (int y = 0; y < size; y++)
            Array.Copy(sheet, (y * sheetW + x0) * 4, tile, y * size * 4, size * 4);
        return tile;
    }

    /// Nearest-neighbour rescale so a swapped-in PNG of any size still packs.
    public static byte[] Scale(byte[] src, int sw, int sh, int size)
    {
        var dst = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int sx = x * sw / size, sy = y * sh / size;
                Array.Copy(src, (sy * sw + sx) * 4, dst, (y * size + x) * 4, 4);
            }
        return dst;
    }

    static byte[] Missing(int size)
    {
        var px = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                bool magenta = (x / 2 + y / 2) % 2 == 0;
                int i = (y * size + x) * 4;
                px[i] = (byte)(magenta ? 236 : 0); px[i + 1] = 0; px[i + 2] = (byte)(magenta ? 236 : 0); px[i + 3] = 255;
            }
        return px;
    }

    /// Copies a tile into a single-row atlas at horizontal tile slot `slot`.
    public static void BlitRow(byte[] atlas, int atlasW, int slot, byte[] tile, int size = TileSize)
    {
        int x0 = slot * size;
        for (int y = 0; y < size; y++)
            Array.Copy(tile, y * size * 4, atlas, (y * atlasW + x0) * 4, size * 4);
    }
}

/// The world block atlas: a single row of 16px tiles indexed by tile number
/// (see TextureGen.BlockTileNames). Loaded from assets/textures/blocks.
public static class BlockTextures
{
    public static string Dir(string assetsDir) => Path.Combine(assetsDir, "textures", "blocks");

    /// Builds and registers the packed block atlas; returns the tile count so
    /// callers can size their UV math (must equal ChunkMesher.AtlasTiles).
    public static (TextureHandle Handle, GpuTexture Tex, int TileCount) Build(Renderer r, string assetsDir)
    {
        var names = TextureGen.BlockTileNames;
        int n = names.Length, size = TextureFiles.TileSize;
        string dir = Dir(assetsDir);
        var legacy = LegacySheet(assetsDir);

        var atlas = new byte[n * size * size * 4];
        for (int i = 0; i < n; i++)
        {
            int idx = i;
            var tile = TextureFiles.LoadOrBake(dir, names[i], () => Source(idx, size, legacy), size);
            TextureFiles.BlitRow(atlas, n * size, i, tile, size);
        }
        var tex = new GpuTexture(r.Ctx, atlas, n * size, size);
        return (r.RegisterTexture(tex), tex, n);
    }

    /// The procedural/legacy source for a tile, used only when its PNG is
    /// missing: tiles 0..13 crop the old atlas.png, 14+ come from TextureGen.
    static byte[] Source(int i, int size, (byte[] rgba, int w, int h) legacy) =>
        i < 14
            ? legacy.rgba != null ? TextureFiles.CropTile(legacy.rgba, legacy.w, i, size) : null
            : TextureGen.GenBlockTile(i, size);

    static (byte[] rgba, int w, int h) LegacySheet(string assetsDir)
    {
        string p = Path.Combine(assetsDir, "textures", "atlas.png");
        return File.Exists(p) ? PngLoader.Load(p) : (null, 0, 0);
    }

    /// Writes every block tile to its own PNG (overwriting), splitting the
    /// legacy sheet and baking the generated tiles. Bake-tool only.
    public static void Bake(string assetsDir)
    {
        var names = TextureGen.BlockTileNames;
        int size = TextureFiles.TileSize;
        string dir = Dir(assetsDir);
        var legacy = LegacySheet(assetsDir);
        for (int i = 0; i < names.Length; i++)
        {
            var tile = Source(i, size, legacy);
            if (tile != null) TextureFiles.Write(dir, names[i], tile, size);
        }
    }
}

/// The mob atlas: a single row of 16px tiles for pig/sheep/chicken parts,
/// indexed by tile number (see AnimalDefs). Loaded from assets/textures/mobs,
/// falling back to the legacy animals.png sheet.
public static class MobTextures
{
    // tile index → file stem; order matches the legacy animals.png columns
    public static readonly string[] Names =
    {
        "pig_body", "pig_face", "pig_snout", "pig_leg",
        "sheep_body", "sheep_head", "sheep_face",
        "chicken_body", "chicken_face", "chicken_beak", "chicken_leg",
    };

    public static string Dir(string assetsDir) => Path.Combine(assetsDir, "textures", "mobs");

    public static (TextureHandle Handle, GpuTexture Tex, int TileCount) Build(Renderer r, string assetsDir)
    {
        int n = Names.Length, size = TextureFiles.TileSize;
        string dir = Dir(assetsDir);
        var legacy = LegacySheet(assetsDir);

        var atlas = new byte[n * size * size * 4];
        for (int i = 0; i < n; i++)
        {
            int idx = i;
            var tile = TextureFiles.LoadOrBake(dir, Names[i],
                () => legacy.rgba != null ? TextureFiles.CropTile(legacy.rgba, legacy.w, idx, size) : null, size);
            TextureFiles.BlitRow(atlas, n * size, i, tile, size);
        }
        var tex = new GpuTexture(r.Ctx, atlas, n * size, size);
        return (r.RegisterTexture(tex), tex, n);
    }

    static (byte[] rgba, int w, int h) LegacySheet(string assetsDir)
    {
        string p = Path.Combine(assetsDir, "textures", "animals.png");
        return File.Exists(p) ? PngLoader.Load(p) : (null, 0, 0);
    }

    public static void Bake(string assetsDir)
    {
        var legacy = LegacySheet(assetsDir);
        if (legacy.rgba == null) return;
        int size = TextureFiles.TileSize;
        string dir = Dir(assetsDir);
        for (int i = 0; i < Names.Length; i++)
            TextureFiles.Write(dir, Names[i], TextureFiles.CropTile(legacy.rgba, legacy.w, i, size), size);
    }
}

/// Dev-only: regenerates every swappable PNG into the source assets tree so
/// the individual files can be committed and hand-edited. Invoked via the
/// --bake-textures CLI flag.
public static class TextureBaker
{
    public static void BakeAll(string assetsDir)
    {
        Console.WriteLine($"Baking textures into {assetsDir} ...");
        BlockTextures.Bake(assetsDir);
        MobTextures.Bake(assetsDir);
        IconAtlas.Bake(Path.Combine(assetsDir, "icons"));
        Console.WriteLine("Done. Block, mob, and icon PNGs written.");
    }
}
