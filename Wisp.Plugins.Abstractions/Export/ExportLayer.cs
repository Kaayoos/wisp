using System;

namespace Wisp.Plugins.Export
{
    /// <summary>
    /// A visual layer a plugin asks Wisp to composite INTO an exported clip - burned into the pixels of the
    /// output file. This is the generic "bake your thing onto the video" surface, the export-time companion
    /// to <see cref="Wisp.Plugins.Player.PlayerOverlay"/> (which only draws live in the player). The host
    /// knows nothing about what the layer is: a webcam picture-in-picture, a logo/watermark, a facecam, a
    /// telestration track, or anything else that's a video or image file.
    ///
    /// Return layers from <see cref="IWispPlugin.GetExportLayers"/>; Wisp draws them, in list order, over
    /// the clip when the user exports with overlay burn-in enabled.
    ///
    /// Geometry is NORMALISED to the exported video frame (fractions 0..1), the same model as
    /// <see cref="Wisp.Plugins.Player.PlayerOverlay"/> - so handing back the rectangle the user positioned
    /// in the player reproduces exactly what they saw.
    /// </summary>
    public sealed class ExportLayer
    {
        /// <param name="sourcePath">Absolute path to the image or video file to composite over the clip.</param>
        public ExportLayer(string sourcePath)
        {
            SourcePath = sourcePath ?? throw new ArgumentNullException(nameof(sourcePath));
        }

        /// <summary>Absolute path to the image or video file to draw over the clip.</summary>
        public string SourcePath { get; }

        /// <summary>Left edge, as a fraction (0..1) of the output video width. Default 0.02.</summary>
        public double X { get; init; } = 0.02;

        /// <summary>Top edge, as a fraction (0..1) of the output video height. Default 0.02.</summary>
        public double Y { get; init; } = 0.02;

        /// <summary>Width, as a fraction (0..1) of the output video width. Default 0.25.</summary>
        public double Width { get; init; } = 0.25;

        /// <summary>
        /// Height, as a fraction (0..1) of the output video height. Default 0.25. Use 0 to keep the source's
        /// own aspect ratio relative to the scaled <see cref="Width"/>.
        /// </summary>
        public double Height { get; init; } = 0.25;

        /// <summary>Layer opacity, 0..1. Default 1.0 (fully opaque).</summary>
        public double Opacity { get; init; } = 1.0;

        /// <summary>Horizontally flip the layer (e.g. a mirrored "selfie" webcam). Default false.</summary>
        public bool Mirror { get; init; }
    }
}
