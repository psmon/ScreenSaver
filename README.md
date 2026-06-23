# Screensaver Overlay

<p align="right"><b>English</b> · <a href="README.ko.md">한국어</a></p>

<p align="center">
  <img src="home/screen_sample_1.jpg" alt="Screensaver Overlay running: Marine Aquarium 3 hosted underneath, with the live Claude Code console (top-left) and generated sprite characters swimming on top" width="100%">
</p>

<p align="center">
  <b>Your screensaver, alive with overlays.</b><br>
  A resident Windows agent hosts your chosen screensaver and paints animated layers on top of it —
  a live <b>Claude Code work console</b>, generated <b>sprite characters</b>, and GDI+ effects —
  all running together over the saver instead of killing it.
</p>

<p align="center">
  <i>Above: Marine Aquarium 3 hosted underneath · top-left = Claude Code's live status console (IPC)
  · the “SAM AI” mascot and scuba divers are sprite-sheet animations made with the bundled
  <a href="#sprite-animation-toolkit-sprite-animator-skill"><code>sprite-animator</code> skill</a>.</i>
</p>

---

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

## Sprite animation toolkit (`sprite-animator` skill)

The animated characters you see in the overlay (the scuba divers `diver` and `diver2`) are not
hand-drawn — they're produced by a **Claude Code skill that was developed in this repo**:
[`.claude/skills/sprite-animator/`](.claude/skills/sprite-animator/SKILL.md). It turns a single
concept image into a **playable 2D sprite-sheet animation**, keeping the character's identity
(face, hair, costume, props) consistent across every frame.

**Real output from this repo** — the `diver2` character below is a single concept image turned
into packed Aseprite sprite sheets that the overlay plays verbatim
([`src/ScreenSaverOverlay/Assets/sprites/diver2/`](src/ScreenSaverOverlay/Assets/sprites/diver2)):

<p align="center">
  <img src="src/ScreenSaverOverlay/Assets/sprites/diver2/swim.png" alt="diver2 swim sheet — 16 frames, 8 directions × 2 kick phases" width="100%"><br>
  <i><code>swim.png</code> — 16 frames = 8 directions × 2 kick phases (192×192 each). The effect picks
  the frame <code>dir*2 + phase</code> to face the diver's heading as she swims.</i>
</p>

<p align="center">
  <img src="src/ScreenSaverOverlay/Assets/sprites/diver2/hunt.png" alt="diver2 hunt sheet — 6 frames, harpoon fire" width="75%"><br>
  <i><code>hunt.png</code> — a 6-frame harpoon-fire action; the projectile detaches as its own sprite.
  The character stays identical to the swim frames — that consistency is the whole craft.</i>
</p>

It's a **self-contained, reusable skill** — you can drive it on its own to make sprite
animations for *any* project, not just this screensaver. Two capabilities, usable together or
alone:

1. **Image generation** — draw concept art and per-frame poses with OpenAI `gpt-image-2` or
   Gemini (`scripts/image-gen.py`, `--provider openai|gemini`).
2. **Sprite pipeline** — matte → crop → downscale → quantize, then pack into a horizontal strip
   sheet + an **Aseprite-Hash `index.json`** (`scripts/sprite-postprocess.py`).

The 5-phase flow (analyze → pilot → batch → fix-pass → integrate) and the consistency craft
(use `edit()` with a verified-clean reference frame + per-character descriptions) live in
[`references/sprite-pipeline.md`](.claude/skills/sprite-animator/references/sprite-pipeline.md)
and [`references/image-providers.md`](.claude/skills/sprite-animator/references/image-providers.md).

**Use it independently** — just invoke the skill (e.g. *"스프라이트 애니메이션 만들어"*,
*"컨셉아트 분리해서 애니로 만들어"*, or *"gpt-image로 그려줘"*) and point it at a concept image.
It needs an API key in `.secret/{openai,gemini}.json` (git-ignored; copy the shipped `.tmp`
templates) and `py -m pip install -r .claude/skills/sprite-animator/scripts/requirements-sprite.txt`.

**Output is engine-ready** (Phaser/Godot load the Aseprite JSON directly). To play a sheet in
*this* overlay, drop it under `src/ScreenSaverOverlay/Assets/sprites/{slug}/` with a small
`manifest.json`; the reusable `SwimmingSpriteEffect : IEffect` then plays it, so a new character
ships as **assets + one `SpriteConfig`** — no new effect code (see `DiverSpriteEffect` /
`Diver2SpriteEffect`).

## Roadmap

- Direct2D / Direct3D renderer for GPU-accelerated and 3D effects (the `IEffect` contract is
  already renderer-agnostic).
- More effects: particles, physics. *(Sprite characters — done, via the `sprite-animator`
  skill above.)*
- Multi-monitor per-screen effect placement. *(Per-monitor host + overlay targeting — done;
  each target screen gets its own screensaver host + overlay.)*
