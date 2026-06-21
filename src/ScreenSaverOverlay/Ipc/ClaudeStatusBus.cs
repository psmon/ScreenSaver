using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ScreenSaverOverlay.Ipc;

/// <summary>One status line received from a Claude Code hook.</summary>
public sealed record StatusLine(DateTime Time, string Kind, string Session, string Text);

/// <summary>
/// Receives Claude Code work-status events over loopback UDP and keeps a rolling buffer of the
/// most recent lines for the on-screen console. UDP is deliberate: a hook fires a datagram and
/// moves on — if this app isn't listening the packet is simply dropped, so the hook never blocks
/// or fails.
///
/// Wire format is <c>kind|session|text</c> (session may be empty for local/system lines). Turn
/// state ("thinking") is tracked <b>per session</b> so multiple concurrent Claude sessions don't
/// stomp on each other, a session that ends (or whose turn-end is missed) stops showing as
/// thinking, and a stale session times out instead of getting stuck.
/// </summary>
public static class ClaudeStatusBus
{
    /// <summary>Loopback port the hook script sends to. Keep in sync with the hook script.</summary>
    public const int Port = 47921;

    private const int Capacity = 400;
    private const double ThinkTimeoutSec = 120;   // a session not heard from this long isn't "thinking"
    private const double SessionTtlSec = 3600;    // forget sessions idle this long

    private static readonly object Lock = new();
    private static readonly LinkedList<StatusLine> Lines = new();
    private static readonly Dictionary<string, Sess> Sessions = new();
    private static UdpClient? _udp;
    private static volatile bool _started;

    public static DateTime LastReceived { get; private set; }
    public static bool Listening { get; private set; }

    private sealed class Sess
    {
        public DateTime LastEvent;
        public bool Thinking;
    }

    /// <summary>How many sessions are mid-turn (thinking) right now.</summary>
    public static int ThinkingCount
    {
        get
        {
            lock (Lock)
            {
                var now = DateTime.Now;
                int n = 0;
                foreach (var s in Sessions.Values)
                    if (s.Thinking && (now - s.LastEvent).TotalSeconds < ThinkTimeoutSec) n++;
                return n;
            }
        }
    }

    /// <summary>True if any session is currently mid-turn.</summary>
    public static bool Working => ThinkingCount > 0;

    public static void Start()
    {
        if (_started) return;
        _started = true;
        try
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, Port));
            Listening = true;
            _ = Task.Run(ReceiveLoopAsync);
            Add("sys", "", $"monitor listening on udp 127.0.0.1:{Port}");
        }
        catch (Exception ex)
        {
            Listening = false;
            Add("sys", "", "monitor bind failed: " + ex.Message);
        }
    }

    private static async Task ReceiveLoopAsync()
    {
        while (_udp is not null)
        {
            try
            {
                var result = await _udp.ReceiveAsync();
                Ingest(Encoding.UTF8.GetString(result.Buffer));
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
                await Task.Delay(100);
            }
        }
    }

    private static void Ingest(string raw)
    {
        raw = raw.Replace("\r", "").Trim();
        if (raw.Length == 0) return;

        foreach (var part in raw.Split('\n'))
        {
            var line = part.Trim();
            if (line.Length == 0) continue;

            // kind|session|text  (split on the first two bars only; text may contain bars)
            string kind = "info", session = "", text = line;
            int b1 = line.IndexOf('|');
            if (b1 is > 0 and < 16)
            {
                int b2 = line.IndexOf('|', b1 + 1);
                if (b2 > b1)
                {
                    kind = line[..b1];
                    session = line[(b1 + 1)..b2];
                    text = line[(b2 + 1)..];
                }
                else
                {
                    kind = line[..b1];        // legacy two-part form
                    text = line[(b1 + 1)..];
                }
            }
            Add(kind, session, text);
        }
    }

    /// <summary>Local/system line with no session.</summary>
    public static void Add(string kind, string text) => Add(kind, "", text);

    public static void Add(string kind, string session, string text)
    {
        lock (Lock)
        {
            var now = DateTime.Now;
            Lines.AddLast(new StatusLine(now, kind, session, text));
            while (Lines.Count > Capacity) Lines.RemoveFirst();
            LastReceived = now;

            if (kind == "end")
            {
                // session closed -> stop tracking it (its "thinking" must not linger)
                if (session.Length > 0) Sessions.Remove(session);
            }
            else if (session.Length > 0)
            {
                if (!Sessions.TryGetValue(session, out var s))
                    Sessions[session] = s = new Sess();
                s.LastEvent = now;
                s.Thinking = kind switch
                {
                    "prompt" or "tool" or "notify" or "think" => true,  // a turn is underway
                    "say" or "idle" => false,                            // turn ended
                    _ => s.Thinking,
                };
            }

            // prune long-idle sessions so the dictionary can't grow unbounded
            if (Sessions.Count > 0)
            {
                var stale = Sessions.Where(kv => (now - kv.Value.LastEvent).TotalSeconds > SessionTtlSec)
                                    .Select(kv => kv.Key).ToList();
                foreach (var k in stale) Sessions.Remove(k);
            }
        }
    }

    /// <summary>The most recent <paramref name="n"/> lines, oldest first.</summary>
    public static List<StatusLine> Recent(int n)
    {
        lock (Lock)
        {
            int skip = Math.Max(0, Lines.Count - n);
            return Lines.Skip(skip).ToList();
        }
    }
}
