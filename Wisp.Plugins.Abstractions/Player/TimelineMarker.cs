using System;

namespace Wisp.Plugins.Player
{
    /// <summary>
    /// A marker your plugin paints onto the player's timeline (the waveform/scrubber strip), at a given
    /// clip-relative time. Markers are how a plugin annotates "something happens here" - a bookmark, a
    /// chapter, a highlight, a kill, a coaching note. Left-clicking a marker seeks the player to it (Wisp
    /// handles that for you); set <see cref="OnRightClick"/> if you want a secondary action like "remove".
    ///
    /// Markers are transient: they belong to the clip that's currently open. Set them (usually from
    /// <see cref="IWispPlayer.ClipOpened"/>) and they show until the clip changes. Persistence is yours -
    /// save them keyed by clip id via <see cref="IPluginStorage"/> and re-apply on the next open.
    /// </summary>
    public sealed class TimelineMarker
    {
        /// <param name="positionSeconds">Clip-relative time, in seconds, where the marker sits.</param>
        public TimelineMarker(double positionSeconds) => PositionSeconds = positionSeconds;

        /// <summary>Clip-relative position of the marker, in seconds.</summary>
        public double PositionSeconds { get; }

        /// <summary>Short text shown in the marker flag (e.g. "1", "★", "A"). Keep it to a few chars; null = a plain pin.</summary>
        public string? Label { get; init; }

        /// <summary>Tooltip shown on hover (e.g. "Triple kill - 1:23"). null for none.</summary>
        public string? Tooltip { get; init; }

        /// <summary>Marker colour as hex ("#FF4DD2"). null follows the user's accent.</summary>
        public string? ColorHex { get; init; }

        /// <summary>
        /// Optional secondary action, invoked on the UI thread when the marker is right-clicked - handy for
        /// "remove this marker". Left-click always seeks the player to the marker. null = no right-click action.
        /// </summary>
        public Action? OnRightClick { get; init; }
    }
}
