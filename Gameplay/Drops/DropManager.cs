using System.Numerics;

namespace VoxelMiner.Gameplay.Drops;

using VoxelMiner.Core;
using VoxelMiner.World;

/// Minecraft-style item drops: broken blocks pop out as little entities that
/// fall, bob on the ground (or float up through water), drift toward a
/// nearby player, and land in the inventory on contact. Nearby drops of the
/// same item merge; drops despawn after five minutes.
public sealed class DropManager
{
    const float Gravity = 18f;
    const float PickupDelay = 0.35f;  // let the drop pop out before it can be grabbed
    const float MagnetRange = 1.5f;
    const float MagnetAccel = 26f;
    const float CollectRange = 1f;
    const float MergeRange = 1.5f;
    const float DespawnAge = 300f;
    const float Radius = 0.1f;       // half-extent of the rendered cube
    const int MaxDrops = 256;

    readonly GameWorld _world;
    public readonly List<ItemDrop> All = new();
    static readonly Random Rng = new();

    /// An item just entered the inventory.
    public event Action<int> Collected;


    public static DropManager Instance { get; private set; }
    public DropManager(GameWorld world)
    {
        _world = world;
        Instance = this;
    }

    public void Spawn(float x, float y, float z, int id, int count = 1)
    {
        // merge into a nearby drop of the same item instead of cluttering
        foreach (var other in All)
            if (other.Id == id && (other.Pos - new Vector3(x, y, z)).Length() < MergeRange)
            {
                other.Count += count;
                other.Age = 0;
                return;
            }
        if (All.Count >= MaxDrops) All.RemoveAt(0);
        All.Add(new ItemDrop
        {
            Pos = new Vector3(x, y, z),
            Vel = new Vector3(((float)Rng.NextDouble() - 0.5f) * 2.2f, 3.4f, ((float)Rng.NextDouble() - 0.5f) * 2.2f),
            Id = id,
            Count = count,
        });
    }
    public void Spawn(float x, float y, float z, ItemStack itemStack)
    {
        // merge into a nearby drop of the same item instead of cluttering
        foreach (var other in All)
            if (other.Id == itemStack.Id && (other.Pos - new Vector3(x, y, z)).Length() < MergeRange)
            {
                other.Count += itemStack.Count;
                other.Age = 0;
                return;
            }
        if (All.Count >= MaxDrops) All.RemoveAt(0);
        All.Add(new ItemDrop
        {
            Pos = new Vector3(x, y, z),
            Vel = new Vector3(((float)Rng.NextDouble() - 0.5f) * 2.2f, 3.4f, ((float)Rng.NextDouble() - 0.5f) * 2.2f),
            Id = itemStack.Id,
            Count = itemStack.Count,
        });
    }

    public void Update(float dt, Player player, Inventory inventory)
    {
        var mouth = player.Pos + new Vector3(0, 0.9f, 0);
        for (int i = All.Count - 1; i >= 0; i--)
        {
            var d = All[i];
            d.Age += dt;
            if (d.Age > DespawnAge) { All.RemoveAt(i); continue; }
            if (d.RetryTimer > 0) d.RetryTimer -= dt;

            // magnetize toward the player, then collect on contact
            var toPlayer = mouth - d.Pos;
            float dist = toPlayer.Length();
            if (d.Age > PickupDelay && d.RetryTimer <= 0 && dist < CollectRange)
            {
                if (inventory.CanFit(d.Id, d.Count))
                {
                    inventory.AddItem(d.Id, d.Count);
                    Collected?.Invoke(d.Id);
                    All.RemoveAt(i);
                    continue;
                }
                d.RetryTimer = 1f;
            }
            if (d.Age > PickupDelay && d.RetryTimer <= 0 && dist < MagnetRange && dist > 1e-3f)
                d.Vel += toPlayer / dist * MagnetAccel * dt;

            StepPhysics(d, dt);
        }
    }

    void StepPhysics(ItemDrop d, float dt)
    {
        int bx = (int)MathF.Floor(d.Pos.X), by = (int)MathF.Floor(d.Pos.Y), bz = (int)MathF.Floor(d.Pos.Z);
        bool inWater = _world.GetBlock(bx, by, bz) == BlockId.Water;
        if (inWater)
            d.Vel.Y = MathF.Min(d.Vel.Y + 14f * dt, 1.3f); // buoyant: bob up to the surface
        else
            d.Vel.Y = MathF.Max(d.Vel.Y - Gravity * dt, -22f);

        bool grounded = _world.IsSolidAt(bx, (int)MathF.Floor(d.Pos.Y - Radius - 0.02f), bz);
        float drag = grounded || inWater ? 0.001f : 0.35f;
        d.Vel.X *= MathF.Pow(drag, dt);
        d.Vel.Z *= MathF.Pow(drag, dt);

        float nx = d.Pos.X + d.Vel.X * dt;
        if (_world.IsSolidAt((int)MathF.Floor(nx), by, bz)) d.Vel.X = 0;
        else d.Pos.X = nx;
        float nz = d.Pos.Z + d.Vel.Z * dt;
        if (_world.IsSolidAt((int)MathF.Floor(d.Pos.X), by, (int)MathF.Floor(nz))) d.Vel.Z = 0;
        else d.Pos.Z = nz;

        float ny = d.Pos.Y + d.Vel.Y * dt;
        if (d.Vel.Y < 0 && _world.IsSolidAt((int)MathF.Floor(d.Pos.X), (int)MathF.Floor(ny - Radius), (int)MathF.Floor(d.Pos.Z)))
        {
            d.Pos.Y = MathF.Floor(ny - Radius) + 1 + Radius;
            d.Vel.Y = 0;
        }
        else if (d.Vel.Y > 0 && _world.IsSolidAt((int)MathF.Floor(d.Pos.X), (int)MathF.Floor(ny + Radius), (int)MathF.Floor(d.Pos.Z)))
        {
            d.Vel.Y = 0;
        }
        else
        {
            d.Pos.Y = ny;
        }
        if (d.Pos.Y < -30) d.Pos.Y = -30; // rests in the void until despawn
    }
}
