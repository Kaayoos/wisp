using System;
using Wisp.Plugins;

namespace Wisp.Services.Plugins
{
    /// <summary>
    /// Tracks one discovered plugin: its manifest, install folder, desired/actual state, and - once
    /// activated - its instance, load context and host. Also the single place plugin code is invoked,
    /// via <see cref="Guard"/>, so every call is crash-isolated and attributed in the log.
    /// </summary>
    internal sealed class LoadedPlugin
    {
        /// <summary>Number of guarded failures before the manager auto-disables the plugin.</summary>
        private const int FailureLimit = 3;

        public PluginManifest Manifest { get; }
        public string DirectoryPath { get; }

        /// <summary>The user's desired state (persisted in plugins.json).</summary>
        public bool Enabled { get; set; }

        /// <summary>True once the assembly is loaded and OnEnabled has succeeded.</summary>
        public bool Active { get; set; }

        /// <summary>Human-readable status for the Plugins tab (e.g. "Enabled", an error, "Disabled").</summary>
        public string Status { get; set; } = "";

        public IWispPlugin? Instance { get; set; }
        public PluginHost? Host { get; set; }
        public PluginLoadContext? Context { get; set; }

        private int _failureCount;

        public LoadedPlugin(PluginManifest manifest, string directoryPath)
        {
            Manifest = manifest;
            DirectoryPath = directoryPath;
        }

        public string Id => Manifest.Id;

        /// <summary>True once failures have crossed the auto-disable threshold.</summary>
        public bool TooManyFailures => _failureCount >= FailureLimit;

        /// <summary>
        /// Runs a piece of plugin code with full isolation: a throw is caught, logged with the plugin
        /// id and the <paramref name="action"/> name, recorded toward the auto-disable counter, and
        /// surfaced in <see cref="Status"/>. Returns true on success.
        /// </summary>
        public bool Guard(string action, Action body)
        {
            try
            {
                body();
                return true;
            }
            catch (Exception ex)
            {
                _failureCount++;
                Logger.Error($"[Plugin:{Id}] '{action}' threw (failure {_failureCount}/{FailureLimit}).", ex);
                Status = $"Error in {action}: {ex.Message}";
                return false;
            }
        }
    }
}
