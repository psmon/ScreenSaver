using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ScreenSaverOverlay.Effects;
using ScreenSaverOverlay.Native;
using ScreenSaverOverlay.Settings;

namespace ScreenSaverOverlay.Overlay;

/// <summary>
/// A borderless, topmost, click-through layered window that covers one target monitor and
/// renders the active effect with per-pixel alpha. It never activates, so it floats above
/// the running screensaver without stealing focus or feeding it input. One instance is
/// created per target monitor (see <see cref="AppSettings.ResolveTargetScreens"/>).
/// </summary>
public sealed class OverlayForm : Form
{
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly Stopwatch _clock = new();
    private long _lastTicks;

    private const int FrameIntervalMs = 33; // ~30 FPS — smooth enough for sprites, ~half the CPU of 60

    private readonly List<IEffect> _effects = new();
    private AppSettings _settings;

    // A persistent DIB section is the layered-window surface: we draw GDI+ straight into its
    // pixels and hand its DC to UpdateLayeredWindow every frame — no per-frame GetHbitmap/DC
    // churn (that allocation+copy was the bulk of the overlay's CPU).
    private Bitmap? _surface;
    private IntPtr _memDc, _dib, _oldObj, _bits;
    private Rectangle _bounds;
    private readonly Rectangle _targetBounds;

    public OverlayForm(AppSettings settings, Rectangle targetBounds)
    {
        _settings = settings.Clone().Normalized();
        _targetBounds = targetBounds;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Text = "ScreenSaverOverlay";

        _timer.Interval = FrameIntervalMs;
        _timer.Tick += OnTick;
    }

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
        _bounds = _targetBounds;
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
        DisposeSurface();

        int w = Math.Max(1, _bounds.Width), h = Math.Max(1, _bounds.Height);
        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        _memDc = NativeMethods.CreateCompatibleDC(screenDc);

        var bi = new NativeMethods.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h,           // top-down so it matches the managed Bitmap orientation
            biPlanes = 1,
            biBitCount = 32,
            biCompression = NativeMethods.BI_RGB,
        };
        _dib = NativeMethods.CreateDIBSection(_memDc, ref bi, NativeMethods.DIB_RGB_COLORS, out _bits, IntPtr.Zero, 0);
        _oldObj = NativeMethods.SelectObject(_memDc, _dib);
        NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);

        // Premultiplied ARGB: GDI+ draws straight into the DIB bits in the format
        // UpdateLayeredWindow expects (AC_SRC_ALPHA).
        _surface = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, _bits);
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
        if (_surface is null || _memDc == IntPtr.Zero) return;

        using (var g = Graphics.FromImage(_surface))
        {
            g.Clear(Color.Transparent);
            foreach (var eff in _effects)
                eff.Render(g, _bounds.Size);
            g.Flush();
        }
        NativeMethods.GdiFlush(); // ensure GDI+ writes have landed in the DIB before the blit

        PushToScreen((byte)_settings.Opacity);
    }

    /// <summary>
    /// Per-pixel-alpha blit via UpdateLayeredWindow, reusing the persistent DIB-backed memory DC
    /// (no per-frame bitmap/DC allocation).
    /// </summary>
    private void PushToScreen(byte opacity)
    {
        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        try
        {
            var size = new NativeMethods.SIZE(_bounds.Width, _bounds.Height);
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
                _memDc, ref src, 0, ref blend, NativeMethods.ULW_ALPHA);
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private void DisposeSurface()
    {
        _surface?.Dispose();
        _surface = null;
        if (_memDc != IntPtr.Zero)
        {
            if (_oldObj != IntPtr.Zero) NativeMethods.SelectObject(_memDc, _oldObj);
            if (_dib != IntPtr.Zero) NativeMethods.DeleteObject(_dib);
            NativeMethods.DeleteDC(_memDc);
        }
        _memDc = _dib = _oldObj = _bits = IntPtr.Zero;
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
            DisposeSurface();
        }
        base.Dispose(disposing);
    }
}
