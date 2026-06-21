using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ScreenSaverOverlay.Ipc;

/// <summary>One status line received from a Claude Code hook.</summary>
public sealed record StatusLine(DateTime Time, string Kind, string Text);

/// <summary>
/// Receives Claude Code work-status events over loopback UDP and keeps a rolling buffer of the
/// most recent lines for the on-screen console. UDP is deliberate: a hook fires a datagram and
/// moves on — if this app isn't listening the packet is simply dropped, so the hook never blocks
/// or fails. Hooks send <c>"kind|text"</c> (e.g. <c>"tool|-> Bash git push"</c>).
/// </summary>
public static class ClaudeStatusBus
{
    /// <summary>Loopback port the hook script sends to. Keep in sync with the hook script.</summary>
    public const int Port = 47921;

    private const int Capacity = 400;
    private static readonly object Lock = new();
    private static readonly LinkedList<StatusLine> Lines = new();
    private static UdpClient? _udp;
    private static volatile bool _started;

    public static DateTime LastReceived { get; private set; }
    public static bool Listening { get; private set; }

    /// <summary>
    /// True while a Claude turn is in progress (a prompt/tool/notification arrived and no turn-end
    /// has come yet). Lets the console show a "thinking" state during long reasoning gaps where no
    /// event fires — instead of falsely going idle.
    /// </summary>
    public static bool Working { get; private set; }

    public static void Start()
    {
        if (_started) return;
        _started = true;
        try
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, Port));
            Listening = true;
            _ = Task.Run(ReceiveLoopAsync);
            Add("sys", $"monitor listening on udp 127.0.0.1:{Port}");
        }
        catch (Exception ex)
        {
            Listening = false;
            Add("sys", "monitor bind failed: " + ex.Message);
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
        // each datagram may contain several newline-separated lines
        foreach (var part in raw.Split('\n'))
        {
            var line = part.Trim();
            if (line.Length == 0) continue;
            string kind = "info", text = line;
            int bar = line.IndexOf('|');
            if (bar is > 0 and < 16) { kind = line[..bar].Trim(); text = line[(bar + 1)..].Trim(); }
            Add(kind, text);
        }
    }

    /// <summary>Inject a line directly (used for local/system messages).</summary>
    public static void Add(string kind, string text)
    {
        lock (Lock)
        {
            Lines.AddLast(new StatusLine(DateTime.Now, kind, text));
            while (Lines.Count > Capacity) Lines.RemoveFirst();
            LastReceived = DateTime.Now;
            Working = kind switch
            {
                "prompt" or "tool" or "notify" or "think" => true,  // a turn is underway
                "say" or "idle" => false,                            // turn ended
                _ => Working,
            };
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
