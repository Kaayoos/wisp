using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Wisp.Services
{
    /// <summary>
    /// Game install locations discovered on this PC (Steam/Epic/GOG), plus a foreground membership check.
    /// The launchers already cataloged which apps are games, so we let them do the work instead of shipping
    /// or fetching a list of our own - and it stays entirely offline.
    /// </summary>
    public class InstalledGames
    {
        // Normalized lowercase paths with a trailing separator, so StartsWith can't match a sibling folder
        // that merely shares a name prefix (e.g. "...\Foo" vs "...\FooBar").
        public HashSet<string> InstallDirs { get; } = new();

        // Lowercase exe base names (no ".exe") - the fallback used when a process's full path can't be read
        // (e.g. an elevated anti-cheat game), since the process name is still readable.
        public HashSet<string> ExeNames { get; } = new();

        public bool Any => InstallDirs.Count > 0 || ExeNames.Count > 0;

        /// <summary>True if the foreground app (by full exe path, or failing that its process name) is a known installed game.</summary>
        public bool IsGame(string? exePath, string? processName)
        {
            if (!string.IsNullOrEmpty(exePath))
            {
                string p = InstalledGameScanner.NormalizePath(exePath);
                foreach (string dir in InstallDirs)
                    if (p.StartsWith(dir, StringComparison.Ordinal)) return true;
            }
            if (!string.IsNullOrEmpty(processName) && ExeNames.Contains(processName.ToLowerInvariant()))
                return true;
            return false;
        }
    }

    /// <summary>Scans the installed-game catalogs of the major PC launchers. Every step is best-effort.</summary>
    public static class InstalledGameScanner
    {
        public static InstalledGames Scan()
        {
            var games = new InstalledGames();
            TrySteam(games);
            TryEpic(games);
            TryGog(games);
            TryRoblox(games);
            Logger.Info($"Installed-game scan: {games.InstallDirs.Count} install dirs, {games.ExeNames.Count} exe names.");
            return games;
        }

        // ---- Steam: SteamPath from registry -> libraryfolders.vdf -> each library's steamapps\common ----
        private static void TrySteam(InstalledGames games)
        {
            try
            {
                if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is not string steamPath
                    || string.IsNullOrWhiteSpace(steamPath))
                    return;

                var libraries = new List<string> { steamPath };
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
                    string common = Path.Combine(lib, "steamapps", "common");
                    if (Directory.Exists(common)) games.InstallDirs.Add(NormalizeDir(common));
                }
            }
            catch (Exception ex) { Logger.Warn($"Steam game scan skipped: {ex.Message}"); }
        }

        // ---- Epic: %ProgramData%\Epic\...\Manifests\*.item (JSON) -> InstallLocation + LaunchExecutable ----
        private static void TryEpic(InstalledGames games)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Epic", "EpicGamesLauncher", "Data", "Manifests");
                if (!Directory.Exists(dir)) return;

                foreach (string file in Directory.GetFiles(dir, "*.item"))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(file));
                        var root = doc.RootElement;
                        if (root.TryGetProperty("InstallLocation", out var loc) && loc.GetString() is string install && !string.IsNullOrWhiteSpace(install))
                            games.InstallDirs.Add(NormalizeDir(install));
                        if (root.TryGetProperty("LaunchExecutable", out var exe) && exe.GetString() is string exePath && !string.IsNullOrWhiteSpace(exePath))
                            games.ExeNames.Add(Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant());
                    }
                    catch { /* one malformed manifest shouldn't sink the rest */ }
                }
            }
            catch (Exception ex) { Logger.Warn($"Epic game scan skipped: {ex.Message}"); }
        }

        // ---- GOG: HKLM\SOFTWARE\WOW6432Node\GOG.com\Games\* -> path + exe ----
        private static void TryGog(InstalledGames games)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
                using var gog = baseKey.OpenSubKey(@"SOFTWARE\GOG.com\Games");
                if (gog == null) return;

                foreach (string id in gog.GetSubKeyNames())
                {
                    try
                    {
                        using var game = gog.OpenSubKey(id);
                        if (game == null) continue;
                        if (game.GetValue("path") is string path && !string.IsNullOrWhiteSpace(path))
                            games.InstallDirs.Add(NormalizeDir(path));
                        if (game.GetValue("exe") is string exe && !string.IsNullOrWhiteSpace(exe))
                            games.ExeNames.Add(Path.GetFileNameWithoutExtension(exe).ToLowerInvariant());
                    }
                    catch { }
                }
            }
            catch (Exception ex) { Logger.Warn($"GOG game scan skipped: {ex.Message}"); }
        }

        // ---- Roblox: installs per-user to %LOCALAPPDATA%\Roblox, not via any of the store launchers, and
        //      typically runs windowed - so it slips past both the catalogs above and the fullscreen
        //      heuristic. The player exe name is constant; only the version folder changes on update. ----
        private static void TryRoblox(InstalledGames games)
        {
            try
            {
                string root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox");
                if (!Directory.Exists(root)) return;

                games.ExeNames.Add("robloxplayerbeta");
                string versions = Path.Combine(root, "Versions");
                if (Directory.Exists(versions)) games.InstallDirs.Add(NormalizeDir(versions));
            }
            catch (Exception ex) { Logger.Warn($"Roblox game scan skipped: {ex.Message}"); }
        }

        public static string NormalizePath(string path) => path.Trim().ToLowerInvariant().Replace('/', '\\');

        private static string NormalizeDir(string path)
        {
            string p = NormalizePath(path);
            if (!p.EndsWith('\\')) p += '\\';
            return p;
        }
    }
}
