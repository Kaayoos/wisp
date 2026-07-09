using System;

namespace Wisp.Services.KillDetection
{
    /// <summary>
    /// A per-game kill detector. Implementations watch ONE game through an anti-cheat-safe channel
    /// (official local API or screen-region sampling - never injection, memory reads, or hooks into
    /// the game) and raise <see cref="KillDetected"/> with the wall-clock UTC instant of each of the
    /// player's own kills. UTC matters: clip assembly anchors its window on segment file times (UTC),
    /// so these timestamps map straight onto clip-relative marker offsets, exactly like chain taps.
    ///
    /// Lifecycle contract: Start/Stop are idempotent, called from the router's background tick, and
    /// Stop must fully quiesce the provider (no timers, sockets, or GDI work left behind). Start must
    /// BASELINE, never replay - kills that happened before Start (or while stopped) must not fire.
    /// </summary>
    public interface IKillProvider : IDisposable
    {
        /// <summary>Process name (no .exe) this provider watches, e.g. "cs2".</summary>
        string ProcessName { get; }

        /// <summary>Display name for logs/UI, e.g. "Counter-Strike 2".</summary>
        string GameName { get; }

        /// <summary>True when detection only works while the game is the foreground window (vision).</summary>
        bool RequiresForeground { get; }

        bool IsRunning { get; }

        void Start();
        void Stop();

        /// <summary>Raised on a background thread with the UTC instant a kill was detected.</summary>
        event Action<DateTime>? KillDetected;
    }
}
