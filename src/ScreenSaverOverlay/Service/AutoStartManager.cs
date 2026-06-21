using Microsoft.Win32;

namespace ScreenSaverOverlay.Service;

/// <summary>
/// Makes the agent stay resident by registering it to launch at user logon.
///
/// Note on "Windows Service": a classic service runs in session 0 and cannot draw the
/// overlay on the interactive desktop. The correct residency model for a desktop overlay
/// is a per-user logon agent, which is what this implements (HKCU\...\Run). It needs no
/// administrator rights and survives logoff/restart.
/// </summary>
public static class AutoStartManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ScreenSaverOverlay";

    private static string ExecutablePath =>
        Environment.ProcessPath ?? Application.ExecutablePath;

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        var value = key?.GetValue(ValueName) as string;
        return !string.IsNullOrEmpty(value);
    }

    public static void Enable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKey);
        // --background tells the agent to start minimized to the tray.
        key.SetValue(ValueName, $"\"{ExecutablePath}\" --background");
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is not null)
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    public static void Set(bool enabled)
    {
        if (enabled) Enable();
        else Disable();
    }
}
