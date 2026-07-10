using System.Numerics;

namespace VoxelMiner.Entities;

using VoxelMiner.Core;
using VoxelMiner.World;
using static VoxelMiner.Core.Constants;

/// Spawns, updates, and despawns animals around the player; handles punches.
public sealed class AnimalManager
{
    const int MaxAnimals = 8;
    const float SpawnInterval = 2f;
    const float SpawnMinDist = 16f, SpawnRange = 20f;
    const float DespawnDist = 90f;
    const float PunchReach = 3.2f;
    const float PunchAimDot = 0.93f;

    readonly GameWorld _world;
    public readonly List<Animal> Animals = new();
    public event Action<Animal> AnimalPunched;

    float _spawnTimer;
    static readonly Random Rng = new();

    public AnimalManager(GameWorld world) => _world = world;

    public void Update(float dt, Vector3 playerPos)
    {
        _spawnTimer -= dt;
        if (_spawnTimer <= 0)
        {
            _spawnTimer = SpawnInterval;
            if (Animals.Count < MaxAnimals) TrySpawn(playerPos);
        }
        for (int i = Animals.Count - 1; i >= 0; i--)
        {
            var a = Animals[i];
            a.Update(dt, _world);
            if (a.Dead || Vector3.Distance(a.Pos, playerPos) > DespawnDist) Animals.RemoveAt(i);
        }
    }

    /// Returns the punched animal (it flees), or null.
    public Animal Punch(Vector3 eye, Vector3 viewDir, Vector3 playerPos, float Damage)
    {
        foreach (var a in Animals)
        {
            var to = a.Pos + new Vector3(0, 0.4f, 0) - eye;
            float dist = to.Length();
            if (dist < PunchReach && Vector3.Dot(Vector3.Normalize(to), viewDir) > PunchAimDot)
            {
                a.Wander(_world, playerPos); // flee!
                a.Damage(Damage);
                AnimalPunched?.Invoke(a);

                if (a.Health <= 0)
                {
                    a.Dead = true;
                    if (a.DropItems != null && a.DropItems.Count > 0)
                        Gameplay.Drops.DropManager.Instance.Spawn(a.Pos.X, a.Pos.Y, a.Pos.Z, a.DropItems);
                }

                return a;
            }
        }
        return null;
    }

    void TrySpawn(Vector3 playerPos)
    {
        for (int tries = 0; tries < 5; tries++)
        {
            double angle = Rng.NextDouble() * Math.PI * 2;
            double dist = SpawnMinDist + Rng.NextDouble() * SpawnRange;
            int gx = (int)Math.Floor(playerPos.X + Math.Cos(angle) * dist);
            int gz = (int)Math.Floor(playerPos.Z + Math.Sin(angle) * dist);
            if (!_world.HasChunk((int)Math.Floor(gx / (double)ChunkSize), (int)Math.Floor(gz / (double)ChunkSize))) continue;
            for (int y = WorldHeight - 2; y > 2; y--)
            {
                if (!_world.IsSolidAt(gx, y - 1, gz)) continue;
                if (_world.GetBlock(gx, y - 1, gz) == BlockId.Grass && Pathfinding.IsStandable(_world, gx, y, gz))
                {
                    var type = (AnimalType)Rng.Next(3);
                    Animals.Add(new Animal(type, gx + 0.5f, y, gz + 0.5f));
                    return;
                }
                break;
            }
        }
    }
}
