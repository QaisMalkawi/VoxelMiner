namespace VoxelMiner.World;

using VoxelMiner.Core;
using static VoxelMiner.Core.Constants;

/// Procedural chunk generation: biome-driven heightmap terrain (oceans,
/// plains, ridged mountains), caves, depth-based ores, trees, vegetation,
/// and villages. Pure and deterministic — the same (cx, cz) always yields
/// the same chunk, and the same world column always yields the same biome
/// and height regardless of which chunk triggers its generation.
public sealed class TerrainGenerator
{
    // macro elevation (continents/oceans/mountains): very low frequency, one
    // band of noise decides whether a region is ocean, calm plains, or the
    // foot of a mountain range. ~600-block landmasses and seas, so oceans
    // read as real oceans instead of ponds.
    const double ContinentalFreq = 0.0016;
    const int OceanFloor = 9;
    const int PlainsBase = 44;
    const int MountainBonus = 48;

    // ridges layered on top only where the continental noise says "mountain":
    // two octaves of ridged noise, squared, give sharp connected crests
    // instead of a uniform dome
    const double RidgeFreq = 0.008;
    const int RidgeBonus = 42;

    // rolling hills across all land, fading out near shores and under ranges
    const double HillFreq = 0.008;
    const int HillBonus = 10;

    // small-scale rolling detail added everywhere
    const double DetailFreq1 = 0.013, DetailFreq2 = 0.06;
    const int DetailBonus1 = 8, DetailBonus2 = 3;

    // two-scale climate, Minecraft-style: continental bands (~2500 blocks)
    // set the broad hot/cold and wet/dry regions, and a regional band
    // (~300 blocks) breaks each region into a patchwork of sister biomes so
    // a short walk crosses several biomes instead of one endless gradient
    const double ClimateBaseFreq = 0.0004;
    const double ClimateVarFreq = 0.0032;

    // badlands: a rare mask gated to warm-dry base climate; where it's active
    // the terrain terraces into stepped mesa plateaus with terracotta strata
    const double BadlandsFreq = 0.0011;

    // mushroom islands: a very rare mask only expressed in open ocean, which
    // lifts the sea floor into a small mycelium island
    const double MushroomFreq = 0.0025;

    // rivers: narrow wandering channels along the 0.5 iso-line of one
    // low-frequency noise band. The channel follows the macro landscape
    // rather than carving to a fixed depth: its water surface sits a couple
    // of blocks under the local macro height (so rivers descend with the
    // terrain, and still cut gorges where ridge lines cross them), and the
    // bed blends smoothly deeper toward the centerline. Thresholds are in
    // noise units around the iso-line: inside Inner the bed is fully carved,
    // between Inner and Outer the banks blend back up to the terrain.
    const double RiverFreq = 0.003;
    const double RiverInner = 0.004;
    const double RiverOuter = 0.022;
    // domain warp: the river band is sampled at coordinates displaced by two
    // fbm noises, so channels meander and kink like real rivers instead of
    // tracing the smooth round iso-lines of the raw band
    const double RiverWarpFreq = 0.004;
    const double RiverWarp = 200;
    const double RiverSurfaceDrop = 2;   // water surface under the macro height
    const double RiverDepth = 3.5;       // extra carve below the surface at the centerline

    public int SurfaceHeight(int gx, int gz) => ComputeHeight(gx, gz).Height;

    /// Everything ComputeHeight learns about a column that ClassifyBiome and
    /// FillColumn need again: masks are 0..1, TBase/MBase are the raw
    /// continent-scale temperature/humidity bands.
    readonly record struct HeightInfo(int Height, double Ocean, double Mountain, double River,
        int RiverSurface, double Badlands, double Mushroom, double TBase, double MBase);

    static double Smooth(double t) => t * t * (3 - 2 * t);

    HeightInfo ComputeHeight(int gx, int gz)
    {
        // offset well away from the noise-lattice origin so the world's
        // spawn point isn't sitting on a degenerate (fx=fz=0) sample
        double cont = Noise.Fbm2(gx * ContinentalFreq + 300, gz * ContinentalFreq + 300);
        double ocean = Smooth(Math.Clamp((0.46 - cont) / 0.10, 0, 1));    // wide smooth shelf into the sea
        double mountain = Smooth(Math.Clamp((cont - 0.58) / 0.16, 0, 1)); // ranges where continents peak

        double macro = PlainsBase - ocean * (PlainsBase - OceanFloor) + mountain * MountainBonus;

        double tBase = Noise.Noise2(gx * ClimateBaseFreq + 1000, gz * ClimateBaseFreq + 1000);
        double mBase = Noise.Noise2(gx * ClimateBaseFreq + 2000, gz * ClimateBaseFreq + 2000);

        double r1 = 1 - Math.Abs(Noise.Noise2(gx * RidgeFreq + 500, gz * RidgeFreq + 500) * 2 - 1);
        double r2 = 1 - Math.Abs(Noise.Noise2(gx * RidgeFreq * 2.7 + 900, gz * RidgeFreq * 2.7 + 900) * 2 - 1);
        double ridge = r1 * 0.7 + r2 * 0.3;

        double hills = (Noise.Fbm2(gx * HillFreq + 700, gz * HillFreq + 700) - 0.35) * HillBonus
                     * (1 - ocean) * (1 - mountain * 0.6);
        double detail = Noise.Fbm2(gx * DetailFreq1, gz * DetailFreq1) * DetailBonus1
                       + Noise.Noise2(gx * DetailFreq2, gz * DetailFreq2) * DetailBonus2;

        double h = macro + mountain * ridge * ridge * RidgeBonus + hills + detail;

        // mushroom islands rise out of open ocean to a low rolling cap
        double mush = 0;
        if (ocean > 0.7)
        {
            mush = Smooth(Math.Clamp((Noise.Noise2(gx * MushroomFreq + 8000, gz * MushroomFreq + 8000) - 0.88) / 0.04, 0, 1))
                 * Math.Clamp((ocean - 0.7) / 0.2, 0, 1);
            if (mush > 0)
            {
                double island = SeaLevel + 3 + Noise.Fbm2(gx * 0.01 + 8500, gz * 0.01 + 8500) * 8;
                h = h * (1 - mush) + island * mush;
            }
        }

        // badlands lift the ground and quantize it into 6-block mesa steps;
        // the mask fades in smoothly so plateau rims blend into normal land
        double bad = Smooth(Math.Clamp((Noise.Noise2(gx * BadlandsFreq + 9000, gz * BadlandsFreq + 9000) - 0.72) / 0.08, 0, 1))
                   * Math.Clamp((tBase - 0.52) / 0.12, 0, 1) * Math.Clamp((0.48 - mBase) / 0.12, 0, 1)
                   * (1 - ocean) * (1 - mountain);
        if (bad > 0)
        {
            double mesa = Math.Floor((h + 10) / 6.0) * 6.0;
            h = h * (1 - bad) + mesa * bad;
        }

        double wx = (Noise.Fbm2(gx * RiverWarpFreq + 5100, gz * RiverWarpFreq + 5100) - 0.5) * RiverWarp;
        double wz = (Noise.Fbm2(gx * RiverWarpFreq + 5700, gz * RiverWarpFreq + 5700) - 0.5) * RiverWarp;
        double riv = Noise.Noise2((gx + wx) * RiverFreq + 4000, (gz + wz) * RiverFreq + 4000);
        double dist = Math.Abs(riv - 0.5);
        double river = Math.Clamp((RiverOuter - dist) / (RiverOuter - RiverInner), 0, 1);
        river = Smooth(river); // soft banks
        int riverSurface = 0;
        if (river > 0)
        {
            // the water surface tracks the macro landscape (min against the
            // real height so a local dip never leaves water perched above
            // its banks); the bed carves deeper toward the centerline
            double surfaceLvl = Math.Min(macro, h) - RiverSurfaceDrop;
            double bed = surfaceLvl - RiverDepth * river;
            h = Math.Min(h, h * (1 - river) + bed * river);
            riverSurface = (int)Math.Floor(surfaceLvl);
        }

        return new(Math.Clamp((int)Math.Floor(h), 4, WorldHeight - 4), ocean, mountain, river,
            riverSurface, bad, mush, tBase, mBase);
    }

    /// <param name="RiverSurface">River water level for this column (0 = not a river)</param>
    public readonly record struct ColumnInfo(int Height, Biome Biome, bool Steep, int RiverSurface);

    /// Scans outward in rings for a safe spawn column: dry land above sea
    /// level, gentle slope, no river channel, and no tree trunk or canopy
    /// overhanging the spot. Deterministic per seed. Falls back to the
    /// starting column if nothing qualifies within the radius.
    public (int X, int Z) FindSpawn(int aroundX = 8, int aroundZ = 8, int radius = 256)
    {
        for (int r = 0; r <= radius; r += 8)
            for (int dz = -r; dz <= r; dz += 8)
                for (int dx = -r; dx <= r; dx += 8)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue; // ring perimeter only
                    if (IsSafeSpawn(aroundX + dx, aroundZ + dz)) return (aroundX + dx, aroundZ + dz);
                }
        return (aroundX, aroundZ);
    }

    bool IsSafeSpawn(int x, int z)
    {
        var col = Sample(x, z);
        if (col.Height <= SeaLevel + 1 || col.Steep || col.RiverSurface > 0) return false;
        if (col.Biome is not (Biome.Plains or Biome.Forest or Biome.Beach or Biome.Desert
            or Biome.BirchForest or Biome.FlowerForest or Biome.Taiga)) return false;
        for (int dx = -2; dx <= 2; dx++)      // canopies reach 2 blocks out from the trunk
            for (int dz = -2; dz <= 2; dz++)
                if (TreeAt(x + dx, z + dz) != null) return false;
        return true;
    }

    /// computeSteep costs two extra height evaluations; bulk consumers that
    /// don't need it (the 2D map) pass false.
    public ColumnInfo Sample(int gx, int gz, bool computeSteep = true)
    {
        var hi = ComputeHeight(gx, gz);
        bool steep = computeSteep &&
                     (Math.Abs(ComputeHeight(gx + 1, gz).Height - hi.Height) >= 3
                   || Math.Abs(ComputeHeight(gx, gz + 1).Height - hi.Height) >= 3);
        return new ColumnInfo(hi.Height, ClassifyBiome(gx, gz, hi), steep, hi.RiverSurface);
    }

    // climate borders get a dose of higher-frequency jitter so neighbouring
    // biomes interleave in a ragged, dithered band instead of splitting along
    // a straight iso-line
    const double ClimateJitterFreq = 0.018;
    const double ClimateJitter = 0.10;
    const int SnowHeight = 86; // peaks above this are white regardless of climate

    Biome ClassifyBiome(int gx, int gz, in HeightInfo hi)
    {
        int h = hi.Height;
        if (hi.Mushroom > 0.5 && h > SeaLevel) return Biome.Mushroom;
        if (hi.River > 0.5 && hi.Ocean <= 0) return Biome.River; // rivers exist at any elevation now

        double jt = (Noise.Noise2(gx * ClimateJitterFreq + 5000, gz * ClimateJitterFreq + 5000) - 0.5) * ClimateJitter;
        double jh = (Noise.Noise2(gx * ClimateJitterFreq + 6000, gz * ClimateJitterFreq + 6000) - 0.5) * ClimateJitter;
        // continent-scale bands blended with the regional patch band, then
        // stretched back out so the extremes (desert, tundra) stay reachable
        double tVar = Noise.Noise2(gx * ClimateVarFreq + 1500, gz * ClimateVarFreq + 1500);
        double mVar = Noise.Noise2(gx * ClimateVarFreq + 2500, gz * ClimateVarFreq + 2500);
        double temperature = 0.5 + (hi.TBase * 0.62 + tVar * 0.38 - 0.5) * 1.5 + jt;
        double humidity = 0.5 + (hi.MBase * 0.62 + mVar * 0.38 - 0.5) * 1.5 + jh;
        // altitude cools the climate: highlands trend toward tundra/snow and
        // deserts only form in the lowlands
        temperature -= Math.Max(0, h - (PlainsBase + 10)) * 0.006;

        if (h <= SeaLevel)
        {
            if (temperature < 0.22) return Biome.FrozenOcean;
            return h <= SeaLevel - 14 ? Biome.DeepOcean : Biome.Ocean;
        }
        if (h <= SeaLevel + 2) return Biome.Beach;
        if (hi.Badlands > 0.4) return Biome.Badlands;

        if (hi.Mountain > 0.5 + jt * 0.6) // jitter raggedises the treeline too
            return h >= SnowHeight || temperature < 0.22 ? Biome.SnowyMountains : Biome.Mountains;

        // a third band picks between sister biomes of the same climate
        // (forest/birch, plains/flower field) so patches interleave
        double variant = Noise.Noise2(gx * ClimateVarFreq + 7000, gz * ClimateVarFreq + 7000);

        if (temperature < 0.24) return humidity > 0.55 ? Biome.SnowyTaiga : Biome.Tundra;
        if (temperature < 0.42) return humidity > 0.50 ? Biome.Taiga : (variant > 0.65 ? Biome.Forest : Biome.Plains);
        if (temperature > 0.64 && humidity < 0.34) return Biome.Desert;
        if (temperature > 0.56 && humidity < 0.48) return Biome.Savanna;
        if (humidity > 0.58 && h <= SeaLevel + 6) return Biome.Swamp;
        if (temperature > 0.56 && humidity > 0.60) return Biome.Jungle;
        if (humidity > 0.68) return Biome.DarkForest;
        if (humidity > 0.52) return variant > 0.62 ? Biome.BirchForest : Biome.Forest;
        if (variant > 0.76) return Biome.FlowerForest;
        return Biome.Plains;
    }

    static (int Surface, int Fill) SurfaceBlocks(Biome biome, bool steep) => biome switch
    {
        Biome.Ocean or Biome.DeepOcean or Biome.FrozenOcean or Biome.River
            or Biome.Beach or Biome.Desert => (BlockId.Sand, BlockId.Sand),
        Biome.Badlands => (BlockId.RedSand, BlockId.Terracotta), // fill deepens into banded strata (FillColumn)
        Biome.Mushroom => (BlockId.Mycelium, BlockId.Dirt),
        Biome.SnowyMountains => steep ? (BlockId.Stone, BlockId.Stone) : (BlockId.Snow, BlockId.Stone),
        Biome.Tundra or Biome.SnowyTaiga when !steep => (BlockId.Snow, BlockId.Dirt),
        _ when steep => (BlockId.Stone, BlockId.Stone),
        Biome.Mountains => (BlockId.Stone, BlockId.Stone), // rocky above the treeline even on gentle ground
        Biome.Savanna => (BlockId.DryGrass, BlockId.Dirt),
        _ => (BlockId.Grass, BlockId.Dirt),
    };

    /// Horizontal strata for badlands mesas: mostly orange terracotta with
    /// recurring red and sand bands, keyed by absolute height so the layers
    /// line up across the whole mesa like Minecraft's painted deserts.
    static int TerracottaBand(int y) => (y % 7) switch
    {
        2 => BlockId.TerracottaRed,
        5 => BlockId.RedSand,
        _ => BlockId.Terracotta,
    };

    public byte[] Generate(int cx, int cz)
    {
        var data = new byte[ChunkSize * ChunkSize * WorldHeight];
        for (int lz = 0; lz < ChunkSize; lz++)
            for (int lx = 0; lx < ChunkSize; lx++)
                FillColumn(data, cx * ChunkSize + lx, cz * ChunkSize + lz, lx, lz);
        PlantTrees(data, cx, cz);
        PlantVegetation(data, cx, cz);
        PlantVillages(data, cx, cz);
        PlantStructures(data, cx, cz);
        return data;
    }

    void FillColumn(byte[] data, int gx, int gz, int lx, int lz)
    {
        var col = Sample(gx, gz);
        int h = col.Height;
        var (surface, fill) = SurfaceBlocks(col.Biome, col.Steep);
        for (int y = 0; y <= h; y++)
        {
            int b;
            if (y == 0 || (y == 1 && Noise.Hash2(gx + 3, gz + 5) < 0.5)) b = BlockId.Bedrock;
            else if (IsCave(gx, y, gz, h)) b = BlockId.Air;
            else if (y == h) b = surface;
            // badlands run 12 deep so terraced cliff faces expose the strata
            else if (col.Biome == Biome.Badlands && y >= h - 11) b = TerracottaBand(y);
            else if (y >= h - 3) b = fill;
            else b = OreAt(gx, y, gz);
            data[BlockIndex(lx, y, lz)] = (byte)b;
        }
        // low ground floods up to sea level; river channels flood up to
        // their own terrain-following surface, which can sit far above it.
        // Frozen oceans cap the flood with a sheet of ice.
        for (int y = h + 1; y <= Math.Max(SeaLevel, col.RiverSurface); y++)
            data[BlockIndex(lx, y, lz)] = (byte)(col.Biome == Biome.FrozenOcean && y == SeaLevel
                ? BlockId.Ice : BlockId.Water);
    }

    bool IsCave(int gx, int gy, int gz, int h)
    {
        if (gy < 2 || gy > h - 3) return false;
        if (Noise.Noise3(gx * 0.08, gy * 0.11, gz * 0.08) > 0.66) return true;
        double worm = Noise.Noise3(gx * 0.045 + 100, gy * 0.05, gz * 0.045 + 100);
        return Math.Abs(worm - 0.5) < 0.035;
    }

    int OreAt(int gx, int gy, int gz)
    {
        double r = Noise.Hash3(gx + 51, gy + 77, gz + 91);
        if (gy < 6 && r < 0.004) return BlockId.Diamond;
        if (gy < 16 && r < 0.010) return BlockId.Gold;
        if (gy < 20 && r < 0.017) return BlockId.RedstoneOre;
        if (gy < 32 && r < 0.024) return BlockId.Iron;
        if (gy < 48 && r < 0.045) return BlockId.Coal;
        return BlockId.Stone;
    }

    // ------------------------------------------------------------- trees

    enum TreeKind { Oak, Birch, Spruce, DarkOak, Jungle, Savanna, Mushroom, Cactus }

    (int H, int Trunk, TreeKind Kind)? TreeAt(int gx, int gz)
    {
        var col = Sample(gx, gz);
        if (col.Steep) return null;
        var (chance, kind, tMin, tVar) = col.Biome switch
        {
            Biome.Forest => (0.05, TreeKind.Oak, 4, 3),
            Biome.BirchForest => (0.055, TreeKind.Birch, 5, 3),
            Biome.DarkForest => (0.09, TreeKind.DarkOak, 4, 2),   // dense closed canopy
            Biome.FlowerForest => (0.015, TreeKind.Oak, 4, 3),
            Biome.Taiga => (0.055, TreeKind.Spruce, 5, 4),
            Biome.SnowyTaiga => (0.035, TreeKind.Spruce, 5, 3),
            Biome.Plains => (0.008, TreeKind.Oak, 4, 3),
            Biome.Jungle => (0.085, TreeKind.Jungle, 6, 4),
            Biome.Swamp => (0.02, TreeKind.Oak, 4, 2),
            Biome.Savanna => (0.006, TreeKind.Savanna, 4, 2),
            Biome.Mushroom => (0.045, TreeKind.Mushroom, 4, 3),
            Biome.Desert => (0.007, TreeKind.Cactus, 1, 3),
            Biome.Badlands => (0.004, TreeKind.Cactus, 1, 2),
            _ => (0.0, TreeKind.Oak, 0, 0),
        };
        if (chance <= 0 || Noise.Hash2(gx + 911, gz + 337) >= chance) return null;
        // regular forests mix in the odd birch, like Minecraft's oak forests
        if (kind == TreeKind.Oak && col.Biome == Biome.Forest && Noise.Hash2(gx + 551, gz + 883) < 0.25)
            kind = TreeKind.Birch;
        return (col.Height, tMin + (int)Math.Floor(Noise.Hash2(gx + 13, gz + 7) * tVar), kind);
    }

    // Trees are deterministic per column, so trees straddling chunk borders
    // come out identical when each neighbouring chunk generates its share.
    void PlantTrees(byte[] data, int cx, int cz)
    {
        for (int lz = -4; lz < ChunkSize + 4; lz++)
            for (int lx = -4; lx < ChunkSize + 4; lx++)
            {
                int gx = cx * ChunkSize + lx, gz = cz * ChunkSize + lz;
                var tree = TreeAt(gx, gz);
                if (tree is not { } t) continue;

                void Put(int bx, int by, int bz, int b, bool onlyAir)
                {
                    int px = bx - cx * ChunkSize, pz = bz - cz * ChunkSize;
                    if (px < 0 || px > ChunkSize - 1 || pz < 0 || pz > ChunkSize - 1 || by < 0 || by >= WorldHeight) return;
                    int i = BlockIndex(px, by, pz);
                    if (onlyAir && data[i] != BlockId.Air) return;
                    data[i] = (byte)b;
                }

                int trunkBlock = t.Kind switch
                {
                    TreeKind.Birch => BlockId.BirchWood,
                    TreeKind.Spruce => BlockId.SpruceWood,
                    TreeKind.Mushroom => BlockId.MushroomStem,
                    TreeKind.Cactus => BlockId.Cactus,
                    _ => BlockId.Wood,
                };
                for (int dy = 1; dy <= t.Trunk; dy++) Put(gx, t.H + dy, gz, trunkBlock, false);
                int top = t.H + t.Trunk;
                switch (t.Kind)
                {
                    case TreeKind.Cactus: // bare column, no canopy
                        break;
                    case TreeKind.Spruce: // conical: alternating narrow/wide rings up to a tip
                        Put(gx, top + 2, gz, BlockId.Leaves, true);
                        for (int dy = 1; dy >= -3; dy--)
                        {
                            int r = dy is -1 or -3 ? 2 : 1;
                            for (int dx = -r; dx <= r; dx++)
                                for (int dz = -r; dz <= r; dz++)
                                    if (!(dx == 0 && dz == 0) && !(r == 2 && Math.Abs(dx) == 2 && Math.Abs(dz) == 2))
                                        Put(gx + dx, top + dy, gz + dz, BlockId.Leaves, true);
                        }
                        break;
                    case TreeKind.Birch: // slim tall crown
                        for (int dx = -1; dx <= 1; dx++)
                            for (int dy = -2; dy <= 2; dy++)
                                for (int dz = -1; dz <= 1; dz++)
                                    if (dx * dx + dy * dy * 0.8 + dz * dz <= 3.4)
                                        Put(gx + dx, top + dy, gz + dz, BlockId.Leaves, true);
                        break;
                    case TreeKind.DarkOak: // huge flat blob that closes the canopy
                        for (int dx = -3; dx <= 3; dx++)
                            for (int dz = -3; dz <= 3; dz++)
                                if (dx * dx + dz * dz <= 10.5)
                                {
                                    Put(gx + dx, top, gz + dz, BlockId.Leaves, true);
                                    Put(gx + dx, top - 1, gz + dz, BlockId.Leaves, true);
                                }
                        for (int dx = -2; dx <= 2; dx++)
                            for (int dz = -2; dz <= 2; dz++)
                                if (dx * dx + dz * dz <= 4.5)
                                    Put(gx + dx, top + 1, gz + dz, BlockId.Leaves, true);
                        break;
                    case TreeKind.Mushroom: // domed red cap with white spots
                        for (int dx = -2; dx <= 2; dx++)
                            for (int dz = -2; dz <= 2; dz++)
                                if (dx * dx + dz * dz <= 6.5)
                                    Put(gx + dx, top + 1, gz + dz, BlockId.MushroomCap, true);
                        for (int dx = -1; dx <= 1; dx++)
                            for (int dz = -1; dz <= 1; dz++)
                                Put(gx + dx, top + 2, gz + dz, BlockId.MushroomCap, true);
                        break;
                    case TreeKind.Jungle: // big rounded canopy for tall jungle trees
                        for (int dx = -3; dx <= 3; dx++)
                            for (int dy = -2; dy <= 2; dy++)
                                for (int dz = -3; dz <= 3; dz++)
                                    if (dx * dx + dy * dy * 2.2 + dz * dz <= 10.5)
                                        Put(gx + dx, top + dy, gz + dz, BlockId.Leaves, true);
                        break;
                    case TreeKind.Savanna: // flat acacia-style crown: wide disk with a small cap
                        for (int dx = -3; dx <= 3; dx++)
                            for (int dz = -3; dz <= 3; dz++)
                                if (dx * dx + dz * dz <= 9.5)
                                    Put(gx + dx, top, gz + dz, BlockId.Leaves, true);
                        for (int dx = -1; dx <= 1; dx++)
                            for (int dz = -1; dz <= 1; dz++)
                                Put(gx + dx, top + 1, gz + dz, BlockId.Leaves, true);
                        break;
                    default:
                        for (int dx = -2; dx <= 2; dx++)
                            for (int dy = -2; dy <= 2; dy++)
                                for (int dz = -2; dz <= 2; dz++)
                                    if (dx * dx + dy * dy * 1.6 + dz * dz <= 5.3)
                                        Put(gx + dx, top + dy, gz + dz, BlockId.Leaves, true);
                        break;
                }
            }
    }

    // ------------------------------------------------------------- vegetation

    // Chunk-local only — grass tufts and flowers are single blocks, so unlike
    // trees they never straddle a chunk border.
    void PlantVegetation(byte[] data, int cx, int cz)
    {
        for (int lz = 0; lz < ChunkSize; lz++)
            for (int lx = 0; lx < ChunkSize; lx++)
            {
                int gx = cx * ChunkSize + lx, gz = cz * ChunkSize + lz;
                var col = Sample(gx, gz);
                if (col.Steep) continue;
                var (grass, flowers) = col.Biome switch
                {
                    Biome.Plains => (0.05, 0.016),
                    Biome.Forest => (0.05, 0.016),
                    Biome.BirchForest => (0.05, 0.02),
                    Biome.DarkForest => (0.04, 0.006),
                    Biome.FlowerForest => (0.06, 0.22), // carpeted in blooms
                    Biome.Taiga => (0.04, 0.004),
                    Biome.Swamp => (0.16, 0.004),   // lush, overgrown ground
                    Biome.Jungle => (0.10, 0.012),
                    Biome.Savanna => (0.12, 0.0),   // dry tufts, no flowers
                    _ => (0.0, 0.0),
                };
                if (grass <= 0) continue;
                int y = col.Height + 1;
                if (y >= WorldHeight) continue;
                int i = BlockIndex(lx, y, lz);
                if (data[i] != BlockId.Air) continue; // a tree (or its leaves) already claimed this cell

                double r = Noise.Hash2(gx + 4001, gz + 4001);
                if (r < grass) data[i] = (byte)BlockId.TallGrass;
                else if (r < grass + flowers / 2) data[i] = (byte)BlockId.FlowerYellow;
                else if (r < grass + flowers) data[i] = (byte)BlockId.FlowerRed;
            }
    }

    // ------------------------------------------------------------- villages

    const int VillageCell = 96;   // grid spacing between candidate village sites
    const int VillageMargin = 40; // max reach of a village's structures from its anchor

    // Villages sit on a coarse grid so candidates are cheap to enumerate;
    // each qualifying cell is re-derived identically by every chunk within
    // reach of it (same pattern as tree placement), so structures straddling
    // chunk borders come out consistent no matter which chunk generates first.
    void PlantVillages(byte[] data, int cx, int cz)
    {
        int minGx = cx * ChunkSize - VillageMargin, maxGx = cx * ChunkSize + ChunkSize + VillageMargin;
        int minGz = cz * ChunkSize - VillageMargin, maxGz = cz * ChunkSize + ChunkSize + VillageMargin;
        int cell0X = FloorDiv(minGx, VillageCell), cell1X = FloorDiv(maxGx, VillageCell);
        int cell0Z = FloorDiv(minGz, VillageCell), cell1Z = FloorDiv(maxGz, VillageCell);

        for (int gcx = cell0X; gcx <= cell1X; gcx++)
            for (int gcz = cell0Z; gcz <= cell1Z; gcz++)
                if (TryVillageAt(gcx, gcz, out int ax, out int az, out int height))
                    PlaceVillage(data, cx, cz, ax, az, height);
    }

    /// Does grid cell (gcx, gcz) qualify for a village, and if so where and
    /// how tall is its anchor. Shared by generation and by external lookups
    /// (e.g. locating a village to visit/screenshot without generating it).
    bool TryVillageAt(int gcx, int gcz, out int ax, out int az, out int height)
    {
        ax = az = height = 0;
        if (Noise.Hash2(gcx * 7919 + 11, gcz * 7919 + 11) > 0.3) return false; // most cells have none
        int jitterRange = VillageCell - 24;
        ax = gcx * VillageCell + 12 + (int)(Noise.Hash2(gcx * 131 + 3, gcz * 131 + 3) * jitterRange);
        az = gcz * VillageCell + 12 + (int)(Noise.Hash2(gcx * 131 + 7, gcz * 131 + 7) * jitterRange);

        var col = Sample(ax, az);
        if (col.Biome is not (Biome.Plains or Biome.Savanna or Biome.Taiga) || col.Steep || !IsFlatArea(ax, az)) return false;
        height = col.Height;
        return true;
    }

    /// Enumerates qualifying village anchors inside a world-space rectangle,
    /// without generating any chunks (used by the 2D map for markers).
    public IEnumerable<(int Ax, int Az)> VillagesIn(int minX, int minZ, int maxX, int maxZ)
    {
        for (int gcx = FloorDiv(minX, VillageCell); gcx <= FloorDiv(maxX, VillageCell); gcx++)
            for (int gcz = FloorDiv(minZ, VillageCell); gcz <= FloorDiv(maxZ, VillageCell); gcz++)
                if (TryVillageAt(gcx, gcz, out int ax, out int az, out _) &&
                    ax >= minX && ax <= maxX && az >= minZ && az <= maxZ)
                    yield return (ax, az);
    }

    /// Scans outward from a point for the nearest qualifying village site,
    /// without generating any chunks. Used by tooling/tests to locate one.
    public (int Ax, int Az)? FindNearestVillage(int aroundX, int aroundZ, int radius)
    {
        int cell0X = FloorDiv(aroundX - radius, VillageCell), cell1X = FloorDiv(aroundX + radius, VillageCell);
        int cell0Z = FloorDiv(aroundZ - radius, VillageCell), cell1Z = FloorDiv(aroundZ + radius, VillageCell);
        (int, int)? best = null;
        int bestDist = int.MaxValue;
        for (int gcx = cell0X; gcx <= cell1X; gcx++)
            for (int gcz = cell0Z; gcz <= cell1Z; gcz++)
                if (TryVillageAt(gcx, gcz, out int ax, out int az, out _))
                {
                    int d = (ax - aroundX) * (ax - aroundX) + (az - aroundZ) * (az - aroundZ);
                    if (d < bestDist) { bestDist = d; best = (ax, az); }
                }
        return best;
    }

    // Sampled at step 10 so even a ~10-block river channel crossing the
    // footprint can't slip between the probes and flood a village.
    bool IsFlatArea(int gx, int gz)
    {
        int baseH = SurfaceHeight(gx, gz);
        for (int dx = -20; dx <= 20; dx += 10)
            for (int dz = -20; dz <= 20; dz += 10)
                if (Math.Abs(SurfaceHeight(gx + dx, gz + dz) - baseH) > 3) return false;
        return true;
    }

    // house positions around the well, each rotated so its door (south wall
    // at rotation 0) faces the well at the village center
    static readonly (int Dx, int Dz, int Rot)[] HouseOffsets =
    {
        (0, -10, 0),  // north of the well: door faces south
        (10, 0, 1),   // east: door faces west
        (0, 10, 2),   // south: door faces north
        (-10, 0, 3),  // west: door faces east
    };

    /// A village is a composite: the "well" structure at the anchor and a
    /// "house" on each side — both loaded from assets\structures, so editing
    /// those files reshapes every village.
    void PlaceVillage(byte[] data, int cx, int cz, int ax, int az, int centerH)
    {
        if (StructureRegistry.Get("well") is { } well)
            PlaceStructure(data, cx, cz, well, ax, az, centerH);
        if (StructureRegistry.Get("house") is { } house)
            foreach (var (dx, dz, rot) in HouseOffsets)
            {
                int hx = ax + dx, hz = az + dz;
                PlaceStructure(data, cx, cz, house.Rotated(rot), hx, hz, SurfaceHeight(hx, hz));
            }
    }

    // ------------------------------------------------------------- structures

    /// Standalone structures (towers, pyramids, huts...): every loaded
    /// structure with a placement cell gets its own deterministic grid, the
    /// same pattern as villages, so pieces straddling chunk borders come out
    /// identical no matter which chunk generates first.
    void PlantStructures(byte[] data, int cx, int cz)
    {
        foreach (var s in StructureRegistry.All.Values)
        {
            if (s.Cell <= 0) continue;
            int margin = Math.Max(s.SizeX, s.SizeZ);
            int cell0X = FloorDiv(cx * ChunkSize - margin, s.Cell), cell1X = FloorDiv(cx * ChunkSize + ChunkSize + margin, s.Cell);
            int cell0Z = FloorDiv(cz * ChunkSize - margin, s.Cell), cell1Z = FloorDiv(cz * ChunkSize + ChunkSize + margin, s.Cell);
            for (int gcx = cell0X; gcx <= cell1X; gcx++)
                for (int gcz = cell0Z; gcz <= cell1Z; gcz++)
                {
                    // each placement rolls its own orientation (deterministic
                    // per cell, so chunk-border pieces agree)
                    int rot = (int)(Noise.Hash2(gcx * 977 + s.Salt, gcz * 977 + s.Salt) * 4) & 3;
                    var rotated = s.Rotated(rot);
                    if (TryStructureAt(rotated, gcx, gcz, out int ax, out int az, out int h))
                        PlaceStructure(data, cx, cz, rotated, ax, az, h);
                }
        }
    }

    /// Does this structure's grid cell spawn one, and where. Deterministic
    /// per (structure, cell) — the salt keeps different structures from
    /// always landing in the same cells.
    bool TryStructureAt(Structure s, int gcx, int gcz, out int ax, out int az, out int height)
    {
        ax = az = height = 0;
        if (Noise.Hash2(gcx * 7919 + s.Salt, gcz * 7919 + s.Salt) > s.Chance) return false;
        int half = Math.Max(s.SizeX, s.SizeZ) / 2 + 2;
        int jitterRange = Math.Max(1, s.Cell - half * 2);
        ax = gcx * s.Cell + half + (int)(Noise.Hash2(gcx * 131 + s.Salt + 3, gcz * 131 + s.Salt + 3) * jitterRange);
        az = gcz * s.Cell + half + (int)(Noise.Hash2(gcx * 131 + s.Salt + 7, gcz * 131 + s.Salt + 7) * jitterRange);

        var col = Sample(ax, az);
        if (col.Steep || col.RiverSurface > 0 || !s.Biomes.Contains(col.Biome)) return false;
        // the footprint corners must sit close to the anchor's height
        int rx = s.SizeX / 2, rz = s.SizeZ / 2;
        foreach (var (dx, dz) in new[] { (-rx, -rz), (rx, -rz), (-rx, rz), (rx, rz) })
            if (Math.Abs(SurfaceHeight(ax + dx, az + dz) - col.Height) > s.MaxSlope)
                return false;
        height = col.Height;
        return true;
    }

    /// Stamps a structure with its anchor column centered in the footprint.
    /// -1 cells leave terrain untouched; door tokens place their upper half
    /// automatically.
    void PlaceStructure(byte[] data, int cx, int cz, Structure s, int ax, int az, int groundH)
    {
        int x0 = ax - s.SizeX / 2, z0 = az - s.SizeZ / 2, y0 = groundH + 1 + s.YBase;
        for (int y = 0; y < s.SizeY; y++)
            for (int z = 0; z < s.SizeZ; z++)
                for (int x = 0; x < s.SizeX; x++)
                {
                    int id = s.At(x, y, z);
                    if (id < 0) continue;
                    Stamp(data, cx, cz, x0 + x, y0 + y, z0 + z, id);
                    if (BlockRegistry.ShapeOf(id) == BlockShape.DoorLower)
                        Stamp(data, cx, cz, x0 + x, y0 + y + 1, z0 + z,
                            BlockRegistry.DoorVariant(BlockRegistry.FacingOf(id), open: false, upper: true));
                }
    }

    // Structures force-place their blocks — they overwrite terrain, trees,
    // and vegetation within their footprint rather than requiring bare air.
    static void Stamp(byte[] data, int cx, int cz, int bx, int by, int bz, int block)
    {
        int px = bx - cx * ChunkSize, pz = bz - cz * ChunkSize;
        if (px < 0 || px > ChunkSize - 1 || pz < 0 || pz > ChunkSize - 1 || by < 0 || by >= WorldHeight) return;
        data[BlockIndex(px, by, pz)] = (byte)block;
    }

    static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);
}
