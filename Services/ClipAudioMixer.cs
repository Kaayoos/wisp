using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.CoreAudioApi;

namespace Wisp.Services
{
    /// <summary>
    /// Plays a clip's separate system + microphone + social tracks together with independent volume and
    /// mute, for the in-app player's live audio mixer. The video (rendered muted by the WPF MediaElement)
    /// is the master clock; this engine is seeked to follow it. All sources are mixed in one NAudio graph,
    /// so they stay sample-locked to each other.
    /// </summary>
    public class ClipAudioMixer : IDisposable
    {
        private IWavePlayer? _output;
        private MediaFoundationReader? _systemReader;
        private MediaFoundationReader? _micReader;
        private MediaFoundationReader? _socialReader;
        private VolumeSampleProvider? _systemVol;
        private VolumeSampleProvider? _micVol;
        private VolumeSampleProvider? _socialVol;
        private VolumeSampleProvider? _master;

        private float _systemVolume = 1f, _micVolume = 1f, _socialVolume = 1f, _masterVolume = 1f;
        private bool _systemMuted, _micMuted, _socialMuted, _masterMuted;
        private bool _isPlaying;

        public bool HasSystem { get; private set; }
        public bool HasMic { get; private set; }
        public bool HasSocial { get; private set; }
        public bool IsLoaded => _output != null;

        /// <summary>True only while the output device is actively rendering (for the watchdog).</summary>
        public bool IsOutputPlaying => _output != null && _output.PlaybackState == PlaybackState.Playing;

        /// <summary>Current read position of the tracks (leads the audible position by the output buffer).</summary>
        public TimeSpan CurrentTime
        {
            get
            {
                var reader = _systemReader ?? _micReader ?? _socialReader;
                if (reader == null) return TimeSpan.Zero;
                try { return reader.CurrentTime; } catch { return TimeSpan.Zero; }
            }
        }

        /// <summary>Loads whichever of the three track files exist. Returns false if none could load.</summary>
        public bool Load(string? systemPath, string? micPath, string? socialPath)
        {
            Dispose();
            try
            {
                var inputs = new List<ISampleProvider>();

                if (!string.IsNullOrEmpty(systemPath) && File.Exists(systemPath))
                {
                    _systemReader = new MediaFoundationReader(systemPath);
                    _systemVol = new VolumeSampleProvider(new EndlessSampleProvider(Normalize(_systemReader.ToSampleProvider()))) { Volume = _systemVolume };
                    inputs.Add(_systemVol);
                    HasSystem = true;
                }
                if (!string.IsNullOrEmpty(micPath) && File.Exists(micPath))
                {
                    _micReader = new MediaFoundationReader(micPath);
                    _micVol = new VolumeSampleProvider(new EndlessSampleProvider(Normalize(_micReader.ToSampleProvider()))) { Volume = _micVolume };
                    inputs.Add(_micVol);
                    HasMic = true;
                }
                if (!string.IsNullOrEmpty(socialPath) && File.Exists(socialPath))
                {
                    _socialReader = new MediaFoundationReader(socialPath);
                    _socialVol = new VolumeSampleProvider(new EndlessSampleProvider(Normalize(_socialReader.ToSampleProvider()))) { Volume = _socialVolume };
                    inputs.Add(_socialVol);
                    HasSocial = true;
                }

                if (inputs.Count == 0) { Dispose(); return false; }

                // All inputs are 48kHz stereo and never end (EndlessSampleProvider), so the mixer never
                // drops a track and never stops at end-of-stream - we control transport explicitly.
                var mixer = new MixingSampleProvider(inputs);
                _master = new VolumeSampleProvider(mixer) { Volume = _masterVolume };

                _output = new WasapiOut(AudioClientShareMode.Shared, 100);
                _output.Init(_master);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("ClipAudioMixer: failed to load tracks.", ex);
                Dispose();
                return false;
            }
        }

        // Both tracks are authored at 48kHz stereo, but normalize defensively so the mixer always sees
        // identical IEEE-float formats.
        private static ISampleProvider Normalize(ISampleProvider sp)
        {
            if (sp.WaveFormat.SampleRate != 48000)
                sp = new WdlResamplingSampleProvider(sp, 48000);
            if (sp.WaveFormat.Channels == 1)
                sp = new MonoToStereoSampleProvider(sp);
            return sp;
        }

        public void Play() { try { _output?.Play(); _isPlaying = true; } catch { } }
        public void Pause() { try { _output?.Pause(); _isPlaying = false; } catch { } }
        public void Stop() { try { _output?.Stop(); _isPlaying = false; } catch { } }

        /// <summary>
        /// Repositions both tracks. The output is paused around the reposition because
        /// MediaFoundationReader.CurrentTime is not safe to set while the render thread is reading -
        /// doing so was what made audio drop out after a seek.
        /// </summary>
        public void Seek(TimeSpan position)
        {
            bool wasPlaying = _isPlaying;
            try { if (wasPlaying) _output?.Pause(); } catch { }

            SeekReader(_systemReader, position);
            SeekReader(_micReader, position);
            SeekReader(_socialReader, position);

            try { if (wasPlaying) _output?.Play(); } catch { }
        }

        private static void SeekReader(MediaFoundationReader? reader, TimeSpan position)
        {
            if (reader == null) return;
            try
            {
                if (position < TimeSpan.Zero) position = TimeSpan.Zero;
                if (position > reader.TotalTime) position = reader.TotalTime;
                reader.CurrentTime = position;
            }
            catch { }
        }

        public void SetSystemVolume(float v) { _systemVolume = v; Apply(); }
        public void SetMicVolume(float v) { _micVolume = v; Apply(); }
        public void SetSocialVolume(float v) { _socialVolume = v; Apply(); }
        public void SetMasterVolume(float v) { _masterVolume = v; Apply(); }
        public void SetSystemMuted(bool m) { _systemMuted = m; Apply(); }
        public void SetMicMuted(bool m) { _micMuted = m; Apply(); }
        public void SetSocialMuted(bool m) { _socialMuted = m; Apply(); }
        public void SetMasterMuted(bool m) { _masterMuted = m; Apply(); }

        private void Apply()
        {
            if (_systemVol != null) _systemVol.Volume = _systemMuted ? 0f : _systemVolume;
            if (_micVol != null) _micVol.Volume = _micMuted ? 0f : _micVolume;
            if (_socialVol != null) _socialVol.Volume = _socialMuted ? 0f : _socialVolume;
            if (_master != null) _master.Volume = _masterMuted ? 0f : _masterVolume;
        }

        public void Dispose()
        {
            try { _output?.Stop(); } catch { }
            try { _output?.Dispose(); } catch { }
            try { _systemReader?.Dispose(); } catch { }
            try { _micReader?.Dispose(); } catch { }
            try { _socialReader?.Dispose(); } catch { }
            _output = null;
            _systemReader = null;
            _micReader = null;
            _socialReader = null;
            _systemVol = null;
            _micVol = null;
            _socialVol = null;
            _master = null;
            _isPlaying = false;
            HasSystem = false;
            HasMic = false;
            HasSocial = false;
        }

        /// <summary>
        /// Wraps a source so it never reports end-of-stream: once the source runs out it returns silence
        /// instead of 0. This keeps the track in the mixer (MixingSampleProvider drops inputs that return
        /// 0) so that seeking back and looping resume real audio instead of permanent silence.
        /// </summary>
        private sealed class EndlessSampleProvider : ISampleProvider
        {
            private readonly ISampleProvider _source;
            public EndlessSampleProvider(ISampleProvider source) { _source = source; }
            public WaveFormat WaveFormat => _source.WaveFormat;

            public int Read(float[] buffer, int offset, int count)
            {
                int read = _source.Read(buffer, offset, count);
                if (read < count)
                {
                    for (int i = offset + read; i < offset + count; i++) buffer[i] = 0f;
                }
                return count;
            }
        }
    }
}
