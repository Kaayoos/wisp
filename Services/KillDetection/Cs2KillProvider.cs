using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wisp.Models;

namespace Wisp.Services.KillDetection
{
    /// <summary>
    /// Counter-Strike 2 kills via Valve's OFFICIAL Game State Integration: a config file installed in
    /// the game's cfg folder (see <see cref="Cs2GsiInstaller"/>) makes the GAME push JSON state to a
    /// local HTTP endpoint we listen on. Nothing touches the game process - it talks to us.
    ///
    /// The listener is a raw TcpListener with a minimal HTTP parse, NOT HttpListener: HttpListener
    /// rides HTTP.SYS, whose URL reservation is denied to non-elevated processes, and Wisp must never
    /// need admin. GSI posts are small (&lt;10 KB), sequential, and loopback-only.
    ///
    /// A kill = the player's match_stats.kills counter increasing while provider.steamid matches
    /// player.steamid (so spectating/casting someone else's kills never fires). The first frame after
    /// Start only baselines; a counter DECREASE (new match / team swap) re-baselines silently.
    /// </summary>
    public class Cs2KillProvider : IKillProvider
    {
        private readonly AppSettings _settings;

        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _acceptLoop;
        private readonly object _lifecycleLock = new();

        private int _lastKills = -1;                  // -1 = not baselined yet
        private DateTime _startFailedUtc = DateTime.MinValue; // port-busy backoff (the router retries every tick)

        public string ProcessName => "cs2";
        public string GameName => "Counter-Strike 2";
        public bool RequiresForeground => false; // the game pushes to us regardless of focus
        public bool IsRunning => _listener != null;

        public event Action<DateTime>? KillDetected;

        public Cs2KillProvider(AppSettings settings)
        {
            _settings = settings;
        }

        public void Start()
        {
            lock (_lifecycleLock)
            {
                if (_listener != null) return;
                if ((DateTime.UtcNow - _startFailedUtc).TotalSeconds < 60) return; // don't hammer a busy port

                int port = _settings.Cs2GsiPort;
                try
                {
                    _lastKills = -1;
                    _cts = new CancellationTokenSource();
                    _listener = new TcpListener(IPAddress.Loopback, port);
                    _listener.Start();
                    var token = _cts.Token;
                    var listener = _listener;
                    _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, token));
                }
                catch (Exception ex)
                {
                    Logger.Warn($"CS2 kill detection could not listen on port {port}: {ex.Message}. Retrying in 60s; change the GSI port in Settings if this persists.");
                    _startFailedUtc = DateTime.UtcNow;
                    try { _listener?.Stop(); } catch { }
                    _listener = null;
                    _cts?.Dispose();
                    _cts = null;
                }
            }
        }

        public void Stop()
        {
            lock (_lifecycleLock)
            {
                if (_listener == null) return;
                try { _cts?.Cancel(); } catch { }
                try { _listener.Stop(); } catch { }
                _listener = null;
                try { _acceptLoop?.Wait(500); } catch { }
                _acceptLoop = null;
                _cts?.Dispose();
                _cts = null;
                _startFailedUtc = DateTime.MinValue; // a manual stop clears the backoff
            }
        }

        public void Dispose() => Stop();

        private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient? client = null;
                try
                {
                    client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                    await HandleRequestAsync(client, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested) break; // listener stopped under us - normal shutdown
                    Logger.Warn($"CS2 GSI request handling failed: {ex.Message}");
                }
                finally
                {
                    try { client?.Dispose(); } catch { }
                }
            }
        }

        private async Task HandleRequestAsync(TcpClient client, CancellationToken token)
        {
            // Bound each request so a stalled connection can never wedge the loop.
            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            requestCts.CancelAfter(TimeSpan.FromSeconds(3));
            var ct = requestCts.Token;

            var stream = client.GetStream();

            // Read the head (until CRLFCRLF), then exactly Content-Length body bytes.
            var buffer = new byte[8192];
            var head = new MemoryStream();
            int headEnd = -1;
            while (headEnd < 0)
            {
                int read = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                if (read <= 0) return;
                head.Write(buffer, 0, read);
                if (head.Length > 64 * 1024) return; // absurd head: not GSI, drop it
                headEnd = FindHeaderEnd(head.GetBuffer(), (int)head.Length);
            }

            string headText = Encoding.ASCII.GetString(head.GetBuffer(), 0, headEnd);
            int contentLength = ParseContentLength(headText);
            if (contentLength <= 0 || contentLength > 1024 * 1024) { await RespondOkAsync(stream, ct).ConfigureAwait(false); return; }

            var body = new MemoryStream();
            int already = (int)head.Length - (headEnd + 4);
            if (already > 0) body.Write(head.GetBuffer(), headEnd + 4, already);
            while (body.Length < contentLength)
            {
                int read = await stream.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, contentLength - body.Length), ct).ConfigureAwait(false);
                if (read <= 0) break;
                body.Write(buffer, 0, read);
            }

            // Ack first so the game never waits on our parsing.
            await RespondOkAsync(stream, ct).ConfigureAwait(false);

            if (body.Length >= contentLength)
                ProcessPayload(body.GetBuffer(), (int)body.Length);
        }

        private static int FindHeaderEnd(byte[] data, int length)
        {
            for (int i = 3; i < length; i++)
                if (data[i - 3] == '\r' && data[i - 2] == '\n' && data[i - 1] == '\r' && data[i] == '\n')
                    return i - 3;
            return -1;
        }

        private static int ParseContentLength(string head)
        {
            foreach (string line in head.Split('\n'))
            {
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                if (line.Substring(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(line.Substring(colon + 1).Trim(), out int len))
                    return len;
            }
            return -1;
        }

        private static async Task RespondOkAsync(NetworkStream stream, CancellationToken ct)
        {
            byte[] ok = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(ok, 0, ok.Length, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        private void ProcessPayload(byte[] json, int length)
        {
            try
            {
                using var doc = JsonDocument.Parse(new ReadOnlyMemory<byte>(json, 0, length));
                var root = doc.RootElement;

                // Only trust frames about OURSELVES: when spectating/casting, "player" is whoever is
                // being observed while "provider" stays the local user - steamids then differ.
                if (!root.TryGetProperty("provider", out var provider) ||
                    !root.TryGetProperty("player", out var player) ||
                    !provider.TryGetProperty("steamid", out var providerId) ||
                    !player.TryGetProperty("steamid", out var playerId) ||
                    !string.Equals(providerId.GetString(), playerId.GetString(), StringComparison.Ordinal))
                    return;

                if (!player.TryGetProperty("match_stats", out var stats) ||
                    !stats.TryGetProperty("kills", out var killsEl) ||
                    !killsEl.TryGetInt32(out int kills))
                    return;

                if (_lastKills < 0 || kills < _lastKills)
                {
                    // First frame after Start, or a counter reset (new match / mp_restartgame / team
                    // swap): baseline only, never fire.
                    _lastKills = kills;
                    return;
                }

                if (kills > _lastKills)
                {
                    // A jump of 2+ in one frame (rare: simultaneous multi-kill inside one GSI throttle
                    // window) still fires once - one marker for that instant is the sane outcome.
                    _lastKills = kills;
                    KillDetected?.Invoke(DateTime.UtcNow);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"CS2 GSI payload parse failed: {ex.Message}");
            }
        }
    }
}
