using ScreenSaverOverlay.Native;

namespace ScreenSaverOverlay.Screensaver;

/// <summary>
/// Detects user inactivity via <c>GetLastInputInfo</c>. In hosted mode the agent — not
/// Windows — decides when the screensaver session starts, so it needs its own idle clock.
/// Raises <see cref="IdleReached"/> once when idle crosses the threshold, and
/// <see cref="ActivityResumed"/> once when input returns.
/// </summary>
public sealed class IdleWatcher : IDisposable
{
    private readonly System.Windows.Forms.Timer _timer;
    private bool _idle;

    /// <summary>Idle seconds required before a session starts.</summary>
    public int ThresholdSeconds { get; set; }

    /// <summary>Raised once when the system has been idle for <see cref="ThresholdSeconds"/>.</summary>
    public event EventHandler? IdleReached;

    /// <summary>Raised once when the user becomes active again.</summary>
    public event EventHandler? ActivityResumed;

    public bool IsIdle => _idle;

    public IdleWatcher(int thresholdSeconds, int pollMilliseconds = 500)
    {
        ThresholdSeconds = Math.Max(1, thresholdSeconds);
        _timer = new System.Windows.Forms.Timer { Interval = Math.Max(200, pollMilliseconds) };
        _timer.Tick += (_, _) => Poll();
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    private void Poll()
    {
        double idle = NativeMethods.GetIdleSeconds();

        if (!_idle && idle >= ThresholdSeconds)
        {
            _idle = true;
            IdleReached?.Invoke(this, EventArgs.Empty);
        }
        else if (_idle && idle < 1.0)
        {
            // Any fresh input resets the idle clock to ~0.
            _idle = false;
            ActivityResumed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose() => _timer.Dispose();
}
