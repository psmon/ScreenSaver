# Sprite pipeline — concept art → consistent sprite sheet

The deep methodology behind the 5-phase flow. Distilled from a real run that produced a
19-character pixel orchestra and a 36-character pixel dance troupe, plus the natural-motion
`vocal-ex` set. It captures the success patterns, the failure patterns, and the fixes — read
the relevant section when a phase needs more than the SKILL.md summary.

The north star: **a character's identity (face, hair, costume, props) must stay 100%
consistent across every frame.** Everything below serves that.

---

## Input modes

| Mode | Input | Rounds | Output | When |
|---|---|---|---|---|
| Simple (single-pose) | 1 concept art | 1 × N chars | 1-frame sheet + JSON | character maps, catalogs |
| Full (multi-frame) | concept art + verified reference | 2 (single-pose → cycle) | 4-frame sheet + master + index | game/screensaver assets |
| Rich (natural motion) | real video motion analysis + concept | concept + N frames | variable-length sheets (idle 6f + play 8f…) | natural-motion hero characters |

The key consistency trick: **reuse a verified simple-pose output as the reference for the full
multi-frame round.** Once a character's identity is locked in one good frame, feed that frame
back as the `edit` reference for all subsequent frames.

**Frame count follows motion complexity.** 4 frames is the default loop. Simple loops
(playing an instrument) are fine at 4f; a phrase with intro→build→climax→finish reads better at
6–8f. More frames = more generation calls = more cost, so validate style on a pilot first.

---

## Phase 1 — analyze (concept art → boxes + descriptions)

**Box coordinates via std-based row/column detection.** Naive even splits (6 rows × 150px)
drift 60–100px on lower rows and bleed into the neighbor. Instead scan luminance variance:

```python
char_area = arr[:, 600:1500, :]              # strip the left label panel
gray = char_area.mean(axis=2)
row_std = gray.std(axis=1)
row_std_s = np.convolve(row_std, np.ones(8)/8, mode='same')
# std < 10 → gap between rows ; std > 35 → a character line. Detect the boundaries.
```

Run `scripts/sprite-analyze.py` to crop per-character boxes and emit a preview grid with the
boxes overlaid, then **visually verify** there's no overlap and each character is fully inside
its box (✋ user checkpoint).

**Every character needs an explicit description** so the model locks onto the centered subject
even if a neighbor bleeds in. Store it in `character-boxes.json` `desc` — it is injected into
every prompt in Phases 2/3/4.

- ✅ `"girl with brown hair in twin tails with ribbons, white and red cheerleading uniform, holding white pom-poms, bright smile"`
- ❌ `"a cheerleader"` (the model may draw the purple dancer one row down)

**Box padding ±15–20px.** Too tight clips a hat/foot/instrument, and the model "completes" the
clipped part by inventing a second character.

---

## Phase 2 — pilot (one character first)

Before generating all N characters, generate **exactly one** via `edit()` and visually verify
costume/hair/pose consistency. Only proceed to the batch when the pilot scores ≥70 or looks
clearly right (✋). If it drifts, go back to Phase 1 and fix the box/desc.

**Gemini path:** `gemini-3.1-flash-image-preview`, pass the reference via `input_image`, force a
chroma-green `#00FF00` background and a single character, seed = `hash(slug) % 2^32`.

**OpenAI gpt-image-2 path (no seed param):** lock consistency with a fixed concept reference:
1. `generate` once → a character concept sheet.
2. Use that sheet as the `edit` `input_image` reference, frozen, for every frame.
3. Prompt: `redraw THIS exact character … as ONE single full-body sprite, on solid #00FF00
   green` + `no other characters, no thumbnails` (stops concept-sheet thumbnails from leaking in).

**Palette caution for modern/colorful characters:** don't force a global earth-tone palette —
it distorts idol navy/red/yellow/denim. Extract a per-character 48-color adaptive palette
(`PIL.quantize(colors=48, MAXCOVERAGE)`) and save it as `image/sprite/{palettes}/{slug}.json`
for the post-process step. Concept art with an obvious original palette → global palette;
colorful new characters → per-character.

---

## Phase 3 — batch (many calls, background)

N chars × M frames is dozens to hundreds of calls (e.g. 36×4 = 144, 19×8 = 152), ~7–30 min.
Run it in the background. Two load-bearing patterns:

- **skip-exists**: a frame whose file already exists is skipped, so re-running the same script
  only retries the missing/failed frames.
- **per-call log** (`tmp-batch-call.log.json`): write each result so an interrupted run resumes.

Transient model misses (`'NoneType' object has no attribute 'save'`) are normal — just re-run
the script and it backfills only the gaps.

Cost reference (2026-06): single-pose ≈ $0.039/call; 4-frame ≈ $0.156/character.

---

## Phase 4 — fix-pass (kill artifacts)

Three artifacts to diagnose:

1. **Head/character duplication** — same hat/costume twice, or a small extra figure beside the
   character. Visual cue is decisive; an alpha-bbox width ≥1.6× the other frames is a flag.
2. **chroma-green residue** — leftover green pixels after matting (HSV H∈[80,140] & S>0.4, count >10).
3. **Neighbor intrusion** — non-character pixels (alpha>0) in the outer 10% left/right margins.

**Round 1 — emphasis prompt** (keep the same reference, strengthen the wording):
```
⚠️ STRICTLY ONE CHARACTER. Do NOT draw two people, do NOT duplicate the character,
do NOT include any adjacent dancers or background figures. Exactly one isolated
character centered.
```
~70% of cases fix here.

**Round 2 — clean frame as reference** (the key insight): if the *reference itself* contains a
neighbor, the model keeps copying it. Swap the reference to a **verified clean frame of the same
character** (e.g. `…-play-f1.png` when f1 is good) and name the cycle position in the prompt
("follow-through, lowering instrument slightly"). This fixes essentially 100%.

For chroma residue, re-calling with a clean reference also clears the green — that's preferred
over fancier matting. Escalate to BiRefNet matting only if needed.

---

## Phase 5 — integrate (sheet + master + index)

Post-process with `scripts/sprite-postprocess.py`:

```bash
# per character+action: matte → bbox crop → nearest downscale → quantize → binarize alpha
py .../sprite-postprocess.py process \
  --character vox7-1 --pose idle --raw-dir image/sprite/raw \
  --palette image/sprite/palette.json --output-dir design/sprite/output/vox7-1 \
  --matting auto --target-size 192x192 --duration-ms 120

# assemble all characters into a packed master.png + index.json
py .../sprite-postprocess.py assemble \
  --output-dir design/sprite/output --boxes image/sprite/character-boxes.json \
  --actions idle,play --target-size 192x192

# (optional) Case-S 3-axis quality score for one character dir
py .../sprite-postprocess.py evaluate \
  --target design/sprite/output/vox7-1 --palette image/sprite/palette.json
```

Critical details:
- **Frames play in numeric order, not lexicographic.** `process` sorts raw files by the numeric
  `-fN` index, so f10/f11 follow f9 (not f1). If you pack frames yourself, sort the same way —
  a plain string sort silently scrambles any animation with ≥10 frames (f10 lands after f1).
- **Use `--align uniform` for multi-frame animation** (the default). It scales every frame by the
  *same* factor and centers it, so the character keeps a stable size/position as it plays. The
  legacy `--align fill` scales each frame to its own bbox — right for a single-pose catalog, but
  it makes an animation jitter and "jump" (looks like overlapping/multiple characters).
- For a directional swim sheet (8 dir × 2 kick), the **up-kick / "fins together" prompt often
  makes the model draw a motion sequence (multiple figures)**. Reference a verified clean
  single-character frame, prompt a *minimal* change, and if it still duplicates, fall back to
  reusing the clean down-kick frame for that direction.
- **Downscale raw (~800px) directly to 192×192**, not to 48×48 then upscale — detail survives
  only with a direct nearest-neighbor downscale (`Image.resize((W,H), Image.NEAREST)`).
- **Scale padding with frame size** (48→2, 192→8) so any CSS/runtime ratio stays constant.
- Output is **Aseprite Hash JSON** (`frames`, `meta.frameTags/slug/size/scale`) — loads directly
  in Phaser/Godot, and is trivial to slice in a custom player (compute frame rects from frame
  size + padding; no runtime JSON fetch needed for `file://`).

Sheet/master sizing example (orchestra, 19 × idle+play 4f): per-action sheet 808×208 (cell
192+padding 8), master is a first-frame catalog.

---

## Output layout

```
image/sprite/
  {name}-concept.png                   # original concept art
  character-boxes.json                 # boxes + per-character desc
  crops/{slug}.png                     # concept-art crops (Phase 1)
  raw/{slug}-{action}-fN.png           # model rounds (Phase 2/3)
design/sprite/output/
  {slug}/{action}.{png,json}           # post-processed per-character sheets
  _master/{master}.png, index.json     # packed catalog + entry point
```

For the screensaver, mirror the final sheets + `index.json` into a resource folder the app
bundles (e.g. `src/ScreenSaverOverlay/Assets/sprites/`). See SKILL.md §"Consuming in the overlay".

---

## Failure catalog (quick reference)

| Symptom | Cause | Cure |
|---|---|---|
| Costume differs from concept | box coords off | std row detection, ±15px padding |
| Costume changes between frames | `generate` (no reference) | `edit` + per-char desc + seed/concept lock |
| Two characters in one frame | box bleed + weak prompt | "STRICTLY ONE CHARACTER" + clean-frame reference |
| chroma-green residue | HSV key threshold too low | re-call with clean reference (cleanest) |
| Pixel grid looks mushy | no nearest downscale | `Image.resize((W,H), Image.NEAREST)` |
| Sheet ratio broken | padding not proportional | padding = 2 × target_w / 48 |
| master.png too small | `assemble` default size 24 | pass `--target-size 192x192` |
| animation plays frames out of order (≥10 frames) | lexicographic sort (f10 after f1) | `process` sorts by numeric `-fN` index |
| character jitters / looks like multiple overlapping | per-frame bbox fill scaling | `--align uniform` (uniform scale + center anchor) |
| up-kick frame has two characters | "fins together" reads as a motion sequence | clean single-frame reference + minimal change, else reuse down-kick |
