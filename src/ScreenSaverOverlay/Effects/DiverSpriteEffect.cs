namespace ScreenSaverOverlay.Effects;

/// <summary>
/// Diver 1 — the original scuba boy. Swim sheet is the 8-frame "spin" turnaround (one pose per
/// direction), so facing follows heading but the fins don't cycle.
/// </summary>
public sealed class DiverSpriteEffect : SwimmingSpriteEffect
{
    protected override SpriteConfig Config { get; } = new(
        Id: "diver-sprite",
        DisplayName: "Scuba Diver (sprite)",
        Folder: "diver",
        SwimSheet: "spin",
        Directions: 8,
        KickPhases: 1,
        KickFps: 0,
        HuntSheet: "hunt",
        FleeSheet: "flee",
        HarpoonAsset: "harpoon.png");
}
