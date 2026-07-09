using System;

namespace Wisp.Plugins.Player
{
    /// <summary>Raised when a clip opens in the player. Carries the clip so you can load its saved markers.</summary>
    public sealed class PlayerClipEventArgs : EventArgs
    {
        /// <summary>The clip now open in the player.</summary>
        public ClipInfo Clip { get; }
        public PlayerClipEventArgs(ClipInfo clip) => Clip = clip;
    }
}
