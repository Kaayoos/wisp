using System;
using System.IO;
using System.Media;
using System.Threading.Tasks;
using Wisp.Models;

namespace Wisp.Services
{
    public static class SoundManager
    {
        public static void PlaySuccessSound(AppSettings settings)
        {
            if (!settings.CaptureSoundEnabled) return;
            Task.Run(() =>
            {
                try
                {
                    using (var ms = GenerateChimeWav(settings.CaptureSoundVolume))
                    {
                        using (var player = new SoundPlayer(ms))
                        {
                            player.PlaySync();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("Failed to play success sound", ex);
                }
            });
        }

        public static void PlayFailureSound(AppSettings settings)
        {
            if (!settings.CaptureSoundEnabled) return;
            Task.Run(() =>
            {
                try
                {
                    using (var ms = GenerateErrorWav(settings.CaptureSoundVolume))
                    {
                        using (var player = new SoundPlayer(ms))
                        {
                            player.PlaySync();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("Failed to play failure sound", ex);
                }
            });
        }

        /// <summary>
        /// A short rising "tick" played each time a clip-chain press extends the current clip. Deliberately
        /// distinct from the success chime (shorter, brighter, ascending) so a rapid chain feels like a
        /// ratchet climbing - ti-ti-ti - rather than repeating the full save chime over and over.
        /// </summary>
        public static void PlayChainTickSound(AppSettings settings)
        {
            if (!settings.CaptureSoundEnabled) return;
            Task.Run(() =>
            {
                try
                {
                    using (var ms = GenerateChainTickWav(settings.CaptureSoundVolume))
                    using (var player = new SoundPlayer(ms))
                    {
                        player.PlaySync();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("Failed to play chain tick sound", ex);
                }
            });
        }

        private static MemoryStream GenerateChainTickWav(int volumePct)
        {
            // Bright ascending blip: a quick sweep from G5 (~784 Hz) up to D6 (~1175 Hz) over 0.13s.
            int sampleRate = 44100;
            double volume = Math.Clamp(volumePct / 100.0, 0.0, 1.0);

            int totalSamples = (int)(sampleRate * 0.13);
            short[] samples = new short[totalSamples];
            double twoPi = 2.0 * Math.PI;

            for (int i = 0; i < totalSamples; i++)
            {
                double t = (double)i / sampleRate;
                double progress = t / 0.13;                       // 0..1 across the blip
                double freq = 784.0 + (1174.7 - 784.0) * progress; // glide upward
                // Fast attack, quick decay so taps stay crisp and don't smear together.
                double env = Math.Min(1.0, progress / 0.08) * Math.Exp(-6.0 * progress);
                double amp = 0.5 * Math.Sin(twoPi * freq * t) * env;
                samples[i] = (short)Math.Clamp(amp * 32767.0 * volume, -32768, 32767);
            }

            return CreateWavStream(samples, sampleRate);
        }

        private static MemoryStream GenerateChimeWav(int volumePct)
        {
            // Success chime: C6 (1046.5 Hz) and E6 (1318.5 Hz) cascading chime
            int sampleRate = 44100;
            double volume = Math.Clamp(volumePct / 100.0, 0.0, 1.0);
            
            // Total duration: 0.5 seconds
            int totalSamples = (int)(sampleRate * 0.5);
            short[] samples = new short[totalSamples];

            double twoPi = 2.0 * Math.PI;

            for (int i = 0; i < totalSamples; i++)
            {
                double t = (double)i / sampleRate;
                double amp = 0.0;

                // Tone 1: C6 (1046.5 Hz) - plays from t = 0 to 0.4s with decay
                if (t >= 0 && t < 0.4)
                {
                    double decay = Math.Exp(-5.0 * t);
                    amp += 0.4 * Math.Sin(twoPi * 1046.5 * t) * decay;
                }

                // Tone 2: E6 (1318.5 Hz) - starts at t = 0.08s, plays to 0.5s with decay
                if (t >= 0.08 && t < 0.5)
                {
                    double t2 = t - 0.08;
                    double decay = Math.Exp(-6.0 * t2);
                    amp += 0.5 * Math.Sin(twoPi * 1318.51 * t2) * decay;
                }

                // Normalize and apply volume settings
                double val = amp * 32767.0 * volume;
                samples[i] = (short)Math.Clamp(val, -32768, 32767);
            }

            return CreateWavStream(samples, sampleRate);
        }

        private static MemoryStream GenerateErrorWav(int volumePct)
        {
            // Error tone: 180 Hz buzz double beep
            int sampleRate = 44100;
            double volume = Math.Clamp(volumePct / 100.0, 0.0, 1.0);
            
            // Total duration: 0.4 seconds
            int totalSamples = (int)(sampleRate * 0.4);
            short[] samples = new short[totalSamples];

            double twoPi = 2.0 * Math.PI;

            for (int i = 0; i < totalSamples; i++)
            {
                double t = (double)i / sampleRate;
                double amp = 0.0;

                // Beep 1: 0.0 to 0.15s
                if (t >= 0 && t < 0.15)
                {
                    amp = Math.Sin(twoPi * 180.0 * t) + 0.3 * Math.Sin(twoPi * 360.0 * t); // fundamental + harmonic
                    amp *= 0.5; // Scale to prevent clipping
                }
                // Beep 2: 0.20 to 0.35s
                else if (t >= 0.20 && t < 0.35)
                {
                    double t2 = t - 0.20;
                    amp = Math.Sin(twoPi * 180.0 * t2) + 0.3 * Math.Sin(twoPi * 360.0 * t2);
                    amp *= 0.5;
                }

                // Normalize and apply volume
                double val = amp * 32767.0 * volume;
                samples[i] = (short)Math.Clamp(val, -32768, 32767);
            }

            return CreateWavStream(samples, sampleRate);
        }

        private static MemoryStream CreateWavStream(short[] samples, int sampleRate)
        {
            var ms = new MemoryStream();
            var bw = new BinaryWriter(ms);

            int byteLength = samples.Length * 2;

            // RIFF header
            bw.Write(new char[] { 'R', 'I', 'F', 'F' });
            bw.Write(36 + byteLength); // Size of the file minus 8
            bw.Write(new char[] { 'W', 'A', 'V', 'E' });

            // fmt subchunk
            bw.Write(new char[] { 'f', 'm', 't', ' ' });
            bw.Write(16); // Subchunk1 size (16 for PCM)
            bw.Write((short)1); // Audio format (1 for PCM)
            bw.Write((short)1); // Number of channels (1 for Mono)
            bw.Write(sampleRate); // Sample rate
            bw.Write(sampleRate * 2); // Byte rate (SampleRate * 1 channel * 2 bytes/sample)
            bw.Write((short)2); // Block align (1 channel * 2 bytes/sample)
            bw.Write((short)16); // Bits per sample (16 bits)

            // data subchunk
            bw.Write(new char[] { 'd', 'a', 't', 'a' });
            bw.Write(byteLength); // Number of bytes in data

            // Write samples
            for (int i = 0; i < samples.Length; i++)
            {
                bw.Write(samples[i]);
            }

            bw.Flush();
            ms.Position = 0;
            return ms;
        }
    }
}
