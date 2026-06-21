using System.Drawing.Drawing2D;
using ScreenSaverOverlay.Settings;

namespace ScreenSaverOverlay.Effects;

/// <summary>
/// The first sample effect: a swarm of circles drifting autonomously and bouncing off
/// the screen edges, drawn with anti-aliased GDI+ and a soft radial glow.
/// </summary>
public sealed class BouncingCircleEffect : IEffect
{
    public string Id => "bouncing-circles";
    public string DisplayName => "Bouncing Circles (GDI+)";

    private sealed class Ball
    {
        public double X, Y;      // center
        public double Vx, Vy;    // velocity px/sec
        public double Radius;
        public Color Color;
    }

    private readonly List<Ball> _balls = new();
    private readonly Random _rng = new();
    private Size _canvas;

    public void Initialize(Size canvasSize, AppSettings settings)
    {
        _canvas = canvasSize;
        _balls.Clear();

        var fixedColor = settings.ResolveColor();
        double radius = Math.Max(2, settings.Size / 2.0);
        double baseSpeed = 140.0 * settings.Speed; // px/sec at default speed

        for (int i = 0; i < settings.Count; i++)
        {
            double angle = _rng.NextDouble() * Math.Tau;
            double speed = baseSpeed * (0.6 + _rng.NextDouble() * 0.8);
            _balls.Add(new Ball
            {
                X = radius + _rng.NextDouble() * Math.Max(1, canvasSize.Width - 2 * radius),
                Y = radius + _rng.NextDouble() * Math.Max(1, canvasSize.Height - 2 * radius),
                Vx = Math.Cos(angle) * speed,
                Vy = Math.Sin(angle) * speed,
                Radius = radius * (0.7 + _rng.NextDouble() * 0.6),
                Color = fixedColor ?? RainbowColor((double)i / Math.Max(1, settings.Count)),
            });
        }
    }

    public void Update(double deltaSeconds)
    {
        // Clamp dt so a stalled frame (e.g. debugger break) can't teleport the balls.
        double dt = Math.Clamp(deltaSeconds, 0, 0.1);

        foreach (var b in _balls)
        {
            b.X += b.Vx * dt;
            b.Y += b.Vy * dt;

            if (b.X - b.Radius < 0) { b.X = b.Radius; b.Vx = Math.Abs(b.Vx); }
            else if (b.X + b.Radius > _canvas.Width) { b.X = _canvas.Width - b.Radius; b.Vx = -Math.Abs(b.Vx); }

            if (b.Y - b.Radius < 0) { b.Y = b.Radius; b.Vy = Math.Abs(b.Vy); }
            else if (b.Y + b.Radius > _canvas.Height) { b.Y = _canvas.Height - b.Radius; b.Vy = -Math.Abs(b.Vy); }
        }
    }

    public void Render(Graphics g, Size canvasSize)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        foreach (var b in _balls)
        {
            var rect = new RectangleF(
                (float)(b.X - b.Radius), (float)(b.Y - b.Radius),
                (float)(b.Radius * 2), (float)(b.Radius * 2));

            using var path = new GraphicsPath();
            path.AddEllipse(rect);
            using var brush = new PathGradientBrush(path)
            {
                CenterColor = Color.FromArgb(b.Color.A, b.Color),
                SurroundColors = new[] { Color.FromArgb(0, b.Color) },
                FocusScales = new PointF(0.25f, 0.25f),
            };
            g.FillEllipse(brush, rect);

            // Bright core
            float coreR = (float)(b.Radius * 0.45);
            var coreRect = new RectangleF(
                (float)(b.X - coreR), (float)(b.Y - coreR), coreR * 2, coreR * 2);
            using var core = new SolidBrush(Color.FromArgb(Math.Min(255, b.Color.A + 30), b.Color));
            g.FillEllipse(core, coreRect);
        }
    }

    private static Color RainbowColor(double t)
    {
        // t in [0,1) -> hue sweep, fully opaque (host applies global opacity).
        double h = t * 360.0;
        double s = 0.85, v = 1.0;
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        double m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromArgb(255,
            (int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
    }
}
