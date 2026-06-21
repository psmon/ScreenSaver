using System.Drawing.Drawing2D;
using ScreenSaverOverlay.Settings;

namespace ScreenSaverOverlay.Effects;

/// <summary>
/// Configuration for a swimming sprite character: which asset folder and sheets to load and how
/// the swim sheet is laid out (directions × kick phases).
/// </summary>
public sealed record SpriteConfig(
    string Id,
    string DisplayName,
    string Folder,
    string SwimSheet,     // sheet used while swimming
    int Directions,       // how many facing directions the swim sheet encodes
    int KickPhases,       // frames per direction (1 = static facing; 2+ = fin-kick cycle)
    double KickFps,       // how fast to cycle kick phases
    string HuntSheet,
    string FleeSheet,
    string HarpoonAsset);

/// <summary>
/// Shared behaviour for a sprite character that swims around the overlay: dominant natural
/// directional swimming (the swim sheet indexed by heading), with rare HUNT (fires a detached
/// harpoon that always misses) and FLEE (a startled dash) actions, and bubbles trailing up.
/// Concrete characters just supply a <see cref="SpriteConfig"/>; the swim sheet may encode a
/// fin-kick cycle (multiple phases per direction) which animates as the character swims.
/// </summary>
public abstract class SwimmingSpriteEffect : IEffect
{
    protected abstract SpriteConfig Config { get; }

    public string Id => Config.Id;
    public string DisplayName => Config.DisplayName;

    private SpriteSheet? _swim, _flee, _hunt;
    private Bitmap? _harpoon;
    private bool _loaded;

    private Size _canvas;
    private double _scale = 1.0;
    private double _baseSpeed = 60;
    private double _kickClock;
    private readonly Random _rng = new();

    private readonly List<Diver> _divers = new();
    private readonly List<Bubble> _bubbles = new();
    private readonly List<Harpoon> _harpoons = new();

    private const int FrameSize = 192;

    public void Initialize(Size canvasSize, AppSettings settings)
    {
        _canvas = canvasSize;
        _scale = Math.Clamp(settings.Size * 2.0, 120, 460) / FrameSize;
        _baseSpeed = 55.0 * settings.Speed;

        LoadAssets();
        _bubbles.Clear();
        _harpoons.Clear();
        _divers.Clear();
        _kickClock = 0;

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
                KickOffset = _rng.NextDouble() * 10,
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
            string dir = Path.Combine(AppContext.BaseDirectory, "Assets", "sprites", Config.Folder);
            _swim = SpriteSheet.Load(Path.Combine(dir, Config.SwimSheet + ".png"), Path.Combine(dir, Config.SwimSheet + ".json"));
            _flee = SpriteSheet.Load(Path.Combine(dir, Config.FleeSheet + ".png"), Path.Combine(dir, Config.FleeSheet + ".json"));
            _hunt = SpriteSheet.Load(Path.Combine(dir, Config.HuntSheet + ".png"), Path.Combine(dir, Config.HuntSheet + ".json"));
            _harpoon = new Bitmap(Path.Combine(dir, Config.HarpoonAsset));
            _loaded = true;
        }
        catch
        {
            _loaded = false;
        }
    }

    // ---- update -----------------------------------------------------------

    public void Update(double dt)
    {
        dt = Math.Clamp(dt, 0, 0.1);
        if (!_loaded) return;
        _kickClock += dt;
        foreach (var d in _divers) UpdateDiver(d, dt);
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
        d.BubbleTimer -= dt;
        if (d.BubbleTimer <= 0)
        {
            d.BubbleTimer = RandRange(0.18, 0.42);
            EmitBubble(d);
        }
    }

    private void UpdateSwim(Diver d, double dt)
    {
        d.HeadingTimer -= dt;
        if (d.HeadingTimer <= 0)
        {
            d.HeadingTimer = RandRange(1.6, 3.6);
            d.TargetHeading = d.Heading + (_rng.NextDouble() - 0.5) * Math.PI;
        }
        SteerEdgesAware(d);
        d.Heading = ApproachAngle(d.Heading, d.TargetHeading, 1.6 * dt);
        Move(d, d.Speed, dt);

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
        d.ActionFrame = 0; d.FrameTime = 0; d.ActionDone = false; d.Fired = false;
        d.FlipX = Math.Cos(d.Heading) < 0;
        d.Speed *= 0.25;
    }

    private void UpdateHunt(Diver d, double dt)
    {
        Move(d, d.Speed, dt);
        d.Speed = Approach(d.Speed, _baseSpeed * 0.05, 40 * dt);
        AdvanceAction(d, _hunt!);
        if (!d.Fired && d.ActionFrame >= 1) { d.Fired = true; SpawnHarpoon(d); }
        if (d.ActionDone) ExitSpecial(d);
    }

    private void EnterFlee(Diver d)
    {
        d.State = DiverState.Flee;
        d.ActionFrame = 0; d.FrameTime = 0;
        d.FleeTime = RandRange(1.6, 2.6);
        double dir = (d.X < _canvas.Width / 2.0) ? 0 : Math.PI;
        dir += (_rng.NextDouble() - 0.5) * 0.7;
        d.Heading = dir; d.TargetHeading = dir;
        d.Speed = _baseSpeed * 3.2;
        d.FlipX = Math.Cos(dir) > 0; // flee art faces left; flip if darting right
    }

    private void UpdateFlee(Diver d, double dt)
    {
        d.FleeTime -= dt;
        SteerEdgesAware(d);
        d.Heading = ApproachAngle(d.Heading, d.TargetHeading, 2.0 * dt);
        Move(d, d.Speed, dt);
        AdvanceAction(d, _flee!, loop: true);
        if (d.FleeTime <= 0) ExitSpecial(d);
    }

    private void ExitSpecial(Diver d)
    {
        d.State = DiverState.Swim;
        d.Speed = _baseSpeed * (0.8 + _rng.NextDouble() * 0.5);
        d.NextSpecial = RandRange(10, 22);
        d.FlipX = false;
    }

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
        double m = FrameSize * _scale * 0.6;
        if (d.X < -m) d.X = _canvas.Width + m; else if (d.X > _canvas.Width + m) d.X = -m;
        if (d.Y < -m) d.Y = _canvas.Height + m; else if (d.Y > _canvas.Height + m) d.Y = -m;
    }

    private void SteerEdgesAware(Diver d)
    {
        double margin = FrameSize * _scale * 0.7;
        if (d.X < margin || d.X > _canvas.Width - margin || d.Y < margin || d.Y > _canvas.Height - margin)
            d.TargetHeading = Math.Atan2(_canvas.Height / 2.0 - d.Y, _canvas.Width / 2.0 - d.X);
    }

    // ---- bubbles (shared, procedural — reused unchanged across characters) -

    private void EmitBubble(Diver d)
    {
        int sign = d.FlipX ? -1 : 1;
        _bubbles.Add(new Bubble
        {
            X = d.X + sign * FrameSize * _scale * 0.14 + (_rng.NextDouble() - 0.5) * 10,
            Y = d.Y - FrameSize * _scale * 0.26,
            Radius = RandRange(2.0, 6.0) * Math.Max(0.6, _scale),
            Rise = RandRange(22, 46), Wobble = RandRange(6, 16),
            Phase = _rng.NextDouble() * Math.Tau, Life = RandRange(1.2, 2.4), Age = 0,
        });
    }

    private void UpdateBubbles(double dt)
    {
        for (int i = _bubbles.Count - 1; i >= 0; i--)
        {
            var b = _bubbles[i];
            b.Age += dt; b.Y -= b.Rise * dt; b.Phase += dt * 4;
            b.X += Math.Sin(b.Phase) * b.Wobble * dt; b.Radius += dt * 1.5;
            if (b.Age >= b.Life || b.Y < -10) _bubbles.RemoveAt(i);
        }
    }

    // ---- harpoons ---------------------------------------------------------

    private void SpawnHarpoon(Diver d)
    {
        int sign = d.FlipX ? -1 : 1;
        _harpoons.Add(new Harpoon
        {
            X = d.X + sign * FrameSize * _scale * 0.34,
            Y = d.Y - FrameSize * _scale * 0.04,
            Vx = sign * (520 + _baseSpeed), Vy = -20, Life = 2.2,
        });
    }

    private void UpdateHarpoons(double dt)
    {
        for (int i = _harpoons.Count - 1; i >= 0; i--)
        {
            var h = _harpoons[i];
            h.Vy += 240 * dt; h.X += h.Vx * dt; h.Y += h.Vy * dt; h.Life -= dt;
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

        foreach (var b in _bubbles) DrawBubble(g, b);
        foreach (var d in _divers) DrawDiver(g, d);
        foreach (var h in _harpoons) DrawHarpoon(g, h);
    }

    private void DrawDiver(Graphics g, Diver d)
    {
        SpriteSheet sheet; int frame;
        switch (d.State)
        {
            case DiverState.Hunt: sheet = _hunt!; frame = d.ActionFrame; break;
            case DiverState.Flee: sheet = _flee!; frame = d.ActionFrame; break;
            default:
                sheet = _swim!;
                double deg = (d.Heading * 180 / Math.PI) % 360; if (deg < 0) deg += 360;
                int dirIdx = (int)Math.Round(deg / 360.0 * Config.Directions) % Config.Directions;
                int phase = Config.KickPhases > 1
                    ? (int)((_kickClock + d.KickOffset) * Config.KickFps) % Config.KickPhases
                    : 0;
                frame = dirIdx * Config.KickPhases + phase;
                break;
        }
        DrawFrame(g, sheet, frame, (float)d.X, (float)d.Y, (float)_scale, d.FlipX);
    }

    private static void DrawFrame(Graphics g, SpriteSheet s, int idx, float cx, float cy, float scale, bool flip)
    {
        idx = Math.Clamp(idx, 0, s.Count - 1);
        var src = s.Frames[idx];
        float w = src.Width * scale, h = src.Height * scale;
        var st = g.Save();
        g.TranslateTransform(cx, cy);
        if (flip) g.ScaleTransform(-1, 1);
        g.DrawImage(s.Image, new RectangleF(-w / 2, -h / 2, w, h),
            new RectangleF(src.X, src.Y, src.Width, src.Height), GraphicsUnit.Pixel);
        g.Restore(st);
    }

    private void DrawHarpoon(Graphics g, Harpoon h)
    {
        if (_harpoon is null) return;
        double angle = Math.Atan2(h.Vy, h.Vx) * 180 / Math.PI;
        float w = _harpoon.Width * (float)_scale * 0.8f, hh = _harpoon.Height * (float)_scale * 0.8f;
        var st = g.Save();
        g.TranslateTransform((float)h.X, (float)h.Y);
        g.RotateTransform((float)angle);
        g.DrawImage(_harpoon, new RectangleF(-w / 2, -hh / 2, w, hh));
        g.Restore(st);
    }

    private static void DrawBubble(Graphics g, Bubble b)
    {
        float fade = (float)Math.Max(0, 1 - b.Age / b.Life);
        int a = (int)(110 * fade);
        if (a <= 0) return;
        float r = (float)b.Radius;
        var rect = new RectangleF((float)b.X - r, (float)b.Y - r, r * 2, r * 2);
        using (var fill = new SolidBrush(Color.FromArgb(a / 2, 235, 245, 255))) g.FillEllipse(fill, rect);
        using (var pen = new Pen(Color.FromArgb(a, 255, 255, 255), Math.Max(1f, r * 0.18f))) g.DrawEllipse(pen, rect);
        using var hl = new SolidBrush(Color.FromArgb(a, 255, 255, 255));
        g.FillEllipse(hl, (float)b.X - r * 0.1f, (float)b.Y - r * 0.45f, r * 0.5f, r * 0.5f);
    }

    // ---- helpers ----------------------------------------------------------

    private double RandRange(double a, double b) => a + _rng.NextDouble() * (b - a);
    private static double Approach(double cur, double target, double maxStep)
    {
        double diff = target - cur;
        return Math.Abs(diff) <= maxStep ? target : cur + Math.Sign(diff) * maxStep;
    }
    private static double ApproachAngle(double cur, double target, double maxStep)
    {
        double diff = Math.Atan2(Math.Sin(target - cur), Math.Cos(target - cur));
        return Math.Abs(diff) <= maxStep ? target : cur + Math.Sign(diff) * maxStep;
    }

    private enum DiverState { Swim, Hunt, Flee }

    private sealed class Diver
    {
        public double X, Y, Heading, TargetHeading, Speed, HeadingTimer, NextSpecial, FrameTime, FleeTime, BubbleTimer, KickOffset;
        public DiverState State = DiverState.Swim;
        public int ActionFrame;
        public bool ActionDone, Fired, FlipX;
    }
    private sealed class Bubble { public double X, Y, Radius, Rise, Wobble, Phase, Life, Age; }
    private sealed class Harpoon { public double X, Y, Vx, Vy, Life; }
}
