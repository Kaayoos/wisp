namespace Wisp.Plugins
{
    /// <summary>
    /// Compile-time information about the plugin SDK a plugin was built against. Lets a plugin
    /// (or the host) reason about compatibility. <see cref="HostInfo.ApiVersion"/> reports the
    /// version the *running host* implements; compare the two if you need to gate features.
    /// </summary>
    public static class WispPluginSdk
    {
        /// <summary>
        /// The plugin API contract version. Bumped when the interfaces in this assembly grow or change.
        /// A host advertises the contract it implements via <see cref="HostInfo.ApiVersion"/>.
        ///
        /// v2 (additive): theming (<see cref="Theming.IWispTheming"/> via <see cref="IWispHost.Theming"/>),
        /// clip actions, native toasts and tray items on <see cref="IUiBridge"/>.
        ///
        /// v3 (additive): the clip player surface (<see cref="Player.IWispPlayer"/> via
        /// <see cref="IWispHost.Player"/>) - control-bar buttons, timeline markers, read/seek playback,
        /// and clip-open/close events. Older plugins still run on a v3 host unchanged; a plugin that
        /// needs the player should set <c>minApiVersion: 3</c> in its plugin.json.
        ///
        /// v4 (additive): generic compositing. Plugins can host their own visual content over the player
        /// video (<see cref="Player.PlayerOverlay"/> via <see cref="Player.IWispPlayer.AddOverlay"/>) and
        /// burn arbitrary image/video layers into an export (<see cref="Export.ExportLayer"/> via
        /// <see cref="IWispPlugin.GetExportLayers"/>). Set <c>minApiVersion: 4</c> if you use these.
        /// </summary>
        public const int ApiVersion = 4;
    }
}
