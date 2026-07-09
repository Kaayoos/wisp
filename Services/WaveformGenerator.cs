using System;
using System.IO;
using NAudio.Wave;

namespace Wisp.Services
{
    public static class WaveformGenerator
    {
        /// <summary>
        /// Decodes a media file's audio and reduces it to <paramref name="buckets"/> peak magnitudes
        /// (0..1) for drawing a waveform under the video. Works for any clip (reads the MP4's audio),
        /// so it is available for old clips too. Returns null on failure. Run off the UI thread.
        /// </summary>
        public static float[]? ComputePeaks(string mediaPath, int buckets)
        {
            if (buckets <= 0 || string.IsNullOrEmpty(mediaPath) || !File.Exists(mediaPath)) return null;

            try
            {
                using var reader = new MediaFoundationReader(mediaPath);
                var sp = reader.ToSampleProvider();
                int channels = Math.Max(1, sp.WaveFormat.Channels);
                int rate = sp.WaveFormat.SampleRate;
                long totalFrames = (long)(reader.TotalTime.TotalSeconds * rate);
                if (totalFrames <= 0) return null;

                var peaks = new float[buckets];
                long framesPerBucket = Math.Max(1, totalFrames / buckets);

                var buffer = new float[rate * channels]; // ~1 second
                long frameIndex = 0;
                int read;
                while ((read = sp.Read(buffer, 0, buffer.Length)) > 0)
                {
                    int frames = read / channels;
                    for (int f = 0; f < frames; f++)
                    {
                        float peak = 0f;
                        for (int c = 0; c < channels; c++)
                        {
                            float s = Math.Abs(buffer[f * channels + c]);
                            if (s > peak) peak = s;
                        }
                        int bucket = (int)(frameIndex / framesPerBucket);
                        if (bucket >= buckets) bucket = buckets - 1;
                        if (peak > peaks[bucket]) peaks[bucket] = peak;
                        frameIndex++;
                    }
                }

                // Gently normalize so quiet clips are still visible.
                float max = 0f;
                foreach (var p in peaks) if (p > max) max = p;
                if (max > 0.0001f)
                {
                    float norm = Math.Min(1f / max, 4f);
                    for (int i = 0; i < buckets; i++) peaks[i] = Math.Min(1f, peaks[i] * norm);
                }
                return peaks;
            }
            catch (Exception ex)
            {
                Logger.Error($"WaveformGenerator: failed to compute peaks for '{mediaPath}'.", ex);
                return null;
            }
        }
    }
}
