using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;

namespace Wisp.Services.KillDetection
{
    /// <summary>
    /// League of Legends kills via Riot's OFFICIAL Live Client Data API - an HTTPS endpoint the game
    /// itself serves on loopback port 2999 during a match (no injection, no memory reads; using it is
    /// explicitly sanctioned by Riot). We poll the event list once a second and fire for each new
    /// ChampionKill whose killer is the active player.
    ///
    /// Details that matter:
    ///  • The endpoint uses Riot's self-signed certificate, so validation is bypassed - but ONLY for
    ///    loopback:2999; every other request this handler could ever see still validates normally.
    ///  • The event list is cumulative for the match, so Start() BASELINES on the first successful
    ///    read (highest EventID) and only events after that fire - stop/start never replays kills.
    ///  • Out-of-match the API simply refuses connections; that's normal and quietly ignored. A long
    ///    failure streak resets the baseline + cached player name so the next match re-baselines.
    ///  • Modern patches return the Riot ID ("Name#TAG"); older ones a bare name. Kills are matched
    ///    on the full string and on the part before '#', both case-insensitive.
    /// </summary>
    public class LolKillProvider : IKillProvider
    {
        private const string BaseUrl = "https://127.0.0.1:2999/liveclientdata";
        private const int PollMs = 1000;
        private const int FailureStreakToReset = 8; // ~8s of consecutive failures = match ended/loading

        private readonly HttpClient _http;
        private Timer? _timer;
        private readonly object _lifecycleLock = new();
        private readonly object _tickLock = new();

        private string? _activePlayer;   // cached per match; null until fetched
        private long _lastEventId = -1;  // highest processed EventID; -1 = not baselined yet
        private int _failureStreak;

        public string ProcessName => "League of Legends";
        public string GameName => "League of Legends";
        public bool RequiresForeground => false; // the local API works regardless of focus
        public bool IsRunning => _timer != null;

        public event Action<DateTime>? KillDetected;

        public LolKillProvider()
        {
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (request, cert, chain, errors) =>
                    errors == System.Net.Security.SslPolicyErrors.None ||
                    (request.RequestUri != null && request.RequestUri.IsLoopback && request.RequestUri.Port == 2999)
            };
            // Shorter than the poll interval so a hung request can't stack ticks (the tick guard
            // protects too, but this keeps each tick bounded).
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(900) };
        }

        public void Start()
        {
            lock (_lifecycleLock)
            {
                if (_timer != null) return;
                _activePlayer = null;
                _lastEventId = -1;
                _failureStreak = 0;
                _timer = new Timer(_ => Tick(), null, 0, PollMs);
            }
        }

        public void Stop()
        {
            lock (_lifecycleLock)
            {
                if (_timer == null) return;
                _timer.Dispose();
                _timer = null;
            }
        }

        public void Dispose()
        {
            Stop();
            _http.Dispose();
        }

        private void Tick()
        {
            if (!Monitor.TryEnter(_tickLock)) return;
            try
            {
                // Resolve who "we" are once per match.
                if (_activePlayer == null)
                {
                    string nameJson = _http.GetStringAsync($"{BaseUrl}/activeplayername").GetAwaiter().GetResult();
                    _activePlayer = JsonSerializer.Deserialize<string>(nameJson);
                    if (string.IsNullOrWhiteSpace(_activePlayer)) { _activePlayer = null; return; }
                    Logger.Info($"LoL kill detection: active player resolved.");
                }

                string eventsJson = _http.GetStringAsync($"{BaseUrl}/eventdata").GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(eventsJson);
                if (!doc.RootElement.TryGetProperty("Events", out var events) || events.ValueKind != JsonValueKind.Array)
                    return;

                long maxId = _lastEventId;
                bool baselined = _lastEventId >= 0;
                foreach (var ev in events.EnumerateArray())
                {
                    if (!ev.TryGetProperty("EventID", out var idEl) || !idEl.TryGetInt64(out long id)) continue;
                    if (id > maxId) maxId = id;
                    if (!baselined || id <= _lastEventId) continue; // first read only baselines

                    if (ev.TryGetProperty("EventName", out var nameEl) && nameEl.GetString() == "ChampionKill" &&
                        ev.TryGetProperty("KillerName", out var killerEl) &&
                        IsActivePlayer(killerEl.GetString()))
                    {
                        KillDetected?.Invoke(DateTime.UtcNow);
                    }
                }
                _lastEventId = maxId < 0 ? 0 : maxId;
                _failureStreak = 0;
            }
            catch
            {
                // Connection refused / timeout = not in a match (champ select, lobby, loading). Normal.
                // A sustained streak means the match ended - forget the baseline so the next one starts fresh.
                if (++_failureStreak >= FailureStreakToReset)
                {
                    _activePlayer = null;
                    _lastEventId = -1;
                    _failureStreak = 0;
                }
            }
            finally
            {
                Monitor.Exit(_tickLock);
            }
        }

        private bool IsActivePlayer(string? killerName)
        {
            if (string.IsNullOrEmpty(killerName) || string.IsNullOrEmpty(_activePlayer)) return false;
            if (string.Equals(killerName, _activePlayer, StringComparison.OrdinalIgnoreCase)) return true;

            // Riot ID vs plain-name drift between patches: compare the parts before '#' too.
            string killerBase = StripTag(killerName);
            string playerBase = StripTag(_activePlayer);
            return string.Equals(killerBase, playerBase, StringComparison.OrdinalIgnoreCase);

            static string StripTag(string s)
            {
                int i = s.IndexOf('#');
                return i > 0 ? s.Substring(0, i) : s;
            }
        }
    }
}
