using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using NAudio.Wave;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;
namespace Wisp.Services
{
    /// <summary>
    /// Optional, fully-offline voice trigger powered by the bundled Vosk speech engine. Unlike the
    /// Windows speech recognizer (which is unreliable and varies machine-to-machine), this ships a
    /// self-contained model and behaves identically everywhere. It listens for one user-defined
    /// phrase via a constrained grammar - light and accurate - and raises <see cref="PhraseDetected"/>.
    /// Nothing is loaded unless the feature is turned on.
    /// </summary>
    public class VoiceTriggerManager : IDisposable
    {
        private Vosk.Model? _model;
        private Vosk.VoskRecognizer? _recognizer;
        private WaveInEvent? _waveIn;
        private string _phrase = "";
        private DateTime _lastFire = DateTime.MinValue;
        private readonly object _lock = new();

        public event Action? PhraseDetected;
        public bool IsRunning { get; private set; }

        public static readonly System.Collections.Generic.Dictionary<string, string> AvailableModels = new(StringComparer.OrdinalIgnoreCase)
        {
            { "en", "https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip" },
            { "pl", "https://alphacephei.com/vosk/models/vosk-model-small-pl-0.22.zip" },
            { "es", "https://alphacephei.com/vosk/models/vosk-model-small-es-0.42.zip" },
            { "fr", "https://alphacephei.com/vosk/models/vosk-model-small-fr-0.22.zip" },
            { "de", "https://alphacephei.com/vosk/models/vosk-model-small-de-0.15.zip" }
        };

        private static string NormalizeLang(string? language)
        {
            if (string.IsNullOrWhiteSpace(language)) return "en";
            string lang = language.Trim().ToLowerInvariant();
            return AvailableModels.ContainsKey(lang) ? lang : "en";
        }
        public static string ModelPath(string language) =>
            Path.Combine(AppContext.BaseDirectory, "vosk-model-" + NormalizeLang(language));

        /// <summary>Whether the bundled model for the given language is present (ships next to the exe).</summary>
        public static bool IsModelAvailable(string language)
        {
            string path = ModelPath(language);
            return Directory.Exists(path) && File.Exists(Path.Combine(path, "am", "final.mdl"));
        }

        public static async Task DownloadModelAsync(string language, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            string lang = NormalizeLang(language);
            if (!AvailableModels.TryGetValue(lang, out string? url))
                throw new Exception($"Unsupported language: {lang}");

            string targetDir = ModelPath(lang);
            if (IsModelAvailable(lang)) return; // Already downloaded

            string zipPath = Path.Combine(AppContext.BaseDirectory, $"vosk-model-{lang}.zip");

            using var client = new HttpClient();
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            long? totalBytes = response.Content.Headers.ContentLength;

            using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
            using (var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[8192];
                long totalRead = 0;
                int bytesRead;

                while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                    totalRead += bytesRead;
                    if (totalBytes.HasValue && progress != null)
                    {
                        progress.Report((double)totalRead / totalBytes.Value);
                    }
                }
            }

            progress?.Report(1.0);

            // Extract
            string extractTemp = Path.Combine(AppContext.BaseDirectory, $"temp-vosk-{lang}");
            if (Directory.Exists(extractTemp)) Directory.Delete(extractTemp, true);
            ZipFile.ExtractToDirectory(zipPath, extractTemp);

            // The zip usually contains a single folder (e.g., "vosk-model-small-en-us-0.15").
            // We need to move its contents to our targetDir ("vosk-model-en").
            string[] subDirs = Directory.GetDirectories(extractTemp);
            string sourceDir = subDirs.Length == 1 ? subDirs[0] : extractTemp;

            if (Directory.Exists(targetDir)) Directory.Delete(targetDir, true);
            Directory.Move(sourceDir, targetDir);

            // Cleanup
            if (Directory.Exists(extractTemp)) Directory.Delete(extractTemp, true);
            if (File.Exists(zipPath)) File.Delete(zipPath);
        }

        public bool Start(string phrase, string language)
        {
            Stop();
            if (string.IsNullOrWhiteSpace(phrase)) return false;

            string modelPath = ModelPath(language);
            if (!IsModelAvailable(language))
            {
                Logger.Error($"Voice trigger: bundled model not found at '{modelPath}'.");
                return false;
            }

            _phrase = phrase.Trim().ToLowerInvariant();

            // Loading the model takes ~1s, so do it (and open the mic) off the calling thread.
            var thread = new Thread(() => InitAndStart(_phrase, modelPath)) { IsBackground = true, Name = "VoiceTrigger" };
            thread.Start();
            return true;
        }

        private void InitAndStart(string phrase, string modelPath)
        {
            try
            {
                Vosk.Vosk.SetLogLevel(-1); // silence Vosk's stdout chatter

                lock (_lock)
                {
                    _model = new Vosk.Model(modelPath);

                    // Constrain recognition to just the phrase (+ "unknown"): fast and accurate.
                    string grammar = JsonSerializer.Serialize(new[] { phrase, "[unk]" });
                    _recognizer = new Vosk.VoskRecognizer(_model, 16000.0f, grammar);

                    _waveIn = new WaveInEvent
                    {
                        WaveFormat = new WaveFormat(16000, 16, 1),
                        BufferMilliseconds = 100
                    };
                    _waveIn.DataAvailable += OnData;
                    _waveIn.StartRecording();
                    IsRunning = true;
                }

                Logger.Info($"Voice trigger (Vosk) listening for '{phrase}'.");
            }
            catch (Exception ex)
            {
                Logger.Error("Voice trigger failed to start (Vosk init or microphone).", ex);
                Stop();
            }
        }

        private void OnData(object? sender, WaveInEventArgs e)
        {
            Vosk.VoskRecognizer? rec;
            lock (_lock) { rec = _recognizer; }
            if (rec == null) return;

            try
            {
                if (rec.AcceptWaveform(e.Buffer, e.BytesRecorded) && MatchesPhrase(rec.Result()))
                {
                    // Debounce so one utterance doesn't fire repeatedly.
                    if ((DateTime.UtcNow - _lastFire).TotalSeconds < 2) return;
                    _lastFire = DateTime.UtcNow;

                    Logger.Info("Voice trigger phrase recognized.");
                    try { PhraseDetected?.Invoke(); }
                    catch (Exception ex) { Logger.Error("Voice trigger handler threw.", ex); }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Voice trigger recognition error.", ex);
            }
        }

        private bool MatchesPhrase(string resultJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(resultJson);
                if (doc.RootElement.TryGetProperty("text", out var t))
                {
                    string text = (t.GetString() ?? "").Trim().ToLowerInvariant();
                    return !string.IsNullOrEmpty(text) && (text == _phrase || text.Contains(_phrase));
                }
            }
            catch { }
            return false;
        }

        public void Stop()
        {
            lock (_lock)
            {
                IsRunning = false;

                if (_waveIn != null)
                {
                    try { _waveIn.DataAvailable -= OnData; } catch { }
                    try { _waveIn.StopRecording(); } catch { }
                    try { _waveIn.Dispose(); } catch { }
                    _waveIn = null;
                }
                if (_recognizer != null)
                {
                    try { _recognizer.Dispose(); } catch { }
                    _recognizer = null;
                }
                if (_model != null)
                {
                    try { _model.Dispose(); } catch { }
                    _model = null;
                }
            }
        }

        public void Dispose() => Stop();
    }
}
