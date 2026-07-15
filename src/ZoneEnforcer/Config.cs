using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZoneEnforcer;

public class Zone
{
    public string Name { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Contains(int px, int py) => px >= X && px < X + Width && py >= Y && py < Y + Height;
    public override string ToString() => $"{Name} ({X},{Y} {Width}x{Height})";
}

public class AutoRule
{
    /// <summary>Process name without .exe, case-insensitive (e.g. "vlc").</summary>
    public string Process { get; set; } = "";
    /// <summary>Optional: only match windows whose title contains this (case-insensitive).</summary>
    public string? TitleContains { get; set; }
    /// <summary>Zone name in the active layout.</summary>
    public string Zone { get; set; } = "";
}

public class Config
{
    public string ActiveLayout { get; set; } = "halves";
    public Dictionary<string, List<Zone>> Layouts { get; set; } = new();
    public List<AutoRule> AutoRules { get; set; } = new();

    [JsonIgnore]
    public List<Zone> ActiveZones =>
        Layouts.TryGetValue(ActiveLayout, out var zones) ? zones : new List<Zone>();

    public Zone? FindZone(string name) =>
        ActiveZones.FirstOrDefault(z => string.Equals(z.Name, name, StringComparison.OrdinalIgnoreCase));

    public static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZoneEnforcer");

    public static string ConfigPath => Path.Combine(ConfigDir, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static Config Load()
    {
        if (File.Exists(ConfigPath))
        {
            var loaded = JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath), JsonOptions);
            if (loaded != null && loaded.Layouts.Count > 0) return loaded;
        }
        var def = CreateDefault();
        def.Save();
        return def;
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOptions));
    }

    /// <summary>Default layouts sized to the primary display.</summary>
    public static Config CreateDefault()
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        int w = screen.Width, h = screen.Height, x0 = screen.X, y0 = screen.Y;

        return new Config
        {
            ActiveLayout = "halves",
            Layouts = new Dictionary<string, List<Zone>>
            {
                ["halves"] = new()
                {
                    new Zone { Name = "left", X = x0, Y = y0, Width = w / 2, Height = h },
                    new Zone { Name = "right", X = x0 + w / 2, Y = y0, Width = w / 2, Height = h },
                },
                ["thirds"] = new()
                {
                    new Zone { Name = "left", X = x0, Y = y0, Width = w / 4, Height = h },
                    new Zone { Name = "center", X = x0 + w / 4, Y = y0, Width = w / 2, Height = h },
                    new Zone { Name = "right", X = x0 + (w * 3) / 4, Y = y0, Width = w / 4, Height = h },
                },
            },
        };
    }
}
