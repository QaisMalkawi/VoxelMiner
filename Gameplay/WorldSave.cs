using System.IO.Compression;
using System.Numerics;

namespace VoxelMiner.Gameplay;

using VoxelMiner.Core;
using VoxelMiner.Gameplay.Drops;
using VoxelMiner.World;

/// Parsed save file. Loading is two-phase: the seed must be known before the
/// terrain generator (and thus the world) can be built, so the file is read
/// into this first and applied to the live systems afterwards.
public sealed class SaveData
{
    public string Name = "World";
    public int Seed;
    public GameMode Mode;
    public float TimeOfDay = 0.25f; // pre-VXW3 saves load at noon
    public Vector3 PlayerPos;
    public float Yaw, Pitch, Health, Hunger, Saturation, Exhaustion, Air;
    public bool Flying;
    public int SelectedSlot;
    public ItemStack[] Inventory = new ItemStack[Gameplay.Inventory.Size];
    public List<(int Cx, int Cz, byte[] Data)> Chunks = new();
    public List<(int X, int Y, int Z, int Dist)> Flow = new();
    public List<(int X, int Y, int Z, ItemStack[] Slots)> Chests = new();
    public List<(int X, int Y, int Z, FurnaceState State)> Furnaces = new();
    public List<(Vector3 Pos, float Yaw)> Boats = new();
    public List<(Vector3 Pos, int Id, int Count)> Drops = new();
}

/// A save file's identity, cheap to read for the world-selection menu.
public sealed record SaveInfo(string Path, string Name, int Seed, DateTime Modified);

/// World persistence: each world is one GZip-compressed binary file under
/// saves\, holding the seed plus only what diverged from generation — edited
/// chunks, containers, water flow, entities, and the player. Untouched
/// terrain regenerates from the seed, so file size scales with how much was
/// built, not how far was explored.
public static class WorldSave
{
    const string Magic = "VXW3";
    const string MagicV2 = "VXW2";     // pre-day/night format: no time-of-day field
    const string LegacyMagic = "VXW1"; // pre-menu format: no world name field

    static bool KnownMagic(string m) => m is Magic or MagicV2 or LegacyMagic;

    public static string SavesDir => Path.Combine(AppContext.BaseDirectory, "saves");

    /// A fresh, collision-free file path for a new world with this name.
    public static string NewPath(string name)
    {
        var slug = new string(name.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        if (slug.Length == 0) slug = "world";
        string path = Path.Combine(SavesDir, slug + ".dat");
        for (int n = 2; File.Exists(path); n++)
            path = Path.Combine(SavesDir, $"{slug}-{n}.dat");
        return path;
    }

    /// All readable saves, most recently played first.
    public static List<SaveInfo> ListSaves()
    {
        if (!Directory.Exists(SavesDir)) return new List<SaveInfo>();
        var list = new List<SaveInfo>();
        foreach (var file in Directory.GetFiles(SavesDir, "*.dat"))
            if (ReadInfo(file) is { } info)
                list.Add(info);
        return list.OrderByDescending(i => i.Modified).ToList();
    }

    /// Reads just the header — enough for the menu without decompressing chunks.
    static SaveInfo ReadInfo(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            using var gz = new GZipStream(file, CompressionMode.Decompress);
            using var r = new BinaryReader(gz);
            // VXW1 predates world names; those saves show their file name
            string magic = new string(r.ReadChars(4));
            if (!KnownMagic(magic)) return null;
            string name = magic == LegacyMagic ? Path.GetFileNameWithoutExtension(path) : r.ReadString();
            int seed = r.ReadInt32();
            return new SaveInfo(path, name, seed, File.GetLastWriteTime(path));
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------- save

    public static void Save(string path, string name, GameWorld world, Fluids fluids, BlockEntities entities,
        Player player, Inventory inventory, GameMode mode, BoatManager boats, DropManager drops, float timeOfDay)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string tmp = path + ".tmp";
        using (var file = File.Create(tmp))
        using (var gz = new GZipStream(file, CompressionLevel.Fastest))
        using (var w = new BinaryWriter(gz))
        {
            w.Write(Magic.ToCharArray());
            w.Write(name);
            w.Write(Constants.Seed);
            w.Write((byte)mode);
            w.Write(timeOfDay);

            WriteVector(w, player.Pos);
            w.Write(player.Yaw);
            w.Write(player.Pitch);
            w.Write(player.Health);
            w.Write(player.Hunger);
            w.Write(player.Saturation);
            w.Write(player.Exhaustion);
            w.Write(player.Air);
            w.Write(player.Flying);

            w.Write(inventory.Selected);
            foreach (var s in inventory.Slots) WriteStack(w, s);

            // only chunks that diverged from generation
            var edited = world.EditedChunks.Where(world.Chunks.ContainsKey).ToList();
            w.Write(edited.Count);
            foreach (var (cx, cz) in edited)
            {
                w.Write(cx);
                w.Write(cz);
                w.Write(world.Chunks[(cx, cz)]);
            }

            var flow = fluids.ExportFlow().ToList();
            w.Write(flow.Count);
            foreach (var (x, y, z, dist) in flow)
            {
                w.Write(x); w.Write(y); w.Write(z);
                w.Write((byte)dist);
            }

            var chests = entities.AllChests().ToList();
            w.Write(chests.Count);
            foreach (var (pos, slots) in chests)
            {
                w.Write(pos.X); w.Write(pos.Y); w.Write(pos.Z);
                foreach (var s in slots) WriteStack(w, s);
            }

            var furnaces = entities.AllFurnaces().ToList();
            w.Write(furnaces.Count);
            foreach (var (pos, f) in furnaces)
            {
                w.Write(pos.X); w.Write(pos.Y); w.Write(pos.Z);
                foreach (var s in f.Slots) WriteStack(w, s);
                w.Write(f.Progress);
                w.Write(f.FuelLeft);
                w.Write(f.FuelTotal);
            }

            w.Write(boats.All.Count);
            foreach (var b in boats.All)
            {
                WriteVector(w, b.Pos);
                w.Write(b.Yaw);
            }

            w.Write(drops.All.Count);
            foreach (var d in drops.All)
            {
                WriteVector(w, d.Pos);
                w.Write(d.Id);
                w.Write(d.Count);
            }
        }
        // atomic swap so a crash mid-write can't corrupt the existing save
        File.Move(tmp, path, overwrite: true);
    }

    // ------------------------------------------------------------- load

    /// Reads a save file; null when missing or unreadable.
    public static SaveData Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var file = File.OpenRead(path);
            using var gz = new GZipStream(file, CompressionMode.Decompress);
            using var r = new BinaryReader(gz);

            string magic = new string(r.ReadChars(4));
            if (!KnownMagic(magic)) return null;
            var s = new SaveData
            {
                Name = magic == LegacyMagic ? Path.GetFileNameWithoutExtension(path) : r.ReadString(),
                Seed = r.ReadInt32(),
                Mode = (GameMode)r.ReadByte(),
            };
            if (magic == Magic) s.TimeOfDay = r.ReadSingle();

            s.PlayerPos = ReadVector(r);
            s.Yaw = r.ReadSingle();
            s.Pitch = r.ReadSingle();
            s.Health = r.ReadSingle();
            s.Hunger = r.ReadSingle();
            s.Saturation = r.ReadSingle();
            s.Exhaustion = r.ReadSingle();
            s.Air = r.ReadSingle();
            s.Flying = r.ReadBoolean();

            s.SelectedSlot = r.ReadInt32();
            for (int i = 0; i < s.Inventory.Length; i++) s.Inventory[i] = ReadStack(r);

            int chunkBytes = Constants.ChunkSize * Constants.ChunkSize * Constants.WorldHeight;
            int chunks = r.ReadInt32();
            for (int i = 0; i < chunks; i++)
            {
                int cx = r.ReadInt32(), cz = r.ReadInt32();
                s.Chunks.Add((cx, cz, r.ReadBytes(chunkBytes)));
            }

            int flow = r.ReadInt32();
            for (int i = 0; i < flow; i++)
                s.Flow.Add((r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadByte()));

            int chests = r.ReadInt32();
            for (int i = 0; i < chests; i++)
            {
                int x = r.ReadInt32(), y = r.ReadInt32(), z = r.ReadInt32();
                var slots = new ItemStack[BlockEntities.ChestSlots];
                for (int k = 0; k < slots.Length; k++) slots[k] = ReadStack(r);
                s.Chests.Add((x, y, z, slots));
            }

            int furnaces = r.ReadInt32();
            for (int i = 0; i < furnaces; i++)
            {
                int x = r.ReadInt32(), y = r.ReadInt32(), z = r.ReadInt32();
                var f = new FurnaceState();
                for (int k = 0; k < f.Slots.Length; k++) f.Slots[k] = ReadStack(r);
                f.Progress = r.ReadSingle();
                f.FuelLeft = r.ReadSingle();
                f.FuelTotal = r.ReadSingle();
                s.Furnaces.Add((x, y, z, f));
            }

            int boats = r.ReadInt32();
            for (int i = 0; i < boats; i++)
                s.Boats.Add((ReadVector(r), r.ReadSingle()));

            int drops = r.ReadInt32();
            for (int i = 0; i < drops; i++)
                s.Drops.Add((ReadVector(r), r.ReadInt32(), r.ReadInt32()));

            return s;
        }
        catch (Exception e)
        {
            Console.WriteLine($"Failed to load save '{path}': {e.Message}");
            return null;
        }
    }

    /// Installs loaded data into the live systems. The caller must already
    /// have set Constants.Seed = data.Seed before creating the world.
    public static void Apply(SaveData s, GameWorld world, Fluids fluids, BlockEntities entities,
        Player player, Inventory inventory, BoatManager boats, DropManager drops)
    {
        foreach (var (cx, cz, data) in s.Chunks)
        {
            world.AdoptChunk(cx, cz, data);
            world.EditedChunks.Add((cx, cz)); // stays dirty for the next save
        }
        foreach (var (x, y, z, dist) in s.Flow) fluids.ImportFlow(x, y, z, dist);
        foreach (var (x, y, z, slots) in s.Chests) entities.RestoreChest(x, y, z, slots);
        foreach (var (x, y, z, f) in s.Furnaces) entities.RestoreFurnace(x, y, z, f);
        foreach (var (pos, yaw) in s.Boats) boats.Spawn(pos.X, pos.Y, pos.Z, yaw);
        foreach (var (pos, id, count) in s.Drops)
            drops.All.Add(new ItemDrop { Pos = pos, Id = id, Count = count });

        inventory.Selected = s.SelectedSlot;
        Array.Copy(s.Inventory, inventory.Slots, inventory.Slots.Length);
        player.Restore(s.PlayerPos, s.Yaw, s.Pitch, s.Health, s.Hunger,
            s.Saturation, s.Exhaustion, s.Air, s.Flying);
    }

    // ------------------------------------------------------------- helpers

    static void WriteVector(BinaryWriter w, Vector3 v)
    {
        w.Write(v.X); w.Write(v.Y); w.Write(v.Z);
    }

    static Vector3 ReadVector(BinaryReader r) =>
        new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

    static void WriteStack(BinaryWriter w, ItemStack s)
    {
        w.Write(s?.Id ?? 0);
        if (s == null) return;
        w.Write(s.Count);
        w.Write(s.Durability);
    }

    static ItemStack ReadStack(BinaryReader r)
    {
        int id = r.ReadInt32();
        if (id == 0) return null;
        return new ItemStack { Id = id, Count = r.ReadInt32(), Durability = r.ReadInt32() };
    }
}
