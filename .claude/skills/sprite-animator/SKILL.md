---
name: sprite-animator
description: >-
  Create 2D character sprite-sheet animations from a concept image — generate the art with an
  image model (OpenAI gpt-image-2 / Gemini), keep the character 100% consistent across frames,
  then post-process into a packed sprite sheet + Aseprite-Hash index.json that the Screensaver
  Overlay can play. Use this skill whenever the user wants to make sprite animations, character
  animation frames, a sprite sheet, an animated overlay character/mascot, "스프라이트 애니메이션
  만들어", "캐릭터 스프라이트 시퀀스/시트 생성", "컨셉아트 분리해서 애니로 만들어", "오버레이에
  띄울 캐릭터 애니 만들어", or needs to generate image resources / sprite assets for the
  overlay — even if they don't say the word "sprite". Also use it for the image-generation step
  alone ("이미지 생성해줘", "gpt-image로 그려줘", "제미나이로 이미지") since that is how sprite
  frames are produced. Covers consistency fixes ("캐릭터 두 명 그려진 거 찾아", "스프라이트 일관성
  검수") and packing frames into a sheet for playback.
allowed-tools: Bash, Read, Write, Edit, Glob, Grep, Agent, WebSearch, WebFetch
---

# sprite-animator — concept art → playable sprite sheet

Produce a **2D sprite-sheet animation** of a character from a single concept image, with the
character's identity (face, hair, costume, props) staying consistent across every frame, and
pack the result into a sheet + `index.json` that the **Screensaver Overlay** renders as a
moving 2D sprite (a future `SpriteEffect : IEffect`).

Two capabilities, used together or alone:
1. **Image generation** — draw concept art and per-frame poses with OpenAI gpt-image-2 or Gemini.
2. **Sprite pipeline** — turn that art into a consistent, post-processed, packed sprite sheet.

This skill bundles the scripts; you orchestrate them. The deep methodology lives in two
reference files — read them when a step needs more than the summary here:
- `references/image-providers.md` — image CLI, the `.secret/*.json` key setup, dependencies.
- `references/sprite-pipeline.md` — the full 5-phase craft (success/failure/fix patterns).

---

## When to use this

Sprite/character animation, animated overlay mascots, sprite sheets, generating image resources
for the overlay — or just the image-generation step on its own. If the user wants a moving 2D
character in the screensaver, this is the resource-creation path.

## Prerequisites (one-time)

The image step needs an API key. Keys live in `.secret/{service}.json` at the repo root; the
repo ships only `.tmp` templates (real `.json` files are git-ignored). If `.secret/openai.json`
or `.secret/gemini.json` is missing, tell the user to set it up:

```bash
cp .secret/openai.json.tmp .secret/openai.json   # then paste the real api_key
cp .secret/gemini.json.tmp .secret/gemini.json
py -m pip install -r .claude/skills/sprite-animator/scripts/requirements-sprite.txt
py -m pip install google-genai                   # only if using Gemini
```

> **Windows: run scripts with `py`, never `python`** — the bare `python` is usually the MS Store
> stub that exits 49 silently and is miserable to debug in a background batch.

See `references/image-providers.md` for the secret JSON shapes and full CLI.

---

## The 5-phase flow

Each phase has a user checkpoint (✋). Don't blast through all of them silently — the cheap
checkpoints (box verification, pilot frame) prevent expensive batch mistakes.

```
Phase 1 analyze   concept art → character box(es) + per-character description
                  scripts/sprite-analyze.py → crops + preview grid   ✋ boxes OK?
Phase 2 pilot     ONE character via edit() (chroma-green, single char) ✋ consistent?
Phase 3 batch     N chars × M frames, background, skip-exists resume
Phase 4 fix-pass  diagnose head-dup / chroma residue / intrusion → re-call clean ✋ all single?
Phase 5 integrate sprite-postprocess.py process + assemble → sheet + master + index.json
```

**Consistency is the whole game.** The model keeps a character identical across frames only
when you feed a *reference* image into `edit()` (not `generate()`), inject an explicit
per-character description into every prompt, and — when a frame goes wrong — swap the reference
to a verified **clean frame of that same character**. Full patterns in `references/sprite-pipeline.md`.

Pick the scope to the motion: 1 frame for a catalog, 4 frames for a standard loop, 6–8 frames
for a character whose natural motion is the point. Validate style on the Phase-2 pilot before
spending the batch.

For a **multi-direction character** (e.g. an 8-way swimmer), have the concept stage produce an
**animation-aware model sheet** (front/side/back + a key action pose) and use *that* as the
`edit` reference — it locks the 3D form so directional frames stay consistent. Lay the swim sheet
out as `directions × kick-phases`. And **verify every action sheet, not just the first** — slice
each sheet by its JSON rects and eyeball it. Both patterns are in `references/sprite-pipeline.md`.

---

## Scripts (in `scripts/`)

| Script | Role |
|---|---|
| `image-gen.py` | `generate` / `edit` via `--provider openai|gemini` (see image-providers.md) |
| `providers/` | `openai_provider.py` (gpt-image-2, urllib-only), `gemini_provider.py` (seeded), `birefnet_provider.py` (high-quality matting) |
| `sprite-analyze.py` / `sprite-analyze-dance.py` | crop characters from concept art, emit a verification preview grid |
| `sprite-postprocess.py` | `process` (matte→crop→downscale→quantize→binarize), `assemble` (pack master + index.json), `evaluate` (3-axis quality score) |
| `requirements-sprite.txt` | Pillow, numpy, colorthief (+ optional torch/opencv for BiRefNet) |

Path logic in these scripts resolves the repo root from their own location, so they read
`.secret/` and write `image/` relative to this repo with no configuration.

Typical post-process (192×192 frames, padding 8):

```bash
py .claude/skills/sprite-animator/scripts/sprite-postprocess.py process \
  --character mascot --pose idle --raw-dir image/sprite/raw \
  --palette image/sprite/palette.json --output-dir design/sprite/output/mascot \
  --matting auto --target-size 192x192 --duration-ms 120

py .claude/skills/sprite-animator/scripts/sprite-postprocess.py assemble \
  --output-dir design/sprite/output --boxes image/sprite/character-boxes.json \
  --actions idle,play --target-size 192x192
```

---

## Output format & consuming in the overlay

The pipeline produces, per character:
- `{action}.png` — a horizontal strip of frames (cell = frame size + padding, e.g. 192+8).
- `{action}.json` — **Aseprite Hash** format: `frames` + `meta.frameTags/slug/size/scale`.
- a packed `_master/{master}.png` + `_master/index.json` catalog.

This is engine-ready (Phaser/Godot load it directly) and trivial to slice in a custom player:
frame rect = `index * (frameSize + padding)`, so a renderer needs no runtime JSON fetch.

**For the Screensaver Overlay (implemented pattern):** mirror the final sheets into
`src/ScreenSaverOverlay/Assets/sprites/{slug}/` (the `.csproj` copies `Assets/sprites/**` to the
build output) and add a small `manifest.json`:

```json
{ "slug": "diver2", "padding": 8, "swimDirections": 8, "swimKickPhases": 2,
  "actions": [ { "name": "swim", "sheet": "swim.png", "json": "swim.json",
                 "frames": 16, "frameWidth": 192, "frameHeight": 192, "durationMs": 110 }, … ] }
```

The reusable `SwimmingSpriteEffect : IEffect` base then plays it: it loads each action sheet via
`SpriteSheet.Load(png, json)` (frame rects from the Aseprite JSON), and a concrete character is
just a config record — `DiverSpriteEffect` (swim sheet = 8-dir "spin", 1 phase) and
`Diver2SpriteEffect` (swim sheet = 8-dir × 2 kick) differ only by `SpriteConfig`. While swimming
it picks the facing frame `dir*kickPhases + phase` where `dir = round(headingDeg/360*D) % D`
follows the character's heading and `phase` cycles over time; rare `hunt`/`flee` actions play
their sheets and a detached projectile is its own sprite. Because `IEffect` is renderer-agnostic,
the same sheets later work for a GPU/Direct2D renderer.

So a new character ships as **assets + one `SpriteConfig`** — no new effect logic. Keep frames on
a transparent (matted) background so they composite cleanly over the screensaver, and (per the
pipeline) **slice and eyeball every action sheet** before wiring it up.

---

## Anti-patterns

| Anti-pattern | Result | Do instead |
|---|---|---|
| `generate()` for every frame | costume/hair drifts frame to frame | `edit()` + reference + per-char desc |
| Vague desc ("a cheerleader") | model draws the neighbor character | name hair color, costume color, props |
| Tight character box | clipped foot/prop → model invents a 2nd character | ±15–20px padding |
| Reference frame contains a neighbor | duplication keeps reappearing | reference a verified **clean** frame of that character |
| Downscale to 48×48 then upscale 4× | mushy, detail lost | downscale raw → 192×192 with NEAREST |
| `assemble` without `--target-size` | master tiny (default 24) | pass `--target-size 192x192` |
| Commit a real key | leak | only `.secret/*.json.tmp` is committed; `.json` is git-ignored |
| Bare `python` on Windows | silent exit 49 in batch | use `py` |

---

## Provenance

Extracted from the `pencil-creator` project's `pencil-design` skill (image providers) and its
`sprite-animator` agent + `sprite-animation-craft` / `sprite-animation-flow` knowledge, where
the pipeline was validated on a 19-character orchestra, a 36-character dance troupe, and the
natural-motion `vocal-ex` set. Re-scoped here for generating overlay sprite assets, with the
ComfyUI local provider dropped (OpenAI + Gemini retained). Further validated in this repo on two
playable scuba divers — `diver` (8-direction spin) and `diver2` (animation-aware model sheet,
8-direction × 2-kick swim, harpoon + flee) — which drove the model-sheet, numeric-ordering,
uniform-alignment, and per-action-verification additions documented above.
