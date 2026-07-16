namespace VoxelMiner.UI;

using VoxelMiner.World;
using static VoxelMiner.Core.Constants;

/// Shared column-to-color rules for the 2D maps (the in-game overlay and the
/// standalone --map viewer): biome base colors, height shading, water depth.
public static class MapColors
{
    /// Color of a column as the terrain generator sees it (no chunk data).
    public static (byte R, byte G, byte B) Generated(TerrainGenerator terrain, int gx, int gz, float dim = 1f)
    {
        var col = terrain.Sample(gx, gz, computeSteep: false);
        if (col.Biome is Biome.Ocean or Biome.DeepOcean or Biome.River)
            return Water(SeaLevel - col.Height, dim);
        if (col.Biome is Biome.FrozenOcean) // pale lavender ice sheets
            return ((byte)(174 * dim), (byte)(183 * dim), (byte)(227 * dim));
        return HeightShade(BiomeColor(col.Biome), col.Height, dim);
    }

    public static (byte, byte, byte) BiomeColor(Biome biome) => biome switch
    {
        Biome.Beach or Biome.Desert => ((byte)216, (byte)203, (byte)146),
        Biome.Forest => ((byte)70, (byte)128, (byte)48),
        Biome.BirchForest => ((byte)136, (byte)175, (byte)96),
        Biome.DarkForest => ((byte)64, (byte)88, (byte)30),
        Biome.FlowerForest => ((byte)150, (byte)190, (byte)100),
        Biome.Taiga => ((byte)22, (byte)106, (byte)89),
        Biome.SnowyTaiga => ((byte)168, (byte)200, (byte)188),
        Biome.Mountains => ((byte)131, (byte)131, (byte)136),
        Biome.SnowyMountains => ((byte)225, (byte)230, (byte)235),
        Biome.Tundra => ((byte)205, (byte)212, (byte)205),
        Biome.Savanna => ((byte)178, (byte)160, (byte)74),
        Biome.Badlands => ((byte)215, (byte)105, (byte)36),
        Biome.Swamp => ((byte)72, (byte)110, (byte)62),
        Biome.Jungle => ((byte)42, (byte)112, (byte)38),
        Biome.Mushroom => ((byte)200, (byte)70, (byte)190),
        _ => ((byte)98, (byte)165, (byte)61), // plains
    };

    public static (byte, byte, byte) Water(int depth, float dim = 1f)
    {
        float f = (1f - Math.Clamp(depth, 0, 20) * 0.034f) * dim;
        return ((byte)(63 * f), (byte)(118 * f), (byte)(228 * f));
    }

    public static (byte, byte, byte) HeightShade((byte R, byte G, byte B) c, int height, float dim)
    {
        float f = (0.62f + 0.38f * Math.Clamp(height / 52f, 0f, 1f)) * dim;
        return ((byte)Math.Min(c.R * f, 255), (byte)Math.Min(c.G * f, 255), (byte)Math.Min(c.B * f, 255));
    }
}
