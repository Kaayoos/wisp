using System;
using System.Collections.Generic;
using Wisp.Plugins.Export;
using Wisp.Plugins.Settings;

namespace Wisp.Plugins
{
    /// <summary>
    /// Base class for Wisp plugins that provides empty virtuals for lifecycle methods
    /// so you only have to override what you need.
    /// </summary>
    public abstract class WispPluginBase : IWispPlugin
    {
        public abstract string Id { get; }
        public virtual string Name => Id;
        public virtual string Version => "1.0.0";
        public virtual string Author => "Unknown";
        public virtual string Description => "";

        /// <summary>The host gateway, available after <see cref="OnLoaded"/>.</summary>
        protected IWispHost Host { get; private set; } = null!;

        public virtual void OnLoaded(IWispHost host)
        {
            Host = host ?? throw new ArgumentNullException(nameof(host));
        }

        public virtual void OnEnabled() { }
        public virtual void OnDisabled() { }
        public virtual void OnShutdown() { }
        public virtual IReadOnlyList<PluginSettingField>? GetSettings() => null;
        public virtual void OnSettingsSaved(IReadOnlyDictionary<string, object> newValues) { }
        public virtual IReadOnlyList<ExportLayer>? GetExportLayers(ClipInfo clip) => null;
    }
}
