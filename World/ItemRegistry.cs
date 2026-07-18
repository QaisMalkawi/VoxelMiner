namespace VoxelMiner.World;

using VoxelMiner.Core;

/// <param name="Tier">Gates ore mining</param>
/// <param name="Speed">Mining-time multiplier on matching blocks</param>
public sealed record ToolSpec(ToolClass Cls, int Tier, double Speed, float Damage);

/// <param name="Points">Food points restored (2 = one drumstick)</param>
/// <param name="Saturation">Hidden saturation restored (Minecraft: points x modifier x 2)</param>
public sealed record FoodSpec(int Points, float Saturation);

public sealed record ItemDef(string Name, int Stack, int Durability = 0, ToolSpec Tool = null, FoodSpec Food = null);

/// <param name="Hand">Craftable straight from the inventory; everything else
/// needs a crafting table (like Minecraft's 2x2 vs 3x3 grid).</param>
public sealed record Recipe((int Id, int Count) Out, (int Id, int Count)[] In, bool Hand = false);

/// Non-block items (tools, crafting materials) and crafting recipes.
public static class ItemRegistry
{
    public static readonly Dictionary<int, ItemDef> ToolItems = new()
    {
        [ItemId.Stick]       = new("Stick", 64),
        [ItemId.WoodPick]    = new("Wooden Pickaxe",  1, 60,   new ToolSpec(ToolClass.Pick, 1, 2, 1.5f)),
        [ItemId.StonePick]   = new("Stone Pickaxe",   1, 130,  new ToolSpec(ToolClass.Pick, 2, 4, 1.75f)),
        [ItemId.IronPick]    = new("Iron Pickaxe",    1, 250,  new ToolSpec(ToolClass.Pick, 3, 6, 2f)),
        [ItemId.DiamondPick] = new("Diamond Pickaxe", 1, 1200, new ToolSpec(ToolClass.Pick, 4, 9, 2.5f)),
        [ItemId.Axe]         = new("Axe",             1, 120,  new ToolSpec(ToolClass.Axe, 1, 4, 3f)),
        [ItemId.Shovel]      = new("Shovel",          1, 120,  new ToolSpec(ToolClass.Shovel, 1, 4, 1)),
        [ItemId.Boat]        = new("Boat",            1),
        // Minecraft values: raw porkchop 3/1.8, raw mutton 2/1.2, raw chicken 2/1.2
        [ItemId.RawPigMeat]    = new("Raw Pig Meat",     64, Food: new FoodSpec(3, 1.8f)),
        [ItemId.RawSheepMeat]  = new("Raw Sheep Meat",   64, Food: new FoodSpec(2, 1.2f)),
        [ItemId.RawChickenMeat]= new("Raw Chicken Meat", 64, Food: new FoodSpec(2, 1.2f)),
        // cooked (furnace): porkchop 8/12.8, mutton 6/9.6, chicken 6/7.2
        [ItemId.CookedPigMeat]    = new("Cooked Pig Meat",     64, Food: new FoodSpec(8, 12.8f)),
        [ItemId.CookedSheepMeat]  = new("Cooked Sheep Meat",   64, Food: new FoodSpec(6, 9.6f)),
        [ItemId.CookedChickenMeat]= new("Cooked Chicken Meat", 64, Food: new FoodSpec(6, 7.2f)),
        [ItemId.Coal]      = new("Coal", 64),
        [ItemId.IronIngot] = new("Iron Ingot", 64),
        [ItemId.GoldIngot] = new("Gold Ingot", 64),
    };

    public static readonly Recipe[] Recipes =
    {
        new((BlockId.Planks, 4),      new[] { (BlockId.Wood, 1) }, Hand: true),
        new((BlockId.Planks, 4),      new[] { (BlockId.BirchWood, 1) }, Hand: true),
        new((BlockId.Planks, 4),      new[] { (BlockId.SpruceWood, 1) }, Hand: true),
        new((ItemId.Stick, 4),        new[] { (BlockId.Planks, 2) }, Hand: true),
        new((BlockId.CraftingTable, 1), new[] { (BlockId.Planks, 4) }, Hand: true),
        new((ItemId.WoodPick, 1),     new[] { (BlockId.Planks, 3), (ItemId.Stick, 2) }),
        new((ItemId.StonePick, 1),    new[] { (BlockId.Stone, 3), (ItemId.Stick, 2) }),
        new((ItemId.IronPick, 1),     new[] { (ItemId.IronIngot, 3), (ItemId.Stick, 2) }),
        new((ItemId.DiamondPick, 1),  new[] { (BlockId.Diamond, 3), (ItemId.Stick, 2) }),
        new((ItemId.Axe, 1),          new[] { (BlockId.Planks, 3), (ItemId.Stick, 2) }),
        new((ItemId.Shovel, 1),       new[] { (BlockId.Planks, 1), (ItemId.Stick, 2) }),
        new((BlockId.Torch, 4),       new[] { (ItemId.Coal, 1), (ItemId.Stick, 1) }, Hand: true),
        new((ItemId.Boat, 1),         new[] { (BlockId.Planks, 5) }),
        new((BlockId.PlankStairs, 4), new[] { (BlockId.Planks, 6) }),
        new((BlockId.StoneStairs, 4), new[] { (BlockId.Stone, 6) }),
        new((BlockId.PlankSlab, 6),   new[] { (BlockId.Planks, 3) }),
        new((BlockId.StoneSlab, 6),   new[] { (BlockId.Stone, 3) }),
        new((BlockId.PlankSlabVert, 6), new[] { (BlockId.Planks, 3) }),
        new((BlockId.StoneSlabVert, 6), new[] { (BlockId.Stone, 3) }),
        new((BlockId.Door, 3),        new[] { (BlockId.Planks, 6) }),
        new((BlockId.Trapdoor, 2),    new[] { (BlockId.Planks, 6) }),
        new((BlockId.Chest, 1),       new[] { (BlockId.Planks, 8) }),
        new((BlockId.Furnace, 1),     new[] { (BlockId.Stone, 8) }),
        new((BlockId.Fence, 3),       new[] { (BlockId.Planks, 4), (ItemId.Stick, 2) }),
        new((BlockId.Lever, 1),       new[] { (ItemId.Stick, 1), (BlockId.Stone, 1) }),
        new((BlockId.Button, 1),      new[] { (BlockId.Stone, 1) }),
        new((BlockId.Repeater, 1),    new[] { (BlockId.Stone, 3), (BlockId.Torch, 2), (BlockId.Dust, 1) }),
        new((BlockId.Comparator, 1),  new[] { (BlockId.Stone, 3), (BlockId.Torch, 3), (BlockId.Dust, 1) }),
        new((BlockId.Observer, 1),    new[] { (BlockId.Stone, 6), (BlockId.Dust, 2) }),
        new((BlockId.Piston, 1),      new[] { (BlockId.Planks, 3), (BlockId.Stone, 4), (ItemId.IronIngot, 1), (BlockId.Dust, 1) }),
        new((BlockId.Piston + 12, 1), new[] { (BlockId.Piston, 1), (BlockId.Slime, 1) }), // sticky piston
        new((BlockId.Slime, 1),       new[] { (BlockId.Leaves, 4), (BlockId.Dust, 1) }),
        new((BlockId.Honey, 1),       new[] { (BlockId.Sand, 4), (BlockId.Dust, 1) }),
    };

    public static string NameOf(int id) =>
        BlockRegistry.Blocks.TryGetValue(id, out var b) ? b.Name : ToolItems[id].Name;

    public static int StackOf(int id) =>
        BlockRegistry.Blocks.ContainsKey(id) ? 64 : ToolItems[id].Stack;

    public static bool IsPlaceableBlock(int id) => BlockRegistry.Blocks.ContainsKey(id);

    public static ToolSpec ToolOf(int id) =>
        ToolItems.TryGetValue(id, out var t) ? t.Tool : null;

    public static FoodSpec FoodOf(int id) =>
        ToolItems.TryGetValue(id, out var t) ? t.Food : null;

    public static int MaxDurability(int id) =>
        ToolItems.TryGetValue(id, out var t) ? t.Durability : 0;
}

public sealed record SmeltRecipe(int In, int Out, float Seconds);

/// Furnace data: what smelts into what, and how long each fuel item burns.
public static class Smelting
{
    public static readonly SmeltRecipe[] Recipes =
    {
        new(ItemId.RawPigMeat, ItemId.CookedPigMeat, 10f),
        new(ItemId.RawSheepMeat, ItemId.CookedSheepMeat, 10f),
        new(ItemId.RawChickenMeat, ItemId.CookedChickenMeat, 10f),
        new(BlockId.Sand, BlockId.Glass, 10f),
        new(BlockId.RedSand, BlockId.Glass, 10f),
        new(BlockId.Coal, ItemId.Coal, 10f),
        new(BlockId.Iron, ItemId.IronIngot, 10f),
        new(BlockId.Gold, ItemId.GoldIngot, 10f),
    };

    public static readonly Dictionary<int, float> FuelSeconds = new()
    {
        [ItemId.Coal] = 80f, // the smelted lump burns; raw ore doesn't
        [BlockId.Wood] = 15f,
        [BlockId.BirchWood] = 15f,
        [BlockId.SpruceWood] = 15f,
        [BlockId.Planks] = 15f,
        [ItemId.Stick] = 5f,
    };

    public static SmeltRecipe For(int id)
    {
        foreach (var r in Recipes) if (r.In == id) return r;
        return null;
    }

    public static bool IsFuel(int id) => FuelSeconds.ContainsKey(id);
}
