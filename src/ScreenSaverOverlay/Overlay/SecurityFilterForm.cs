using ScreenSaverOverlay.Native;

namespace ScreenSaverOverlay.Overlay;

/// <summary>
/// An opaque, borderless, topmost cover for one monitor that is NOT showing the screensaver.
/// While the machine is locked it hides whatever was on that screen behind a designated
/// wallpaper (or plain black), so no sensitive desktop content stays visible on the other
/// monitors. It never takes focus — the PIN prompt owns the foreground.
/// </summary>
public sealed class SecurityFilterForm : Form
{
    private readonly Image? _wallpaper;

    public SecurityFilterForm(Rectangle bounds, string? wallpaperPath)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Color.Black;
        DoubleBuffered = true;
        Text = "SecurityFilter";
        Bounds = bounds;

        _wallpaper = TryLoad(wallpaperPath);
    }

    private static Image? TryLoad(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        try
        {
            // Copy into an in-memory bitmap so the file isn't kept locked on disk.
            using var loaded = Image.FromFile(path);
            return new Bitmap(loaded);
        }
        catch
        {
            return null;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW   // hide from Alt-Tab / taskbar
                        | NativeMethods.WS_EX_NOACTIVATE;  // let the PIN prompt keep focus
            return cp;
        }
    }

    public void Start()
    {
        Show();
        NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST,
            Bounds.Left, Bounds.Top, Bounds.Width, Bounds.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    public void Stop() => Hide();

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_wallpaper is null)
        {
            base.OnPaint(e);
            return;
        }

        // Cover the monitor while preserving the image's aspect ratio (center-crop).
        var g = e.Graphics;
        double scale = Math.Max(
            ClientSize.Width / (double)_wallpaper.Width,
            ClientSize.Height / (double)_wallpaper.Height);
        int w = (int)Math.Ceiling(_wallpaper.Width * scale);
        int h = (int)Math.Ceiling(_wallpaper.Height * scale);
        int x = (ClientSize.Width - w) / 2;
        int y = (ClientSize.Height - h) / 2;
        g.DrawImage(_wallpaper, x, y, w, h);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _wallpaper?.Dispose();
        base.Dispose(disposing);
    }
}
