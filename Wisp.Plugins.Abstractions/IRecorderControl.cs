namespace Wisp.Plugins
{
    /// <summary>
    /// Observe and drive the recorder. Mirrors what the tray icon / hotkey can do, so a plugin can
    /// build its own capture triggers (a Stream Deck button, a game-event webhook, etc.).
    /// </summary>
    public interface IRecorderControl
    {
        /// <summary>Whether the rolling buffer is currently running.</summary>
        bool IsRecording { get; }

        /// <summary>The game/app currently in focus (empty for desktop/unknown).</summary>
        GameInfo CurrentGame { get; }

        /// <summary>Starts the rolling buffer (no-op if already recording).</summary>
        void Start();

        /// <summary>Stops the rolling buffer (no-op if not recording).</summary>
        void Stop();

        /// <summary>
        /// Saves a clip right now from the current buffer - exactly as if the user pressed the
        /// capture hotkey (honours clip-chaining). No-op if the buffer isn't running.
        /// </summary>
        void CaptureClipNow();
    }
}
