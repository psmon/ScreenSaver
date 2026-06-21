namespace ScreenSaverOverlay.Effects;

/// <summary>
/// Diver 2 — a cute girl diver designed from an animation-aware model sheet. Her swim sheet
/// encodes 8 directions × 2 kick phases, so her fins actively kick as she swims and she turns
/// smoothly through all 8 facings. Same harpoon-hunt and emergency-flee actions; bubbles reused.
/// </summary>
public sealed class Diver2SpriteEffect : SwimmingSpriteEffect
{
    protected override SpriteConfig Config { get; } = new(
        Id: "diver2-sprite",
        DisplayName: "Scuba Diver 2 — finned (sprite)",
        Folder: "diver2",
        SwimSheet: "swim",
        Directions: 8,
        KickPhases: 2,
        KickFps: 6,        // ~3 kick cycles per second
        HuntSheet: "hunt",
        FleeSheet: "flee",
        HarpoonAsset: "harpoon.png");
}
