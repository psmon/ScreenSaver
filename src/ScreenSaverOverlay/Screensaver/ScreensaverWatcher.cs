using ScreenSaverOverlay.Native;

namespace ScreenSaverOverlay.Screensaver;

/// <summary>
/// Polls Windows for the "screensaver is running" state and raises events when it starts
/// and stops. Polling (rather than hooking) keeps the agent simple and robust: it makes
/// no attempt to control the screensaver, it only observes it.
/// </summary>
public sealed class ScreensaverWatcher : IDisposable
{
    private readonly System.Windows.Forms.Timer _timer;
    private bool _running;

    /// <summary>Raised on the UI thread when the screensaver transitions to running.</summary>
    public event EventHandler? Started;

    /// <summary>Raised on the UI thread when the screensaver transitions to stopped.</summary>
    public event EventHandler? Stopped;

    public bool IsRunning => _running;

    public ScreensaverWatcher(int pollMilliseconds = 1000)
    {
        _timer = new System.Windows.Forms.Timer { Interval = Math.Max(250, pollMilliseconds) };
        _timer.Tick += (_, _) => Poll();
    }

    public void Start()
    {
        _running = QueryRunning();
        _timer.Start();
    }

    public void Pause() => _timer.Stop();

    private void Poll()
    {
        bool now = QueryRunning();
        if (now == _running)
            return;

        _running = now;
        if (now)
            Started?.Invoke(this, EventArgs.Empty);
        else
            Stopped?.Invoke(this, EventArgs.Empty);
    }

    private static bool QueryRunning()
    {
        int running = 0;
        // SPI_GETSCREENSAVERRUNNING reports TRUE while a screensaver is actually displayed.
        if (NativeMethods.SystemParametersInfoW(NativeMethods.SPI_GETSCREENSAVERRUNNING, 0, ref running, 0))
            return running != 0;
        return false;
    }

    public void Dispose() => _timer.Dispose();
}
