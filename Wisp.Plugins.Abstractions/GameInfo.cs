namespace Wisp.Plugins
{
    /// <summary>The game/app Wisp associates with a recording session.</summary>
    public sealed record GameInfo
    {
        /// <summary>Friendly product name, e.g. "Valorant" ("" if unknown / desktop).</summary>
        public string Name { get; init; } = "";

        /// <summary>Process name without ".exe" if known, else "".</summary>
        public string ProcessName { get; init; } = "";
    }
}
