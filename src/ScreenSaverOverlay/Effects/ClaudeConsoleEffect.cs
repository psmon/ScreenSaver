using System.Drawing.Drawing2D;
using ScreenSaverOverlay.Ipc;
using ScreenSaverOverlay.Settings;

namespace ScreenSaverOverlay.Effects;

/// <summary>
/// A terminal-style monitor pinned to the top-left that renders the live Claude Code status
/// stream (received over UDP by <see cref="ClaudeStatusBus"/>). Lines are colour-coded by kind
/// and scroll like a console, with a blinking cursor and a pulsing "live" indicator.
/// </summary>
public sealed class ClaudeConsoleEffect : IEffect
{
    public string Id => "claude-console";
    public string DisplayName => "Claude Code Monitor (console)";

    private Size _canvas;
    private double _clock;
    private float _fontPt = 11f;
    private Point _origin;          // top-left of the PRIMARY screen within the canvas
    private Font _font = null!;
    private Font _headFont = null!;
    private float _charW;
    private float _lineH;
    private float _tsW;

    public void Initialize(Size canvasSize, AppSettings settings)
    {
        _canvas = canvasSize;
        ClaudeStatusBus.Start();

        _fontPt = (float)Math.Clamp(settings.Size * 0.11, 9.0, 18.0);
        _font?.Dispose();
        _headFont?.Dispose();
        _font = new Font("Consolas", _fontPt, FontStyle.Regular);
        _headFont = new Font("Consolas", _fontPt + 1f, FontStyle.Bold);

        // Place the panel on the primary monitor's top-left, mapped into canvas coordinates
        // (the canvas spans the whole virtual desktop, whose origin may be a different monitor).
        var vs = SystemInformation.VirtualScreen;
        var pr = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, canvasSize.Width, canvasSize.Height);
        _origin = new Point(pr.X - vs.X, pr.Y - vs.Y);

        using var tmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(tmp);
        var sz = g.MeasureString("MMMMMMMMMM", _font);
        _charW = sz.Width / 10f;
        _lineH = sz.Height * 0.92f;
        _tsW = g.MeasureString("00:00:00", _font).Width;
    }

    public void Update(double dt) => _clock += dt;

    public void Render(Graphics g, Size canvasSize)
    {
        const int margin = 26;
        int panelW = (int)Math.Clamp(canvasSize.Width * 0.34, 540, 820);
        int panelH = (int)Math.Clamp(canvasSize.Height * 0.52, 300, 900);
        int x = _origin.X + margin, y = _origin.Y + margin;
        int pad = 14;
        int headH = (int)(_lineH + 14);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var panel = new Rectangle(x, y, panelW, panelH);

        // panel body + header
        using (var bg = new SolidBrush(Color.FromArgb(214, 9, 12, 18)))
        using (var path = Rounded(panel, 10))
            g.FillPath(bg, path);
        using (var head = new SolidBrush(Color.FromArgb(235, 18, 22, 32)))
        using (var hpath = Rounded(new Rectangle(x, y, panelW, headH), 10))
            g.FillPath(head, hpath);
        using (var border = new Pen(Color.FromArgb(170, 55, 75, 110), 1.4f))
        using (var path = Rounded(panel, 10))
            g.DrawPath(border, path);

        // header: pulsing dot + title + live status (right)
        double pulse = 0.5 + 0.5 * Math.Sin(_clock * 3.0);
        bool live = ClaudeStatusBus.Listening && (DateTime.Now - ClaudeStatusBus.LastReceived).TotalSeconds < 8;
        var dotColor = live ? Color.FromArgb((int)(120 + 135 * pulse), 60, 230, 130)
                            : Color.FromArgb(170, 120, 130, 140);
        using (var dot = new SolidBrush(dotColor))
            g.FillEllipse(dot, x + pad, y + headH / 2 - 5, 10, 10);
        using (var title = new SolidBrush(Color.FromArgb(235, 210, 220, 235)))
            g.DrawString("claude code · monitor", _headFont, title, x + pad + 18, y + 7);
        string status = ClaudeStatusBus.Listening ? (live ? "LIVE" : "idle") : "offline";
        using (var st = new SolidBrush(live ? Color.FromArgb(230, 80, 230, 140) : Color.FromArgb(190, 150, 160, 170)))
        {
            var ssz = g.MeasureString(status, _font);
            g.DrawString(status, _font, st, x + panelW - pad - ssz.Width, y + 9);
        }

        // body lines
        var clip = new Rectangle(x + pad, y + headH, panelW - pad * 2, panelH - headH - pad);
        var oldClip = g.Clip;
        g.SetClip(clip);

        float tsCol = x + pad;
        float textCol = tsCol + _tsW + 12;            // text starts after the timestamp column
        int visible = Math.Max(1, (int)(clip.Height / _lineH));
        int maxChars = Math.Max(8, (int)((clip.Right - textCol) / _charW));
        var lines = ClaudeStatusBus.Recent(visible);

        float ly = clip.Bottom - lines.Count * _lineH;   // newest at the bottom
        foreach (var line in lines)
        {
            string ts = line.Time.ToString("HH:mm:ss");
            string text = line.Text.Length > maxChars ? line.Text[..(maxChars - 1)] + "…" : line.Text;
            using (var tsb = new SolidBrush(Color.FromArgb(150, 110, 120, 135)))
                g.DrawString(ts, _font, tsb, tsCol, ly);
            using (var tb = new SolidBrush(ColorFor(line.Kind)))
                g.DrawString(text, _font, tb, textCol, ly);
            ly += _lineH;
        }

        // blinking cursor on the line after the last entry
        if (((int)(_clock * 2)) % 2 == 0)
        {
            float cy = clip.Bottom - _lineH;
            using var cur = new SolidBrush(Color.FromArgb(220, 120, 230, 150));
            g.FillRectangle(cur, textCol, cy + 3, _charW * 0.9f, _lineH - 6);
        }

        g.Clip = oldClip;
    }

    private static Color ColorFor(string kind) => kind switch
    {
        "prompt" => Color.FromArgb(240, 232, 236, 245),
        "tool" => Color.FromArgb(235, 90, 215, 240),
        "done" => Color.FromArgb(230, 70, 215, 150),
        "notify" => Color.FromArgb(235, 250, 200, 90),
        "error" => Color.FromArgb(235, 245, 120, 120),
        "idle" => Color.FromArgb(200, 160, 170, 185),
        "sys" => Color.FromArgb(190, 120, 135, 155),
        _ => Color.FromArgb(225, 205, 215, 225),
    };

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
