using System;

namespace Wisp.Plugins
{
    /// <summary>
    /// The lifecycle event hub. Subscribe in your plugin's <see cref="IWispPlugin.OnEnabled"/> and
    /// unsubscribe in <see cref="IWispPlugin.OnDisabled"/>.
    ///
    /// IMPORTANT: handlers are invoked on a BACKGROUND thread (so a slow handler - e.g. an upload -
    /// can never stall capture or the UI). If you need to touch UI, marshal with
    /// <see cref="IUiBridge.RunOnUiThread"/>. Each plugin has its own isolated hub; a throw in your
    /// handler is caught and logged and won't affect Wisp or other plugins.
    /// </summary>
    public interface IWispEvents
    {
        /// <summary>A clip was saved to the library and shown to the user (the final, user-facing clip).</summary>
        event EventHandler<ClipSavedEventArgs>? ClipSaved;

        /// <summary>A clip was removed (a normal delete, or a first-tap clip superseded by a chain).</summary>
        event EventHandler<ClipRemovedEventArgs>? ClipRemoved;

        /// <summary>The rolling buffer started recording.</summary>
        event EventHandler<RecordingEventArgs>? RecordingStarted;

        /// <summary>The rolling buffer stopped recording.</summary>
        event EventHandler<RecordingEventArgs>? RecordingStopped;

        /// <summary>Auto-detection recognised a game and began recording it.</summary>
        event EventHandler<GameEventArgs>? GameDetected;

        /// <summary>Auto-detection's grace period lapsed and recording stopped.</summary>
        event EventHandler<GameEventArgs>? GameStopped;

        /// <summary>The user pressed the capture hotkey (or spoke the voice phrase).</summary>
        event EventHandler<HotkeyEventArgs>? HotkeyTriggered;
    }
}
