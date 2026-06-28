using System.Drawing.Drawing2D;
using ScreenSaverOverlay.Ipc;
using ScreenSaverOverlay.Overlay;
using ScreenSaverOverlay.Screensaver;
using ScreenSaverOverlay.Service;
using ScreenSaverOverlay.Settings;

namespace ScreenSaverOverlay;

/// <summary>
/// The resident agent. Lives in the system tray. In hosted auto mode it detects idle itself,
/// hosts the chosen screensaver inside its own fullscreen window, and — after a short delay —
/// overlays the effect on top. When the user returns, it tears everything down and stands by.
/// While managing the session it disables Windows' own auto-screensaver to avoid a double
/// trigger, restoring it on exit.
/// </summary>
public sealed class TrayAppContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly IdleWatcher _idle;
    private readonly KeyboardHook _previewKeyHook = new();
    private readonly System.Windows.Forms.Timer _overlayDelay = new();

    // One overlay / host per target monitor (see AppSettings.ResolveTargetScreens).
    private readonly List<OverlayForm> _overlays = new();
    private readonly List<ScreenSaverHostForm> _hosts = new();
    // Opaque covers on the non-target monitors while locked (security filter).
    private readonly List<SecurityFilterForm> _filters = new();
    private LockScreenForm? _lockScreen;
    private AppSettings _settings;

    private bool _sessionActive;    // an automatic idle-triggered session is running
    private bool _lockedSession;     // the active session is a PIN-locked one
    private bool _hostedPreview;     // manual "host + overlay" test from the tray
    private bool _previewMode;       // manual overlay-only test from the tray/settings
    private bool _suppressingWinSaver; // we have Windows' auto-screensaver turned off

    private SettingsForm? _settingsForm;

    private ToolStripMenuItem _previewItem = null!;
    private ToolStripMenuItem _statusItem = null!;
    private ToolStripMenuItem _autoStartItem = null!;

    public TrayAppContext()
    {
        _settings = AppSettings.Load();

        // Listen for Claude Code status from the moment the agent starts, so the console has a
        // backlog ready when the screensaver kicks in (the UDP listener is idle and cheap).
        ClaudeStatusBus.Start();

        _tray = new NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Visible = true,
            Text = "Screensaver Overlay",
            ContextMenuStrip = BuildMenu(),
        };
        _tray.DoubleClick += (_, _) => OpenSettings();

        _overlayDelay.Tick += OnOverlayDelayElapsed;

        _idle = new IdleWatcher(_settings.IdleSeconds);
        _idle.IdleReached += OnIdleReached;
        _idle.ActivityResumed += OnActivityResumed;

        // A keypress dismisses any manual preview (the overlay never takes focus, so a global
        // hook is the only way to catch it). Installed only while a preview is up.
        _previewKeyHook.KeyPressed += OnPreviewKeyPressed;

        // Make sure we restore the user's Windows screensaver even on an unexpected shutdown.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RestoreWindowsSaver();

        ApplyAutoMode();
    }

    // ---- menu -------------------------------------------------------------

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        _statusItem = new ToolStripMenuItem("Status") { Enabled = false };
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(new ToolStripMenuItem("Settings…", null, (_, _) => OpenSettings()));

        _previewItem = new ToolStripMenuItem("Preview overlay only", null, (_, _) => TogglePreview())
        {
            CheckOnClick = false,
        };
        menu.Items.Add(_previewItem);

        menu.Items.Add(new ToolStripMenuItem("Preview WITH screensaver (hosted)",
            null, (_, _) => ToggleHostedPreview()));

        menu.Items.Add(new ToolStripSeparator());

        _autoStartItem = new ToolStripMenuItem("Start with Windows (service)", null, (_, _) => ToggleAutoStart())
        {
            CheckOnClick = false,
            Checked = AutoStartManager.IsEnabled(),
        };
        menu.Items.Add(_autoStartItem);

        menu.Items.Add(new ToolStripMenuItem("Create desktop shortcut", null, (_, _) => CreateDesktopShortcut()));

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitApp()));
        return menu;
    }

    private void UpdateStatus(string text)
    {
        _statusItem.Text = "● " + text;
        _tray.Text = "Screensaver Overlay — " + text;
    }

    // ---- auto mode (idle trigger + Windows saver handling) ----------------

    private void ApplyAutoMode()
    {
        if (_settings.HostedAutoMode)
        {
            // Take over from Windows' own auto-screensaver while we manage the session.
            WindowsScreenSaverControl.SetAutoActivate(false);
            _suppressingWinSaver = true;

            _idle.ThresholdSeconds = _settings.IdleSeconds;
            _idle.Start();
            UpdateStatus($"대기 중 — 유휴 {_settings.IdleSeconds}s 후 작동");
        }
        else
        {
            if (_sessionActive) StopSession();
            _idle.Stop();
            RestoreWindowsSaver();
            UpdateStatus("자동 모드 꺼짐");
        }
    }

    private void RestoreWindowsSaver()
    {
        if (_suppressingWinSaver)
        {
            WindowsScreenSaverControl.RestoreToUserPreference();
            _suppressingWinSaver = false;
        }
    }

    private void OnIdleReached(object? sender, EventArgs e)
    {
        if (_previewMode || _hostedPreview || _sessionActive) return;
        StartSession();
    }

    private void OnActivityResumed(object? sender, EventArgs e)
    {
        if (!_sessionActive) return;

        // A locked session does not tear down on activity — it demands the PIN first.
        if (_lockedSession)
            PresentLockScreen();
        else
            StopSession();
    }

    /// <summary>Bring up (or re-focus) the PIN screen; the correct PIN ends the session.</summary>
    private void PresentLockScreen()
    {
        if (_lockScreen is { IsDisposed: false })
        {
            _lockScreen.Present();
            return;
        }

        var screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
        _lockScreen = new LockScreenForm(_settings, screen.Bounds);
        _lockScreen.Unlocked += (_, _) => StopSession();
        _lockScreen.Present();
        UpdateStatus("잠금 — PIN 입력 대기");
    }

    // ---- automatic session (host + delayed overlay) -----------------------

    private void StartSession()
    {
        _sessionActive = true;
        // A session locks only when the user enabled it AND set a PIN (otherwise it could
        // never be unlocked). Manual previews are never locked.
        _lockedSession = _settings.LockEnabled && _settings.HasPin;

        var scr = _settings.ResolveScreenSaverPath();
        bool hosted = !string.IsNullOrEmpty(scr) && File.Exists(scr);

        if (_lockedSession)
        {
            // Cover every non-target monitor with the security filter so no desktop content
            // stays visible on the other screens while locked.
            StartFilters(_settings.ResolveSecurityScreens());
            // Without a hosted .scr the target monitors carry only a click-through overlay,
            // which would leave the desktop reachable — back them with an opaque filter too.
            if (!hosted)
                StartFilters(_settings.ResolveTargetScreens());
        }

        if (hosted)
        {
            // Host the chosen screensaver — one window per target monitor.
            StartHosts(scr!);

            // The overlay joins after the configured delay (default 10s).
            int delayMs = Math.Max(0, _settings.StartDelaySeconds) * 1000;
            if (delayMs == 0)
            {
                ShowOverlays();
            }
            else
            {
                _overlayDelay.Interval = delayMs;
                _overlayDelay.Start();
                UpdateStatus($"화면보호기 작동 — {_settings.StartDelaySeconds}s 후 오버레이");
            }
        }
        else
        {
            // No screensaver available -> overlay only.
            ShowOverlays();
            UpdateStatus("오버레이 작동 (화면보호기 없음)");
        }
    }

    private void OnOverlayDelayElapsed(object? sender, EventArgs e)
    {
        _overlayDelay.Stop();
        if (_sessionActive)
        {
            ShowOverlays();
            // If the user already returned and the PIN prompt is up, the freshly-shown overlay
            // would otherwise render its sprites over the prompt — keep the prompt on top.
            if (_lockScreen is { IsDisposed: false })
                _lockScreen.Present();
            else
                UpdateStatus("화면보호기 + 오버레이 작동 중");
        }
    }

    private void StopSession()
    {
        _sessionActive = false;
        _lockedSession = false;
        _overlayDelay.Stop();
        CloseLockScreen();
        StopOverlays();
        StopHosts();
        StopFilters();
        UpdateStatus($"대기 중 — 유휴 {_settings.IdleSeconds}s 후 작동");
    }

    private void CloseLockScreen()
    {
        if (_lockScreen is { IsDisposed: false })
        {
            _lockScreen.Close();
            _lockScreen.Dispose();
        }
        _lockScreen = null;
    }

    // ---- overlay / host helpers (one per target monitor) ------------------

    private void ShowOverlays()
    {
        StopOverlays();
        foreach (var screen in _settings.ResolveTargetScreens())
        {
            var overlay = new OverlayForm(_settings, screen.Bounds);
            overlay.Start();
            _overlays.Add(overlay);
        }
    }

    private void StopOverlays()
    {
        foreach (var overlay in _overlays)
        {
            overlay.Stop();
            overlay.Dispose();
        }
        _overlays.Clear();
    }

    private void StartHosts(string scr)
    {
        StopHosts();
        foreach (var screen in _settings.ResolveTargetScreens())
        {
            var host = new ScreenSaverHostForm(scr, screen.Bounds);
            host.Start();
            _hosts.Add(host);
        }
    }

    private void StopHosts()
    {
        foreach (var host in _hosts)
        {
            host.Stop();
            host.Dispose();
        }
        _hosts.Clear();
    }

    private void StartFilters(IEnumerable<Screen> screens)
    {
        foreach (var screen in screens)
        {
            var filter = new SecurityFilterForm(screen.Bounds, _settings.SecondaryWallpaperPath);
            filter.Start();
            _filters.Add(filter);
        }
    }

    private void StopFilters()
    {
        foreach (var filter in _filters)
        {
            filter.Stop();
            filter.Dispose();
        }
        _filters.Clear();
    }

    // ---- manual previews --------------------------------------------------

    private void TogglePreview()
    {
        _previewMode = !_previewMode;
        _previewItem.Checked = _previewMode;
        if (_previewMode)
        {
            ShowOverlays();
            UpdateStatus("오버레이 미리보기 (아무 키나 누르면 종료)");
        }
        else
        {
            StopOverlays();
            UpdateStatus($"대기 중 — 유휴 {_settings.IdleSeconds}s 후 작동");
        }
        UpdatePreviewKeyHook();
    }

    private void ToggleHostedPreview()
    {
        if (_hostedPreview)
        {
            _hostedPreview = false;
            StopOverlays();
            StopHosts();
            UpdateStatus($"대기 중 — 유휴 {_settings.IdleSeconds}s 후 작동");
            UpdatePreviewKeyHook();
            return;
        }

        var scr = _settings.ResolveScreenSaverPath();
        if (string.IsNullOrEmpty(scr) || !File.Exists(scr))
        {
            MessageBox.Show("호스팅할 화면보호기를 찾지 못했습니다. Settings에서 화면보호기를 선택하세요.",
                "Screensaver Overlay", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _hostedPreview = true;
        StartHosts(scr);
        ShowOverlays();
        UpdateStatus("호스팅 미리보기 (아무 키나 누르면 종료)");
        UpdatePreviewKeyHook();
    }

    // ---- preview dismissal (keyboard) -------------------------------------

    /// <summary>Keep the keyboard hook installed exactly while a manual preview is up.</summary>
    private void UpdatePreviewKeyHook()
    {
        if (_previewMode || _hostedPreview)
            _previewKeyHook.Install();
        else
            _previewKeyHook.Uninstall();
    }

    private void OnPreviewKeyPressed(object? sender, EventArgs e)
    {
        // Tear down whichever preview is running; both toggles flip their flag off and
        // refresh the hook via UpdatePreviewKeyHook.
        if (_previewMode) TogglePreview();
        else if (_hostedPreview) ToggleHostedPreview();
    }

    // ---- residency / shortcut --------------------------------------------

    private void ToggleAutoStart()
    {
        bool enable = !AutoStartManager.IsEnabled();
        try
        {
            AutoStartManager.Set(enable);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not change auto-start: " + ex.Message,
                "Screensaver Overlay", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _settings.AutoStart = enable;
        _settings.Save();
        _autoStartItem.Checked = enable;

        _tray.ShowBalloonTip(2500, "Screensaver Overlay",
            enable ? "로그인 시 자동 시작이 켜졌습니다." : "로그인 시 자동 시작이 꺼졌습니다.",
            ToolTipIcon.Info);
    }

    private void SyncAutoStartItem() => _autoStartItem.Checked = AutoStartManager.IsEnabled();

    private void CreateDesktopShortcut()
    {
        try
        {
            ShortcutManager.CreateDesktopShortcut();
            _tray.ShowBalloonTip(2500, "Screensaver Overlay",
                "바탕화면에 바로가기를 만들었습니다.", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not create desktop shortcut: " + ex.Message,
                "Screensaver Overlay", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ---- settings ---------------------------------------------------------

    private void OpenSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Activate();
            return;
        }

        _settingsForm = new SettingsForm(_settings);
        _settingsForm.LivePreviewRequested += (_, s) =>
        {
            _settings = s;
            _previewMode = true;
            _previewItem.Checked = true;
            ShowOverlays();
            UpdateStatus("오버레이 미리보기 (아무 키나 누르면 종료)");
            UpdatePreviewKeyHook();
        };
        _settingsForm.SettingsSaved += (_, s) =>
        {
            _settings = s;
            SyncAutoStartItem();
            ApplyAutoMode(); // pick up idle-timeout / auto-mode changes
            // Rebuild any live overlays so new settings (incl. monitor target) take effect.
            if (_overlays.Count > 0)
                ShowOverlays();
        };
        _settingsForm.FormClosed += (_, _) => _settingsForm = null;
        _settingsForm.Show();
        _settingsForm.Activate();
    }

    // ---- shutdown ---------------------------------------------------------

    private void ExitApp()
    {
        _overlayDelay.Stop();
        _idle.Stop();
        _idle.Dispose();
        _previewKeyHook.Dispose();
        RestoreWindowsSaver();
        CloseLockScreen();
        StopHosts();
        StopOverlays();
        StopFilters();
        _tray.Visible = false;
        _tray.Dispose();
        ExitThread();
    }

    // ---- tray icon (generated, no .ico asset needed) ----------------------

    private static Icon CreateTrayIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bg = new SolidBrush(Color.FromArgb(30, 30, 40));
            g.FillEllipse(bg, 1, 1, 30, 30);
            using var dot = new SolidBrush(Color.FromArgb(80, 200, 255));
            g.FillEllipse(dot, 9, 9, 14, 14);
        }
        IntPtr hIcon = bmp.GetHicon();
        return (Icon)Icon.FromHandle(hIcon).Clone();
    }
}
