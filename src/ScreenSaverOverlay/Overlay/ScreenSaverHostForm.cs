using System.Diagnostics;

namespace ScreenSaverOverlay.Overlay;

/// <summary>
/// A fullscreen window that *hosts* an existing screensaver (.scr) inside itself by launching
/// it in preview mode (<c>scr /p &lt;hwnd&gt;</c>) parented into this window. Because the saver
/// runs as our child — and we own the foreground — it never loses input focus and so never
/// self-terminates. The overlay effect is drawn by a separate layered window placed on top,
/// so the saver and the effect coexist.
///
/// This is the same technique multi-monitor screensaver tools (e.g. DisplayFusion) use.
/// Note: a few screensavers refuse to render into a preview host; that's why hosting is
/// verified per-saver before relying on it.
/// </summary>
public sealed class ScreenSaverHostForm : Form
{
    private readonly string _scrPath;
    private Process? _scrProcess;

    public ScreenSaverHostForm(string scrPath)
    {
        _scrPath = scrPath;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Color.Black;
        Text = "ScreenSaverHost";
        Bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
    }

    public void Start()
    {
        Show();
        Activate(); // we deliberately own the foreground here (it's our own host window)
        LaunchSaver();
    }

    private void LaunchSaver()
    {
        if (!File.Exists(_scrPath))
            return;

        // /p <hwnd> = preview into the given window. The saver SetParents itself into our HWND.
        var psi = new ProcessStartInfo
        {
            FileName = _scrPath,
            Arguments = $"/p {Handle.ToInt64()}",
            UseShellExecute = false,
        };

        try
        {
            _scrProcess = Process.Start(psi);
        }
        catch
        {
            _scrProcess = null;
        }
    }

    public void Stop()
    {
        try
        {
            if (_scrProcess is { HasExited: false })
                _scrProcess.Kill(entireProcessTree: true);
        }
        catch { /* best effort */ }
        finally
        {
            _scrProcess?.Dispose();
            _scrProcess = null;
        }
        Hide();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            Stop();
        base.Dispose(disposing);
    }
}
