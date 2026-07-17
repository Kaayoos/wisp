using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wisp.Models;

namespace Wisp.Services
{
    public class FFmpegRecorderService
    {
        // We ship ffmpeg but extract it under a Wisp-branded name so it reads as ours in Task Manager
        // (grouped under Wisp as a child process) instead of a mysterious "ffmpeg.exe" a user might kill or
        // flag as malware. It's the same binary - only the on-disk filename differs. Bump this and the
        // stale-name cleanup list together if it ever changes again.
        private const string CaptureExeName = "WispCapture.exe";
        private static readonly string[] LegacyExeNames = { "ffmpeg.exe" };

        private Process? _recordingProcess;
        private string _tempSegmentsFolder = "";
        private readonly DatabaseService _dbService;
        private string _ffmpegExePath = "";
        private bool _isRecording = false;
        private string _lastFFmpegError = "";
        private DateTime _recordingStartTimeUtc = DateTime.MinValue;

        // Auto-restart watchdog state. When the capture process dies on its own (e.g. a game taking
        // exclusive fullscreen triggers DXGI_ERROR_ACCESS_LOST in ddagrab and FFmpeg EOFs), we
        // transparently re-create the pipeline. _stopRequested distinguishes a deliberate StopRecording
        // from a crash, and the counter caps how often we'll relaunch so a hard-failing config can't
        // spin in a restart storm.
        private volatile bool _stopRequested = false;
        private AppSettings? _activeSettings;
        // The monitor the current pipeline is capturing. Resolved at StartRecording from the user's
        // RecordMonitor setting (and, in "Auto", the foreground window). The auto-follow watcher compares
        // its device name against the game's monitor to decide when to re-target.
        private DisplayHelper.MonitorTarget? _activeMonitor;
        private int _restartCount = 0;
        private DateTime _restartWindowStartUtc = DateTime.MinValue;
        private const int MaxRestartsPerWindow = 5;
        private const int RestartWindowSeconds = 60;

        // Set true the moment ANY ddagrab (Desktop Duplication) pipeline survives startup this run. It
        // proves the machine can do flicker-free GPU capture, so a LATER failure to init ddagrab is almost
        // always transient - the capture surface is momentarily gone during an exclusive-fullscreen grab or
        // an HDR/resolution mode-switch, not a real capability gap. The auto-restart path reads this to keep
        // re-acquiring ddagrab instead of dropping a proven machine onto the flickering gdigrab fallback
        // (the exact "cursor flickers after a while" bug). Sticky for the process lifetime - we never
        // "un-prove" hardware, and a machine that genuinely can't do ddagrab simply never sets it, so its
        // gdigrab fallback behavior is unchanged.
        private volatile bool _ddagrabProvenThisSession = false;

        // ── Multi-run buffer: survive alt-tab without wiping recorded footage ──
        // A capture "run" is one continuous FFmpeg session. Each run writes its own segment set
        // (seg_{seq}_*.ts) + list (segments_{seq}.csv). On an AUTO-restart (a transient surface loss such as
        // alt-tabbing out of exclusive fullscreen) we DON'T wipe - a new run opens and the prior run's
        // footage stays on disk, so a clip taken right after still includes what happened before the tab.
        // The assembler stitches ALL surviving footage across runs and splices a "GAME OUT OF FOCUS" card
        // ONLY over true dead-capture holes (the restart settle time, where no video exists), sized to the
        // real hole so the continuous audio stays in sync. Recorded footage is never hidden or skipped.
        // A fresh user start wipes for a clean slate. Single run (no alt-tab) = byte-for-byte the original
        // behavior.
        private int _runSeq = 0;
        // Longest dead-capture hole (no video recorded at all) we'll bridge with a single "GAME OUT OF
        // FOCUS" card. Beyond this the clip just stops reaching further back rather than showing a very
        // long black card. The card is always sized to the REAL hole (never truncated), so audio stays in
        // sync; this only decides bridge-or-stop.
        private const double OutOfFocusMaxBridgeSeconds = 10.0;
        private sealed class RunMeta
        {
            public bool IsGame;                  // DIAGNOSTIC-ONLY (start-moment snapshot; too unreliable to hide footage by)
            public int Width, Height;            // this run's output frame size (for a codec-matched gap card)
            public int Fps = 30;
            public string CodecFamily = "h264";  // h264 | hevc | av1 - so a generated card concats with -c copy
        }
        // Concurrent: written by StartRecording (auto-restart runs on a background Task) and read by the clip
        // assembler (its own background Task) - the two can overlap if a clip is saved mid-alt-tab.
        private readonly ConcurrentDictionary<int, RunMeta> _runs = new();

        public bool IsRecording => _isRecording;
        public string LastError => _lastFFmpegError;
        public string FFmpegExePath => _ffmpegExePath;

        /// <summary>
        /// Kill detection hook (optional): returns the detected kill instants within [startUtc, endUtc]
        /// so clip assembly can stamp them into Clip.KillMarkers. Set by App when kill detection is
        /// enabled; null (or an empty result) means no markers - clip saving is otherwise untouched.
        /// Invoked on the assembly worker thread, so the implementation must be thread-safe and must
        /// never touch UI.
        /// </summary>
        public Func<DateTime, DateTime, IReadOnlyList<DateTime>>? KillTimestampProvider { get; set; }

        /// <summary>Device name of the monitor currently being captured (empty when not recording).</summary>
        public string ActiveMonitorKey => _activeMonitor?.DeviceName ?? "";

        // P/Invoke for foreground window detection and screen metrics
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        private const int DESKTOPHORZRES = 118;
        private const int DESKTOPVERTRES = 117;
        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;

        public FFmpegRecorderService(DatabaseService dbService)
        {
            _dbService = dbService;
            InitializeFolders();
        }

        private void InitializeFolders()
        {
            string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Wisp");
            _ffmpegExePath = Path.Combine(appData, "bin", CaptureExeName);

            _tempSegmentsFolder = Path.Combine(Path.GetTempPath(), "Wisp", "segments");
            if (!Directory.Exists(_tempSegmentsFolder))
            {
                Directory.CreateDirectory(_tempSegmentsFolder);
            }
            
            string thumbnailsFolder = Path.Combine(appData, "thumbnails");
            if (!Directory.Exists(thumbnailsFolder))
            {
                Directory.CreateDirectory(thumbnailsFolder);
            }
        }

        public static (int Width, int Height) GetPrimaryMonitorPhysicalResolution()
        {
            try
            {
                IntPtr hdc = GetDC(IntPtr.Zero);
                if (hdc != IntPtr.Zero)
                {
                    int width = GetDeviceCaps(hdc, DESKTOPHORZRES);
                    int height = GetDeviceCaps(hdc, DESKTOPVERTRES);
                    ReleaseDC(IntPtr.Zero, hdc);
                    if (width > 0 && height > 0)
                    {
                        return (width, height);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Error getting primary monitor physical resolution via Win32, falling back to logical screen parameters", ex);
            }

            try
            {
                int w = GetSystemMetrics(SM_CXSCREEN);
                int h = GetSystemMetrics(SM_CYSCREEN);
                if (w > 0 && h > 0) return (w, h);
            }
            catch { }

            // Default fallback
            return (1920, 1080);
        }

        public void ExtractFFmpegFromResources()
        {
            string appFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Wisp");
            string binFolder = Path.Combine(appFolder, "bin");
            if (!Directory.Exists(binFolder))
            {
                Directory.CreateDirectory(binFolder);
            }

            string ffmpegPath = Path.Combine(binFolder, CaptureExeName);

            // Upgrade path: older installs extracted the binary as "ffmpeg.exe". Now that we ship it under a
            // Wisp-branded name, remove the stale copy so the user isn't left with a leftover "random ffmpeg"
            // (the very thing we're getting rid of) plus a duplicate ~100 MB file. Best-effort: a copy that's
            // somehow still running (it shouldn't be this early in startup) just gets skipped.
            foreach (var legacy in LegacyExeNames)
            {
                try
                {
                    string legacyPath = Path.Combine(binFolder, legacy);
                    if (!legacyPath.Equals(ffmpegPath, StringComparison.OrdinalIgnoreCase) && File.Exists(legacyPath))
                    {
                        File.Delete(legacyPath);
                        Logger.Info($"Removed stale capture binary '{legacyPath}'.");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Could not remove stale capture binary '{legacy}': {ex.Message}");
                }
            }

            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            const string resourceName = "Wisp.Resources.ffmpeg.exe";

            using (Stream? resourceStream = assembly.GetManifestResourceStream(resourceName))
            {
                if (resourceStream == null)
                {
                    // Dev-only fallback: the embedded resource is missing, so copy the loose binary from the
                    // build output. Re-copy when absent or a different size (so a refreshed binary wins).
                    string localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "ffmpeg.exe");
                    if (File.Exists(localPath))
                    {
                        long localLen = TryGetLength(localPath);
                        if (!File.Exists(ffmpegPath) || TryGetLength(ffmpegPath) != localLen)
                        {
                            Logger.Info($"Copying local fallback capture binary '{localPath}' -> '{ffmpegPath}'.");
                            try { File.Copy(localPath, ffmpegPath, true); }
                            catch (IOException ex) { Logger.Warn($"Could not replace capture binary (in use?): {ex.Message}. Using existing copy."); }
                        }
                        _ffmpegExePath = ffmpegPath;
                        return;
                    }
                    var err = new FileNotFoundException($"FFmpeg resource not found! Resource: '{resourceName}'");
                    Logger.Error("FFmpeg extraction failed because resource stream was null and local fallback didn't exist.", err);
                    throw err;
                }

                // Re-extract when the on-disk copy is missing OR a different size than the embedded binary.
                // The size check is what lets a NEW build replace an older extracted copy instead of being
                // skipped just because the filename already exists - that's how the Wisp-branded binary (and
                // any future ffmpeg upgrade) supersedes the previously extracted one without a version file.
                bool needsExtract = true;
                if (File.Exists(ffmpegPath))
                {
                    try { needsExtract = new FileInfo(ffmpegPath).Length != resourceStream.Length; }
                    catch { needsExtract = true; }
                }

                if (needsExtract)
                {
                    try
                    {
                        Logger.Info($"Extracting embedded capture binary '{resourceName}' to '{ffmpegPath}' ({resourceStream.Length} bytes).");
                        using (FileStream fileStream = new FileStream(ffmpegPath, FileMode.Create, FileAccess.Write))
                        {
                            resourceStream.CopyTo(fileStream);
                        }
                        Logger.Info("Capture binary extraction completed successfully.");
                    }
                    catch (IOException ex)
                    {
                        // Most likely an orphan from a previous session is still holding the old file. Keep
                        // what's on disk - it still runs, it just isn't this session's refreshed binary.
                        Logger.Warn($"Could not replace capture binary at '{ffmpegPath}' (in use?): {ex.Message}. Using existing copy.");
                    }
                }
            }

            _ffmpegExePath = ffmpegPath;
        }

        /// <summary>File length, or -1 if it can't be read - used for the cheap "is the extracted binary stale?" check.</summary>
        private static long TryGetLength(string path)
        {
            try { return new FileInfo(path).Length; } catch { return -1; }
        }

        public string DetectGpuEncoder()
        {
            try
            {
                var gpuNames = new List<string>();

                // 1. Query Win32_VideoController to get all video controllers
                using (var searcher = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))
                {
                    foreach (var mo in searcher.Get())
                    {
                        string name = mo["Name"]?.ToString()?.ToLower() ?? "";
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            gpuNames.Add(name);
                        }
                    }
                }

                // 2. Also check Registry Class keys for 0000, 0001, etc. as fallback or additional info
                try
                {
                    using (var baseKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                    {
                        if (baseKey != null)
                        {
                            foreach (var subKeyName in baseKey.GetSubKeyNames())
                            {
                                if (subKeyName.Length == 4 && int.TryParse(subKeyName, out _))
                                {
                                    using (var subKey = baseKey.OpenSubKey(subKeyName))
                                    {
                                        if (subKey != null)
                                        {
                                            string provider = subKey.GetValue("ProviderName")?.ToString()?.ToLower() ?? "";
                                            string desc = subKey.GetValue("DriverDesc")?.ToString()?.ToLower() ?? "";
                                            if (!string.IsNullOrWhiteSpace(provider)) gpuNames.Add(provider);
                                            if (!string.IsNullOrWhiteSpace(desc)) gpuNames.Add(desc);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Registry GPU subkey search encountered an issue: {ex.Message}");
                }

                // Prioritize NVIDIA -> AMD -> Intel
                if (gpuNames.Any(name => name.Contains("nvidia") || name.Contains("geforce") || name.Contains("quadro") || name.Contains("tesla")))
                {
                    return "h264_nvenc";
                }
                if (gpuNames.Any(name => name.Contains("amd") || name.Contains("radeon") || name.Contains("ati")))
                {
                    return "h264_amf";
                }
                if (gpuNames.Any(name => name.Contains("intel") || name.Contains("arc") || name.Contains("hd graphics") || name.Contains("iris")))
                {
                    return "h264_qsv";
                }
            }
            catch (Exception ex)
            {
                Logger.Error("GPU detection error.", ex);
            }

            return "libx264";
        }

        /// <summary>All video controllers (GPUs) on the system, by name.</summary>
        public List<string> GetAvailableGpus()
        {
            var list = new List<string>();
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
                foreach (var mo in searcher.Get())
                {
                    string name = mo["Name"]?.ToString() ?? "";
                    if (!string.IsNullOrWhiteSpace(name) && !list.Contains(name)) list.Add(name);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("GetAvailableGpus error.", ex);
            }
            return list;
        }

        /// <summary>Maps a GPU name to its hardware encoder (or libx264 if unknown).</summary>
        private static string EncoderForGpuName(string name)
        {
            string n = (name ?? "").ToLowerInvariant();
            if (n.Contains("nvidia") || n.Contains("geforce") || n.Contains("quadro") || n.Contains("rtx") || n.Contains("gtx") || n.Contains("tesla"))
                return "h264_nvenc";
            if (n.Contains("amd") || n.Contains("radeon") || n.Contains("ati"))
                return "h264_amf";
            if (n.Contains("intel") || n.Contains("arc") || n.Contains("iris") || n.Contains("hd graphics") || n.Contains("uhd"))
                return "h264_qsv";
            return "libx264";
        }

        /// <summary>Resolves the encoder from the user's GPU preference ("Auto", a GPU name, or "CPU").</summary>
        private string ResolveEncoder(string? preference)
        {
            if (string.IsNullOrWhiteSpace(preference) || preference.Equals("Auto", StringComparison.OrdinalIgnoreCase))
                return DetectGpuEncoder();
            if (preference.StartsWith("CPU", StringComparison.OrdinalIgnoreCase))
                return "libx264";
            return EncoderForGpuName(preference);
        }

        /// <summary>
        /// Quick FFmpeg probe confirming an encoder actually initializes on this machine. Hardware encoders
        /// can be absent or disabled, and a codec may exceed the GPU's capabilities (e.g. AV1 on an older
        /// card), so we test before committing the buffer to it. libx264 is built in, so it's never probed.
        /// </summary>
        private bool TestEncoder(string encoder)
        {
            if (encoder == "libx264") return true;

            try
            {
                // 1280x720 because some hardware encoders (especially AMD AMF) refuse to initialize with
                // tiny resolutions like 64x64.
                string testArgs = $"-f lavfi -i nullsrc=s=1280x720:d=0.1 -c:v {encoder} -f null -";
                var psi = new ProcessStartInfo
                {
                    FileName = _ffmpegExePath.Replace('\\', '/'),
                    Arguments = testArgs,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                Logger.Info($"Probing encoder '{encoder}' with command: {_ffmpegExePath} {testArgs}");
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    ChildProcessTracker.AddProcess(proc);
                    proc.WaitForExit(5000);
                    if (proc.ExitCode == 0)
                    {
                        Logger.Info($"Encoder '{encoder}' probed successfully.");
                        return true;
                    }

                    string err = proc.StandardError.ReadToEnd();
                    Logger.Warn($"Encoder '{encoder}' failed probe (Exit code {proc.ExitCode}): {err}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Encoder probe error for '{encoder}'", ex);
            }

            return false;
        }

        /// <summary>Normalizes the Advanced "Video Codec" setting to an ffmpeg codec token.</summary>
        private static string CodecToken(string? codec) => (codec ?? "").Trim().ToUpperInvariant() switch
        {
            "H.265" or "HEVC" or "H265" => "hevc",
            "AV1" => "av1",
            _ => "h264",
        };

        /// <summary>
        /// Ordered encoders to try for the chosen codec, given the vendor-resolved base encoder
        /// (h264_nvenc/_amf/_qsv or libx264). Hardware paths fall back to a lower codec on the SAME vendor,
        /// then to software, so an unsupported codec/GPU pair never stops the buffer. The CPU path supports
        /// H.264/H.265 (libx264/libx265); software AV1 (libaom) is far too slow for real-time capture, so an
        /// AV1-on-CPU request degrades to H.265/H.264.
        /// </summary>
        private static List<string> BuildEncoderCandidates(string baseEncoder, string? codec)
        {
            string c = CodecToken(codec);
            string vendor = baseEncoder.EndsWith("_nvenc") ? "nvenc"
                          : baseEncoder.EndsWith("_amf") ? "amf"
                          : baseEncoder.EndsWith("_qsv") ? "qsv" : "cpu";

            var list = new List<string>();
            if (vendor != "cpu")
            {
                if (c == "av1") { list.Add($"av1_{vendor}"); list.Add($"hevc_{vendor}"); list.Add($"h264_{vendor}"); }
                else if (c == "hevc") { list.Add($"hevc_{vendor}"); list.Add($"h264_{vendor}"); }
                else { list.Add($"h264_{vendor}"); }
                list.Add("libx264");
            }
            else
            {
                if (c == "hevc" || c == "av1") list.Add("libx265");
                list.Add("libx264");
            }
            return list.Distinct().ToList();
        }

        /// <summary>
        /// Hardware encoders from the OTHER GPU vendors, tried when the chosen vendor's encoder fails to
        /// attach to a working ddagrab capture (stage 2a). On dual-GPU machines the second GPU's encoder
        /// usually initializes fine, keeping capture flicker-free at hardware-encode CPU cost instead of
        /// dropping to software x264. Each vendor keeps the user's codec preference with the same per-vendor
        /// degrade order as <see cref="BuildEncoderCandidates"/>. Empty when a software encoder was the one
        /// that failed - there is nothing cheaper left to offer.
        /// </summary>
        private static List<string> BuildCrossVendorHardwareFallbacks(string failedEncoder, string? codec)
        {
            string failedVendor = failedEncoder.EndsWith("_nvenc") ? "nvenc"
                                : failedEncoder.EndsWith("_amf") ? "amf"
                                : failedEncoder.EndsWith("_qsv") ? "qsv" : "";
            var list = new List<string>();
            if (failedVendor.Length == 0) return list;

            foreach (var vendor in new[] { "nvenc", "amf", "qsv" })
            {
                if (vendor == failedVendor) continue;
                foreach (var cand in BuildEncoderCandidates($"h264_{vendor}", codec))
                {
                    if (!cand.StartsWith("lib")) list.Add(cand);
                }
            }
            return list.Distinct().ToList();
        }

        public List<string> GetMicrophoneDevices()
        {
            var list = new List<string>();
            try
            {
                var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                var devices = enumerator.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.DeviceState.Active);
                foreach (var device in devices)
                {
                    list.Add(device.FriendlyName);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to get mic devices.", ex);
            }
            return list;
        }

        /// <summary>
        /// Returns the title and process name of the current foreground window.
        /// </summary>
        public static (string Title, string ProcessName) GetForegroundWindowInfo()
        {
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return ("Desktop", "");

                var sb = new StringBuilder(256);
                GetWindowText(hwnd, sb, 256);
                string title = sb.ToString();

                GetWindowThreadProcessId(hwnd, out uint pid);
                string processName = "";
                try
                {
                    using var proc = Process.GetProcessById((int)pid);
                    processName = proc.ProcessName;
                }
                catch { }

                if (string.IsNullOrWhiteSpace(title)) title = processName;
                if (string.IsNullOrWhiteSpace(title)) title = "Desktop";

                return (title, processName);
            }
            catch
            {
                return ("Desktop", "");
            }
        }

        /// <summary>
        /// Best-effort STABLE name of the app focused right now, for tagging a clip. Returns "" for the
        /// desktop/shell. Deliberately ignores the window title - a browser's title is the page/video
        /// name, so titles spawned a brand-new "game" category per YouTube video. Instead we use the
        /// executable's product name (e.g. "Google Chrome") and fall back to the process name
        /// (e.g. "valorant"), both of which are constant for a given app.
        /// </summary>
        public static string GetForegroundGameName()
        {
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return "";

                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0) return "";

                using var proc = Process.GetProcessById((int)pid);
                string processName = proc.ProcessName ?? "";

                // The Windows shell being focused means "desktop" - don't tag the clip.
                if (processName.Equals("explorer", StringComparison.OrdinalIgnoreCase))
                    return "";

                // Prefer the friendly, constant product name from the exe's version info.
                string product = "";
                try { product = proc.MainModule?.FileVersionInfo?.ProductName?.Trim() ?? ""; }
                catch { /* cross-bitness / protected process: fall back to the process name */ }

                string name = IsUsefulProductName(product) ? product : PrettifyProcessName(processName);
                name = System.Text.RegularExpressions.Regex.Replace((name ?? "").Trim(), @"\s+", " ");
                return name;
            }
            catch
            {
                return "";
            }
        }

        /// <summary>Rejects empty or generic OS-component product names (e.g. "Microsoft® Windows® Operating System").</summary>
        private static bool IsUsefulProductName(string product)
        {
            if (string.IsNullOrWhiteSpace(product)) return false;
            if (product.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0 &&
                product.IndexOf("Windows", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return true;
        }

        /// <summary>Capitalizes a bare process name ("valorant" -> "Valorant") for display.</summary>
        private static string PrettifyProcessName(string processName)
        {
            if (string.IsNullOrWhiteSpace(processName)) return "";
            processName = processName.Trim();
            return char.ToUpperInvariant(processName[0]) + processName.Substring(1);
        }

        /// <summary>Turns a game name into something safe to embed in a clip filename.</summary>
        private static string SanitizeForFilename(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c.ToString(), "");
            name = name.Replace(' ', '_').Trim('_', '.');
            if (name.Length > 40) name = name.Substring(0, 40);
            return name;
        }

        private bool StartFFmpegProcessInternal(AppSettings settings, string encoder, bool useDdaGrab)
        {
            _lastFFmpegError = "";
            string ffmpegArgs = BuildFFmpegArgs(settings, encoder, useDdaGrab);

            var psi = new ProcessStartInfo
            {
                FileName = _ffmpegExePath.Replace('\\', '/'),
                Arguments = ffmpegArgs,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            Logger.Info($"Spawning FFmpeg (Encoder: {encoder}, Capture: {(useDdaGrab ? "ddagrab" : "gdigrab")}): {psi.FileName} {ffmpegArgs}");

            try
            {
                // Set true only once the process has survived the startup probe below. The Exited
                // handler uses it to tell a genuine mid-recording crash (auto-restart) apart from an
                // immediate startup failure (already handled by StartRecording's fallback chain).
                bool established = false;

                var process = Process.Start(psi);
                if (process == null) return false;
                ChildProcessTracker.AddProcess(process);

                // The game always outranks the recorder: BelowNormal keeps the capture pipeline's CPU work
                // (mux/scale, and the CPU-encoder fallback rungs especially) from stealing cycles a running
                // game needs. NVENC/AMF runs are barely affected (their heavy lifting is on the GPU); under
                // genuine CPU starvation ffmpeg drops capture frames rather than stuttering the game -
                // the right trade for a background clipper.
                try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* exited already / access denied - non-fatal */ }

                _recordingProcess = process;
                _isRecording = true;

                // Read stderr asynchronously
                process.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        Logger.Warn($"[FFmpeg Stderr] {e.Data}");
                        _lastFFmpegError = e.Data;
                    }
                };
                process.BeginErrorReadLine();

                // Monitor exited event
                process.EnableRaisingEvents = true;
                process.Exited += (s, e) =>
                {
                    Logger.Warn($"FFmpeg process exited with code: {process.ExitCode}");
                    if (_recordingProcess == process)
                    {
                        _isRecording = false;

                        // A process that was running fine and then died (rather than failing at
                        // startup) almost always means the capture surface was yanked out from under
                        // ddagrab - typically a game taking exclusive fullscreen, a resolution/HDR
                        // change, or the lock screen. Re-create the pipeline so the buffer resumes.
                        if (established && !_stopRequested)
                        {
                            // If this pipeline ran healthily for a good while before dying, any earlier
                            // deaths were transient and already recovered - clear the storm counter so a long,
                            // healthy session peppered with the odd exclusive-fullscreen mode-switch doesn't
                            // slowly accumulate toward the give-up cap. Only rapid, repeated deaths that never
                            // reach this threshold keep the counter climbing and can still trip the cap.
                            if ((DateTime.UtcNow - _recordingStartTimeUtc).TotalSeconds >= RestartWindowSeconds)
                            {
                                _restartCount = 0;
                            }
                            ScheduleAutoRestart();
                        }
                    }
                };

                // Wait to see if it exits immediately
                Thread.Sleep(800);
                if (process.HasExited)
                {
                    Logger.Error($"FFmpeg process died immediately after startup. ExitCode: {process.ExitCode}, Last error: {_lastFFmpegError}");
                    _isRecording = false;
                    _recordingProcess = null;
                    return false;
                }

                _recordingStartTimeUtc = DateTime.UtcNow;
                established = true;
                // Record that ddagrab works on this machine so the watchdog re-acquires it (instead of
                // flickering on gdigrab) after a transient mid-recording surface loss. See the field's note.
                if (useDdaGrab) _ddagrabProvenThisSession = true;
                // Remember the codec this run actually ended up on (the fallback chain may have swapped it),
                // so a gap card generated for a cross-run clip is encoded to match and concats with -c copy.
                if (_runs.TryGetValue(_runSeq, out var rmEstablished)) rmEstablished.CodecFamily = CodecFamilyOf(encoder);
                Logger.Info($"FFmpeg process is running successfully in the background. Recording start time: {_recordingStartTimeUtc:O}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("Exception thrown during FFmpeg process startup.", ex);
                _isRecording = false;
                _recordingProcess = null;
                return false;
            }
        }

        public void StartRecording(AppSettings settings) => StartRecording(settings, forceGdiFallback: false, preserveBuffer: false);

        // forceGdiFallback is normally false. The auto-restart watchdog sets it true ONLY as a genuine last
        // resort - after it has exhausted its ddagrab re-acquire budget on a machine that has proven ddagrab
        // this session - so the buffer keeps running (accepting cursor flicker) rather than going dark on a
        // stuck/persistent surface loss. On a fresh user start it is always false.
        // preserveBuffer is true ONLY on the auto-restart path: it keeps the previous run's segments on disk
        // (opening a NEW run sequence) instead of wiping, so a clip taken right after an alt-tab still
        // includes the footage from before it. A fresh user start passes false and wipes for a clean slate.
        private void StartRecording(AppSettings settings, bool forceGdiFallback, bool preserveBuffer = false)
        {
            if (_isRecording) StopRecording();

            // Remember the config so the watchdog can relaunch with it, and clear the stop flag that a
            // prior StopRecording (including the one just above) may have set.
            _activeSettings = settings;
            _stopRequested = false;

            Logger.Info($"Starting continuous recording buffer (preserveBuffer={preserveBuffer}).");

            // 1. Buffer housekeeping. A fresh start wipes everything for a clean slate; an auto-restart
            //    PRESERVES prior run(s) and just opens a new run sequence, so footage from before the alt-tab
            //    survives to be stitched into a clip. Old runs are evicted once they age past the buffer.
            try
            {
                if (!Directory.Exists(_tempSegmentsFolder))
                {
                    Directory.CreateDirectory(_tempSegmentsFolder);
                    _runs.Clear();
                    _runSeq = 0;
                }
                else if (!preserveBuffer)
                {
                    WipeAllRuns();
                    _runs.Clear();
                    _runSeq = 0;
                }
                else
                {
                    _runSeq++;
                    EvictOldRuns(MaxBufferKeepSeconds(settings)); // bound disk use across many alt-tabs
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Error preparing the segment buffer directory.", ex);
            }

            // 2. Resolve the encoder: (Recording GPU vendor) x (Advanced > Video Codec), then probe the
            //    candidate list in order so an unsupported codec/GPU pair gracefully falls back instead of
            //    stopping the buffer. The default (H.264 + Auto) probes exactly the one encoder it always did.
            string baseEncoder = ResolveEncoder(settings.RecordingGpu);
            var candidates = BuildEncoderCandidates(baseEncoder, settings.VideoCodec);
            Logger.Info($"Recording GPU '{settings.RecordingGpu}' + codec '{settings.VideoCodec}' -> base '{baseEncoder}'; candidates: {string.Join(", ", candidates)}");
            string encoder = "libx264";
            foreach (var cand in candidates)
            {
                if (TestEncoder(cand)) { encoder = cand; break; }
            }
            Logger.Info($"Selected video encoder: {encoder}");

            // 2b. Resolve which monitor to capture. "Auto" follows the foreground (game) window's monitor;
            //     otherwise the user's chosen display. On auto-restart this re-resolves, so a game that just
            //     took exclusive fullscreen on another monitor is picked up correctly.
            _activeMonitor = DisplayHelper.ResolveTarget(settings.RecordMonitor, GetForegroundWindow());
            Logger.Info($"Recording monitor '{settings.RecordMonitor}' -> {_activeMonitor.DeviceName} " +
                $"(screen index {_activeMonitor.Index}, {_activeMonitor.Width}x{_activeMonitor.Height} " +
                $"at {_activeMonitor.X},{_activeMonitor.Y}, scale {_activeMonitor.DpiScale:0.##}x); " +
                "ddagrab output_idx is resolved from the DXGI output order at capture start.");

            // Tag this run for the multi-run assembler: the output frame size / rate / codec family, so a
            // dead-gap card can be generated that concats cleanly and so the assembler can detect a codec
            // boundary between runs. IsGame is DIAGNOSTIC-ONLY (log line below) - the assembler must never
            // hide footage by it: it's a single start-moment snapshot, and an auto-restart starts mid-
            // transition, so it routinely mislabels a run that then records minutes of real gameplay.
            // CodecFamily is refined to the encoder that actually establishes (StartFFmpegProcessInternal).
            var (outW, outH) = ComputeOutputResolution(settings, _activeMonitor);
            _runs[_runSeq] = new RunMeta
            {
                IsGame = !string.IsNullOrWhiteSpace(GetForegroundGameName()),
                Width = outW,
                Height = outH,
                Fps = Math.Max(1, settings.CaptureFps),
                CodecFamily = CodecFamilyOf(encoder),
            };
            Logger.Info($"Run {_runSeq}: {(_runs[_runSeq].IsGame ? "GAME" : "out-of-focus/desktop")}, " +
                $"{outW}x{outH}@{_runs[_runSeq].Fps}, codec {_runs[_runSeq].CodecFamily}.");

            // The capture METHOD matters more than the encoder for one user-visible reason: ddagrab
            // (Desktop Duplication) never touches the on-screen cursor, whereas gdigrab grabs via GDI
            // BitBlt with the CAPTUREBLT flag, which makes the REAL desktop mouse pointer flicker the whole
            // time we record. That's a documented FFmpeg behavior (trac #11598/#11599) and this build has no
            // `use_captureblt` switch to turn it off - so the only cure is to not be on gdigrab. We therefore
            // exhaust every ddagrab option (even dropping to CPU encoding) BEFORE ever touching gdigrab.

            // 3. Stage 1: ddagrab + the chosen GPU encoder - flicker-free + low overhead. The normal path.
            if (StartFFmpegProcessInternal(settings, encoder, useDdaGrab: true))
            {
                return;
            }

            // 3a. Hybrid-GPU display-ownership flip. On iGPU+dGPU machines Windows virtualizes the DXGI
            //     display topology PER PROCESS based on the exe's GPU preference: in one view only the
            //     iGPU-owned display(s) can be duplicated, in the other only the dGPU-owned ones - and a
            //     game engaging the dGPU (exclusive fullscreen) can migrate a display between views
            //     MID-SESSION. When that happens ddagrab reports "Failed to enumerate DXGI output N" /
            //     "Selected output not supported" and no amount of encoder swapping or waiting helps -
            //     the field failure mode was a restart storm that ended in permanent flickering gdigrab.
            //     The fix: flip the capture exe's per-app GPU preference (the same HKCU value the Windows
            //     Settings > Graphics page writes - user-scoped, reversible) and retry ddagrab once. The
            //     output_idx stays the global screen index: verified on hybrid hardware that both views
            //     list outputs in global order, each view just only DUPLICATES the ones its GPU owns.
            //     Single-GPU machines never fail stage 1 this way, so they never reach this.
            bool ddagrabInputFailed = LastErrorWasDdagrabInputFailure();
            if (ddagrabInputFailed && TryFlipCaptureGpuPreference())
            {
                if (StartFFmpegProcessInternal(settings, encoder, useDdaGrab: true))
                {
                    return;
                }
                ddagrabInputFailed = LastErrorWasDdagrabInputFailure();
            }

            // 3b. Stage 2a: ddagrab + a DIFFERENT hardware encoder. If ddagrab itself is fine but the chosen
            //     hardware ENCODER won't attach - the common case on hybrid-GPU machines, where the encoder's
            //     GPU is parked for power or display ownership just migrated to the other GPU - the OTHER
            //     GPU's encoder usually attaches fine. Try every other vendor's hardware encoder (probed
            //     first, so machines without one skip this at the cost of a sub-second probe) BEFORE
            //     conceding to CPU encoding: software x264 costs several cores continuously, which on
            //     smaller CPUs starves the running game. Skipped when the INPUT failed to open - no encoder
            //     can fix a capture source that isn't there, and each pointless spawn costs the running
            //     game a stutter hitch.
            if (!ddagrabInputFailed && encoder != "libx264")
            {
                foreach (var alt in BuildCrossVendorHardwareFallbacks(encoder, settings.VideoCodec))
                {
                    if (!TestEncoder(alt)) continue;
                    Logger.Warn($"ddagrab + {encoder} failed to start. Retrying ddagrab with hardware encoder '{alt}' before any CPU fallback.");
                    if (StartFFmpegProcessInternal(settings, alt, useDdaGrab: true))
                    {
                        return;
                    }
                    if (LastErrorWasDdagrabInputFailure())
                    {
                        // The capture source itself just died (display mode-switch mid-chain); further
                        // encoder swaps are pointless spawns against a missing input.
                        ddagrabInputFailed = true;
                        break;
                    }
                }
            }

            // 3c. Stage 2b: ddagrab + libx264 (CPU) - only after every hardware encoder on the machine has
            //     had its chance. Stays on the no-flicker capture and pays CPU for it; kept because a
            //     machine whose only hardware encoder can't attach still deserves smooth capture, but it is
            //     deliberately the LAST resort before gdigrab (see the field CPU reports: a session that
            //     lands here encodes on several cores for its whole lifetime).
            if (!ddagrabInputFailed && encoder != "libx264")
            {
                Logger.Warn("ddagrab + hardware encoder failed to start. Retrying ddagrab with the libx264 CPU encoder (capture stays flicker-free).");
                if (StartFFmpegProcessInternal(settings, "libx264", useDdaGrab: true))
                {
                    return;
                }
            }

            // gdigrab captures via GDI BitBlt (CAPTUREBLT) and makes the LIVE desktop cursor flicker the
            // whole time we record - the exact bug users hit. Before ever touching it, check whether this
            // machine has already proven it can do ddagrab this run. If it has, ddagrab merely failing to
            // re-init right now is a transient surface loss (a game grabbing exclusive fullscreen, or an
            // HDR/resolution mode-switch still settling) - NOT a reason to start flickering. Bail and let the
            // watchdog re-acquire ddagrab once the display settles; it only forces the gdigrab path
            // (forceGdiFallback) as a genuine last resort after several failed re-acquires, so the buffer
            // never goes dark. A machine that has never done ddagrab this session falls through to gdigrab
            // exactly as before - its behavior is unchanged.
            if (_ddagrabProvenThisSession && !forceGdiFallback)
            {
                Logger.Warn("ddagrab did not initialize this attempt, but it has worked before this session " +
                    "(transient surface loss - likely a display mode-switch). Skipping the flickering GDI " +
                    "fallback and scheduling a ddagrab re-acquire.");
                ScheduleAutoRestart();
                return;
            }

            Logger.Warn("ddagrab could not start at all. Falling back to GDI grab - note this can make the desktop mouse cursor flicker on some systems.");

            // 4. Stage 3: gdigrab + GPU encoder. Only reached when ddagrab capture genuinely can't init.
            if (StartFFmpegProcessInternal(settings, encoder, useDdaGrab: false))
            {
                return;
            }

            // 5. Stage 4: gdigrab + libx264 (CPU) - the absolute last resort.
            if (encoder != "libx264")
            {
                Logger.Warn("GDI grab with hardware encoder failed. Falling back to GDI grab with libx264 CPU encoder.");
                if (StartFFmpegProcessInternal(settings, "libx264", useDdaGrab: false))
                {
                    return;
                }
            }

            Logger.Error("All fallback configurations for FFmpeg recording failed to start.");
        }

        /// <summary>
        /// True when the last FFmpeg stderr line says the ddagrab INPUT could not open (the target display
        /// is not duplicatable in the capture exe's current DXGI view) - as opposed to an encoder failure.
        /// Distinguishes "flip the GPU-preference view / skip encoder swaps" from "try another encoder".
        /// </summary>
        private bool LastErrorWasDdagrabInputFailure()
        {
            string e = _lastFFmpegError ?? "";
            return e.Contains("Error opening input", StringComparison.OrdinalIgnoreCase)
                || e.Contains("Failed to enumerate DXGI output", StringComparison.OrdinalIgnoreCase)
                || e.Contains("Selected output not supported", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Flips the capture exe's per-application GPU preference between "power saving" (iGPU-first DXGI
        /// view) and "high performance" (dGPU-first view) in HKCU - the exact value the Windows Settings >
        /// Graphics page manages. On hybrid-GPU machines each view can only duplicate the displays its GPU
        /// currently owns, and ownership migrates when a game runs; flipping lets the next spawn see the
        /// side that owns the target display. User-scoped, affects only our bundled WispCapture.exe, and
        /// self-corrects: if a later spawn fails in the flipped view it simply flips back. Returns false
        /// (and changes nothing) if the registry isn't writable - the caller then keeps today's behavior.
        /// </summary>
        private bool TryFlipCaptureGpuPreference()
        {
            try
            {
                string exePath = Path.GetFullPath(_ffmpegExePath);
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\DirectX\UserGpuPreferences");
                if (key == null) return false;
                string current = key.GetValue(exePath) as string ?? "";
                // Unset behaves like power-saving (verified on hybrid hardware), so unset flips to 2.
                string next = current.Contains("GpuPreference=2") ? "GpuPreference=1;" : "GpuPreference=2;";
                key.SetValue(exePath, next);
                Logger.Warn($"ddagrab cannot duplicate the target display in the current per-process GPU view. " +
                    $"Flipped WispCapture's GPU preference ('{current}' -> '{next}') and retrying (hybrid-GPU " +
                    "display ownership moved, typically a game engaging the dGPU).");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("Could not flip the capture exe's GPU preference; keeping current behavior.", ex);
                return false;
            }
        }

        private string BuildFFmpegArgs(AppSettings settings, string encoder, bool useDdaGrab)
        {
            var args = new StringBuilder();

            // The monitor to capture, resolved in StartRecording. Fall back to the foreground window's
            // monitor if somehow unset (e.g. a direct call path), so we never crash on a null here.
            var mon = _activeMonitor ?? DisplayHelper.ResolveTarget(settings.RecordMonitor, GetForegroundWindow());

            // Screen Video Input (Video Only - Audio is offloaded to C# NAudio)
            if (useDdaGrab)
            {
                // ddagrab is a source filter under the lavfi (libavfilter) system. It outputs d3d11 hardware frames.
                // We download them to system memory (CPU) via vf filters. We omit global d3d11va initialization to prevent
                // conflicts with NVENC/AMF GPU encoding initialization on multi-GPU setups.
                // output_idx selects the DXGI output (monitor). ddagrab counts outputs in DXGI enumeration
                // order, which is NOT necessarily Screen.AllScreens order (mon.Index) - when they differ,
                // the screen index points ddagrab at the wrong monitor, or at a non-existent output that
                // fails and drops us into the fallback chain. Map the device to its real DXGI ordinal;
                // TryGetDdaGrabOutputIndex returns null (so we keep mon.Index) for single-monitor / same-order
                // setups and for a monitor on another GPU, where the gdigrab fallback's absolute coordinates
                // capture the right region regardless.
                int outputIdx = DisplayHelper.TryGetDdaGrabOutputIndex(mon.DeviceName) ?? mon.Index;
                if (outputIdx != mon.Index)
                    Logger.Info($"ddagrab output_idx corrected {mon.Index} -> {outputIdx} for {mon.DeviceName} " +
                        "(DXGI output order differs from Windows' display order on this machine).");
                args.Append($"-y -f lavfi -i ddagrab=output_idx={outputIdx}:draw_mouse=1:framerate={settings.CaptureFps} ");
            }
            else
            {
                // gdigrab captures via GDI which is compatible but causes cursor flickering. We point it at
                // the target monitor's physical rect on the virtual desktop (offsets may be negative for a
                // display left of / above the primary).
                Logger.Info($"GDI Grab capture: {mon.Width}x{mon.Height} at offset {mon.X},{mon.Y} ({mon.DeviceName}).");
                args.Append($"-y -f gdigrab -framerate {settings.CaptureFps} -draw_mouse 1 -video_size {mon.Width}x{mon.Height} -offset_x {mon.X} -offset_y {mon.Y} -i desktop ");
            }

            // Resolution scale filter. Resolution is free-form now ("Native", "1440p", "1080p",
            // "720p", or an explicit "1920x1080"), so resolve it to an ffmpeg scale spec; a null spec
            // means capture at the native size with no scaling.
            string? scaleSpec = ResolveScaleSpec(settings.CaptureResolution);
            if (useDdaGrab)
            {
                // ddagrab hands us GPU frames: download to system memory as bgra, optionally scale,
                // then convert to nv12 for the encoder.
                args.Append(scaleSpec != null
                    ? $"-vf \"hwdownload,format=bgra,scale={scaleSpec},format=nv12\" "
                    : "-vf \"hwdownload,format=bgra,format=nv12\" ");
            }
            else
            {
                args.Append(scaleSpec != null
                    ? $"-vf \"scale={scaleSpec},format=nv12\" "
                    : "-vf format=nv12 ");
            }

            // Force a keyframe every second so the segment muxer can cut cleanly at 1s boundaries
            int gopSize = settings.CaptureFps; // keyframe every N frames = 1 second

            // Encoder + quality settings. The encoder name carries the vendor (suffix _nvenc/_amf/_qsv, or
            // lib* for software) and the codec (prefix); presets are per-vendor and identical across codecs.
            // The default path (H.264 + a Low/Medium/High quality preset, no custom bitrate) emits exactly
            // the same arguments it always has - only the new H.265/AV1 and custom-bitrate branches differ.
            bool isNvenc = encoder.EndsWith("_nvenc");
            bool isAmf = encoder.EndsWith("_amf");
            bool isQsv = encoder.EndsWith("_qsv");
            bool isSoftware = encoder.StartsWith("lib");
            string vq = settings.VideoQuality;

            args.Append($"-c:v {encoder} ");
            if (isNvenc)
            {
                string nvencPreset = vq == "High" ? "p5" : (vq == "Low" ? "p1" : "p3");
                args.Append($"-preset {nvencPreset} -tune ll -g {gopSize} ");
            }
            else if (isAmf)
            {
                args.Append($"-quality speed -g {gopSize} ");
            }
            else if (isQsv)
            {
                args.Append($"-preset fast -g {gopSize} ");
            }
            else // software libx264 / libx265
            {
                string cpuPreset = vq == "High" ? "veryfast" : (vq == "Low" ? "ultrafast" : "superfast");
                args.Append($"-preset {cpuPreset} -tune zerolatency -g {gopSize} ");
            }

            // Rate control. A custom bitrate (Advanced) overrides the preset; otherwise software uses CRF and
            // hardware uses the preset's target bitrate - the long-standing behavior, byte-for-byte.
            if (settings.CustomBitrateEnabled && settings.CustomVideoBitrateMbps > 0)
            {
                int mb = Math.Clamp(settings.CustomVideoBitrateMbps, 1, 500);
                args.Append($"-b:v {mb}M ");
                if (isNvenc) args.Append($"-maxrate:v {(int)Math.Ceiling(mb * 1.34)}M -bufsize:v {mb * 2}M ");
            }
            else if (isSoftware)
            {
                if (vq == "High") args.Append("-crf 18 ");
                else if (vq == "Low") args.Append("-crf 28 ");
                else args.Append("-crf 23 ");
            }
            else if (isNvenc)
            {
                if (vq == "High") args.Append("-b:v 15M -maxrate:v 20M -bufsize:v 20M ");
                else if (vq == "Low") args.Append("-b:v 2M ");
                else args.Append("-b:v 6M ");
            }
            else // amf / qsv
            {
                if (vq == "High") args.Append("-b:v 15M ");
                else if (vq == "Low") args.Append("-b:v 2M ");
                else args.Append("-b:v 6M ");
            }

            // Instruct FFmpeg to omit audio track during continuous recording
            args.Append("-an ");

            // Circular Segment Muxer. The ring must hold enough 1-second segments to cover the longest
            // clip we might assemble: a normal buffer clip, or - with chaining on - a stitched clip up to
            // EffectiveMaxChainedClipSeconds long (which reaches back to the first tap's pre-roll). It needs
            // an extra ChainWindow of headroom because a chain finalizes that long AFTER its last tap, so
            // the oldest needed segment must survive until then.
            int ringSeconds = settings.ClipChainingEnabled
                ? settings.EffectiveMaxChainedClipSeconds + settings.ChainWindowSeconds
                : settings.BufferLengthSeconds;
            int wrapLimit = Math.Max(135, ringSeconds + 15);
            // Per-run filenames (seg_{seq}_*.ts + segments_{seq}.csv) so a preserved previous run isn't
            // clobbered when the next run restarts numbering at 000.
            string segmentPattern = Path.Combine(_tempSegmentsFolder, $"seg_{_runSeq}_%03d.ts").Replace('\\', '/');
            string csvFilePath = Path.Combine(_tempSegmentsFolder, $"segments_{_runSeq}.csv").Replace('\\', '/');
            args.Append($"-f segment -segment_time 1 -segment_wrap {wrapLimit} -segment_list_size {wrapLimit} -segment_list \"{csvFilePath}\" -segment_list_type csv -segment_format mpegts -reset_timestamps 1 \"{segmentPattern}\"");

            return args.ToString();
        }

        /// <summary>
        /// Resolves a free-form resolution setting into an ffmpeg scale target, or null for native
        /// (no scaling). Accepts "Native"/"", a "{height}p" or bare height (scaled by height with the
        /// aspect ratio preserved via even-width "-2"), or an explicit "{width}x{height}". Tolerant of
        /// junk: anything unrecognized falls through to native so a bad value never breaks recording.
        /// </summary>
        private static string? ResolveScaleSpec(string? resolution)
        {
            string r = (resolution ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(r) || r.StartsWith("native")) return null;

            // Explicit "WxH": scale to exactly that, forcing even dimensions for h264 / yuv420p.
            int xi = r.IndexOf('x');
            if (xi < 0) xi = r.IndexOf('×'); // unicode multiplication sign ×
            if (xi > 0)
            {
                string wp = new string(r.Substring(0, xi).Where(char.IsDigit).ToArray());
                string hp = new string(r.Substring(xi + 1).Where(char.IsDigit).ToArray());
                if (int.TryParse(wp, out int w) && int.TryParse(hp, out int h) && w > 0 && h > 0)
                    return $"{(w % 2 == 0 ? w : w - 1)}:{(h % 2 == 0 ? h : h - 1)}";
                return null;
            }

            // "{height}p" or a bare number: scale by height, keep aspect ratio (-2 = nearest even width).
            string digits = new string(r.Where(char.IsDigit).ToArray());
            if (int.TryParse(digits, out int height) && height > 0)
                return $"-2:{height}";

            return null;
        }

        public void StopRecording()
        {
            // Mark this as a deliberate stop so the Exited handler doesn't treat it as a crash and
            // relaunch the recorder out from under us.
            _stopRequested = true;

            if (_recordingProcess == null)
            {
                _isRecording = false;
                return;
            }

            Logger.Info("Stopping continuous recording process.");
            try
            {
                if (!_recordingProcess.HasExited)
                {
                    _recordingProcess.StandardInput.Write("q");
                    _recordingProcess.StandardInput.Flush();
                    if (!_recordingProcess.WaitForExit(3000))
                    {
                        Logger.Warn("FFmpeg did not exit on 'q' command. Killing process.");
                        _recordingProcess.Kill();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Exception while stopping FFmpeg. Forcing process kill.", ex);
                try { _recordingProcess.Kill(); } catch { }
            }
            finally
            {
                _recordingProcess.Dispose();
                _recordingProcess = null;
                _isRecording = false;
                Logger.Info("Recording process stopped.");
            }
        }

        /// <summary>
        /// Relaunches the capture pipeline after it died unexpectedly mid-recording - most often because
        /// a game grabbed exclusive fullscreen and Desktop Duplication (ddagrab) lost the surface with
        /// DXGI_ERROR_ACCESS_LOST (0x887A0026). A fresh StartRecording re-creates the duplication, which
        /// can then capture the now-fullscreen game; if ddagrab still can't initialize, StartRecording's
        /// own gdigrab/libx264 fallback chain takes over. A short delay lets the display mode-switch
        /// settle first, and a per-minute cap stops a hard-failing config from spinning forever.
        /// </summary>
        private void ScheduleAutoRestart()
        {
            var now = DateTime.UtcNow;
            if ((now - _restartWindowStartUtc).TotalSeconds > RestartWindowSeconds)
            {
                _restartWindowStartUtc = now;
                _restartCount = 0;
            }

            _restartCount++;

            // Past the per-window cap the pipeline is failing persistently, not just blipping. What we do
            // now depends on whether this machine can do ddagrab at all:
            //   - Never proven -> it genuinely can't capture with the current config: give up (unchanged).
            //   - Proven -> ddagrab works here but won't re-acquire right now (a stuck/persistent surface
            //     loss). Rather than leave the user with no buffer, do ONE last-resort start that permits the
            //     flickering gdigrab path so recording continues. The next 60s window resets the counter and
            //     prefers ddagrab again, so we self-heal back to flicker-free the moment the display recovers.
            bool lastResort = false;
            if (_restartCount > MaxRestartsPerWindow)
            {
                if (!_ddagrabProvenThisSession)
                {
                    Logger.Error($"Capture pipeline died and was restarted {MaxRestartsPerWindow}+ times in " +
                        $"{RestartWindowSeconds}s. Giving up auto-restart to avoid a restart storm - the buffer is now stopped.");
                    return;
                }
                lastResort = true;
                Logger.Warn($"ddagrab could not be re-acquired within {MaxRestartsPerWindow} attempts this " +
                    $"window. As a last resort, allowing the GDI capture path so the buffer keeps running " +
                    $"(the cursor may flicker until the display recovers and ddagrab is preferred again).");
            }

            var settings = _activeSettings;
            if (settings == null) return;

            Logger.Warn($"Capture pipeline exited unexpectedly (likely a fullscreen/display mode-switch). " +
                $"Auto-restarting (attempt {_restartCount} this window{(lastResort ? ", last-resort GDI permitted" : "")}).");

            // Escalating settle delay. A display that's still mid-switch needs longer each time it isn't
            // ready; re-probing ddagrab too early is exactly what used to mistake a still-settling display for
            // a hard failure and drop to the flickering gdigrab path. Caps at 3s so we never stall the buffer
            // for long. (Was a flat 1s.)
            int settleMs = Math.Min(1000 + (_restartCount - 1) * 500, 3000);

            Task.Run(() =>
            {
                try
                {
                    Thread.Sleep(settleMs); // let the display mode-switch / fullscreen transition settle
                    if (_stopRequested) return; // user stopped (or quit) during the wait - stand down
                    // preserveBuffer: keep the pre-alt-tab footage so a clip taken right after still has it.
                    StartRecording(settings, forceGdiFallback: lastResort, preserveBuffer: true);
                }
                catch (Exception ex)
                {
                    Logger.Error("Auto-restart of the capture pipeline failed.", ex);
                }
            });
        }

        // ───────────────────────────── multi-run buffer helpers ─────────────────────────────

        private string[] SafeGlob(string pattern)
        {
            try { return Directory.GetFiles(_tempSegmentsFolder, pattern); }
            catch { return Array.Empty<string>(); }
        }

        /// <summary>Deletes every run's segments + lists (and any legacy single-run files) for a clean slate.</summary>
        private void WipeAllRuns()
        {
            foreach (var pattern in new[] { "seg_*.ts", "segments_*.csv", "card_*.ts", "segment_*.ts", "segments.csv" })
                foreach (var f in SafeGlob(pattern))
                    try { File.Delete(f); } catch { }
            Logger.Info("Cleared all buffered runs (fresh start).");
        }

        /// <summary>Evicts non-current runs whose newest segment has aged past <paramref name="keepSeconds"/>.</summary>
        private void EvictOldRuns(int keepSeconds)
        {
            try
            {
                var cutoff = DateTime.UtcNow.AddSeconds(-keepSeconds);
                // Sweep any leaked out-of-focus cards from earlier clips (belt-and-suspenders).
                foreach (var card in SafeGlob("card_*.ts"))
                    try { if (File.GetLastWriteTimeUtc(card) < cutoff) File.Delete(card); } catch { }
                foreach (var csv in SafeGlob("segments_*.csv"))
                {
                    int seq = ParseRunSeq(Path.GetFileName(csv));
                    if (seq < 0 || seq == _runSeq) continue; // never evict the current run

                    var segs = SafeGlob($"seg_{seq}_*.ts");
                    DateTime newest = DateTime.MinValue;
                    foreach (var s in segs)
                        try { var t = File.GetLastWriteTimeUtc(s); if (t > newest) newest = t; } catch { }

                    if (segs.Length == 0 || newest < cutoff)
                    {
                        foreach (var s in segs) { try { File.Delete(s); } catch { } }
                        try { File.Delete(csv); } catch { }
                        _runs.TryRemove(seq, out _);
                        Logger.Info($"Evicted aged run {seq} from the buffer.");
                    }
                }
            }
            catch (Exception ex) { Logger.Error("EvictOldRuns failed (non-fatal).", ex); }
        }

        /// <summary>Longest span a clip could reach back over - keep at least this much history across runs.</summary>
        private static int MaxBufferKeepSeconds(AppSettings s)
        {
            int baseSeconds = s.ClipChainingEnabled
                ? s.EffectiveMaxChainedClipSeconds + s.ChainWindowSeconds
                : s.BufferLengthSeconds;
            return Math.Max(60, baseSeconds + 15);
        }

        /// <summary>Parses N from "segments_N.csv" (or -1 for the legacy unnumbered list).</summary>
        private static int ParseRunSeq(string csvFileName)
        {
            const string prefix = "segments_";
            if (!csvFileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return -1;
            string mid = Path.GetFileNameWithoutExtension(csvFileName).Substring(prefix.Length);
            return int.TryParse(mid, out int n) ? n : -1;
        }

        /// <summary>h264 | hevc | av1 - the family of a resolved encoder name, for a concat-compatible card.</summary>
        private static string CodecFamilyOf(string encoder)
        {
            if (encoder.Contains("hevc") || encoder.Contains("265")) return "hevc";
            if (encoder.Contains("av1")) return "av1";
            return "h264";
        }

        /// <summary>
        /// The real output frame size for a run: the monitor size after the resolution setting's scale is
        /// applied (mirrors ResolveScaleSpec). A gap card must match this exactly to concat with -c copy.
        /// </summary>
        private static (int w, int h) ComputeOutputResolution(AppSettings settings, DisplayHelper.MonitorTarget mon)
        {
            int Even(int v) => v % 2 == 0 ? v : v - 1;
            int monW = Even(Math.Max(2, mon.Width)), monH = Even(Math.Max(2, mon.Height));
            string? spec = ResolveScaleSpec(settings.CaptureResolution);
            if (spec == null) return (monW, monH);

            var parts = spec.Split(':'); // "W:H" or "-2:H"
            if (parts.Length == 2 && int.TryParse(parts[1], out int h) && h > 0)
            {
                if (int.TryParse(parts[0], out int w) && w > 0) return (Even(w), Even(h));
                int wv = (int)Math.Round((double)monW * h / monH); // "-2:H" - preserve aspect, nearest even
                return (Even(Math.Max(2, wv)), Even(h));
            }
            return (monW, monH);
        }

        /// <summary>
        /// Renders a short "GAME OUT OF FOCUS" black card as an mpegts segment matching a run's frame size /
        /// rate / codec, so it concats (-c copy) between real segments to bridge an alt-tab gap. Returns the
        /// path, or null if it couldn't be produced (the caller then simply doesn't stitch across that gap,
        /// so a failure only means "no cross-run clip", never a broken one). Disabled for AV1 (a mismatched
        /// h264 card can't -c copy alongside AV1 segments, and an AV1 card encoder may be absent).
        /// </summary>
        private string? GenerateOutOfFocusCard(double durationSec, RunMeta run, int uniqueId)
        {
            try
            {
                if (run.CodecFamily == "av1") return null;

                double dur = Math.Clamp(durationSec, 0.2, 15.0);
                int w = run.Width > 1 ? run.Width : 1280;
                int h = run.Height > 1 ? run.Height : 720;
                int fps = run.Fps > 0 ? run.Fps : 30;
                string enc = run.CodecFamily == "hevc" ? "libx265" : "libx264";
                string cardPath = Path.Combine(_tempSegmentsFolder, $"card_{_runSeq}_{uniqueId}.ts").Replace('\\', '/');
                int fontSize = Math.Max(18, h / 22);
                string durStr = dur.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

                string baseIn = $"-y -f lavfi -i color=c=black:s={w}x{h}:r={fps}:d={durStr}";
                string encOut = $"-c:v {enc} -pix_fmt yuv420p -g {fps} -f mpegts \"{cardPath}\"";
                string draw = $"-vf \"drawtext=fontfile='C\\:/Windows/Fonts/arialbd.ttf':text='GAME OUT OF FOCUS':fontcolor=white:fontsize={fontSize}:x=(w-text_w)/2:y=(h-text_h)/2\"";

                // Try with the label; if drawtext (font) isn't available, fall back to a plain black card so
                // the gap is still bridged (audio stays in sync either way).
                try { File.Delete(cardPath); } catch { }
                RunFFmpegSync($"{baseIn} {draw} {encOut}", 15000);
                if (!File.Exists(cardPath) || new FileInfo(cardPath).Length == 0)
                    RunFFmpegSync($"{baseIn} {encOut}", 15000);

                if (File.Exists(cardPath) && new FileInfo(cardPath).Length > 0) return cardPath;
            }
            catch (Exception ex) { Logger.Error("Failed to generate out-of-focus card.", ex); }
            return null;
        }

        /// <summary>
        /// Saves the standard "last N seconds" clip ending at the current moment - the normal single-tap
        /// capture. Equivalent to assembling the window [now - buffer, now].
        /// </summary>
        public Task<Clip?> SaveClipAsync(AppSettings settings, AudioCaptureManager audioManager, string gameName = "")
            => AssembleClipAsync(settings, audioManager, gameName, null, null, null, null);

        /// <summary>
        /// Assembles a single STITCHED clip for a chain of hotkey taps: one continuous file spanning from
        /// <paramref name="windowStartUtc"/> (the first tap minus the buffer) through the newest buffered
        /// frame, with a marker recorded at each tap so the library + player can show where the moments
        /// were. <paramref name="createdAt"/> is forced so the stitched clip keeps the first tap's place in
        /// the timeline after it replaces the short first-tap clip.
        /// </summary>
        public Task<Clip?> AssembleChainedClipAsync(AppSettings settings, AudioCaptureManager audioManager, string gameName,
            DateTime windowStartUtc, DateTime windowEndUtc, IReadOnlyList<DateTime> tapTimesUtc, DateTime createdAt)
            => AssembleClipAsync(settings, audioManager, gameName, windowStartUtc, windowEndUtc, tapTimesUtc, createdAt);

        /// <summary>
        /// Core clip assembler. With <paramref name="windowStartUtc"/> null it behaves exactly like the
        /// classic "grab the last buffer seconds" save; with it set, it reaches back to that wall-clock
        /// instant (clamped to the buffer length and the chain ceiling) to stitch a chained clip and tags
        /// the result with per-tap markers.
        /// </summary>
        private async Task<Clip?> AssembleClipAsync(AppSettings settings, AudioCaptureManager audioManager, string gameName,
            DateTime? windowStartUtc, DateTime? windowEndUtc, IReadOnlyList<DateTime>? tapTimesUtc, DateTime? createdAtOverride)
        {
            if (!_isRecording)
            {
                Logger.Warn("SaveClipAsync called but recorder is not currently active.");
                return null;
            }

            return await Task.Run(() =>
            {
                string tempVideoPath = Path.Combine(_tempSegmentsFolder, "temp_video.mp4").Replace('\\', '/');
                string tempAudioPath = Path.Combine(_tempSegmentsFolder, "temp_audio.wav").Replace('\\', '/');

                try
                {
                    Logger.Info("SaveClipAsync: Assembling buffered clip segments...");
                    int assemblyRunSeq = _runSeq; // snapshot: a concurrent auto-restart mustn't shift "current run" mid-assembly

                    var segmentPaths = new List<string>();
                    var segmentDurations = new List<double>();
                    var segmentRunSeq = new List<int>(); // which capture run each segment belongs to

                    // Gather segments across ALL runs (survives alt-tab). Each run has its own
                    // segments_{seq}.csv; reading them in seq order (== time order) keeps the combined list
                    // chronological. Fall back to the legacy single segments.csv if per-run lists are absent
                    // (e.g. first save after an in-place upgrade).
                    var runCsvs = SafeGlob("segments_*.csv")
                        .Select(p => (seq: ParseRunSeq(Path.GetFileName(p)), path: p))
                        .Where(x => x.seq >= 0)
                        .OrderBy(x => x.seq)
                        .ToList();
                    if (runCsvs.Count == 0)
                    {
                        string legacy = Path.Combine(_tempSegmentsFolder, "segments.csv");
                        if (File.Exists(legacy)) runCsvs.Add((-1, legacy));
                    }
                    if (runCsvs.Count == 0)
                    {
                        Logger.Error("SaveClipAsync: no segment lists exist yet. Nothing recorded.");
                        return null;
                    }

                    foreach (var (seq, csvPath) in runCsvs)
                    {
                        try
                        {
                            // FileShare.ReadWrite so we don't fight FFmpeg still appending to the current run.
                            using var fs = new FileStream(csvPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                            using var sr = new StreamReader(fs, Encoding.UTF8);
                            string? line;
                            while ((line = sr.ReadLine()) != null)
                            {
                                if (string.IsNullOrWhiteSpace(line)) continue;
                                var parts = line.Split(',');
                                if (parts.Length < 3) continue;
                                if (double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double startTime) &&
                                    double.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double endTime))
                                {
                                    string resolvedPath = parts[0].Trim();
                                    if (!Path.IsPathRooted(resolvedPath))
                                        resolvedPath = Path.Combine(_tempSegmentsFolder, resolvedPath).Replace('\\', '/');
                                    segmentPaths.Add(resolvedPath);
                                    segmentDurations.Add(endTime - startTime);
                                    segmentRunSeq.Add(seq);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"SaveClipAsync: failed reading {Path.GetFileName(csvPath)} (skipping that run).", ex);
                        }
                    }

                    if (segmentPaths.Count < 2)
                    {
                        Logger.Warn($"SaveClipAsync: Too few segments ({segmentPaths.Count}). Cannot produce clip.");
                        return null;
                    }

                    // Choose the END of the clip (the "anchor" segment we walk back from) and how many
                    // seconds to collect. For a normal clip the anchor is the newest segment on disk and we
                    // collect the buffer length. For a chained clip the anchor is the newest segment that
                    // ends at/just after windowEndUtc (the last tap + a short tail) - so the clip ends right
                    // after the final moment instead of trailing the whole chain window of dead footage -
                    // and we collect back to windowStartUtc (the first tap's pre-roll), clamped to the
                    // [buffer, chain-ceiling] range so audio retention + the segment ring always cover it.
                    int anchorIndex = -1;
                    for (int i = segmentPaths.Count - 1; i >= 0; i--)
                    {
                        if (File.Exists(segmentPaths[i])) { anchorIndex = i; break; }
                    }
                    double targetBufferSeconds = settings.BufferLengthSeconds;
                    if (windowStartUtc.HasValue)
                    {
                        if (windowEndUtc.HasValue)
                        {
                            for (int i = segmentPaths.Count - 1; i >= 0; i--)
                            {
                                if (!File.Exists(segmentPaths[i])) continue;
                                DateTime endWall;
                                try { endWall = File.GetLastWriteTimeUtc(segmentPaths[i]); } catch { continue; }
                                if (endWall <= windowEndUtc.Value.AddSeconds(0.5)) { anchorIndex = i; break; }
                            }
                        }

                        DateTime anchorEndWall = DateTime.UtcNow;
                        if (anchorIndex >= 0 && File.Exists(segmentPaths[anchorIndex]))
                        {
                            try { anchorEndWall = File.GetLastWriteTimeUtc(segmentPaths[anchorIndex]); } catch { }
                        }
                        double requested = (anchorEndWall - windowStartUtc.Value).TotalSeconds;
                        targetBufferSeconds = Math.Clamp(requested, settings.BufferLengthSeconds, settings.EffectiveMaxChainedClipSeconds);
                        Logger.Info($"AssembleClipAsync: chained window requested {requested:F1}s -> collecting {targetBufferSeconds:F1}s (anchor segment #{anchorIndex}).");
                    }
                    if (anchorIndex < 0) anchorIndex = segmentPaths.Count - 1; // no existing segment found; fall back

                    var selectedSegments = new List<string>();
                    double collectedDuration = 0;
                    string? newestSegmentPath = null; // most recent included segment, used to anchor the wall clock

                    int distinctRuns = segmentRunSeq.Distinct().Count();

                    if (distinctRuns <= 1)
                    {
                        // ── Single run (no alt-tab): the original walk, byte-for-byte. ──
                        // Walk backwards from the newest COMPLETED segment (Count-1 keeps the last ~2s the
                        // old Count-3 start used to drop). Only the sub-second segment still being written at
                        // the instant of the press is unavoidably lost.
                        for (int i = anchorIndex; i >= 0; i--)
                        {
                            if (File.Exists(segmentPaths[i]))
                            {
                                if (newestSegmentPath == null) newestSegmentPath = segmentPaths[i];
                                selectedSegments.Add(segmentPaths[i]);
                                collectedDuration += segmentDurations[i];
                            }
                            else Logger.Warn($"SaveClipAsync: Expected segment file missing: {segmentPaths[i]}");

                            if (collectedDuration >= targetBufferSeconds) break;
                        }
                    }
                    else
                    {
                        // ── Multiple runs (capture restarted, e.g. alt-tab out of exclusive fullscreen):
                        //    stitch ALL surviving footage across runs, newest first. Recorded footage is
                        //    NEVER skipped or hidden - the per-run IsGame tag is diagnostic-only, because a
                        //    start-moment focus snapshot proved unreliable in the field (an auto-restart
                        //    lands mid-transition, mislabelling minutes of real gameplay, which either
                        //    carded over live play or shifted whole clips minutes into the past). A
                        //    "GAME OUT OF FOCUS" card is spliced ONLY over true dead-capture holes - wall-
                        //    clock time between runs where no video exists at all (measured from segment
                        //    file times, hard facts) - sized to the REAL hole so the continuous audio track
                        //    stays in sync. Runs whose codec family or frame size differ from the anchor
                        //    run's can't -c copy into one stream, so the walk stops at such a boundary
                        //    (a shorter clip, never a broken one).
                        RunMeta? anchorMeta = _runs.TryGetValue(segmentRunSeq[anchorIndex], out var am) ? am : null;
                        DateTime lastStartWall = DateTime.MinValue;
                        for (int i = anchorIndex; i >= 0; i--)
                        {
                            if (!File.Exists(segmentPaths[i])) continue;

                            // Codec/size boundary: an older run encoded differently (e.g. a last-resort
                            // fallback rung or a monitor change) -> stop rather than emit a stream players
                            // can't decode past the junction.
                            if (anchorMeta != null && _runs.TryGetValue(segmentRunSeq[i], out var segMeta) &&
                                (segMeta.CodecFamily != anchorMeta.CodecFamily ||
                                 segMeta.Width != anchorMeta.Width || segMeta.Height != anchorMeta.Height))
                            {
                                Logger.Info($"Cross-run stitch: stopping at run {segmentRunSeq[i]} " +
                                    $"({segMeta.CodecFamily} {segMeta.Width}x{segMeta.Height}) - incompatible with " +
                                    $"anchor run ({anchorMeta.CodecFamily} {anchorMeta.Width}x{anchorMeta.Height}).");
                                break;
                            }

                            DateTime segEndWall;
                            try { segEndWall = File.GetLastWriteTimeUtc(segmentPaths[i]); } catch { continue; }
                            double segDur = segmentDurations[i];

                            // A wall-clock hole before the previously-included segment = dead capture time
                            // (the restart settle). Bridge it with a card of that exact length so the
                            // continuous audio stays in sync across the join.
                            if (lastStartWall != DateTime.MinValue)
                            {
                                double gap = (lastStartWall - segEndWall).TotalSeconds;
                                if (gap > OutOfFocusMaxBridgeSeconds) break; // capture dead too long: stop reaching back
                                if (gap > 0.4)
                                {
                                    string? card = anchorMeta != null
                                        ? GenerateOutOfFocusCard(gap, anchorMeta, selectedSegments.Count)
                                        : null; // no meta for the anchor run -> can't size a matching card
                                    if (card == null) break; // can't bridge safely -> stop rather than desync
                                    selectedSegments.Add(card);
                                    collectedDuration += gap;
                                    if (collectedDuration >= targetBufferSeconds) break;
                                }
                            }

                            if (newestSegmentPath == null) newestSegmentPath = segmentPaths[i];
                            selectedSegments.Add(segmentPaths[i]);
                            collectedDuration += segDur;
                            lastStartWall = segEndWall.AddSeconds(-segDur);
                            if (collectedDuration >= targetBufferSeconds) break;
                        }

                        // Safety net: if the cross-run walk yielded essentially nothing (e.g. it stopped
                        // immediately at a boundary or an unbridgeable hole), fall back to the plain
                        // CURRENT-run walk - exactly the pre-multi-run behavior - so a clip is never lost
                        // to the stitching logic itself.
                        int realCount = selectedSegments.Count(p => !Path.GetFileName(p).StartsWith("card_", StringComparison.OrdinalIgnoreCase));
                        if (realCount < 2 || collectedDuration < 1.0)
                        {
                            Logger.Warn("Cross-run assembly yielded too little; falling back to the current run only.");
                            foreach (var c in selectedSegments.Where(p => Path.GetFileName(p).StartsWith("card_", StringComparison.OrdinalIgnoreCase)))
                                try { File.Delete(c); } catch { }
                            selectedSegments.Clear();
                            collectedDuration = 0;
                            newestSegmentPath = null;
                            for (int i = anchorIndex; i >= 0; i--)
                            {
                                if (segmentRunSeq[i] != assemblyRunSeq || !File.Exists(segmentPaths[i])) continue;
                                if (newestSegmentPath == null) newestSegmentPath = segmentPaths[i];
                                selectedSegments.Add(segmentPaths[i]);
                                collectedDuration += segmentDurations[i];
                                if (collectedDuration >= targetBufferSeconds) break;
                            }
                        }
                    }

                    if (selectedSegments.Count == 0)
                    {
                        Logger.Error("SaveClipAsync: No valid segment files found to concatenate.");
                        return null;
                    }

                    selectedSegments.Reverse(); // Put in chronological order (oldest first)
                    double durationSeconds = collectedDuration;

                    // Anchor the audio to the same wall clock as the video. The newest included
                    // segment's file write-time marks the real-world instant the video clip ends;
                    // its start is that minus the collected duration. The audio is then rendered for
                    // exactly this window, so the two streams share one clock instead of two.
                    DateTime videoEndWallUtc;
                    try
                    {
                        videoEndWallUtc = (newestSegmentPath != null && File.Exists(newestSegmentPath))
                            ? File.GetLastWriteTimeUtc(newestSegmentPath)
                            : DateTime.UtcNow;
                    }
                    catch
                    {
                        videoEndWallUtc = DateTime.UtcNow;
                    }
                    DateTime videoStartWallUtc = videoEndWallUtc.AddSeconds(-collectedDuration);

                    // Map each tap's wall-clock instant to an offset within the assembled clip, so the
                    // library + player can show where the chained "moments" are. De-duplicate taps that
                    // land within ~0.3s of each other (double-fire / held key).
                    string chainMarkersCsv = "";
                    double firstMarkerOffset = -1;
                    if (tapTimesUtc != null && tapTimesUtc.Count > 0)
                    {
                        var offsets = new List<double>();
                        foreach (var tap in tapTimesUtc)
                        {
                            double o = Math.Clamp((tap - videoStartWallUtc).TotalSeconds, 0, durationSeconds);
                            offsets.Add(o);
                        }
                        offsets.Sort();
                        var deduped = new List<double>();
                        foreach (var o in offsets)
                            if (deduped.Count == 0 || o - deduped[deduped.Count - 1] > 0.3) deduped.Add(o);
                        chainMarkersCsv = string.Join(",", deduped.Select(o => o.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)));
                        if (deduped.Count > 0) firstMarkerOffset = deduped[0];
                    }

                    // Kill detection: map detected kills inside this window to clip-relative offsets,
                    // exactly like the chain taps above (own lane, same CSV format). Purely additive
                    // metadata - any failure here must never fail the save, and with the feature off
                    // the provider is null so this is a no-op.
                    string killMarkersCsv = "";
                    try
                    {
                        var kills = KillTimestampProvider?.Invoke(videoStartWallUtc, videoEndWallUtc);
                        if (kills != null && kills.Count > 0)
                        {
                            var kOffsets = new List<double>();
                            foreach (var kill in kills)
                            {
                                double o = Math.Clamp((kill - videoStartWallUtc).TotalSeconds, 0, durationSeconds);
                                kOffsets.Add(o);
                            }
                            kOffsets.Sort();
                            var kDeduped = new List<double>();
                            foreach (var o in kOffsets)
                                if (kDeduped.Count == 0 || o - kDeduped[kDeduped.Count - 1] > 0.3) kDeduped.Add(o);
                            killMarkersCsv = string.Join(",", kDeduped.Select(o => o.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)));
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"Kill marker mapping failed (clip saved without kill markers): {ex.Message}");
                    }

                    Logger.Info($"SaveClipAsync: Selected {selectedSegments.Count} segments (duration: {durationSeconds:F2}s). " +
                        $"Range: {Path.GetFileName(selectedSegments.First())} -> {Path.GetFileName(selectedSegments.Last())}");

                    // ── Freeze the audio for this window NOW, before the multi-second video concat/mux/
                    //    thumbnail work below. The audio ring buffers are pruned continuously to a fixed
                    //    history; the old code didn't read them until AFTER all that video work (the
                    //    sidecars went last, ~9s after the window was anchored), by which point the oldest
                    //    blocks the window needs had already been pruned away - baking 1–5s of leading
                    //    silence into every clip (worst in the in-app player + waveform, which use the
                    //    sidecars). Snapshotting here, within ms of anchoring the window, keeps the audio
                    //    aligned to the video no matter how long the encode takes.
                    string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Wisp");
                    float systemGain = Math.Clamp(settings.SystemAudioVolume / 100f, 0f, 4f);
                    float micGain = Math.Clamp(settings.MicrophoneVolume / 100f, 0f, 4f);
                    float socialGain = Math.Clamp(settings.SocialAudioVolume / 100f, 0f, 4f);
                    double audioOffsetSec = settings.AudioOffsetMs / 1000.0;
                    // A positive offset shifts the audio window earlier in wall-clock time, which delays
                    // the audio relative to the video in the finished clip.
                    DateTime audioStartUtc = videoStartWallUtc.AddSeconds(-audioOffsetSec);
                    DateTime audioEndUtc = videoEndWallUtc.AddSeconds(-audioOffsetSec);
                    Logger.Info($"SaveClipAsync: Saving audio for window {audioStartUtc:O} -> {audioEndUtc:O} (sysGain={systemGain:F2}, micGain={micGain:F2}, socialGain={socialGain:F2}, offset={settings.AudioOffsetMs}ms).");

                    // Baked stereo mix for the shareable MP4 (muxed in step 8).
                    audioManager.SaveAudioClip(tempAudioPath, audioStartUtc, audioEndUtc, systemGain, micGain, socialGain);

                    // Per-source sidecars (for the in-app live mixer + waveforms) snapshot the SAME frozen
                    // window. Write the raw WAVs now; they're compressed to .m4a after the video work.
                    string sysWav = Path.Combine(_tempSegmentsFolder, "temp_system.wav").Replace('\\', '/');
                    string micWav = Path.Combine(_tempSegmentsFolder, "temp_mic.wav").Replace('\\', '/');
                    string socialWav = Path.Combine(_tempSegmentsFolder, "temp_social.wav").Replace('\\', '/');
                    bool hasSys = false, hasMic = false, hasSocial = false;
                    try
                    {
                        (hasSys, hasMic, hasSocial) = audioManager.SaveSourceClips(sysWav, micWav, socialWav, audioStartUtc, audioEndUtc);
                    }
                    catch (Exception exSrc)
                    {
                        Logger.Error("Failed to snapshot per-source audio; the live mixer/waveform will be unavailable for this clip.", exSrc);
                    }

                    // 4. Generate concat list file
                    string concatFilePath = Path.Combine(_tempSegmentsFolder, "concat.txt").Replace('\\', '/');
                    using (var sw = new StreamWriter(concatFilePath, false, new UTF8Encoding(false)))
                    {
                        foreach (var filePath in selectedSegments)
                        {
                            string escapedPath = filePath.Replace(@"\\", "/").Replace("'", @"'\\''");
                            sw.WriteLine($"file '{escapedPath}'");
                        }
                    }

                    // 5. Concatenate video segments into a temporary silent video file
                    string concatArgs = $"-y -avoid_negative_ts make_zero -f concat -safe 0 -i \"{concatFilePath}\" -c copy \"{tempVideoPath}\"";
                    RunFFmpegSync(concatArgs, 30000);

                    // If the concat produced nothing AND we spliced cards, the likely cause is a card whose
                    // codec params didn't stream-copy next to the real segments. Rather than fail the whole
                    // clip, rewrite the list WITHOUT the cards and retry - the gameplay still stitches (audio
                    // may drift by the bridged gap, but a clip beats no clip).
                    bool badConcat = !File.Exists(tempVideoPath) || new FileInfo(tempVideoPath).Length == 0;
                    if (badConcat && selectedSegments.Any(p => Path.GetFileName(p).StartsWith("card_", StringComparison.OrdinalIgnoreCase)))
                    {
                        Logger.Warn("Concat with out-of-focus cards failed; retrying without the cards.");
                        var noCards = selectedSegments.Where(p => !Path.GetFileName(p).StartsWith("card_", StringComparison.OrdinalIgnoreCase)).ToList();
                        if (noCards.Count > 0)
                        {
                            using (var sw = new StreamWriter(concatFilePath, false, new UTF8Encoding(false)))
                                foreach (var filePath in noCards)
                                    sw.WriteLine($"file '{filePath.Replace(@"\\", "/").Replace("'", @"'\\''")}'");
                            RunFFmpegSync(concatArgs, 30000);
                            badConcat = !File.Exists(tempVideoPath) || new FileInfo(tempVideoPath).Length == 0;
                        }
                    }
                    if (badConcat)
                    {
                        Logger.Error("Failed to output concatenated video clip.");
                        return null;
                    }

                    // 7. Create output folder
                    if (!Directory.Exists(settings.OutputFolder))
                    {
                        Directory.CreateDirectory(settings.OutputFolder);
                    }

                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    string safeGame = SanitizeForFilename(gameName);
                    string template = string.IsNullOrWhiteSpace(settings.FilenameTemplate) ? "{Game}_{Date}_{Time}" : settings.FilenameTemplate;
                    
                    string generatedName = template
                        .Replace("{Game}", string.IsNullOrEmpty(safeGame) ? "Desktop" : safeGame, StringComparison.OrdinalIgnoreCase)
                        .Replace("{Date}", DateTime.Now.ToString("yyyyMMdd"), StringComparison.OrdinalIgnoreCase)
                        .Replace("{Time}", DateTime.Now.ToString("HHmmss"), StringComparison.OrdinalIgnoreCase);
                    
                    if (string.IsNullOrWhiteSpace(generatedName)) generatedName = $"clip_{timestamp}";
                    string outputFilename = $"{SanitizeForFilename(generatedName)}.mp4";
                    string outputFilePath = Path.Combine(settings.OutputFolder, outputFilename).Replace('\\', '/');

                    // 8. Combine the silent video and mixed WAV audio into the final MP4 (encode audio to AAC)
                    Logger.Info($"SaveClipAsync: Muxing video and audio tracks to {outputFilePath}");
                    string combineArgs = $"-y -fflags +genpts -avoid_negative_ts make_zero -i \"{tempVideoPath}\" -i \"{tempAudioPath}\" -c:v copy -c:a aac -b:a {settings.AudioBitrateKbps}k -shortest -movflags +faststart \"{outputFilePath}\"";
                    RunFFmpegSync(combineArgs, 30000);

                    if (!File.Exists(outputFilePath) || new FileInfo(outputFilePath).Length == 0)
                    {
                        Logger.Error("Failed to output combined AV clip.");
                        return null;
                    }

                    var clipInfo = new FileInfo(outputFilePath);
                    Logger.Info($"SaveClipAsync: Clip file created: {outputFilePath} ({clipInfo.Length} bytes)");

                    // 9. Extract Thumbnail. For a chained clip, grab the frame at the first moment (the play
                    //    that kicked the chain off) so the card preview shows the action; otherwise mid-frame.
                    double targetSeek = (firstMarkerOffset >= 0 && firstMarkerOffset < durationSeconds)
                        ? firstMarkerOffset
                        : durationSeconds / 2.0;
                    string thumbnailPath = Path.Combine(appData, "thumbnails", $"thumb_{timestamp}.jpg").Replace('\\', '/');

                    string targetSeekStr = targetSeek.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                    // Downscale the thumbnail to gallery size. A full-resolution JPEG would decode to
                    // ~8 MB in the WPF gallery; a 480px-wide one is a fraction of that.
                    string thumbArgs = $"-y -ss {targetSeekStr} -i \"{outputFilePath}\" -vframes 1 -vf \"scale=480:-2\" -f image2 \"{thumbnailPath}\"";
                    RunFFmpegSync(thumbArgs, 10000);

                    // Fallback to start if middle frame extraction fails
                    if (!File.Exists(thumbnailPath))
                    {
                        Logger.Warn("Thumbnail mid-frame extraction failed. Retrying extraction from start offset 0.0.");
                        string fallbackArgs = $"-y -ss 0.0 -i \"{outputFilePath}\" -vframes 1 -vf \"scale=480:-2\" -f image2 \"{thumbnailPath}\"";
                        RunFFmpegSync(fallbackArgs, 10000);
                    }

                    // 9b. Compress the per-source WAVs (already frozen up front, before the video work)
                    //     into .m4a sidecars so the in-app player can mix them live + draw a waveform per
                    //     source. The shareable MP4 still carries the baked stereo mix.
                    string systemTrackPath = "";
                    string micTrackPath = "";
                    string socialTrackPath = "";
                    try
                    {
                        string tracksFolder = Path.Combine(appData, "tracks");
                        if (!Directory.Exists(tracksFolder)) Directory.CreateDirectory(tracksFolder);

                        // Match the preview tracks to the chosen audio quality so what the user hears in the
                        // in-app mixer is as crisp as the baked clip (these .m4a sidecars feed the live player).
                        int trackKbps = settings.AudioBitrateKbps;
                        if (hasSys)
                        {
                            string outSys = Path.Combine(tracksFolder, $"track_{timestamp}.sys.m4a").Replace('\\', '/');
                            RunFFmpegSync($"-y -i \"{sysWav}\" -c:a aac -b:a {trackKbps}k \"{outSys}\"", 20000);
                            if (File.Exists(outSys)) systemTrackPath = outSys;
                        }
                        if (hasMic)
                        {
                            string outMic = Path.Combine(tracksFolder, $"track_{timestamp}.mic.m4a").Replace('\\', '/');
                            RunFFmpegSync($"-y -i \"{micWav}\" -c:a aac -b:a {trackKbps}k \"{outMic}\"", 20000);
                            if (File.Exists(outMic)) micTrackPath = outMic;
                        }
                        if (hasSocial)
                        {
                            string outSocial = Path.Combine(tracksFolder, $"track_{timestamp}.social.m4a").Replace('\\', '/');
                            RunFFmpegSync($"-y -i \"{socialWav}\" -c:a aac -b:a {trackKbps}k \"{outSocial}\"", 20000);
                            if (File.Exists(outSocial)) socialTrackPath = outSocial;
                        }

                        try { if (File.Exists(sysWav)) File.Delete(sysWav); } catch { }
                        try { if (File.Exists(micWav)) File.Delete(micWav); } catch { }
                        try { if (File.Exists(socialWav)) File.Delete(socialWav); } catch { }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("Failed to render separate audio tracks; the live mixer will be unavailable for this clip.", ex);
                    }

                    // 10. Register in database
                    var clip = new Clip
                    {
                        FilePath = outputFilePath,
                        ThumbnailPath = File.Exists(thumbnailPath) ? thumbnailPath : "",
                        Filename = outputFilename,
                        CreatedAt = createdAtOverride ?? DateTime.Now,
                        DurationSeconds = durationSeconds,
                        FileSizeBytes = clipInfo.Length,
                        GameName = gameName ?? "",
                        SystemTrackPath = systemTrackPath,
                        MicTrackPath = micTrackPath,
                        SocialTrackPath = socialTrackPath,
                        ChainMarkers = chainMarkersCsv,
                        KillMarkers = killMarkersCsv
                    };

                    _dbService.AddClip(clip);
                    Logger.Info($"Registered clip '{outputFilename}' in database (ID: {clip.Id})");

                    // Clean up temporary session files (incl. any out-of-focus cards generated for this clip)
                    try { File.Delete(concatFilePath); } catch { }
                    try { File.Delete(tempVideoPath); } catch { }
                    try { File.Delete(tempAudioPath); } catch { }
                    foreach (var c in selectedSegments.Where(p => Path.GetFileName(p).StartsWith("card_", StringComparison.OrdinalIgnoreCase)))
                        try { File.Delete(c); } catch { }

                    Logger.Info("SaveClipAsync: Temp files cleaned up. Save operation completed.");
                    return clip;
                }
                catch (Exception ex)
                {
                    Logger.Error("Failed to save clip.", ex);
                    
                    // Cleanup on error
                    try { if (File.Exists(tempVideoPath)) File.Delete(tempVideoPath); } catch { }
                    try { if (File.Exists(tempAudioPath)) File.Delete(tempAudioPath); } catch { }
                    
                    return null;
                }
            });
        }

        /// <summary>
        /// Runs an FFmpeg command synchronously and waits for it to complete.
        /// </summary>
        private void RunFFmpegSync(string arguments, int timeoutMs = 30000)
        {
            var psi = new ProcessStartInfo
            {
                FileName = _ffmpegExePath.Replace('\\', '/'),
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            Logger.Info($"RunFFmpegSync: Running: {_ffmpegExePath} {arguments}");

            try
            {
                using var p = Process.Start(psi);
                if (p != null)
                {
                    ChildProcessTracker.AddProcess(p);
                    string stderr = p.StandardError.ReadToEnd();
                    p.WaitForExit(timeoutMs);
                    if (p.ExitCode != 0)
                    {
                        Logger.Error($"RunFFmpegSync: FFmpeg exited with code {p.ExitCode}. Stderr: {stderr}");
                    }
                    else
                    {
                        Logger.Info($"RunFFmpegSync: Command finished successfully.");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"RunFFmpegSync exception.", ex);
            }
        }

        /// <summary>
        /// Kills capture processes left over from a previous Wisp session that crashed or was force-quit
        /// before the Job Object could reap them. We only touch instances launched from OUR extracted binary
        /// path, so a user's unrelated ffmpeg is never affected. Must run BEFORE we start our own recorder so
        /// we don't kill the fresh process. Matches both the current Wisp-branded name and the legacy
        /// "ffmpeg" name so zombies from a pre-rename build still get cleaned up after upgrade.
        /// </summary>
        public void KillOrphanedFFmpeg()
        {
            try
            {
                // "Our" binary lives under the app's data folder. We also match the legacy "Clippy"
                // folder so zombies spawned before the rebrand get cleaned up on first launch too.
                string appDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var ourRoots = new[]
                {
                    Path.Combine(appDataRoot, "Wisp") + Path.DirectorySeparatorChar,
                    Path.Combine(appDataRoot, "Clippy") + Path.DirectorySeparatorChar
                };

                // Process names have no ".exe" suffix. Look for the current capture name plus any legacy
                // names, so an upgrade still reaps orphans the old build left behind.
                var processNames = new[] { Path.GetFileNameWithoutExtension(CaptureExeName) }
                    .Concat(LegacyExeNames.Select(Path.GetFileNameWithoutExtension))
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase);

                foreach (var name in processNames)
                {
                    foreach (var p in Process.GetProcessesByName(name))
                    {
                        try
                        {
                            string? path = p.MainModule?.FileName;
                            if (path != null)
                            {
                                string full = Path.GetFullPath(path);
                                bool isOurs = ourRoots.Any(r => full.StartsWith(r, StringComparison.OrdinalIgnoreCase));
                                if (isOurs)
                                {
                                    Logger.Warn($"Killing orphaned capture process '{name}' (pid {p.Id}) left over from a previous session.");
                                    p.Kill();
                                    p.WaitForExit(2000);
                                }
                            }
                        }
                        catch { /* access denied / already gone - skip */ }
                        finally { p.Dispose(); }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("KillOrphanedFFmpeg failed.", ex);
            }
        }

        /// <summary>
        /// Scans <paramref name="outputFolder"/> for .mp4 files that aren't in the library yet and adds
        /// them. Used so clips already on disk (e.g. recorded before the rebrand, or in a folder the
        /// user just pointed Wisp at) show up. Imported clips have no separate audio sidecars, so the
        /// in-player live mixer is simply unavailable for them. Returns the number imported.
        /// Intended to run on a background thread - it probes duration and renders a thumbnail per file.
        /// </summary>
        public int ImportUntrackedClips(string outputFolder)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(outputFolder) || !Directory.Exists(outputFolder)) return 0;

                var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in _dbService.GetAllClips())
                {
                    try { known.Add(Path.GetFullPath(c.FilePath)); } catch { }
                }

                int added = 0;
                foreach (var file in Directory.GetFiles(outputFolder, "*.mp4"))
                {
                    try
                    {
                        if (known.Contains(Path.GetFullPath(file))) continue;

                        var fi = new FileInfo(file);
                        if (fi.Length == 0) continue;

                        double duration = ProbeDurationSeconds(file);
                        var clip = new Clip
                        {
                            FilePath = file.Replace('\\', '/'),
                            Filename = fi.Name,
                            ThumbnailPath = GenerateImportThumbnail(file, duration),
                            CreatedAt = fi.LastWriteTime,
                            DurationSeconds = duration,
                            FileSizeBytes = fi.Length,
                            GameName = ParseGameFromFilename(fi.Name),
                            SystemTrackPath = "",
                            MicTrackPath = ""
                        };
                        _dbService.AddClip(clip);
                        added++;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"ImportUntrackedClips: skipped '{file}': {ex.Message}");
                    }
                }

                if (added > 0)
                    Logger.Info($"ImportUntrackedClips: imported {added} pre-existing clip(s) from {outputFolder}.");
                return added;
            }
            catch (Exception ex)
            {
                Logger.Error("ImportUntrackedClips failed.", ex);
                return 0;
            }
        }

        /// <summary>
        /// Imports a single existing .mp4 into the library (probes duration, renders a thumbnail) and
        /// returns the new <see cref="Clip"/>. If the file is already tracked, returns that existing
        /// clip instead of adding a duplicate. Returns null if the file is missing/empty. Exposed for
        /// the plugin API (<c>IClipLibrary.ImportClip</c>) so plugins that produce their own videos can
        /// register them. Runs synchronously (probe + thumbnail) - call off the UI thread.
        /// </summary>
        public Clip? ImportSingleFile(string filePath, string gameName = "")
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return null;
                var fi = new FileInfo(filePath);
                if (fi.Length == 0) return null;

                string fullPath = Path.GetFullPath(filePath);
                var existing = _dbService.GetAllClips()
                    .FirstOrDefault(c => string.Equals(Path.GetFullPath(c.FilePath), fullPath, StringComparison.OrdinalIgnoreCase));
                if (existing != null) return existing;

                double duration = ProbeDurationSeconds(filePath);
                var clip = new Clip
                {
                    FilePath = filePath.Replace('\\', '/'),
                    Filename = fi.Name,
                    ThumbnailPath = GenerateImportThumbnail(filePath, duration),
                    CreatedAt = fi.LastWriteTime,
                    DurationSeconds = duration,
                    FileSizeBytes = fi.Length,
                    GameName = string.IsNullOrWhiteSpace(gameName) ? ParseGameFromFilename(fi.Name) : gameName.Trim(),
                };
                _dbService.AddClip(clip);
                Logger.Info($"ImportSingleFile: imported '{fi.Name}' (id {clip.Id}).");
                return clip;
            }
            catch (Exception ex)
            {
                Logger.Error($"ImportSingleFile failed for '{filePath}'.", ex);
                return null;
            }
        }

        /// <summary>Reads a media file's duration (seconds) from ffmpeg's banner output. 0 on failure.</summary>
        private double ProbeDurationSeconds(string file)
        {
            try
            {
                string stderr = RunFFmpegCaptureStderr($"-hide_banner -i \"{file.Replace('\\', '/')}\"", 10000);
                var m = System.Text.RegularExpressions.Regex.Match(stderr, @"Duration:\s*(\d+):(\d{2}):(\d{2})\.(\d+)");
                if (m.Success)
                {
                    int h = int.Parse(m.Groups[1].Value);
                    int mins = int.Parse(m.Groups[2].Value);
                    int secs = int.Parse(m.Groups[3].Value);
                    double frac = double.Parse("0." + m.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture);
                    return h * 3600 + mins * 60 + secs + frac;
                }
            }
            catch { }
            return 0;
        }

        /// <summary>Renders a gallery-sized thumbnail for an imported clip. Returns "" if it can't.</summary>
        private string GenerateImportThumbnail(string videoFile, double durationSeconds)
        {
            try
            {
                string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Wisp");
                string thumbsFolder = Path.Combine(appData, "thumbnails");
                if (!Directory.Exists(thumbsFolder)) Directory.CreateDirectory(thumbsFolder);

                string thumb = Path.Combine(thumbsFolder, $"thumb_import_{Guid.NewGuid():N}.jpg").Replace('\\', '/');
                double seek = durationSeconds > 1 ? durationSeconds / 2.0 : 0;
                string seekStr = seek.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                RunFFmpegSync($"-y -ss {seekStr} -i \"{videoFile.Replace('\\', '/')}\" -vframes 1 -vf \"scale=480:-2\" -f image2 \"{thumb}\"", 10000);
                return File.Exists(thumb) ? thumb : "";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// Recovers the app/game tag from a Wisp clip filename ("{Game}_yyyyMMdd_HHmmss.mp4"). Returns
        /// "" for the generic "clip_..." name or any foreign file that doesn't match the pattern.
        /// </summary>
        private static string ParseGameFromFilename(string fileName)
        {
            string name = Path.GetFileNameWithoutExtension(fileName);
            var m = System.Text.RegularExpressions.Regex.Match(name, @"^(.*)_(\d{8}_\d{6})$");
            if (m.Success)
            {
                string game = m.Groups[1].Value.Trim();
                if (game.Equals("clip", StringComparison.OrdinalIgnoreCase)) return "";
                return game.Replace('_', ' ').Trim();
            }
            return "";
        }

        /// <summary>Runs ffmpeg and returns its stderr text (used for metadata probing). Tracked + best-effort.</summary>
        private string RunFFmpegCaptureStderr(string arguments, int timeoutMs)
        {
            var psi = new ProcessStartInfo
            {
                FileName = _ffmpegExePath.Replace('\\', '/'),
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            try
            {
                using var p = Process.Start(psi);
                if (p == null) return "";
                ChildProcessTracker.AddProcess(p);
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit(timeoutMs);
                return err;
            }
            catch
            {
                return "";
            }
        }
    }
}
