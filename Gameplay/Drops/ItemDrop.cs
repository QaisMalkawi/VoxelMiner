using System.Numerics;

namespace VoxelMiner.Gameplay.Drops;

/// A dropped item lying in the world, waiting to be picked up.
public sealed class ItemDrop
{
    public Vector3 Pos, Vel;
    public int Id;
    public int Count = 1;
    public float Age;
    public float RetryTimer; // backoff while the inventory is full
}
