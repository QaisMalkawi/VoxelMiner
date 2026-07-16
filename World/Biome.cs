namespace VoxelMiner.World;

/// Climate/terrain classification used by TerrainGenerator to pick surface
/// blocks, tree and vegetation density, and where villages may appear.
public enum Biome
{
    Ocean, River, Beach, Plains, Forest, Desert, Mountains,
    Savanna, Swamp, Jungle, Tundra, SnowyMountains,
    DeepOcean, FrozenOcean, Taiga, SnowyTaiga,
    BirchForest, DarkForest, FlowerForest, Badlands, Mushroom,
}
