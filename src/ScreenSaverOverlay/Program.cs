using ScreenSaverOverlay.Service;

namespace ScreenSaverOverlay;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Handle non-UI commands first so they can be scripted (e.g. installer / setup).
        foreach (var arg in args)
        {
            switch (arg.ToLowerInvariant())
            {
                case "--install":
                case "--register":
                    AutoStartManager.Enable();
                    return 0;
                case "--uninstall":
                case "--unregister":
                    AutoStartManager.Disable();
                    return 0;
            }
        }

        // Single instance: a second launch just exits (the resident agent keeps running).
        using var mutex = new Mutex(initiallyOwned: true, "ScreenSaverOverlay.SingleInstance", out bool isNew);
        if (!isNew)
            return 0;

        ApplicationConfiguration.Initialize();

        // --settings opens just the settings dialog (handy for quick edits / UI testing).
        if (args.Any(a => a.Equals("--settings", StringComparison.OrdinalIgnoreCase)))
        {
            var settings = Settings.AppSettings.Load();
            using var dlg = new Settings.SettingsForm(settings);
            dlg.SettingsSaved += (_, s) => s.Save();
            Application.Run(dlg);
            return 0;
        }

        Application.Run(new TrayAppContext());
        GC.KeepAlive(mutex);
        return 0;
    }
}
