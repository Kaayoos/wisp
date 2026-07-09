using System;

namespace Wisp.Plugins.Player
{
    /// <summary>
    /// A button a plugin adds to the clip player's control bar (next to play/pause/export). Use it for a
    /// per-clip tool - "Add marker", "Screenshot frame", "Loop section", "Send to Discord", etc. The
    /// button is visible whenever the player is open; Wisp removes it automatically when your plugin is
    /// disabled.
    /// </summary>
    public sealed class PlayerButton
    {
        /// <param name="id">Stable id, unique within your plugin. Re-adding the same id replaces the button.</param>
        /// <param name="label">Text on the button (keep it short). May be "" for an icon-only button.</param>
        /// <param name="onClick">Invoked on the UI thread when clicked. Read <see cref="IWispPlayer.PositionSeconds"/>
        /// etc. here. Keep it quick; throwing is caught and logged, it won't crash Wisp.</param>
        public PlayerButton(string id, string label, Action onClick)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Label = label ?? "";
            OnClick = onClick ?? throw new ArgumentNullException(nameof(onClick));
        }

        /// <summary>Stable id, unique within your plugin.</summary>
        public string Id { get; }

        /// <summary>Button caption ("" for icon-only).</summary>
        public string Label { get; }

        /// <summary>Runs on the UI thread when the button is clicked.</summary>
        public Action OnClick { get; }

        /// <summary>
        /// Optional Segoe MDL2 Assets icon shown before the label: the glyph itself ("") or a hex code
        /// ("E8A1", "0xE8A1", "&amp;#xE8A1;"). Wisp normalises any of these to the right glyph.
        /// </summary>
        public string? IconGlyph { get; init; }

        /// <summary>Optional tooltip shown on hover.</summary>
        public string? Tooltip { get; init; }
    }
}
