using System.Numerics;

namespace VoxelMiner.Gameplay;

using VoxelMiner.Core;
using VoxelMiner.World;

/// A rideable boat: floats on the water surface, driven with the move keys
/// while ridden (W/S accelerate and reverse, A/D steer), punched to pick
/// back up as an item. Yaw follows the animal convention: +X is the bow,
/// facing direction = (cos Yaw, 0, -sin Yaw).
public sealed class Boat
{
    public Vector3 Pos; // hull center at the waterline
    public float Yaw;
    public float Speed; // signed, along Yaw
    public float VelY;
}

public sealed class BoatManager
{
    const float Reach = 4.5f;
    const float PickRadius = 0.8f;
    const float HalfWidth = 0.55f;
    const float MaxSpeed = 7f;
    const float MaxReverse = -2.5f;
    const float Accel = 5.5f;
    const float CoastDrag = 0.25f;  // per-second speed multiplier when not powered
    const float TurnRate = 1.7f;    // rad/s
    const float BuoyancyRate = 4f;  // m/s the hull rises/settles toward the surface
    const float Gravity = 22f;
    const float WaterSurfaceHeight = 14f / 16f; // must match ChunkMesher's surface
    const float EyeAboveHull = 1.2f;

    readonly GameWorld _world;
    public readonly List<Boat> All = new();
    public Boat Ridden { get; private set; }

    /// Creative: placing doesn't consume the item; punching removes the boat
    /// without returning one (like breaking anything in creative).
    public GameMode Mode = GameMode.Survival;

    public event Action<string> Toast;
    public event Action Placed;
    public event Action PickedUp;
    public event Action<Vector3, int> Dropped; // position, item id (survival breaks)

    public BoatManager(GameWorld world) => _world = world;

    // ------------------------------------------------------------- interaction

    /// Handles a right click: mount a boat under the crosshair, or place a
    /// held boat on the water the player is pointing at. Returns true when
    /// the click was consumed (so it shouldn't fall through to block placing).
    public bool HandleRightClick(Player player, Inventory inventory)
    {
        if (Mode == GameMode.Spectator) return false;

        if (Ridden != null) return false;
        if (PickBoat(player) is { } boat)
        {
            Mount(boat, player);
            return true;
        }
        if (inventory.SelectedItem?.Id != ItemId.Boat) return false;

        if (FindWaterAlongView(player) is not { } spot)
        {
            Toast?.Invoke("Point at water to place the boat!");
            return true;
        }
        if (Mode == GameMode.Survival) inventory.ConsumeSelected();
        // face the way the player is looking (animal yaw convention)
        float dirX = -MathF.Sin(player.Yaw), dirZ = -MathF.Cos(player.Yaw);
        All.Add(new Boat { Pos = spot, Yaw = MathF.Atan2(-dirZ, dirX) });
        Placed?.Invoke();
        Toast?.Invoke("Right-click the boat to get in");
        return true;
    }

    /// Punch a boat under the crosshair to pick it back up. Returns true if
    /// a boat was hit (the click shouldn't also start mining).
    public bool TryPunch(Player player, Inventory inventory)
    {
        if (Mode == GameMode.Spectator) return false;

        if (PickBoat(player) is not { } boat) return false;
        All.Remove(boat);
        // survival: the boat breaks into a drop entity to walk over
        if (Mode == GameMode.Survival)
            Dropped?.Invoke(boat.Pos + new Vector3(0, 0.3f, 0), ItemId.Boat);
        PickedUp?.Invoke();
        return true;
    }

    void Mount(Boat boat, Player player)
    {
        Ridden = boat;
        player.Vel = Vector3.Zero;
        Toast?.Invoke("W/S to row, A/D to steer, Space to hop out");
    }

    public void Dismount(Player player)
    {
        if (Ridden == null) return;
        player.Pos = Ridden.Pos + new Vector3(0, 0.6f, 0);
        player.Vel = Vector3.Zero;
        Ridden = null;
    }

    /// Nearest un-ridden boat whose hull the view ray passes close to.
    Boat PickBoat(Player player)
    {
        Vector3 eye = player.EyePos, dir = player.ViewDir;
        Boat best = null;
        float bestT = float.MaxValue;
        foreach (var boat in All)
        {
            if (boat == Ridden) continue;
            var v = boat.Pos + new Vector3(0, 0.1f, 0) - eye;
            float t = Vector3.Dot(v, dir);
            if (t < 0 || t > Reach || t > bestT) continue;
            if ((v - dir * t).Length() > PickRadius) continue;
            best = boat;
            bestT = t;
        }
        return best;
    }

    /// Steps along the view ray for the first water cell (the block raycast
    /// deliberately passes through water). Solid ground blocks the ray.
    Vector3? FindWaterAlongView(Player player)
    {
        Vector3 eye = player.EyePos, dir = player.ViewDir;
        for (float t = 0.5f; t <= Reach + 2f; t += 0.2f)
        {
            var p = eye + dir * t;
            int x = (int)MathF.Floor(p.X), y = (int)MathF.Floor(p.Y), z = (int)MathF.Floor(p.Z);
            int id = _world.GetBlock(x, y, z);
            if (id == BlockId.Water && WaterSurface(x, y, z) is { } surface)
                return new Vector3(x + 0.5f, surface, z + 0.5f);
            if (BlockRegistry.IsSolid(id)) return null;
        }
        return null;
    }

    // ------------------------------------------------------------- simulation

    /// Applies rider input: W/S accelerate along the bow, A/D steer.
    public void UpdateRidden(float dt, float forward, float strafe)
    {
        var b = Ridden;
        if (b == null) return;
        b.Yaw -= strafe * TurnRate * dt;
        if (forward != 0)
            b.Speed = Math.Clamp(b.Speed + forward * Accel * dt, MaxReverse, MaxSpeed);
        else
            b.Speed *= MathF.Pow(CoastDrag, dt);
    }

    /// Physics for every boat: buoyancy, gravity over land, hull-vs-terrain
    /// collision. Seats the player on the ridden boat afterwards.
    public void Update(float dt, Player player)
    {
        foreach (var b in All)
        {
            if (b != Ridden) b.Speed *= MathF.Pow(CoastDrag, dt); // drift dies out

            int bx = (int)MathF.Floor(b.Pos.X), bz = (int)MathF.Floor(b.Pos.Z);
            if (WaterSurface(bx, (int)MathF.Floor(b.Pos.Y), bz) is { } surface)
            {
                b.VelY = 0;
                float d = surface - b.Pos.Y;
                b.Pos.Y += Math.Clamp(d, -BuoyancyRate * dt, BuoyancyRate * dt);
            }
            else
            {
                // beached or airborne: fall until resting on solid ground
                b.VelY = MathF.Max(b.VelY - Gravity * dt, -20f);
                float ny = b.Pos.Y + b.VelY * dt;
                int floorY = (int)MathF.Floor(ny - 0.05f);
                if (_world.IsSolidAt(bx, floorY, bz))
                {
                    b.Pos.Y = floorY + 1.05f;
                    b.VelY = 0;
                }
                else
                {
                    b.Pos.Y = ny;
                }
            }

            if (MathF.Abs(b.Speed) > 0.02f) MoveHorizontal(b, dt);
        }

        if (Ridden is { } r)
        {
            // seat the player: eye ends up EyeAboveHull over the waterline
            player.Pos = r.Pos + new Vector3(0, EyeAboveHull - Player.Eye, 0);
            player.Vel = Vector3.Zero;
        }
    }

    void MoveHorizontal(Boat b, float dt)
    {
        float dx = MathF.Cos(b.Yaw) * b.Speed * dt;
        float dz = -MathF.Sin(b.Yaw) * b.Speed * dt;
        b.Pos.X += dx;
        if (HullBlocked(b)) { b.Pos.X -= dx; b.Speed *= 0.5f; }
        b.Pos.Z += dz;
        if (HullBlocked(b)) { b.Pos.Z -= dz; b.Speed *= 0.5f; }
    }

    bool HullBlocked(Boat b)
    {
        int y = (int)MathF.Floor(b.Pos.Y + 0.2f);
        for (int cx = -1; cx <= 1; cx += 2)
            for (int cz = -1; cz <= 1; cz += 2)
                if (_world.IsSolidAt(
                        (int)MathF.Floor(b.Pos.X + cx * HalfWidth), y,
                        (int)MathF.Floor(b.Pos.Z + cz * HalfWidth)))
                    return true;
        return false;
    }

    /// Y of the water surface at this column, if there's water within one
    /// cell of the given height (rides up a submerged column to its top).
    float? WaterSurface(int x, int aroundY, int z)
    {
        int cy = int.MinValue;
        for (int y = aroundY + 1; y >= aroundY - 1; y--)
            if (_world.GetBlock(x, y, z) == BlockId.Water) { cy = y; break; }
        if (cy == int.MinValue) return null;
        while (_world.GetBlock(x, cy + 1, z) == BlockId.Water) cy++;
        return cy + WaterSurfaceHeight;
    }

    /// Direct spawn for tests and scripted scenes.
    public Boat Spawn(float x, float y, float z, float yaw = 0)
    {
        var boat = new Boat { Pos = new Vector3(x, y, z), Yaw = yaw };
        All.Add(boat);
        return boat;
    }
}
