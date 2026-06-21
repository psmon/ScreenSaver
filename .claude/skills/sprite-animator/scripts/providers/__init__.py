"""Provider registry for image generation."""


def get_provider(name: str):
    if name == "gemini":
        from .gemini_provider import GeminiProvider
        return GeminiProvider()
    if name in ("openai", "gpt-image", "gpt-image-2", "gpt2"):
        from .openai_provider import OpenAIImageProvider
        return OpenAIImageProvider()
    raise ValueError(
        f"Unknown provider: {name}. Available: gemini, openai (gpt-image-2)"
    )