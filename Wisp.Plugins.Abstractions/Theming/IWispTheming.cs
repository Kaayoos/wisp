using System;
using System.Collections.Generic;

namespace Wisp.Plugins.Theming
{
    /// <summary>
    /// Wisp's appearance engine, exposed to plugins via <see cref="IWispHost.Theming"/>. Three levels of
    /// control, smallest to largest:
    ///
    ///   1. <see cref="SetAccent"/> - recolour just the signature accent, live.
    ///   2. <see cref="RegisterTheme"/> - ship full named themes that restyle every surface + the fonts.
    ///      Registered themes show up in Settings → Appearance for the <i>user</i> to pick (and the
    ///      choice persists across restarts - re-register on <see cref="IWispPlugin.OnEnabled"/> and Wisp
    ///      re-applies it). Use <see cref="ApplyTheme"/> to switch programmatically.
    ///   3. <see cref="ApplyRawResources"/> - the escape hatch: merge an arbitrary WPF
    ///      <c>ResourceDictionary</c> over the app's resources for total control (custom control
    ///      templates, gradients, anything). Powerful and unguarded - you own the result.
    ///
    /// All methods are safe to call from any thread; Wisp marshals UI work for you. Changes apply live
    /// across the whole app (Wisp's UI binds the palette via dynamic resources).
    /// </summary>
    public interface IWispTheming
    {
        /// <summary>The accent currently in effect, as "#RRGGBB".</summary>
        string CurrentAccentHex { get; }

        /// <summary>The id of the active full theme, or null if Wisp is on a plain accent (no full theme).</summary>
        string? ActiveThemeId { get; }

        /// <summary>Raised after the theme or accent changes, so you can recolour any custom UI you own.</summary>
        event EventHandler? ThemeChanged;

        /// <summary>Recolours the accent immediately (and persists it). Accepts "#RRGGBB"/"#AARRGGBB".</summary>
        void SetAccent(string hex);

        /// <summary>
        /// Registers (or replaces, by <see cref="WispTheme.Id"/>) a full theme so the user can select it
        /// in Settings → Appearance. Does not switch to it - call <see cref="ApplyTheme"/> for that, or
        /// let the user pick. Re-register your themes in <see cref="IWispPlugin.OnEnabled"/> so a theme the
        /// user selected last session is available again on startup.
        /// </summary>
        void RegisterTheme(WispTheme theme);

        /// <summary>
        /// Removes a previously registered theme. If it was the active theme, Wisp reverts to its default
        /// look. Wisp also unregisters a plugin's themes automatically when it is disabled.
        /// </summary>
        void UnregisterTheme(string themeId);

        /// <summary>Applies a registered theme by id (built-in or plugin) and persists the choice.</summary>
        void ApplyTheme(string themeId);

        /// <summary>Reverts to Wisp's default dark theme with the user's chosen accent.</summary>
        void ResetToDefault();

        /// <summary>Every theme Wisp currently knows about (built-ins first, then registered plugin themes).</summary>
        IReadOnlyList<WispThemeInfo> GetThemes();

        /// <summary>
        /// The escape hatch. Merges a WPF <c>System.Windows.ResourceDictionary</c> over the application's
        /// resources, live. Pass the dictionary as <see cref="object"/> (the SDK has no WPF dependency);
        /// Wisp casts it. Any keys you set override Wisp's - including brushes, colours, and control
        /// templates. Wisp does not validate the result, so test it; a bad merge can make the UI unusable
        /// until the override is cleared by switching themes.
        /// </summary>
        /// <param name="resourceDictionary">A <c>System.Windows.ResourceDictionary</c> instance.</param>
        void ApplyRawResources(object resourceDictionary);
    }
}
