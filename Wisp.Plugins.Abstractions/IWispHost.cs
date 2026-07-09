using Wisp.Plugins.Player;
using Wisp.Plugins.Theming;

namespace Wisp.Plugins
{
    /// <summary>
    /// Everything Wisp exposes to a plugin - the whole API surface, handed to you once in
    /// <see cref="IWispPlugin.OnLoaded"/>. Hold onto it. Each plugin receives its own instance scoped
    /// to its id (so logging, storage and events are isolated per plugin).
    /// </summary>
    public interface IWispHost
    {
        /// <summary>Write to Wisp's shared log (auto-prefixed with your plugin id).</summary>
        IWispLog Log { get; }

        /// <summary>Subscribe to lifecycle events (clip saved, recording start/stop, game detected …).</summary>
        IWispEvents Events { get; }

        /// <summary>Read and lightly manage the clip library.</summary>
        IClipLibrary Clips { get; }

        /// <summary>Observe and drive the recorder.</summary>
        IRecorderControl Recorder { get; }

        /// <summary>Per-plugin settings + private data folder.</summary>
        IPluginStorage Storage { get; }

        /// <summary>Marshal work onto the UI thread (required for any WPF UI you create).</summary>
        IUiBridge Ui { get; }

        /// <summary>Recolour the accent, ship full themes, or take over the look entirely.</summary>
        IWispTheming Theming { get; }

        /// <summary>Extend the clip player: control-bar buttons, timeline markers, read/seek playback.</summary>
        IWispPlayer Player { get; }

        /// <summary>Read-only facts about the running app (version, paths, output folder).</summary>
        HostInfo Info { get; }
    }
}
