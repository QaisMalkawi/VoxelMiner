namespace VoxelMiner.Core;

/// World dimensions and shared identifiers.
public static class Constants
{
    public const int ChunkSize = 16;
    public const int WorldHeight = 192;
    public static int ViewRadius = 8; // chunks; driven by the render-distance setting
    public static int Seed = Random.Shared.Next();
    public const int SeaLevel = 28; // air at or below this height generates as water
    public const int MaxLight = 15;

    // Packing assumes ChunkSize = 16 (4 bits per horizontal axis).
    public static int BlockIndex(int lx, int y, int lz) => lx | (lz << 4) | (y << 8);
}

/// Minecraft-style play modes. Survival: health, fall/drowning damage, finite
/// items, timed mining. Creative: invulnerable, instant breaking, infinite
/// blocks, flight.
public enum GameMode { Survival, Creative,
    Spectator
}

/// Placeable voxel types.
public static class BlockId
{
    public const int Air = 0;
    public const int Grass = 1;
    public const int Dirt = 2;
    public const int Stone = 3;
    public const int Sand = 4;
    public const int Wood = 5;
    public const int Leaves = 6;
    public const int Coal = 7;
    public const int Iron = 8;
    public const int Gold = 9;
    public const int Diamond = 10;
    public const int Bedrock = 11;
    public const int Planks = 12;
    public const int Torch = 13;
    public const int Water = 14;
    public const int TallGrass = 15;
    public const int FlowerYellow = 16;
    public const int FlowerRed = 17;
    public const int Snow = 18;      // tundra / snowy mountain surface
    public const int DryGrass = 19;  // savanna surface
    public const int BirchWood = 20;     // white-barked birch forest trunk
    public const int SpruceWood = 21;    // dark taiga trunk
    public const int Cactus = 22;        // desert / badlands column plant
    public const int Ice = 23;           // frozen ocean surface
    public const int RedSand = 24;       // badlands surface
    public const int Terracotta = 25;    // badlands strata (orange)
    public const int TerracottaRed = 26; // badlands strata (dark red band)
    public const int Mycelium = 27;      // mushroom island surface
    public const int MushroomCap = 28;   // giant mushroom cap (red, white-spotted)
    public const int MushroomStem = 29;  // giant mushroom stem

    // Oriented / stateful blocks: each state is its own id (chunks store one
    // byte per cell, no metadata channel). The first id of a family is the
    // "base" — the id items and drops carry; +0..3 encodes facing N/E/S/W.
    public const int PlankStairs = 30;     // 30..33  facing
    public const int StoneStairs = 34;     // 34..37  facing
    public const int PlankSlab = 38;       // bottom half
    public const int PlankSlabTop = 39;
    public const int StoneSlab = 40;
    public const int StoneSlabTop = 41;
    public const int PlankSlabVert = 42;   // 42..45  facing = occupied half
    public const int StoneSlabVert = 46;   // 46..49
    public const int Door = 50;            // 50..65: +4 open, +8 upper half, +facing
    public const int Trapdoor = 66;        // 66..73: +4 open, +facing
    public const int Chest = 74;           // 74..77  facing = front
    public const int Furnace = 78;         // 78..85: +4 lit, +facing
    public const int Fence = 86;           // arms connect to neighbours at mesh time
    public const int Glass = 87;           // cutout-transparent cube
}

/// Non-placeable item types (share the same id space as blocks).
public static class ItemId
{
    public const int Stick = 100100;
    public const int WoodPick = 100101;
    public const int StonePick = 100102;
    public const int IronPick = 100103;
    public const int DiamondPick = 100104;
    public const int Axe = 100105;
    public const int Shovel = 100106;
    public const int Boat = 100107;


    public const int RawPigMeat = 100108;
    public const int RawSheepMeat = 100109;
    public const int RawChickenMeat = 100110;

    public const int CookedPigMeat = 100111;
    public const int CookedSheepMeat = 100112;
    public const int CookedChickenMeat = 100113;
}
