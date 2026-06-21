using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenSaverOverlay.Settings;

/// <summary>
/// One overlay effect layer with its own count/size/speed. The overlay can run several of
/// these at once (e.g. circles + diver1 + diver2), each independently configured.
/// </summary>
public sealed class EffectLayer
{
    public string EffectId { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int Count { get; set; } = 3;
    public int Size { get; set; } = 100;
    public double Speed { get; set; } = 1.0;

    public EffectLayer Clone() => (EffectLayer)MemberwiseClone();

    public EffectLayer Normalized()
    {
        Count = Math.Clamp(Count, 1, 500);
        Size = Math.Clamp(Size, 4, 1000);
        Speed = Math.Clamp(Speed, 0.05, 20.0);
        return this;
    }
}

/// <summary>
/// User-configurable settings, persisted as JSON under
/// %AppData%\ScreenSaverOverlay\settings.json.
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// The overlay effect layers. Several can be enabled at once, each with its own count/size.
    /// Empty = migrate from the legacy single-effect fields below.
    /// </summary>
    public List<EffectLayer> Layers { get; set; } = new();

    // ---- legacy single-effect fields (kept for migration / back-compat) ---

    /// <summary>Legacy: id of the single selected effect. Superseded by <see cref="Layers"/>.</summary>
    public string EffectId { get; set; } = "diver2-sprite";

    /// <summary>Legacy: number of shapes / particles.</summary>
    public int Count { get; set; } = 12;

    /// <summary>Legacy: base size of a shape in pixels.</summary>
    public int Size { get; set; } = 80;

    /// <summary>Legacy: movement speed multiplier (1.0 = default).</summary>
    public double Speed { get; set; } = 1.0;

    /// <summary>Overall opacity 0..255.</summary>
    public int Opacity { get; set; } = 220;

    /// <summary>Primary color as ARGB hex (#RRGGBB). Empty = rainbow per shape.</summary>
    public string ColorHex { get; set; } = "";

    /// <summary>Seconds to wait after the (hosted) screensaver starts before the overlay joins.</summary>
    public int StartDelaySeconds { get; set; } = 10;

    /// <summary>
    /// When true, the agent itself detects idle, hosts the chosen screensaver, and overlays
    /// the effect — temporarily disabling Windows' own auto-screensaver to avoid a conflict.
    /// </summary>
    public bool HostedAutoMode { get; set; } = true;

    /// <summary>Idle seconds before a hosted session starts (like the Windows screensaver wait).</summary>
    public int IdleSeconds { get; set; } = 60;

    /// <summary>
    /// Full path of the .scr screensaver to host underneath the overlay. Empty = use the
    /// screensaver currently registered in Windows. We render this saver into our own window
    /// and draw the effect on top, so the saver and the overlay coexist without conflict.
    /// </summary>
    public string ScreenSaverPath { get; set; } = "";

    /// <summary>Start the agent automatically when the user logs in.</summary>
    public bool AutoStart { get; set; } = false;

    // ---- persistence ------------------------------------------------------

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ScreenSaverOverlay");

    public static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is not null)
                    return loaded.Normalized();
            }
        }
        catch
        {
            // Corrupt or unreadable settings should never crash the agent — fall back to defaults.
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsDirectory);
        var json = JsonSerializer.Serialize(Normalized(), JsonOptions);
        File.WriteAllText(SettingsPath, json);
    }

    public AppSettings Clone()
    {
        var c = (AppSettings)MemberwiseClone();
        c.Layers = Layers.Select(l => l.Clone()).ToList(); // deep copy so clones don't share layers
        return c;
    }

    /// <summary>Clamp values into sane ranges so a hand-edited file can't break rendering.</summary>
    public AppSettings Normalized()
    {
        Count = Math.Clamp(Count, 1, 500);
        Size = Math.Clamp(Size, 4, 1000);
        Speed = Math.Clamp(Speed, 0.05, 20.0);
        Opacity = Math.Clamp(Opacity, 1, 255);
        StartDelaySeconds = Math.Clamp(StartDelaySeconds, 0, 600);
        IdleSeconds = Math.Clamp(IdleSeconds, 5, 7200);
        if (string.IsNullOrWhiteSpace(EffectId))
            EffectId = "bouncing-circles";

        // Migrate a legacy single-effect config into one layer.
        if (Layers.Count == 0)
            Layers.Add(new EffectLayer { EffectId = EffectId, Enabled = true, Count = Count, Size = Size, Speed = Speed });
        foreach (var l in Layers)
            l.Normalized();
        return this;
    }

    /// <summary>Enabled layers to render; falls back to one layer from the legacy fields.</summary>
    public List<EffectLayer> EnabledLayers()
    {
        var enabled = Layers.Where(l => l.Enabled && !string.IsNullOrWhiteSpace(l.EffectId)).ToList();
        if (enabled.Count == 0)
            enabled.Add(new EffectLayer { EffectId = EffectId, Enabled = true, Count = Count, Size = Size, Speed = Speed });
        return enabled;
    }

    /// <summary>A settings view where the global Count/Size/Speed come from a specific layer,
    /// so an effect's <c>Initialize</c> sees that layer's own configuration.</summary>
    public AppSettings ForLayer(EffectLayer layer)
    {
        var s = Clone();
        s.EffectId = layer.EffectId;
        s.Count = layer.Count;
        s.Size = layer.Size;
        s.Speed = layer.Speed;
        return s;
    }

    /// <summary>The .scr to host: explicit selection, else the one registered in Windows.</summary>
    public string? ResolveScreenSaverPath()
    {
        if (!string.IsNullOrWhiteSpace(ScreenSaverPath) && File.Exists(ScreenSaverPath))
            return ScreenSaverPath;
        return Screensaver.ScreenSaverCatalog.CurrentRegistered();
    }

    public Color? ResolveColor()
    {
        if (string.IsNullOrWhiteSpace(ColorHex))
            return null;
        try
        {
            return ColorTranslator.FromHtml(ColorHex.StartsWith('#') ? ColorHex : "#" + ColorHex);
        }
        catch
        {
            return null;
        }
    }
}
