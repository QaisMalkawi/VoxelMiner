using System.Numerics;

namespace VoxelMiner.Entities;

using VoxelMiner.Core;
using VoxelMiner.Gameplay;
using VoxelMiner.World;

public enum AnimalType { Pig, Sheep, Chicken }
public enum PartKind { Body, Head, Leg }

/// One textured box of an animal's body. Legs/head rotate around PivotPos.
/// SideTile covers 5 faces; FrontTile is the +X face (where the head looks).
public sealed record AnimalPart(PartKind Kind, Vector3 Size, Vector3 LocalPos, Vector3 PivotPos, int SideTile, int FrontTile);

public sealed record AnimalDef(float Speed, float Scale, AnimalPart[] Parts);

/// Tile indices refer to assets/textures/animals.png (see tools/generate-textures.mjs).
public static class AnimalDefs
{
    static AnimalPart[] BuildParts(Vector3 body, float bodyY, Vector3 head, float legH, float legW,
                                    int bodyTile, int headTile, int faceTile, int legTile, AnimalPart extra = null)
    {
        var parts = new List<AnimalPart>
        {
            new(PartKind.Body, body, Vector3.Zero, new Vector3(0, bodyY, 0), bodyTile, bodyTile),
            new(PartKind.Head, head, Vector3.Zero,
                new Vector3(body.X / 2 + head.X / 2 - 0.08f, bodyY + body.Y * 0.3f, 0), headTile, faceTile),
        };
        if (extra != null) parts.Add(extra);
        foreach (var (fx, fz) in new[] { (1, 1), (1, -1), (-1, 1), (-1, -1) })
        {
            parts.Add(new AnimalPart(PartKind.Leg, new Vector3(legW, legH, legW), Vector3.Zero,
                new Vector3(fx * (body.X / 2 - 0.12f), bodyY - body.Y / 2 - legH / 2 + 0.02f, fz * (body.Z / 2 - 0.08f)),
                legTile, legTile));
        }
        return parts.ToArray();
    }

    public static readonly Dictionary<AnimalType, AnimalDef> Defs = new()
    {
        [AnimalType.Pig] = new(1.7f, 1f, BuildParts(
            new Vector3(0.85f, 0.5f, 0.55f), 0.5f, new Vector3(0.4f, 0.4f, 0.4f), 0.28f, 0.14f,
            bodyTile: 0, headTile: 0, faceTile: 1, legTile: 3,
            // snout, attached to the head pivot
            extra: new AnimalPart(PartKind.Head, new Vector3(0.08f, 0.14f, 0.18f), new Vector3(0.24f, -0.06f, 0),
                new Vector3(0.85f / 2 + 0.2f - 0.08f, 0.5f + 0.5f * 0.3f, 0), 2, 2))),
        [AnimalType.Sheep] = new(1.5f, 1f, BuildParts(
            new Vector3(0.9f, 0.6f, 0.6f), 0.58f, new Vector3(0.32f, 0.34f, 0.3f), 0.3f, 0.14f,
            bodyTile: 4, headTile: 5, faceTile: 6, legTile: 5)),
        [AnimalType.Chicken] = new(2.1f, 0.9f, BuildParts(
            new Vector3(0.45f, 0.42f, 0.36f), 0.34f, new Vector3(0.2f, 0.3f, 0.2f), 0.2f, 0.08f,
            bodyTile: 7, headTile: 7, faceTile: 8, legTile: 10,
            // beak
            extra: new AnimalPart(PartKind.Head, new Vector3(0.12f, 0.08f, 0.1f), new Vector3(0.15f, 0, 0),
                new Vector3(0.45f / 2 + 0.1f - 0.08f, 0.34f + 0.42f * 0.3f, 0), 9, 9))),
    };
}

/// A wandering passive creature: idle/graze/walk states, A* path following.
public sealed class Animal : IDamagableEntity
{
    const float WanderRange = 20f;
    const float FleeSpeedMult = 1.6f;
    const float TurnRate = 8f;

    public readonly AnimalType Type;
    public readonly AnimalDef Def;
    public Vector3 Pos;
    public float Yaw, TargetYaw;
    public float Anim;
    public float HeadDip;   // head rotation for grazing
    public float LegSwing;  // leg rotation while walking
    public bool Dead;

    List<(int X, int Y, int Z)> _path;
    int _pathIndex;
    bool _walking, _fleeing;
    float _timer;
    static readonly Random Rng = new();

    public float MaxHealth { get; init; } = 10;
    public float Health { get; set; }
    public ItemStack DropItems { get; set; }

    public Animal(AnimalType type, float x, float y, float z)
    {
        Type = type;
        Def = AnimalDefs.Defs[type];
        Pos = new Vector3(x, y, z);
        Yaw = TargetYaw = (float)(Rng.NextDouble() * Math.PI * 2);
        _timer = 1 + (float)Rng.NextDouble() * 3;
        Anim = (float)Rng.NextDouble() * 10;

        Health = MaxHealth;

        switch (type)
        {
            case AnimalType.Pig:
                DropItems = new ItemStack()
                {
                    Id = ItemId.RawPigMeat,
                    Count = Random.Shared.Next(1, 4)
                };
                break;
            case AnimalType.Sheep:
                DropItems = new ItemStack()
                {
                    Id = ItemId.RawSheepMeat,
                    Count = Random.Shared.Next(1, 3)
                };
                break;
            case AnimalType.Chicken:
                DropItems = new ItemStack()
                {
                    Id = ItemId.RawChickenMeat,
                    Count = Random.Shared.Next(0, 2)
                };
                break;
            default:
                break;
        }

    }

    /// fleeFrom: position to run away from, or null for a random stroll.
    public void Wander(GameWorld world, Vector3? fleeFrom = null)
    {
        int bx = (int)MathF.Floor(Pos.X), by = (int)MathF.Round(Pos.Y), bz = (int)MathF.Floor(Pos.Z);
        for (int i = 0; i < 8; i++)
        {
            int dx, dz;
            if (fleeFrom is { } f)
            {
                float ax = Pos.X - f.X, az = Pos.Z - f.Z;
                float al = MathF.Max(MathF.Sqrt(ax * ax + az * az), 1e-3f);
                dx = (int)MathF.Round(ax / al * (7 + (float)Rng.NextDouble() * 5) + ((float)Rng.NextDouble() - .5f) * 5);
                dz = (int)MathF.Round(az / al * (7 + (float)Rng.NextDouble() * 5) + ((float)Rng.NextDouble() - .5f) * 5);
            }
            else
            {
                dx = (int)MathF.Floor(((float)Rng.NextDouble() - .5f) * WanderRange);
                dz = (int)MathF.Floor(((float)Rng.NextDouble() - .5f) * WanderRange);
            }
            int tx = bx + dx, tz = bz + dz;
            for (int ty = by + 4; ty >= by - 6; ty--)
            {
                if (!Pathfinding.IsStandable(world, tx, ty, tz)) continue;
                var path = Pathfinding.FindPath(world, bx, by, bz, tx, ty, tz);
                if (path is { Count: > 1 })
                {
                    _path = path;
                    _pathIndex = 1;
                    _walking = true;
                    _fleeing = fleeFrom != null;
                    return;
                }
                break;
            }
        }
        _walking = false;
        _timer = 2 + (float)Rng.NextDouble() * 3;
    }

    public void Update(float dt, GameWorld world)
    {
        Anim += dt;
        if (!_walking) UpdateIdle(dt, world);
        else UpdateWalk(dt, world);

        float d = TargetYaw - Yaw;
        while (d > MathF.PI) d -= 2 * MathF.PI;
        while (d < -MathF.PI) d += 2 * MathF.PI;
        Yaw += d * MathF.Min(1, dt * TurnRate);
    }

    void UpdateIdle(float dt, GameWorld world)
    {
        float? gy = Pathfinding.GroundHeightBelow(world, Pos);
        if (gy == null)
        {
            Pos.Y -= 8 * dt;
            if (Pos.Y < -5) Dead = true;
        }
        else if (Pos.Y > gy + 0.01f)
        {
            Pos.Y = MathF.Max(gy.Value, Pos.Y - 8 * dt);
        }
        HeadDip = MathF.Sin(Anim * 0.7f) < -0.55f ? -0.55f : 0; // graze dip
        LegSwing = 0;
        _timer -= dt;
        if (_timer <= 0) Wander(world);
    }

    void UpdateWalk(float dt, GameWorld world)
    {
        var node = _path[_pathIndex];
        if (!Pathfinding.IsStandable(world, node.X, node.Y, node.Z))
        {
            // route dug out from under us — rethink
            _path = null;
            _walking = false;
            _timer = 0.4f;
            return;
        }
        float dx = node.X + 0.5f - Pos.X, dz = node.Z + 0.5f - Pos.Z;
        float dist = MathF.Sqrt(dx * dx + dz * dz);
        float speed = Def.Speed * (_fleeing ? FleeSpeedMult : 1);
        if (dist > 1e-3f)
        {
            float step = MathF.Min(speed * dt, dist);
            Pos.X += dx / dist * step;
            Pos.Z += dz / dist * step;
            TargetYaw = MathF.Atan2(-dz, dx);
        }
        if (Pos.Y < node.Y) Pos.Y = MathF.Min(node.Y, Pos.Y + 7 * dt);        // hop up
        else if (Pos.Y > node.Y) Pos.Y = MathF.Max(node.Y, Pos.Y - 10 * dt);  // drop down
        HeadDip = 0;
        LegSwing = MathF.Sin(Anim * 9) * 0.55f;
        if (dist < 0.12f && MathF.Abs(Pos.Y - node.Y) < 0.05f)
        {
            _pathIndex++;
            if (_pathIndex >= _path.Count)
            {
                _path = null;
                _walking = false;
                _timer = 2 + (float)Rng.NextDouble() * 4;
                _fleeing = false;
            }
        }
    }

    public bool Damage(float Damage)
    {
        Health -= Damage;
        return Health <= 0;
    }
}
