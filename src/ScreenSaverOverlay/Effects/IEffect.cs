using ScreenSaverOverlay.Settings;

namespace ScreenSaverOverlay.Effects;

/// <summary>
/// An overlay effect. Effects are deliberately decoupled from <em>how</em> they are drawn:
/// today the host renders them with GDI+ (System.Drawing.Graphics). A future Direct2D/3D
/// renderer can implement a richer surface and reuse the same update/lifecycle model.
/// </summary>
public interface IEffect
{
    /// <summary>Stable identifier stored in settings.</summary>
    string Id { get; }

    /// <summary>Human-friendly name shown in the settings UI.</summary>
    string DisplayName { get; }

    /// <summary>(Re)initialize the effect for the given canvas and settings.</summary>
    void Initialize(Size canvasSize, AppSettings settings);

    /// <summary>Advance the simulation by <paramref name="deltaSeconds"/>.</summary>
    void Update(double deltaSeconds);

    /// <summary>Draw the current frame onto a transparent canvas.</summary>
    void Render(Graphics g, Size canvasSize);
}
