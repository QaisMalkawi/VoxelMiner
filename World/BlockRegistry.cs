using System.Numerics;

namespace VoxelMiner.World;

public enum ToolClass { None, Pick, Shovel, Axe }

/// Geometry/behaviour class of a block. Everything except Cube is meshed
/// from sub-boxes and uses per-box collision instead of the full cell.
public enum BlockShape
{
    Cube, Stairs, SlabBottom, SlabTop, SlabVert,
    DoorLower, DoorUpper, Trapdoor, Chest, Furnace, Fence,
}

/// An axis-aligned collision box in block-local coordinates (0..1).
public readonly record struct Box(float X0, float Y0, float Z0, float X1, float Y1, float Z1);

/// <param name="Tiles">Atlas tile per face [top, bottom, side] (+ optional [front] for facing blocks)</param>
/// <param name="Cls">Tool class that speeds up mining</param>
/// <param name="Hard">Seconds to mine by hand</param>
/// <param name="Req">Minimum pickaxe tier required (0 = hand ok)</param>
/// <param name="Drop">Item id given when mined</param>
/// <param name="Shape">Geometry class; Facing 0..3 = N/E/S/W, Open for doors/trapdoors</param>
public sealed record BlockDef(string Name, int[] Tiles, ToolClass Cls, double Hard, int Req, int Drop, Vector3 ParticleColor,
    BlockShape Shape = BlockShape.Cube, int Facing = 0, bool Open = false);

/// Data-driven block definitions. Adding a block = adding an entry here.
public static class BlockRegistry
{
    static Vector3 Rgb(int r, int g, int b) => new(r / 255f, g / 255f, b / 255f);

    // Generated atlas tile indices (base atlas is 0..13, TextureGen appends the rest).
    public const int DoorTopTile = 19, DoorBottomTile = 20, TrapdoorTile = 21;
    public const int ChestTopTile = 22, ChestSideTile = 23, ChestFrontTile = 24;
    public const int FurnaceTopTile = 25, FurnaceSideTile = 26, FurnaceFrontTile = 27, FurnaceLitTile = 28;
    public const int SnowTile = 29, DryGrassTopTile = 30, DryGrassSideTile = 31;
    public const int BirchSideTile = 32, SpruceSideTile = 33, CactusTile = 34, IceTile = 35;
    public const int RedSandTile = 36, TerracottaTile = 37, TerracottaRedTile = 38;
    public const int MyceliumTopTile = 39, MyceliumSideTile = 40, MushroomCapTile = 41, MushroomStemTile = 42;
    public const int GlassTile = 43;

    public const float PanelThickness = 3f / 16f; // doors & trapdoors

    public static readonly Dictionary<int, BlockDef> Blocks = new()
    {
        [Core.BlockId.Grass]   = new("Grass",       new[] { 0, 2, 1 },    ToolClass.Shovel, 0.60, 0, Core.BlockId.Dirt,    Rgb(93, 160, 60)),
        [Core.BlockId.Dirt]    = new("Dirt",        new[] { 2, 2, 2 },    ToolClass.Shovel, 0.50, 0, Core.BlockId.Dirt,    Rgb(121, 85, 58)),
        [Core.BlockId.Stone]   = new("Stone",       new[] { 3, 3, 3 },    ToolClass.Pick,   3.0,  1, Core.BlockId.Stone,   Rgb(138, 138, 138)),
        [Core.BlockId.Sand]    = new("Sand",        new[] { 4, 4, 4 },    ToolClass.Shovel, 0.40, 0, Core.BlockId.Sand,    Rgb(216, 203, 146)),
        [Core.BlockId.Wood]    = new("Wood",        new[] { 6, 6, 5 },    ToolClass.Axe,    2.0,  0, Core.BlockId.Wood,    Rgb(107, 74, 43)),
        [Core.BlockId.Leaves]  = new("Leaves",      new[] { 7, 7, 7 },    ToolClass.Axe,    0.25, 0, Core.BlockId.Leaves,  Rgb(62, 125, 44)),
        [Core.BlockId.Coal]    = new("Coal Ore",    new[] { 8, 8, 8 },    ToolClass.Pick,   3.2,  1, Core.BlockId.Coal,    Rgb(58, 58, 58)),
        [Core.BlockId.Iron]    = new("Iron Ore",    new[] { 9, 9, 9 },    ToolClass.Pick,   4.5,  2, Core.BlockId.Iron,    Rgb(201, 165, 131)),
        [Core.BlockId.Gold]    = new("Gold Ore",    new[] { 10, 10, 10 }, ToolClass.Pick,   4.0,  3, Core.BlockId.Gold,    Rgb(232, 201, 63)),
        [Core.BlockId.Diamond] = new("Diamond Ore", new[] { 11, 11, 11 }, ToolClass.Pick,   6.0,  3, Core.BlockId.Diamond, Rgb(89, 216, 212)),
        [Core.BlockId.Bedrock] = new("Bedrock",     new[] { 12, 12, 12 }, ToolClass.Pick,   double.PositiveInfinity, 9, 0, Rgb(51, 51, 51)),
        [Core.BlockId.Planks]  = new("Planks",      new[] { 13, 13, 13 }, ToolClass.Axe,    1.8,  0, Core.BlockId.Planks,  Rgb(160, 125, 75)),
        [Core.BlockId.Torch]       = new("Torch",       new[] { 14, 14, 14 }, ToolClass.None, 0.05, 0, Core.BlockId.Torch,       Rgb(255, 180, 60)),
        [Core.BlockId.TallGrass]   = new("Tall Grass",  new[] { 16, 16, 16 }, ToolClass.None, 0.05, 0, 0,                        Rgb(93, 160, 60)),
        [Core.BlockId.FlowerYellow] = new("Yellow Flower", new[] { 17, 17, 17 }, ToolClass.None, 0.05, 0, Core.BlockId.FlowerYellow, Rgb(230, 200, 40)),
        [Core.BlockId.FlowerRed]   = new("Red Flower",  new[] { 18, 18, 18 }, ToolClass.None, 0.05, 0, Core.BlockId.FlowerRed,   Rgb(210, 50, 50)),
        [Core.BlockId.Snow]     = new("Snow",      new[] { SnowTile, SnowTile, SnowTile },        ToolClass.Shovel, 0.35, 0, Core.BlockId.Snow, Rgb(238, 242, 245)),
        [Core.BlockId.DryGrass] = new("Dry Grass", new[] { DryGrassTopTile, 2, DryGrassSideTile }, ToolClass.Shovel, 0.60, 0, Core.BlockId.Dirt, Rgb(178, 160, 74)),
        [Core.BlockId.BirchWood]  = new("Birch Wood",  new[] { 6, 6, BirchSideTile },  ToolClass.Axe, 2.0, 0, Core.BlockId.BirchWood,  Rgb(216, 213, 202)),
        [Core.BlockId.SpruceWood] = new("Spruce Wood", new[] { 6, 6, SpruceSideTile }, ToolClass.Axe, 2.0, 0, Core.BlockId.SpruceWood, Rgb(84, 60, 34)),
        [Core.BlockId.Cactus]     = new("Cactus",      new[] { CactusTile, CactusTile, CactusTile }, ToolClass.None, 0.4, 0, Core.BlockId.Cactus, Rgb(54, 126, 44)),
        [Core.BlockId.Ice]        = new("Ice",         new[] { IceTile, IceTile, IceTile },          ToolClass.Pick, 0.6, 0, Core.BlockId.Ice,    Rgb(158, 196, 238)),
        [Core.BlockId.RedSand]    = new("Red Sand",    new[] { RedSandTile, RedSandTile, RedSandTile }, ToolClass.Shovel, 0.40, 0, Core.BlockId.RedSand, Rgb(191, 103, 49)),
        [Core.BlockId.Terracotta] = new("Terracotta",  new[] { TerracottaTile, TerracottaTile, TerracottaTile }, ToolClass.Pick, 1.3, 0, Core.BlockId.Terracotta, Rgb(172, 92, 48)),
        [Core.BlockId.TerracottaRed] = new("Red Terracotta", new[] { TerracottaRedTile, TerracottaRedTile, TerracottaRedTile }, ToolClass.Pick, 1.3, 0, Core.BlockId.TerracottaRed, Rgb(146, 62, 41)),
        [Core.BlockId.Mycelium]   = new("Mycelium",    new[] { MyceliumTopTile, 2, MyceliumSideTile }, ToolClass.Shovel, 0.60, 0, Core.BlockId.Dirt, Rgb(122, 103, 128)),
        [Core.BlockId.MushroomCap]  = new("Mushroom Cap",  new[] { MushroomCapTile, MushroomCapTile, MushroomCapTile },    ToolClass.Axe, 0.3, 0, Core.BlockId.MushroomCap,  Rgb(198, 48, 44)),
        [Core.BlockId.MushroomStem] = new("Mushroom Stem", new[] { MushroomStemTile, MushroomStemTile, MushroomStemTile }, ToolClass.Axe, 0.3, 0, Core.BlockId.MushroomStem, Rgb(212, 206, 193)),
        [Core.BlockId.Fence] = new("Fence", new[] { 13, 13, 13 }, ToolClass.Axe, 1.8, 0, Core.BlockId.Fence, Rgb(160, 125, 75), BlockShape.Fence),
        [Core.BlockId.Glass] = new("Glass", new[] { GlassTile, GlassTile, GlassTile }, ToolClass.None, 0.4, 0, Core.BlockId.Glass, Rgb(196, 220, 230)),
    };

    static readonly Vector3 PlankColor = Rgb(160, 125, 75);
    static readonly Vector3 StoneColor = Rgb(138, 138, 138);

    static BlockRegistry()
    {
        // Stairs: facing = direction of ascent (the full-height half sits on that side).
        for (int f = 0; f < 4; f++)
        {
            Blocks[Core.BlockId.PlankStairs + f] = new("Plank Stairs", new[] { 13, 13, 13 }, ToolClass.Axe, 1.8, 0,
                Core.BlockId.PlankStairs, PlankColor, BlockShape.Stairs, f);
            Blocks[Core.BlockId.StoneStairs + f] = new("Stone Stairs", new[] { 3, 3, 3 }, ToolClass.Pick, 3.0, 1,
                Core.BlockId.StoneStairs, StoneColor, BlockShape.Stairs, f);
        }

        Blocks[Core.BlockId.PlankSlab]    = new("Plank Slab", new[] { 13, 13, 13 }, ToolClass.Axe, 1.8, 0, Core.BlockId.PlankSlab, PlankColor, BlockShape.SlabBottom);
        Blocks[Core.BlockId.PlankSlabTop] = new("Plank Slab", new[] { 13, 13, 13 }, ToolClass.Axe, 1.8, 0, Core.BlockId.PlankSlab, PlankColor, BlockShape.SlabTop);
        Blocks[Core.BlockId.StoneSlab]    = new("Stone Slab", new[] { 3, 3, 3 },    ToolClass.Pick, 3.0, 1, Core.BlockId.StoneSlab, StoneColor, BlockShape.SlabBottom);
        Blocks[Core.BlockId.StoneSlabTop] = new("Stone Slab", new[] { 3, 3, 3 },    ToolClass.Pick, 3.0, 1, Core.BlockId.StoneSlab, StoneColor, BlockShape.SlabTop);

        // Vertical slabs: facing = which half of the cell is occupied.
        for (int f = 0; f < 4; f++)
        {
            Blocks[Core.BlockId.PlankSlabVert + f] = new("Plank Vertical Slab", new[] { 13, 13, 13 }, ToolClass.Axe, 1.8, 0,
                Core.BlockId.PlankSlabVert, PlankColor, BlockShape.SlabVert, f);
            Blocks[Core.BlockId.StoneSlabVert + f] = new("Stone Vertical Slab", new[] { 3, 3, 3 }, ToolClass.Pick, 3.0, 1,
                Core.BlockId.StoneSlabVert, StoneColor, BlockShape.SlabVert, f);
        }

        // Doors: facing = cell edge the closed panel occupies; open rotates the
        // panel one edge clockwise. Both halves drop the one door item.
        for (int f = 0; f < 4; f++)
            foreach (bool open in new[] { false, true })
            {
                Blocks[DoorVariant(f, open, upper: false)] = new("Wooden Door", new[] { DoorBottomTile, DoorBottomTile, DoorBottomTile },
                    ToolClass.Axe, 1.6, 0, Core.BlockId.Door, PlankColor, BlockShape.DoorLower, f, open);
                Blocks[DoorVariant(f, open, upper: true)] = new("Wooden Door", new[] { DoorTopTile, DoorTopTile, DoorTopTile },
                    ToolClass.Axe, 1.6, 0, Core.BlockId.Door, PlankColor, BlockShape.DoorUpper, f, open);
            }

        // Trapdoors: closed = plate on the cell floor; open = vertical panel
        // standing against the edge given by facing.
        for (int f = 0; f < 4; f++)
            foreach (bool open in new[] { false, true })
                Blocks[TrapdoorVariant(f, open)] = new("Trapdoor", new[] { TrapdoorTile, TrapdoorTile, TrapdoorTile },
                    ToolClass.Axe, 1.6, 0, Core.BlockId.Trapdoor, PlankColor, BlockShape.Trapdoor, f, open);

        for (int f = 0; f < 4; f++)
            Blocks[Core.BlockId.Chest + f] = new("Chest", new[] { ChestTopTile, ChestTopTile, ChestSideTile, ChestFrontTile },
                ToolClass.Axe, 1.8, 0, Core.BlockId.Chest, PlankColor, BlockShape.Chest, f);

        for (int f = 0; f < 4; f++)
            foreach (bool lit in new[] { false, true })
                Blocks[FurnaceVariant(f, lit)] = new("Furnace",
                    new[] { FurnaceTopTile, FurnaceTopTile, FurnaceSideTile, lit ? FurnaceLitTile : FurnaceFrontTile },
                    ToolClass.Pick, 3.0, 1, Core.BlockId.Furnace, StoneColor, BlockShape.Furnace, f, lit);

        BuildCollisionBoxes();
    }

    // ------------------------------------------------------------- variants

    public static int DoorVariant(int facing, bool open, bool upper) =>
        Core.BlockId.Door + (upper ? 8 : 0) + (open ? 4 : 0) + facing;

    public static int TrapdoorVariant(int facing, bool open) =>
        Core.BlockId.Trapdoor + (open ? 4 : 0) + facing;

    /// Lit furnaces reuse Open as the lit flag.
    public static int FurnaceVariant(int facing, bool lit) =>
        Core.BlockId.Furnace + (lit ? 4 : 0) + facing;

    public static BlockShape ShapeOf(int id) =>
        Blocks.TryGetValue(id, out var d) ? d.Shape : BlockShape.Cube;

    public static int FacingOf(int id) => Blocks.TryGetValue(id, out var d) ? d.Facing : 0;
    public static bool IsOpen(int id) => Blocks.TryGetValue(id, out var d) && d.Open;

    /// The id items/drops/icons use for this block (the family's first id).
    public static int BaseOf(int id) => Blocks.TryGetValue(id, out var d) && d.Drop != 0 ? d.Drop : id;

    public static bool IsDoor(int id) => ShapeOf(id) is BlockShape.DoorLower or BlockShape.DoorUpper;
    public static bool IsPartial(int id) =>
        Blocks.TryGetValue(id, out var d) && d.Shape != BlockShape.Cube && d.Shape != BlockShape.Furnace;

    /// Rotates a block id by quarter turns clockwise (seen from above).
    /// Every facing family stores its facing in the low bits, so the id can
    /// be re-based onto the new facing; facing-less blocks pass through.
    public static int RotateBlock(int id, int quarters)
    {
        quarters &= 3;
        if (quarters == 0 || !Blocks.TryGetValue(id, out var def)) return id;
        bool faced = def.Shape is BlockShape.Stairs or BlockShape.SlabVert or BlockShape.Chest
            or BlockShape.DoorLower or BlockShape.DoorUpper or BlockShape.Trapdoor or BlockShape.Furnace;
        return faced ? id - def.Facing + ((def.Facing + quarters) & 3) : id;
    }

    /// Facing index (0=N/-Z, 1=E/+X, 2=S/+Z, 3=W/-X) to direction.
    public static (int Dx, int Dz) FacingDir(int facing) => facing switch
    {
        0 => (0, -1),
        1 => (1, 0),
        2 => (0, 1),
        _ => (-1, 0),
    };

    // Water is deliberately not in Blocks: it can't be mined, placed, or held.

    static bool IsCross(int id) =>
        id is Core.BlockId.TallGrass or Core.BlockId.FlowerYellow or Core.BlockId.FlowerRed;

    /// Cross-quad decoration (grass tufts, flowers): rendered as two crossing
    /// billboard quads, no collision, doesn't block or absorb light.
    public static bool IsVegetation(int id) => IsCross(id);

    /// Blocks the player and animals collide with. Partial blocks (slabs,
    /// stairs, doors...) count as solid here; the player refines this with
    /// CollisionBoxes, everything else treats them as full cells.
    public static bool IsSolid(int id) =>
        id != Core.BlockId.Air && id != Core.BlockId.Water && id != Core.BlockId.Torch && !IsCross(id);

    /// Blocks that fully hide faces behind them (culling and ambient occlusion).
    /// Leaves and glass are cutout-transparent, so faces behind them must
    /// still render. Partial blocks never fill their cell, so they hide nothing.
    public static bool IsOpaque(int id) =>
        id != Core.BlockId.Air && id != Core.BlockId.Water && id != Core.BlockId.Torch
        && id != Core.BlockId.Leaves && id != Core.BlockId.Glass && !IsCross(id) && !IsPartial(id);

    /// How much light a block absorbs per step (15 = fully opaque).
    public static int LightOpacity(int id) => id switch
    {
        Core.BlockId.Air or Core.BlockId.Torch or Core.BlockId.Glass => 0,
        _ when IsCross(id) => 0,
        _ when IsPartial(id) => 0, // light flows through slab gaps, door frames...
        Core.BlockId.Leaves => 1,
        Core.BlockId.Water => 2,
        _ => 15,
    };

    /// Light level a block emits.
    public static int LightEmission(int id) => id switch
    {
        Core.BlockId.Torch => 14,
        _ when ShapeOf(id) == BlockShape.Furnace && IsOpen(id) => 13, // lit furnace
        _ => 0,
    };

    // ------------------------------------------------------------- collision

    static readonly Box[] NoBoxes = Array.Empty<Box>();
    static readonly Box[] FullCube = { new(0, 0, 0, 1, 1, 1) };
    static readonly Box[] TorchBox = { new(7f / 16, 0, 7f / 16, 9f / 16, 10f / 16, 9f / 16) };
    static readonly Box[] PlantBox = { new(0.2f, 0, 0.2f, 0.8f, 0.75f, 0.8f) };
    static readonly Dictionary<int, Box[]> _boxes = new();

    /// Collision boxes for a block id, in block-local 0..1 coordinates.
    public static Box[] CollisionBoxes(int id) =>
        _boxes.TryGetValue(id, out var b) ? b : IsSolid(id) ? FullCube : NoBoxes;

    /// What the crosshair can target: the visible shape rather than the full
    /// cell. Torches and plants have no collision but still get a tight box.
    public static Box[] SelectionBoxes(int id) =>
        id == Core.BlockId.Torch ? TorchBox
        : IsVegetation(id) ? PlantBox
        : CollisionBoxes(id);

    /// Blocks whose selection shape is smaller than their cell, so the
    /// targeting ray must hit the shape itself, not just the cell.
    public static bool HasPreciseSelection(int id) =>
        id == Core.BlockId.Torch || IsVegetation(id) || IsPartial(id);

    /// Union of the selection boxes — the bounds the highlight outline draws.
    public static (Vector3 Min, Vector3 Max) SelectionBounds(int id)
    {
        var boxes = SelectionBoxes(id);
        if (boxes.Length == 0) return (Vector3.Zero, Vector3.One);
        var mn = new Vector3(float.PositiveInfinity);
        var mx = new Vector3(float.NegativeInfinity);
        foreach (var b in boxes)
        {
            mn = Vector3.Min(mn, new Vector3(b.X0, b.Y0, b.Z0));
            mx = Vector3.Max(mx, new Vector3(b.X1, b.Y1, b.Z1));
        }
        return (mn, mx);
    }

    /// A thin full-height panel hugging one cell edge.
    static Box EdgePanel(int facing, float y0 = 0, float y1 = 1) => facing switch
    {
        0 => new Box(0, y0, 0, 1, y1, PanelThickness),
        1 => new Box(1 - PanelThickness, y0, 0, 1, y1, 1),
        2 => new Box(0, y0, 1 - PanelThickness, 1, y1, 1),
        _ => new Box(0, y0, 0, PanelThickness, y1, 1),
    };

    /// The full-height half of a cell on the given side.
    static Box HalfCell(int facing) => facing switch
    {
        0 => new Box(0, 0, 0, 1, 1, 0.5f),
        1 => new Box(0.5f, 0, 0, 1, 1, 1),
        2 => new Box(0, 0, 0.5f, 1, 1, 1),
        _ => new Box(0, 0, 0, 0.5f, 1, 1),
    };

    // ------------------------------------------------------------- fences

    // Fence geometry depends on its neighbours, so boxes are precomputed per
    // 4-bit connection mask (bit = facing 0..3, i.e. N/E/S/W) rather than per
    // block id. GameWorld derives the mask; the mesher and physics look the
    // boxes up here.
    const float FenceP0 = 6f / 16, FenceP1 = 10f / 16;   // post cross-section
    const float FenceR0 = 7f / 16, FenceR1 = 9f / 16;    // rail cross-section
    static readonly Box[][] FenceCollision = new Box[16][];
    static readonly Box[][] FenceMesh = new Box[16][];

    /// What a fence arm reaches out to: other fences, glass, and any
    /// full-cube block.
    public static bool FenceConnects(int id) =>
        ShapeOf(id) == BlockShape.Fence || id == Core.BlockId.Glass || IsOpaque(id);

    public static Box[] FenceCollisionBoxes(int mask) => FenceCollision[mask & 15];
    public static Box[] FenceMeshBoxes(int mask) => FenceMesh[mask & 15];

    /// An arm from the post to the cell edge in a facing direction, spanning
    /// y0..y1 with the given cross-section half-width bounds.
    static Box FenceArm(int facing, float a0, float a1, float y0, float y1) => facing switch
    {
        0 => new Box(a0, y0, 0, a1, y1, FenceP0),
        1 => new Box(FenceP1, y0, a0, 1, y1, a1),
        2 => new Box(a0, y0, FenceP1, a1, y1, 1),
        _ => new Box(0, y0, a0, FenceP0, y1, a1),
    };

    static void BuildFenceBoxes()
    {
        for (int mask = 0; mask < 16; mask++)
        {
            var coll = new List<Box> { new(FenceP0, 0, FenceP0, FenceP1, 1, FenceP1) };
            var mesh = new List<Box>(coll);
            for (int f = 0; f < 4; f++)
            {
                if ((mask & (1 << f)) == 0) continue;
                coll.Add(FenceArm(f, FenceP0, FenceP1, 0, 1)); // full wall keeps animals penned
                mesh.Add(FenceArm(f, FenceR0, FenceR1, 6f / 16, 9f / 16));
                mesh.Add(FenceArm(f, FenceR0, FenceR1, 12f / 16, 15f / 16));
            }
            FenceCollision[mask] = coll.ToArray();
            FenceMesh[mask] = mesh.ToArray();
        }
    }

    static void BuildCollisionBoxes()
    {
        BuildFenceBoxes();
        foreach (var (id, def) in Blocks)
        {
            switch (def.Shape)
            {
                case BlockShape.Stairs:
                    var back = HalfCell(def.Facing);
                    _boxes[id] = new[] { new Box(0, 0, 0, 1, 0.5f, 1), back with { Y0 = 0.5f } };
                    break;
                case BlockShape.SlabBottom:
                    _boxes[id] = new[] { new Box(0, 0, 0, 1, 0.5f, 1) };
                    break;
                case BlockShape.SlabTop:
                    _boxes[id] = new[] { new Box(0, 0.5f, 0, 1, 1, 1) };
                    break;
                case BlockShape.SlabVert:
                    _boxes[id] = new[] { HalfCell(def.Facing) };
                    break;
                case BlockShape.DoorLower:
                case BlockShape.DoorUpper:
                    _boxes[id] = new[] { EdgePanel(def.Open ? (def.Facing + 1) % 4 : def.Facing) };
                    break;
                case BlockShape.Trapdoor:
                    _boxes[id] = def.Open
                        ? new[] { EdgePanel(def.Facing) }
                        : new[] { new Box(0, 0, 0, 1, PanelThickness, 1) };
                    break;
                case BlockShape.Chest:
                    _boxes[id] = new[] { new Box(1f / 16, 0, 1f / 16, 15f / 16, 14f / 16, 15f / 16) };
                    break;
                case BlockShape.Fence:
                    // context-free fallback (drops, icons); live physics and
                    // meshing use the connection-aware Fence*Boxes instead
                    _boxes[id] = new[] { new Box(FenceP0, 0, FenceP0, FenceP1, 1, FenceP1) };
                    break;
            }
        }
    }
}
