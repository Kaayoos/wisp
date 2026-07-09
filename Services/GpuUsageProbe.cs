using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Wisp.Services
{
    /// <summary>
    /// Reads per-process GPU engine utilization from the Windows "GPU Engine" performance counters - the
    /// same data Task Manager shows. Utilization is a precision timer, so we keep the previous raw sample
    /// per instance and let <see cref="CounterSampleCalculator"/> cook the value over the interval; this is
    /// also naturally robust to instances appearing/disappearing as processes start and stop.
    ///
    /// The signal that matters for game detection is the *3D* engine: games hammer it, whereas fullscreen
    /// video (YouTube/Netflix) drives the *VideoDecode* engine instead. So a high 3D reading is a strong
    /// "this is a game" vote that a fullscreen check alone can't give.
    /// </summary>
    public class GpuUsageProbe
    {
        private const string CategoryName = "GPU Engine";
        private const string CounterName = "Utilization Percentage";

        private PerformanceCounterCategory? _category;
        private bool _available = true;

        // instanceName -> previous sample, used to cook a utilization% over the gap to the next Sample().
        private readonly Dictionary<string, CounterSample> _prev = new();

        // pid -> summed utilization% from the most recent Sample(), split by engine type.
        private Dictionary<uint, double> _3d = new();
        private Dictionary<uint, double> _video = new();
        private readonly object _lock = new();

        /// <summary>
        /// True until a read throws (e.g. the GPU Engine counter set isn't present on this machine). Once
        /// false, the detector transparently falls back to fullscreen-only detection.
        /// </summary>
        public bool Available => _available;

        /// <summary>
        /// Takes a fresh snapshot and recomputes per-PID utilization from the delta since the last call.
        /// Call once per detection tick - it's cheap: one ReadCategory() plus a little string parsing.
        /// </summary>
        public void Sample()
        {
            if (!_available) return;
            try
            {
                _category ??= new PerformanceCounterCategory(CategoryName);
                InstanceDataCollectionCollection data = _category.ReadCategory();
                InstanceDataCollection? util = data[CounterName];
                if (util == null) return;

                var new3d = new Dictionary<uint, double>();
                var newVideo = new Dictionary<uint, double>();
                var nextPrev = new Dictionary<string, CounterSample>(util.Keys.Count);

                foreach (string instanceName in util.Keys)
                {
                    CounterSample sample = util[instanceName].Sample;
                    nextPrev[instanceName] = sample;

                    // Only the engine types we care about, and only once we have a previous sample to
                    // compute the rate against (a brand-new instance contributes nothing this tick).
                    Dictionary<uint, double>? bucket =
                        instanceName.EndsWith("engtype_3D", StringComparison.Ordinal) ? new3d :
                        instanceName.EndsWith("engtype_VideoDecode", StringComparison.Ordinal) ? newVideo : null;
                    if (bucket == null) continue;
                    if (!_prev.TryGetValue(instanceName, out CounterSample prev)) continue;
                    if (!TryParsePid(instanceName, out uint pid)) continue;

                    double pct = CounterSampleCalculator.ComputeCounterValue(prev, sample);
                    if (double.IsNaN(pct) || pct < 0) pct = 0;
                    bucket[pid] = bucket.TryGetValue(pid, out double cur) ? cur + pct : pct;
                }

                _prev.Clear();
                foreach (var kv in nextPrev) _prev[kv.Key] = kv.Value;

                lock (_lock)
                {
                    _3d = new3d;
                    _video = newVideo;
                }
            }
            catch (Exception ex)
            {
                _available = false;
                Logger.Warn($"GPU Engine counters unavailable; auto-detect falls back to fullscreen-only. {ex.Message}");
            }
        }

        /// <summary>Summed 3D-engine utilization (%) for the PID, from the most recent <see cref="Sample"/>.</summary>
        public double Get3DUtilizationForPid(uint pid)
        {
            lock (_lock) { return _3d.TryGetValue(pid, out double v) ? v : 0; }
        }

        /// <summary>Summed video-decode-engine utilization (%) for the PID, from the most recent <see cref="Sample"/>.</summary>
        public double GetVideoDecodeUtilizationForPid(uint pid)
        {
            lock (_lock) { return _video.TryGetValue(pid, out double v) ? v : 0; }
        }

        /// <summary>Extracts the PID from an instance name like "pid_1234_luid_0x..._phys_0_eng_0_engtype_3D".</summary>
        private static bool TryParsePid(string instanceName, out uint pid)
        {
            pid = 0;
            const string marker = "pid_";
            int start = instanceName.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return false;
            start += marker.Length;
            int end = instanceName.IndexOf('_', start);
            if (end < 0) return false;
            return uint.TryParse(instanceName.AsSpan(start, end - start), out pid);
        }
    }
}
