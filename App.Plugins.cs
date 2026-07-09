using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;
using Wisp.Models;
using Wisp.Plugins.Export;
using Wisp.Plugins.Player;
using Wisp.Plugins.UI;
using Wisp.Services;
using Wisp.Services.Plugins;

namespace Wisp
{
    /// <summary>
    /// Plugin integration for the app. App is the <see cref="IHostBridge"/> the plugin layer talks to
    /// (so plugins never reach into App directly), owns the <see cref="PluginManager"/>, and raises the
    /// lifecycle events from the existing chokepoints in App.xaml.cs / App.Chaining.cs.
    /// </summary>
    public partial class App : IHostBridge
    {
        private PluginManager? _pluginManager;

        /// <summary>The plugin manager (null until startup has initialized it).</summary>
        public PluginManager? Plugins => _pluginManager;

        /// <summary>Creates the manager and loads all enabled plugins. Never throws into startup.</summary>
        private void InitializePlugins()
        {
            try
            {
                _pluginManager = new PluginManager(this);
                _pluginManager.LoadAll();
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to initialize the plugin system.", ex);
            }
        }

        /// <summary>Shuts every plugin down cleanly. Called from ShutdownApp before services are torn down.</summary>
        private void ShutdownPlugins()
        {
            try { _pluginManager?.ShutdownAll(); }
            catch (Exception ex) { Logger.Error("Error shutting down plugins.", ex); }
        }

        /// <summary>Runs an action on the UI thread (inline if already there).</summary>
        private void RunOnUi(Action action)
        {
            if (Dispatcher.CheckAccess()) action();
            else Dispatcher.Invoke(action);
        }

        // ───────────────────────────── IHostBridge ─────────────────────────────

        AppSettings IHostBridge.Settings => _settings;
        DatabaseService IHostBridge.Db => _dbService;
        FFmpegRecorderService IHostBridge.Recorder => _recorderService;
        Dispatcher IHostBridge.Dispatcher => Dispatcher;

        string IHostBridge.AppVersion =>
            Assembly.GetExecutingAssembly().GetName().Version?.ToString(2) ?? "1.0";

        string IHostBridge.AppDataFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Wisp");

        string IHostBridge.PluginsFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Wisp", "plugins");

        bool IHostBridge.IsRecording => _recorderService?.IsRecording ?? false;

        string IHostBridge.CurrentGameName => FFmpegRecorderService.GetForegroundGameName();

        void IHostBridge.StartRecording() => RunOnUi(StartBackgroundRecording);
        void IHostBridge.StopRecording() => RunOnUi(StopBackgroundRecording);
        void IHostBridge.CaptureClipNow() => RunOnUi(OnHotkeyTriggered);

        void IHostBridge.NotifyClipsChanged() => RunOnUi(() => _mainWindow?.LoadClips());

        // ───────────────────────────── plugin UI contributions ─────────────────────────────

        private readonly object _contribLock = new();
        private readonly List<PluginClipActionEntry> _clipActions = new();
        private readonly Dictionary<string, MenuItem> _trayPluginItems = new(StringComparer.Ordinal);
        private readonly List<PluginPlayerButtonEntry> _playerButtons = new();
        private readonly List<PluginPlayerMarkerEntry> _playerMarkers = new();
        private readonly List<PluginPlayerOverlayEntry> _playerOverlays = new();

        /// <summary>Snapshot of registered clip actions, for MainWindow to fold into the clip context menu.</summary>
        internal IReadOnlyList<PluginClipActionEntry> GetPluginClipActions()
        {
            lock (_contribLock) return _clipActions.ToList();
        }

        /// <summary>Snapshot of registered player buttons, for MainWindow to render on the player control bar.</summary>
        internal IReadOnlyList<PluginPlayerButtonEntry> GetPlayerButtons()
        {
            lock (_contribLock) return _playerButtons.ToList();
        }

        /// <summary>Snapshot of registered timeline markers, for MainWindow to paint on the scrubber.</summary>
        internal IReadOnlyList<PluginPlayerMarkerEntry> GetPlayerMarkers()
        {
            lock (_contribLock) return _playerMarkers.ToList();
        }

        /// <summary>Snapshot of registered video overlays, for MainWindow to host over the player video.</summary>
        internal IReadOnlyList<PluginPlayerOverlayEntry> GetPlayerOverlays()
        {
            lock (_contribLock) return _playerOverlays.ToList();
        }

        /// <summary>Collects the export layers every active plugin wants burned into a clip (for MainWindow export).</summary>
        internal IReadOnlyList<ExportLayer> GetExportLayersForClip(Clip clip)
            => _pluginManager?.CollectExportLayers(clip) ?? Array.Empty<ExportLayer>();

        /// <summary>
        /// Drops every plugin's timeline markers. MainWindow calls this as a clip opens/closes so each clip
        /// starts with a clean timeline - the owning plugin repaints via its <c>ClipOpened</c> handler.
        /// </summary>
        internal void ClearAllPlayerMarkers()
        {
            lock (_contribLock) _playerMarkers.Clear();
        }

        /// <summary>Forwards a player-open event to plugins (called from MainWindow on the UI thread).</summary>
        internal void RaisePlayerOpened(Clip clip) => _pluginManager?.RaisePlayerOpened(clip);

        /// <summary>Forwards a player-close event to plugins (called from MainWindow on the UI thread).</summary>
        internal void RaisePlayerClosed() => _pluginManager?.RaisePlayerClosed();

        /// <summary>Reads a value on the UI thread (plugins may query the player from a background handler).</summary>
        private T GetOnUi<T>(Func<T> read)
        {
            if (Dispatcher.CheckAccess()) return read();
            try { return Dispatcher.Invoke(read); }
            catch { return default!; }
        }

        void IHostBridge.ShowToast(string title, string? message, ToastKind kind)
            => RunOnUi(() => _mainWindow?.ShowToast(title, message, MapToastKind(kind)));

        void IHostBridge.RegisterClipAction(string pluginId, ClipAction action)
        {
            if (action == null) return;
            lock (_contribLock)
            {
                _clipActions.RemoveAll(e => e.PluginId == pluginId && e.Action.Id == action.Id);
                _clipActions.Add(new PluginClipActionEntry(pluginId, action));
            }
        }

        void IHostBridge.UnregisterClipAction(string pluginId, string actionId)
        {
            lock (_contribLock) _clipActions.RemoveAll(e => e.PluginId == pluginId && e.Action.Id == actionId);
        }

        void IHostBridge.AddTrayMenuItem(string pluginId, string id, string label, Action onClick)
            => RunOnUi(() =>
            {
                if (_trayMenu == null) return;
                string key = pluginId + "::" + id;
                RemoveTrayItemByKey(key);

                var item = new MenuItem { Header = label };
                item.Click += (s, e) =>
                {
                    try { onClick?.Invoke(); }
                    catch (Exception ex) { Logger.Error($"[Plugin:{pluginId}] tray item '{id}' threw.", ex); }
                };

                int idx = _trayPluginAnchor != null ? _trayMenu.Items.IndexOf(_trayPluginAnchor) : -1;
                if (idx < 0) idx = _trayMenu.Items.Count;
                _trayMenu.Items.Insert(idx, item);
                _trayPluginItems[key] = item;
            });

        void IHostBridge.RemoveTrayMenuItem(string pluginId, string id)
            => RunOnUi(() => RemoveTrayItemByKey(pluginId + "::" + id));

        // ───────────────────────────── player surface ─────────────────────────────
        // Reads marshal to the UI thread (a plugin may query from a background event handler); the player
        // lives in MainWindow. Button/marker mutations update the registry then refresh the live player UI.

        bool IHostBridge.PlayerIsOpen => GetOnUi(() => _mainWindow?.PlayerIsOpen ?? false);
        Clip? IHostBridge.PlayerCurrentClip => GetOnUi(() => _mainWindow?.PlayerActiveClip);
        double IHostBridge.PlayerPositionSeconds => GetOnUi(() => _mainWindow?.PlayerPositionSeconds ?? 0.0);
        double IHostBridge.PlayerDurationSeconds => GetOnUi(() => _mainWindow?.PlayerDurationSeconds ?? 0.0);
        bool IHostBridge.PlayerIsPlaying => GetOnUi(() => _mainWindow?.PlayerIsPlaying ?? false);

        void IHostBridge.PlayerSeek(double seconds) => RunOnUi(() => _mainWindow?.SeekToSeconds(seconds));
        void IHostBridge.PlayerPlay() => RunOnUi(() => _mainWindow?.PlayerPlay());
        void IHostBridge.PlayerPause() => RunOnUi(() => _mainWindow?.PlayerPause());

        void IHostBridge.AddPlayerButton(string pluginId, PlayerButton button)
        {
            if (button == null) return;
            lock (_contribLock)
            {
                _playerButtons.RemoveAll(e => e.PluginId == pluginId && e.Button.Id == button.Id);
                _playerButtons.Add(new PluginPlayerButtonEntry(pluginId, button));
            }
            RunOnUi(() => _mainWindow?.RefreshPlayerPluginButtons());
        }

        void IHostBridge.RemovePlayerButton(string pluginId, string buttonId)
        {
            lock (_contribLock) _playerButtons.RemoveAll(e => e.PluginId == pluginId && e.Button.Id == buttonId);
            RunOnUi(() => _mainWindow?.RefreshPlayerPluginButtons());
        }

        void IHostBridge.SetPlayerMarkers(string pluginId, IReadOnlyList<TimelineMarker> markers)
        {
            lock (_contribLock)
            {
                _playerMarkers.RemoveAll(e => e.PluginId == pluginId);
                if (markers != null)
                    foreach (var m in markers)
                        if (m != null) _playerMarkers.Add(new PluginPlayerMarkerEntry(pluginId, m));
            }
            RunOnUi(() => _mainWindow?.RefreshPlayerMarkers());
        }

        void IHostBridge.ClearPlayerMarkers(string pluginId)
        {
            lock (_contribLock) _playerMarkers.RemoveAll(e => e.PluginId == pluginId);
            RunOnUi(() => _mainWindow?.RefreshPlayerMarkers());
        }

        void IHostBridge.AddPlayerOverlay(string pluginId, PlayerOverlay overlay)
        {
            if (overlay == null) return;
            lock (_contribLock)
            {
                _playerOverlays.RemoveAll(e => e.PluginId == pluginId && e.Overlay.Id == overlay.Id);
                _playerOverlays.Add(new PluginPlayerOverlayEntry(pluginId, overlay));
            }
            RunOnUi(() => _mainWindow?.RefreshPlayerOverlays());
        }

        void IHostBridge.RemovePlayerOverlay(string pluginId, string overlayId)
        {
            lock (_contribLock) _playerOverlays.RemoveAll(e => e.PluginId == pluginId && e.Overlay.Id == overlayId);
            RunOnUi(() => _mainWindow?.RefreshPlayerOverlays());
        }

        void IHostBridge.ClearPlayerOverlays(string pluginId)
        {
            lock (_contribLock) _playerOverlays.RemoveAll(e => e.PluginId == pluginId);
            RunOnUi(() => _mainWindow?.RefreshPlayerOverlays());
        }

        void IHostBridge.RemovePluginContributions(string pluginId)
            => RunOnUi(() =>
            {
                lock (_contribLock)
                {
                    _clipActions.RemoveAll(e => e.PluginId == pluginId);
                    _playerButtons.RemoveAll(e => e.PluginId == pluginId);
                    _playerMarkers.RemoveAll(e => e.PluginId == pluginId);
                    _playerOverlays.RemoveAll(e => e.PluginId == pluginId);
                }
                _mainWindow?.RefreshPlayerPluginButtons();
                _mainWindow?.RefreshPlayerMarkers();
                _mainWindow?.RefreshPlayerOverlays();

                var keys = _trayPluginItems.Keys
                    .Where(k => k.StartsWith(pluginId + "::", StringComparison.Ordinal)).ToList();
                foreach (var k in keys) RemoveTrayItemByKey(k);

                bool activeThemeRemoved = ThemeManager.UnregisterThemesOf(pluginId);
                if (activeThemeRemoved)
                {
                    _settings.ActiveThemeId = "";
                    try { _settings.Save(); } catch { /* best effort */ }
                    ThemeManager.Apply(_settings); // revert to the default palette + saved accent
                }
            });

        private void RemoveTrayItemByKey(string key)
        {
            if (_trayMenu != null && _trayPluginItems.TryGetValue(key, out var item))
                _trayMenu.Items.Remove(item);
            _trayPluginItems.Remove(key);
        }

        // Qualify the type explicitly: bare "MainWindow" would bind to the inherited Application.MainWindow
        // property (App : Application), not the Wisp.MainWindow type that owns the ToastKind enum.
        private static Wisp.MainWindow.ToastKind MapToastKind(ToastKind kind) => kind switch
        {
            ToastKind.Success => Wisp.MainWindow.ToastKind.Success,
            ToastKind.Warning => Wisp.MainWindow.ToastKind.Warning,
            ToastKind.Error   => Wisp.MainWindow.ToastKind.Error,
            _                 => Wisp.MainWindow.ToastKind.Info,
        };
    }

    /// <summary>A clip action a plugin registered, plus the id of the owning plugin (for cleanup).</summary>
    internal sealed record PluginClipActionEntry(string PluginId, ClipAction Action);

    /// <summary>A player control-bar button a plugin registered, plus the owning plugin id (for cleanup).</summary>
    internal sealed record PluginPlayerButtonEntry(string PluginId, PlayerButton Button);

    /// <summary>One timeline marker a plugin registered, plus the owning plugin id (for cleanup).</summary>
    internal sealed record PluginPlayerMarkerEntry(string PluginId, TimelineMarker Marker);

    /// <summary>One player video overlay a plugin registered, plus the owning plugin id (for cleanup).</summary>
    internal sealed record PluginPlayerOverlayEntry(string PluginId, PlayerOverlay Overlay);
}
