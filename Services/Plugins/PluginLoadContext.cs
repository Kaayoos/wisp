using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace Wisp.Services.Plugins
{
    /// <summary>
    /// Isolated, collectible load context for one plugin. Each plugin's DLL (and any private
    /// dependencies it ships in its own folder) loads here, separate from the app - so two plugins
    /// can use different versions of the same library, and a plugin can be unloaded on disable.
    ///
    /// THE CRITICAL RULE (see PLUGINS.md / the plan): for the host's <c>IWispPlugin</c> to be the same
    /// type the plugin implements, any assembly the host already has loaded - above all
    /// <c>Wisp.Plugins.Abstractions</c> and the framework - must resolve to the SHARED copy in the
    /// default context, NOT a second copy loaded here. We do that by returning null from
    /// <see cref="Load"/> for anything already present in <see cref="AssemblyLoadContext.Default"/>,
    /// which defers resolution to the default context. Only genuinely plugin-private dependencies are
    /// loaded into this context.
    /// </summary>
    internal sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        public PluginLoadContext(string pluginMainDllPath)
            : base(name: "WispPlugin:" + Path.GetFileNameWithoutExtension(pluginMainDllPath), isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(pluginMainDllPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Share anything the host already loaded (the SDK contract, the framework, NAudio, etc.).
            // Returning null falls back to the default context, unifying the type identity.
            if (assemblyName.Name != null)
            {
                foreach (var loaded in Default.Assemblies)
                {
                    if (string.Equals(loaded.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase))
                        return null;
                }
            }

            // Otherwise it's a dependency private to this plugin - load it from the plugin's folder.
            string? path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path != null ? LoadFromAssemblyPath(path) : null; // null => let the runtime try defaults
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            // Native deps shipped alongside the plugin (e.g. OpenCV's runtime DLLs).
            string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path != null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
        }
    }
}
