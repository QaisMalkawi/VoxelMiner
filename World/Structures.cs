using System.Text;

namespace VoxelMiner.World;

using VoxelMiner.Core;

/// A prefab loaded from a text file: a stack of ASCII layer maps plus
/// placement rules. -1 in Blocks means "keep whatever terrain is there".
public sealed class Structure
{
    public string Name;
    public int SizeX, SizeY, SizeZ;
    public int[] Blocks;              // x + z*SizeX + y*SizeX*SizeZ
    public int Cell;                  // placement grid spacing; 0 = manual only (village parts)
    public double Chance = 0.3;       // per-cell spawn probability
    public HashSet<Biome> Biomes = new();
    public int YBase;                 // layer 0 sits at ground + 1 + YBase
    public int MaxSlope = 3;          // max height difference across the footprint
    public int Salt;                  // deterministic per-name hash for placement noise

    public int At(int x, int y, int z) => Blocks[x + z * SizeX + y * SizeX * SizeZ];

    Structure[] _rotations;

    /// This structure turned clockwise (seen from above) by quarter turns.
    /// Facing-aware blocks (doors, chests, stairs...) rotate with it. Cached —
    /// rotation is deterministic and structures are immutable after load.
    public Structure Rotated(int quarters)
    {
        quarters &= 3;
        if (quarters == 0) return this;
        _rotations ??= new Structure[4];
        return _rotations[quarters] ??= BuildRotation(quarters);
    }

    Structure BuildRotation(int q)
    {
        bool swap = (q & 1) == 1;
        var r = new Structure
        {
            Name = Name, Salt = Salt, Cell = Cell, Chance = Chance, Biomes = Biomes,
            YBase = YBase, MaxSlope = MaxSlope, SizeY = SizeY,
            SizeX = swap ? SizeZ : SizeX,
            SizeZ = swap ? SizeX : SizeZ,
        };
        r.Blocks = new int[r.SizeX * r.SizeY * r.SizeZ];
        for (int y = 0; y < SizeY; y++)
            for (int z = 0; z < SizeZ; z++)
                for (int x = 0; x < SizeX; x++)
                {
                    var (nx, nz) = q switch
                    {
                        1 => (SizeZ - 1 - z, x),
                        2 => (SizeX - 1 - x, SizeZ - 1 - z),
                        _ => (z, SizeX - 1 - x),
                    };
                    r.Blocks[nx + nz * r.SizeX + y * r.SizeX * r.SizeZ] =
                        BlockRegistry.RotateBlock(At(x, y, z), q);
                }
        return r;
    }
}

/// Loads every structure from assets\structures\*.txt once (thread-safe —
/// chunk generation runs on background tasks). Missing default files are
/// written out first, so the folder always contains editable templates:
/// change a file (or add a new .txt) and the next launch uses it.
///
/// File format:
///   # comment
///   cell: 220           placement grid in blocks (0 = only placed by code, e.g. village parts)
///   chance: 0.25        probability that a grid cell spawns one
///   biomes: plains forest
///   ybase: -1           layer 0 sits at ground+1+ybase (-1 = sunk into the surface)
///   maxslope: 3         max terrain height difference across the footprint
///   palette:
///   P = planks          single char = block token; tokens with facing: door_s,
///   . = air             chest_n, furnace_e, plankstairs_w, trapdoor_s...
///   _ = keep            (leave terrain untouched)
///   layers:
///   PPPPP               rows are Z (top row = north), chars are X (left = west)
///   P...P
///   ---                 separates Y layers, bottom-up
///   ...
public static class StructureRegistry
{
    static readonly Lazy<Dictionary<string, Structure>> _all = new(LoadAll, isThreadSafe: true);

    public static IReadOnlyDictionary<string, Structure> All => _all.Value;

    public static Structure Get(string name) => All.TryGetValue(name, out var s) ? s : null;

    public static string Dir => Path.Combine(AppContext.BaseDirectory, "assets", "structures");

    static Dictionary<string, Structure> LoadAll()
    {
        var dict = new Dictionary<string, Structure>(StringComparer.OrdinalIgnoreCase);
        try
        {
            Directory.CreateDirectory(Dir);
            foreach (var (name, content) in DefaultFiles)
            {
                string path = Path.Combine(Dir, name + ".txt");
                if (!File.Exists(path)) File.WriteAllText(path, content);
            }
            foreach (var file in Directory.GetFiles(Dir, "*.txt"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                try
                {
                    dict[name] = Parse(name, File.ReadAllLines(file));
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Bad structure file '{file}': {e.Message}");
                }
            }
            Console.WriteLine($"Structures loaded: {string.Join(", ", dict.Keys)}");
        }
        catch (Exception e)
        {
            Console.WriteLine($"Structure loading failed: {e.Message}");
        }
        return dict;
    }

    // ------------------------------------------------------------- parsing

    static Structure Parse(string name, string[] lines)
    {
        var s = new Structure { Name = name, Salt = StableHash(name) };
        var palette = new Dictionary<char, int> { [' '] = -1 }; // padding = keep
        var layers = new List<List<string>>();
        var mode = "header";
        List<string> current = null;

        foreach (var raw in lines)
        {
            string line = raw.TrimEnd();
            if (line.TrimStart().StartsWith('#')) continue;

            if (mode != "layers" && line.Length == 0) continue;
            string lower = line.Trim().ToLowerInvariant();
            if (lower == "palette:") { mode = "palette"; continue; }
            if (lower == "layers:") { mode = "layers"; current = null; continue; }

            switch (mode)
            {
                case "header":
                {
                    int colon = line.IndexOf(':');
                    if (colon < 0) throw new FormatException($"expected 'key: value', got '{line}'");
                    string key = line[..colon].Trim().ToLowerInvariant();
                    string val = line[(colon + 1)..].Trim();
                    switch (key)
                    {
                        case "cell": s.Cell = int.Parse(val); break;
                        case "chance": s.Chance = double.Parse(val, System.Globalization.CultureInfo.InvariantCulture); break;
                        case "ybase": s.YBase = int.Parse(val); break;
                        case "maxslope": s.MaxSlope = int.Parse(val); break;
                        case "biomes":
                            foreach (var b in val.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
                                s.Biomes.Add(Enum.Parse<Biome>(b, ignoreCase: true));
                            break;
                        default: throw new FormatException($"unknown key '{key}'");
                    }
                    break;
                }
                case "palette":
                {
                    // "P = planks" (or "P planks")
                    var parts = line.Trim().Split(new[] { '=', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 2 || parts[0].Length != 1)
                        throw new FormatException($"bad palette line '{line}'");
                    palette[parts[0][0]] = ResolveToken(parts[1]);
                    break;
                }
                case "layers":
                {
                    if (line.Trim() == "---") { current = null; continue; }
                    if (line.Length == 0) continue;
                    if (current == null) { current = new List<string>(); layers.Add(current); }
                    current.Add(line);
                    break;
                }
            }
        }

        if (layers.Count == 0) throw new FormatException("no layers");
        s.SizeY = layers.Count;
        s.SizeZ = layers.Max(l => l.Count);
        s.SizeX = layers.Max(l => l.Max(row => row.Length));
        s.Blocks = new int[s.SizeX * s.SizeY * s.SizeZ];
        Array.Fill(s.Blocks, -1);

        for (int y = 0; y < s.SizeY; y++)
            for (int z = 0; z < layers[y].Count; z++)
                for (int x = 0; x < layers[y][z].Length; x++)
                {
                    char c = layers[y][z][x];
                    if (!palette.TryGetValue(c, out int id))
                        throw new FormatException($"char '{c}' not in palette");
                    s.Blocks[x + z * s.SizeX + y * s.SizeX * s.SizeZ] = id;
                }
        return s;
    }

    /// Block token to id: plain names, or family_facing (door_s, chest_n,
    /// furnace_e, plankstairs_w...). "keep" (-1) leaves terrain untouched.
    static int ResolveToken(string token)
    {
        token = token.ToLowerInvariant();
        switch (token)
        {
            case "keep": return -1;
            case "air": return BlockId.Air;
            case "grass": return BlockId.Grass;
            case "dirt": return BlockId.Dirt;
            case "stone": return BlockId.Stone;
            case "sand": return BlockId.Sand;
            case "wood": return BlockId.Wood;
            case "planks": return BlockId.Planks;
            case "leaves": return BlockId.Leaves;
            case "torch": return BlockId.Torch;
            case "water": return BlockId.Water;
            case "bedrock": return BlockId.Bedrock;
            case "snow": return BlockId.Snow;
            case "drygrass": return BlockId.DryGrass;
            case "coal": return BlockId.Coal;
            case "iron": return BlockId.Iron;
            case "gold": return BlockId.Gold;
            case "diamond": return BlockId.Diamond;
            case "tallgrass": return BlockId.TallGrass;
            case "floweryellow": return BlockId.FlowerYellow;
            case "flowerred": return BlockId.FlowerRed;
            case "plankslab": return BlockId.PlankSlab;
            case "plankslabtop": return BlockId.PlankSlabTop;
            case "stoneslab": return BlockId.StoneSlab;
            case "stoneslabtop": return BlockId.StoneSlabTop;
        }

        int sep = token.LastIndexOf('_');
        if (sep > 0)
        {
            string family = token[..sep];
            int facing = token[(sep + 1)..] switch
            {
                "n" => 0, "e" => 1, "s" => 2, "w" => 3,
                _ => throw new FormatException($"bad facing in '{token}'"),
            };
            return family switch
            {
                "door" => BlockRegistry.DoorVariant(facing, open: false, upper: false), // upper half is auto-placed
                "trapdoor" => BlockRegistry.TrapdoorVariant(facing, open: false),
                "chest" => BlockId.Chest + facing,
                "furnace" => BlockRegistry.FurnaceVariant(facing, lit: false),
                "plankstairs" => BlockId.PlankStairs + facing,
                "stonestairs" => BlockId.StoneStairs + facing,
                "plankslabv" => BlockId.PlankSlabVert + facing,
                "stoneslabv" => BlockId.StoneSlabVert + facing,
                _ => throw new FormatException($"unknown block '{token}'"),
            };
        }
        throw new FormatException($"unknown block '{token}'");
    }

    /// string.GetHashCode is randomized per process; placement noise needs a
    /// hash that is identical across runs.
    static int StableHash(string s)
    {
        int h = 17;
        foreach (char c in s) h = h * 31 + c;
        return h & 0x7fffffff;
    }

    // ------------------------------------------------------------- defaults

    static readonly (string Name, string Content)[] DefaultFiles =
    {
        ("house", """
            # Village house: plank walls with wood corners, a real door, a chest
            # and a furnace inside, under a pitched stair roof. Placed by
            # village generation (cell: 0); rotated to face the well.
            cell: 0
            ybase: -1
            palette:
            W = wood
            P = planks
            T = torch
            C = chest_s
            F = furnace_s
            D = door_s
            > = plankstairs_e
            < = plankstairs_w
            s = plankslab
            . = air
            _ = keep
            layers:
            PPPPP
            PPPPP
            PPPPP
            PPPPP
            PPPPP
            PPPPP
            ---
            WPPPW
            PC.FP
            P...P
            P...P
            PT..P
            WPDPW
            ---
            WPPPW
            P...P
            P...P
            P...P
            P...P
            WP_PW
            ---
            WPPPW
            P...P
            P...P
            P...P
            P...P
            WPPPW
            ---
            >PPP<
            >PPP<
            >PPP<
            >PPP<
            >PPP<
            >PPP<
            ---
            _>P<_
            _>P<_
            _>P<_
            _>P<_
            _>P<_
            _>P<_
            ---
            __s__
            __s__
            __s__
            __s__
            __s__
            __s__
            """),

        ("well", """
            # Village well: stone basin with a wooden canopy. Placed by village
            # generation (cell: 0).
            cell: 0
            ybase: -1
            palette:
            S = stone
            W = wood
            s = plankslab
            ~ = water
            . = air
            _ = keep
            layers:
            SSS
            S~S
            SSS
            ---
            SSS
            S.S
            SSS
            ---
            W_W
            ___
            W_W
            ---
            W_W
            ___
            W_W
            ---
            W_W
            ___
            W_W
            ---
            sss
            sss
            sss
            """),

        ("ruined_tower", """
            # Crumbling stone watchtower with a chest and a torch inside.
            cell: 220
            chance: 0.22
            biomes: plains forest savanna mountains
            ybase: -1
            maxslope: 3
            palette:
            S = stone
            s = stoneslab
            C = chest_s
            T = torch
            . = air
            _ = keep
            layers:
            SSSSS
            SSSSS
            SSSSS
            SSSSS
            SSSSS
            ---
            SSSSS
            S.C.S
            S...S
            S.T.S
            SS.SS
            ---
            SSSSS
            S...S
            S...S
            S...S
            SS.SS
            ---
            SSSSS
            S...S
            S...S
            S...S
            SSSSS
            ---
            S_s_S
            _...s
            s..._
            _...S
            S_s__
            ---
            s___S
            _____
            _____
            _____
            __s__
            """),

        ("desert_pyramid", """
            # Stepped sand pyramid hiding a small chamber with a chest.
            cell: 260
            chance: 0.3
            biomes: desert
            ybase: -1
            maxslope: 4
            palette:
            A = sand
            C = chest_s
            T = torch
            . = air
            _ = keep
            layers:
            AAAAAAAAA
            AAAAAAAAA
            AAAAAAAAA
            AAAAAAAAA
            AAAAAAAAA
            AAAAAAAAA
            AAAAAAAAA
            AAAAAAAAA
            AAAAAAAAA
            ---
            AAAAAAAAA
            A.......A
            A..T....A
            A.......A
            A...C...A
            A.......A
            A.......A
            A.......A
            AAAA.AAAA
            ---
            AAAAAAAAA
            A.......A
            A.......A
            A.......A
            A.......A
            A.......A
            A.......A
            A.......A
            AAAA.AAAA
            ---
            AAAAAAAAA
            AAAAAAAAA
            AAAAAAAAA
            AAAAAAAAA
            AAAAAAAAA
            AAAAAAAAA
            AAAAAAAAA
            AAAAAAAAA
            AAAAAAAAA
            ---
            _________
            _AAAAAAA_
            _AAAAAAA_
            _AAAAAAA_
            _AAAAAAA_
            _AAAAAAA_
            _AAAAAAA_
            _AAAAAAA_
            _________
            ---
            _________
            _________
            __AAAAA__
            __AAAAA__
            __AAAAA__
            __AAAAA__
            __AAAAA__
            _________
            _________
            ---
            _________
            _________
            _________
            ___AAA___
            ___AAA___
            ___AAA___
            _________
            _________
            _________
            ---
            _________
            _________
            _________
            _________
            ____A____
            _________
            _________
            _________
            _________
            """),

        ("swamp_hut", """
            # Raised swamp hut: corner posts, a floor one block up, a stair
            # step to the door, and a trapdoor hatch in the floor.
            cell: 200
            chance: 0.3
            biomes: swamp
            ybase: -1
            maxslope: 3
            palette:
            W = wood
            P = planks
            T = torch
            D = door_s
            ^ = plankstairs_n
            H = trapdoor_n
            . = air
            _ = keep
            layers:
            W___W
            _____
            _____
            _____
            W___W
            _____
            ---
            PPPPP
            PPPPP
            PPHPP
            PPPPP
            PPPPP
            __^__
            ---
            PPPPP
            PT..P
            P...P
            P...P
            PPDPP
            _____
            ---
            PPPPP
            P...P
            P...P
            P...P
            PP_PP
            _____
            ---
            WWWWW
            WWWWW
            WWWWW
            WWWWW
            WWWWW
            _____
            """),

        ("campsite", """
            # Tiny campsite: log and slab-bench seats around a fire.
            cell: 140
            chance: 0.18
            biomes: plains savanna forest
            ybase: 0
            maxslope: 2
            palette:
            W = wood
            s = plankslab
            T = torch
            _ = keep
            layers:
            _____
            _W_W_
            _sTs_
            _W_W_
            _____
            """),
    };
}
