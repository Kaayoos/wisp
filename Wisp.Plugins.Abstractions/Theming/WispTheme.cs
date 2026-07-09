namespace Wisp.Plugins.Theming
{
    /// <summary>
    /// A complete look for Wisp - every surface colour plus the two type families. Build one of these
    /// and hand it to <see cref="IWispTheming.RegisterTheme"/>; it then appears in Wisp's Settings →
    /// Appearance picker for the user to choose, exactly like a built-in theme.
    ///
    /// Colours are <b>hex strings</b> ("#RRGGBB" or "#AARRGGBB"). Every slot is optional: leave one
    /// <c>null</c> and Wisp keeps its default for that surface, so you can ship a theme that only
    /// repaints the accent + background, or one that overhauls everything. Fonts are family names
    /// ("Bahnschrift", "Consolas", …); leave them null to keep Wisp's defaults.
    ///
    /// Wisp is a dark-first app - its layout, shadows and contrast are tuned for dark surfaces. You can
    /// ship a light theme, but you own the legibility; set <see cref="IsDark"/> = false so the UI can
    /// adapt where it branches on brightness.
    /// </summary>
    public sealed record WispTheme
    {
        /// <param name="id">Stable unique id, e.g. "com.acme.midnight". Used to persist the user's choice.</param>
        /// <param name="name">Display name shown in the Appearance picker, e.g. "Midnight".</param>
        public WispTheme(string id, string name)
        {
            Id = id;
            Name = name;
        }

        /// <summary>Stable unique id (persisted as the user's selected theme).</summary>
        public string Id { get; }

        /// <summary>Display name shown in Settings → Appearance.</summary>
        public string Name { get; }

        /// <summary>Hint: is this a dark theme? Affects nothing visual on its own, but lets the UI
        /// branch sensibly. Defaults to true (Wisp is dark-first).</summary>
        public bool IsDark { get; init; } = true;

        // ── Accent ──────────────────────────────────────────────────────────────────────────────
        /// <summary>The signature colour: nav highlight, primary buttons, focus rings, links.</summary>
        public string? Accent { get; init; }
        /// <summary>Hover/lit shade of the accent. If null, Wisp derives it by blending toward white.</summary>
        public string? AccentHover { get; init; }

        // ── Neutrals (the dark UI scaffold) ─────────────────────────────────────────────────────
        /// <summary>App window background - the base canvas everything sits on.</summary>
        public string? Background { get; init; }
        /// <summary>Deepest recessed "well": thumbnail beds, search fields, grooves (darker than the bg).</summary>
        public string? Well { get; init; }
        /// <summary>Panels and cards that sit on the background.</summary>
        public string? Surface { get; init; }
        /// <summary>Hovered state of a surface.</summary>
        public string? SurfaceHover { get; init; }
        /// <summary>A surface lifted slightly ABOVE the panel (e.g. the capture console).</summary>
        public string? SurfaceRaised { get; init; }
        /// <summary>Default 1px borders/dividers between surfaces.</summary>
        public string? PanelBorder { get; init; }
        /// <summary>Emphasised border for focal panels and active states.</summary>
        public string? BorderStrong { get; init; }
        /// <summary>Primary text colour (headings, values).</summary>
        public string? TextPrimary { get; init; }
        /// <summary>Muted/secondary text (labels, captions).</summary>
        public string? TextMuted { get; init; }

        // ── Semantics (override only if your palette demands it) ─────────────────────────────────
        /// <summary>Success / positive state.</summary>
        public string? Success { get; init; }
        /// <summary>Warning / caution state.</summary>
        public string? Warning { get; init; }
        /// <summary>Error / destructive state.</summary>
        public string? Error { get; init; }

        // ── Type ────────────────────────────────────────────────────────────────────────────────
        /// <summary>Display/heading font family (Wisp's default is Bahnschrift). Family name, not a path.</summary>
        public string? DisplayFont { get; init; }
        /// <summary>Monospace/readout font family (Wisp's default is Cascadia Mono).</summary>
        public string? MonoFont { get; init; }
    }

    /// <summary>A read-only summary of a theme Wisp knows about, returned by <see cref="IWispTheming.GetThemes"/>.</summary>
    public sealed record WispThemeInfo(string Id, string Name, bool IsBuiltIn, bool IsDark);
}
