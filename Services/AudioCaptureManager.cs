using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using NAudio.Wave.SampleProviders;
using Wisp.Models;

namespace Wisp.Services
{
    /// <summary>
    /// Captures up to three independent audio paths into rolling timestamped ring buffers:
    ///   • System / Game  - desktop audio. When social isolation is active it is captured EXCLUDING
    ///     the social app; otherwise it is the whole-device loopback.
    ///   • Microphone     - the selected capture device.
    ///   • Social app     - Discord/Messenger/etc., captured on its own via process loopback so it can
    ///     be isolated from the system path and shown/mixed separately.
    /// On save, each path can be rendered separately (for the in-player mixer + per-source waveforms)
    /// and/or summed into one baked stereo mix (for the shareable MP4).
    /// </summary>
    public class AudioCaptureManager
    {
        // System path is one of these two at a time: full-device loopback (no social isolation) OR a
        // process-loopback stream that EXCLUDES the social app's process tree.
        private WasapiLoopbackCapture? _loopbackCapture;
        private ProcessLoopbackCapture? _systemExcludeCapture;
        private ProcessLoopbackCapture? _socialCapture;
        private WasapiCapture? _micCapture;

        private readonly List<AudioSampleBlock> _loopbackSamples = new(); // System / Game timeline
        private readonly List<AudioSampleBlock> _micSamples = new();
        private readonly List<AudioSampleBlock> _socialSamples = new();

        // Guards the sample buffers + recording state. Held only briefly by the capture callbacks.
        private readonly object _lock = new();
        // Guards capture-object lifecycle (create/start/stop). Kept SEPARATE from _lock so stopping a
        // process-loopback stream (which joins its thread) can never deadlock against a data callback
        // that is waiting on _lock.
        private readonly object _lifecycleLock = new();

        private volatile bool _isRecording;
        private double _bufferLengthSeconds = 30;

        // While a clip chain is active the save can reach much further into the past than a normal clip
        // (back to the FIRST tap minus the buffer), so the ring must hold more history than usual. The
        // recorder bumps this to the chain's max length when a chain starts and resets it to 0 when the
        // chain finalizes - keeping steady-state memory unchanged but never pruning audio a chain needs.
        private volatile int _chainRetentionSeconds;

        private AppSettings? _settings;          // retained for the social-source monitor
        private uint _currentSocialPid;          // 0 = no social isolation currently active
        private Timer? _socialMonitor;

        private struct AudioSampleBlock
        {
            // UTC time the block was delivered by WASAPI. The samples it carries end at
            // roughly this instant, so block start = Timestamp - (bytes / AverageBytesPerSecond).
            public DateTime Timestamp;
            public byte[] Data;
            public WaveFormat Format;
        }

        public void Start(AppSettings settings)
        {
            lock (_lifecycleLock)
            {
                if (_isRecording) StopInternal();

                Logger.Info($"Starting audio capture manager. SystemAudio: {settings.SystemAudioEnabled}, Mic: {settings.MicrophoneEnabled} (Device: {settings.MicrophoneDevice}), Social: {settings.SocialAudioEnabled}");
                _settings = settings;

                lock (_lock)
                {
                    _isRecording = true;
                    _bufferLengthSeconds = settings.BufferLengthSeconds;
                    _loopbackSamples.Clear();
                    _micSamples.Clear();
                    _socialSamples.Clear();
                }

                StartMicrophone(settings);

                uint socialPid = ResolvePrimarySocialPid(settings);
                ConfigureSystemAndSocial(settings, socialPid);

                // Re-evaluate periodically so a social app that launches (or closes) mid-buffer gets
                // picked up. Only the system+social streams reconfigure; the mic is left alone.
                _socialMonitor = new Timer(_ => ReevaluateSocialSource(), null, 4000, 4000);
            }
        }

        public void Stop()
        {
            lock (_lifecycleLock)
            {
                if (!_isRecording) return;
                Logger.Info("Stopping audio capture manager.");
                StopInternal();
            }
        }

        /// <summary>
        /// Sets how many seconds of audio history to retain while a clip chain is in progress (0 = normal
        /// buffer retention). Bumped when a chain starts so the first tap's pre-roll isn't pruned before
        /// the chain finalizes, then reset to 0. Cheap and lock-free; the new value is picked up by the
        /// next prune in AddBlock.
        /// </summary>
        public void SetChainRetentionSeconds(int seconds)
        {
            _chainRetentionSeconds = Math.Max(0, seconds);
        }

        // ===================== Capture lifecycle (under _lifecycleLock) =====================

        /// <summary>Sets up the system + social streams for the given social PID (0 = no isolation).</summary>
        private void ConfigureSystemAndSocial(AppSettings settings, uint socialPid)
        {
            bool wantSocial = settings.SocialAudioEnabled && socialPid != 0;
            bool wantSystem = settings.SystemAudioEnabled;

            if (wantSocial)
            {
                // Social = ONLY the app's process tree.
                try
                {
                    _socialCapture = new ProcessLoopbackCapture(socialPid, includeTree: true);
                    _socialCapture.DataAvailable += OnSocialDataAvailable;
                    _socialCapture.StartRecording();
                    Logger.Info($"Social audio capture started (pid {socialPid}).");
                }
                catch (Exception ex) { Logger.Error("Failed to start social loopback capture.", ex); }

                // System = everything EXCEPT the social app, so the two don't overlap.
                if (wantSystem)
                {
                    try
                    {
                        _systemExcludeCapture = new ProcessLoopbackCapture(socialPid, includeTree: false);
                        _systemExcludeCapture.DataAvailable += OnSystemExcludeDataAvailable;
                        _systemExcludeCapture.StartRecording();
                        Logger.Info($"System audio capture started (excluding social pid {socialPid}).");
                    }
                    catch (Exception ex) { Logger.Error("Failed to start system (exclude-social) capture.", ex); }
                }
            }
            else if (wantSystem)
            {
                // No social isolation: keep the simple whole-device loopback.
                try
                {
                    _loopbackCapture = new WasapiLoopbackCapture();
                    _loopbackCapture.DataAvailable += OnLoopbackDataAvailable;
                    _loopbackCapture.StartRecording();
                    Logger.Info($"System audio loopback capture started. Format: {_loopbackCapture.WaveFormat}");
                }
                catch (Exception ex) { Logger.Error("Failed to start system loopback capture.", ex); }
            }

            _currentSocialPid = wantSocial ? socialPid : 0;
        }

        private void StartMicrophone(AppSettings settings)
        {
            if (!settings.MicrophoneEnabled || string.IsNullOrEmpty(settings.MicrophoneDevice)) return;
            try
            {
                MMDevice? micDevice = FindMicDevice(settings.MicrophoneDevice);
                if (micDevice != null)
                {
                    _micCapture = new WasapiCapture(micDevice);
                    _micCapture.DataAvailable += OnMicDataAvailable;
                    _micCapture.StartRecording();
                    Logger.Info($"Microphone capture started. Device: {settings.MicrophoneDevice}, Format: {_micCapture.WaveFormat}");
                }
                else
                {
                    Logger.Warn($"Microphone device not found: '{settings.MicrophoneDevice}'");
                }
            }
            catch (Exception ex) { Logger.Error("Failed to start microphone capture.", ex); }
        }

        /// <summary>Stops just the system + social streams (used on reconfigure). Buffers are kept.</summary>
        private void TeardownSystemAndSocial()
        {
            if (_socialCapture != null) { try { _socialCapture.DataAvailable -= OnSocialDataAvailable; _socialCapture.StopRecording(); } catch { } _socialCapture = null; }
            if (_systemExcludeCapture != null) { try { _systemExcludeCapture.DataAvailable -= OnSystemExcludeDataAvailable; _systemExcludeCapture.StopRecording(); } catch { } _systemExcludeCapture = null; }
            if (_loopbackCapture != null) { try { _loopbackCapture.StopRecording(); } catch { } try { _loopbackCapture.Dispose(); } catch { } _loopbackCapture = null; }
        }

        /// <summary>Full stop: monitor + all four sources + buffers. Caller holds _lifecycleLock.</summary>
        private void StopInternal()
        {
            try { _socialMonitor?.Dispose(); } catch { }
            _socialMonitor = null;

            // Make every data callback a no-op BEFORE we join capture threads: the callbacks check
            // _isRecording before taking _lock, so none can be stuck holding/awaiting it during a join.
            lock (_lock) { _isRecording = false; }

            TeardownSystemAndSocial();

            if (_micCapture != null) { try { _micCapture.StopRecording(); } catch { } try { _micCapture.Dispose(); } catch { } _micCapture = null; }

            lock (_lock)
            {
                _loopbackSamples.Clear();
                _micSamples.Clear();
                _socialSamples.Clear();
            }
            _currentSocialPid = 0;
            _settings = null;
        }

        /// <summary>Timer tick: if the primary social app changed, restart the system + social streams.</summary>
        private void ReevaluateSocialSource()
        {
            // Skip this tick if a Start/Stop is mid-flight rather than block the timer thread.
            if (!Monitor.TryEnter(_lifecycleLock)) return;
            try
            {
                if (!_isRecording || _settings == null || !_settings.SocialAudioEnabled) return;
                uint pid = ResolvePrimarySocialPid(_settings);
                if (pid == _currentSocialPid) return;

                Logger.Info($"AudioCaptureManager: social source changed (pid {_currentSocialPid} -> {pid}); reconfiguring system/social capture.");
                TeardownSystemAndSocial();
                ConfigureSystemAndSocial(_settings, pid);
            }
            catch (Exception ex) { Logger.Error("ReevaluateSocialSource failed.", ex); }
            finally { Monitor.Exit(_lifecycleLock); }
        }

        /// <summary>
        /// Finds the PID of the primary running "social" app from the user's configured process names.
        /// Prefers a process that owns a main window (the app's UI process), then the largest one.
        /// Returns 0 if none is running. Matching is case-insensitive prefix ("discord" → DiscordCanary).
        /// </summary>
        private static uint ResolvePrimarySocialPid(AppSettings settings)
        {
            if (!settings.SocialAudioEnabled || settings.SocialAppProcesses == null || settings.SocialAppProcesses.Count == 0)
                return 0;

            Process? best = null;
            try
            {
                foreach (var p in Process.GetProcesses())
                {
                    bool keep = false;
                    try
                    {
                        string name = p.ProcessName;
                        bool match = settings.SocialAppProcesses.Any(s =>
                            !string.IsNullOrWhiteSpace(s) &&
                            name.StartsWith(s, StringComparison.OrdinalIgnoreCase));
                        if (match)
                        {
                            if (best == null) { best = p; keep = true; }
                            else if (IsBetterSocialCandidate(p, best)) { best.Dispose(); best = p; keep = true; }
                        }
                    }
                    catch { /* protected/exited process - ignore */ }
                    finally { if (!keep && !ReferenceEquals(p, best)) { try { p.Dispose(); } catch { } } }
                }

                return best != null ? (uint)best.Id : 0u;
            }
            catch (Exception ex)
            {
                Logger.Warn($"ResolvePrimarySocialPid failed: {ex.Message}");
                return 0u;
            }
            finally { try { best?.Dispose(); } catch { } }
        }

        private static bool IsBetterSocialCandidate(Process candidate, Process current)
        {
            try
            {
                bool candWin = candidate.MainWindowHandle != IntPtr.Zero;
                bool curWin = current.MainWindowHandle != IntPtr.Zero;
                if (candWin != curWin) return candWin;            // window-owning process wins
                return candidate.WorkingSet64 > current.WorkingSet64; // else the heavier one
            }
            catch { return false; }
        }

        private MMDevice? FindMicDevice(string deviceName)
        {
            try
            {
                var enumerator = new MMDeviceEnumerator();
                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                foreach (var device in devices)
                {
                    if (device.FriendlyName == deviceName) return device;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error finding mic device '{deviceName}'", ex);
            }
            return null;
        }

        // ===================== Capture callbacks (under _lock) =====================

        private void OnLoopbackDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (_loopbackCapture != null) AddBlock(_loopbackSamples, e, _loopbackCapture.WaveFormat);
        }

        private void OnSystemExcludeDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (sender is ProcessLoopbackCapture c) AddBlock(_loopbackSamples, e, c.WaveFormat);
        }

        private void OnSocialDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (sender is ProcessLoopbackCapture c) AddBlock(_socialSamples, e, c.WaveFormat);
        }

        private void OnMicDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (_micCapture != null) AddBlock(_micSamples, e, _micCapture.WaveFormat);
        }

        /// <summary>Copies one captured buffer into a source's ring buffer and prunes the old tail.</summary>
        private void AddBlock(List<AudioSampleBlock> target, WaveInEventArgs e, WaveFormat format)
        {
            if (e.BytesRecorded == 0 || !_isRecording) return;

            lock (_lock)
            {
                if (!_isRecording) return;
                var buffer = new byte[e.BytesRecorded];
                Buffer.BlockCopy(e.Buffer, 0, buffer, 0, e.BytesRecorded);

                target.Add(new AudioSampleBlock { Timestamp = DateTime.UtcNow, Data = buffer, Format = format });

                // Retain a little more than the buffer window. The saved video clip ends a couple
                // seconds behind "now" (it skips the still-writing segments), so the save path needs
                // to reach further into the past than the nominal buffer length. While a chain is active
                // (_chainRetentionSeconds > buffer) we keep correspondingly more so the first tap's
                // pre-roll is still around when the chain finally finalizes.
                double retain = Math.Max(_bufferLengthSeconds, _chainRetentionSeconds) + 8;
                DateTime cutoff = DateTime.UtcNow.AddSeconds(-retain);
                target.RemoveAll(s => s.Timestamp < cutoff);
            }
        }

        // ===================== Saving =====================

        /// <summary>
        /// Renders the mixed system + mic + social audio for a precise wall-clock window into a
        /// 48kHz/16-bit stereo WAV. The window is supplied by the recorder as the exact real-world time
        /// span that the saved video covers, so audio and video share one clock and stay in sync. Each
        /// captured block is positioned by its own timestamp, which keeps all sources aligned.
        /// </summary>
        public void SaveAudioClip(string outputPath, DateTime windowStartUtc, DateTime windowEndUtc, float systemGain = 1f, float micGain = 1f, float socialGain = 1f)
        {
            double windowSeconds = (windowEndUtc - windowStartUtc).TotalSeconds;
            if (windowSeconds <= 0)
            {
                Logger.Warn($"SaveAudioClip: non-positive window ({windowSeconds:F2}s); writing minimal silence.");
                windowSeconds = 0.1;
            }

            // Snapshot the qualifying blocks under the lock, then do the heavy resampling/mixing
            // work outside it so we don't stall the capture callbacks.
            List<AudioSampleBlock> loopbackBlocks;
            List<AudioSampleBlock> micBlocks;
            List<AudioSampleBlock> socialBlocks;
            lock (_lock)
            {
                loopbackBlocks = _loopbackSamples.Where(b => BlockOverlapsWindow(b, windowStartUtc, windowEndUtc)).ToList();
                micBlocks = _micSamples.Where(b => BlockOverlapsWindow(b, windowStartUtc, windowEndUtc)).ToList();
                socialBlocks = _socialSamples.Where(b => BlockOverlapsWindow(b, windowStartUtc, windowEndUtc)).ToList();
            }

            Logger.Info($"SaveAudioClip: window {windowSeconds:F2}s -> '{outputPath}' (loopback={loopbackBlocks.Count}, mic={micBlocks.Count}, social={socialBlocks.Count})");

            const int OutRate = 48000;
            const int OutChannels = 2;
            int outFrames = (int)Math.Round(windowSeconds * OutRate);
            if (outFrames <= 0) outFrames = 1;

            // Interleaved stereo accumulation buffer (starts as silence; sources are summed in).
            var mix = new float[outFrames * OutChannels];

            if (loopbackBlocks.Count == 0 && micBlocks.Count == 0 && socialBlocks.Count == 0)
            {
                Logger.Warn("SaveAudioClip: no audio captured in window; outputting silence.");
            }
            else
            {
                RenderSourceOntoTimeline(loopbackBlocks, windowStartUtc, OutRate, mix, outFrames, systemGain);
                RenderSourceOntoTimeline(micBlocks, windowStartUtc, OutRate, mix, outFrames, micGain);
                RenderSourceOntoTimeline(socialBlocks, windowStartUtc, OutRate, mix, outFrames, socialGain);
            }

            WriteWav(mix, outputPath);
            Logger.Info($"SaveAudioClip: wrote {outFrames} frames @ {OutRate}Hz to '{outputPath}'.");
        }

        /// <summary>
        /// Writes the system, microphone and social audio for the given window as up to three SEPARATE
        /// 48kHz/16-bit stereo WAV files (raw, no gain), so the in-app player can mix them live and draw
        /// a waveform per source. Returns which sources actually had audio. Uses the same wall-clock
        /// window as the video for sync.
        /// </summary>
        public (bool hasSystem, bool hasMic, bool hasSocial) SaveSourceClips(string systemPath, string micPath, string socialPath, DateTime windowStartUtc, DateTime windowEndUtc)
        {
            double windowSeconds = (windowEndUtc - windowStartUtc).TotalSeconds;
            if (windowSeconds <= 0) windowSeconds = 0.1;

            List<AudioSampleBlock> loopbackBlocks;
            List<AudioSampleBlock> micBlocks;
            List<AudioSampleBlock> socialBlocks;
            lock (_lock)
            {
                loopbackBlocks = _loopbackSamples.Where(b => BlockOverlapsWindow(b, windowStartUtc, windowEndUtc)).ToList();
                micBlocks = _micSamples.Where(b => BlockOverlapsWindow(b, windowStartUtc, windowEndUtc)).ToList();
                socialBlocks = _socialSamples.Where(b => BlockOverlapsWindow(b, windowStartUtc, windowEndUtc)).ToList();
            }

            bool hasSystem = WriteSourceWav(loopbackBlocks, windowStartUtc, windowSeconds, systemPath);
            bool hasMic = WriteSourceWav(micBlocks, windowStartUtc, windowSeconds, micPath);
            bool hasSocial = WriteSourceWav(socialBlocks, windowStartUtc, windowSeconds, socialPath);
            Logger.Info($"SaveSourceClips: window {windowSeconds:F2}s -> system={hasSystem}, mic={hasMic}, social={hasSocial}");
            return (hasSystem, hasMic, hasSocial);
        }

        private static bool WriteSourceWav(List<AudioSampleBlock> blocks, DateTime windowStartUtc, double windowSeconds, string path)
        {
            if (blocks.Count == 0) return false;

            const int OutRate = 48000;
            const int OutChannels = 2;
            int outFrames = (int)Math.Round(windowSeconds * OutRate);
            if (outFrames <= 0) outFrames = 1;

            var mix = new float[outFrames * OutChannels];
            RenderSourceOntoTimeline(blocks, windowStartUtc, OutRate, mix, outFrames, 1f);
            WriteWav(mix, path);
            return true;
        }

        /// <summary>Writes an interleaved-stereo float buffer as a 48kHz/16-bit PCM WAV.</summary>
        private static void WriteWav(float[] mix, string path)
        {
            var fmt = new WaveFormat(48000, 16, 2);
            using var writer = new WaveFileWriter(path, fmt);
            var bytes = new byte[mix.Length * 2];
            for (int i = 0; i < mix.Length; i++)
            {
                short val = (short)(Math.Clamp(mix[i], -1.0f, 1.0f) * 32767.0f);
                bytes[i * 2] = (byte)(val & 0xFF);
                bytes[i * 2 + 1] = (byte)((val >> 8) & 0xFF);
            }
            writer.Write(bytes, 0, bytes.Length);
        }

        /// <summary>True if any part of the block falls inside [startUtc, endUtc].</summary>
        private static bool BlockOverlapsWindow(AudioSampleBlock block, DateTime startUtc, DateTime endUtc)
        {
            double blockSeconds = block.Format.AverageBytesPerSecond > 0
                ? (double)block.Data.Length / block.Format.AverageBytesPerSecond
                : 0;
            DateTime blockEnd = block.Timestamp;
            DateTime blockStart = block.Timestamp.AddSeconds(-blockSeconds);
            return blockEnd > startUtc && blockStart < endUtc;
        }

        /// <summary>
        /// Converts one capture source to 48kHz stereo float and sums it into <paramref name="mix"/>,
        /// positioning audio by each block's wall-clock timestamp.
        ///
        /// A source's capture format can change mid-window: the system path in particular switches
        /// between whole-device loopback and a process-loopback stream (with a possibly different mix
        /// format) whenever a social app launches or closes, and both feed the same buffer. The old
        /// code locked onto blocks[0]'s format and silently dropped every block that didn't match it,
        /// muting whichever portion of the audio used the other format. Instead we split the blocks
        /// into maximal runs of one format and render each run on its own, anchored to that run's first
        /// block - so a format change just starts a new, correctly placed run with nothing lost.
        /// </summary>
        private static void RenderSourceOntoTimeline(List<AudioSampleBlock> blocks, DateTime windowStartUtc, int outRate, float[] mix, int outFrames, float gain)
        {
            if (blocks.Count == 0 || gain <= 0f) return;

            int i = 0;
            while (i < blocks.Count)
            {
                WaveFormat runFormat = blocks[i].Format;
                int runStart = i;
                long runBytes = 0;
                while (i < blocks.Count && blocks[i].Format.Equals(runFormat))
                {
                    runBytes += blocks[i].Data.Length;
                    i++;
                }
                RenderContiguousRun(blocks, runStart, i, runFormat, runBytes, windowStartUtc, outRate, mix, outFrames, gain);
            }
        }

        /// <summary>
        /// Renders one run of consecutive same-format blocks (indices [start, end)) onto the mix
        /// timeline, anchored at the run's first-block wall-clock start. Runs are disjoint in time, so
        /// several runs simply sum into <paramref name="mix"/> at their own offsets.
        /// </summary>
        private static void RenderContiguousRun(List<AudioSampleBlock> blocks, int start, int end, WaveFormat runFormat, long runBytes, DateTime windowStartUtc, int outRate, float[] mix, int outFrames, float gain)
        {
            if (end <= start || runBytes <= 0) return;

            var provider = new BufferedWaveProvider(runFormat)
            {
                BufferLength = (int)runBytes + runFormat.AverageBytesPerSecond, // headroom
                DiscardOnBufferOverflow = true
            };
            for (int k = start; k < end; k++)
                provider.AddSamples(blocks[k].Data, 0, blocks[k].Data.Length);

            ISampleProvider sample = runFormat.Encoding == WaveFormatEncoding.IeeeFloat
                ? new SampleChannel(provider, false)
                : new WaveToSampleProvider(provider);

            if (sample.WaveFormat.SampleRate != outRate)
                sample = new WdlResamplingSampleProvider(sample, outRate);

            if (sample.WaveFormat.Channels == 1)
                sample = new MonoToStereoSampleProvider(sample);

            // Where this run begins relative to the window start.
            double firstBlockSeconds = runFormat.AverageBytesPerSecond > 0
                ? (double)blocks[start].Data.Length / runFormat.AverageBytesPerSecond
                : 0;
            DateTime firstStart = blocks[start].Timestamp.AddSeconds(-firstBlockSeconds);
            int destFrame = (int)Math.Round((firstStart - windowStartUtc).TotalSeconds * outRate);

            var buffer = new float[outRate * 2]; // 1s of stereo
            int read;
            while ((read = sample.Read(buffer, 0, buffer.Length)) > 0)
            {
                int framesRead = read / 2;
                for (int f = 0; f < framesRead; f++)
                {
                    int outF = destFrame + f;
                    if (outF < 0) continue;
                    if (outF >= outFrames) return; // reached the end of the window
                    mix[outF * 2] += buffer[f * 2] * gain;
                    mix[outF * 2 + 1] += buffer[f * 2 + 1] * gain;
                }
                destFrame += framesRead;
            }
        }
    }
}
