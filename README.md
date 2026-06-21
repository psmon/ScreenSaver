# Screensaver Overlay

A resident Windows agent that, when the PC goes idle, **hosts your chosen screensaver inside
its own window and paints an animated overlay on top of it** — so the screensaver and the
overlay run together. When you return, both tear down and the agent stands by.

Built with **.NET 10** (WinForms, `net10.0-windows`).

## Why "hosted"?

Windows screensavers terminate the instant they lose input focus (confirmed in the
[`DefScreenSaverProc` docs](https://learn.microsoft.com/en-us/windows/win32/api/scrnsave/nf-scrnsave-defscreensaverproc):
*"Closes the screen saver if … losing the input focus"*). So you **cannot** reliably float a
window over the OS-launched screensaver — the moment the overlay appears, the saver dies.

Instead, the agent runs the screensaver itself: it detects idle, opens its own fullscreen
window, launches the chosen `.scr` in preview mode (`scr /p <hwnd>`) parented into that
window, and draws the overlay on top. Because the agent owns the foreground, the saver never
loses focus and never self-terminates. (This is the technique multi-monitor tools like
DisplayFusion use, and it's verified working with **Marine Aquarium 3**.)

## Features

- **Tray agent** — runs quietly in the system tray.
- **Idle trigger** — `GetLastInputInfo`; after the configured idle time (default 60 s) the
  hosted session starts; the overlay joins after a short delay (default 10 s).
- **Pick your screensaver** — any installed `.scr` (Marine Aquarium 3, Bubbles, Mystify,
  Photos, Ribbons, …) or browse to a custom one.
- **Coexistence, not conflict** — the saver is hosted as our child; Windows' own
  auto-screensaver is suppressed while the agent runs (session-only) and restored from your
  saved registry preference on exit, so a crash can't permanently change your setting.
- **Click-through layered overlay** — topmost, per-pixel alpha, never steals focus.
- **Settings window** — screensaver, effect, count, size, speed, opacity, color, idle time,
  overlay delay, auto-mode toggle.
- **Stay resident** — register to launch at logon (per-user, no admin needed).

## Build & run

```powershell
dotnet build -c Release
dotnet run --project src/ScreenSaverOverlay
```

The app starts minimized to the tray. Right-click the tray icon:

- **Settings…** — configure the effect (the **Preview** button shows it live).
- **Preview overlay** — toggle the overlay immediately without waiting for the screensaver.
- **Exit**.

## Stay resident (auto-start at logon)

From the Settings window tick **"Start with Windows"**, or from the command line:

```powershell
# register / unregister the logon agent (writes HKCU\...\Run, no admin required)
ScreenSaverOverlay.exe --install
ScreenSaverOverlay.exe --uninstall
```

### Why a logon agent and not a classic Windows Service?

A classic service runs in **session 0** and cannot draw on the interactive desktop, so it
cannot show the overlay. The correct residency model for a desktop overlay is a per-user
**logon agent**, which is what `--install` sets up. It needs no administrator rights and
survives logoff/restart.

## Testing

- **Instant, no waiting:** right-click the tray icon →
  **"Preview WITH screensaver (hosted)"** — the chosen screensaver fills the screen with the
  overlay on top. Click again to stop. (**"Preview overlay only"** shows just the effect.)
- **Full auto flow:** in Settings lower **Idle to start** (e.g. 10 s) and **Overlay delay**
  (e.g. 3 s), then leave the machine idle. The screensaver starts, the overlay joins after
  the delay, and moving the mouse ends both.

## Project layout

```
src/ScreenSaverOverlay/
  Program.cs                       entry point, CLI args, single-instance
  TrayAppContext.cs                resident agent: idle trigger, hosted session, standby
  Native/NativeMethods.cs          P/Invoke (layered window, GetLastInputInfo, SPI, GDI)
  Overlay/OverlayForm.cs           layered click-through window + UpdateLayeredWindow blit
  Overlay/ScreenSaverHostForm.cs   hosts the chosen .scr via /p and overlays on top
  Effects/IEffect.cs               renderer-agnostic effect contract
  Effects/BouncingCircleEffect.cs  first GDI+ sample effect
  Effects/EffectRegistry.cs        effect catalog (add new effects here)
  Screensaver/IdleWatcher.cs       GetLastInputInfo idle detection (the trigger)
  Screensaver/ScreenSaverCatalog.cs   discovers installed .scr files
  Screensaver/WindowsScreenSaverControl.cs  suppress/restore Windows' auto-screensaver
  Screensaver/ScreensaverWatcher.cs   (legacy) observes OS screensaver start/stop
  Settings/AppSettings.cs          JSON settings in %AppData%\ScreenSaverOverlay
  Settings/SettingsForm.cs         settings dialog
  Service/AutoStartManager.cs      logon-agent registration
  Service/ShortcutManager.cs       desktop shortcut creation
```

## Claude Code live monitor (IPC)

The `claude-console` effect renders a terminal-style panel in the top-left that shows **Claude
Code's live work status** while the screensaver runs. The path is:

```
Claude Code hook (account-wide) → claude-status-hook.ps1 → UDP 127.0.0.1:47921
   → ClaudeStatusBus (in the agent) → ClaudeConsoleEffect (top-left console)
```

UDP is deliberate: the hook fires a datagram and returns immediately (configured `async:true`),
so it never blocks Claude Code, and if the agent isn't running the packet is just dropped.

**Enable (account-wide):** install the hook script and add the `hooks` block to
`%USERPROFILE%\.claude\settings.json` for the events you want streamed — `UserPromptSubmit`,
`PreToolUse`, `PostToolUse`, `Notification`, `Stop`, `SessionStart`, `SessionEnd`. Each runs:

```json
{ "type": "command", "command": "powershell",
  "args": ["-NoProfile", "-File", "C:\\Users\\<you>\\.claude\\claude-status-hook.ps1"],
  "async": true, "timeout": 5 }
```

The canonical script lives at `tools/ipc/claude-status-hook.ps1` (copy it to `~/.claude/`).
**Disable:** remove the `hooks` block from `~/.claude/settings.json` (a `.bak` backup is kept).
Then add the **"Claude Code Monitor (console)"** effect as an overlay layer in Settings.

## Roadmap

- Direct2D / Direct3D renderer for GPU-accelerated and 3D effects (the `IEffect` contract is
  already renderer-agnostic).
- More effects: particles, sprites/characters, physics.
- Multi-monitor per-screen effect placement.
