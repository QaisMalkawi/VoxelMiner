namespace VoxelMiner.Gameplay;

using VoxelMiner.Core;
using VoxelMiner.World;

/// One furnace's live state: three slots and the burn/smelt timers.
public sealed class FurnaceState
{
    public const int Input = 0, Fuel = 1, Output = 2;

    public readonly ItemStack[] Slots = new ItemStack[3];
    public float Progress;       // seconds into the current smelt
    public float FuelLeft;       // burn seconds remaining
    public float FuelTotal = 1;  // capacity of the last fuel item (for the UI bar)

    public bool Lit => FuelLeft > 0;
}

/// Per-position item storage for chests and furnaces, plus the furnace
/// smelting simulation. Entries exist only while their block does; breaking
/// the block spills the contents via ItemSpilled.
public sealed class BlockEntities
{
    public const int ChestSlots = 27;

    readonly GameWorld _world;
    readonly Dictionary<(int X, int Y, int Z), ItemStack[]> _chests = new();
    readonly Dictionary<(int X, int Y, int Z), FurnaceState> _furnaces = new();

    public event Action<int, int, int, int, int> ItemSpilled; // x, y, z, itemId, count

    public BlockEntities(GameWorld world) => _world = world;

    public ItemStack[] Chest(int x, int y, int z)
    {
        if (!_chests.TryGetValue((x, y, z), out var slots))
            _chests[(x, y, z)] = slots = new ItemStack[ChestSlots];
        return slots;
    }

    public FurnaceState Furnace(int x, int y, int z)
    {
        if (!_furnaces.TryGetValue((x, y, z), out var f))
            _furnaces[(x, y, z)] = f = new FurnaceState();
        return f;
    }

    // --------------------------------------------------------- persistence

    public IEnumerable<((int X, int Y, int Z) Pos, ItemStack[] Slots)> AllChests()
    {
        foreach (var (pos, slots) in _chests) yield return (pos, slots);
    }

    public IEnumerable<((int X, int Y, int Z) Pos, FurnaceState State)> AllFurnaces()
    {
        foreach (var (pos, f) in _furnaces) yield return (pos, f);
    }

    public void RestoreChest(int x, int y, int z, ItemStack[] slots) => _chests[(x, y, z)] = slots;
    public void RestoreFurnace(int x, int y, int z, FurnaceState state) => _furnaces[(x, y, z)] = state;

    /// Call when a block was broken: drops any stored items at its position.
    public void OnBlockBroken(int x, int y, int z)
    {
        if (_chests.Remove((x, y, z), out var slots)) Spill(x, y, z, slots);
        if (_furnaces.Remove((x, y, z), out var f)) Spill(x, y, z, f.Slots);
    }

    void Spill(int x, int y, int z, ItemStack[] slots)
    {
        foreach (var s in slots)
            if (s != null)
                ItemSpilled?.Invoke(x, y, z, s.Id, s.Count);
    }

    /// Advances every furnace: consumes fuel, smelts input into output, and
    /// swaps the world block between the lit and unlit variant.
    public void Update(float dt)
    {
        foreach (var ((x, y, z), f) in _furnaces)
        {
            var recipe = f.Slots[FurnaceState.Input] != null ? Smelting.For(f.Slots[FurnaceState.Input].Id) : null;
            bool canSmelt = recipe != null && CanAccept(f.Slots[FurnaceState.Output], recipe.Out);

            if (f.FuelLeft > 0) f.FuelLeft = MathF.Max(0, f.FuelLeft - dt);

            // ignite: only when there is work to do and fuel to burn
            if (f.FuelLeft <= 0 && canSmelt && f.Slots[FurnaceState.Fuel] is { } fuel
                && Smelting.FuelSeconds.TryGetValue(fuel.Id, out float burn))
            {
                f.FuelLeft = f.FuelTotal = burn;
                fuel.Count--;
                if (fuel.Count <= 0) f.Slots[FurnaceState.Fuel] = null;
            }

            if (canSmelt && f.FuelLeft > 0)
            {
                f.Progress += dt;
                if (f.Progress >= recipe.Seconds)
                {
                    f.Progress = 0;
                    var input = f.Slots[FurnaceState.Input];
                    input.Count--;
                    if (input.Count <= 0) f.Slots[FurnaceState.Input] = null;
                    if (f.Slots[FurnaceState.Output] == null)
                        f.Slots[FurnaceState.Output] = new ItemStack { Id = recipe.Out, Count = 1 };
                    else
                        f.Slots[FurnaceState.Output].Count++;
                }
            }
            else
            {
                f.Progress = MathF.Max(0, f.Progress - 2 * dt); // smelting cools off
            }

            // reflect the burn state in the world (lit furnaces glow and emit light)
            int id = _world.GetBlock(x, y, z);
            if (BlockRegistry.ShapeOf(id) == BlockShape.Furnace && BlockRegistry.IsOpen(id) != f.Lit)
                _world.SetBlock(x, y, z, BlockRegistry.FurnaceVariant(BlockRegistry.FacingOf(id), f.Lit));
        }
    }

    static bool CanAccept(ItemStack output, int id) =>
        output == null || (output.Id == id && output.Count < ItemRegistry.StackOf(id));
}
