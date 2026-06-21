#!/usr/bin/env python3
"""
sprite-analyze-dance.py — Case S simplified dance flow (Phase 1 only)

기존 sprite-analyze.py의 간소화 버전:
  - 입력: 컨셉아트 + character-boxes-dance.json (style 필드 포함)
  - 출력: 스타일별 디렉토리로 분류된 크롭 + 박스 검증용 preview-grid
  - 팔레트/스타일 메타 생략 (간소화 플로우)
"""

import argparse
import json
import pathlib
import sys

from PIL import Image, ImageDraw, ImageFont


def crop_grouped(image: Image.Image, characters: list, crops_root: pathlib.Path) -> list:
    results = []
    for ch in characters:
        style = ch["style"]
        (crops_root / style).mkdir(parents=True, exist_ok=True)
        crop = image.crop(tuple(ch["box"]))
        out = crops_root / style / f"{ch['slug']}.png"
        crop.save(out, "PNG")
        results.append({"slug": ch["slug"], "style": style, "box": ch["box"], "path": str(out)})
    return results


def render_preview_grid(image: Image.Image, characters: list, out_path: pathlib.Path) -> None:
    style_colors = {
        "kpop":     (255, 102, 178, 220),
        "hiphop":   (153,  92, 255, 220),
        "jazz":     (255, 191,  64, 220),
        "ballet":   (255, 153, 204, 220),
        "cheer":    (255,  92,  92, 220),
        "waacking": (140,  92, 255, 220),
    }
    preview = image.copy()
    draw = ImageDraw.Draw(preview, "RGBA")
    try:
        font = ImageFont.truetype("malgun.ttf", 14)
    except OSError:
        font = ImageFont.load_default()
    for ch in characters:
        l, t, r, b = ch["box"]
        color = style_colors.get(ch["style"], (255, 255, 255, 220))
        draw.rectangle([l, t, r, b], outline=color, width=3)
        label = ch["slug"]
        draw.rectangle([l, t, l + 100, t + 22], fill=(0, 0, 0, 180))
        draw.text((l + 4, t + 2), label, fill=color[:3] + (255,), font=font)
    preview.save(out_path, "PNG")


def main() -> int:
    parser = argparse.ArgumentParser(description="Dance concept art → grouped crops + preview")
    parser.add_argument("--input", required=True, help="Concept art image path")
    parser.add_argument("--boxes", required=True, help="character-boxes-dance.json path")
    parser.add_argument("--output-dir", required=True, help="Output base directory (e.g. image/sprite/)")
    args = parser.parse_args()

    inp = pathlib.Path(args.input)
    boxes = json.loads(pathlib.Path(args.boxes).read_text(encoding="utf-8"))
    out_dir = pathlib.Path(args.output_dir)

    if not inp.exists():
        print(json.dumps({"status": "error", "message": f"input not found: {inp}"}, ensure_ascii=False))
        return 1

    image = Image.open(inp).convert("RGB")
    crops_root = out_dir / "crops_dance"
    crops = crop_grouped(image, boxes["characters"], crops_root)
    render_preview_grid(image, boxes["characters"], out_dir / "preview-grid-dance.png")

    by_style = {}
    for c in crops:
        by_style.setdefault(c["style"], []).append(c["slug"])
    print(json.dumps({
        "status": "ok",
        "crops_count": len(crops),
        "by_style": by_style,
        "files": {
            "crops_root": str(crops_root),
            "preview": str(out_dir / "preview-grid-dance.png"),
        },
    }, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
