namespace Wisp.Plugins
{
    /// <summary>Read-only facts about the running Wisp instance and its environment.</summary>
    public sealed record HostInfo
    {
        /// <summary>Wisp application version, e.g. "1.0".</summary>
        public string AppVersion { get; init; } = "";

        /// <summary>
        /// The plugin-API contract version the running host implements. Compare against
        /// <see cref="WispPluginSdk.ApiVersion"/> if you need to gate features by host capability.
        /// </summary>
        public int ApiVersion { get; init; }

        /// <summary>Folder where finished clips (.mp4) are written.</summary>
        public string ClipOutputFolder { get; init; } = "";

        /// <summary>Wisp's app-data root (<c>%AppData%\Wisp</c>).</summary>
        public string AppDataFolder { get; init; } = "";

        /// <summary>The plugins root (<c>%AppData%\Wisp\plugins</c>).</summary>
        public string PluginsFolder { get; init; } = "";
    }
}
