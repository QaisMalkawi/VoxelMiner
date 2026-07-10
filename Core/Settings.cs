using System.Text.Json;

namespace VoxelMiner.Core;

/// User-tunable options, persisted as settings.json next to the executable.
public sealed class GameSettings
{
    public float MouseSensitivity { get; set; } = 1f; // multiplier on look speed
    public bool InvertY { get; set; }
    public int Fov { get; set; } = 75;                // degrees
    public int RenderDistance { get; set; } = 8;      // chunks
    public int Volume { get; set; } = 100;            // percent

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static GameSettings Load()
    {
        try
        {
            if (File.Exists(DefaultPath))
            {
                var s = JsonSerializer.Deserialize<GameSettings>(File.ReadAllText(DefaultPath));
                if (s != null)
                {
                    s.ClampAll();
                    return s;
                }
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"Couldn't read settings: {e.Message}");
        }
        return new GameSettings();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(DefaultPath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e)
        {
            Console.WriteLine($"Couldn't write settings: {e.Message}");
        }
    }

    public void ClampAll()
    {
        MouseSensitivity = Math.Clamp(MathF.Round(MouseSensitivity * 10) / 10, 0.2f, 3f);
        Fov = Math.Clamp(Fov, 60, 110);
        RenderDistance = Math.Clamp(RenderDistance, 4, 32);
        Volume = Math.Clamp(Volume, 0, 100);
    }
}
