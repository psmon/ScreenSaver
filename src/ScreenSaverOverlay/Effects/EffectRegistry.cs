namespace ScreenSaverOverlay.Effects;

/// <summary>
/// Central catalog of available effects. Add new effects here; the settings UI and the
/// overlay both discover effects through this registry.
/// </summary>
public static class EffectRegistry
{
    private static readonly Func<IEffect>[] Factories =
    {
        () => new BouncingCircleEffect(),
        () => new DiverSpriteEffect(),
        () => new Diver2SpriteEffect(),
        // Future: () => new Direct2DParticlesEffect(), () => new Model3DEffect(), ...
    };

    public static IReadOnlyList<(string Id, string DisplayName)> Available =>
        Factories.Select(f => { var e = f(); return (e.Id, e.DisplayName); }).ToList();

    public static IEffect Create(string id)
    {
        foreach (var factory in Factories)
        {
            var effect = factory();
            if (string.Equals(effect.Id, id, StringComparison.OrdinalIgnoreCase))
                return effect;
        }
        // Unknown id -> fall back to the first registered effect.
        return Factories[0]();
    }
}
