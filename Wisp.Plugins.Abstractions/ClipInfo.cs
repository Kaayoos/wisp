using System;

namespace Wisp.Plugins
{
    /// <summary>
    /// An immutable, read-only view of a clip in the Wisp library, handed to plugins via events and
    /// <see cref="IClipLibrary"/>. This is a stable projection of Wisp's internal clip model - it
    /// deliberately does NOT expose the app's mutable internals, so the contract survives refactors.
    /// </summary>
    public sealed record ClipInfo
    {
        /// <summary>Library database id. Use with <see cref="IClipLibrary"/> to act on this clip.</summary>
        public int Id { get; init; }

        /// <summary>Absolute path to the shareable .mp4 (baked stereo audio mix).</summary>
        public string FilePath { get; init; } = "";

        /// <summary>Just the file name, e.g. "Valorant_20260619_213045.mp4".</summary>
        public string Filename { get; init; } = "";

        /// <summary>When the clip was created (local time).</summary>
        public DateTime CreatedAt { get; init; }

        /// <summary>Clip length in seconds.</summary>
        public double DurationSeconds { get; init; }

        /// <summary>File size of the .mp4 in bytes.</summary>
        public long FileSizeBytes { get; init; }

        /// <summary>The foreground game/app detected at capture, or "" for desktop/unknown.</summary>
        public string GameName { get; init; } = "";

        /// <summary>Comma-separated user tags ("" if none).</summary>
        public string Tags { get; init; } = "";

        /// <summary>Whether the user starred this clip.</summary>
        public bool IsFavorite { get; init; }

        /// <summary>Absolute path to the gallery thumbnail JPEG ("" if none).</summary>
        public string ThumbnailPath { get; init; } = "";

        /// <summary>Optional per-source audio sidecars (system / mic / social), "" when unavailable.</summary>
        public string SystemTrackPath { get; init; } = "";
        /// <summary><inheritdoc cref="SystemTrackPath"/></summary>
        public string MicTrackPath { get; init; } = "";
        /// <summary><inheritdoc cref="SystemTrackPath"/></summary>
        public string SocialTrackPath { get; init; } = "";

        /// <summary>
        /// CSV of clip-relative offsets (seconds) marking each chained "moment", "" for a plain clip.
        /// Two or more markers means this clip stitches several rapid hotkey taps together.
        /// </summary>
        public string ChainMarkers { get; init; } = "";
    }
}
