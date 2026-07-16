using System.Numerics;

namespace VoxelMiner.Gameplay;

using VoxelMiner.Core;
using VoxelMiner.World;
using static VoxelMiner.Core.Constants;

public struct MoveInput
{
    public float Forward, Strafe;
    public bool Jump, Sprint, Descend; // Descend: Shift while flying (creative)
    public bool Sneak;                 // Shift on the ground: crouch/sneak
}

/// First-person player: position, look angles, AABB physics against the
/// voxel world, plus survival state — health, breath, fall damage — and
/// creative flight. Damage is applied inside Step (survival mode only);
/// the game loop polls LastDamage/JustDied for effects and the death screen.
public sealed class Player
{
    const float Gravity = 26f;
    const float MaxFallSpeed = 38f;
    const float JumpSpeed = 8.6f;
    const float WalkSpeed = 5.2f;
    const float SprintSpeed = 8.0f;
    const float SneakSpeed = 1.6f;   // ~30% of walk, Minecraft's sneak ratio
    const float LookSensitivity = 0.0024f;
    const float MaxPitch = 1.55f;
    const float RespawnY = -20f;
    const float Eps = 1e-3f;

    public const float Width = 0.3f; // half-extent
    public const float Height = 1.8f;
    public const float Eye = 1.62f;
    const float CrouchEye = 1.32f;   // camera height while sneaking
    const float EyeLerpRate = 16f;   // crouch/stand camera transition speed

    const float SwimUpSpeed = 4.5f;
    const float SwimUpAccel = 30f;
    const float SinkSpeed = 3.0f;
    const float WaterDrag = 10f;
    const float WaterSpeedFactor = 0.55f;

    const float FlySpeed = 10.8f;
    const float FlyVerticalSpeed = 7.5f;
    const float SpectatorMultiplier = 3f;

    public const float MaxHealth = 20f;
    public const float MaxHunger = 20f;
    public const float MaxAir = 15f;             // seconds of breath
    const float SafeFallBlocks = 3f;             // falls beyond this hurt
    const float DrownDamage = 2f;                // per DrownInterval once out of air
    const float DrownInterval = 1f;

    // Hunger follows Minecraft's FoodStats: actions add exhaustion; every
    // 4 exhaustion drains 1 saturation, or 1 food once saturation is empty.
    const float DefaultSaturation = 5f;
    const float ExhaustionPerPoint = 4f;
    const float SprintMinHunger = 6f;            // can't sprint at 3 drumsticks or less
    const float RegenMinHunger = 18f;
    const float RegenInterval = 4f;              // food >= 18: +1 HP per interval
    const float FastRegenInterval = 0.5f;        // food full with saturation left
    const float StarveInterval = 4f;             // food 0: 1 damage per interval
    const float StarveFloor = 1f;                // starvation can't kill (Normal difficulty)
    const float RegenExhaustion = 6f;            // exhaustion cost per HP healed
    const float SprintExhaustion = 0.1f;         // per meter sprinted
    const float SwimExhaustion = 0.01f;          // per meter swum
    const float JumpExhaustion = 0.05f;
    const float SprintJumpExhaustion = 0.2f;

    public Vector3 Pos = new(8.5f, 50f, 8.5f);
    public Vector3 Vel;
    public float Yaw, Pitch;
    public bool OnGround;
    public bool InWater { get; private set; }
    public bool Flying;                          // creative only; forced off in survival
    public bool Sneaking { get; private set; }

    public float Health { get; private set; } = MaxHealth;
    public float Hunger { get; private set; } = MaxHunger;
    public float Saturation { get; private set; } = DefaultSaturation;
    public float Exhaustion { get; private set; }
    public float Air { get; private set; } = MaxAir;
    public float FallDistance { get; private set; }

    /// Set for one Step call when something hurt the player / health hit 0.
    public float LastDamage { get; private set; }
    public bool JustDied { get; private set; }

    float _drownTimer, _foodTimer;
    float _eyeHeight = Eye;

    public Vector3 EyePos => Pos + new Vector3(0, _eyeHeight, 0);

    public Vector3 ViewDir => new(
        -MathF.Cos(Pitch) * MathF.Sin(Yaw),
        MathF.Sin(Pitch),
        -MathF.Cos(Pitch) * MathF.Cos(Yaw));

    public void Look(float dx, float dy)
    {
        Yaw -= dx * LookSensitivity;
        Pitch = Math.Clamp(Pitch - dy * LookSensitivity, -MaxPitch, MaxPitch);
    }

    public void Spawn(GameWorld world)
    {
        // a vetted column: dry land, gentle slope, no tree above it
        var (sx, sz) = world.FindSpawn();
        world.EnsureChunk((int)MathF.Floor(sx / (float)ChunkSize), (int)MathF.Floor(sz / (float)ChunkSize));
        // place on the actual surface — structures may have altered the column
        for (int y = WorldHeight - 1; y > 0; y--)
        {
            if (world.IsSolidAt(sx, y, sz))
            {
                Pos = new Vector3(sx + 0.5f, y + 1.01f, sz + 0.5f);
                break;
            }
        }
        Vel = Vector3.Zero;
        Health = MaxHealth;
        Hunger = MaxHunger;
        Saturation = DefaultSaturation;
        Exhaustion = 0;
        Air = MaxAir;
        FallDistance = 0;
        _foodTimer = 0;
    }

    /// Reinstates a saved player verbatim (world loading).
    public void Restore(Vector3 pos, float yaw, float pitch, float health, float hunger,
        float saturation, float exhaustion, float air, bool flying)
    {
        Pos = pos;
        Yaw = yaw;
        Pitch = pitch;
        Health = health;
        Hunger = hunger;
        Saturation = saturation;
        Exhaustion = exhaustion;
        Air = air;
        Flying = flying;
        Vel = Vector3.Zero;
        FallDistance = 0;
    }

    bool WaterAt(GameWorld world, float yOffset) =>
        world.GetBlock((int)MathF.Floor(Pos.X), (int)MathF.Floor(Pos.Y + yOffset), (int)MathF.Floor(Pos.Z)) == BlockId.Water;

    public void Step(float dt, MoveInput input, GameWorld world, GameMode mode)
    {
        bool justJumped = false;

        LastDamage = 0;
        JustDied = false;
        bool wasOnGround = OnGround;
        if (mode == GameMode.Survival) Flying = false;

        // Minecraft blocks sprinting at 3 drumsticks or less
        Sneaking = input.Sneak && !Flying;
        bool sprinting = input.Sprint && !Sneaking && (mode != GameMode.Survival || Hunger > SprintMinHunger);

        // camera dips toward crouch height and eases back up on standing
        float targetEye = Sneaking ? CrouchEye : Eye;
        _eyeHeight += (targetEye - _eyeHeight) * MathF.Min(1f, dt * EyeLerpRate);

        InWater = WaterAt(world, 0.1f) || WaterAt(world, 1.0f);
        float speed = Flying ? FlySpeed * (mode == GameMode.Spectator ? SpectatorMultiplier : 1)
                    : (sprinting ? SprintSpeed : Sneaking ? SneakSpeed : WalkSpeed) * (InWater ? WaterSpeedFactor : 1f);
        float len = MathF.Max(MathF.Sqrt(input.Forward * input.Forward + input.Strafe * input.Strafe), 1f);
        float sy = MathF.Sin(Yaw), cy = MathF.Cos(Yaw);
        Vel.X = (input.Forward / len * -sy + input.Strafe / len * cy) * speed;
        Vel.Z = (input.Forward / len * -cy + input.Strafe / len * -sy) * speed;

        if (Flying)
        {
            FallDistance = 0;
            Vel.Y = (input.Jump ? FlyVerticalSpeed : 0) + (input.Descend ? -FlyVerticalSpeed : 0);
        }
        else if (input.Jump && OnGround)
        {
            Vel.Y = JumpSpeed; // standing in shallow water still allows a full hop out
            OnGround = false;
            justJumped = true;
        }
        else if (InWater)
        {
            FallDistance = 0;
            Vel.Y = input.Jump
                ? MathF.Min(Vel.Y + SwimUpAccel * dt, SwimUpSpeed)
                : MathF.Max(Vel.Y - WaterDrag * dt, -SinkSpeed);
        }
        else
        {
            Vel.Y = MathF.Max(Vel.Y - Gravity * dt, -MaxFallSpeed);
        }

        float preX = Pos.X, preZ = Pos.Z;
        ResolveHorizontal(world, dt, mode, axisX: true);
        ResolveHorizontal(world, dt, mode, axisX: false);
        if (!Flying && !InWater && Vel.Y < 0) FallDistance += -Vel.Y * dt;
        OnGround = false;
        ResolveVertical(world, dt, mode);

        if (OnGround)
        {
            // fall damage only on the landing transition; the distance resets
            // every grounded frame so the tiny per-frame gravity velocity
            // (applied then zeroed by the floor) can't accumulate into
            // phantom fall damage while standing or walking
            if (!wasOnGround && mode == GameMode.Survival && FallDistance > SafeFallBlocks + 0.5f)
                Damage(MathF.Floor(FallDistance - SafeFallBlocks));
            FallDistance = 0;
        }

        if (mode == GameMode.Survival)
        {
            // movement exhaustion, Minecraft rates: walking is free
            if (justJumped) AddExhaustion(sprinting ? SprintJumpExhaustion : JumpExhaustion);
            float moved = MathF.Sqrt((Pos.X - preX) * (Pos.X - preX) + (Pos.Z - preZ) * (Pos.Z - preZ));
            if (InWater) AddExhaustion(SwimExhaustion * moved);
            else if (sprinting && OnGround) AddExhaustion(SprintExhaustion * moved);

            UpdateBreathAndFood(dt, world);
        }
        else
        {
            Air = MaxAir;
            Hunger = MaxHunger;
            Saturation = DefaultSaturation;
            Health = MaxHealth;
        }
        if (Pos.Y < RespawnY) Spawn(world);
    }

    /// Actions cost exhaustion; every 4 points drains 1 saturation, then
    /// 1 food once saturation is gone (Minecraft's exhaustion mechanic).
    public void AddExhaustion(float amount)
    {
        Exhaustion += amount;
        while (Exhaustion >= ExhaustionPerPoint)
        {
            Exhaustion -= ExhaustionPerPoint;
            if (Saturation > 0) Saturation = MathF.Max(Saturation - 1, 0);
            else Hunger = MathF.Max(Hunger - 1, 0);
        }
    }

    public bool CanEat => Hunger < MaxHunger;

    public void Eat(FoodSpec food)
    {
        Hunger = MathF.Min(Hunger + food.Points, MaxHunger);
        Saturation = MathF.Min(Saturation + food.Saturation, Hunger); // saturation never exceeds food
    }

    void UpdateBreathAndFood(float dt, GameWorld world)
    {
        #region Calculate Air
        var eye = EyePos;
        bool submerged = world.GetBlock(
            (int)MathF.Floor(eye.X), (int)MathF.Floor(eye.Y), (int)MathF.Floor(eye.Z)) == BlockId.Water;
        if (submerged)
        {
            Air = MathF.Max(Air - dt, 0);
            if (Air <= 0)
            {
                _drownTimer += dt;
                if (_drownTimer >= DrownInterval)
                {
                    _drownTimer = 0;
                    Damage(DrownDamage);
                }
            }
        }
        else
        {
            Air = MathF.Min(Air + dt * 3f, MaxAir);
            _drownTimer = 0;
        }
        #endregion

        #region Food: regeneration & starvation (Minecraft FoodStats parity)
        if (Hunger >= MaxHunger && Saturation > 0 && Health < MaxHealth)
        {
            // saturated fast regen: heal every half second, paid for in
            // exhaustion, which in turn burns the saturation down
            _foodTimer += dt;
            if (_foodTimer >= FastRegenInterval)
            {
                _foodTimer = 0;
                float f = MathF.Min(Saturation, RegenExhaustion);
                Health = MathF.Min(Health + f / RegenExhaustion, MaxHealth);
                AddExhaustion(f);
            }
        }
        else if (Hunger >= RegenMinHunger && Health < MaxHealth)
        {
            _foodTimer += dt;
            if (_foodTimer >= RegenInterval)
            {
                _foodTimer = 0;
                Health = MathF.Min(Health + 1, MaxHealth);
                AddExhaustion(RegenExhaustion);
            }
        }
        else if (Hunger <= 0)
        {
            _foodTimer += dt;
            if (_foodTimer >= StarveInterval)
            {
                _foodTimer = 0;
                if (Health > StarveFloor) Damage(1);
            }
        }
        else
        {
            _foodTimer = 0;
        }
        #endregion
    }

    void Damage(float amount)
    {
        if (amount <= 0) return;
        Health -= amount;
        LastDamage += amount;
        if (Health <= 0)
        {
            Health = 0;
            JustDied = true;
        }
    }

    public bool IntersectsBlock(int x, int y, int z) =>
        x + 1 > Pos.X - Width && x < Pos.X + Width &&
        y + 1 > Pos.Y && y < Pos.Y + Height &&
        z + 1 > Pos.Z - Width && z < Pos.Z + Width;

    // --------------------------------------------------------- collision
    // The player collides with per-block collision boxes (slabs, stairs,
    // door panels...), not whole cells, and auto-steps up ledges of up to
    // StepHeight while on the ground so stairs and slabs can be walked up.

    const float StepHeight = 0.55f;

    /// Walks every collision box overlapping the player AABB at p; the
    /// visitor receives world-space box bounds. Returns true if any was hit.
    static bool VisitBoxes(GameWorld world, Vector3 p,
        Action<float, float, float, float, float, float> visit = null)
    {
        float bx0 = p.X - Width, bx1 = p.X + Width;
        float by0 = p.Y, by1 = p.Y + Height;
        float bz0 = p.Z - Width, bz1 = p.Z + Width;
        int cx0 = (int)MathF.Floor(bx0), cx1 = (int)MathF.Floor(bx1);
        int cy0 = Math.Max(0, (int)MathF.Floor(by0)), cy1 = (int)MathF.Floor(by1);
        int cz0 = (int)MathF.Floor(bz0), cz1 = (int)MathF.Floor(bz1);
        bool any = false;
        for (int x = cx0; x <= cx1; x++)
            for (int y = cy0; y <= cy1; y++)
                for (int z = cz0; z <= cz1; z++)
                    foreach (var b in world.CollisionBoxesAt(x, y, z))
                    {
                        float wx0 = x + b.X0, wx1 = x + b.X1;
                        float wy0 = y + b.Y0, wy1 = y + b.Y1;
                        float wz0 = z + b.Z0, wz1 = z + b.Z1;
                        if (wx0 >= bx1 || wx1 <= bx0 || wy0 >= by1 || wy1 <= by0 || wz0 >= bz1 || wz1 <= bz0)
                            continue;
                        any = true;
                        if (visit == null) return true; // existence check only
                        visit(wx0, wy0, wz0, wx1, wy1, wz1);
                    }
        return any;
    }

    bool Collides(GameWorld world, Vector3 p, GameMode mode) =>
        mode != GameMode.Spectator && VisitBoxes(world, p);

    const float SneakEdgeStep = 0.05f; // granularity of the sneak ledge clamp

    void ResolveHorizontal(GameWorld world, float dt, GameMode mode, bool axisX)
    {
        float vel = axisX ? Vel.X : Vel.Z;
        float disp = vel * dt;

        // Sneaking on the ground refuses to walk off ledges (Minecraft edge
        // protection): shrink the displacement until something — ground within
        // step reach below, or a wall we'd bump into anyway — still overlaps
        // the destination AABB dropped by StepHeight.
        if (disp != 0 && Sneaking && OnGround && !InWater && !Flying && mode != GameMode.Spectator)
        {
            while (disp != 0 && !VisitBoxes(world, new Vector3(
                Pos.X + (axisX ? disp : 0), Pos.Y - StepHeight, Pos.Z + (axisX ? 0 : disp))))
            {
                disp = MathF.Abs(disp) <= SneakEdgeStep ? 0 : disp - MathF.Sign(disp) * SneakEdgeStep;
            }
        }

        if (axisX) Pos.X += disp; else Pos.Z += disp;
        if (mode == GameMode.Spectator || vel == 0) return;

        float limit = vel > 0 ? float.PositiveInfinity : float.NegativeInfinity;
        float maxTop = float.NegativeInfinity;
        bool hit = VisitBoxes(world, Pos, (x0, y0, z0, x1, y1, z1) =>
        {
            float near = axisX ? (vel > 0 ? x0 : x1) : (vel > 0 ? z0 : z1);
            limit = vel > 0 ? MathF.Min(limit, near) : MathF.Max(limit, near);
            maxTop = MathF.Max(maxTop, y1);
        });
        if (!hit) return;

        // step up low ledges (stair treads, slabs) instead of stopping
        if (OnGround && !InWater && !Flying && maxTop > Pos.Y && maxTop - Pos.Y <= StepHeight
            && !VisitBoxes(world, new Vector3(Pos.X, maxTop + Eps, Pos.Z)))
        {
            Pos.Y = maxTop + Eps;
            return;
        }

        float clamped = vel > 0 ? limit - Width - Eps : limit + Width + Eps;
        if (axisX) { Pos.X = clamped; Vel.X = 0; }
        else { Pos.Z = clamped; Vel.Z = 0; }
    }

    void ResolveVertical(GameWorld world, float dt, GameMode mode)
    {
        Pos.Y += Vel.Y * dt;
        if (mode == GameMode.Spectator) return;

        float floorTop = float.NegativeInfinity, ceiling = float.PositiveInfinity;
        bool hit = VisitBoxes(world, Pos, (x0, y0, z0, x1, y1, z1) =>
        {
            floorTop = MathF.Max(floorTop, y1);
            ceiling = MathF.Min(ceiling, y0);
        });
        if (!hit) return;

        if (Vel.Y < 0)
        {
            Pos.Y = floorTop;
            OnGround = true;
        }
        else
        {
            Pos.Y = ceiling - Height - Eps;
        }
        Vel.Y = 0;
    }
}
