using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using OpenCvSharp;

namespace WispWebcamOverlay
{
    /// <summary>
    /// Headless webcam recorder: captures continuously into a rolling ring of short on-disk segments while
    /// Wisp's buffer runs (nothing is shown live, nothing is kept in RAM), and on demand assembles the last
    /// N seconds into a single clip-matched .mp4. This is what lets the camera appear ONLY inside saved clips.
    ///
    /// Disk is bounded to roughly <c>BufferSeconds</c> of footage; older segments are pruned as new ones roll.
    /// Files are written as MPEG-4 Part 2 (fourcc "mp4v"), which OpenCV writes reliably and both WPF's
    /// MediaElement (preview) and Wisp's bundled ffmpeg (export burn-in) read.
    /// </summary>
    public sealed class CameraRecorder : IDisposable
    {
        private const double SegmentSeconds = 3.0; // short segments → the tail is finalised quickly on assemble
        private const int MinFps = 5, MaxFps = 60;

        private readonly int _cameraIndex;
        private readonly string _bufferDir;
        private readonly int _bufferSeconds;

        private readonly object _sync = new();
        private Thread? _thread;
        private volatile bool _running;

        private VideoCapture? _cap;
        private VideoWriter? _writer;
        private Segment? _current;
        private readonly List<Segment> _segments = new();

        private double _fps = 30;
        private Size _frameSize;

        public bool IsAvailable { get; private set; }

        private sealed class Segment
        {
            public string Path = "";
            public DateTime StartUtc;
            public DateTime EndUtc;   // real wall-clock when the segment was finalised
            public int FrameCount;
            // The segment's TRUE average fps from the real time it spanned - not the writer's declared fps,
            // which the webcam rarely actually delivers. Used to place each frame on the timeline.
            public double RealFps => FrameCount <= 1 || EndUtc <= StartUtc
                ? 30 : FrameCount / (EndUtc - StartUtc).TotalSeconds;
        }

        public CameraRecorder(int cameraIndex, string bufferDir, int bufferSeconds)
        {
            _cameraIndex = cameraIndex;
            _bufferDir = bufferDir;
            _bufferSeconds = Math.Max(10, bufferSeconds);
        }

        public void Start()
        {
            if (_running) return;
            try
            {
                Directory.CreateDirectory(_bufferDir);
                // Wipe any stale segments from a previous session.
                foreach (var f in Directory.EnumerateFiles(_bufferDir, "seg_*.mp4")) TryDelete(f);

                _cap = new VideoCapture(_cameraIndex);
                if (!_cap.IsOpened())
                {
                    _cap.Dispose();
                    _cap = null;
                    IsAvailable = false;
                    return;
                }
                double camFps = _cap.Get(VideoCaptureProperties.Fps);
                _fps = double.IsNaN(camFps) || camFps < MinFps || camFps > MaxFps ? 30 : camFps;
                IsAvailable = true;

                _running = true;
                _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "WispCameraRecorder" };
                _thread.Start();
            }
            catch
            {
                IsAvailable = false;
                _running = false;
                try { _cap?.Dispose(); } catch { }
                _cap = null;
            }
        }

        public void Stop()
        {
            _running = false;
            try { _thread?.Join(2000); } catch { }
            _thread = null;

            lock (_sync)
            {
                try { _writer?.Release(); } catch { }
                _writer?.Dispose();
                _writer = null;
                _current = null;
                foreach (var s in _segments) TryDelete(s.Path);
                _segments.Clear();
            }
            try { _cap?.Release(); } catch { }
            _cap?.Dispose();
            _cap = null;
        }

        private void CaptureLoop()
        {
            using var frame = new Mat();
            var sw = Stopwatch.StartNew();
            double intervalMs = 1000.0 / _fps;

            while (_running)
            {
                long t0 = sw.ElapsedMilliseconds;
                try
                {
                    if (_cap == null || !_cap.Read(frame) || frame.Empty())
                    {
                        Thread.Sleep(15);
                        continue;
                    }

                    lock (_sync)
                    {
                        if (!_running) break;
                        if (_writer == null)
                        {
                            _frameSize = new Size(frame.Width, frame.Height);
                            OpenNewSegment();
                        }
                        _writer!.Write(frame);
                        if (_current != null) _current.FrameCount++;

                        if (_current != null && (DateTime.UtcNow - _current.StartUtc).TotalSeconds >= SegmentSeconds)
                            RollSegment();
                    }
                }
                catch
                {
                    // Swallow capture/encode glitches; the buffer just skips a frame.
                }

                long spent = sw.ElapsedMilliseconds - t0;
                int sleep = (int)Math.Max(1, intervalMs - spent);
                Thread.Sleep(sleep);
            }
        }

        // Closes the current segment, prunes anything older than the buffer window, and starts a fresh one.
        // Caller must hold _sync.
        private void RollSegment()
        {
            FinalizeCurrent();
            PruneOldSegments();
            OpenNewSegment();
        }

        // Caller must hold _sync.
        private void OpenNewSegment()
        {
            string path = Path.Combine(_bufferDir, $"seg_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.mp4");
            var writer = new VideoWriter(path, VideoWriter.FourCC('m', 'p', '4', 'v'), _fps, _frameSize, isColor: true);
            if (!writer.IsOpened())
            {
                writer.Dispose();
                IsAvailable = false;
                return;
            }
            _writer = writer;
            _current = new Segment { Path = path, StartUtc = DateTime.UtcNow, FrameCount = 0 };
        }

        // Caller must hold _sync.
        private void FinalizeCurrent()
        {
            try { _writer?.Release(); } catch { }
            _writer?.Dispose();
            _writer = null;
            if (_current != null && _current.FrameCount > 0)
            {
                _current.EndUtc = DateTime.UtcNow;
                _segments.Add(_current);
            }
            else if (_current != null) TryDelete(_current.Path);
            _current = null;
        }

        // Caller must hold _sync.
        private void PruneOldSegments()
        {
            DateTime cutoff = DateTime.UtcNow.AddSeconds(-_bufferSeconds);
            for (int i = _segments.Count - 1; i >= 0; i--)
            {
                if (_segments[i].EndUtc < cutoff)
                {
                    TryDelete(_segments[i].Path);
                    _segments.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// Assembles the <paramref name="durationSeconds"/> of camera footage ending "now" into a single mp4
        /// at <paramref name="destPath"/>, so it lines up with a just-saved clip of that length. Returns false
        /// if there's no footage (e.g. the camera wasn't available). Safe to call off the UI thread.
        /// </summary>
        public bool AssembleClip(string destPath, double durationSeconds, DateTime endUtc, int syncOffsetMs)
        {
            if (!IsAvailable || durationSeconds <= 0) return false;

            List<Segment> segs;
            lock (_sync)
            {
                // Finalise the in-progress segment so the freshest footage (the clip's climax) is included.
                FinalizeCurrent();
                OpenNewSegment();
                segs = _segments.OrderBy(s => s.StartUtc).ToList();
            }

            // End the window at the clip's actual end (the hotkey moment the caller passes), NOT "now": the
            // save fires a beat later, and using "now" makes the camera run ahead of the game by that gap.
            DateTime tEnd = endUtc.AddMilliseconds(-syncOffsetMs);
            DateTime tStart = tEnd.AddSeconds(-durationSeconds);

            // Pass 1 (no decode): count the frames inside the window so we can choose an output fps that makes
            // the camera file EXACTLY durationSeconds long. Otherwise it plays at the wrong speed (the webcam's
            // real rate ≠ the writer's declared rate) and drifts ahead of / behind the clip.
            int n = 0;
            foreach (var seg in segs)
            {
                if (seg.EndUtc < tStart || seg.StartUtc > tEnd) continue;
                double fps = seg.RealFps;
                for (int i = 0; i < seg.FrameCount; i++)
                {
                    DateTime ft = seg.StartUtc.AddSeconds(i / fps);
                    if (ft < tStart) continue;
                    if (ft > tEnd) break;
                    n++;
                }
            }
            if (n == 0) { TryDelete(destPath); return false; }

            double outFps = Math.Clamp(n / durationSeconds, 1.0, 120.0);

            // Pass 2: decode the same frames and write them at outFps.
            VideoWriter? dest = null;
            int written = 0;
            try
            {
                foreach (var seg in segs)
                {
                    if (seg.EndUtc < tStart || seg.StartUtc > tEnd) continue;
                    double fps = seg.RealFps;
                    using var cap = new VideoCapture(seg.Path);
                    if (!cap.IsOpened()) continue;
                    using var frame = new Mat();
                    int i = 0;
                    while (cap.Read(frame) && !frame.Empty())
                    {
                        DateTime ft = seg.StartUtc.AddSeconds(i / fps);
                        i++;
                        if (ft < tStart) continue;
                        if (ft > tEnd) break;

                        dest ??= OpenDest(destPath, new Size(frame.Width, frame.Height), outFps);
                        if (dest == null) return false;
                        dest.Write(frame);
                        written++;
                    }
                }
            }
            finally
            {
                try { dest?.Release(); } catch { }
                dest?.Dispose();
            }

            if (written == 0) { TryDelete(destPath); return false; }
            return true;
        }

        private static VideoWriter? OpenDest(string path, Size size, double fps)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var w = new VideoWriter(path, VideoWriter.FourCC('m', 'p', '4', 'v'), fps, size, isColor: true);
                if (w.IsOpened()) return w;
                w.Dispose();
            }
            catch { }
            return null;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        public void Dispose() => Stop();
    }
}
