using System.Runtime.InteropServices;

namespace ScreenSaverOverlay.Service;

/// <summary>
/// Creates / removes a desktop shortcut (.lnk) to the agent. Uses the Windows Script Host
/// shell via late-bound COM so no extra COM reference is needed.
/// </summary>
public static class ShortcutManager
{
    public static string DesktopShortcutPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "Screensaver Overlay.lnk");

    public static bool DesktopShortcutExists() => File.Exists(DesktopShortcutPath);

    private static string ExecutablePath =>
        Environment.ProcessPath ?? Application.ExecutablePath;

    public static void CreateDesktopShortcut()
    {
        string exe = ExecutablePath;
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
            throw new InvalidOperationException("WScript.Shell is not available on this system.");

        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(DesktopShortcutPath);
            link.TargetPath = exe;
            link.Arguments = "--background";
            link.WorkingDirectory = Path.GetDirectoryName(exe) ?? "";
            link.Description = "Screensaver Overlay agent";
            link.IconLocation = exe + ",0";
            link.Save();
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    public static void RemoveDesktopShortcut()
    {
        if (DesktopShortcutExists())
            File.Delete(DesktopShortcutPath);
    }
}
