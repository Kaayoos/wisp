using System;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Wisp.Services.KillDetection
{
    /// <summary>
    /// Installs Wisp's Game State Integration config for Counter-Strike 2. GSI is Valve's official,
    /// sanctioned integration mechanism: a small .cfg file in the game's cfg folder tells CS2 to POST
    /// its game state to a local URL. Writing that one file is the entire "integration" - no game
    /// files are modified.
    ///
    /// The cfg dir is located with the same Steam recipe as InstalledGameScanner.TrySteam (registry
    /// SteamPath -> libraryfolders.vdf -> each library), probing for the CS2 install. When that fails
    /// (unusual Steam setup), the settings UI lets the user pick the cfg folder by hand
    /// (AppSettings.Cs2CfgDirOverride). Install only ever happens on the user's explicit action of
    /// enabling CS2 kill detection in Settings.
    /// </summary>
    public static class Cs2GsiInstaller
    {
        public const string ConfigFileName = "gamestate_integration_wisp.cfg";
        private const string Cs2CfgRelative = @"steamapps\common\Counter-Strike Global Offensive\game\csgo\cfg";

        /// <summary>The CS2 cfg directory, honoring the user override first. Null when not found.</summary>
        public static string? FindCfgDir(string? overrideDir)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(overrideDir) && Directory.Exists(overrideDir))
                    return overrideDir;

                if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is not string steamPath
                    || string.IsNullOrWhiteSpace(steamPath))
                    return null;

                var libraries = new System.Collections.Generic.List<string> { steamPath };
                string vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                {
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s*\"([^\"]+)\""))
                    {
                        // Steam escapes backslashes in the vdf ("D:\\SteamLibrary").
                        string lib = m.Groups[1].Value.Replace(@"\\", @"\");
                        if (!string.IsNullOrWhiteSpace(lib)) libraries.Add(lib);
                    }
                }

                foreach (string lib in libraries)
                {
                    string cfg = Path.Combine(lib, Cs2CfgRelative);
                    if (Directory.Exists(cfg)) return cfg;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"CS2 cfg folder lookup failed: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// True when Wisp's GSI config exists AND points at the given port (a stale port counts as not
        /// installed, so the settings UI re-installs after a port change).
        /// </summary>
        public static bool IsInstalled(int port, string? overrideDir, out string configPath)
        {
            configPath = "";
            string? dir = FindCfgDir(overrideDir);
            if (dir == null) return false;
            configPath = Path.Combine(dir, ConfigFileName);
            try
            {
                return File.Exists(configPath) && File.ReadAllText(configPath).Contains($"127.0.0.1:{port}", StringComparison.Ordinal);
            }
            catch { return false; }
        }

        /// <summary>Writes (or rewrites) the GSI config. Returns false with a user-showable error on failure.</summary>
        public static bool TryInstall(int port, string? overrideDir, out string configPath, out string error)
        {
            configPath = "";
            error = "";
            string? dir = FindCfgDir(overrideDir);
            if (dir == null)
            {
                error = "CS2 installation not found. Use \"Locate CS2 folder\" to pick the game's csgo\\cfg folder.";
                return false;
            }

            configPath = Path.Combine(dir, ConfigFileName);
            try
            {
                // throttle/buffer keep updates snappy (a kill shows within ~0.2s) without flooding;
                // heartbeat keeps the connection warm so the first kill of a quiet round isn't delayed.
                string content =
                    "\"Wisp Kill Detection\"\r\n" +
                    "{\r\n" +
                    $"    \"uri\" \"http://127.0.0.1:{port}\"\r\n" +
                    "    \"timeout\" \"1.0\"\r\n" +
                    "    \"buffer\" \"0.2\"\r\n" +
                    "    \"throttle\" \"0.2\"\r\n" +
                    "    \"heartbeat\" \"10.0\"\r\n" +
                    "    \"data\"\r\n" +
                    "    {\r\n" +
                    "        \"provider\"           \"1\"\r\n" +
                    "        \"player_id\"          \"1\"\r\n" +
                    "        \"player_state\"       \"1\"\r\n" +
                    "        \"player_match_stats\" \"1\"\r\n" +
                    "    }\r\n" +
                    "}\r\n";
                File.WriteAllText(configPath, content);
                Logger.Info($"CS2 GSI config installed: {configPath}");
                return true;
            }
            catch (Exception ex)
            {
                error = $"Could not write the GSI config: {ex.Message}";
                Logger.Warn($"CS2 GSI config install failed: {ex.Message}");
                return false;
            }
        }
    }
}
