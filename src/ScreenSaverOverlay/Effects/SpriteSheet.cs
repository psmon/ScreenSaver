using System.Drawing.Imaging;
using System.Text.Json;

namespace ScreenSaverOverlay.Effects;

/// <summary>
/// A loaded sprite sheet: one PNG strip plus the per-frame rectangles and durations parsed
/// from its Aseprite-Hash JSON (the format produced by the sprite-animator skill). Frames are
/// read in document order, which is the frame order the pipeline writes them in.
/// </summary>
public sealed class SpriteSheet : IDisposable
{
    public Bitmap Image { get; }
    public Rectangle[] Frames { get; }
    public int[] DurationsMs { get; }
    public int Count => Frames.Length;

    private SpriteSheet(Bitmap image, Rectangle[] frames, int[] durations)
    {
        Image = image;
        Frames = frames;
        DurationsMs = durations;
    }

    public static SpriteSheet Load(string pngPath, string jsonPath)
    {
        // Load the bitmap without locking the file on disk.
        Bitmap image;
        using (var tmp = new Bitmap(pngPath))
            image = new Bitmap(tmp);

        var frames = new List<Rectangle>();
        var durations = new List<int>();

        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        var framesEl = doc.RootElement.GetProperty("frames");

        if (framesEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in framesEl.EnumerateObject())
                ReadFrame(prop.Value, frames, durations);
        }
        else if (framesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in framesEl.EnumerateArray())
                ReadFrame(el, frames, durations);
        }

        return new SpriteSheet(image, frames.ToArray(), durations.ToArray());
    }

    private static void ReadFrame(JsonElement el, List<Rectangle> frames, List<int> durations)
    {
        var f = el.GetProperty("frame");
        frames.Add(new Rectangle(
            f.GetProperty("x").GetInt32(), f.GetProperty("y").GetInt32(),
            f.GetProperty("w").GetInt32(), f.GetProperty("h").GetInt32()));
        durations.Add(el.TryGetProperty("duration", out var d) ? d.GetInt32() : 120);
    }

    public void Dispose() => Image.Dispose();
}
