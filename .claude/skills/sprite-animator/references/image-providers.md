# Image generation — OpenAI gpt-image-2 & Gemini

The sprite pipeline needs an image model to draw concept art and per-frame poses. This skill
bundles `scripts/image-gen.py`, a tiny CLI with a provider abstraction. Two cloud providers
are wired up: **OpenAI gpt-image-2** and **Gemini**. Both support `generate` (text→image) and
`edit` (image+prompt→image). `edit` is what keeps a character consistent across frames.

## 1. Secret setup (one-time)

API keys live in `.secret/{service}.json` at the **repo root**. The repo ships only `.tmp`
templates; the real `.json` files are git-ignored so keys never get committed.

```bash
# from repo root — copy the template, then paste the real key into api_key
cp .secret/openai.json.tmp .secret/openai.json
cp .secret/gemini.json.tmp .secret/gemini.json
```

`.secret/openai.json`:
```json
{ "base_url": "https://api.openai.com/v1", "api_key": "sk-...", "image_model": "gpt-image-2" }
```

`.secret/gemini.json`:
```json
{ "base_url": "https://generativelanguage.googleapis.com/v1beta/openai",
  "api_key": "...", "image_model": "gemini-3.1-flash-image-preview" }
```

Path overrides via env vars `OPENAI_SECRET_PATH`, `GEMINI_SECRET_PATH`, and `IMAGE_GEN_ROOT`
(where images are written, default `image/`). Never hardcode a key into a script — the
providers load `api_key` from these files automatically.

## 2. Dependencies

```bash
py -m pip install -r .claude/skills/sprite-animator/scripts/requirements-sprite.txt  # Pillow, numpy, colorthief
py -m pip install google-genai   # Gemini generate (+ Pillow for Gemini edit)
# OpenAI provider needs nothing extra — it uses urllib only.
```

> **Windows: always use `py`, never `python`.** The bare `python` command usually points at
> the Microsoft Store stub, which exits 49 and prints nothing — it fails *silently*, which is
> brutal to debug in a background batch. Use the Python Launcher `py`.

## 3. CLI

```bash
# Generate (text → image)
py .claude/skills/sprite-animator/scripts/image-gen.py generate \
  --prompt "A friendly robot reading a book in a cozy library" \
  --topic robot-reading --provider openai --aspect-ratio 16:9

# Edit (image + prompt → image) — the key to character consistency
py .claude/skills/sprite-animator/scripts/image-gen.py edit \
  --prompt "redraw THIS exact character as ONE full-body sprite on solid #00FF00 green" \
  --input-image image/openai/2026-06-13-vox7-concept.png \
  --topic vox7-idle-f0 --provider openai
```

Provider aliases: `openai` = `gpt-image-2` = `gpt2`; `gemini`.

Output is one JSON line — parse `status` before using `path`:
```json
{"status": "ok", "path": "image/openai/2026-06-13-vox7-idle-f0.png", "provider": "openai"}
{"status": "error", "message": "..."}
```
Saved as `image/{provider}/{YYYY-MM-DD}-{topic}.png`. After generating, open the PNG with the
Read tool to eyeball quality before continuing — cheaper than discovering a bad frame later.

## 4. Picking a provider

| | OpenAI gpt-image-2 | Gemini |
|---|---|---|
| Consistency lever | concept sheet as fixed `edit` reference (no seed param) | seed = `hash(slug)` + `edit` reference |
| Best for | single character, rich expressions/poses, concept-first design | large batches (many characters × frames), seed reproducibility |
| Edit support | yes | yes |
| Deps | none (urllib) | `google-genai` (+ Pillow for edit) |

Both keep identity via `edit` with an `input_image` reference + an explicit per-pose
description. Use chroma-green `#00FF00` backgrounds and "STRICTLY ONE CHARACTER" prompts so the
matting step (see `sprite-pipeline.md`) can cleanly cut the figure out.

## 5. Anti-patterns

| Anti-pattern | Result | Do instead |
|---|---|---|
| Hardcode API key in a script | leak risk | load from `.secret/{service}.json` |
| Use bare `python` on Windows | silent exit 49 | use `py` |
| Trust `path` without checking `status` | wrong path on error | parse JSON, check `status` first |
| Korean filenames (`--topic`) | OS portability issues | English topic keywords |
| `generate` (no reference) for every frame | costume drifts between frames | `edit` + reference + per-character desc |
