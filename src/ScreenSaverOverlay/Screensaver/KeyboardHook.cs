using System.Runtime.InteropServices;

namespace ScreenSaverOverlay.Screensaver;

/// <summary>
/// A global low-level keyboard hook (<c>WH_KEYBOARD_LL</c>). While installed it raises
/// <see cref="KeyPressed"/> on the first key-down of any key. Used to dismiss a running
/// preview the instant the user presses a key — the overlay window never takes focus, so
/// it can't catch <c>KeyDown</c> itself, and a global hook is the reliable way to notice.
///
/// The callback is dispatched on the thread that installed the hook (our UI thread, which
/// pumps messages), so the <see cref="KeyPressed"/> handler can touch UI directly.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private readonly LowLevelKeyboardProc _proc; // kept alive for the lifetime of the hook
    private IntPtr _hook;

    /// <summary>Raised on any key-down while the hook is installed.</summary>
    public event EventHandler? KeyPressed;

    public KeyboardHook() => _proc = HookCallback;

    public bool IsInstalled => _hook != IntPtr.Zero;

    public void Install()
    {
        if (_hook != IntPtr.Zero) return;
        // GetModuleHandle(null) returns the .exe's module handle, which is sufficient for a
        // low-level hook whose callback lives in our own process.
        _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, GetModuleHandleW(null), 0);
    }

    public void Uninstall()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                KeyPressed?.Invoke(this, EventArgs.Empty);
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose() => Uninstall();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetModuleHandleW([MarshalAs(UnmanagedType.LPWStr)] string? lpModuleName);
}
