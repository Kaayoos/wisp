namespace Wisp.Plugins.UI
{
    /// <summary>
    /// Severity of a toast shown via <see cref="IUiBridge.ShowToast"/>. Controls the icon and colour of
    /// the native Wisp toast.
    /// </summary>
    public enum ToastKind
    {
        /// <summary>Neutral information. Tinted with the user's accent colour.</summary>
        Info,

        /// <summary>A positive result (saved, uploaded, done).</summary>
        Success,

        /// <summary>A caution the user should notice but that isn't a failure.</summary>
        Warning,

        /// <summary>Something failed.</summary>
        Error
    }
}
