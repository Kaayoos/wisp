namespace Wisp.Plugins
{
    /// <summary>
    /// Per-plugin persistence. Each plugin gets its own private folder under
    /// <c>%AppData%\Wisp\plugins\&lt;id&gt;\</c>; settings are a single JSON file in it.
    /// Use this for config (server URLs, API keys, preferences) and any working files.
    /// </summary>
    public interface IPluginStorage
    {
        /// <summary>
        /// Absolute path to this plugin's private data folder (created on first access). Put caches,
        /// downloaded models, logs, etc. here.
        /// </summary>
        string DataDirectory { get; }

        /// <summary>
        /// Loads this plugin's settings, deserialized from its JSON store as <typeparamref name="T"/>.
        /// Returns null if nothing has been saved yet (or the JSON can't be read).
        /// </summary>
        T? LoadSettings<T>() where T : class;

        /// <summary>Serializes <paramref name="settings"/> to this plugin's JSON store.</summary>
        void SaveSettings<T>(T settings) where T : class;
    }
}
