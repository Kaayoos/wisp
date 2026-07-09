namespace WispWebcamOverlay
{
    /// <summary>
    /// User-configurable settings, persisted via Host.Storage. The position/size is stored NORMALISED
    /// (fractions of the video rect, 0..1), the same model the player overlay and export layer use - so the
    /// spot the user drags the camera to in the player is exactly where it burns into an export.
    /// </summary>
    public class OverlaySettings
    {
        /// <summary>OpenCV camera index (0 = default webcam).</summary>
        public int CameraIndex { get; set; } = 0;

        /// <summary>Mirror the camera horizontally (selfie mode) in the preview and the export.</summary>
        public bool Mirror { get; set; } = true;

        /// <summary>Overlay opacity in the player (0.1 – 1.0). Also applied to the export layer.</summary>
        public double Opacity { get; set; } = 1.0;

        /// <summary>
        /// How many seconds of camera footage to keep buffered on disk. Set this >= your Wisp replay length
        /// so a saved clip always has matching camera footage. Bounds disk use; nothing is kept in RAM.
        /// </summary>
        public int BufferSeconds { get; set; } = 120;

        /// <summary>
        /// Fine sync nudge (milliseconds). Positive shifts the camera window earlier - increase it if the
        /// face lags the action, decrease if it leads. 0 is usually fine.
        /// </summary>
        public int SyncOffsetMs { get; set; } = 0;

        // Normalised picture-in-picture rectangle (fractions of the displayed video). Default: a square-ish
        // box parked in the bottom-right corner.
        public double PosX { get; set; } = 0.74;
        public double PosY { get; set; } = 0.70;
        public double SizeW { get; set; } = 0.24;
        public double SizeH { get; set; } = 0.28;
    }
}
