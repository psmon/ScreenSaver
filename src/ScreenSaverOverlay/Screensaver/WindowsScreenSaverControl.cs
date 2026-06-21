using Microsoft.Win32;
using ScreenSaverOverlay.Native;

namespace ScreenSaverOverlay.Screensaver;

/// <summary>
/// Controls whether Windows auto-launches its own screensaver. In hosted mode the agent
/// drives the screensaver itself, so it temporarily switches Windows' auto-activation off
/// (to avoid a double trigger) and restores the original state on exit.
///
/// Changes are made in-memory only (fWinIni = 0), so they are scoped to the session and a
/// crash can't permanently alter the user's saved preference.
/// </summary>
public static class WindowsScreenSaverControl
{
    public static bool IsAutoActivateEnabled()
    {
        int active = 0;
        NativeMethods.SystemParametersInfoW(NativeMethods.SPI_GETSCREENSAVEACTIVE, 0, ref active, 0);
        return active != 0;
    }

    public static void SetAutoActivate(bool enabled)
    {
        NativeMethods.SystemParametersInfoW(
            NativeMethods.SPI_SETSCREENSAVEACTIVE,
            (uint)(enabled ? 1 : 0),
            IntPtr.Zero,
            0); // no SPIF_UPDATEINIFILE -> session-only, does not overwrite saved setting
    }

    /// <summary>
    /// The user's durable screensaver preference from the registry. Because we only ever
    /// change the in-memory state (never the registry), this always reflects what the user
    /// actually wants — so restoring from it is correct even after an unclean shutdown.
    /// </summary>
    public static bool SavedUserPreference()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            var raw = key?.GetValue("ScreenSaveActive") as string;
            return raw is null || raw != "0";
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Restore Windows' auto-screensaver to the user's saved preference.</summary>
    public static void RestoreToUserPreference() => SetAutoActivate(SavedUserPreference());
}
