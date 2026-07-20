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

/// <summary>A named layout: its zones plus per-layout behavior flags.</summary>
[JsonConverter(typeof(LayoutDefConverter))]
public class LayoutDef
{
    public List<Zone> Zones { get; set; } = new();

    /// <summary>Quick-snapped windows extend over/behind the taskbar instead of clipping to the work area.</summary>
    public bool OverTaskbar { get; set; }
}

/// <summary>Accepts both the legacy plain-array layout format and the current object form.</summary>
public class LayoutDefConverter : JsonConverter<LayoutDef>
{
    public override LayoutDef Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.StartArray)
            return new LayoutDef { Zones = JsonSerializer.Deserialize<List<Zone>>(ref reader, options) ?? new() };

        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException();
        var def = new LayoutDef();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString();
            reader.Read();
            if (string.Equals(name, "zones", StringComparison.OrdinalIgnoreCase))
                def.Zones = JsonSerializer.Deserialize<List<Zone>>(ref reader, options) ?? new();
            else if (string.Equals(name, "overTaskbar", StringComparison.OrdinalIgnoreCase))
                def.OverTaskbar = reader.GetBoolean();
            else
                reader.Skip();
        }
        return def;
    }

    public override void Write(Utf8JsonWriter writer, LayoutDef value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("zones");
        JsonSerializer.Serialize(writer, value.Zones, options);
        writer.WriteBoolean("overTaskbar", value.OverTaskbar);
        writer.WriteEndObject();
    }
}

public class Config
{
    public string ActiveLayout { get; set; } = "halves";
    public Dictionary<string, LayoutDef> Layouts { get; set; } = new();
    public List<AutoRule> AutoRules { get; set; } = new();

    /// <summary>Focused clamped windows go always-on-top so they cover the taskbar.</summary>
    public bool TopmostOnFocus { get; set; } = true;

    /// <summary>Quick Zones: Shift+drag a window to snap it into a zone (one-time, no clamping).</summary>
    public bool QuickZonesEnabled { get; set; } = true;

    /// <summary>Layout Quick Zones uses — independent of the Forced Zones active layout.
    /// Initialized to the active layout when missing (e.g. older configs).</summary>
    public string? QuickZonesLayout { get; set; }

    [JsonIgnore]
    public LayoutDef? QuickZonesDef =>
        QuickZonesLayout != null && Layouts.TryGetValue(QuickZonesLayout, out var def)
            ? def
            : Layouts.TryGetValue(ActiveLayout, out var active) ? active : null;

    [JsonIgnore]
    public List<Zone> QuickZones => QuickZonesDef?.Zones ?? new List<Zone>();

    [JsonIgnore]
    public List<Zone> ActiveZones =>
        Layouts.TryGetValue(ActiveLayout, out var def) ? def.Zones : new List<Zone>();

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
            if (loaded != null && loaded.Layouts.Count > 0)
            {
                // Quick Zones always has its own concrete layout; older configs used
                // null to mean "follow the active layout", so materialize that here.
                if (loaded.QuickZonesLayout == null || !loaded.Layouts.ContainsKey(loaded.QuickZonesLayout))
                    loaded.QuickZonesLayout = loaded.ActiveLayout;
                return loaded;
            }
        }
        var def = CreateDefault();
        def.QuickZonesLayout = def.ActiveLayout;
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
            Layouts = new Dictionary<string, LayoutDef>
            {
                ["halves"] = new()
                {
                    Zones =
                    {
                        new Zone { Name = "left", X = x0, Y = y0, Width = w / 2, Height = h },
                        new Zone { Name = "right", X = x0 + w / 2, Y = y0, Width = w / 2, Height = h },
                    },
                },
                ["thirds"] = new()
                {
                    Zones =
                    {
                        new Zone { Name = "left", X = x0, Y = y0, Width = w / 4, Height = h },
                        new Zone { Name = "center", X = x0 + w / 4, Y = y0, Width = w / 2, Height = h },
                        new Zone { Name = "right", X = x0 + (w * 3) / 4, Y = y0, Width = w / 4, Height = h },
                    },
                },
            },
        };
    }
}
