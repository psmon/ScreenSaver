using System.Diagnostics;
using Microsoft.Win32;

namespace ScreenSaverOverlay.Screensaver;

/// <summary>One installed screensaver (.scr) the user can pick from.</summary>
public sealed record ScreenSaverInfo(string DisplayName, string Path)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// Discovers installed screensavers (.scr) so the user can choose which one to host.
/// Looks in the standard system folders and resolves friendly names from each file's
/// version info. Also reports the screensaver currently registered in Windows.
/// </summary>
public static class ScreenSaverCatalog
{
    private static string[] SearchDirs =>
        new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System)),        // System32
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86)),     // SysWOW64
        };

    public static IReadOnlyList<ScreenSaverInfo> Enumerate()
    {
        var byKey = new Dictionary<string, ScreenSaverInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in SearchDirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in SafeEnumerateScr(dir))
            {
                // De-dupe System32/SysWOW64 copies by file name; prefer System32 (first).
                var key = Path.GetFileName(file);
                if (!byKey.ContainsKey(key))
                    byKey[key] = new ScreenSaverInfo(FriendlyName(file), file);
            }
        }

        return byKey.Values.OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static IEnumerable<string> SafeEnumerateScr(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*.scr"); }
        catch { return Array.Empty<string>(); }
    }

    private static string FriendlyName(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var desc = info.FileDescription;
            if (!string.IsNullOrWhiteSpace(desc))
                return $"{desc.Trim()}  ({Path.GetFileName(path)})";
        }
        catch { /* fall through */ }
        return Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>Full path of the screensaver currently registered in Windows, or null.</summary>
    public static string? CurrentRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            var raw = key?.GetValue("SCRNSAVE.EXE") as string;
            if (string.IsNullOrWhiteSpace(raw)) return null;

            // The registry may hold a short (8.3) path like MARINE~1.SCR — expand it.
            var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(raw));
            return ResolveLongPath(full);
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveLongPath(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (dir is null || !Directory.Exists(dir)) return path;
            // Match the real file by comparing against enumerated long names.
            var name = Path.GetFileName(path);
            foreach (var f in Directory.EnumerateFiles(dir, "*.scr"))
            {
                if (string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase))
                    return f;
            }
            // Short name didn't match a long name directly; let the OS resolve via a file handle.
            if (File.Exists(path))
                return new FileInfo(path).FullName;
        }
        catch { /* ignore */ }
        return path;
    }
}
