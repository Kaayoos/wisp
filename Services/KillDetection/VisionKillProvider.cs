using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using Wisp.Models;

namespace Wisp.Services.KillDetection
{
    /// <summary>
    /// Kill detection for games WITHOUT an official local API (Valorant, Overwatch 2): samples small
    /// regions of the game's monitor a few times a second and looks for the game's own kill
    /// confirmation UI (kill banner / elimination text) by color signature. Screen sampling only -
    /// the same class of desktop capture the recorder already does, so it is anti-cheat-safe by
    /// construction: no injection, no memory reads, no hooks.
    ///
    /// Capture uses Graphics.CopyFromScreen with EXPLICIT CopyPixelOperation.SourceCopy - plain BitBlt
    /// SRCCOPY. Never CAPTUREBLT: that flag is this project's documented desktop-cursor-flicker cause
    /// (see the gdigrab notes in FFmpegRecorderService). The ROIs are tiny (a few % of the screen), so
    /// at ~3 Hz the cost is negligible next to the game + recorder.
    ///
    /// Detection is heuristic and ships as EXPERIMENTAL: per-game tuning lives in an embedded JSON
    /// spec (ROIs + HSV signature + thresholds), so improving accuracy is a resource edit, not a code
    /// change. A kill fires on the RISING EDGE of the signature (after MinConsecutiveHits samples),
    /// then a refractory period suppresses re-fires while the banner persists, and the signal must
    /// drop below threshold before the detector re-arms. Worst case is a missed or extra MARKER -
    /// recording and clip saving are never affected.
    ///
    /// Diagnostic mode (Settings) dumps each sampled ROI + its score to %APPDATA%\Wisp\
    /// kill-detect-debug so specs can be tuned against real gameplay.
    /// </summary>
    public class VisionKillProvider : IKillProvider
    {
        private readonly VisionGameSpec _spec;
        private readonly AppSettings _settings;
        private readonly Func<string?> _gameMonitorKey;

        private Timer? _timer;
        private readonly object _lifecycleLock = new();
        private readonly object _tickLock = new();

        // Edge/refractory state, only touched inside a tick.
        private int _consecutiveHits;
        private bool _armed = true;
        private DateTime _lastFireUtc = DateTime.MinValue;

        // Monitor geometry cache (re-resolved periodically; displays rarely change mid-match).
        private DisplayHelper.MonitorTarget? _monitor;
        private int _samplesSinceMonitorResolve;

        // Diagnostics.
        private string? _diagDir;
        private int _diagImagesThisSession;
        private const int DiagImageCap = 500;

        private int _captureFailStreak;

        public string ProcessName => _spec.ProcessName;
        public string GameName => _spec.GameName;
        public bool RequiresForeground => true; // sampling the screen is only meaningful with the game visible
        public bool IsRunning => _timer != null;

        public event Action<DateTime>? KillDetected;

        public VisionKillProvider(VisionGameSpec spec, AppSettings settings, Func<string?> gameMonitorKey)
        {
            _spec = spec;
            _settings = settings;
            _gameMonitorKey = gameMonitorKey;
        }

        /// <summary>Loads a game spec from embedded resources (Resources/KillDetection/{name}.json).</summary>
        public static VisionKillProvider FromEmbeddedSpec(string name, AppSettings settings, Func<string?> gameMonitorKey)
        {
            var assembly = Assembly.GetExecutingAssembly();
            string logicalName = $"Wisp.Resources.KillDetection.{name}.json";
            Stream? stream = assembly.GetManifestResourceStream(logicalName);
            if (stream == null)
            {
                // Fall back to matching by suffix if the exact logical name isn't found.
                foreach (string res in assembly.GetManifestResourceNames())
                {
                    if (res.EndsWith($".{name}.json", StringComparison.OrdinalIgnoreCase))
                    {
                        stream = assembly.GetManifestResourceStream(res);
                        break;
                    }
                }
            }
            if (stream == null) throw new FileNotFoundException($"Embedded vision spec '{name}' not found.");
            using (stream)
            using (var reader = new StreamReader(stream))
            {
                return new VisionKillProvider(VisionGameSpec.Parse(reader.ReadToEnd()), settings, gameMonitorKey);
            }
        }

        public void Start()
        {
            lock (_lifecycleLock)
            {
                if (_timer != null) return;
                _consecutiveHits = 0;
                _armed = true;
                _monitor = null;
                _samplesSinceMonitorResolve = 0;
                _captureFailStreak = 0;
                PrepareDiagnostics();
                int intervalMs = Math.Max(100, 1000 / Math.Max(1, _spec.SampleHz));
                _timer = new Timer(_ => Tick(), null, 0, intervalMs);
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

        public void Dispose() => Stop();

        private void Tick()
        {
            if (!Monitor.TryEnter(_tickLock)) return;
            try
            {
                var monitor = ResolveMonitor();
                if (monitor == null || monitor.Width <= 0 || monitor.Height <= 0) return;

                bool anyHit = false;
                foreach (var roi in _spec.Rois)
                {
                    bool hit;
                    bool captureOk;
                    if (roi.Mode == "feedRow")
                    {
                        // Structural killfeed detection (see SampleRoiFeedRow) - a "hit" is a feed row
                        // whose gold/teal/red bars sit in the killer-side order.
                        int killBands = SampleRoiFeedRow(monitor, roi, out captureOk);
                        if (!captureOk) return;
                        hit = killBands > 0;
                        WriteDiagnostics(roi, killBands, hit);
                    }
                    else
                    {
                        double fraction = SampleRoiFraction(monitor, roi, out captureOk);
                        if (!captureOk) return; // capture failed (logged inside); skip the whole sample
                        // MaxFraction guards against screen floods: a flashbang (or death vignette) can
                        // push the whole ROI past the color gates, but the real UI element only ever
                        // fills a small part of it - too MUCH match is a non-match.
                        hit = fraction >= roi.MinFraction && fraction <= roi.MaxFraction;
                        WriteDiagnostics(roi, fraction, hit);
                    }
                    anyHit |= hit;
                }

                if (anyHit)
                {
                    _consecutiveHits++;
                    if (_armed && _consecutiveHits >= _spec.MinConsecutiveHits &&
                        (DateTime.UtcNow - _lastFireUtc).TotalSeconds >= _spec.RefractorySeconds)
                    {
                        _armed = false; // stays disarmed until the signature drops (banner gone)
                        _lastFireUtc = DateTime.UtcNow;
                        KillDetected?.Invoke(_lastFireUtc);
                    }
                }
                else
                {
                    _consecutiveHits = 0;
                    _armed = true;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"{GameName} vision sample failed: {ex.Message}");
            }
            finally
            {
                Monitor.Exit(_tickLock);
            }
        }

        private DisplayHelper.MonitorTarget? ResolveMonitor()
        {
            if (_monitor != null && ++_samplesSinceMonitorResolve < 30) return _monitor;
            _samplesSinceMonitorResolve = 0;

            var monitors = DisplayHelper.GetMonitors();
            string? key = _gameMonitorKey();
            if (!string.IsNullOrEmpty(key))
            {
                foreach (var m in monitors)
                    if (string.Equals(m.DeviceName, key, StringComparison.OrdinalIgnoreCase)) { _monitor = m; return m; }
            }

            // No tracked game monitor (AlwaysOn/Manual recording modes): the router only runs this
            // provider while the game is foreground, so the foreground window's monitor IS the game's.
            try
            {
                var fg = DisplayHelper.GetMonitorForWindow(GetForegroundWindow());
                if (fg != null) { _monitor = fg; return fg; }
            }
            catch { }

            _monitor = monitors.Count > 0 ? monitors[0] : null;
            return _monitor;
        }

        /// <summary>
        /// Captures one ROI (physical pixels, SRCCOPY only) and returns the fraction of its pixels
        /// matching the ROI's HSV signature.
        /// </summary>
        private double SampleRoiFraction(DisplayHelper.MonitorTarget monitor, VisionRoi roi, out bool captureOk)
        {
            captureOk = false;
            int px = monitor.X + (int)(roi.X * monitor.Width);
            int py = monitor.Y + (int)(roi.Y * monitor.Height);
            int pw = Math.Max(1, (int)(roi.W * monitor.Width));
            int ph = Math.Max(1, (int)(roi.H * monitor.Height));

            try
            {
                using var bmp = new Bitmap(pw, ph, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    // SourceCopy = BitBlt SRCCOPY. NEVER add CaptureBlt here - it is the documented
                    // cause of desktop cursor flicker in this project.
                    g.CopyFromScreen(px, py, 0, 0, new Size(pw, ph), CopyPixelOperation.SourceCopy);
                }
                captureOk = true;
                _captureFailStreak = 0;

                var data = bmp.LockBits(new Rectangle(0, 0, pw, ph), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                int matching = 0, total = pw * ph;
                try
                {
                    // Copy out and scan managed - the ROIs are tiny, so this stays well under a
                    // millisecond and avoids unsafe code.
                    int bytes = data.Stride * ph;
                    byte[] pixels = new byte[bytes];
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, bytes);
                    for (int y = 0; y < ph; y++)
                    {
                        int rowStart = y * data.Stride;
                        for (int x = 0; x < pw; x++)
                        {
                            int i = rowStart + x * 3;
                            byte b = pixels[i], gCh = pixels[i + 1], r = pixels[i + 2];
                            if (MatchesSignature(r, gCh, b, roi)) matching++;
                        }
                    }
                }
                finally
                {
                    bmp.UnlockBits(data);
                }
                double fraction = total > 0 ? (double)matching / total : 0;
                DumpDiagImage(bmp, roi, fraction);
                return fraction;
            }
            catch (Exception ex)
            {
                // Typical when a game runs TRUE exclusive fullscreen (GDI can't see it - borderless/
                // windowed fullscreen works, which Wisp already recommends). Log once per streak.
                if (++_captureFailStreak == 1)
                    Logger.Warn($"{GameName} vision capture failed (exclusive fullscreen? {ex.Message}). Will keep retrying quietly.");
                return 0;
            }
        }

        /// <summary>
        /// Structural killfeed detection (mode "feedRow"), validated offline against real gameplay
        /// frames. Valorant's killfeed rows are game-constant UI (skin- and mode-independent):
        ///   the player's KILL row reads  [gold-boxed you] [teal bar] [red bar]   left-to-right;
        ///   the player's DEATH row is the mirror image    [red] [teal] [gold-boxed you];
        ///   teammate/enemy rows have no gold at all.
        /// So a kill = a feed "band" (a run of pixel rows with real bar mass) whose gold, teal and
        /// red x-centroids are in ascending order with healthy separation. Six conditions must hold
        /// at once (band height, teal mass, red mass, gold mass, and two centroid separations), which
        /// measured ZERO false positives across every validation frame. The gold trim is brightest in
        /// the row's first ~0.5s, so the spec uses minConsecutiveHits=1 and relies on these
        /// structural gates instead of persistence. Returns the number of kill-ordered bands.
        /// NOTE: hue windows assume Valorant's default (non-colorblind) palette; they live in the
        /// spec JSON so alternate palettes can ship as spec edits.
        /// </summary>
        private int SampleRoiFeedRow(DisplayHelper.MonitorTarget monitor, VisionRoi roi, out bool captureOk)
        {
            captureOk = false;
            int px = monitor.X + (int)(roi.X * monitor.Width);
            int py = monitor.Y + (int)(roi.Y * monitor.Height);
            int pw = Math.Max(1, (int)(roi.W * monitor.Width));
            int ph = Math.Max(1, (int)(roi.H * monitor.Height));

            // Structural thresholds are tuned at 1920x1080; scale counts by area, distances by axis.
            double xScale = monitor.Width / 1920.0;
            double yScale = monitor.Height / 1080.0;
            double areaScale = xScale * yScale;
            int minGold = Math.Max(1, (int)(roi.MinGold * areaScale));
            int minTeal = Math.Max(1, (int)(roi.MinTeal * areaScale));
            int minRed = Math.Max(1, (int)(roi.MinRed * areaScale));
            int minBandHeight = Math.Max(1, (int)(roi.MinBandHeight * yScale));
            double minSeparation = roi.MinSeparation * xScale;
            int minRowWidth = Math.Max(1, (int)(roi.MinRowWidth * xScale));
            // Gold CEILINGS separate the real you-highlight (a thin anti-aliased outline: measured
            // total 42-75 px, never more than ~10 px in one pixel-row at 1080p) from look-alikes -
            // yellow agent portraits (Killjoy) and sunlit-map bleed measure 138+ total with 23+ px
            // row streaks. Too much gold, or too FAT a gold row, is not the highlight.
            int maxGoldTotal = Math.Max(minGold, (int)(roi.MaxGoldTotal * areaScale));
            int maxGoldRowPeak = Math.Max(1, (int)(roi.MaxGoldRowPeak * xScale));

            try
            {
                using var bmp = new Bitmap(pw, ph, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    // SourceCopy = BitBlt SRCCOPY. NEVER add CaptureBlt (cursor-flicker landmine).
                    g.CopyFromScreen(px, py, 0, 0, new Size(pw, ph), CopyPixelOperation.SourceCopy);
                }
                captureOk = true;
                _captureFailStreak = 0;

                // Per pixel-row class tallies: [gold, teal, red] counts + x-sums.
                var counts = new int[ph, 3];
                var xsums = new double[ph, 3];
                var data = bmp.LockBits(new Rectangle(0, 0, pw, ph), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    int bytes = data.Stride * ph;
                    byte[] pixels = new byte[bytes];
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, bytes);
                    for (int y = 0; y < ph; y++)
                    {
                        int rowStart = y * data.Stride;
                        for (int x = 0; x < pw; x++)
                        {
                            int i = rowStart + x * 3;
                            byte b = pixels[i], gCh = pixels[i + 1], r = pixels[i + 2];
                            int cls = ClassifyFeedPixel(r, gCh, b, roi);
                            if (cls >= 0) { counts[y, cls]++; xsums[y, cls] += x; }
                        }
                    }
                }
                finally
                {
                    bmp.UnlockBits(data);
                }

                // Group consecutive pixel-rows with real bar mass into bands, then test each band.
                int killBands = 0;
                int yPos = 0;
                while (yPos < ph)
                {
                    if (counts[yPos, 1] + counts[yPos, 2] < minRowWidth) { yPos++; continue; }
                    int bandStart = yPos;
                    long gC = 0, tC = 0, rC = 0;
                    double gX = 0, tX = 0, rX = 0;
                    int gRowPeak = 0;
                    while (yPos < ph && counts[yPos, 1] + counts[yPos, 2] >= minRowWidth)
                    {
                        gC += counts[yPos, 0]; gX += xsums[yPos, 0];
                        if (counts[yPos, 0] > gRowPeak) gRowPeak = counts[yPos, 0];
                        tC += counts[yPos, 1]; tX += xsums[yPos, 1];
                        rC += counts[yPos, 2]; rX += xsums[yPos, 2];
                        yPos++;
                    }
                    if (yPos - bandStart < minBandHeight) continue;
                    if (gC < minGold || gC > maxGoldTotal || gRowPeak > maxGoldRowPeak) continue;
                    if (tC < minTeal || rC < minRed) continue;
                    double goldCx = gX / gC, tealCx = tX / tC, redCx = rX / rC;
                    if (goldCx < tealCx - minSeparation && tealCx < redCx - minSeparation)
                        killBands++;
                }

                DumpDiagImage(bmp, roi, killBands);
                return killBands;
            }
            catch (Exception ex)
            {
                if (++_captureFailStreak == 1)
                    Logger.Warn($"{GameName} vision capture failed (exclusive fullscreen? {ex.Message}). Will keep retrying quietly.");
                return 0;
            }
        }

        /// <summary>Killfeed pixel classes for feedRow mode: 0 = gold (you-highlight), 1 = teal (ally bar), 2 = red (enemy bar), -1 = none.</summary>
        private static int ClassifyFeedPixel(byte r, byte g, byte b, VisionRoi roi)
        {
            int max = Math.Max(r, Math.Max(g, b));
            int min = Math.Min(r, Math.Min(g, b));
            double v = max / 255.0;
            if (v < 0.35) return -1;
            double s = max == 0 ? 0 : (max - min) / (double)max;
            if (s < 0.35) return -1;

            double h;
            if (max == min) h = 0;
            else if (max == r) h = 60.0 * (g - b) / (max - min);
            else if (max == g) h = 60.0 * (2.0 + (double)(b - r) / (max - min));
            else h = 60.0 * (4.0 + (double)(r - g) / (max - min));
            if (h < 0) h += 360;

            if (InHue(h, roi.GoldHueMin, roi.GoldHueMax) && s >= roi.GoldSatMin && v >= roi.GoldValMin) return 0;
            if (InHue(h, roi.TealHueMin, roi.TealHueMax) && s >= roi.TealSatMin && v >= roi.TealValMin) return 1;
            if (InHue(h, roi.RedHueMin, roi.RedHueMax) && s >= roi.RedSatMin && v >= roi.RedValMin) return 2;
            return -1;

            static bool InHue(double h, double lo, double hi) => lo <= hi ? (h >= lo && h <= hi) : (h >= lo || h <= hi);
        }

        private static bool MatchesSignature(byte r, byte g, byte b, VisionRoi roi)
        {
            int max = Math.Max(r, Math.Max(g, b));
            int min = Math.Min(r, Math.Min(g, b));
            double v = max / 255.0;
            if (v < roi.ValMin) return false;
            double s = max == 0 ? 0 : (max - min) / (double)max;
            if (s < roi.SatMin || s > roi.SatMax) return false; // SatMax < 1 = detect WHITE/gray UI text

            double h;
            if (max == min) h = 0;
            else if (max == r) h = 60.0 * (g - b) / (max - min);
            else if (max == g) h = 60.0 * (2.0 + (double)(b - r) / (max - min));
            else h = 60.0 * (4.0 + (double)(r - g) / (max - min));
            if (h < 0) h += 360;

            // Hue window with wraparound (e.g. 345..10 spans through 0 - the reds these games use).
            return roi.HueMin <= roi.HueMax
                ? h >= roi.HueMin && h <= roi.HueMax
                : h >= roi.HueMin || h <= roi.HueMax;
        }

        // ===================== Diagnostics =====================

        private void PrepareDiagnostics()
        {
            _diagImagesThisSession = 0;
            _diagDir = null;
            if (!_settings.KillDetectionDiagnostics) return;
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Wisp", "kill-detect-debug");
                Directory.CreateDirectory(dir);
                foreach (string file in Directory.GetFiles(dir))
                {
                    try { if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-7)) File.Delete(file); } catch { }
                }
                _diagDir = dir;
                Logger.Info($"{GameName} vision diagnostics on: dumping to {dir}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"Vision diagnostics unavailable: {ex.Message}");
            }
        }

        private void DumpDiagImage(Bitmap bmp, VisionRoi roi, double fraction)
        {
            if (_diagDir == null || _diagImagesThisSession >= DiagImageCap) return;
            try
            {
                _diagImagesThisSession++;
                string path = Path.Combine(_diagDir, $"roi_{_spec.ProcessName}_{roi.Id}_{DateTime.UtcNow.Ticks}.png");
                bmp.Save(path, ImageFormat.Png);
                if (_diagImagesThisSession == DiagImageCap)
                    Logger.Info($"{GameName} vision diagnostics: image cap reached for this session (scores still logged).");
            }
            catch { }
        }

        private void WriteDiagnostics(VisionRoi roi, double fraction, bool hit)
        {
            if (_diagDir == null) return;
            try
            {
                File.AppendAllText(Path.Combine(_diagDir, "scores.csv"),
                    string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"{DateTime.UtcNow:O},{_spec.ProcessName},{roi.Id},{fraction:0.####},{(hit ? 1 : 0)},{_consecutiveHits}\r\n"));
            }
            catch { }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
    }

    /// <summary>One screen region of interest + the signature that means "kill UI visible".</summary>
    public sealed class VisionRoi
    {
        public string Id = "";
        public string Mode = "color";          // "color" = HSV fraction; "feedRow" = killfeed structure
        public double X, Y, W, H;              // normalized 0-1 of the monitor

        // -- color mode --
        public double HueMin, HueMax;          // degrees 0-360; HueMin > HueMax wraps through 0
        public double SatMin, ValMin;          // 0-1
        public double SatMax = 1.0;            // < 1 detects WHITE/gray UI (low-saturation window)
        public double MinFraction;             // fraction of ROI pixels that must match
        public double MaxFraction = 1.0;       // above this = screen flood (flashbang), not UI

        // -- feedRow mode: class hue windows (default Valorant palette) --
        public double GoldHueMin = 40, GoldHueMax = 62, GoldSatMin = 0.5, GoldValMin = 0.6;
        public double TealHueMin = 150, TealHueMax = 190, TealSatMin = 0.35, TealValMin = 0.35;
        public double RedHueMin = 340, RedHueMax = 10, RedSatMin = 0.5, RedValMin = 0.45;
        // -- feedRow structural gates, in 1920x1080 pixels (scaled to the real monitor at runtime) --
        public int MinGold = 30;               // gold trim mass in a band (you-highlight)
        public int MaxGoldTotal = 110;         // above this it's a yellow portrait/map bleed, not the trim
        public int MaxGoldRowPeak = 14;        // the trim is a THIN outline: no fat gold pixel-rows
        public int MinTeal = 300;              // teal bar mass
        public int MinRed = 300;               // red bar mass
        public int MinBandHeight = 8;          // feed rows are ~25-35 px tall at 1080p
        public int MinSeparation = 60;         // required gap between gold<teal<red centroids
        public int MinRowWidth = 40;           // per-pixel-row bar mass that counts as "a feed row"
    }

    /// <summary>
    /// A vision-detected game's tuning spec, parsed MANUALLY from embedded JSON with JsonDocument
    /// (no serialization POCOs, so the JSON keys stay decoupled from the C# member names).
    /// </summary>
    public sealed class VisionGameSpec
    {
        public string ProcessName = "";
        public string GameName = "";
        public int SampleHz = 3;
        public double RefractorySeconds = 1.5;
        public int MinConsecutiveHits = 2;
        public List<VisionRoi> Rois = new();

        public static VisionGameSpec Parse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var spec = new VisionGameSpec
            {
                ProcessName = GetString(root, "processName") ?? "",
                GameName = GetString(root, "gameName") ?? "",
                SampleHz = (int)GetDouble(root, "sampleHz", 3),
                RefractorySeconds = GetDouble(root, "refractorySeconds", 1.5),
                MinConsecutiveHits = (int)GetDouble(root, "minConsecutiveHits", 2),
            };
            if (string.IsNullOrWhiteSpace(spec.ProcessName)) throw new FormatException("vision spec: processName missing");
            if (string.IsNullOrWhiteSpace(spec.GameName)) spec.GameName = spec.ProcessName;

            if (root.TryGetProperty("rois", out var rois) && rois.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in rois.EnumerateArray())
                {
                    spec.Rois.Add(new VisionRoi
                    {
                        Id = GetString(r, "id") ?? $"roi{spec.Rois.Count}",
                        Mode = GetString(r, "mode") ?? "color",
                        X = GetDouble(r, "x", 0), Y = GetDouble(r, "y", 0),
                        W = GetDouble(r, "w", 0.1), H = GetDouble(r, "h", 0.1),
                        HueMin = GetDouble(r, "hueMin", 0), HueMax = GetDouble(r, "hueMax", 360),
                        SatMin = GetDouble(r, "satMin", 0.5), ValMin = GetDouble(r, "valMin", 0.4),
                        SatMax = GetDouble(r, "satMax", 1.0),
                        MinFraction = GetDouble(r, "minFraction", 0.05),
                        MaxFraction = GetDouble(r, "maxFraction", 1.0),
                        GoldHueMin = GetDouble(r, "goldHueMin", 40), GoldHueMax = GetDouble(r, "goldHueMax", 62),
                        GoldSatMin = GetDouble(r, "goldSatMin", 0.5), GoldValMin = GetDouble(r, "goldValMin", 0.6),
                        TealHueMin = GetDouble(r, "tealHueMin", 150), TealHueMax = GetDouble(r, "tealHueMax", 190),
                        TealSatMin = GetDouble(r, "tealSatMin", 0.35), TealValMin = GetDouble(r, "tealValMin", 0.35),
                        RedHueMin = GetDouble(r, "redHueMin", 340), RedHueMax = GetDouble(r, "redHueMax", 10),
                        RedSatMin = GetDouble(r, "redSatMin", 0.5), RedValMin = GetDouble(r, "redValMin", 0.45),
                        MinGold = (int)GetDouble(r, "minGold", 30),
                        MaxGoldTotal = (int)GetDouble(r, "maxGoldTotal", 110),
                        MaxGoldRowPeak = (int)GetDouble(r, "maxGoldRowPeak", 14),
                        MinTeal = (int)GetDouble(r, "minTeal", 300),
                        MinRed = (int)GetDouble(r, "minRed", 300),
                        MinBandHeight = (int)GetDouble(r, "minBandHeight", 8),
                        MinSeparation = (int)GetDouble(r, "minSeparation", 60),
                        MinRowWidth = (int)GetDouble(r, "minRowWidth", 40),
                    });
                }
            }
            if (spec.Rois.Count == 0) throw new FormatException("vision spec: no ROIs");
            return spec;

            static string? GetString(JsonElement el, string name)
                => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            static double GetDouble(JsonElement el, string name, double fallback)
                => el.TryGetProperty(name, out var p) && p.TryGetDouble(out double v) ? v : fallback;
        }
    }
}
