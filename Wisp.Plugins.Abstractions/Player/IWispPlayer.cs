using System;
using System.Collections.Generic;

namespace Wisp.Plugins.Player
{
    /// <summary>
    /// The clip player surface - everything a plugin needs to extend the built-in video player Wisp opens
    /// when you watch a clip. Reach it via <see cref="IWispHost.Player"/>.
    ///
    /// It lets you:
    ///   • add toolbar <see cref="PlayerButton"/>s to the player's control bar,
    ///   • drop <see cref="TimelineMarker"/>s on the scrubber (and let the user click to jump),
    ///   • read where playback is and <see cref="Seek"/> / <see cref="Play"/> / <see cref="Pause"/> it,
    ///   • react to clips opening (<see cref="ClipOpened"/>) and the player closing (<see cref="Closed"/>).
    ///
    /// Deliberately generic: clip-marking is the obvious use, but the same surface powers A/B loops,
    /// chapter export, frame screenshots, coaching annotations, and more.
    ///
    /// THREADING: unlike <see cref="IWispEvents"/> (which fans out on a background thread), the player's
    /// events fire on the UI thread, and button/marker callbacks run on the UI thread too - because they're
    /// inherently tied to the on-screen player. You can therefore call straight back into this interface
    /// from a handler without marshalling. Keep handlers quick.
    /// </summary>
    public interface IWispPlayer
    {
        /// <summary>True while a clip is open in the player.</summary>
        bool IsOpen { get; }

        /// <summary>The clip currently open, or null when the player is closed.</summary>
        ClipInfo? CurrentClip { get; }

        /// <summary>Current playback position, in seconds (0 when closed).</summary>
        double PositionSeconds { get; }

        /// <summary>Length of the open clip, in seconds (0 until known / when closed).</summary>
        double DurationSeconds { get; }

        /// <summary>True if the clip is playing, false if paused/closed.</summary>
        bool IsPlaying { get; }

        /// <summary>Seek the open clip to <paramref name="seconds"/> (clamped to the clip). No-op if closed.</summary>
        void Seek(double seconds);

        /// <summary>Resume playback. No-op if closed or already playing.</summary>
        void Play();

        /// <summary>Pause playback. No-op if closed or already paused.</summary>
        void Pause();

        /// <summary>
        /// Adds (or replaces, by <see cref="PlayerButton.Id"/>) a button on the player's control bar. Call
        /// in <see cref="IWispPlugin.OnEnabled"/>; the button shows whenever the player is open. Wisp removes
        /// your buttons automatically when the plugin is disabled.
        /// </summary>
        void AddButton(PlayerButton button);

        /// <summary>Removes a player button you added.</summary>
        void RemoveButton(string buttonId);

        /// <summary>
        /// Replaces all of THIS plugin's timeline markers with the given set. Other plugins' markers are
        /// untouched. Typically called from <see cref="ClipOpened"/> with the markers you saved for that clip.
        /// </summary>
        void SetMarkers(IEnumerable<TimelineMarker> markers);

        /// <summary>Removes all of this plugin's timeline markers.</summary>
        void ClearMarkers();

        /// <summary>
        /// Adds (or replaces, by <see cref="PlayerOverlay.Id"/>) a free-floating visual layer over the clip
        /// video - your own WPF content, positioned in normalised coordinates and optionally user-movable.
        /// Typically called from <see cref="ClipOpened"/>. Wisp removes all of a plugin's overlays when the
        /// player closes and when the plugin is disabled; re-add them on the next <see cref="ClipOpened"/>.
        /// Must be called on the UI thread (build the content there too).
        /// </summary>
        void AddOverlay(PlayerOverlay overlay);

        /// <summary>Removes a video overlay you added, by id.</summary>
        void RemoveOverlay(string overlayId);

        /// <summary>Removes all of this plugin's video overlays.</summary>
        void ClearOverlays();

        /// <summary>Raised (on the UI thread) when a clip opens in the player.</summary>
        event EventHandler<PlayerClipEventArgs>? ClipOpened;

        /// <summary>Raised (on the UI thread) when the player closes.</summary>
        event EventHandler? Closed;
    }
}
