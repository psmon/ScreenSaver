using System.Diagnostics;
using System.Drawing.Imaging;
using ScreenSaverOverlay.Effects;
using ScreenSaverOverlay.Native;
using ScreenSaverOverlay.Settings;

namespace ScreenSaverOverlay.Overlay;

/// <summary>
/// A borderless, topmost, click-through layered window that covers the whole desktop and
/// renders the active effect with per-pixel alpha. It never activates, so it floats above
/// the running screensaver without stealing focus or feeding it input.
/// </summary>
public sealed class OverlayForm : Form
{
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly Stopwatch _clock = new();
    private long _lastTicks;

    private readonly List<IEffect> _effects = new();
    private AppSettings _settings;
    private Bitmap? _surface;
    private Rectangle _bounds;

    public OverlayForm(AppSettings settings)
    {
        _settings = settings.Clone().Normalized();

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Text = "ScreenSaverOverlay";

        _timer.Interval = 16; // ~60 FPS
        _timer.Tick += OnTick;
    }

    // Cover the entire virtual desktop (all monitors).
    private static Rectangle FullDesktopBounds => SystemInformation.VirtualScreen;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_LAYERED
                        | NativeMethods.WS_EX_TRANSPARENT
                        | NativeMethods.WS_EX_TOPMOST
                        | NativeMethods.WS_EX_TOOLWINDOW
                        | NativeMethods.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    public void Start()
    {
        _bounds = FullDesktopBounds;
        Bounds = _bounds;
        RecreateSurface();
        BuildEffects();

        Show();
        ForceTopMost();

        _clock.Restart();
        _lastTicks = _clock.ElapsedTicks;
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        _clock.Stop();
        Hide();
    }

    /// <summary>Apply new settings live (used by the preview / settings dialog).</summary>
    public void ApplySettings(AppSettings settings)
    {
        _settings = settings.Clone().Normalized();
        _bounds = FullDesktopBounds;
        Bounds = _bounds;
        RecreateSurface();
        BuildEffects();
    }

    /// <summary>Create one effect instance per enabled layer, each with its own count/size/speed.</summary>
    private void BuildEffects()
    {
        _effects.Clear();
        foreach (var layer in _settings.EnabledLayers())
        {
            var eff = EffectRegistry.Create(layer.EffectId);
            eff.Initialize(_bounds.Size, _settings.ForLayer(layer));
            _effects.Add(eff);
        }
    }

    private void RecreateSurface()
    {
        _surface?.Dispose();
        _surface = new Bitmap(Math.Max(1, _bounds.Width), Math.Max(1, _bounds.Height),
            PixelFormat.Format32bppArgb);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        long now = _clock.ElapsedTicks;
        double dt = (now - _lastTicks) / (double)Stopwatch.Frequency;
        _lastTicks = now;

        foreach (var eff in _effects)
            eff.Update(dt);
        RenderFrame();
    }

    private void RenderFrame()
    {
        if (_surface is null) return;

        using (var g = Graphics.FromImage(_surface))
        {
            g.Clear(Color.Transparent);
            foreach (var eff in _effects)
                eff.Render(g, _bounds.Size);
        }

        PushToScreen(_surface, (byte)_settings.Opacity);
    }

    /// <summary>
    /// Canonical per-pixel-alpha blit via UpdateLayeredWindow. The bitmap carries the alpha
    /// channel; <paramref name="opacity"/> scales the whole frame uniformly.
    /// </summary>
    private void PushToScreen(Bitmap bitmap, byte opacity)
    {
        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        IntPtr memDc = NativeMethods.CreateCompatibleDC(screenDc);
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;

        try
        {
            hBitmap = bitmap.GetHbitmap(Color.FromArgb(0)); // preserves the alpha channel
            oldBitmap = NativeMethods.SelectObject(memDc, hBitmap);

            var size = new NativeMethods.SIZE(bitmap.Width, bitmap.Height);
            var src = new NativeMethods.POINT(0, 0);
            var dst = new NativeMethods.POINT(_bounds.Left, _bounds.Top);

            var blend = new NativeMethods.BLENDFUNCTION
            {
                BlendOp = NativeMethods.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = opacity,
                AlphaFormat = NativeMethods.AC_SRC_ALPHA,
            };

            NativeMethods.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size,
                memDc, ref src, 0, ref blend, NativeMethods.ULW_ALPHA);
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            if (hBitmap != IntPtr.Zero)
            {
                NativeMethods.SelectObject(memDc, oldBitmap);
                NativeMethods.DeleteObject(hBitmap);
            }
            NativeMethods.DeleteDC(memDc);
        }
    }

    private void ForceTopMost()
    {
        NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST,
            _bounds.Left, _bounds.Top, _bounds.Width, _bounds.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _surface?.Dispose();
        }
        base.Dispose(disposing);
    }
}
