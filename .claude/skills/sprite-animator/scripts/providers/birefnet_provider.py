"""BiRefNet background removal / matting provider.

Heavy deps (torch, transformers) are imported lazily so that this module can be
imported in environments where BiRefNet is not installed. Callers should check
`is_available()` first; if False, fall back to HSV chroma key removal.
"""

import pathlib
from typing import Optional

_MODEL = None
_PROCESSOR = None
_DEVICE = None
_LOAD_ERROR: Optional[str] = None


def is_available() -> bool:
    """True if torch + transformers can be imported."""
    try:
        import torch  # noqa: F401
        import transformers  # noqa: F401
        return True
    except Exception:
        return False


def _ensure_loaded(model_name: str = "ZhengPeng7/BiRefNet"):
    global _MODEL, _PROCESSOR, _DEVICE, _LOAD_ERROR
    if _MODEL is not None:
        return
    try:
        import torch
        from transformers import AutoModelForImageSegmentation
        _DEVICE = "cuda" if torch.cuda.is_available() else "cpu"
        _MODEL = AutoModelForImageSegmentation.from_pretrained(
            model_name, trust_remote_code=True
        )
        _MODEL.to(_DEVICE).eval()
    except Exception as exc:
        _LOAD_ERROR = str(exc)
        raise RuntimeError(f"Failed to load BiRefNet model: {exc}") from exc


class BiRefNetProvider:
    def __init__(self, model_name: str = "ZhengPeng7/BiRefNet", input_size: int = 1024):
        self.model_name = model_name
        self.input_size = input_size

    def matting(self, input_path: pathlib.Path, output_path: pathlib.Path) -> pathlib.Path:
        """Generate a transparent-background PNG (RGBA) at output_path."""
        from PIL import Image
        import torch
        from torchvision import transforms

        _ensure_loaded(self.model_name)

        img = Image.open(input_path).convert("RGB")
        orig_size = img.size

        transform = transforms.Compose([
            transforms.Resize((self.input_size, self.input_size)),
            transforms.ToTensor(),
            transforms.Normalize([0.485, 0.456, 0.406], [0.229, 0.224, 0.225]),
        ])
        tensor = transform(img).unsqueeze(0).to(_DEVICE)

        with torch.no_grad():
            preds = _MODEL(tensor)[-1].sigmoid().cpu()
        pred = preds[0].squeeze()
        mask = transforms.ToPILImage()(pred).resize(orig_size, Image.BILINEAR)

        rgba = img.convert("RGBA")
        rgba.putalpha(mask)
        rgba.save(output_path, "PNG")
        return output_path
