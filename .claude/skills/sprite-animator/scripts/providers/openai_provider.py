"""OpenAI gpt-image-2 generation provider.

`.secret/openai.json` 에서 api_key, base_url, image_model 을 읽어 OpenAI Images
API(`/images/generations`, `/images/edits`) 를 호출한다. 의존성 없이 urllib만 사용한다.

aspect_ratio 가 주어지면 OpenAI 가 허용하는 WxH 로 매핑한다. 응답은 b64_json 을
우선 사용해 단일 라운드트립으로 파일을 저장하고, url 만 오면 내려받아 저장한다.
"""

import base64
import json
import mimetypes
import os
import pathlib
import urllib.error
import urllib.request
import uuid

SCRIPT_PATH = pathlib.Path(__file__).resolve()
REPO_ROOT = SCRIPT_PATH.parents[5]
SECRET_PATH = pathlib.Path(
    os.environ.get("OPENAI_SECRET_PATH") or str(REPO_ROOT / ".secret/openai.json")
).expanduser()

DEFAULT_MODEL = "gpt-image-2"
DEFAULT_SIZE = "1536x1024"  # wide / 3:2 landscape (OpenAI 허용 크기)

ASPECT_SIZES = {
    "1:1": "1024x1024",
    "3:2": "1536x1024",
    "2:3": "1024x1536",
    "16:9": "1536x1024",
    "9:16": "1024x1536",
    "auto": "auto",
}


def _load_openai_config() -> dict:
    if not SECRET_PATH.exists():
        raise RuntimeError(
            f"OpenAI 시크릿 파일을 찾을 수 없습니다: {SECRET_PATH}. "
            "`.secret/openai.json` 에 api_key/base_url 을 정의하거나 "
            "OPENAI_SECRET_PATH 환경변수를 지정하세요."
        )
    # Windows 에디터가 붙이는 UTF-8 BOM 도 견디도록 utf-8-sig 로 읽는다.
    cfg = json.loads(SECRET_PATH.read_text(encoding="utf-8-sig"))
    if not cfg.get("api_key"):
        raise RuntimeError(f"api_key not found in {SECRET_PATH}")
    return cfg


class OpenAIImageProvider:
    """OpenAI gpt-image-2 (또는 호환 모델) 프로바이더."""

    def __init__(self):
        cfg = _load_openai_config()
        self.api_key = cfg["api_key"]
        self.base_url = cfg.get("base_url", "https://api.openai.com/v1").rstrip("/")
        # text_model 등 chat 용 키와 섞이지 않도록 이미지 전용 키를 우선 사용
        self.model = cfg.get("image_model") or DEFAULT_MODEL

    def _resolve_size(self, aspect_ratio: str, size: str) -> str:
        if size and "x" in size:
            head, _, tail = size.partition("x")
            if head.isdigit() and tail.isdigit():
                return size
        if aspect_ratio in ASPECT_SIZES:
            return ASPECT_SIZES[aspect_ratio]
        return DEFAULT_SIZE

    def _request_json(self, url: str, data: bytes, headers: dict, timeout: int) -> dict:
        req = urllib.request.Request(url, data=data, headers=headers, method="POST")
        try:
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                return json.loads(resp.read().decode("utf-8"))
        except urllib.error.HTTPError as e:
            body = e.read().decode("utf-8", errors="replace")
            raise RuntimeError(f"OpenAI image API error {e.code}: {body}") from e
        except urllib.error.URLError as e:
            raise RuntimeError(f"OpenAI image API 연결 실패: {e}") from e

    def _save_first_image(self, response: dict, output_path: pathlib.Path) -> pathlib.Path:
        data = response.get("data") or []
        if not data:
            raise RuntimeError(f"No image returned from OpenAI: {response}")
        first = data[0]
        if first.get("b64_json"):
            output_path.write_bytes(base64.b64decode(first["b64_json"]))
            return output_path
        if first.get("url"):
            with urllib.request.urlopen(first["url"], timeout=60) as resp:
                output_path.write_bytes(resp.read())
            return output_path
        raise RuntimeError(f"OpenAI response missing image data: {first}")

    def generate(self, prompt: str, output_path: pathlib.Path,
                 aspect_ratio: str = "16:9", size: str = DEFAULT_SIZE,
                 **kwargs) -> pathlib.Path:
        resolved_size = self._resolve_size(aspect_ratio, size)
        payload = json.dumps({
            "model": self.model,
            "prompt": prompt,
            "n": 1,
            "size": resolved_size,
        }).encode("utf-8")
        result = self._request_json(
            f"{self.base_url}/images/generations",
            data=payload,
            headers={
                "Content-Type": "application/json",
                "Authorization": f"Bearer {self.api_key}",
            },
            timeout=180,
        )
        return self._save_first_image(result, output_path)

    def edit(self, prompt: str, input_image_path: pathlib.Path,
             output_path: pathlib.Path, aspect_ratio: str = "16:9",
             size: str = DEFAULT_SIZE, **kwargs) -> pathlib.Path:
        if not input_image_path.exists():
            raise FileNotFoundError(f"Input image not found: {input_image_path}")
        mime, _ = mimetypes.guess_type(str(input_image_path))
        mime = mime or "image/png"
        img_bytes = input_image_path.read_bytes()
        resolved_size = self._resolve_size(aspect_ratio, size)

        boundary = f"----imagegen{uuid.uuid4().hex}"
        parts: list = []

        def add_text(name: str, value: str) -> None:
            parts.append(f"--{boundary}\r\n".encode())
            parts.append(
                f'Content-Disposition: form-data; name="{name}"\r\n\r\n'.encode()
            )
            parts.append(value.encode("utf-8"))
            parts.append(b"\r\n")

        def add_file(name: str, filename: str, content_type: str, blob: bytes) -> None:
            parts.append(f"--{boundary}\r\n".encode())
            parts.append(
                f'Content-Disposition: form-data; name="{name}"; '
                f'filename="{filename}"\r\n'.encode()
            )
            parts.append(f"Content-Type: {content_type}\r\n\r\n".encode())
            parts.append(blob)
            parts.append(b"\r\n")

        add_text("model", self.model)
        add_text("prompt", prompt)
        add_text("n", "1")
        add_text("size", resolved_size)
        add_file("image", input_image_path.name, mime, img_bytes)
        parts.append(f"--{boundary}--\r\n".encode())

        body = b"".join(parts)
        result = self._request_json(
            f"{self.base_url}/images/edits",
            data=body,
            headers={
                "Content-Type": f"multipart/form-data; boundary={boundary}",
                "Authorization": f"Bearer {self.api_key}",
            },
            timeout=240,
        )
        return self._save_first_image(result, output_path)
