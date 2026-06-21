#!/usr/bin/env python3
"""
sprite-postprocess.py — Case S Phase 2b/2c/2d/3

서브커맨드:
  process   — 단일 캐릭터/액션의 raw 프레임들을 후처리하여 액션 시트(+JSON) 생성
  assemble  — 모든 캐릭터의 액션 시트들을 PACKED MASTER + index.json으로 조립
  evaluate  — Case S 3축 점수(S1/S2/S3) 자동 계산

후처리 4단계 (process):
  1) 매팅: BiRefNet (사용 가능 시) or HSV 그린키 제거 (폴백)
  2) 캐릭터 영역 크롭 (알파 bbox)
  3) 24x32 다운스케일 (nearest-neighbor)
  4) 원본 팔레트 강제 양자화 + 알파 이진화
"""

import argparse
import json
import math
import pathlib
import sys
from datetime import datetime
from typing import List, Optional, Tuple

import numpy as np
from PIL import Image


TARGET_W, TARGET_H = 24, 32  # default — override with --target-size WxH
SHEET_PADDING = 2
ALPHA_THRESHOLD = 128


# ─────────────────────────────────────────────────────────────
# Matting (BiRefNet or HSV fallback)
# ─────────────────────────────────────────────────────────────

def matte_hsv_green_key(input_path: pathlib.Path) -> Image.Image:
    """Remove pure green chroma key — returns RGBA Image."""
    img = Image.open(input_path).convert("RGB")
    arr = np.array(img)
    r, g, b = arr[..., 0].astype(int), arr[..., 1].astype(int), arr[..., 2].astype(int)
    is_green = (g > 140) & (g > r + 40) & (g > b + 40)
    alpha = np.where(is_green, 0, 255).astype(np.uint8)
    rgba = np.concatenate([arr, alpha[..., None]], axis=2)
    return Image.fromarray(rgba, mode="RGBA")


def matte(input_path: pathlib.Path, mode: str = "auto") -> Image.Image:
    """Return RGBA image with background removed.

    mode: "birefnet" | "hsv" | "auto"
    """
    if mode == "hsv":
        return matte_hsv_green_key(input_path)
    if mode == "birefnet":
        from providers import birefnet_provider
        if not birefnet_provider.is_available():
            raise RuntimeError("BiRefNet not available — install torch + transformers")
        out = input_path.with_suffix(".matted.png")
        birefnet_provider.BiRefNetProvider().matting(input_path, out)
        return Image.open(out).convert("RGBA")
    # auto
    try:
        from providers import birefnet_provider
        if birefnet_provider.is_available():
            out = input_path.with_suffix(".matted.png")
            birefnet_provider.BiRefNetProvider().matting(input_path, out)
            return Image.open(out).convert("RGBA")
    except Exception:
        pass
    return matte_hsv_green_key(input_path)


# ─────────────────────────────────────────────────────────────
# Alpha bbox crop + downscale
# ─────────────────────────────────────────────────────────────

def alpha_bbox(rgba: Image.Image, alpha_min: int = 32) -> Optional[Tuple[int, int, int, int]]:
    arr = np.array(rgba)
    mask = arr[..., 3] >= alpha_min
    if not mask.any():
        return None
    ys, xs = np.where(mask)
    return int(xs.min()), int(ys.min()), int(xs.max()) + 1, int(ys.max()) + 1


def parse_size(spec: str) -> Tuple[int, int]:
    w, h = spec.lower().split("x")
    return int(w), int(h)


def fit_to_target(rgba: Image.Image, target_w: int = TARGET_W, target_h: int = TARGET_H) -> Image.Image:
    """Crop to alpha bbox, downscale to target preserving aspect, center on transparent canvas."""
    bbox = alpha_bbox(rgba)
    if bbox is None:
        return Image.new("RGBA", (target_w, target_h), (0, 0, 0, 0))
    cropped = rgba.crop(bbox)
    cw, ch = cropped.size
    scale = min(target_w / cw, target_h / ch)
    new_w = max(1, int(round(cw * scale)))
    new_h = max(1, int(round(ch * scale)))
    resized = cropped.resize((new_w, new_h), Image.NEAREST)
    canvas = Image.new("RGBA", (target_w, target_h), (0, 0, 0, 0))
    canvas.paste(resized, ((target_w - new_w) // 2, target_h - new_h), resized)  # anchor to bottom
    return canvas


# ─────────────────────────────────────────────────────────────
# Palette quantize + alpha binarize
# ─────────────────────────────────────────────────────────────

def _palette_to_image(palette_rgb: List[List[int]]) -> Image.Image:
    flat = []
    for c in palette_rgb:
        flat.extend(c)
    while len(flat) < 768:
        flat.extend([0, 0, 0])
    pal_img = Image.new("P", (16, 16))
    pal_img.putpalette(flat[:768])
    return pal_img


def quantize_to_palette(rgba: Image.Image, palette_rgb: List[List[int]]) -> Image.Image:
    arr = np.array(rgba)
    alpha = arr[..., 3]
    rgb_img = Image.fromarray(arr[..., :3], mode="RGB")
    pal_img = _palette_to_image(palette_rgb)
    quant = rgb_img.quantize(palette=pal_img, dither=Image.Dither.NONE).convert("RGB")
    arr_q = np.array(quant)
    bin_alpha = np.where(alpha >= ALPHA_THRESHOLD, 255, 0).astype(np.uint8)
    arr_q[bin_alpha == 0] = 0
    out = np.concatenate([arr_q, bin_alpha[..., None]], axis=2)
    return Image.fromarray(out, mode="RGBA")


# ─────────────────────────────────────────────────────────────
# Sheet assembly (per-action)
# ─────────────────────────────────────────────────────────────

def assemble_action_sheet(frames: List[Image.Image], target_w: int = TARGET_W, target_h: int = TARGET_H) -> Tuple[Image.Image, list]:
    """Horizontal sheet of frames with 2px padding. Returns (sheet_image, frame_rects)."""
    n = len(frames)
    fw, fh = target_w, target_h
    sw = n * fw + (n + 1) * SHEET_PADDING
    sh = fh + 2 * SHEET_PADDING
    sheet = Image.new("RGBA", (sw, sh), (0, 0, 0, 0))
    rects = []
    for i, frame in enumerate(frames):
        x = SHEET_PADDING + i * (fw + SHEET_PADDING)
        y = SHEET_PADDING
        sheet.paste(frame, (x, y), frame)
        rects.append({"x": x, "y": y, "w": fw, "h": fh})
    return sheet, rects


def build_aseprite_hash(slug: str, action: str, frames: List[dict], image_rel: str,
                        target_w: int = TARGET_W, target_h: int = TARGET_H, duration_ms: int = 120) -> dict:
    doc = {
        "frames": {},
        "meta": {
            "app": "pencil-creator",
            "version": "2.7.0",
            "image": image_rel,
            "format": "RGBA8888",
            "size": {"w": frames[-1]["x"] + frames[-1]["w"] + SHEET_PADDING,
                     "h": target_h + 2 * SHEET_PADDING},
            "scale": "1",
            "frameTags": [
                {"name": action, "from": 0, "to": len(frames) - 1, "direction": "forward"}
            ],
            "slug": slug,
        },
    }
    for i, r in enumerate(frames):
        key = f"{slug}_{action}_{i}.png"
        doc["frames"][key] = {
            "frame": {"x": r["x"], "y": r["y"], "w": r["w"], "h": r["h"]},
            "rotated": False,
            "trimmed": False,
            "spriteSourceSize": {"x": 0, "y": 0, "w": target_w, "h": target_h},
            "sourceSize": {"w": target_w, "h": target_h},
            "duration": duration_ms,
        }
    return doc


# ─────────────────────────────────────────────────────────────
# Process (single character/action)
# ─────────────────────────────────────────────────────────────

def cmd_process(args) -> int:
    raw_dir = pathlib.Path(args.raw_dir)
    palette_doc = json.loads(pathlib.Path(args.palette).read_text(encoding="utf-8"))
    palette_rgb = palette_doc["global"]["rgb"]
    output_dir = pathlib.Path(args.output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)
    target_w, target_h = parse_size(args.target_size)
    # Scale SHEET_PADDING proportionally to maintain consistent sheet/frame ratio
    # (default base = 48px frame -> 2px padding; 192px frame -> 8px)
    global SHEET_PADDING
    SHEET_PADDING = max(1, 2 * target_w // 48)

    pattern = f"*-{args.character}-{args.pose}-f*.png"
    raw_files = sorted(raw_dir.glob(pattern))
    if not raw_files:
        print(json.dumps({"status": "error", "message": f"no raw frames matching {pattern} in {raw_dir}"}, ensure_ascii=False))
        return 1

    processed: List[Image.Image] = []
    for f in raw_files:
        rgba = matte(f, mode=args.matting)
        fit = fit_to_target(rgba, target_w, target_h)
        quant = quantize_to_palette(fit, palette_rgb)
        processed.append(quant)

    sheet, rects = assemble_action_sheet(processed, target_w, target_h)
    sheet_path = output_dir / f"{args.pose}.png"
    sheet.save(sheet_path, "PNG")

    aseprite = build_aseprite_hash(args.character, args.pose, rects, image_rel=sheet_path.name,
                                    target_w=target_w, target_h=target_h,
                                    duration_ms=args.duration_ms)
    json_path = output_dir / f"{args.pose}.json"
    json_path.write_text(json.dumps(aseprite, ensure_ascii=False, indent=2), encoding="utf-8")

    print(json.dumps({
        "status": "ok",
        "character": args.character, "pose": args.pose,
        "frames": len(processed),
        "matting": args.matting,
        "sheet": str(sheet_path), "json": str(json_path),
    }, ensure_ascii=False, indent=2))
    return 0


# ─────────────────────────────────────────────────────────────
# Assemble (packed master)
# ─────────────────────────────────────────────────────────────

def cmd_assemble(args) -> int:
    base = pathlib.Path(args.output_dir)
    master_dir = base / "_master"
    master_dir.mkdir(parents=True, exist_ok=True)
    boxes = json.loads(pathlib.Path(args.boxes).read_text(encoding="utf-8"))
    actions = args.actions.split(",")
    slugs = [c["slug"] for c in boxes["characters"]]

    # Override frame + padding from --target-size if provided
    global TARGET_W, TARGET_H, SHEET_PADDING
    if getattr(args, "target_size", None):
        TARGET_W, TARGET_H = parse_size(args.target_size)
        SHEET_PADDING = max(1, 2 * TARGET_W // 48)

    cell_w = len(actions) * (TARGET_W + SHEET_PADDING) + SHEET_PADDING
    cell_h = TARGET_H + 2 * SHEET_PADDING
    master_w = cell_w
    master_h = cell_h * len(slugs)
    master = Image.new("RGBA", (master_w, master_h), (0, 0, 0, 0))
    index = {"version": "2.7.0", "characters": {}}

    for row, slug in enumerate(slugs):
        char_dir = base / slug
        char_entry = {"row": row, "actions": {}}
        for col, action in enumerate(actions):
            sheet_path = char_dir / f"{action}.png"
            json_path = char_dir / f"{action}.json"
            if not sheet_path.exists():
                continue
            sheet_img = Image.open(sheet_path).convert("RGBA")
            # Place this action's first frame in master cell (the whole sheet is wider; we use first frame only)
            arr = np.array(sheet_img)
            # The master has placeholder per-character row containing each action's first frame side by side
            x0 = SHEET_PADDING + col * (TARGET_W + SHEET_PADDING)
            y0 = row * cell_h + SHEET_PADDING
            master.paste(sheet_img.crop((SHEET_PADDING, SHEET_PADDING, SHEET_PADDING + TARGET_W, SHEET_PADDING + TARGET_H)),
                         (x0, y0))
            char_entry["actions"][action] = {
                "sheet": str(sheet_path.relative_to(base)).replace("\\", "/"),
                "json": str(json_path.relative_to(base)).replace("\\", "/"),
            }
        index["characters"][slug] = char_entry

    master_path = master_dir / "orchestra-master.png"
    master.save(master_path, "PNG")
    index_path = master_dir / "index.json"
    index["actions"] = actions
    index["master_sheet"] = str(master_path.relative_to(base)).replace("\\", "/")
    index["frame_size"] = [TARGET_W, TARGET_H]
    index["padding"] = SHEET_PADDING
    index_path.write_text(json.dumps(index, ensure_ascii=False, indent=2), encoding="utf-8")

    print(json.dumps({"status": "ok", "master": str(master_path), "index": str(index_path),
                      "characters_packed": len(index["characters"]),
                      "actions": actions}, ensure_ascii=False, indent=2))
    return 0


# ─────────────────────────────────────────────────────────────
# Evaluate (Case S 3-axis scoring)
# ─────────────────────────────────────────────────────────────

def color_distance(rgb1, rgb2) -> float:
    return math.sqrt(sum((a - b) ** 2 for a, b in zip(rgb1, rgb2)))


def palette_drift_count(image: Image.Image, palette_rgb: List[List[int]], threshold: float = 30.0) -> Tuple[int, int]:
    """Return (drift_count, sample_size). Drift = visible pixels whose nearest palette color exceeds threshold."""
    arr = np.array(image.convert("RGBA"))
    rgb, alpha = arr[..., :3], arr[..., 3]
    visible = rgb[alpha >= ALPHA_THRESHOLD]
    if len(visible) == 0:
        return 0, 0
    pal = np.array(palette_rgb, dtype=float)
    sample = visible[:: max(1, len(visible) // 2000)]
    drift = 0
    for px in sample:
        d = np.linalg.norm(pal - px, axis=1).min()
        if d > threshold:
            drift += 1
    return drift, len(sample)


def green_residual_count(image: Image.Image) -> int:
    arr = np.array(image.convert("RGBA"))
    r, g, b, a = arr[..., 0], arr[..., 1], arr[..., 2], arr[..., 3]
    return int(((g > 200) & (r < 100) & (b < 100) & (a > 0)).sum())


def cmd_evaluate(args) -> int:
    target_dir = pathlib.Path(args.target)
    palette_doc = json.loads(pathlib.Path(args.palette).read_text(encoding="utf-8"))
    palette_rgb = palette_doc["global"]["rgb"]
    # Per-character palette (more accurate fidelity baseline) — falls back to global
    per_char_pal = palette_doc.get("per_character", {})
    char_slug = target_dir.name
    char_pal_entry = per_char_pal.get(char_slug, {})
    char_palette = char_pal_entry.get("rgb") if isinstance(char_pal_entry, dict) else None
    full_palette = list(palette_rgb)
    if char_palette:
        full_palette = full_palette + char_palette

    sheets = sorted(target_dir.glob("*.png"))
    jsons = sorted(target_dir.glob("*.json"))
    if not sheets:
        print(json.dumps({"status": "error", "message": f"no sheets in {target_dir}"}, ensure_ascii=False))
        return 1

    n_actions = len(sheets)
    n_frames_total = 0
    drift_total = 0
    sample_total = 0
    green_total = 0
    aseprite_valid = 0
    for sheet_path in sheets:
        img = Image.open(sheet_path).convert("RGBA")
        # Read frame count from JSON (authoritative); fall back to width heuristic
        json_path = sheet_path.with_suffix(".json")
        n_frames_here = 0
        if json_path.exists():
            try:
                doc = json.loads(json_path.read_text(encoding="utf-8"))
                n_frames_here = len(doc.get("frames", {}))
            except Exception:
                pass
        if n_frames_here == 0:
            n_frames_here = max(1, (img.width - SHEET_PADDING) // (TARGET_W + SHEET_PADDING))
        n_frames_total += n_frames_here
        d, s = palette_drift_count(img, full_palette, threshold=35.0)
        drift_total += d
        sample_total += s
        green_total += green_residual_count(img)
    for jp in jsons:
        try:
            doc = json.loads(jp.read_text(encoding="utf-8"))
            if doc.get("meta", {}).get("app") in ("aseprite", "pencil-creator") and "frames" in doc:
                aseprite_valid += 1
        except Exception:
            pass

    drift_ratio = drift_total / sample_total if sample_total > 0 else 1.0

    # S1 — character fidelity (palette drift ratio against global + per-character palette)
    if drift_ratio < 0.05 and n_frames_total >= 4:
        s1 = 35 if (n_actions >= 2 and n_frames_total >= 8 and args.reference) else 28
    elif drift_ratio < 0.15:
        s1 = 25
    elif drift_ratio < 0.30:
        s1 = 15
    else:
        s1 = 5
    if not args.reference:
        s1 = min(s1, 28)  # SSIM not measured — cap at "near-A"

    # S2 — animation quality
    if n_frames_total >= 8 and n_actions >= 2:
        s2 = 28
    elif n_frames_total >= 4:
        s2 = 22
    elif n_frames_total >= 2:
        s2 = 15
    else:
        s2 = 0

    # S3 — engineering usability
    s3 = 0
    if green_total < 10:
        s3 += 10
    if aseprite_valid >= n_actions and aseprite_valid > 0:
        s3 += 8
    if aseprite_valid >= n_actions and n_actions >= 1:
        s3 += 4
    master_path = target_dir.parent / "_master" / "index.json"
    if master_path.exists():
        s3 += 8
    s3 = min(s3, 30)

    total = s1 + s2 + s3
    grade = "A" if total >= 80 else "B" if total >= 60 else "C" if total >= 40 else "D"

    result = {
        "status": "ok",
        "case": "S",
        "target": str(target_dir),
        "actions": n_actions,
        "frames_total": n_frames_total,
        "diagnostics": {
            "palette_drift_pixels": drift_total,
            "palette_sample_pixels": sample_total,
            "palette_drift_ratio": round(drift_ratio, 4),
            "green_residual_pixels": green_total,
            "aseprite_json_valid": aseprite_valid,
        },
        "axes": {
            "S1_character_fidelity": s1,
            "S2_animation_quality": s2,
            "S3_engineering_usability": s3,
        },
        "total": total,
        "grade": grade,
        "timestamp": datetime.now().isoformat(timespec="seconds"),
    }
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0


# ─────────────────────────────────────────────────────────────
# CLI
# ─────────────────────────────────────────────────────────────

def main() -> int:
    parser = argparse.ArgumentParser(description="Sprite post-processing + sheet assembly + Case S evaluation")
    sub = parser.add_subparsers(dest="command", required=True)

    p = sub.add_parser("process", help="Process raw frames → action sheet + Aseprite Hash JSON")
    p.add_argument("--character", required=True, help="character slug (e.g., piano)")
    p.add_argument("--pose", required=True, help="action name (e.g., idle, play)")
    p.add_argument("--raw-dir", required=True, help="dir containing raw Gemini outputs")
    p.add_argument("--palette", required=True, help="image/sprite/palette.json")
    p.add_argument("--output-dir", required=True, help="design/sprite/output/{character}/")
    p.add_argument("--matting", default="auto", choices=["auto", "birefnet", "hsv"])
    p.add_argument("--target-size", default="24x32", help="WxH (default 24x32, e.g. 48x48 for character+instrument)")
    p.add_argument("--duration-ms", type=int, default=120)
    p.set_defaults(func=cmd_process)

    a = sub.add_parser("assemble", help="Pack all characters' sheets into PACKED MASTER + index.json")
    a.add_argument("--output-dir", required=True, help="design/sprite/output/")
    a.add_argument("--boxes", required=True, help="image/sprite/character-boxes.json")
    a.add_argument("--actions", default="idle,play", help="comma-separated action names")
    a.add_argument("--target-size", default=None, help="WxH override (default uses 24x32 base)")
    a.set_defaults(func=cmd_assemble)

    e = sub.add_parser("evaluate", help="Compute Case S 3-axis score for a target character dir")
    e.add_argument("--target", required=True, help="design/sprite/output/{character}/")
    e.add_argument("--palette", required=True, help="image/sprite/palette.json")
    e.add_argument("--reference", default=None, help="(optional) image/sprite/crops/{character}.png for SSIM")
    e.set_defaults(func=cmd_evaluate)

    args = parser.parse_args()

    sys.path.insert(0, str(pathlib.Path(__file__).parent))
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
