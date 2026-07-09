using System.Text.Json.Serialization;

namespace Wisp.Plugins
{
    /// <summary>
    /// The <c>plugin.json</c> that sits next to a plugin's DLL. Wisp reads this to list the plugin in
    /// the Plugins tab WITHOUT loading its code - so a disabled plugin's assembly is never executed.
    ///
    /// Example:
    /// <code>
    /// {
    ///   "id": "com.acme.webcam-overlay",
    ///   "name": "Webcam Overlay",
    ///   "version": "1.0.0",
    ///   "author": "Acme",
    ///   "description": "Shows a webcam overlay while recording.",
    ///   "entryAssembly": "WispWebcamOverlay.dll",
    ///   "minApiVersion": 1
    /// }
    /// </code>
    /// </summary>
    public sealed class PluginManifest
    {
        /// <summary>Stable unique id; must match <see cref="IWispPlugin.Id"/>.</summary>
        [JsonPropertyName("id")] public string Id { get; set; } = "";

        /// <summary>Display name.</summary>
        [JsonPropertyName("name")] public string Name { get; set; } = "";

        /// <summary>Display version.</summary>
        [JsonPropertyName("version")] public string Version { get; set; } = "1.0.0";

        /// <summary>Author / vendor.</summary>
        [JsonPropertyName("author")] public string Author { get; set; } = "";

        /// <summary>One-line description.</summary>
        [JsonPropertyName("description")] public string Description { get; set; } = "";

        /// <summary>
        /// File name of the DLL to load (relative to the plugin folder). If omitted, Wisp scans every
        /// .dll in the folder for an <see cref="IWispPlugin"/> implementation.
        /// </summary>
        [JsonPropertyName("entryAssembly")] public string EntryAssembly { get; set; } = "";

        /// <summary>
        /// Minimum host API version this plugin needs (<see cref="HostInfo.ApiVersion"/>). Wisp skips
        /// loading a plugin that asks for a newer API than it provides. 0 = no requirement.
        /// </summary>
        [JsonPropertyName("minApiVersion")] public int MinApiVersion { get; set; }
    }
}
