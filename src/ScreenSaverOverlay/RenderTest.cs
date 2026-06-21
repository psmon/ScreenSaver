using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using ScreenSaverOverlay.Effects;
using ScreenSaverOverlay.Settings;

namespace ScreenSaverOverlay;

/// <summary>
/// Offscreen visual QA: steps an effect through simulated time and tiles snapshots into one
/// montage PNG, composited over a water-like gradient so transparent sprites/bubbles are
/// visible. Invoked via <c>--render-test &lt;effectId&gt; &lt;outPng&gt;</c>.
/// </summary>
internal static class RenderTest
{
    public static void Run(string effectId, string outPng)
    {
        const int cw = 900, ch = 560;        // per-cell render size
        double[] sampleTimes = { 0.5, 3, 6, 9, 12, 16, 20, 25 };
        const double dt = 1.0 / 60.0;

        // effectId "*" => composite every enabled layer from the saved settings (multi-effect test);
        // otherwise a single effect by id.
        var effects = new List<IEffect>();
        if (effectId == "*")
        {
            var settings = AppSettings.Load();
            foreach (var layer in settings.EnabledLayers())
            {
                var e = EffectRegistry.Create(layer.EffectId);
                e.Initialize(new Size(cw, ch), settings.ForLayer(layer));
                effects.Add(e);
            }
        }
        else
        {
            var settings = new AppSettings { EffectId = effectId, Count = 3, Size = 90, Speed = 1.0 };
            var e = EffectRegistry.Create(effectId);
            e.Initialize(new Size(cw, ch), settings);
            effects.Add(e);
        }

        var cells = new List<Bitmap>();
        double t = 0;
        int si = 0;
        double end = sampleTimes[^1] + 0.01;
        while (t <= end && si < sampleTimes.Length)
        {
            foreach (var e in effects) e.Update(dt);
            t += dt;
            if (t >= sampleTimes[si])
            {
                cells.Add(RenderCell(effects, cw, ch, sampleTimes[si]));
                si++;
            }
        }

        // tile 2 columns
        int cols = 2, rows = (cells.Count + 1) / 2, pad = 8;
        int W = cols * cw + (cols + 1) * pad, H = rows * ch + (rows + 1) * pad;
        using var montage = new Bitmap(W, H);
        using (var g = Graphics.FromImage(montage))
        {
            g.Clear(Color.FromArgb(20, 22, 28));
            for (int i = 0; i < cells.Count; i++)
            {
                int r = i / cols, c = i % cols;
                g.DrawImage(cells[i], pad + c * (cw + pad), pad + r * (ch + pad));
            }
        }
        foreach (var b in cells) b.Dispose();

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPng)) ?? ".");
        montage.Save(outPng, ImageFormat.Png);
        Console.WriteLine($"saved {outPng} ({W}x{H}) from effect '{effectId}'");
    }

    private static Bitmap RenderCell(List<IEffect> effects, int w, int h, double t)
    {
        var bmp = new Bitmap(w, h);
        using var g = Graphics.FromImage(bmp);
        // water gradient
        using (var br = new LinearGradientBrush(new Rectangle(0, 0, w, h),
                   Color.FromArgb(255, 18, 78, 110), Color.FromArgb(255, 6, 20, 44), 90f))
            g.FillRectangle(br, 0, 0, w, h);
        foreach (var effect in effects) effect.Render(g, new Size(w, h));
        using (var font = new Font("Segoe UI", 12, FontStyle.Bold))
            g.DrawString($"t = {t:0.0}s", font, Brushes.White, 10, 8);
        return bmp;
    }
}
