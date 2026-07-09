using System;

namespace Wisp.Plugins.UI
{
    /// <summary>
    /// A command a plugin contributes to a clip - it appears in the right-click context menu of every
    /// clip in the library (e.g. "Upload", "Open in editor", "Trim", "Copy share link"). Register it with
    /// <see cref="IUiBridge.RegisterClipAction"/> in <see cref="IWispPlugin.OnEnabled"/>; remove it in
    /// <see cref="IWispPlugin.OnDisabled"/> (or let Wisp remove it for you when the plugin is disabled).
    ///
    /// <see cref="Invoke"/> runs on the UI thread when the user clicks the item. Keep it snappy - kick
    /// heavy work (uploads, transcodes) onto a background thread yourself. A throw is caught, logged and
    /// surfaced to the user as a toast; it never crashes Wisp.
    /// </summary>
    public sealed class ClipAction
    {
        /// <param name="id">Stable unique id for this action (per plugin), e.g. "upload".</param>
        /// <param name="label">Menu text shown to the user, e.g. "Upload to server".</param>
        /// <param name="invoke">Runs (on the UI thread) with the targeted clip when the user clicks.</param>
        public ClipAction(string id, string label, Action<ClipInfo> invoke)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Id is required.", nameof(id));
            if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("Label is required.", nameof(label));
            Id = id;
            Label = label;
            Invoke = invoke ?? throw new ArgumentNullException(nameof(invoke));
        }

        /// <summary>Stable unique id for this action.</summary>
        public string Id { get; }

        /// <summary>The menu text shown on each clip's context menu.</summary>
        public string Label { get; }

        /// <summary>
        /// Optional leading icon: a Segoe MDL2 Assets glyph. Accepts the glyph character itself ("")
        /// or a hex code ("E898", "0xE898", "&amp;#xE898;") - Wisp normalises either. Null = no icon.
        /// </summary>
        public string? IconGlyph { get; set; }

        /// <summary>
        /// Optional filter: return false to hide this action for a given clip (e.g. only show "Upload"
        /// for clips longer than 5s). Called on the UI thread as the menu opens; keep it cheap. Null = always show.
        /// </summary>
        public Func<ClipInfo, bool>? CanShow { get; set; }

        /// <summary>Invoked (on the UI thread) with the targeted clip when the user clicks the item.</summary>
        public Action<ClipInfo> Invoke { get; }
    }
}
