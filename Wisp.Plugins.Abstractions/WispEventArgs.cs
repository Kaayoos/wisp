using System;

namespace Wisp.Plugins
{
    /// <summary>Raised after a clip has been saved to the library and shown to the user.</summary>
    public sealed class ClipSavedEventArgs : EventArgs
    {
        /// <summary>The saved clip.</summary>
        public ClipInfo Clip { get; }
        public ClipSavedEventArgs(ClipInfo clip) => Clip = clip;
    }

    /// <summary>
    /// Raised when a clip is removed from the library. Most often a normal delete, but ALSO fired
    /// for the short first-tap clip that a chained capture supersedes - see PLUGINS.md. An uploader
    /// can use this to cancel/dedupe an in-flight upload of a clip that was just replaced.
    /// </summary>
    public sealed class ClipRemovedEventArgs : EventArgs
    {
        /// <summary>The clip that was removed.</summary>
        public ClipInfo Clip { get; }

        /// <summary>True when this removal is because a longer chained clip replaced it.</summary>
        public bool Superseded { get; }

        public ClipRemovedEventArgs(ClipInfo clip, bool superseded)
        {
            Clip = clip;
            Superseded = superseded;
        }
    }

    /// <summary>Raised when the rolling buffer starts or stops.</summary>
    public sealed class RecordingEventArgs : EventArgs
    {
        /// <summary>Recorder state at the time of the event.</summary>
        public RecordingInfo Recording { get; }
        public RecordingEventArgs(RecordingInfo recording) => Recording = recording;
    }

    /// <summary>Raised when auto-detection starts/stops recording for a game.</summary>
    public sealed class GameEventArgs : EventArgs
    {
        /// <summary>The detected game (may be empty for stop events).</summary>
        public GameInfo Game { get; }
        public GameEventArgs(GameInfo game) => Game = game;
    }

    /// <summary>Raised when the user triggers the capture hotkey (or voice phrase).</summary>
    public sealed class HotkeyEventArgs : EventArgs
    {
        /// <summary>The game/app in focus when the hotkey fired.</summary>
        public GameInfo Game { get; }
        public HotkeyEventArgs(GameInfo game) => Game = game;
    }
}
