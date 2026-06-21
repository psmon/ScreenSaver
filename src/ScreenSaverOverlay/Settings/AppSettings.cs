using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenSaverOverlay.Settings;

/// <summary>
/// User-configurable settings, persisted as JSON under
/// %AppData%\ScreenSaverOverlay\settings.json.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Id of the selected effect (see EffectRegistry).</summary>
    public string EffectId { get; set; } = "bouncing-circles";

    /// <summary>Number of shapes / particles.</summary>
    public int Count { get; set; } = 12;

    /// <summary>Base size of a shape in pixels.</summary>
    public int Size { get; set; } = 80;

    /// <summary>Movement speed multiplier (1.0 = default).</summary>
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

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

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
        return this;
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
