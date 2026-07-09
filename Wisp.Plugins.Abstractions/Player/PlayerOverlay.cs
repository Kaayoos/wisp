using System;

namespace Wisp.Plugins.Player
{
    /// <summary>
    /// A free-floating visual layer a plugin places ON TOP of the clip video in the player. This is the
    /// generic "draw your own thing over the picture" surface: the host hosts your <see cref="Content"/>,
    /// positions it, and (optionally) lets the user drag/resize it - it knows nothing about what the content
    /// <em>is</em>. A picture-in-picture webcam is the obvious use, but the same primitive powers a stats
    /// HUD, live subtitles, a drawing/telestration layer, a reaction GIF, a watermark preview, and more.
    ///
    /// Geometry is NORMALISED to the displayed video rectangle: <see cref="X"/>/<see cref="Y"/>/
    /// <see cref="Width"/>/<see cref="Height"/> are fractions in 0..1 of the actual (letterbox-corrected)
    /// video area, not pixels. That way an overlay keeps its place as the window resizes, and the very same
    /// rectangle can be handed to <see cref="Wisp.Plugins.Export.ExportLayer"/> to burn it into an export at
    /// the spot the user sees.
    ///
    /// THREADING: create and mutate overlays on the UI thread (see <see cref="IUiBridge.RunOnUiThread"/>),
    /// the same as any WPF object - <see cref="Content"/> is a WPF element you build. The host adds your
    /// overlays when the player is open and removes them all automatically when your plugin is disabled.
    /// </summary>
    public sealed class PlayerOverlay
    {
        /// <param name="id">Stable id, unique within your plugin. Re-adding the same id replaces the overlay.</param>
        /// <param name="content">The visual to host - a WPF <c>FrameworkElement</c> (e.g. a MediaElement, Image,
        /// Border or a whole UserControl). Typed as <see cref="object"/> so the SDK stays free of a WPF
        /// dependency; the host casts it. Passing a non-WPF object is ignored and logged.</param>
        public PlayerOverlay(string id, object content)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Content = content ?? throw new ArgumentNullException(nameof(content));
        }

        /// <summary>Stable id, unique within your plugin.</summary>
        public string Id { get; }

        /// <summary>The WPF element to render over the video (host-cast from <see cref="object"/>).</summary>
        public object Content { get; }

        /// <summary>Left edge, as a fraction (0..1) of the displayed video width. Default 0.02.</summary>
        public double X { get; init; } = 0.02;

        /// <summary>Top edge, as a fraction (0..1) of the displayed video height. Default 0.02.</summary>
        public double Y { get; init; } = 0.02;

        /// <summary>Width, as a fraction (0..1) of the displayed video width. Default 0.25.</summary>
        public double Width { get; init; } = 0.25;

        /// <summary>Height, as a fraction (0..1) of the displayed video height. Default 0.25.</summary>
        public double Height { get; init; } = 0.25;

        /// <summary>When true, the user can drag the overlay around the video. Default true.</summary>
        public bool Movable { get; init; } = true;

        /// <summary>When true, the user can resize the overlay from its corner. Default false.</summary>
        public bool Resizable { get; init; } = false;

        /// <summary>Stacking order among overlays (higher is in front). Default 0.</summary>
        public int ZIndex { get; init; }

        /// <summary>Overall opacity of the overlay, 0..1. Default 1.0.</summary>
        public double Opacity { get; init; } = 1.0;

        /// <summary>
        /// Raised on the UI thread after the user moves or resizes the overlay, with the new NORMALISED
        /// rectangle. Persist it (e.g. via <see cref="IPluginStorage"/>) and pass it back as the overlay's
        /// position next time - and as the <see cref="Wisp.Plugins.Export.ExportLayer"/> rect - so the player
        /// preview and the export agree. null = you don't care where it ends up.
        /// </summary>
        public Action<PlayerOverlayRect>? OnRectChanged { get; init; }
    }

    /// <summary>A normalised (0..1, relative to the displayed video) overlay rectangle.</summary>
    public readonly record struct PlayerOverlayRect(double X, double Y, double Width, double Height);
}
