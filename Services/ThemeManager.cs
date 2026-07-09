using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Wisp.Models;

namespace Wisp.Services
{
    /// <summary>
    /// Owns the app's look. Wisp's whole identity is its appearance, so this is user-pickable at two
    /// levels: a curated/custom <b>accent</b> (the signature colour), and full <b>themes</b> that restyle
    /// every surface plus the type families. Plugins extend both - see Services/Plugins/HostTheming.
    ///
    /// How re-theming works: XAML references palette tokens via {DynamicResource ...} and this manager
    /// overwrites those entries in Application.Resources, so every DynamicResource consumer re-renders.
    /// Spots that can't use DynamicResource (code-behind brushes, ColorAnimation targets) read the static
    /// <see cref="AccentColor"/> and subscribe to <see cref="AccentChanged"/>.
    ///
    /// Because the RESOURCES are replaced (not mutated), all palette references in XAML must use
    /// DynamicResource - a StaticResource would capture the old brush and never update. Every theme is
    /// applied as an overlay on <see cref="Default"/>, so switching themes never leaves stale surfaces.
    /// </summary>
    public static class ThemeManager
    {
        /// <summary>Raised after the accent or theme changes so code-behind/animation consumers refresh.</summary>
        public static event Action? AccentChanged;

        /// <summary>Raised when the set of registered themes changes (a plugin added/removed one).</summary>
        public static event Action? ThemesChanged;

        public const string DefaultAccentHex = "#00F2FF";
        public const string DefaultThemeId = "wisp.default";

        public static Color AccentColor { get; private set; } = Parse(DefaultAccentHex);
        public static Color AccentHoverColor { get; private set; } = Lighten(Parse(DefaultAccentHex), 0.45);

        /// <summary>The id of the active full theme, or null when on a plain accent (the default look).</summary>
        public static string? ActiveThemeId { get; private set; }

        /// <summary>A fresh (unfrozen) accent brush for code-behind that needs to assign one directly.</summary>
        public static SolidColorBrush AccentBrush => new SolidColorBrush(AccentColor);

        /// <summary>Curated accent palettes. Name shown in the UI, value is the accent hex.</summary>
        public static readonly (string Name, string Hex)[] Presets =
        {
            ("Wisp Cyan",   "#00F2FF"),
            ("Wisp Purple", "#B98CFF"),
            ("Ember",       "#FF7A45"),
            ("Mint",        "#34D399"),
            ("Crimson",     "#FF4D6D"),
            ("Gold",        "#FFC53D"),
        };

        /// <summary>
        /// Wisp's stock palette - the exact values from Themes/Palette.xaml, expressed in code so any
        /// theme (built-in or plugin) is applied as an overlay on top of these. Whatever a theme leaves
        /// null falls back to the matching slot here.
        /// </summary>
        public static readonly ThemeDefinition Default = new()
        {
            Id = DefaultThemeId,
            Name = "Wisp Dark",
            IsDark = true,
            IsBuiltIn = true,
            Accent = DefaultAccentHex,
            AccentHover = "#7DF4FF",
            Background = "#121414",
            Well = "#0C0E0E",
            Surface = "#1E2020",
            SurfaceHover = "#282A2B",
            SurfaceRaised = "#24282A",
            PanelBorder = "#3B494B",
            BorderStrong = "#4C6063",
            TextPrimary = "#E2E2E2",
            TextMuted = "#B9CACB",
            Success = "#34D399",
            Warning = "#FFC53D",
            Error = "#FF4D4D",
            DisplayFont = "Bahnschrift, Segoe UI Semibold, Segoe UI",
            MonoFont = "Cascadia Mono, Consolas",
        };

        // Extra dark themes Wisp ships, to show off (and exercise) full-palette theming.
        private static readonly ThemeDefinition Midnight = new()
        {
            Id = "wisp.midnight", Name = "Midnight", IsDark = true, IsBuiltIn = true,
            Accent = "#B98CFF", Background = "#0E0B1A", Well = "#08060F", Surface = "#1A1530",
            SurfaceHover = "#241D40", SurfaceRaised = "#201A38", PanelBorder = "#3A2F5C",
            BorderStrong = "#50447A", TextPrimary = "#ECE8FF", TextMuted = "#B9AEDC",
        };

        private static readonly ThemeDefinition Carbon = new()
        {
            Id = "wisp.carbon", Name = "Carbon", IsDark = true, IsBuiltIn = true,
            Accent = "#FF7A45", Background = "#0D0D0D", Well = "#070707", Surface = "#1A1A1A",
            SurfaceHover = "#242424", SurfaceRaised = "#1F1F1F", PanelBorder = "#333333",
            BorderStrong = "#4A4A4A", TextPrimary = "#ECECEC", TextMuted = "#A8A8A8",
        };

        private static readonly object _lock = new();
        private static readonly Dictionary<string, ThemeDefinition> _themes =
            new(StringComparer.OrdinalIgnoreCase)
            {
                [Default.Id] = Default,
                [Midnight.Id] = Midnight,
                [Carbon.Id] = Carbon,
            };

        // ───────────────────────────── accent (existing surface) ─────────────────────────────

        /// <summary>
        /// Resolves the accent hex from settings: a valid custom color wins, else the named preset,
        /// else the default. Keeps the precedence in one place for the settings UI and startup alike.
        /// </summary>
        public static string ResolveAccentHex(AppSettings settings)
        {
            if (!string.IsNullOrWhiteSpace(settings.AccentColorHex) && TryParse(settings.AccentColorHex, out _))
                return settings.AccentColorHex.Trim();

            var preset = Presets.FirstOrDefault(p => string.Equals(p.Name, settings.ThemePreset, StringComparison.OrdinalIgnoreCase));
            return preset.Hex ?? DefaultAccentHex;
        }

        /// <summary>
        /// Applies the look resolved from settings. If a full theme is selected (and known), it wins;
        /// otherwise the default palette with the resolved accent. Safe to call before windows exist.
        /// </summary>
        public static void Apply(AppSettings settings)
        {
            if (!string.IsNullOrWhiteSpace(settings.ActiveThemeId) &&
                !string.Equals(settings.ActiveThemeId, DefaultThemeId, StringComparison.OrdinalIgnoreCase))
            {
                lock (_lock)
                {
                    if (_themes.TryGetValue(settings.ActiveThemeId!, out var def))
                    {
                        ApplyInternal(def, def.Id);
                        return;
                    }
                }
                // Selected theme isn't registered yet (e.g. a plugin theme not loaded). Fall back to the
                // accent; HostTheming re-applies the theme the moment its plugin registers it.
            }
            ApplyAccentHex(ResolveAccentHex(settings));
        }

        /// <summary>Applies an accent hex immediately on the default palette (used for live preview + presets).</summary>
        public static void ApplyAccentHex(string hex)
        {
            if (!TryParse(hex, out Color accent)) return;
            ApplyInternal(DefaultWithAccent(accent), null);
        }

        // ───────────────────────────── theme registry ─────────────────────────────

        /// <summary>Registers or replaces a theme (by id). Built-ins are seeded already.</summary>
        public static void RegisterTheme(ThemeDefinition theme)
        {
            if (string.IsNullOrWhiteSpace(theme.Id)) return;
            lock (_lock) _themes[theme.Id] = theme;
            ThemesChanged?.Invoke();
        }

        /// <summary>Removes a registered theme by id (built-ins are protected).</summary>
        public static void UnregisterTheme(string themeId)
        {
            bool changed, wasActive = false;
            lock (_lock)
            {
                changed = _themes.TryGetValue(themeId, out var def) && !def.IsBuiltIn && _themes.Remove(themeId);
                if (changed && string.Equals(ActiveThemeId, themeId, StringComparison.OrdinalIgnoreCase))
                {
                    wasActive = true;
                    ActiveThemeId = null;
                }
            }
            if (wasActive) ApplyInternal(DefaultWithAccent(AccentColor), null);
            if (changed) ThemesChanged?.Invoke();
        }

        /// <summary>
        /// Drops every theme a plugin registered (called when the plugin is disabled). Returns true if the
        /// active theme was one of them - the caller should then re-apply the user's saved accent.
        /// </summary>
        public static bool UnregisterThemesOf(string pluginId)
        {
            bool changed = false, activeRemoved = false;
            lock (_lock)
            {
                var owned = _themes.Values.Where(t => string.Equals(t.OwnerPluginId, pluginId, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var t in owned)
                {
                    _themes.Remove(t.Id);
                    changed = true;
                    if (string.Equals(ActiveThemeId, t.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        activeRemoved = true;
                        ActiveThemeId = null;
                    }
                }
            }
            if (changed) ThemesChanged?.Invoke();
            return activeRemoved;
        }

        /// <summary>All known themes, built-ins first, then plugin themes alphabetically.</summary>
        public static IReadOnlyList<ThemeDefinition> GetThemes()
        {
            lock (_lock)
                return _themes.Values
                    .OrderByDescending(t => t.IsBuiltIn)
                    .ThenBy(t => t.IsBuiltIn ? 0 : 1)
                    .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
        }

        public static bool TryGetTheme(string id, out ThemeDefinition theme)
        {
            lock (_lock) return _themes.TryGetValue(id, out theme!);
        }

        /// <summary>Applies a registered theme by id and records it as active (default id = accent-only).</summary>
        public static void ApplyThemeById(string id)
        {
            ThemeDefinition def;
            lock (_lock)
            {
                if (!_themes.TryGetValue(id, out def!)) return;
            }

            if (string.Equals(id, DefaultThemeId, StringComparison.OrdinalIgnoreCase))
                ApplyInternal(DefaultWithAccent(AccentColor), null); // keep the live accent
            else
                ApplyInternal(def, def.Id);
        }

        /// <summary>Reverts to the default palette, keeping the current accent. Clears the active theme.</summary>
        public static void ResetToDefault() => ApplyInternal(DefaultWithAccent(AccentColor), null);

        // ───────────────────────────── raw escape hatch ─────────────────────────────

        /// <summary>
        /// Merges an arbitrary ResourceDictionary's keys directly over Application.Resources (overriding
        /// even directly-defined keys, which a MergedDictionaries add would not). For plugin power users.
        /// </summary>
        public static void ApplyRawResources(ResourceDictionary dict)
        {
            var res = Application.Current?.Resources;
            if (res == null || dict == null) return;
            foreach (var key in dict.Keys)
            {
                try { res[key] = dict[key]; } catch { /* skip a bad entry rather than abort */ }
            }
            AccentChanged?.Invoke();
        }

        // ───────────────────────────── core writer ─────────────────────────────

        /// <summary>
        /// The stock <see cref="Default"/> palette re-pointed to an arbitrary accent. Critically it DROPS
        /// Default's hand-tuned cyan hover (<c>AccentHover = "#7DF4FF"</c>) for any non-stock accent, so
        /// <see cref="ApplyInternal"/> derives the hover from the chosen accent (Lighten) instead of leaving
        /// it cyan. Without this, an accent-only look (the "Default" theme) kept the fixed cyan hover and
        /// every hover state - the setup-wizard buttons, Save Configuration, the library Play button, the
        /// sidebar tab icons - stayed cyan no matter which accent was picked. Full themes (Midnight/Carbon)
        /// never hit this because they leave AccentHover null already, which is why switching theme "fixed"
        /// it. When the accent IS the stock cyan we return Default untouched so its curated hover is exact.
        /// </summary>
        private static ThemeDefinition DefaultWithAccent(Color accent)
            => string.Equals(ToHex(accent), Default.Accent, StringComparison.OrdinalIgnoreCase)
                ? Default
                : Default with { Accent = ToHex(accent), AccentHover = null };

        /// <summary>Resolves <paramref name="def"/> over <see cref="Default"/> and writes the whole palette.</summary>
        private static void ApplyInternal(ThemeDefinition def, string? activeThemeId)
        {
            Color C(string? hex, string fallback) => TryParse(hex, out var c) ? c : Parse(fallback);

            Color accent = C(def.Accent, Default.Accent!);
            Color accentHover = TryParse(def.AccentHover, out var ah) ? ah : Lighten(accent, 0.45);

            AccentColor = accent;
            AccentHoverColor = accentHover;
            ActiveThemeId = activeThemeId;

            var res = Application.Current?.Resources;
            if (res != null)
            {
                // Accent family (Color + Brush forms; dim/soft keep their translucency). Brushes are
                // MUTATED in place (see SetBrush) so every element already bound to them recolors instantly.
                res["AccentColor"] = accent;
                res["AccentHoverColor"] = accentHover;
                SetBrush(res, "AccentBrush", accent);
                SetBrush(res, "AccentHoverBrush", accentHover);
                SetBrush(res, "AccentDimBrush", accent, 0.14);
                SetBrush(res, "AccentSoftBrush", accent, 0.20);

                // Neutrals.
                Color bg = C(def.Background, Default.Background!);
                res["AppBackgroundColor"] = bg;
                SetBrush(res, "AppBackgroundBrush", bg);
                SetBrush(res, "AppWellBrush", C(def.Well, Default.Well!));
                SetBrush(res, "SurfaceBrush", C(def.Surface, Default.Surface!));
                SetBrush(res, "SurfaceHoverBrush", C(def.SurfaceHover, Default.SurfaceHover!));
                SetBrush(res, "SurfaceRaisedBrush", C(def.SurfaceRaised, Default.SurfaceRaised!));
                SetBrush(res, "PanelBorderBrush", C(def.PanelBorder, Default.PanelBorder!));
                SetBrush(res, "BorderStrongBrush", C(def.BorderStrong, Default.BorderStrong!));
                SetBrush(res, "TextPrimaryBrush", C(def.TextPrimary, Default.TextPrimary!));
                SetBrush(res, "TextMutedBrush", C(def.TextMuted, Default.TextMuted!));

                // Semantics.
                SetBrush(res, "SuccessBrush", C(def.Success, Default.Success!));
                SetBrush(res, "WarningBrush", C(def.Warning, Default.Warning!));
                SetBrush(res, "ErrorBrush", C(def.Error, Default.Error!));

                // Type.
                res["FontDisplay"] = new FontFamily(string.IsNullOrWhiteSpace(def.DisplayFont) ? Default.DisplayFont! : def.DisplayFont);
                res["FontMono"] = new FontFamily(string.IsNullOrWhiteSpace(def.MonoFont) ? Default.MonoFont! : def.MonoFont);
            }

            AccentChanged?.Invoke();
        }

        /// <summary>
        /// Writes a themed brush by MUTATING the existing SolidColorBrush's Color/Opacity in place instead of
        /// replacing the resource entry. Mutating updates every element already referencing that brush
        /// instantly and reliably. Replacing the key does NOT always invalidate DynamicResource references
        /// when the same key also lives in a merged dictionary (Themes/Palette.xaml) - which is why a few
        /// controls (the sidebar tab icons and the Save button) used to stay stuck on the previous accent.
        /// Falls back to replacing only when there's no existing brush, or it's frozen / not a SolidColorBrush.
        /// </summary>
        private static void SetBrush(ResourceDictionary res, string key, Color color, double opacity = 1.0)
        {
            if (res[key] is SolidColorBrush b && !b.IsFrozen)
            {
                b.Color = color;
                b.Opacity = opacity;
            }
            else
            {
                res[key] = new SolidColorBrush(color) { Opacity = opacity };
            }
        }

        // ───────────────────────────── helpers ─────────────────────────────

        private static bool TryParse(string? hex, out Color color)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(hex))
                {
                    color = (Color)ColorConverter.ConvertFromString(hex.Trim());
                    return true;
                }
            }
            catch { /* malformed hex - fall through */ }
            color = Colors.Black;
            return false;
        }

        private static Color Parse(string hex)
            => TryParse(hex, out Color c) ? c : (Color)ColorConverter.ConvertFromString(DefaultAccentHex);

        /// <summary>"#RRGGBB" for a color (drops alpha; the palette is opaque).</summary>
        public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        /// <summary>Blends a color toward white by <paramref name="amount"/> (0..1) for the hover shade.</summary>
        private static Color Lighten(Color c, double amount)
        {
            byte Mix(byte ch) => (byte)Math.Round(ch + (255 - ch) * amount);
            return Color.FromRgb(Mix(c.R), Mix(c.G), Mix(c.B));
        }
    }
}
