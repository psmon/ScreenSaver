using System.Drawing.Drawing2D;
using ScreenSaverOverlay.Settings;

namespace ScreenSaverOverlay.Effects;

/// <summary>
/// A cute scuba diver that swims around the screen, naturally turning to face wherever it's
/// heading (the 8-frame 360° spin set is indexed by movement direction). Most of the time it
/// just swims; occasionally — at long random intervals — it does a special action: a HUNT
/// (aims and fires a harpoon that detaches and flies, always missing) or a FLEE (a startled
/// dash). Bubbles trail up from the regulator throughout. Drawn with GDI+ on top of the hosted
/// screensaver, like the other effects.
/// </summary>
public sealed class DiverSpriteEffect : IEffect
{
    public string Id => "diver-sprite";
    public string DisplayName => "Scuba Diver (sprite)";

    // ---- loaded assets ----------------------------------------------------
    private SpriteSheet? _idle, _spin, _flee, _hunt;
    private Bitmap? _harpoon;
    private bool _loaded;

    private Size _canvas;
    private double _scale = 1.0;     // 192px frame -> on-screen size
    private double _baseSpeed = 60;  // px/s
    private readonly Random _rng = new();

    private readonly List<Diver> _divers = new();
    private readonly List<Bubble> _bubbles = new();
    private readonly List<Harpoon> _harpoons = new();

    private const int FrameSize = 192;

    // ---- lifecycle --------------------------------------------------------

    public void Initialize(Size canvasSize, AppSettings settings)
    {
        _canvas = canvasSize;
        double drawH = Math.Clamp(settings.Size * 2.0, 120, 460);
        _scale = drawH / FrameSize;
        _baseSpeed = 55.0 * settings.Speed;

        LoadAssets();
        _bubbles.Clear();
        _harpoons.Clear();
        _divers.Clear();

        int count = _loaded ? Math.Clamp(settings.Count, 1, 4) : 0;
        for (int i = 0; i < count; i++)
        {
            var d = new Diver
            {
                X = _rng.Next(canvasSize.Width),
                Y = _rng.Next(canvasSize.Height),
                Heading = _rng.NextDouble() * Math.Tau,
                Speed = _baseSpeed * (0.8 + _rng.NextDouble() * 0.5),
                NextSpecial = RandRange(9, 20),
            };
            d.TargetHeading = d.Heading;
            _divers.Add(d);
        }
    }

    private void LoadAssets()
    {
        if (_loaded) return;
        try
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "Assets", "sprites", "diver");
            _idle = SpriteSheet.Load(Path.Combine(dir, "idle.png"), Path.Combine(dir, "idle.json"));
            _spin = SpriteSheet.Load(Path.Combine(dir, "spin.png"), Path.Combine(dir, "spin.json"));
            _flee = SpriteSheet.Load(Path.Combine(dir, "flee.png"), Path.Combine(dir, "flee.json"));
            _hunt = SpriteSheet.Load(Path.Combine(dir, "hunt.png"), Path.Combine(dir, "hunt.json"));
            _harpoon = new Bitmap(Path.Combine(dir, "harpoon.png"));
            _loaded = true;
        }
        catch
        {
            _loaded = false; // assets missing -> effect renders nothing rather than crashing
        }
    }

    // ---- update -----------------------------------------------------------

    public void Update(double dt)
    {
        dt = Math.Clamp(dt, 0, 0.1);
        if (!_loaded) return;

        foreach (var d in _divers)
            UpdateDiver(d, dt);

        UpdateBubbles(dt);
        UpdateHarpoons(dt);
    }

    private void UpdateDiver(Diver d, double dt)
    {
        d.FrameTime += dt;

        switch (d.State)
        {
            case DiverState.Swim: UpdateSwim(d, dt); break;
            case DiverState.Hunt: UpdateHunt(d, dt); break;
            case DiverState.Flee: UpdateFlee(d, dt); break;
        }

        // Bubbles trail from the regulator regardless of state.
        d.BubbleTimer -= dt;
        if (d.BubbleTimer <= 0)
        {
            d.BubbleTimer = RandRange(0.18, 0.42);
            EmitBubble(d);
        }
    }

    private void UpdateSwim(Diver d, double dt)
    {
        // Gently wander: occasionally pick a new target heading, steer toward it smoothly so
        // the diver curves and turns (which, via the spin sheet, reads as natural rotation).
        d.HeadingTimer -= dt;
        if (d.HeadingTimer <= 0)
        {
            d.HeadingTimer = RandRange(1.6, 3.6);
            d.TargetHeading = d.Heading + (_rng.NextDouble() - 0.5) * Math.PI;
        }
        SteerTowardEdgesAware(d);
        d.Heading = ApproachAngle(d.Heading, d.TargetHeading, 1.6 * dt);

        Move(d, d.Speed, dt);

        // Time to do something special?
        d.NextSpecial -= dt;
        if (d.NextSpecial <= 0)
        {
            if (_rng.NextDouble() < 0.5) EnterHunt(d);
            else EnterFlee(d);
        }
    }

    private void EnterHunt(Diver d)
    {
        d.State = DiverState.Hunt;
        d.ActionFrame = 0;
        d.FrameTime = 0;
        d.Fired = false;
        d.FlipX = Math.Cos(d.Heading) < 0; // hunt art faces right; flip if heading left
        d.Speed *= 0.25;                    // settle to aim
    }

    private void UpdateHunt(Diver d, double dt)
    {
        Move(d, d.Speed, dt);
        d.Speed = Approach(d.Speed, _baseSpeed * 0.05, 40 * dt);

        var sheet = _hunt!;
        AdvanceAction(d, sheet);

        // Fire the harpoon as the shot leaves (frame 1), once.
        if (!d.Fired && d.ActionFrame >= 1)
        {
            d.Fired = true;
            SpawnHarpoon(d);
        }

        if (d.ActionDone)
            ExitSpecial(d);
    }

    private void EnterFlee(Diver d)
    {
        d.State = DiverState.Flee;
        d.ActionFrame = 0;
        d.FrameTime = 0;
        d.FleeTime = RandRange(1.6, 2.6);
        // Dart mostly horizontally away from screen center, with a little vertical.
        double dir = (d.X < _canvas.Width / 2.0) ? 0 : Math.PI;     // flee toward nearest side
        dir += (_rng.NextDouble() - 0.5) * 0.7;
        d.Heading = dir;
        d.TargetHeading = dir;
        d.Speed = _baseSpeed * 3.2;
        d.FlipX = Math.Cos(dir) > 0; // flee art faces left; flip if darting right
    }

    private void UpdateFlee(Diver d, double dt)
    {
        d.FleeTime -= dt;
        SteerTowardEdgesAware(d);
        d.Heading = ApproachAngle(d.Heading, d.TargetHeading, 2.0 * dt);
        Move(d, d.Speed, dt);

        // loop the flee frames quickly
        AdvanceAction(d, _flee!, loop: true);

        if (d.FleeTime <= 0)
            ExitSpecial(d);
    }

    private void ExitSpecial(Diver d)
    {
        d.State = DiverState.Swim;
        d.Speed = _baseSpeed * (0.8 + _rng.NextDouble() * 0.5);
        d.NextSpecial = RandRange(10, 22);
        d.FlipX = false;
    }

    // advance an action sheet by real time; sets ActionDone when the last frame elapses
    private void AdvanceAction(Diver d, SpriteSheet sheet, bool loop = false)
    {
        int dur = sheet.DurationsMs[Math.Clamp(d.ActionFrame, 0, sheet.Count - 1)];
        if (d.FrameTime * 1000 >= dur)
        {
            d.FrameTime = 0;
            d.ActionFrame++;
            if (d.ActionFrame >= sheet.Count)
            {
                if (loop) d.ActionFrame = 0;
                else { d.ActionFrame = sheet.Count - 1; d.ActionDone = true; }
            }
        }
    }

    private void Move(Diver d, double speed, double dt)
    {
        d.X += Math.Cos(d.Heading) * speed * dt;
        d.Y += Math.Sin(d.Heading) * speed * dt;

        // Soft wrap with a margin so the diver glides off and back rather than popping.
        double m = FrameSize * _scale * 0.6;
        if (d.X < -m) d.X = _canvas.Width + m;
        else if (d.X > _canvas.Width + m) d.X = -m;
        if (d.Y < -m) d.Y = _canvas.Height + m;
        else if (d.Y > _canvas.Height + m) d.Y = -m;
    }

    // Nudge the target heading back toward the canvas when the diver nears an edge.
    private void SteerTowardEdgesAware(Diver d)
    {
        double margin = FrameSize * _scale * 0.7;
        if (d.X < margin || d.X > _canvas.Width - margin ||
            d.Y < margin || d.Y > _canvas.Height - margin)
        {
            double toCenter = Math.Atan2(_canvas.Height / 2.0 - d.Y, _canvas.Width / 2.0 - d.X);
            d.TargetHeading = toCenter;
        }
    }

    // ---- bubbles ----------------------------------------------------------

    private void EmitBubble(Diver d)
    {
        int sign = d.FlipX ? -1 : 1;
        double bx = d.X + sign * FrameSize * _scale * 0.14;
        double by = d.Y - FrameSize * _scale * 0.26;
        _bubbles.Add(new Bubble
        {
            X = bx + (_rng.NextDouble() - 0.5) * 10,
            Y = by,
            Radius = RandRange(2.0, 6.0) * Math.Max(0.6, _scale),
            Rise = RandRange(22, 46),
            Wobble = RandRange(6, 16),
            Phase = _rng.NextDouble() * Math.Tau,
            Life = RandRange(1.2, 2.4),
            Age = 0,
        });
    }

    private void UpdateBubbles(double dt)
    {
        for (int i = _bubbles.Count - 1; i >= 0; i--)
        {
            var b = _bubbles[i];
            b.Age += dt;
            b.Y -= b.Rise * dt;
            b.Phase += dt * 4;
            b.X += Math.Sin(b.Phase) * b.Wobble * dt;
            b.Radius += dt * 1.5;          // bubbles expand slightly as they rise
            if (b.Age >= b.Life || b.Y < -10)
                _bubbles.RemoveAt(i);
        }
    }

    // ---- harpoons ---------------------------------------------------------

    private void SpawnHarpoon(Diver d)
    {
        int sign = d.FlipX ? -1 : 1;
        double hx = d.X + sign * FrameSize * _scale * 0.34;
        double hy = d.Y - FrameSize * _scale * 0.04;
        double v = 520 + _baseSpeed; // fast
        _harpoons.Add(new Harpoon
        {
            X = hx, Y = hy,
            Vx = sign * v,
            Vy = -20,            // a touch upward, then gravity pulls it down -> it "misses"
            Life = 2.2,
        });
    }

    private void UpdateHarpoons(double dt)
    {
        for (int i = _harpoons.Count - 1; i >= 0; i--)
        {
            var h = _harpoons[i];
            h.Vy += 240 * dt;     // gravity: the shot droops and misses
            h.X += h.Vx * dt;
            h.Y += h.Vy * dt;
            h.Life -= dt;
            if (h.Life <= 0 || h.X < -200 || h.X > _canvas.Width + 200 || h.Y > _canvas.Height + 200)
                _harpoons.RemoveAt(i);
        }
    }

    // ---- render -----------------------------------------------------------

    public void Render(Graphics g, Size canvasSize)
    {
        if (!_loaded) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        // Bubbles behind the divers.
        foreach (var b in _bubbles)
            DrawBubble(g, b);

        foreach (var d in _divers)
            DrawDiver(g, d);

        // Harpoons in front.
        foreach (var h in _harpoons)
            DrawHarpoon(g, h);
    }

    private void DrawDiver(Graphics g, Diver d)
    {
        SpriteSheet sheet;
        int frame;
        switch (d.State)
        {
            case DiverState.Hunt:
                sheet = _hunt!; frame = d.ActionFrame; break;
            case DiverState.Flee:
                sheet = _flee!; frame = d.ActionFrame; break;
            default:
                // Swimming: pick the spin frame that matches the heading direction.
                sheet = _spin!;
                double deg = (d.Heading * 180 / Math.PI) % 360;
                if (deg < 0) deg += 360;
                frame = (int)Math.Round(deg / 360.0 * sheet.Count) % sheet.Count;
                break;
        }
        DrawFrame(g, sheet, frame, (float)d.X, (float)d.Y, (float)_scale, d.FlipX);
    }

    private void DrawFrame(Graphics g, SpriteSheet s, int idx, float cx, float cy, float scale, bool flip)
    {
        idx = Math.Clamp(idx, 0, s.Count - 1);
        var src = s.Frames[idx];
        float w = src.Width * scale, h = src.Height * scale;
        var state = g.Save();
        g.TranslateTransform(cx, cy);
        if (flip) g.ScaleTransform(-1, 1);
        g.DrawImage(s.Image, new RectangleF(-w / 2, -h / 2, w, h),
            new RectangleF(src.X, src.Y, src.Width, src.Height), GraphicsUnit.Pixel);
        g.Restore(state);
    }

    private void DrawHarpoon(Graphics g, Harpoon h)
    {
        if (_harpoon is null) return;
        double angle = Math.Atan2(h.Vy, h.Vx) * 180 / Math.PI;
        float w = _harpoon.Width * (float)_scale * 0.8f;
        float hh = _harpoon.Height * (float)_scale * 0.8f;
        var state = g.Save();
        g.TranslateTransform((float)h.X, (float)h.Y);
        g.RotateTransform((float)angle);
        g.DrawImage(_harpoon, new RectangleF(-w / 2, -hh / 2, w, hh));
        g.Restore(state);
    }

    private void DrawBubble(Graphics g, Bubble b)
    {
        float fade = (float)Math.Max(0, 1 - b.Age / b.Life);
        int a = (int)(110 * fade);
        if (a <= 0) return;
        float r = (float)b.Radius;
        var rect = new RectangleF((float)b.X - r, (float)b.Y - r, r * 2, r * 2);
        using (var fill = new SolidBrush(Color.FromArgb(a / 2, 235, 245, 255)))
            g.FillEllipse(fill, rect);
        using (var pen = new Pen(Color.FromArgb(a, 255, 255, 255), Math.Max(1f, r * 0.18f)))
            g.DrawEllipse(pen, rect);
        // little highlight
        using var hl = new SolidBrush(Color.FromArgb(a, 255, 255, 255));
        g.FillEllipse(hl, (float)b.X - r * 0.1f, (float)b.Y - r * 0.45f, r * 0.5f, r * 0.5f);
    }

    // ---- helpers ----------------------------------------------------------

    private double RandRange(double a, double b) => a + _rng.NextDouble() * (b - a);

    private static double Approach(double cur, double target, double maxStep)
    {
        double diff = target - cur;
        if (Math.Abs(diff) <= maxStep) return target;
        return cur + Math.Sign(diff) * maxStep;
    }

    // shortest-path angular approach
    private static double ApproachAngle(double cur, double target, double maxStep)
    {
        double diff = Math.Atan2(Math.Sin(target - cur), Math.Cos(target - cur));
        if (Math.Abs(diff) <= maxStep) return target;
        return cur + Math.Sign(diff) * maxStep;
    }

    // ---- state ------------------------------------------------------------

    private enum DiverState { Swim, Hunt, Flee }

    private sealed class Diver
    {
        public double X, Y;
        public double Heading, TargetHeading;
        public double Speed;
        public double HeadingTimer;
        public double NextSpecial;
        public DiverState State = DiverState.Swim;
        public int ActionFrame;
        public double FrameTime;
        public bool ActionDone;
        public bool Fired;
        public bool FlipX;
        public double FleeTime;
        public double BubbleTimer;
    }

    private sealed class Bubble
    {
        public double X, Y, Radius, Rise, Wobble, Phase, Life, Age;
    }

    private sealed class Harpoon
    {
        public double X, Y, Vx, Vy, Life;
    }
}
