using System;
using System.Runtime.InteropServices;

namespace Wisp.Services
{
    /// <summary>
    /// Reclaims managed memory and hands the process working set back to the OS. Wisp spends most of its
    /// life in the tray, where the GC almost never runs a Gen-2 collection (nothing allocates enough to
    /// trigger one), so collectable garbage and committed-but-unused heap accumulate and inflate the number
    /// Task Manager shows. Calling this pulls that footprint back down without the user having to open and
    /// close the window - it's just the reclaim that the window-hide path already does, on a timer.
    ///
    /// Two variants because the buffer is usually *running* while Wisp is backgrounded (that's the whole
    /// point of an instant-replay recorder), and the footprint grows during that time too:
    /// <list type="bullet">
    /// <item><see cref="TrimIdle"/> - fuller, blocking collection; only safe when NOT recording.</item>
    /// <item><see cref="TrimWhileBusy"/> - non-blocking; safe to run mid-recording.</item>
    /// </list>
    /// See <c>MainWindow.TrimMemory</c> for the heaviest, user-driven variant (LOH compaction) on hide.
    /// </summary>
    public static class MemoryTrimmer
    {
        // GetCurrentProcess returns a pseudo-handle (-1) that must NOT be closed.
        [DllImport("kernel32.dll", EntryPoint = "GetCurrentProcess")]
        private static extern IntPtr GetCurrentProcessHandle();

        [DllImport("psapi.dll")]
        private static extern int EmptyWorkingSet(IntPtr hProcess);

        /// <summary>
        /// Lighter idle trim: a single full blocking collection (no LOH compaction - that's expensive and,
        /// once GPU sampling is gated, the idle workload barely touches the large-object heap) followed by a
        /// working-set trim. The blocking collect briefly suspends managed threads, so this is for the
        /// NOT-recording case only; callers run it while the window is hidden, so the pause is invisible.
        /// </summary>
        public static void TrimIdle()
        {
            try
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
                EmptyWorkingSet(GetCurrentProcessHandle());
            }
            catch { }
        }

        /// <summary>
        /// Recording-safe trim. Unlike <see cref="TrimIdle"/> this never does a stop-the-world collection:
        /// it requests a <em>background</em> (non-blocking) Gen-2 collection, so the managed audio-capture
        /// thread is not suspended and clips can't pick up a gap. The working-set trim that follows doesn't
        /// suspend anything either - pages are soft-faulted back on next touch (microseconds), and the screen
        /// capture (ffmpeg) and the game run in separate processes, so neither is affected. This is what lets
        /// the background footprint stay low even while the buffer is actively running.
        /// </summary>
        public static void TrimWhileBusy()
        {
            try
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false);
                EmptyWorkingSet(GetCurrentProcessHandle());
            }
            catch { }
        }
    }
}
