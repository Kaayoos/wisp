using System.Collections.Generic;

namespace Wisp.Plugins
{
    /// <summary>
    /// Read and lightly manage the clip library. Backed by Wisp's SQLite database; calls are safe to
    /// make from a background thread. For "react when a clip is saved", subscribe to
    /// <see cref="IWispEvents.ClipSaved"/> instead of polling.
    /// </summary>
    public interface IClipLibrary
    {
        /// <summary>All clips, newest first.</summary>
        IReadOnlyList<ClipInfo> GetClips();

        /// <summary>A single clip by id, or null if it no longer exists.</summary>
        ClipInfo? GetClip(int id);

        /// <summary>Replaces a clip's comma-separated tags.</summary>
        void SetTags(int id, string tags);

        /// <summary>Stars / unstars a clip.</summary>
        void SetFavorite(int id, bool isFavorite);

        /// <summary>Removes a clip from the library and deletes its files (best-effort).</summary>
        void Delete(int id);

        /// <summary>
        /// Imports an existing .mp4 from disk into the library (generates a thumbnail, probes
        /// duration) and returns the new clip - handy for plugins that produce their own videos.
        /// Returns null if the file is missing/unreadable.
        /// </summary>
        ClipInfo? ImportClip(string filePath, string gameName = "");
    }
}
