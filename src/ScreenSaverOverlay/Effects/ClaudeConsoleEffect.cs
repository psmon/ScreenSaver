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

        // header: pulsing dot + title + turn state (right)
        int thinkingCount = ClaudeStatusBus.ThinkingCount;
        bool working = thinkingCount > 0;
        double pulse = 0.5 + 0.5 * Math.Sin(_clock * 4.0);
        var dotColor = working ? Color.FromArgb((int)(120 + 135 * pulse), 150, 130, 245)   // thinking = lavender pulse
                     : ClaudeStatusBus.Listening ? Color.FromArgb(180, 70, 200, 130)        // ready = dim green
                     : Color.FromArgb(170, 120, 130, 140);
        using (var dot = new SolidBrush(dotColor))
            g.FillEllipse(dot, x + pad, y + headH / 2 - 5, 10, 10);
        using (var title = new SolidBrush(Color.FromArgb(235, 210, 220, 235)))
            g.DrawString("claude code", _headFont, title, x + pad + 18, y + 7);

        // right-aligned state: animated "thinking" while a turn runs, else idle/offline
        string status; Color statusColor;
        if (working)
        {
            char[] spin = { '|', '/', '-', '\\' };
            string mult = thinkingCount > 1 ? $" x{thinkingCount}" : "";   // multiple Claude sessions
            status = "thinking " + spin[((int)(_clock * 8)) % spin.Length] + mult;
            statusColor = Color.FromArgb(240, 205, 195, 248);
        }
        else
        {
            status = ClaudeStatusBus.Listening ? "idle" : "offline";
            statusColor = Color.FromArgb(195, 150, 160, 175);
        }
        using (var st = new SolidBrush(statusColor))
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
        int visibleRows = Math.Max(1, (int)(clip.Height / _lineH));
        int maxChars = Math.Max(10, (int)((clip.Right - textCol) / _charW));

        var recent = ClaudeStatusBus.Recent(60);
        // when more than one Claude session is sending, tag each line so they're distinguishable
        bool multiSession = recent.Select(e => e.Session)
            .Where(s => !string.IsNullOrEmpty(s)).Distinct().Take(2).Count() > 1;

        // Wrap each status line into one or more rows so long/rich text shows in full
        // (a single Bash command or Claude's narration spills onto several console rows).
        var rows = new List<(StatusLine Line, string Text, bool First)>();
        foreach (var entry in recent)
        {
            string display = multiSession && entry.Session.Length > 0
                ? $"[{Tag(entry.Session)}] {entry.Text}"
                : entry.Text;
            var chunks = WrapText(display, maxChars);
            for (int i = 0; i < chunks.Count; i++)
                rows.Add((entry, chunks[i], i == 0));
        }
        int startRow = Math.Max(0, rows.Count - visibleRows);

        float ly = clip.Bottom - (rows.Count - startRow) * _lineH;   // newest at the bottom
        string lastText = "";
        for (int r = startRow; r < rows.Count; r++)
        {
            var (line, text, first) = rows[r];
            if (first)
                using (var tsb = new SolidBrush(Color.FromArgb(150, 110, 120, 135)))
                    g.DrawString(line.Time.ToString("HH:mm:ss"), _font, tsb, tsCol, ly);
            using (var tb = new SolidBrush(ColorFor(line.Kind)))
                g.DrawString(text, _font, tb, textCol, ly);
            lastText = text;
            ly += _lineH;
        }

        // blinking block cursor right after the last character
        if (((int)(_clock * 2)) % 2 == 0)
        {
            float cy = clip.Bottom - _lineH;
            float cx = textCol + lastText.Length * _charW;
            using var cur = new SolidBrush(Color.FromArgb(220, 120, 230, 150));
            g.FillRectangle(cur, cx, cy + 3, _charW * 0.9f, _lineH - 6);
        }

        g.Clip = oldClip;
    }

    private static Color ColorFor(string kind) => kind switch
    {
        "prompt" => Color.FromArgb(245, 250, 235, 150),  // user prompt — soft yellow
        "say" => Color.FromArgb(245, 225, 220, 248),     // Claude narration — lavender white
        "tool" => Color.FromArgb(235, 90, 215, 240),     // tool call — cyan
        "done" => Color.FromArgb(230, 70, 215, 150),     // tool done — green
        "notify" => Color.FromArgb(235, 250, 200, 90),   // notification — amber
        "error" => Color.FromArgb(235, 245, 120, 120),
        "idle" => Color.FromArgb(200, 160, 170, 185),
        "sys" => Color.FromArgb(190, 120, 135, 155),
        _ => Color.FromArgb(225, 205, 215, 225),
    };

    /// <summary>Short, stable tag for a session id (last 4 chars).</summary>
    private static string Tag(string session) =>
        session.Length <= 4 ? session : session[^4..];

    /// <summary>Greedy word-wrap to <paramref name="maxChars"/> columns (monospace).</summary>
    private static List<string> WrapText(string s, int maxChars)
    {
        var rows = new List<string>();
        if (string.IsNullOrEmpty(s)) { rows.Add(""); return rows; }
        string cur = "";
        foreach (var word in s.Split(' '))
        {
            var w = word;
            // a single word longer than the line: hard-split it
            while (w.Length > maxChars)
            {
                if (cur.Length > 0) { rows.Add(cur); cur = ""; }
                rows.Add(w[..maxChars]);
                w = w[maxChars..];
            }
            if (cur.Length == 0) cur = w;
            else if (cur.Length + 1 + w.Length <= maxChars) cur += " " + w;
            else { rows.Add(cur); cur = w; }
        }
        if (cur.Length > 0) rows.Add(cur);
        if (rows.Count == 0) rows.Add("");
        return rows;
    }

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
