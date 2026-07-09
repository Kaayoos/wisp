namespace Wisp.Plugins
{
    /// <summary>Snapshot of the recorder's state at the moment an event fired.</summary>
    public sealed record RecordingInfo
    {
        /// <summary>Whether the rolling buffer is currently running.</summary>
        public bool IsRecording { get; init; }

        /// <summary>The game/app in focus, if any (may be empty).</summary>
        public GameInfo Game { get; init; } = new();
    }
}
