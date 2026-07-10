using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;

namespace VoxelMiner.Engine;

/// PNG loading, the runtime-composited icon atlas, and the rasterized font atlas.
public static class PngLoader
{
    public static (byte[] Rgba, int Width, int Height) Load(string path)
    {
        using var bmp = new Bitmap(path);
        return ToRgba(bmp);
    }

    public static (byte[] Rgba, int Width, int Height) ToRgba(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var bgra = new byte[data.Stride * bmp.Height];
        Marshal.Copy(data.Scan0, bgra, 0, bgra.Length);
        bmp.UnlockBits(data);

        var rgba = new byte[bmp.Width * bmp.Height * 4];
        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                int src = y * data.Stride + x * 4;
                int dst = (y * bmp.Width + x) * 4;
                rgba[dst] = bgra[src + 2];     // R
                rgba[dst + 1] = bgra[src + 1]; // G
                rgba[dst + 2] = bgra[src];     // B
                rgba[dst + 3] = bgra[src + 3]; // A
            }
        }
        return (rgba, bmp.Width, bmp.Height);
    }
}

/// Procedurally drawn pixels for blocks that have no PNG (torch, water,
/// vegetation).
public static class TextureGen
{
    /// Appends the torch, water, and vegetation tiles to the block atlas so
    /// the mesher can reference them as tiles 14..18, then the door, trapdoor,
    /// chest, and furnace tiles as 19..28. Vegetation tiles carry real alpha
    /// (0 = background) — the world shader discards those texels.
    public static (byte[] Rgba, int Width) ExtendAtlas(byte[] rgba, int width, int height)
    {
        int tile = height; // single-row atlas: tile size == atlas height
        var generated = new[] { TorchTile(tile), WaterTile(tile), GrassTile(tile),
            FlowerTile(tile, 235, 205, 60), FlowerTile(tile, 205, 60, 60),
            DoorTile(tile, top: true), DoorTile(tile, top: false), TrapdoorTileTex(tile),
            ChestTopTile(tile), ChestSideTile(tile, front: false), ChestSideTile(tile, front: true),
            FurnaceTopTile(tile), FurnaceSideTile(tile, FurnaceFace.Side),
            FurnaceSideTile(tile, FurnaceFace.Front), FurnaceSideTile(tile, FurnaceFace.FrontLit),
            SnowTileTex(tile), DryGrassTile(tile, top: true), DryGrassTile(tile, top: false) };
        int newW = width + generated.Length * tile;
        var pixels = new byte[newW * height * 4];
        for (int y = 0; y < height; y++)
            Array.Copy(rgba, y * width * 4, pixels, y * newW * 4, width * 4);
        for (int i = 0; i < generated.Length; i++)
            BlitTile(pixels, newW, width + i * tile, tile, generated[i]);
        return (pixels, newW);
    }

    static void BlitTile(byte[] atlas, int atlasW, int xOff, int tile, byte[] src)
    {
        for (int y = 0; y < tile; y++)
            Array.Copy(src, y * tile * 4, atlas, (y * atlasW + xOff) * 4, tile * 4);
    }

    static void Put(byte[] px, int size, int x, int y, int r, int g, int b, int a = 255)
    {
        int i = (y * size + x) * 4;
        px[i] = (byte)r; px[i + 1] = (byte)g; px[i + 2] = (byte)b; px[i + 3] = (byte)a;
    }

    /// Vertical stick strip with a glowing ember top; the mesher maps the
    /// stick columns onto the little torch post's faces.
    static byte[] TorchTile(int size)
    {
        var px = new byte[size * size * 4];
        int x0 = size * 7 / 16, x1 = size * 9 / 16;
        int emberTop = size * 6 / 16, emberBot = size * 8 / 16, baseRow = size * 14 / 16;
        for (int y = emberTop; y < size; y++)
            for (int x = x0; x < x1; x++)
            {
                bool edge = x == x0;
                if (y < emberBot) Put(px, size, x, y, 255, edge ? 170 : 214, 80);      // ember
                else if (y >= baseRow) Put(px, size, x, y, 92, 62, 34);                // base
                else Put(px, size, x, y, edge ? 118 : 146, edge ? 78 : 98, 42);        // stick
            }
        return px;
    }

    static byte[] WaterTile(int size)
    {
        var px = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                double n = Core.Noise.Hash2(x + 811, y * 31 + 47);
                int v = (int)(n * 18) - 9;
                double wave = Math.Sin((y + x * 0.5) * Math.PI / 4) * 6;
                Put(px, size, x, y, Clamp(58 + v), Clamp(120 + v + (int)wave), Clamp(214 + v));
            }
        return px;

        static int Clamp(int v) => Math.Clamp(v, 0, 255);
    }

    /// Three jagged blades of grass on a transparent background.
    static byte[] GrassTile(int size)
    {
        var px = new byte[size * size * 4]; // zero-initialized = fully transparent
        void Blade(int cx, int top, int g)
        {
            for (int y = top; y < size; y++)
            {
                int sway = (int)Math.Round(Math.Sin((y - top) * 0.9) * 1.2);
                int x = Math.Clamp(cx + sway, 0, size - 1);
                Put(px, size, x, y, 55, g, 38);
            }
        }
        Blade(size * 3 / 16, size * 4 / 16, 150);
        Blade(size * 8 / 16, size * 1 / 16, 172);
        Blade(size * 12 / 16, size * 6 / 16, 132);
        return px;
    }

    /// A short stem with a small five-petal flower head, transparent bg.
    static byte[] FlowerTile(int size, int r, int g, int b)
    {
        var px = new byte[size * size * 4];
        int cx = size / 2;
        for (int y = size * 9 / 16; y < size; y++) Put(px, size, cx, y, 60, 125, 45);
        int hy = size * 5 / 16;
        Put(px, size, cx - 1, hy, r, g, b);
        Put(px, size, cx + 1, hy, r, g, b);
        Put(px, size, cx, hy - 1, r, g, b);
        Put(px, size, cx, hy + 1, r, g, b);
        Put(px, size, cx, hy, 255, 232, 120);
        return px;
    }

    // ------------------------------------------------------------- new block tiles

    /// Vertical wooden boards with per-board shading, the base for doors.
    static void WoodBoards(byte[] px, int size, int boards, int rBase, int gBase, int bBase)
    {
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int board = x * boards / size;
                bool seam = x == board * size / boards;
                int v = (int)(Core.Noise.Hash2(x + board * 57, y + 913) * 20) - 10;
                int shade = seam ? -35 : (board % 2 == 0 ? 0 : -12);
                Put(px, size, x, y,
                    Math.Clamp(rBase + shade + v, 0, 255),
                    Math.Clamp(gBase + shade + v, 0, 255),
                    Math.Clamp(bBase + shade / 2 + v, 0, 255));
            }
    }

    /// Door texture: vertical boards with a dark frame; the top half carries
    /// a 2x2 window.
    static byte[] DoorTile(int size, bool top)
    {
        var px = new byte[size * size * 4];
        WoodBoards(px, size, 3, 132, 100, 58);
        int t = Math.Max(1, size / 16); // frame thickness
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                if (x < t || x >= size - t || (top && y < t) || (!top && y >= size - t))
                    Put(px, size, x, y, 92, 68, 38);
        if (top)
        {
            // window: 2x2 panes centered in the upper area
            int w0 = size * 4 / 16, w1 = size * 12 / 16;
            int h0 = size * 3 / 16, h1 = size * 9 / 16;
            for (int y = h0; y < h1; y++)
                for (int x = w0; x < w1; x++)
                {
                    bool bar = x == (w0 + w1) / 2 || x == (w0 + w1) / 2 - 1 || y == (h0 + h1) / 2 || y == (h0 + h1) / 2 - 1;
                    if (bar) Put(px, size, x, y, 82, 60, 34);
                    else Put(px, size, x, y, 165, 200, 216); // glassy pane
                }
        }
        else
        {
            // door handle hint on the bottom half's edge
            int hy = size * 2 / 16;
            for (int y = hy; y < hy + Math.Max(1, size / 8); y++)
                Put(px, size, size - 3 * t, y, 60, 60, 64);
        }
        return px;
    }

    /// Trapdoor: horizontal boards with a dark border and cross brace.
    static byte[] TrapdoorTileTex(int size)
    {
        var px = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int board = y * 4 / size;
                bool seam = y == board * size / 4;
                int v = (int)(Core.Noise.Hash2(x + 311, y + board * 71) * 18) - 9;
                int shade = seam ? -30 : (board % 2 == 0 ? 0 : -10);
                Put(px, size, x, y,
                    Math.Clamp(148 + shade + v, 0, 255),
                    Math.Clamp(112 + shade + v, 0, 255),
                    Math.Clamp(64 + shade / 2 + v, 0, 255));
            }
        int t = Math.Max(1, size / 16);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                if (x < t || x >= size - t || y < t || y >= size - t ||
                    Math.Abs(x - y) < t || Math.Abs(x - (size - 1 - y)) < t)
                    Put(px, size, x, y, 100, 74, 42);
        return px;
    }

    static byte[] ChestWood(int size, int rimShade)
    {
        var px = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int v = (int)(Core.Noise.Hash2(x + 577, y + 133) * 16) - 8;
                bool rim = x < size / 16 + 1 || x >= size - size / 16 - 1 || y < size / 16 + 1 || y >= size - size / 16 - 1;
                int s = rim ? rimShade : 0;
                Put(px, size, x, y,
                    Math.Clamp(158 + s + v, 0, 255),
                    Math.Clamp(106 + s + v, 0, 255),
                    Math.Clamp(52 + s / 2 + v, 0, 255));
            }
        return px;
    }

    static byte[] ChestTopTile(int size) => ChestWood(size, -45);

    /// Chest side; the front adds the lid seam latch.
    static byte[] ChestSideTile(int size, bool front)
    {
        var px = ChestWood(size, -45);
        int seamY = size * 6 / 16;
        for (int x = 0; x < size; x++) Put(px, size, x, seamY, 96, 62, 30);
        if (front)
        {
            int lx0 = size * 7 / 16, lx1 = size * 9 / 16;
            for (int y = seamY - size / 16 - 1; y <= seamY + size / 16 + 1; y++)
                for (int x = lx0; x < lx1; x++)
                    Put(px, size, x, Math.Clamp(y, 0, size - 1), 190, 190, 196); // iron latch
        }
        return px;
    }

    /// Soft white snow with faint blue-gray speckles.
    static byte[] SnowTileTex(int size)
    {
        var px = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int v = (int)(Core.Noise.Hash2(x + 1223, y + 881) * 14) - 7;
                Put(px, size, x, y,
                    Math.Clamp(238 + v, 0, 255),
                    Math.Clamp(242 + v, 0, 255),
                    Math.Clamp(247 + v / 2, 0, 255));
            }
        return px;
    }

    /// Savanna ground: sun-dried yellow grass; the side variant shows dirt
    /// with a dry-grass rim on top (like the grass block's side).
    static byte[] DryGrassTile(int size, bool top)
    {
        var px = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int v = (int)(Core.Noise.Hash2(x + 733, y + 577) * 26) - 13;
                bool grass = top || y < size * 3 / 16 + (int)(Core.Noise.Hash2(x + 91, 7) * (size / 8.0));
                if (grass)
                    Put(px, size, x, y,
                        Math.Clamp(178 + v, 0, 255),
                        Math.Clamp(160 + v, 0, 255),
                        Math.Clamp(74 + v / 2, 0, 255));
                else
                    Put(px, size, x, y,
                        Math.Clamp(121 + v, 0, 255),
                        Math.Clamp(85 + v, 0, 255),
                        Math.Clamp(58 + v / 2, 0, 255));
            }
        return px;
    }

    static void StonePixel(byte[] px, int size, int x, int y, int baseGray)
    {
        int v = (int)(Core.Noise.Hash2(x + 977, y + 389) * 26) - 13;
        int g = Math.Clamp(baseGray + v, 0, 255);
        Put(px, size, x, y, g, g, g);
    }

    static byte[] FurnaceTopTile(int size)
    {
        var px = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                StonePixel(px, size, x, y, 120);
        return px;
    }

    enum FurnaceFace { Side, Front, FrontLit }

    static byte[] FurnaceSideTile(int size, FurnaceFace face)
    {
        var px = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                StonePixel(px, size, x, y, y < size / 8 || y >= size - size / 8 ? 96 : 132);
        if (face != FurnaceFace.Side)
        {
            // mouth opening in the lower half
            int x0 = size * 4 / 16, x1 = size * 12 / 16;
            int y0 = size * 8 / 16, y1 = size * 14 / 16;
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    if (face == FurnaceFace.FrontLit)
                    {
                        double n = Core.Noise.Hash2(x * 7 + 15, y * 5 + 91);
                        bool ember = y > y0 + (y1 - y0) / 3 || n > 0.6;
                        if (ember) Put(px, size, x, y, 255, (int)(140 + n * 90), 40);
                        else Put(px, size, x, y, 40, 22, 16);
                    }
                    else
                    {
                        Put(px, size, x, y, 28, 28, 30);
                    }
                }
        }
        return px;
    }

    /// 16x16 inventory icon for a flower, matching FlowerTile's shape.
    public static byte[] FlowerIcon(int r, int g, int b, int size = 16) => FlowerTile(size, r, g, b);

    static readonly string[] HeartShape =
    {
        ".##.##.",
        "#######",
        "#######",
        ".#####.",
        "..###..",
        "...#...",
    };

    /// 16x16 heart for the health bar. fill: 2 = full, 1 = left half, 0 = empty.
    public static byte[] HeartIcon(int fill, int size = 16)
    {
        var px = new byte[size * size * 4];
        for (int r = 0; r < HeartShape.Length; r++)
            for (int c = 0; c < 7; c++)
            {
                if (HeartShape[r][c] != '#') continue;
                bool red = fill == 2 || (fill == 1 && c < 4);
                var (cr, cg, cb) = red ? (211, 45, 45) : (58, 58, 62);
                // highlight pixel top-left, like MC hearts
                if (red && r == 1 && c == 1) (cr, cg, cb) = (255, 150, 150);
                for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                        Put(px, size, 1 + c * 2 + dx, 2 + r * 2 + dy, cr, cg, cb);
            }
        return px;
    }
    static readonly string[] DrumstickShape =
    {
        "...####.",
        "..#MMMM#",
        ".#MLLMM#",
        ".#MLMMM#",
        ".#MMMM#.",
        ".B#MM#..",
        "BWWB#...",
        ".BB.....",
    };

    /// 16x16 drumstick for the hunger bar. fill: 2 = full, 1 = right half
    /// (the hunger bar mirrors the health bar, like Minecraft), 0 = empty.
    public static byte[] HungerIcon(int fill, int size = 16)
    {
        var px = new byte[size * size * 4];
        for (int r = 0; r < DrumstickShape.Length; r++)
            for (int c = 0; c < 8; c++)
            {
                char ch = DrumstickShape[r][c];
                if (ch == '.') continue;
                bool filled = fill == 2 || (fill == 1 && c >= 4);
                var (cr, cg, cb) = ch switch
                {
                    '#' => filled ? (72, 42, 22) : (45, 45, 48),      // outline
                    'M' => filled ? (186, 112, 62) : (78, 78, 82),    // meat
                    'L' => filled ? (224, 158, 98) : (95, 95, 100),   // highlight
                    'W' => filled ? (238, 234, 224) : (105, 105, 110),// bone
                    _   => filled ? (168, 158, 144) : (66, 66, 70),   // bone edge
                };
                for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                        Put(px, size, c * 2 + dx, r * 2 + dy, cr, cg, cb);
            }
        return px;
    }

    /// Renders a 16x16 string pixel-map with a palette; '.' (or any char
    /// missing from the palette) stays transparent.
    static byte[] IconFromMap(string[] map, Dictionary<char, (int R, int G, int B)> pal, int size = 16)
    {
        var px = new byte[size * size * 4];
        for (int y = 0; y < map.Length; y++)
            for (int x = 0; x < map[y].Length; x++)
                if (pal.TryGetValue(map[y][x], out var c))
                    Put(px, size, x, y, c.R, c.G, c.B);
        return px;
    }

    static readonly string[] PorkMap =
    {
        "................",
        ".....######.....",
        "....#PPPPPP#....",
        "...#PLLPPPPP#...",
        "..#PLLPPPPPPP#..",
        "..#PLPPPPPPPP#..",
        "..#PPPPPPPPDD#..",
        "..#PPPPPPPPDD#..",
        "...#PPPPPPDD#...",
        "....#PPPPPD#....",
        ".....#PPPP#.....",
        "......BWWB......",
        ".....BWWWB......",
        ".....BWWB.......",
        "......BB........",
        "................",
    };

    /// 16x16 icon: pink porkchop with a bone stub at the bottom.
    public static byte[] RawPorkIcon() => IconFromMap(PorkMap, new()
    {
        ['#'] = (150, 74, 82),
        ['P'] = (238, 146, 152),
        ['L'] = (250, 200, 198),
        ['D'] = (205, 106, 118),
        ['W'] = (240, 236, 228),
        ['B'] = (160, 150, 140),
    });

    /// 16x16 icon: browned porkchop (furnace-cooked).
    public static byte[] CookedPorkIcon() => IconFromMap(PorkMap, new()
    {
        ['#'] = (104, 62, 32),
        ['P'] = (196, 132, 78),
        ['L'] = (226, 176, 112),
        ['D'] = (160, 98, 52),
        ['W'] = (240, 236, 228),
        ['B'] = (160, 150, 140),
    });

    static readonly string[] MuttonMap =
    {
        "................",
        "............##..",
        "...........#WW#.",
        "..........#WW#..",
        ".....####.#W#...",
        "....#MMLL#W#....",
        "...#MMMLL##.....",
        "..#MMMMMM#......",
        "..#MMMMMM#......",
        "..#DMMMM#.......",
        ".#W#DMM#........",
        "#WW##D#.........",
        "#WW#.#..........",
        ".##.............",
        "................",
        "................",
    };

    /// 16x16 icon: red mutton chunk on a diagonal bone.
    public static byte[] RawMuttonIcon() => IconFromMap(MuttonMap, new()
    {
        ['#'] = (120, 34, 38),
        ['M'] = (198, 60, 62),
        ['L'] = (232, 116, 106),
        ['D'] = (158, 40, 46),
        ['W'] = (240, 236, 228),
        ['B'] = (160, 150, 140),
    });

    /// 16x16 icon: browned mutton (furnace-cooked).
    public static byte[] CookedMuttonIcon() => IconFromMap(MuttonMap, new()
    {
        ['#'] = (96, 52, 26),
        ['M'] = (172, 108, 58),
        ['L'] = (210, 152, 92),
        ['D'] = (138, 82, 40),
        ['W'] = (240, 236, 228),
        ['B'] = (160, 150, 140),
    });

    static readonly string[] ChickenMap =
    {
        "................",
        "......####......",
        ".....#CCLL#.....",
        "....#CCCLLC#....",
        "...#CCCCCCCC#...",
        "..#CCCCCCCCCC#..",
        "..#CCCCCCCCCC#..",
        "..#DCCCCCCCCD#..",
        "..#DDCCCCCCDD#..",
        "...#DDCCCCDD#...",
        "....##DDDD##....",
        "...#W##DD#W#....",
        "...#WW####WW#...",
        "....##....##....",
        "................",
        "................",
    };

    /// 16x16 icon: whole raw chicken, pale skin with two leg bones.
    public static byte[] RawChickenIcon() => IconFromMap(ChickenMap, new()
    {
        ['#'] = (168, 110, 92),
        ['C'] = (240, 202, 172),
        ['L'] = (250, 230, 205),
        ['D'] = (216, 164, 140),
        ['W'] = (240, 236, 228),
    });

    /// 16x16 icon: golden roast chicken (furnace-cooked).
    public static byte[] CookedChickenIcon() => IconFromMap(ChickenMap, new()
    {
        ['#'] = (128, 74, 34),
        ['C'] = (214, 150, 74),
        ['L'] = (236, 188, 110),
        ['D'] = (180, 118, 52),
        ['W'] = (240, 236, 228),
    });

    /// 16x16 icon for the tall grass tuft (same art as its world tile).
    public static byte[] TallGrassIcon(int size = 16) => GrassTile(size);

    /// 16x16 inventory icon for the boat: a simple canoe profile.
    public static byte[] BoatIcon(int size = 16)
    {
        var px = new byte[size * size * 4];
        void Wood(int x, int y) => Put(px, size, x, y, 140, 97, 51);
        void Dark(int x, int y) => Put(px, size, x, y, 107, 71, 36);
        for (int x = 2; x <= 13; x++) Dark(x, 10);                  // hull bottom
        for (int x = 4; x <= 11; x++) Wood(x, 9);                   // inner floor
        foreach (int x in new[] { 2, 3, 12, 13 }) Dark(x, 8);       // rising ends
        foreach (int x in new[] { 2, 13 }) Dark(x, 7);
        for (int x = 5; x <= 10; x++) Dark(x, 11);                  // keel
        return px;
    }

    /// 16x16 inventory icon for the torch.
    public static byte[] TorchIcon(int size = 16)
    {
        var px = new byte[size * size * 4];
        for (int y = 6; y <= 15; y++)
            for (int x = 7; x <= 8; x++)
                Put(px, size, x, y, x == 7 ? 118 : 146, x == 7 ? 78 : 98, 42);
        for (int y = 4; y <= 5; y++)
            for (int x = 7; x <= 8; x++)
                Put(px, size, x, y, 255, 214, 80);
        Put(px, size, 6, 4, 255, 150, 40); Put(px, size, 9, 4, 255, 150, 40);
        Put(px, size, 7, 3, 255, 150, 40); Put(px, size, 8, 3, 255, 150, 40);
        Put(px, size, 7, 2, 255, 200, 90); Put(px, size, 8, 2, 255, 200, 90);
        return px;
    }

    // ------------------------------------------------------------- new block icons

    static void ShadedRect(byte[] px, int size, int x0, int y0, int x1, int y1, int r, int g, int b)
    {
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int v = (int)(Core.Noise.Hash2(x + 41, y + 793) * 16) - 8;
                bool edge = x == x0 || x == x1 - 1 || y == y0 || y == y1 - 1;
                int s = edge ? -40 : 0;
                Put(px, size, x, y, Math.Clamp(r + s + v, 0, 255), Math.Clamp(g + s + v, 0, 255), Math.Clamp(b + s + v, 0, 255));
            }
    }

    static (int R, int G, int B) MaterialColor(bool stone) => stone ? (138, 138, 138) : (160, 125, 75);

    /// 16x16 icon: two-step stair profile.
    public static byte[] StairsIcon(bool stone, int size = 16)
    {
        var px = new byte[size * size * 4];
        var (r, g, b) = MaterialColor(stone);
        ShadedRect(px, size, 1, 8, 15, 15, r, g, b);   // bottom step, full width
        ShadedRect(px, size, 8, 1, 15, 8, r, g, b);    // top step, right half
        return px;
    }

    /// 16x16 icon: half-block slab (horizontal or vertical).
    public static byte[] SlabIcon(bool stone, bool vertical, int size = 16)
    {
        var px = new byte[size * size * 4];
        var (r, g, b) = MaterialColor(stone);
        if (vertical) ShadedRect(px, size, 1, 1, 8, 15, r, g, b);
        else ShadedRect(px, size, 1, 8, 15, 15, r, g, b);
        return px;
    }

    /// 16x16 icon: full door seen from the front (window on top).
    public static byte[] DoorIcon(int size = 16)
    {
        var px = new byte[size * size * 4];
        ShadedRect(px, size, 4, 0, 12, 16, 132, 100, 58);
        for (int y = 2; y < 6; y++)
            for (int x = 6; x < 10; x++)
                Put(px, size, x, y, 165, 200, 216);         // window
        Put(px, size, 10, 8, 60, 60, 64);                    // handle
        Put(px, size, 10, 9, 60, 60, 64);
        return px;
    }

    public static byte[] TrapdoorIcon(int size = 16) => TrapdoorTileTex(size);
    public static byte[] ChestIcon(int size = 16) => ChestSideTile(size, front: true);
    public static byte[] FurnaceIcon(int size = 16) => FurnaceSideTile(size, FurnaceFace.Front);
    public static byte[] SnowIcon(int size = 16) => SnowTileTex(size);
    public static byte[] DryGrassIcon(int size = 16) => DryGrassTile(size, top: false);
}

/// All inventory icon PNGs composited into one texture, with per-item UV rects.
/// Also carries a few non-item HUD icons (hearts) under pseudo ids.
public sealed class IconAtlas
{
    public const int HeartFull = 9001;
    public const int HeartHalf = 9002;
    public const int HeartEmpty = 9003;

    public const int HungerFull = 9004;
    public const int HungerHalf = 9005;
    public const int HungerEmpty = 9006;

    public GpuTexture Texture;
    public TextureHandle Handle;
    readonly Dictionary<int, (float U0, float V0, float U1, float V1)> _uvs = new();

    static readonly (int Id, string File)[] Files =
    {
        (Core.BlockId.Grass, "grass"), (Core.BlockId.Dirt, "dirt"), (Core.BlockId.Stone, "stone"),
        (Core.BlockId.Sand, "sand"), (Core.BlockId.Wood, "wood"), (Core.BlockId.Leaves, "leaves"),
        (Core.BlockId.Coal, "coal_ore"), (Core.BlockId.Iron, "iron_ore"), (Core.BlockId.Gold, "gold_ore"),
        (Core.BlockId.Diamond, "diamond_ore"), (Core.BlockId.Bedrock, "bedrock"), (Core.BlockId.Planks, "planks"),
        (Core.ItemId.Stick, "stick"), (Core.ItemId.WoodPick, "wooden_pickaxe"), (Core.ItemId.StonePick, "stone_pickaxe"),
        (Core.ItemId.IronPick, "iron_pickaxe"), (Core.ItemId.DiamondPick, "diamond_pickaxe"),
        (Core.ItemId.Axe, "axe"), (Core.ItemId.Shovel, "shovel"),
    };

    public IconAtlas(Renderer renderer, string iconDir)
    {
        const int cell = 16, cols = 9;
        int cells = Files.Length + 29; // generated: torch, flowers, boat, tall grass, hearts, hunger, meats, new blocks
        int rows = (cells + cols - 1) / cols;
        int atlasW = cols * cell, atlasH = rows * cell;
        var pixels = new byte[atlasW * atlasH * 4];

        void Blit(int i, int id, byte[] rgba, int w, int h)
        {
            int cx = (i % cols) * cell, cy = (i / cols) * cell;
            for (int y = 0; y < Math.Min(h, cell); y++)
                for (int x = 0; x < Math.Min(w, cell); x++)
                {
                    int src = (y * w + x) * 4;
                    int dst = ((cy + y) * atlasW + cx + x) * 4;
                    for (int c = 0; c < 4; c++) pixels[dst + c] = rgba[src + c];
                }
            _uvs[id] = ((float)cx / atlasW, (float)cy / atlasH,
                        (float)(cx + cell) / atlasW, (float)(cy + cell) / atlasH);
        }

        for (int i = 0; i < Files.Length; i++)
        {
            var (rgba, w, h) = PngLoader.Load(Path.Combine(iconDir, Files[i].File + ".png"));
            Blit(i, Files[i].Id, rgba, w, h);
        }
        Blit(Files.Length, Core.BlockId.Torch, TextureGen.TorchIcon(), 16, 16);
        Blit(Files.Length + 1, Core.BlockId.FlowerYellow, TextureGen.FlowerIcon(235, 205, 60), 16, 16);
        Blit(Files.Length + 2, Core.BlockId.FlowerRed, TextureGen.FlowerIcon(205, 60, 60), 16, 16);
        Blit(Files.Length + 3, Core.ItemId.Boat, TextureGen.BoatIcon(), 16, 16);
        Blit(Files.Length + 4, Core.BlockId.TallGrass, TextureGen.TallGrassIcon(), 16, 16);

        Blit(Files.Length + 5, HeartFull, TextureGen.HeartIcon(2), 16, 16);
        Blit(Files.Length + 6, HeartHalf, TextureGen.HeartIcon(1), 16, 16);
        Blit(Files.Length + 7, HeartEmpty, TextureGen.HeartIcon(0), 16, 16);

        Blit(Files.Length + 8, HungerFull, TextureGen.HungerIcon(2), 16, 16);
        Blit(Files.Length + 9, HungerHalf, TextureGen.HungerIcon(1), 16, 16);
        Blit(Files.Length + 10, HungerEmpty, TextureGen.HungerIcon(0), 16, 16);

        Blit(Files.Length + 11, Core.ItemId.RawPigMeat, TextureGen.RawPorkIcon(), 16, 16);
        Blit(Files.Length + 12, Core.ItemId.RawSheepMeat, TextureGen.RawMuttonIcon(), 16, 16);
        Blit(Files.Length + 13, Core.ItemId.RawChickenMeat, TextureGen.RawChickenIcon(), 16, 16);

        Blit(Files.Length + 14, Core.ItemId.CookedPigMeat, TextureGen.CookedPorkIcon(), 16, 16);
        Blit(Files.Length + 15, Core.ItemId.CookedSheepMeat, TextureGen.CookedMuttonIcon(), 16, 16);
        Blit(Files.Length + 16, Core.ItemId.CookedChickenMeat, TextureGen.CookedChickenIcon(), 16, 16);

        Blit(Files.Length + 17, Core.BlockId.PlankStairs, TextureGen.StairsIcon(stone: false), 16, 16);
        Blit(Files.Length + 18, Core.BlockId.StoneStairs, TextureGen.StairsIcon(stone: true), 16, 16);
        Blit(Files.Length + 19, Core.BlockId.PlankSlab, TextureGen.SlabIcon(stone: false, vertical: false), 16, 16);
        Blit(Files.Length + 20, Core.BlockId.StoneSlab, TextureGen.SlabIcon(stone: true, vertical: false), 16, 16);
        Blit(Files.Length + 21, Core.BlockId.PlankSlabVert, TextureGen.SlabIcon(stone: false, vertical: true), 16, 16);
        Blit(Files.Length + 22, Core.BlockId.StoneSlabVert, TextureGen.SlabIcon(stone: true, vertical: true), 16, 16);
        Blit(Files.Length + 23, Core.BlockId.Door, TextureGen.DoorIcon(), 16, 16);
        Blit(Files.Length + 24, Core.BlockId.Trapdoor, TextureGen.TrapdoorIcon(), 16, 16);
        Blit(Files.Length + 25, Core.BlockId.Chest, TextureGen.ChestIcon(), 16, 16);
        Blit(Files.Length + 26, Core.BlockId.Furnace, TextureGen.FurnaceIcon(), 16, 16);
        Blit(Files.Length + 27, Core.BlockId.Snow, TextureGen.SnowIcon(), 16, 16);
        Blit(Files.Length + 28, Core.BlockId.DryGrass, TextureGen.DryGrassIcon(), 16, 16);

        Texture = new GpuTexture(renderer.Ctx, pixels, atlasW, atlasH);
        Handle = renderer.RegisterTexture(Texture);
    }

    public void Draw(HudBatcher batch, int itemId, float x, float y, float size, float alpha = 1f)
    {
        if (!_uvs.TryGetValue(itemId, out var uv)) return;
        batch.Quad(Handle, x, y, size, size, uv.U0, uv.V0, uv.U1, uv.V1, new Vector4(1, 1, 1, alpha));
    }
}

/// ASCII 32..126 rasterized once with System.Drawing into a grid atlas.
public sealed class FontAtlas
{
    public GpuTexture Texture;
    public TextureHandle Handle;
    public readonly int CellW, CellH;
    const int Cols = 16;
    const int FirstChar = 32, LastChar = 126;
    readonly int _atlasW, _atlasH;

    public FontAtlas(Renderer renderer, float fontSize = 14f)
    {
        using var font = new Font("Consolas", fontSize, GraphicsUnit.Pixel);
        CellW = (int)Math.Ceiling(fontSize * 0.62f);
        CellH = (int)Math.Ceiling(fontSize * 1.35f);
        int rows = (LastChar - FirstChar + Cols) / Cols;
        _atlasW = Cols * CellW;
        _atlasH = rows * CellH;

        using var bmp = new Bitmap(_atlasW, _atlasH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var brush = new SolidBrush(Color.White);
            var format = StringFormat.GenericTypographic;
            for (int c = FirstChar; c <= LastChar; c++)
            {
                int i = c - FirstChar;
                g.DrawString(((char)c).ToString(), font, brush, (i % Cols) * CellW, (i / Cols) * CellH, format);
            }
        }
        var (rgba, w, h) = PngLoader.ToRgba(bmp);
        Texture = new GpuTexture(renderer.Ctx, rgba, w, h, srgb: false);
        Handle = renderer.RegisterTexture(Texture);
    }

    public float Measure(string text, float scale = 1f) => text.Length * CellW * scale;

    public void Draw(HudBatcher batch, string text, float x, float y, Vector4 color, float scale = 1f)
    {
        float cx = x;
        foreach (char ch in text)
        {
            int c = ch is >= (char)FirstChar and <= (char)LastChar ? ch : '?';
            int i = c - FirstChar;
            float u0 = (i % Cols) * CellW / (float)_atlasW;
            float v0 = (i / Cols) * CellH / (float)_atlasH;
            float u1 = u0 + CellW / (float)_atlasW;
            float v1 = v0 + CellH / (float)_atlasH;
            batch.Quad(Handle, cx, y, CellW * scale, CellH * scale, u0, v0, u1, v1, color);
            cx += CellW * scale;
        }
    }
}
