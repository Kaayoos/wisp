using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Wisp.Plugins;
using Wisp.Plugins.Player;
using Wisp.Plugins.UI;

namespace WispClipMarkers
{
    /// <summary>
    /// A complete, real sample of the Wisp v3 player API (<see cref="IWispPlayer"/>). In a long clip you can
    /// click "Add marker" to bookmark the current spot; each bookmark shows as a clickable gold flag on the
    /// timeline so you can jump back to it later. Bookmarks are saved per-clip, so they're still there the
    /// next time you open that clip.
    ///
    /// It demonstrates the whole surface end-to-end:
    ///   • <see cref="IWispPlayer.AddButton"/> - a control-bar button that reads the playhead,
    ///   • <see cref="IWispPlayer.SetMarkers"/> - painting markers (left-click jumps; right-click removes),
    ///   • <see cref="IWispPlayer.ClipOpened"/> - re-applying a clip's saved markers when it opens,
    ///   • <see cref="IPluginStorage"/> - persisting the bookmarks as JSON in the plugin's data folder.
    ///
    /// None of this needs WPF - it's pure API usage.
    /// </summary>
    public sealed class ClipMarkersPlugin : WispPluginBase
    {
        // Warm gold so user bookmarks read as distinct from the cyan chain-moment markers.
        private const string MarkerColor = "#FFC857";

        private MarkerData _data = new();

        public override string Id => "com.wisp.sample.clipmarkers";
        public override string Name => "Clip Markers (Sample)";
        public override string Version => "1.0.0";
        public override string Author => "MinimalPulse";
        public override string Description => "Sample: bookmark moments in a clip from the player and jump back to them.";

        public override void OnEnabled()
        {
            _data = Host.Storage.LoadSettings<MarkerData>() ?? new MarkerData();

            // A button on the player's control bar. Its click runs on the UI thread; we read the playhead
            // straight off Host.Player there.
            Host.Player.AddButton(new PlayerButton("add", "Add marker", AddMarkerAtPlayhead)
            {
                IconGlyph = "E7C1", // Segoe MDL2 "Flag"
                Tooltip = "Bookmark the current moment (right-click a marker to remove it)"
            });

            // Re-paint a clip's saved markers whenever it opens in the player.
            Host.Player.ClipOpened += OnClipOpened;

            // If a clip is already open the moment we're enabled, paint its markers now.
            var open = Host.Player.CurrentClip;
            if (open != null) ApplyMarkers(open.Id);

            Host.Log.Info("Clip Markers ready.");
        }

        public override void OnDisabled()
        {
            // Wisp removes a plugin's player contributions automatically on disable, but unsubscribing and
            // clearing explicitly is good hygiene (and required if you wire things up outside OnEnabled).
            Host.Player.ClipOpened -= OnClipOpened;
            Host.Player.RemoveButton("add");
            Host.Player.ClearMarkers();
        }

        private void OnClipOpened(object? sender, PlayerClipEventArgs e) => ApplyMarkers(e.Clip.Id);

        /// <summary>Marks the current playhead position on the open clip.</summary>
        private void AddMarkerAtPlayhead()
        {
            var clip = Host.Player.CurrentClip;
            if (clip == null)
            {
                Host.Ui.ShowToast("Clip Markers", "Open a clip first.", ToastKind.Info);
                return;
            }

            double pos = Host.Player.PositionSeconds;
            var list = GetList(clip.Id);

            // Don't stack markers on top of each other (within ~0.4s).
            if (list.Any(t => Math.Abs(t - pos) < 0.4))
            {
                Host.Ui.ShowToast("Clip Markers", "There's already a marker here.", ToastKind.Info);
                return;
            }

            list.Add(pos);
            list.Sort();
            Save();
            ApplyMarkers(clip.Id);
            Host.Ui.ShowToast("Marker added", $"At {Format(pos)} - {list.Count} on this clip.", ToastKind.Success);
        }

        /// <summary>Removes the marker nearest <paramref name="offset"/> on a clip (used by right-click).</summary>
        private void RemoveMarker(int clipId, double offset)
        {
            var list = GetList(clipId);
            int before = list.Count;
            list.RemoveAll(t => Math.Abs(t - offset) < 0.001);
            if (list.Count == before) return;

            Save();
            // Only repaint if this clip is the one on screen.
            if (Host.Player.CurrentClip?.Id == clipId) ApplyMarkers(clipId);
        }

        /// <summary>Pushes a clip's saved offsets to the player as numbered, clickable timeline markers.</summary>
        private void ApplyMarkers(int clipId)
        {
            var list = GetList(clipId);
            var markers = new List<TimelineMarker>(list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                double offset = list[i]; // capture per-iteration for the right-click closure
                markers.Add(new TimelineMarker(offset)
                {
                    Label = (i + 1).ToString(CultureInfo.InvariantCulture),
                    ColorHex = MarkerColor,
                    Tooltip = $"Marker {i + 1} - {Format(offset)}  (left-click: jump · right-click: remove)",
                    OnRightClick = () => RemoveMarker(clipId, offset)
                });
            }
            Host.Player.SetMarkers(markers);
        }

        private List<double> GetList(int clipId)
        {
            string key = clipId.ToString(CultureInfo.InvariantCulture);
            if (!_data.Markers.TryGetValue(key, out var list))
            {
                list = new List<double>();
                _data.Markers[key] = list;
            }
            return list;
        }

        private void Save() => Host.Storage.SaveSettings(_data);

        private static string Format(double seconds)
        {
            var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return ts.Hours > 0 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
        }
    }

    /// <summary>Persisted shape (serialized to settings.json in the plugin's data folder): clip id → offsets (s).</summary>
    public sealed class MarkerData
    {
        public Dictionary<string, List<double>> Markers { get; set; } = new();
    }
}
