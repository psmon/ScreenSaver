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
    private readonly System.Windows.Forms.Timer _overlayDelay = new();

    private OverlayForm? _overlay;
    private ScreenSaverHostForm? _host;
    private AppSettings _settings;

    private bool _sessionActive;    // an automatic idle-triggered session is running
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
        if (_sessionActive)
            StopSession();
    }

    // ---- automatic session (host + delayed overlay) -----------------------

    private void StartSession()
    {
        _sessionActive = true;

        var scr = _settings.ResolveScreenSaverPath();
        bool hosted = !string.IsNullOrEmpty(scr) && File.Exists(scr);

        if (hosted)
        {
            // Host the chosen screensaver inside our own fullscreen window.
            _host = new ScreenSaverHostForm(scr!);
            _host.Start();

            // The overlay joins after the configured delay (default 10s).
            int delayMs = Math.Max(0, _settings.StartDelaySeconds) * 1000;
            if (delayMs == 0)
            {
                ShowOverlay();
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
            ShowOverlay();
            UpdateStatus("오버레이 작동 (화면보호기 없음)");
        }
    }

    private void OnOverlayDelayElapsed(object? sender, EventArgs e)
    {
        _overlayDelay.Stop();
        if (_sessionActive)
        {
            ShowOverlay();
            UpdateStatus("화면보호기 + 오버레이 작동 중");
        }
    }

    private void StopSession()
    {
        _sessionActive = false;
        _overlayDelay.Stop();
        _overlay?.Stop();
        _host?.Stop();
        _host?.Dispose();
        _host = null;
        UpdateStatus($"대기 중 — 유휴 {_settings.IdleSeconds}s 후 작동");
    }

    // ---- overlay helpers --------------------------------------------------

    private void ShowOverlay()
    {
        EnsureOverlay();
        _overlay!.ApplySettings(_settings);
        _overlay.Start();
    }

    private void EnsureOverlay()
    {
        if (_overlay is { IsDisposed: false }) return;
        _overlay = new OverlayForm(_settings);
    }

    // ---- manual previews --------------------------------------------------

    private void TogglePreview()
    {
        _previewMode = !_previewMode;
        _previewItem.Checked = _previewMode;
        if (_previewMode)
        {
            ShowOverlay();
            UpdateStatus("오버레이 미리보기");
        }
        else
        {
            _overlay?.Stop();
            UpdateStatus($"대기 중 — 유휴 {_settings.IdleSeconds}s 후 작동");
        }
    }

    private void ToggleHostedPreview()
    {
        if (_hostedPreview)
        {
            _hostedPreview = false;
            _overlay?.Stop();
            _host?.Stop();
            _host?.Dispose();
            _host = null;
            UpdateStatus($"대기 중 — 유휴 {_settings.IdleSeconds}s 후 작동");
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
        _host = new ScreenSaverHostForm(scr);
        _host.Start();
        ShowOverlay();
        UpdateStatus("호스팅 미리보기 (화면보호기 + 오버레이)");
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
            ShowOverlay();
            UpdateStatus("오버레이 미리보기");
        };
        _settingsForm.SettingsSaved += (_, s) =>
        {
            _settings = s;
            SyncAutoStartItem();
            ApplyAutoMode(); // pick up idle-timeout / auto-mode changes
            if (_overlay is { IsDisposed: false })
                _overlay.ApplySettings(_settings);
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
        RestoreWindowsSaver();
        _host?.Stop();
        _host?.Dispose();
        _overlay?.Stop();
        _overlay?.Dispose();
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
