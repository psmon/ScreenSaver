#!/usr/bin/env python3
"""
sprite-analyze.py — Case S Phase 1 (researching)

컨셉아트에서 캐릭터별 크롭, 글로벌 팔레트, 스타일 메타를 추출한다.

입력:
  - 컨셉아트 PNG (예: image/sprite/악단컨셉.png)
  - character-boxes.json (캐릭터별 박스 좌표)

출력:
  - image/sprite/crops/{slug}.png       (캐릭터별 크롭, Gemini edit() 입력용)
  - image/sprite/palette.json           (글로벌 + 캐릭터별 도미넌트 색상)
  - image/sprite/style.json             (스타일 키워드)
  - image/sprite/preview-grid.png       (박스 오버레이 — 좌표 검증용)

사용:
  python sprite-analyze.py \\
    --input image/sprite/악단컨셉.png \\
    --boxes image/sprite/character-boxes.json \\
    --output-dir image/sprite/
"""

import argparse
import json
import pathlib
import sys

from PIL import Image, ImageDraw, ImageFont
from colorthief import ColorThief


PALETTE_SIZE_GLOBAL = 13
PALETTE_SIZE_PER_CHAR = 5


def crop_characters(image: Image.Image, characters: list, crops_dir: pathlib.Path) -> list:
    crops_dir.mkdir(parents=True, exist_ok=True)
    results = []
    for ch in characters:
        box = tuple(ch["box"])
        crop = image.crop(box)
        out = crops_dir / f"{ch['slug']}.png"
        crop.save(out, "PNG")
        results.append({"slug": ch["slug"], "name_ko": ch["name_ko"],
                        "box": box, "crop_size": crop.size, "path": str(out)})
    return results


def extract_palette(path: pathlib.Path, color_count: int) -> list:
    ct = ColorThief(str(path))
    return [list(rgb) for rgb in ct.get_palette(color_count=color_count, quality=2)]


def rgb_to_hex(rgb: list) -> str:
    return "#{:02X}{:02X}{:02X}".format(*rgb)


def render_preview_grid(image: Image.Image, characters: list, out_path: pathlib.Path) -> None:
    preview = image.copy()
    draw = ImageDraw.Draw(preview, "RGBA")
    try:
        font = ImageFont.truetype("malgun.ttf", 14)
    except OSError:
        font = ImageFont.load_default()
    for ch in characters:
        left, top, right, bottom = ch["box"]
        draw.rectangle([left, top, right, bottom], outline=(255, 0, 255, 220), width=3)
        label = f"{ch['slug']}"
        draw.rectangle([left, top, left + 130, top + 22], fill=(0, 0, 0, 180))
        draw.text((left + 4, top + 2), label, fill=(255, 255, 255, 255), font=font)
    preview.save(out_path, "PNG")


def main() -> int:
    parser = argparse.ArgumentParser(description="Concept art → crops + palette + style")
    parser.add_argument("--input", required=True, help="Concept art image path")
    parser.add_argument("--boxes", required=True, help="character-boxes.json path")
    parser.add_argument("--output-dir", required=True, help="Output base directory")
    parser.add_argument("--style-keywords", default="chibi pixel art, 24x32 sprite, dark fantasy fairy tale, muted earth tones, soft cel shading, single character isolated",
                        help="Global style keywords for Gemini prompts")
    args = parser.parse_args()

    input_path = pathlib.Path(args.input)
    boxes_path = pathlib.Path(args.boxes)
    out_dir = pathlib.Path(args.output_dir)

    if not input_path.exists():
        print(json.dumps({"status": "error", "message": f"input not found: {input_path}"}, ensure_ascii=False))
        return 1
    if not boxes_path.exists():
        print(json.dumps({"status": "error", "message": f"boxes not found: {boxes_path}"}, ensure_ascii=False))
        return 1

    boxes = json.loads(boxes_path.read_text(encoding="utf-8"))
    characters = boxes["characters"]

    image = Image.open(input_path).convert("RGB")

    crops = crop_characters(image, characters, out_dir / "crops")

    global_palette = extract_palette(input_path, PALETTE_SIZE_GLOBAL)
    per_char_palette = {}
    for crop in crops:
        try:
            per_char_palette[crop["slug"]] = extract_palette(pathlib.Path(crop["path"]), PALETTE_SIZE_PER_CHAR)
        except Exception as exc:
            per_char_palette[crop["slug"]] = {"error": str(exc)}

    palette_doc = {
        "source": str(input_path),
        "global": {
            "rgb": global_palette,
            "hex": [rgb_to_hex(c) for c in global_palette],
        },
        "per_character": {
            slug: {"rgb": pal, "hex": [rgb_to_hex(c) for c in pal] if isinstance(pal, list) else None}
            for slug, pal in per_char_palette.items()
        },
    }
    (out_dir / "palette.json").write_text(json.dumps(palette_doc, ensure_ascii=False, indent=2), encoding="utf-8")

    style_doc = {
        "source": str(input_path),
        "image_size": list(image.size),
        "character_count": len(characters),
        "style_keywords": args.style_keywords,
        "target_sprite_size": [24, 32],
        "chroma_key_color": "#00FF00",
        "gemini_prompt_template": (
            "{style_keywords}, single {character_desc} on pure {chroma_key_color} chroma background, "
            "{pose} pose frame {frame_index}, no shadow, no text, no border"
        ),
    }
    (out_dir / "style.json").write_text(json.dumps(style_doc, ensure_ascii=False, indent=2), encoding="utf-8")

    render_preview_grid(image, characters, out_dir / "preview-grid.png")

    summary = {
        "status": "ok",
        "crops": [c["slug"] for c in crops],
        "crops_count": len(crops),
        "palette_global_count": len(global_palette),
        "files": {
            "crops_dir": str(out_dir / "crops"),
            "palette": str(out_dir / "palette.json"),
            "style": str(out_dir / "style.json"),
            "preview": str(out_dir / "preview-grid.png"),
        },
    }
    print(json.dumps(summary, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
