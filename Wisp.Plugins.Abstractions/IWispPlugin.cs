using System.Collections.Generic;
using Wisp.Plugins.Export;
using Wisp.Plugins.Settings;

namespace Wisp.Plugins
{
    /// <summary>
    /// The entry point every Wisp plugin implements. The host discovers the single public type in
    /// your assembly that implements this interface, instantiates it with its parameterless
    /// constructor, and drives it through the lifecycle below.
    ///
    /// Lifecycle (each step is crash-isolated by the host - a throw is logged and disables the
    /// plugin, it never crashes Wisp):
    ///   1. <see cref="OnLoaded"/>  - once, right after construction. Stash the <see cref="IWispHost"/>.
    ///   2. <see cref="OnEnabled"/> - when the plugin becomes active (at startup if enabled, or when
    ///                                the user flips it on). Subscribe to events / spin up work here.
    ///   3. <see cref="OnDisabled"/>- when the user turns the plugin off. Undo what OnEnabled did.
    ///   4. <see cref="OnShutdown"/>- once, when Wisp exits (or the plugin is reloaded). Release everything.
    ///
    /// For convenience, derive from <see cref="WispPluginBase"/> instead of implementing this directly.
    /// </summary>
    public interface IWispPlugin
    {
        /// <summary>
        /// Stable, unique id (e.g. "com.acme.webcam-overlay"). Used for the plugin's data folder,
        /// log prefix, and enabled-state key. Should match the "id" in your plugin.json.
        /// </summary>
        string Id { get; }

        /// <summary>Human-friendly name shown in the Plugins tab.</summary>
        string Name { get; }

        /// <summary>Display version, e.g. "1.0.0".</summary>
        string Version { get; }

        /// <summary>Author / vendor name shown in the Plugins tab.</summary>
        string Author { get; }

        /// <summary>One-line description of what the plugin does.</summary>
        string Description { get; }

        /// <summary>
        /// Called once after construction. The <paramref name="host"/> is your gateway to everything
        /// Wisp exposes (events, clip library, recorder, storage, logging, UI thread). Save it.
        /// Do NOT subscribe to events or start work here - wait for <see cref="OnEnabled"/>.
        /// </summary>
        void OnLoaded(IWispHost host);

        /// <summary>Plugin is now active. Subscribe to <see cref="IWispHost.Events"/> and start work.</summary>
        void OnEnabled();

        /// <summary>Plugin is being turned off. Unsubscribe and stop/hide anything you started.</summary>
        void OnDisabled();

        /// <summary>Final teardown (app exit / reload). Dispose every resource, thread and window.</summary>
        void OnShutdown();

        /// <summary>
        /// Optional: Returns a list of settings fields to be rendered in Wisp's plugin UI.
        /// Return null or an empty list if your plugin has no configurable settings.
        /// </summary>
        IReadOnlyList<PluginSettingField>? GetSettings();

        /// <summary>
        /// Called when the user clicks "Save" in the Wisp-generated settings dialog.
        /// The dictionary contains the updated values keyed by field Key.
        /// </summary>
        void OnSettingsSaved(IReadOnlyDictionary<string, object> newValues);

        /// <summary>
        /// Optional: contribute visual layers to <em>burn into</em> a clip when the user exports it (the host
        /// composites them over the video with ffmpeg). Called on export for the clip being exported; return
        /// null or empty to add nothing. Hand back the same normalised rectangle the user positioned in the
        /// player (see <see cref="Player.PlayerOverlay"/>) so the export matches the preview.
        /// </summary>
        IReadOnlyList<ExportLayer>? GetExportLayers(ClipInfo clip);
    }
}
