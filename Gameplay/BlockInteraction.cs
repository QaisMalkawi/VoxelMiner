using System.Numerics;

namespace VoxelMiner.Gameplay;

using VoxelMiner.Core;
using VoxelMiner.World;
using static VoxelMiner.Core.Constants;

public sealed record RayHit(int X, int Y, int Z, int FaceX, int FaceY, int FaceZ, Vector3 Point);

/// Mining and placing: voxel raycast (DDA), tool-aware mining progress,
/// tier gating, durability wear, and block placement from the selected slot.
/// Side effects (particles, sounds, toasts) are raised as events.
public sealed class BlockInteraction
{
    const float Reach = 6f;
    const float PlaceInterval = 0.22f;
    const float EatDuration = 1.61f;    // Minecraft: 32 ticks of chewing
    const float ChewInterval = 0.23f;
    const float MineExhaustion = 0.005f; // per block broken (Minecraft)
    static readonly string[] TierNames = { "", "a wooden", "a stone", "an iron" };

    readonly GameWorld _world;
    readonly Player _player;
    readonly Inventory _inventory;

    public RayHit MineTarget { get; private set; }
    public double MineProgress { get; private set; }
    public RayHit Highlighted { get; private set; }
    public float EatProgress { get; private set; }

    /// Creative: instant breaking (even bedrock), no drops, no tool wear,
    /// and placing doesn't consume items.
    public GameMode Mode = GameMode.Survival;

    public event Action<string> Toast;
    public event Action<int, int, int, int> BlockBroken;   // x, y, z, blockId
    public event Action<int, int, int, int> ItemDropped;   // x, y, z, itemId (survival breaks)
    public event Action BlockPlaced;
    public event Action MineTick;
    public event Action EatTick;                            // chewing, while holding use on food
    public event Action Ate;
    public event Action<int, int, int, bool> ContainerOpened; // x, y, z, isFurnace
    public event Action CraftingOpened;                       // right-clicked a crafting table
    public event Action DoorToggled;

    float _tickTimer, _placeTimer, _creativeBreakTimer;
    float _eatTimer, _chewTimer;
    bool _wasPlacing, _usedInteractive;

    public BlockInteraction(GameWorld world, Player player, Inventory inventory)
    {
        _world = world;
        _player = player;
        _inventory = inventory;
    }

    public void Update(float dt, bool mining, bool placing)
    {
        UpdateMining(dt, mining);
        // a fresh right-click first tries to use the targeted block (open a
        // door, chest...); if it did, the rest of this hold neither eats nor places
        if (placing && !_wasPlacing)
        {
            _placeTimer = 0;
            _usedInteractive = TryUse();
        }
        if (!placing) _usedInteractive = false;
        _wasPlacing = placing;
        if (_usedInteractive)
        {
            _eatTimer = 0;
            EatProgress = 0;
            return;
        }

        // food is eaten, never placed
        var held = _inventory.SelectedItem;
        var food = held != null ? ItemRegistry.FoodOf(held.Id) : null;
        if (food != null)
        {
            UpdateEating(dt, placing, food);
            return;
        }
        _eatTimer = 0;
        EatProgress = 0;

        if (placing)
        {
            _placeTimer -= dt;
            if (_placeTimer <= 0)
            {
                TryPlace();
                _placeTimer = PlaceInterval;
            }
        }
    }

    /// Hold right-click to chew for EatDuration, Minecraft-style. Only works
    /// while the hunger bar has room; creative pins hunger full, so it no-ops.
    void UpdateEating(float dt, bool eating, FoodSpec food)
    {
        if (!eating || !_player.CanEat)
        {
            _eatTimer = 0;
            EatProgress = 0;
            return;
        }
        _eatTimer += dt;
        _chewTimer -= dt;
        if (_chewTimer <= 0)
        {
            EatTick?.Invoke();
            _chewTimer = ChewInterval;
        }
        if (_eatTimer >= EatDuration)
        {
            _eatTimer = 0;
            _player.Eat(food);
            _inventory.ConsumeSelected();
            Ate?.Invoke();
        }
        EatProgress = _eatTimer / EatDuration;
    }

    /// Voxel DDA (Amanatides & Woo).
    public RayHit Raycast(float maxDist = Reach)
    {
        Vector3 o = _player.EyePos;
        Vector3 d = _player.ViewDir;
        int x = (int)MathF.Floor(o.X), y = (int)MathF.Floor(o.Y), z = (int)MathF.Floor(o.Z);
        int stX = d.X > 0 ? 1 : -1, stY = d.Y > 0 ? 1 : -1, stZ = d.Z > 0 ? 1 : -1;
        float dX = MathF.Abs(1 / d.X), dY = MathF.Abs(1 / d.Y), dZ = MathF.Abs(1 / d.Z);
        float tX = (stX > 0 ? x + 1 - o.X : o.X - x) * dX;
        float tY = (stY > 0 ? y + 1 - o.Y : o.Y - y) * dY;
        float tZ = (stZ > 0 ? z + 1 - o.Z : o.Z - z) * dZ;
        int fx = 0, fy = 0, fz = 0;
        for (int i = 0; i < 200; i++)
        {
            float t;
            if (tX < tY && tX < tZ) { x += stX; t = tX; tX += dX; fx = -stX; fy = 0; fz = 0; }
            else if (tY < tZ)       { y += stY; t = tY; tY += dY; fx = 0; fy = -stY; fz = 0; }
            else                    { z += stZ; t = tZ; tZ += dZ; fx = 0; fy = 0; fz = -stZ; }
            if (t > maxDist) return null;
            int id = y >= 0 && y < WorldHeight ? _world.GetBlock(x, y, z) : BlockId.Air;
            // the ray passes through water (like Minecraft) but can target torches
            if (id == BlockId.Air || id == BlockId.Water) continue;
            if (!BlockRegistry.HasPreciseSelection(id))
                return new RayHit(x, y, z, fx, fy, fz, o + d * t);
            // partial shapes (stairs, slabs, torches, plants...): the ray must
            // strike the visible geometry, not just enter the cell
            var boxHit = RayBoxes(o, d, x, y, z, _world.SelectionBoxesAt(x, y, z), maxDist, fx, fy, fz);
            if (boxHit != null) return boxHit;
        }
        return null;
    }

    /// Nearest intersection of the ray with any of a block's selection boxes;
    /// null when the ray threads past them. The reported face is the box face
    /// that was struck (falling back to the cell-entry face on edge cases).
    static RayHit RayBoxes(Vector3 o, Vector3 d, int x, int y, int z, Box[] boxes, float maxDist, int fx, int fy, int fz)
    {
        float best = float.PositiveInfinity;
        int nx = fx, ny = fy, nz = fz;
        foreach (var b in boxes)
        {
            if (!RayBox(o, d, x + b.X0, y + b.Y0, z + b.Z0, x + b.X1, y + b.Y1, z + b.Z1,
                    out float t, out int axis, out int sign) || t >= best)
                continue;
            best = t;
            if (axis >= 0)
            {
                nx = axis == 0 ? sign : 0;
                ny = axis == 1 ? sign : 0;
                nz = axis == 2 ? sign : 0;
            }
        }
        return best <= maxDist ? new RayHit(x, y, z, nx, ny, nz, o + d * best) : null;
    }

    /// Slab-method ray/AABB test. axis/sign describe the entry face's outward
    /// normal; axis is -1 when the ray starts inside the box.
    static bool RayBox(Vector3 o, Vector3 d, float x0, float y0, float z0, float x1, float y1, float z1,
        out float tHit, out int axis, out int sign)
    {
        tHit = 0;
        axis = -1;
        sign = 0;
        Span<float> og = stackalloc float[] { o.X, o.Y, o.Z };
        Span<float> dir = stackalloc float[] { d.X, d.Y, d.Z };
        Span<float> mn = stackalloc float[] { x0, y0, z0 };
        Span<float> mx = stackalloc float[] { x1, y1, z1 };
        float tmin = 0f, tmax = float.PositiveInfinity;
        for (int i = 0; i < 3; i++)
        {
            if (MathF.Abs(dir[i]) < 1e-8f)
            {
                if (og[i] < mn[i] || og[i] > mx[i]) return false;
                continue;
            }
            float inv = 1f / dir[i];
            float t1 = (mn[i] - og[i]) * inv, t2 = (mx[i] - og[i]) * inv;
            if (t1 > t2) (t1, t2) = (t2, t1);
            if (t1 > tmin)
            {
                tmin = t1;
                axis = i;
                sign = dir[i] > 0 ? -1 : 1;
            }
            tmax = MathF.Min(tmax, t2);
            if (tmax < tmin) return false;
        }
        tHit = tmin;
        return true;
    }

    void UpdateMining(float dt, bool mining)
    {
        if (Mode == GameMode.Spectator)
        {
            MineTarget = null;
            MineProgress = 0;
            return;
        }

        var hit = Raycast();
        Highlighted = hit;

        if (Mode == GameMode.Creative)
        {
            MineTarget = null;
            MineProgress = 0;
            _creativeBreakTimer -= dt;
            if (mining && hit != null && _creativeBreakTimer <= 0)
            {
                int broken = _world.GetBlock(hit.X, hit.Y, hit.Z);
                _world.SetBlock(hit.X, hit.Y, hit.Z, BlockId.Air);
                RemoveOtherDoorHalf(hit.X, hit.Y, hit.Z, broken);
                BlockBroken?.Invoke(hit.X, hit.Y, hit.Z, broken);
                _creativeBreakTimer = 0.16f; // slight pacing so held clicks stay controllable
            }
            return;
        }

        bool same = hit != null && MineTarget != null &&
                    hit.X == MineTarget.X && hit.Y == MineTarget.Y && hit.Z == MineTarget.Z;

        if (!mining || hit == null)
        {
            MineTarget = null;
            MineProgress = 0;
            return;
        }

        bool fresh = !same;
        if (fresh)
        {
            MineTarget = hit;
            MineProgress = 0;
        }
        int id = _world.GetBlock(hit.X, hit.Y, hit.Z);
        BlockRegistry.Blocks.TryGetValue(id, out var def);
        var held = _inventory.SelectedItem;
        var tool = held != null ? ItemRegistry.ToolOf(held.Id) : null;
        int req = def?.Req ?? 0;

        if (def == null || double.IsInfinity(def.Hard))
        {
            MineProgress = 0;
        }
        else if (req > 0 && (tool == null || tool.Cls != ToolClass.Pick || tool.Tier < req))
        {
            MineProgress = 0;
            if (fresh) Toast?.Invoke($"Needs {TierNames[req]} pickaxe or better!");
        }
        else
        {
            double mult = tool != null && tool.Cls == def.Cls ? tool.Speed : 1;
            MineProgress += dt * mult / def.Hard;
            _tickTimer -= dt;
            if (_tickTimer <= 0)
            {
                MineTick?.Invoke();
                _tickTimer = 0.14f;
            }
            if (MineProgress >= 1) BreakBlock(hit, id, def, tool != null);
        }
    }

    void BreakBlock(RayHit hit, int id, BlockDef def, bool usedTool)
    {
        _world.SetBlock(hit.X, hit.Y, hit.Z, BlockId.Air);
        RemoveOtherDoorHalf(hit.X, hit.Y, hit.Z, id);
        BlockBroken?.Invoke(hit.X, hit.Y, hit.Z, id);

        // the block pops out as a drop entity; it reaches the inventory only
        // when the player walks over and collects it
        if (def.Drop != 0) ItemDropped?.Invoke(hit.X, hit.Y, hit.Z, def.Drop);

        if (usedTool)
        {
            string broken = _inventory.WearSelectedTool();
            if (broken != null) Toast?.Invoke($"Your {broken} broke!");
        }
        _player.AddExhaustion(MineExhaustion);
        MineTarget = null;
        MineProgress = 0;
    }

    void TryPlace()
    {
        if (Mode == GameMode.Spectator) return;

        var hit = Raycast();
        if (hit == null) return;
        int x = hit.X + hit.FaceX, y = hit.Y + hit.FaceY, z = hit.Z + hit.FaceZ;
        if (y < 0 || y >= WorldHeight) return;
        int target = _world.GetBlock(x, y, z);
        if (target != BlockId.Air && target != BlockId.Water) return; // solid blocks displace water

        var held = _inventory.SelectedItem;
        if (held == null)
        {
            Toast?.Invoke("Selected slot is empty - mine some blocks!");
            return;
        }
        if (!ItemRegistry.IsPlaceableBlock(held.Id))
        {
            Toast?.Invoke($"Can't place {ItemRegistry.NameOf(held.Id)}");
            return;
        }
        if (held.Id == BlockId.Torch && target == BlockId.Water)
        {
            Toast?.Invoke("Torches don't work underwater!");
            return;
        }
        if (_player.IntersectsBlock(x, y, z)) return;

        // doors span two cells
        if (held.Id == BlockId.Door)
        {
            if (y + 1 >= WorldHeight || _world.GetBlock(x, y + 1, z) != BlockId.Air)
            {
                Toast?.Invoke("Doors need room above!");
                return;
            }
            if (_player.IntersectsBlock(x, y + 1, z)) return;
            int df = Opposite(PlayerFacing());
            if (Mode == GameMode.Survival) _inventory.ConsumeSelected();
            _world.SetBlock(x, y, z, BlockRegistry.DoorVariant(df, open: false, upper: false));
            _world.SetBlock(x, y + 1, z, BlockRegistry.DoorVariant(df, open: false, upper: true));
            BlockPlaced?.Invoke();
            return;
        }

        int id = OrientedVariant(held.Id, hit);
        // flat redstone components need their supporting block to exist
        if (BlockRegistry.IsFlat(id))
        {
            var def = BlockRegistry.Blocks[id];
            var (ax, ay, az) = def.Shape is BlockShape.Lever or BlockShape.Button
                ? BlockRegistry.AttachDir(def.Aux)
                : (0, -1, 0);
            if (!BlockRegistry.IsFullCube(_world.GetBlock(x + ax, y + ay, z + az)))
            {
                Toast?.Invoke("Needs a solid block to sit on!");
                return;
            }
        }
        if (Mode == GameMode.Survival) _inventory.ConsumeSelected(); // creative: infinite
        _world.SetBlock(x, y, z, id);
        BlockPlaced?.Invoke();
    }

    // --------------------------------------------------------- use & orientation

    /// Right-click actions on the targeted block: toggle doors/trapdoors,
    /// open chests and furnaces. Returns true when the click was consumed.
    bool TryUse()
    {
        if (Mode == GameMode.Spectator) return false;
        var hit = Raycast();
        if (hit == null) return false;
        int id = _world.GetBlock(hit.X, hit.Y, hit.Z);
        if (id == BlockId.CraftingTable)
        {
            CraftingOpened?.Invoke();
            return true;
        }
        switch (BlockRegistry.ShapeOf(id))
        {
            case BlockShape.DoorLower:
            case BlockShape.DoorUpper:
                ToggleDoor(hit.X, hit.Y, hit.Z, id);
                return true;
            case BlockShape.Trapdoor:
                _world.SetBlock(hit.X, hit.Y, hit.Z,
                    BlockRegistry.TrapdoorVariant(BlockRegistry.FacingOf(id), !BlockRegistry.IsOpen(id)));
                DoorToggled?.Invoke();
                return true;
            case BlockShape.Chest:
                ContainerOpened?.Invoke(hit.X, hit.Y, hit.Z, false);
                return true;
            case BlockShape.Furnace:
                ContainerOpened?.Invoke(hit.X, hit.Y, hit.Z, true);
                return true;
            case BlockShape.Lever:
            {
                var def = BlockRegistry.Blocks[id];
                _world.SetBlock(hit.X, hit.Y, hit.Z, BlockRegistry.LeverVariant(def.Aux, !def.Open));
                DoorToggled?.Invoke();
                return true;
            }
            case BlockShape.Button:
            {
                var def = BlockRegistry.Blocks[id];
                if (!def.Open) // the redstone sim schedules the release
                {
                    _world.SetBlock(hit.X, hit.Y, hit.Z, BlockRegistry.ButtonVariant(def.Aux, pressed: true));
                    DoorToggled?.Invoke();
                }
                return true;
            }
            case BlockShape.Repeater:
            {
                var def = BlockRegistry.Blocks[id];
                _world.SetBlock(hit.X, hit.Y, hit.Z,
                    BlockRegistry.RepeaterVariant(def.Facing, def.Aux % 4 + 1, def.Open));
                DoorToggled?.Invoke();
                return true;
            }
            case BlockShape.Comparator:
            {
                var def = BlockRegistry.Blocks[id];
                _world.SetBlock(hit.X, hit.Y, hit.Z,
                    BlockRegistry.ComparatorVariant(def.Facing, def.Aux == 0, def.Open));
                DoorToggled?.Invoke();
                return true;
            }
        }
        return false;
    }

    /// Opens/closes both halves of a door in sync.
    void ToggleDoor(int x, int y, int z, int id)
    {
        int lowerY = BlockRegistry.ShapeOf(id) == BlockShape.DoorUpper ? y - 1 : y;
        int lower = _world.GetBlock(x, lowerY, z);
        int facing = BlockRegistry.FacingOf(lower);
        bool open = !BlockRegistry.IsOpen(lower);
        _world.SetBlock(x, lowerY, z, BlockRegistry.DoorVariant(facing, open, upper: false));
        if (BlockRegistry.IsDoor(_world.GetBlock(x, lowerY + 1, z)))
            _world.SetBlock(x, lowerY + 1, z, BlockRegistry.DoorVariant(facing, open, upper: true));
        DoorToggled?.Invoke();
    }

    /// Breaking one door half silently removes the other (the broken half
    /// already dropped the single door item). Pistons pair the same way:
    /// breaking a head removes its extended base and vice versa.
    void RemoveOtherDoorHalf(int x, int y, int z, int broken)
    {
        var shape = BlockRegistry.ShapeOf(broken);
        if (shape == BlockShape.DoorLower && BlockRegistry.ShapeOf(_world.GetBlock(x, y + 1, z)) == BlockShape.DoorUpper)
            _world.SetBlock(x, y + 1, z, BlockId.Air);
        else if (shape == BlockShape.DoorUpper && BlockRegistry.ShapeOf(_world.GetBlock(x, y - 1, z)) == BlockShape.DoorLower)
            _world.SetBlock(x, y - 1, z, BlockId.Air);
        else if (shape is BlockShape.PistonHead or BlockShape.Piston)
        {
            var (dx, dy, dz) = BlockRegistry.Facing6(BlockRegistry.FacingOf(broken));
            if (shape == BlockShape.PistonHead)
            {
                int bx = x - dx, by = y - dy, bz = z - dz;
                int baseId = _world.GetBlock(bx, by, bz);
                if (BlockRegistry.ShapeOf(baseId) == BlockShape.Piston && BlockRegistry.IsOpen(baseId))
                    _world.SetBlock(bx, by, bz, BlockId.Air);
            }
            else if (BlockRegistry.IsOpen(broken)
                     && BlockRegistry.ShapeOf(_world.GetBlock(x + dx, y + dy, z + dz)) == BlockShape.PistonHead)
            {
                _world.SetBlock(x + dx, y + dy, z + dz, BlockId.Air);
            }
        }
    }

    /// The player's dominant horizontal look direction (0=N/-Z, 1=E, 2=S, 3=W).
    int PlayerFacing()
    {
        var d = _player.ViewDir;
        return MathF.Abs(d.X) >= MathF.Abs(d.Z) ? (d.X > 0 ? 1 : 3) : (d.Z > 0 ? 2 : 0);
    }

    static int Opposite(int facing) => (facing + 2) % 4;

    static int FacingIndex(int dx, int dz) => dx == 1 ? 1 : dx == -1 ? 3 : dz == 1 ? 2 : 0;

    static float Frac(float v) => v - MathF.Floor(v);

    /// Picks the concrete block variant for a held base item: stairs face the
    /// player's walk direction, slabs go top/bottom (or hug the clicked wall)
    /// by hit position, chests and furnaces turn their front to the player.
    int OrientedVariant(int baseId, RayHit hit)
    {
        switch (BlockRegistry.ShapeOf(baseId))
        {
            case BlockShape.Stairs:
                return baseId + PlayerFacing();
            case BlockShape.SlabBottom:
            {
                bool top = hit.FaceY < 0 || (hit.FaceY == 0 && Frac(hit.Point.Y) > 0.5f);
                return top ? baseId + 1 : baseId; // base = bottom slab, +1 = top slab
            }
            case BlockShape.SlabVert:
            {
                if (hit.FaceY == 0) // clicked a wall: occupy the half against it
                    return baseId + FacingIndex(-hit.FaceX, -hit.FaceZ);
                float dx = Frac(hit.Point.X) - 0.5f, dz = Frac(hit.Point.Z) - 0.5f;
                return baseId + (MathF.Abs(dx) >= MathF.Abs(dz) ? (dx > 0 ? 1 : 3) : (dz > 0 ? 2 : 0));
            }
            case BlockShape.Trapdoor:
                return BlockRegistry.TrapdoorVariant(Opposite(PlayerFacing()), open: false);
            case BlockShape.Chest:
            case BlockShape.Furnace:
                return baseId + Opposite(PlayerFacing());
            case BlockShape.Lever:
                return BlockRegistry.LeverVariant(AttachFromHit(hit), on: false);
            case BlockShape.Button:
                return BlockRegistry.ButtonVariant(AttachFromHit(hit), pressed: false);
            case BlockShape.Repeater:
                return BlockRegistry.RepeaterVariant(PlayerFacing(), delay: 1, powered: false);
            case BlockShape.Comparator:
                return BlockRegistry.ComparatorVariant(PlayerFacing(), subtract: false, powered: false);
            case BlockShape.Observer:
                return BlockRegistry.ObserverVariant(Facing6TowardPlayer(), powered: false);
            case BlockShape.Piston:
                return BlockRegistry.PistonVariant(Facing6TowardPlayer(), extended: false,
                    sticky: BlockRegistry.AuxOf(baseId) == 1);
            default:
                return baseId;
        }
    }

    /// Lever/button mount face from the clicked face: top/bottom clicks give
    /// a floor mount, wall clicks hang it on that wall.
    static int AttachFromHit(RayHit hit) =>
        hit.FaceY != 0 ? 0 : FacingIndex(-hit.FaceX, -hit.FaceZ) + 1;

    /// 6-way facing pointing back at the player (pistons face their placer,
    /// observers watch away over your shoulder — like Minecraft).
    int Facing6TowardPlayer()
    {
        var d = _player.ViewDir;
        if (d.Y < -0.65f) return 4; // looking down: face up toward the player
        if (d.Y > 0.65f) return 5;
        return Opposite(PlayerFacing());
    }
}
