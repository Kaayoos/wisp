using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Wisp.Models;
using Wisp.Plugins;
using Wisp.Plugins.Export;

namespace Wisp.Services.Plugins
{
    /// <summary>Persisted enable/disable state (plugins.json at the plugins root).</summary>
    internal sealed class PluginsState
    {
        public List<string> Enabled { get; set; } = new();
    }

    /// <summary>
    /// Discovers, loads, isolates and drives all plugins, and fans Wisp's lifecycle events out to them.
    /// This is the single object <c>App</c> talks to. All activation/deactivation happens on the UI
    /// thread (startup, the Plugins tab, or a marshaled auto-disable); events fan out on a background
    /// thread so a slow handler can never stall capture or the UI.
    /// </summary>
    public sealed class PluginManager
    {
        private readonly IHostBridge _bridge;
        private readonly string _pluginsRoot;
        private readonly string _dataRoot;
        private readonly string _statePath;
        private readonly object _lock = new();
        private readonly List<LoadedPlugin> _plugins = new();
        private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

        internal PluginManager(IHostBridge bridge)
        {
            _bridge = bridge;
            _pluginsRoot = bridge.PluginsFolder;
            _dataRoot = Path.Combine(_pluginsRoot, ".data");
            _statePath = Path.Combine(_pluginsRoot, "plugins.json");
        }

        /// <summary>The plugins root folder (<c>%AppData%\Wisp\plugins</c>).</summary>
        public string PluginsRootPath => _pluginsRoot;

        /// <summary>Snapshot of all discovered plugins, for the Plugins tab.</summary>
        internal IReadOnlyList<LoadedPlugin> Plugins
        {
            get { lock (_lock) return _plugins.ToList(); }
        }

        // ───────────────────────────── lifecycle ─────────────────────────────

        /// <summary>Discovers plugins and activates the ones marked enabled. Never throws.</summary>
        public void LoadAll()
        {
            try
            {
                Discover();
                List<LoadedPlugin> toActivate;
                lock (_lock) toActivate = _plugins.Where(p => p.Enabled).ToList();
                foreach (var lp in toActivate) ActivatePlugin(lp);
                Logger.Info($"Plugin manager: {_plugins.Count} discovered, {_plugins.Count(p => p.Active)} active.");
            }
            catch (Exception ex)
            {
                Logger.Error("Plugin LoadAll failed.", ex);
            }
        }

        /// <summary>Tears everything down and re-discovers from disk (picks up newly dropped plugins).</summary>
        public void ReloadAll()
        {
            ShutdownAll();
            LoadAll();
        }

        /// <summary>Shuts down every active plugin (app exit / reload).</summary>
        public void ShutdownAll()
        {
            List<LoadedPlugin> active;
            lock (_lock) active = _plugins.Where(p => p.Instance != null).ToList();
            foreach (var lp in active)
            {
                var instance = lp.Instance;
                if (instance != null) lp.Guard("OnShutdown", () => instance.OnShutdown());
                lp.Active = false;
                SafeUnload(lp);
            }
        }

        /// <summary>Turns a plugin on: persists the choice and activates it now.</summary>
        public void Enable(string id)
        {
            var lp = Find(id);
            if (lp == null) return;
            lp.Enabled = true;
            PersistState();
            if (!lp.Active) ActivatePlugin(lp);
        }

        /// <summary>Turns a plugin off: persists the choice and deactivates it now.</summary>
        public void Disable(string id)
        {
            var lp = Find(id);
            if (lp == null) return;
            lp.Enabled = false;
            PersistState();
            if (lp.Active) DeactivatePlugin(lp);
            lp.Status = "Disabled";
        }

        // ───────────────────────────── discovery / activation ─────────────────────────────

        private void Discover()
        {
            lock (_lock)
            {
                _plugins.Clear();
                var state = LoadState();

                if (!Directory.Exists(_pluginsRoot))
                {
                    try { Directory.CreateDirectory(_pluginsRoot); } catch { }
                    return;
                }

                foreach (var dir in Directory.GetDirectories(_pluginsRoot))
                {
                    string folderName = Path.GetFileName(dir);
                    if (folderName.StartsWith(".")) continue; // .data and hidden folders aren't plugins

                    string manifestPath = Path.Combine(dir, "plugin.json");
                    if (!File.Exists(manifestPath))
                    {
                        Logger.Warn($"Plugin folder '{folderName}' has no plugin.json; skipping.");
                        continue;
                    }

                    PluginManifest? manifest = null;
                    try { manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath)); }
                    catch (Exception ex) { Logger.Error($"Failed to read plugin.json in '{folderName}'.", ex); }
                    if (manifest == null) continue;

                    if (string.IsNullOrWhiteSpace(manifest.Id)) manifest.Id = folderName;
                    if (string.IsNullOrWhiteSpace(manifest.Name)) manifest.Name = manifest.Id;

                    if (_plugins.Any(p => p.Id.Equals(manifest.Id, StringComparison.OrdinalIgnoreCase)))
                    {
                        Logger.Warn($"Duplicate plugin id '{manifest.Id}'; skipping folder '{folderName}'.");
                        continue;
                    }

                    bool enabled = state.Enabled.Contains(manifest.Id, StringComparer.OrdinalIgnoreCase);
                    var lp = new LoadedPlugin(manifest, dir)
                    {
                        Enabled = enabled,
                        Status = enabled ? "Enabling…" : "Disabled"
                    };
                    _plugins.Add(lp);
                }
            }
        }

        private void ActivatePlugin(LoadedPlugin lp)
        {
            if (lp.Active) return;

            if (lp.Manifest.MinApiVersion > WispPluginSdk.ApiVersion)
            {
                lp.Status = $"Requires a newer Wisp (plugin API v{lp.Manifest.MinApiVersion}).";
                Logger.Warn($"[Plugin:{lp.Id}] {lp.Status}");
                return;
            }

            try
            {
                string dll = ResolveEntryDll(lp);
                var ctx = new PluginLoadContext(dll);
                var asm = ctx.LoadFromAssemblyPath(dll);
                var type = FindPluginType(asm)
                           ?? throw new InvalidOperationException("no public IWispPlugin implementation found.");
                var instance = (IWispPlugin)Activator.CreateInstance(type)!;

                string dataDir = Path.Combine(_dataRoot, SafeId(lp.Id));
                var host = new PluginHost(_bridge, lp.Id, dataDir);

                lp.Context = ctx;
                lp.Instance = instance;
                lp.Host = host;

                bool ok = lp.Guard("OnLoaded", () => instance.OnLoaded(host))
                          && lp.Guard("OnEnabled", () => instance.OnEnabled());

                if (ok)
                {
                    lp.Active = true;
                    lp.Status = "Enabled";
                    Logger.Info($"[Plugin:{lp.Id}] activated ({lp.Manifest.Name} v{lp.Manifest.Version}).");
                }
                else
                {
                    // Failed during load/enable - Guard already logged + set the status. Tear back down.
                    lp.Active = false;
                    SafeUnload(lp);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[Plugin:{lp.Id}] failed to load.", ex);
                lp.Status = "Load failed: " + ex.Message;
                SafeUnload(lp);
            }
        }

        private void DeactivatePlugin(LoadedPlugin lp)
        {
            var instance = lp.Instance;
            if (instance != null && lp.Active)
                lp.Guard("OnDisabled", () => instance.OnDisabled());
            lp.Active = false;
            SafeUnload(lp);
        }

        private void SafeUnload(LoadedPlugin lp)
        {
            // Release host-held subscriptions and contributions BEFORE dropping the instance/context, so
            // a disabled plugin leaves nothing behind (static-event forwarder, clip actions, tray items,
            // themes) that would leak or block the ALC from unloading.
            try { lp.Host?.Teardown(); } catch (Exception ex) { Logger.Warn($"[Plugin:{lp.Id}] teardown: {ex.Message}"); }
            try { _bridge.RemovePluginContributions(lp.Id); } catch (Exception ex) { Logger.Warn($"[Plugin:{lp.Id}] contribution cleanup: {ex.Message}"); }

            lp.Instance = null;
            lp.Host = null;
            try { lp.Context?.Unload(); }
            catch (Exception ex) { Logger.Warn($"[Plugin:{lp.Id}] context unload: {ex.Message}"); }
            lp.Context = null;
        }

        private static string ResolveEntryDll(LoadedPlugin lp)
        {
            string dir = lp.DirectoryPath;

            if (!string.IsNullOrWhiteSpace(lp.Manifest.EntryAssembly))
            {
                string p = Path.Combine(dir, lp.Manifest.EntryAssembly);
                if (File.Exists(p)) return p;
                throw new FileNotFoundException($"entryAssembly '{lp.Manifest.EntryAssembly}' not found in plugin folder.");
            }

            // Infer the main assembly from its <name>.deps.json (so dependency resolution works).
            var deps = Directory.GetFiles(dir, "*.deps.json");
            if (deps.Length == 1)
            {
                string candidate = deps[0][..^".deps.json".Length] + ".dll";
                if (File.Exists(candidate)) return candidate;
            }

            // Last resort: the first DLL that isn't the shared SDK.
            string? dll = Directory.GetFiles(dir, "*.dll").FirstOrDefault(f =>
                !Path.GetFileName(f).Equals("Wisp.Plugins.Abstractions.dll", StringComparison.OrdinalIgnoreCase));
            if (dll != null) return dll;

            throw new FileNotFoundException("no plugin DLL found in " + dir);
        }

        private static Type? FindPluginType(Assembly asm)
        {
            Type?[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types; }

            return types.FirstOrDefault(t =>
                t != null && typeof(IWispPlugin).IsAssignableFrom(t) &&
                !t.IsAbstract && !t.IsInterface && t.GetConstructor(Type.EmptyTypes) != null);
        }

        // ───────────────────────────── event fan-out ─────────────────────────────

        public void RaiseClipSaved(Clip clip)
        {
            var a = new ClipSavedEventArgs(PluginMap.ToClipInfo(clip));
            Fan("ClipSaved", e => e.RaiseClipSaved(a));
        }

        public void RaiseClipRemoved(Clip clip, bool superseded)
        {
            var a = new ClipRemovedEventArgs(PluginMap.ToClipInfo(clip), superseded);
            Fan("ClipRemoved", e => e.RaiseClipRemoved(a));
        }

        public void RaiseRecordingStarted()
        {
            var a = new RecordingEventArgs(CurrentRecording());
            Fan("RecordingStarted", e => e.RaiseRecordingStarted(a));
        }

        public void RaiseRecordingStopped()
        {
            var a = new RecordingEventArgs(CurrentRecording());
            Fan("RecordingStopped", e => e.RaiseRecordingStopped(a));
        }

        public void RaiseGameDetected(string game)
        {
            var a = new GameEventArgs(new GameInfo { Name = game ?? "" });
            Fan("GameDetected", e => e.RaiseGameDetected(a));
        }

        public void RaiseGameStopped()
        {
            var a = new GameEventArgs(new GameInfo());
            Fan("GameStopped", e => e.RaiseGameStopped(a));
        }

        public void RaiseHotkey()
        {
            var a = new HotkeyEventArgs(new GameInfo { Name = _bridge.CurrentGameName });
            Fan("HotkeyTriggered", e => e.RaiseHotkey(a));
        }

        /// <summary>A clip just opened in the player - notify plugins so they can paint markers, etc.</summary>
        public void RaisePlayerOpened(Clip clip)
        {
            var info = PluginMap.ToClipInfo(clip);
            FanUi("PlayerOpened", p => p.RaiseOpened(info));
        }

        /// <summary>The player closed - notify plugins.</summary>
        public void RaisePlayerClosed() => FanUi("PlayerClosed", p => p.RaiseClosed());

        /// <summary>
        /// Asks every active plugin for the export layers it wants burned into <paramref name="clip"/>, in
        /// plugin order. Each call is crash-isolated; a throwing or empty plugin simply contributes nothing.
        /// Returned to the exporter so it can composite them with ffmpeg.
        /// </summary>
        public IReadOnlyList<ExportLayer> CollectExportLayers(Clip clip)
        {
            var info = PluginMap.ToClipInfo(clip);
            List<LoadedPlugin> targets;
            lock (_lock) targets = _plugins.Where(p => p.Active && p.Instance != null).ToList();

            var layers = new List<ExportLayer>();
            foreach (var lp in targets)
            {
                var instance = lp.Instance;            // snapshot; a concurrent Disable may null this
                if (instance == null || !lp.Active) continue;
                IReadOnlyList<ExportLayer>? contributed = null;
                lp.Guard("GetExportLayers", () => contributed = instance.GetExportLayers(info));
                if (contributed != null)
                    foreach (var layer in contributed)
                        if (layer != null && !string.IsNullOrEmpty(layer.SourcePath)) layers.Add(layer);
            }
            return layers;
        }

        private RecordingInfo CurrentRecording() => new()
        {
            IsRecording = _bridge.IsRecording,
            Game = new GameInfo { Name = _bridge.CurrentGameName }
        };

        /// <summary>Fans one event out to every active plugin on a background thread, isolated per plugin.</summary>
        private void Fan(string evt, Action<HostEvents> raise)
        {
            List<LoadedPlugin> targets;
            lock (_lock) targets = _plugins.Where(p => p.Active && p.Host != null).ToList();
            if (targets.Count == 0) return;

            Task.Run(() =>
            {
                foreach (var lp in targets)
                {
                    var host = lp.Host;             // snapshot; a concurrent Disable may null this
                    if (host == null || !lp.Active) continue;
                    lp.Guard($"event {evt}", () => raise(host.EventsImpl));
                    MaybeAutoDisable(lp);
                }
            });
        }

        /// <summary>
        /// Fans a player event out to every active plugin SYNCHRONOUSLY on the calling (UI) thread. Player
        /// events are UI-coupled - handlers paint markers / read playback - so they run inline on the UI
        /// thread (callers raise from there), letting handlers call straight back into the player surface.
        /// </summary>
        private void FanUi(string evt, Action<HostPlayer> raise)
        {
            List<LoadedPlugin> targets;
            lock (_lock) targets = _plugins.Where(p => p.Active && p.Host != null).ToList();
            foreach (var lp in targets)
            {
                var host = lp.Host;                 // snapshot; a concurrent Disable may null this
                if (host == null || !lp.Active) continue;
                lp.Guard($"event {evt}", () => raise(host.PlayerImpl));
                MaybeAutoDisable(lp);
            }
        }

        private void MaybeAutoDisable(LoadedPlugin lp)
        {
            if (!lp.TooManyFailures || !lp.Enabled) return;
            Logger.Warn($"[Plugin:{lp.Id}] auto-disabled after repeated failures.");
            lp.Enabled = false;
            PersistState();
            // Deactivate on the UI thread (the plugin may own WPF objects).
            _bridge.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (lp.Active) DeactivatePlugin(lp);
                lp.Status = "Auto-disabled after repeated errors";
            }));
        }

        // ───────────────────────────── state persistence ─────────────────────────────

        private LoadedPlugin? Find(string id)
        {
            lock (_lock) return _plugins.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        }

        private PluginsState LoadState()
        {
            try
            {
                if (File.Exists(_statePath))
                    return JsonSerializer.Deserialize<PluginsState>(File.ReadAllText(_statePath)) ?? new PluginsState();
            }
            catch (Exception ex) { Logger.Warn($"Failed to read plugins.json: {ex.Message}"); }
            return new PluginsState();
        }

        private void PersistState()
        {
            try
            {
                List<string> enabled;
                lock (_lock) enabled = _plugins.Where(p => p.Enabled).Select(p => p.Id).ToList();
                Directory.CreateDirectory(_pluginsRoot);
                File.WriteAllText(_statePath, JsonSerializer.Serialize(new PluginsState { Enabled = enabled }, Indented));
            }
            catch (Exception ex) { Logger.Error("Failed to persist plugins.json.", ex); }
        }

        private static string SafeId(string id)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) id = id.Replace(c, '_');
            return id;
        }
    }
}
