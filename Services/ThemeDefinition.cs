namespace Wisp.Services
{
    /// <summary>
    /// A complete palette + type pairing the app can apply at runtime. This is the host-side mirror of
    /// the plugin SDK's <c>WispTheme</c> (kept separate so <see cref="ThemeManager"/> has no plugin
    /// dependency). Every colour slot is an optional hex string ("#RRGGBB"/"#AARRGGBB"); a null slot
    /// inherits <see cref="ThemeManager"/>'s default for that surface, so a theme can repaint everything
    /// or just the accent + background.
    /// </summary>
    public sealed record ThemeDefinition
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public bool IsDark { get; init; } = true;

        /// <summary>True for the themes Wisp ships; false for ones a plugin registered.</summary>
        public bool IsBuiltIn { get; init; }

        /// <summary>Id of the plugin that registered this theme (null for built-ins). Lets the host drop
        /// a plugin's themes when it is disabled.</summary>
        public string? OwnerPluginId { get; init; }

        // Accent
        public string? Accent { get; init; }
        public string? AccentHover { get; init; }

        // Neutrals
        public string? Background { get; init; }
        public string? Well { get; init; }
        public string? Surface { get; init; }
        public string? SurfaceHover { get; init; }
        public string? SurfaceRaised { get; init; }
        public string? PanelBorder { get; init; }
        public string? BorderStrong { get; init; }
        public string? TextPrimary { get; init; }
        public string? TextMuted { get; init; }

        // Semantics
        public string? Success { get; init; }
        public string? Warning { get; init; }
        public string? Error { get; init; }

        // Type
        public string? DisplayFont { get; init; }
        public string? MonoFont { get; init; }
    }
}
