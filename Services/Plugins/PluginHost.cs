using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Wisp.Models;
using Wisp.Plugins;
using Wisp.Plugins.Player;
using Wisp.Plugins.Theming;
using Wisp.Plugins.UI;

namespace Wisp.Services.Plugins
{
    /// <summary>
    /// What a <see cref="PluginHost"/> needs from the application to service plugin calls. Implemented
    /// by <c>App</c> (see App.Plugins.cs) so the plugin layer depends on this small abstraction rather
    /// than reaching into the App directly. All recorder-control members are expected to marshal to the
    /// UI thread themselves.
    /// </summary>
    internal interface IHostBridge
    {
        AppSettings Settings { get; }
        DatabaseService Db { get; }
        FFmpegRecorderService Recorder { get; }
        Dispatcher Dispatcher { get; }

        string AppVersion { get; }
        string AppDataFolder { get; }
        string PluginsFolder { get; }

        bool IsRecording { get; }
        string CurrentGameName { get; }

        void StartRecording();
        void StopRecording();
        void CaptureClipNow();

        /// <summary>Tell the open library window (if any) to reload after a plugin changed clips.</summary>
        void NotifyClipsChanged();

        // ── UI extension points (the host marshals these onto the UI thread) ──
        /// <summary>Shows a native Wisp toast on behalf of a plugin.</summary>
        void ShowToast(string title, string? message, ToastKind kind);

        /// <summary>Registers/replaces a plugin's clip context-menu command.</summary>
        void RegisterClipAction(string pluginId, ClipAction action);

        /// <summary>Removes one of a plugin's clip actions.</summary>
        void UnregisterClipAction(string pluginId, string actionId);

        /// <summary>Adds/replaces a plugin item in the tray menu (above Quit).</summary>
        void AddTrayMenuItem(string pluginId, string id, string label, Action onClick);

        /// <summary>Removes one of a plugin's tray items.</summary>
        void RemoveTrayMenuItem(string pluginId, string id);

        // ── Clip player surface (the host marshals reads/writes onto the UI thread) ──
        /// <summary>True while a clip is open in the player.</summary>
        bool PlayerIsOpen { get; }
        /// <summary>The clip open in the player, or null.</summary>
        Clip? PlayerCurrentClip { get; }
        /// <summary>Current playback position in seconds (0 when closed).</summary>
        double PlayerPositionSeconds { get; }
        /// <summary>Open clip length in seconds (0 when unknown/closed).</summary>
        double PlayerDurationSeconds { get; }
        /// <summary>True if the clip is playing.</summary>
        bool PlayerIsPlaying { get; }
        /// <summary>Seeks the open clip (seconds, clamped).</summary>
        void PlayerSeek(double seconds);
        /// <summary>Resumes playback.</summary>
        void PlayerPlay();
        /// <summary>Pauses playback.</summary>
        void PlayerPause();
        /// <summary>Adds/replaces a plugin's player control-bar button.</summary>
        void AddPlayerButton(string pluginId, PlayerButton button);
        /// <summary>Removes one of a plugin's player buttons.</summary>
        void RemovePlayerButton(string pluginId, string buttonId);
        /// <summary>Replaces a plugin's set of timeline markers and redraws.</summary>
        void SetPlayerMarkers(string pluginId, IReadOnlyList<TimelineMarker> markers);
        /// <summary>Clears a plugin's timeline markers and redraws.</summary>
        void ClearPlayerMarkers(string pluginId);
        /// <summary>Adds/replaces a plugin's video overlay and redraws the overlay host.</summary>
        void AddPlayerOverlay(string pluginId, PlayerOverlay overlay);
        /// <summary>Removes one of a plugin's video overlays.</summary>
        void RemovePlayerOverlay(string pluginId, string overlayId);
        /// <summary>Clears all of a plugin's video overlays.</summary>
        void ClearPlayerOverlays(string pluginId);

        /// <summary>Drops everything a plugin contributed (clip actions, tray items, themes, player UI) on disable.</summary>
        void RemovePluginContributions(string pluginId);
    }

    /// <summary>
    /// The concrete <see cref="IWispHost"/> handed to a single plugin. Scoped to that plugin's id for
    /// logging + storage, and owns the plugin's private event hub.
    /// </summary>
    internal sealed class PluginHost : IWispHost
    {
        private readonly IHostBridge _bridge;

        public PluginHost(IHostBridge bridge, string pluginId, string pluginDataDir)
        {
            _bridge = bridge;
            var log = new HostLog(pluginId);
            Log = log;
            EventsImpl = new HostEvents(log);
            Clips = new HostClipLibrary(bridge);
            Recorder = new HostRecorderControl(bridge);
            Storage = new HostPluginStorage(pluginDataDir);
            Ui = new HostUiBridge(bridge, pluginId);
            ThemingImpl = new HostTheming(bridge, pluginId);
            Theming = ThemingImpl;
            PlayerImpl = new HostPlayer(bridge, pluginId, log);
            Player = PlayerImpl;
            Info = new HostInfo
            {
                AppVersion = bridge.AppVersion,
                ApiVersion = WispPluginSdk.ApiVersion,
                ClipOutputFolder = bridge.Settings.OutputFolder,
                AppDataFolder = bridge.AppDataFolder,
                PluginsFolder = bridge.PluginsFolder
            };
        }

        public IWispLog Log { get; }
        public IWispEvents Events => EventsImpl;
        public IClipLibrary Clips { get; }
        public IRecorderControl Recorder { get; }
        public IPluginStorage Storage { get; }
        public IUiBridge Ui { get; }
        public IWispTheming Theming { get; }
        public IWispPlayer Player { get; }
        public HostInfo Info { get; }

        /// <summary>The event hub, typed so the manager can raise events into it.</summary>
        public HostEvents EventsImpl { get; }

        /// <summary>The theming impl, typed so the manager can detach its static-event hook on unload.</summary>
        internal HostTheming ThemingImpl { get; }

        /// <summary>The player impl, typed so the manager can raise clip-open/close into it.</summary>
        internal HostPlayer PlayerImpl { get; }

        /// <summary>Releases host-held subscriptions for this plugin (called before the ALC is unloaded).</summary>
        internal void Teardown() => ThemingImpl.Detach();
    }

    /// <summary>Maps Wisp's internal clip model to the read-only DTO crossing the plugin boundary.</summary>
    internal static class PluginMap
    {
        public static ClipInfo ToClipInfo(Clip c) => new()
        {
            Id = c.Id,
            FilePath = c.FilePath,
            Filename = c.Filename,
            CreatedAt = c.CreatedAt,
            DurationSeconds = c.DurationSeconds,
            FileSizeBytes = c.FileSizeBytes,
            GameName = c.GameName,
            Tags = c.Tags,
            IsFavorite = c.IsFavorite,
            ThumbnailPath = c.ThumbnailPath,
            SystemTrackPath = c.SystemTrackPath,
            MicTrackPath = c.MicTrackPath,
            SocialTrackPath = c.SocialTrackPath,
            ChainMarkers = c.ChainMarkers
        };
    }

    // ───────────────────────────── sub-service implementations ─────────────────────────────

    internal sealed class HostLog : IWispLog
    {
        private readonly string _prefix;
        public HostLog(string pluginId) => _prefix = $"[Plugin:{pluginId}] ";
        public void Info(string message) => Logger.Info(_prefix + message);
        public void Warn(string message) => Logger.Warn(_prefix + message);
        public void Error(string message, Exception? ex = null) => Logger.Error(_prefix + message, ex);
    }

    /// <summary>
    /// One plugin's isolated event hub. Handlers run on whatever thread the manager raises from (a
    /// background thread for the fan-out). Each handler is invoked in its own try/catch so one bad
    /// handler neither breaks the others nor escapes to the host.
    /// </summary>
    internal sealed class HostEvents : IWispEvents
    {
        private readonly IWispLog _log;
        public HostEvents(IWispLog log) => _log = log;

        public event EventHandler<ClipSavedEventArgs>? ClipSaved;
        public event EventHandler<ClipRemovedEventArgs>? ClipRemoved;
        public event EventHandler<RecordingEventArgs>? RecordingStarted;
        public event EventHandler<RecordingEventArgs>? RecordingStopped;
        public event EventHandler<GameEventArgs>? GameDetected;
        public event EventHandler<GameEventArgs>? GameStopped;
        public event EventHandler<HotkeyEventArgs>? HotkeyTriggered;

        internal void RaiseClipSaved(ClipSavedEventArgs a) => Dispatch(ClipSaved, a, nameof(ClipSaved));
        internal void RaiseClipRemoved(ClipRemovedEventArgs a) => Dispatch(ClipRemoved, a, nameof(ClipRemoved));
        internal void RaiseRecordingStarted(RecordingEventArgs a) => Dispatch(RecordingStarted, a, nameof(RecordingStarted));
        internal void RaiseRecordingStopped(RecordingEventArgs a) => Dispatch(RecordingStopped, a, nameof(RecordingStopped));
        internal void RaiseGameDetected(GameEventArgs a) => Dispatch(GameDetected, a, nameof(GameDetected));
        internal void RaiseGameStopped(GameEventArgs a) => Dispatch(GameStopped, a, nameof(GameStopped));
        internal void RaiseHotkey(HotkeyEventArgs a) => Dispatch(HotkeyTriggered, a, nameof(HotkeyTriggered));

        private void Dispatch<T>(EventHandler<T>? handlers, T args, string name) where T : EventArgs
        {
            if (handlers == null) return;
            foreach (var d in handlers.GetInvocationList())
            {
                try { ((EventHandler<T>)d).Invoke(this, args); }
                catch (Exception ex) { _log.Error($"Event handler for {name} threw.", ex); }
            }
        }
    }

    internal sealed class HostClipLibrary : IClipLibrary
    {
        private readonly IHostBridge _bridge;
        public HostClipLibrary(IHostBridge bridge) => _bridge = bridge;

        public IReadOnlyList<ClipInfo> GetClips()
            => _bridge.Db.GetAllClips().Select(PluginMap.ToClipInfo).ToList();

        public ClipInfo? GetClip(int id)
        {
            var clip = _bridge.Db.GetAllClips().FirstOrDefault(c => c.Id == id);
            return clip != null ? PluginMap.ToClipInfo(clip) : null;
        }

        public void SetTags(int id, string tags)
        {
            _bridge.Db.UpdateTags(id, tags ?? "");
            _bridge.NotifyClipsChanged();
        }

        public void SetFavorite(int id, bool isFavorite)
        {
            _bridge.Db.SetFavorite(id, isFavorite);
            _bridge.NotifyClipsChanged();
        }

        public void Delete(int id)
        {
            var clip = _bridge.Db.GetAllClips().FirstOrDefault(c => c.Id == id);
            if (clip != null)
            {
                _bridge.Db.DeleteClipAndFiles(clip);
                _bridge.NotifyClipsChanged();
            }
        }

        public ClipInfo? ImportClip(string filePath, string gameName = "")
        {
            var clip = _bridge.Recorder.ImportSingleFile(filePath, gameName);
            if (clip == null) return null;
            _bridge.NotifyClipsChanged();
            return PluginMap.ToClipInfo(clip);
        }
    }

    internal sealed class HostRecorderControl : IRecorderControl
    {
        private readonly IHostBridge _bridge;
        public HostRecorderControl(IHostBridge bridge) => _bridge = bridge;

        public bool IsRecording => _bridge.IsRecording;
        public GameInfo CurrentGame => new() { Name = _bridge.CurrentGameName };
        public void Start() => _bridge.StartRecording();
        public void Stop() => _bridge.StopRecording();
        public void CaptureClipNow() => _bridge.CaptureClipNow();
    }

    internal sealed class HostPluginStorage : IPluginStorage
    {
        private readonly string _dir;
        private readonly string _settingsPath;
        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

        public HostPluginStorage(string pluginDataDir)
        {
            _dir = pluginDataDir;
            _settingsPath = Path.Combine(_dir, "settings.json");
        }

        public string DataDirectory
        {
            get
            {
                try { Directory.CreateDirectory(_dir); } catch { }
                return _dir;
            }
        }

        public T? LoadSettings<T>() where T : class
        {
            try
            {
                if (File.Exists(_settingsPath))
                    return JsonSerializer.Deserialize<T>(File.ReadAllText(_settingsPath));
            }
            catch (Exception ex)
            {
                Logger.Warn($"Plugin settings load failed ({_settingsPath}): {ex.Message}");
            }
            return null;
        }

        public void SaveSettings<T>(T settings) where T : class
        {
            try
            {
                Directory.CreateDirectory(_dir);
                File.WriteAllText(_settingsPath, JsonSerializer.Serialize(settings, Options));
            }
            catch (Exception ex)
            {
                Logger.Error("Plugin settings save failed.", ex);
            }
        }
    }

    /// <summary>Normalises a Segoe MDL2 icon spec (glyph char, "E10B", "0xE10B", "&amp;#xE10B;", "U+E10B")
    /// into the actual glyph string. In code-behind the XAML entity "&amp;#xE10B;" is a literal 7-char
    /// string, NOT the glyph - this is what turns a plugin's icon spec into something that renders.</summary>
    internal static class GlyphUtil
    {
        public static string ToGlyph(string? spec)
        {
            if (string.IsNullOrWhiteSpace(spec)) return "";
            string s = spec.Trim();
            if (s.Length == 1) return s; // already a single glyph char

            string h = s;
            if (h.StartsWith("&#x", StringComparison.OrdinalIgnoreCase)) h = h.Substring(3).TrimEnd(';');
            else if (h.StartsWith("U+", StringComparison.OrdinalIgnoreCase)) h = h.Substring(2);
            else if (h.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) h = h.Substring(2);
            else if (h.StartsWith("\\u", StringComparison.OrdinalIgnoreCase)) h = h.Substring(2);

            if (int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code) && code > 0)
            {
                try { return char.ConvertFromUtf32(code); } catch { /* out of range - fall through */ }
            }
            return s; // not a hex code - use verbatim
        }
    }

    /// <summary>
    /// One plugin's view of Wisp's appearance engine. Recolours the accent, registers/applies full themes
    /// (persisting the user's choice), or merges a raw ResourceDictionary. All work is marshalled onto the
    /// UI thread. The <see cref="ThemeChanged"/> forwarder is detached on unload (see <see cref="Detach"/>)
    /// so a disabled plugin doesn't leak a subscription onto the static <see cref="ThemeManager"/> event.
    /// </summary>
    internal sealed class HostTheming : IWispTheming
    {
        private readonly IHostBridge _bridge;
        private readonly string _pluginId;
        private readonly Action _forwarder;

        public HostTheming(IHostBridge bridge, string pluginId)
        {
            _bridge = bridge;
            _pluginId = pluginId;
            _forwarder = () =>
            {
                var h = ThemeChanged;
                if (h != null) _bridge.Dispatcher.BeginInvoke(new Action(() => h(this, EventArgs.Empty)));
            };
            ThemeManager.AccentChanged += _forwarder;
        }

        public event EventHandler? ThemeChanged;

        public string CurrentAccentHex => ThemeManager.ToHex(ThemeManager.AccentColor);
        public string? ActiveThemeId => ThemeManager.ActiveThemeId;

        public void SetAccent(string hex) => OnUi(() =>
        {
            ThemeManager.ApplyAccentHex(hex);
            _bridge.Settings.AccentColorHex = hex ?? "";
            _bridge.Settings.ActiveThemeId = "";
            Save();
        });

        public void RegisterTheme(WispTheme theme) => OnUi(() =>
        {
            if (theme == null || string.IsNullOrWhiteSpace(theme.Id)) return;
            var def = Map(theme, _pluginId);
            ThemeManager.RegisterTheme(def);
            // If the user had this theme selected last session, apply it now that it exists.
            if (string.Equals(def.Id, _bridge.Settings.ActiveThemeId, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(ThemeManager.ActiveThemeId, def.Id, StringComparison.OrdinalIgnoreCase))
            {
                ThemeManager.ApplyThemeById(def.Id);
            }
        });

        public void UnregisterTheme(string themeId) => OnUi(() =>
        {
            ThemeManager.UnregisterTheme(themeId);
            if (string.Equals(_bridge.Settings.ActiveThemeId, themeId, StringComparison.OrdinalIgnoreCase))
            {
                _bridge.Settings.ActiveThemeId = "";
                Save();
            }
        });

        public void ApplyTheme(string themeId) => OnUi(() =>
        {
            ThemeManager.ApplyThemeById(themeId);
            _bridge.Settings.ActiveThemeId = ThemeManager.ActiveThemeId ?? "";
            Save();
        });

        public void ResetToDefault() => OnUi(() =>
        {
            _bridge.Settings.ActiveThemeId = "";
            Save();
            ThemeManager.Apply(_bridge.Settings); // default palette + the user's saved accent
        });

        public IReadOnlyList<WispThemeInfo> GetThemes()
            => ThemeManager.GetThemes()
                .Select(d => new WispThemeInfo(d.Id, d.Name, d.IsBuiltIn, d.IsDark))
                .ToList();

        public void ApplyRawResources(object resourceDictionary) => OnUi(() =>
        {
            if (resourceDictionary is ResourceDictionary dict) ThemeManager.ApplyRawResources(dict);
            else Logger.Warn($"[Plugin:{_pluginId}] ApplyRawResources expected a System.Windows.ResourceDictionary.");
        });

        /// <summary>Detaches the static-event forwarder so the plugin can be unloaded cleanly.</summary>
        internal void Detach() => ThemeManager.AccentChanged -= _forwarder;

        private void OnUi(Action a)
        {
            if (_bridge.Dispatcher.CheckAccess()) a();
            else _bridge.Dispatcher.BeginInvoke(a);
        }

        private void Save()
        {
            try { _bridge.Settings.Save(); }
            catch (Exception ex) { Logger.Warn($"[Plugin:{_pluginId}] settings save failed: {ex.Message}"); }
        }

        private static ThemeDefinition Map(WispTheme t, string owner) => new()
        {
            Id = t.Id, Name = t.Name, IsDark = t.IsDark, IsBuiltIn = false, OwnerPluginId = owner,
            Accent = t.Accent, AccentHover = t.AccentHover, Background = t.Background, Well = t.Well,
            Surface = t.Surface, SurfaceHover = t.SurfaceHover, SurfaceRaised = t.SurfaceRaised,
            PanelBorder = t.PanelBorder, BorderStrong = t.BorderStrong, TextPrimary = t.TextPrimary,
            TextMuted = t.TextMuted, Success = t.Success, Warning = t.Warning, Error = t.Error,
            DisplayFont = t.DisplayFont, MonoFont = t.MonoFont,
        };
    }

    internal sealed class HostUiBridge : IUiBridge
    {
        private readonly Dispatcher _dispatcher;
        private readonly IHostBridge _bridge;
        private readonly string _pluginId;

        public HostUiBridge(IHostBridge bridge, string pluginId)
        {
            _bridge = bridge;
            _pluginId = pluginId;
            _dispatcher = bridge.Dispatcher;
        }

        public bool IsUiThread => _dispatcher.CheckAccess();

        public void RunOnUiThread(Action action)
        {
            if (_dispatcher.CheckAccess()) action();
            else _dispatcher.BeginInvoke(action);
        }

        public Task InvokeAsync(Action action) => _dispatcher.InvokeAsync(action).Task;
        public Task<T> InvokeAsync<T>(Func<T> func) => _dispatcher.InvokeAsync(func).Task;

        public object MainWindow => System.Windows.Application.Current.MainWindow;

        public void AddSidebarTab(string id, string title, string iconHex, object content)
        {
            RunOnUiThread(() =>
            {
                if (System.Windows.Application.Current.MainWindow is not MainWindow mw) return;
                if (content is not System.Windows.UIElement uiContent) return;

                var tab = new System.Windows.Controls.Border
                {
                    Style = (System.Windows.Style)mw.FindResource("SidebarTabStyle"),
                    Tag = ""
                };

                var grid = new System.Windows.Controls.Grid();
                grid.Children.Add(new System.Windows.Controls.Border { Style = (System.Windows.Style)mw.FindResource("SidebarTabSelectedOverlay") });
                grid.Children.Add(new System.Windows.Controls.Border { Style = (System.Windows.Style)mw.FindResource("SidebarTabHoverOverlay") });

                var stack = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                stack.Children.Add(new System.Windows.Controls.TextBlock { Text = GlyphUtil.ToGlyph(iconHex), Style = (System.Windows.Style)mw.FindResource("SidebarTabIconStyle") });
                stack.Children.Add(new System.Windows.Controls.TextBlock { Text = title.ToUpperInvariant(), Style = (System.Windows.Style)mw.FindResource("SidebarTabTextStyle") });

                grid.Children.Add(stack);
                tab.Child = grid;

                tab.MouseDown += (s, e) =>
                {
                    foreach (var child in mw.SidebarPanel.Children)
                    {
                        if (child is System.Windows.Controls.Border b) b.Tag = "";
                    }
                    tab.Tag = "Selected";

                    foreach (var child in mw.MainContentContainer.Children)
                    {
                        if (child is System.Windows.UIElement ui) ui.Visibility = System.Windows.Visibility.Collapsed;
                    }
                    uiContent.Visibility = System.Windows.Visibility.Visible;
                };

                mw.SidebarPanel.Children.Add(tab);

                uiContent.Visibility = System.Windows.Visibility.Collapsed;
                if (uiContent is System.Windows.FrameworkElement fw)
                {
                    fw.Margin = new System.Windows.Thickness(20, 15, 20, 15);
                }
                mw.MainContentContainer.Children.Add(uiContent);
            });
        }

        public void AddSettingsCategory(string title, object content)
        {
            RunOnUiThread(() =>
            {
                if (System.Windows.Application.Current.MainWindow is not MainWindow mw) return;
                if (content is not System.Windows.UIElement uiContent) return;

                var container = new System.Windows.Controls.Border
                {
                    Background = (System.Windows.Media.Brush)mw.FindResource("SurfaceBrush"),
                    BorderBrush = (System.Windows.Media.Brush)mw.FindResource("PanelBorderBrush"),
                    BorderThickness = new System.Windows.Thickness(1),
                    CornerRadius = new System.Windows.CornerRadius(8),
                    Padding = new System.Windows.Thickness(20),
                    Margin = new System.Windows.Thickness(0, 0, 0, 15)
                };

                var panel = new System.Windows.Controls.StackPanel();

                var headerPanel = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new System.Windows.Thickness(0, 0, 0, 15) };
                headerPanel.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = "",
                    FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                    Foreground = (System.Windows.Media.Brush)mw.FindResource("AccentBrush"),
                    FontWeight = System.Windows.FontWeights.Bold,
                    FontSize = 13,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center,
                    Margin = new System.Windows.Thickness(0, 0, 8, 0)
                });
                headerPanel.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = title,
                    Foreground = (System.Windows.Media.Brush)mw.FindResource("AccentBrush"),
                    FontWeight = System.Windows.FontWeights.Bold,
                    FontSize = 13,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center
                });

                panel.Children.Add(headerPanel);
                panel.Children.Add(uiContent);
                container.Child = panel;

                mw.SettingsStackPanel.Children.Add(container);
            });
        }

        public void ShowCustomDialog(string title, object content, double width = 400, double height = 400)
        {
            RunOnUiThread(() =>
            {
                var dialog = new System.Windows.Window
                {
                    Title = title,
                    Width = width,
                    Height = height,
                    WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
                    Owner = System.Windows.Application.Current.MainWindow,
                    Content = content,
                    Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#121414")),
                    Foreground = System.Windows.Media.Brushes.White,
                    ShowInTaskbar = false
                };
                dialog.ShowDialog();
            });
        }

        public void ShowToast(string title, string? message = null, ToastKind kind = ToastKind.Info)
            => _bridge.ShowToast(title, message, kind);

        public void RegisterClipAction(ClipAction action) => _bridge.RegisterClipAction(_pluginId, action);
        public void UnregisterClipAction(string actionId) => _bridge.UnregisterClipAction(_pluginId, actionId);

        public void AddTrayMenuItem(string id, string label, Action onClick)
            => _bridge.AddTrayMenuItem(_pluginId, id, label, onClick);
        public void RemoveTrayMenuItem(string id) => _bridge.RemoveTrayMenuItem(_pluginId, id);
    }

    /// <summary>
    /// One plugin's view of the clip player. Reads/seeks/contributions all go through the bridge, which
    /// marshals onto the UI thread; buttons and markers are scoped to this plugin's id so cleanup on
    /// disable is exact. <see cref="ClipOpened"/>/<see cref="Closed"/> are raised by the manager on the UI
    /// thread (see PluginManager.RaisePlayerOpened/Closed), each handler isolated so one bad handler is
    /// logged rather than breaking the others.
    /// </summary>
    internal sealed class HostPlayer : IWispPlayer
    {
        private readonly IHostBridge _bridge;
        private readonly string _pluginId;
        private readonly IWispLog _log;

        public HostPlayer(IHostBridge bridge, string pluginId, IWispLog log)
        {
            _bridge = bridge;
            _pluginId = pluginId;
            _log = log;
        }

        public bool IsOpen => _bridge.PlayerIsOpen;

        public ClipInfo? CurrentClip
        {
            get { var c = _bridge.PlayerCurrentClip; return c != null ? PluginMap.ToClipInfo(c) : null; }
        }

        public double PositionSeconds => _bridge.PlayerPositionSeconds;
        public double DurationSeconds => _bridge.PlayerDurationSeconds;
        public bool IsPlaying => _bridge.PlayerIsPlaying;

        public void Seek(double seconds) => _bridge.PlayerSeek(seconds);
        public void Play() => _bridge.PlayerPlay();
        public void Pause() => _bridge.PlayerPause();

        public void AddButton(PlayerButton button)
        {
            if (button != null) _bridge.AddPlayerButton(_pluginId, button);
        }

        public void RemoveButton(string buttonId) => _bridge.RemovePlayerButton(_pluginId, buttonId);

        public void SetMarkers(IEnumerable<TimelineMarker> markers)
            => _bridge.SetPlayerMarkers(_pluginId,
                   markers?.Where(m => m != null).ToList() ?? new List<TimelineMarker>());

        public void ClearMarkers() => _bridge.ClearPlayerMarkers(_pluginId);

        public void AddOverlay(PlayerOverlay overlay)
        {
            if (overlay != null) _bridge.AddPlayerOverlay(_pluginId, overlay);
        }

        public void RemoveOverlay(string overlayId) => _bridge.RemovePlayerOverlay(_pluginId, overlayId);

        public void ClearOverlays() => _bridge.ClearPlayerOverlays(_pluginId);

        public event EventHandler<PlayerClipEventArgs>? ClipOpened;
        public event EventHandler? Closed;

        internal void RaiseOpened(ClipInfo clip)
        {
            var handlers = ClipOpened;
            if (handlers == null) return;
            var args = new PlayerClipEventArgs(clip);
            foreach (var d in handlers.GetInvocationList())
            {
                try { ((EventHandler<PlayerClipEventArgs>)d).Invoke(this, args); }
                catch (Exception ex) { _log.Error("Player ClipOpened handler threw.", ex); }
            }
        }

        internal void RaiseClosed()
        {
            var handlers = Closed;
            if (handlers == null) return;
            foreach (var d in handlers.GetInvocationList())
            {
                try { ((EventHandler)d).Invoke(this, EventArgs.Empty); }
                catch (Exception ex) { _log.Error("Player Closed handler threw.", ex); }
            }
        }
    }
}
