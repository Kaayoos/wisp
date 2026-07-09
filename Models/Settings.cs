using System;
using System.IO;
using System.Text.Json;

namespace Wisp.Models
{
    public class AppSettings
    {
        public string OutputFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Wisp");
        public int BufferLengthSeconds { get; set; } = 30;

        // ===== Clip chaining =====
        // When the hotkey is tapped several times within ChainWindowSeconds of each other, the overlapping
        // buffers are stitched into ONE continuous longer clip (with a marker per tap) instead of separate
        // files - for "that was insane… wait, it's STILL happening" moments.
        public bool ClipChainingEnabled { get; set; } = true;
        public int ChainWindowSeconds { get; set; } = 20; // how long after a tap another tap still extends the clip
        // Hard ceiling on a chained clip's total length, so a long chain can't grow the in-RAM audio
        // retention / on-disk segment ring without bound. Reaching it finalizes the chain immediately.
        public int MaxChainedClipSeconds { get; set; } = 90;

        /// <summary>
        /// The longest a chained clip may actually become - at least 30s past the normal buffer so chaining
        /// always has room to grow, and never less than the buffer itself. Drives the segment-ring size,
        /// the audio retention bump, and the finalize cap so they all agree. Equals the plain buffer when
        /// chaining is off.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public int EffectiveMaxChainedClipSeconds =>
            ClipChainingEnabled ? Math.Max(MaxChainedClipSeconds, BufferLengthSeconds + 30) : BufferLengthSeconds;
        
        // Save the hotkey as a virtual key code + optional modifier flags
        public int HotkeyVirtualKey { get; set; } = 0x78; // VK_F9 (F9 key)
        public int HotkeyModifiers { get; set; } = 0; // 0=none, 1=Ctrl, 2=Alt, 4=Shift (bitmask)
        public string HotkeyText { get; set; } = "F9";
        public string HotkeyKeys { get; set; } = "";
        
        public bool SystemAudioEnabled { get; set; } = true;
        public bool MicrophoneEnabled { get; set; } = false;
        public string MicrophoneDevice { get; set; } = "";

        // Social app audio (Discord, Messenger, etc.) captured as its own isolated path. When on and a
        // listed app is running, that app is pulled out of the system/game path and into this one.
        public bool SocialAudioEnabled { get; set; } = true;

        // Process names (no .exe, case-insensitive) treated as "social". User-editable; these are the
        // defaults. Matching is by prefix so "discord" also matches Discord's helper processes.
        public System.Collections.Generic.List<string> SocialAppProcesses { get; set; } = new()
        {
            "discord", "slack", "teams", "ms-teams", "msteams", "zoom",
            "skype", "telegram", "whatsapp", "messenger", "signal", "mumble", "teamspeak"
        };

        // Per-source mix levels (percent, 100 = unchanged) and a fine sync nudge.
        public int SystemAudioVolume { get; set; } = 100; // 0-200
        public int MicrophoneVolume { get; set; } = 100;  // 0-200
        public int SocialAudioVolume { get; set; } = 100;  // 0-200
        public int AudioOffsetMs { get; set; } = 0;       // positive = audio later, negative = earlier
        
        // Voice-activated clipping (offline). Off by default; when on, a custom phrase triggers a clip.
        public bool VoiceTriggerEnabled { get; set; } = false;
        public string VoiceTriggerLanguage { get; set; } = "en"; // "en", "pl", "es", "fr", "de", etc.
        public string VoiceTriggerPhrase { get; set; } = "clip that for me";

        public string VideoQuality { get; set; } = "Medium"; // Low, Medium, High
        public int CaptureFps { get; set; } = 30; // free-form; any FPS the display supports (clamped 5–480)
        public string CaptureResolution { get; set; } = "Native"; // "Native", "{height}p" (e.g. "1440p"), or "WxH" (e.g. "1920x1080")

        // Audio encode quality. Capture is already 48 kHz float (WASAPI shared mode), so the real quality
        // knob is the AAC bitrate used for the saved clip, the per-source preview tracks, AND re-encoded
        // exports. "Standard" (192k) matches Wisp's long-standing baked-clip bitrate; the higher presets
        // trade a little file size for crisper mic & game audio. Sample rate / channels are unchanged.
        public string AudioQuality { get; set; } = "Standard"; // Standard=192, High=256, Studio=320

        [System.Text.Json.Serialization.JsonIgnore]
        public int AudioBitrateKbps => AudioQuality switch
        {
            "High" => 256,
            "Studio" => 320,
            _ => 192,
        };

        // Which GPU/encoder to record with. "Auto" = best detected; otherwise a GPU name (the encoder
        // is chosen from its vendor), or "CPU" for software x264. Lets users keep the gaming GPU free.
        public string RecordingGpu { get; set; } = "Auto";

        // ===== Advanced encoding (recording) =====
        // Power-user overrides for how the rolling buffer is encoded. The defaults reproduce the classic
        // H.264 + quality-preset behavior exactly, so untouched installs encode identically.
        //   VideoCodec: "H.264" (most compatible), "H.265" (HEVC; ~same quality at smaller size), or "AV1"
        //     (newest GPUs only). The real encoder is (this codec) x (the RecordingGpu vendor) - e.g. H.265
        //     on an NVIDIA GPU -> hevc_nvenc - probed at startup with graceful fallback to H.264 / software
        //     when the GPU can't do the chosen codec. Note: H.265/AV1 clips need the matching Windows codec
        //     extension to preview in Wisp's player and aren't accepted everywhere you might share them.
        public string VideoCodec { get; set; } = "H.264";

        // When enabled, the quality preset's bitrate is replaced by an explicit target (megabits/sec).
        // Off => the Low/Medium/High preset drives the bitrate (the long-standing behavior).
        public bool CustomBitrateEnabled { get; set; } = false;
        public int CustomVideoBitrateMbps { get; set; } = 6; // clamped 1-500 when applied

        // Which monitor to capture. "Auto" follows the active game / foreground window's monitor (and
        // re-targets if the game is dragged to another display mid-session); otherwise a stable display
        // device name (e.g. "\\.\DISPLAY2"). On a single-monitor PC "Auto" is just that one screen.
        public string RecordMonitor { get; set; } = "Auto";

        public bool LaunchOnStartup { get; set; } = false;

        // ===== Auto game detection =====
        // How Wisp decides when the rolling buffer runs:
        //   "Auto"     – record only while a game is detected in the foreground (default for new installs)
        //   "AlwaysOn" – legacy behavior: the buffer runs the whole time Wisp is open
        //   "Manual"   – the buffer only runs when the user toggles it (tray / hotkey)
        // Existing installs are migrated to "AlwaysOn" in Load() so upgrading doesn't change their behavior.
        public string RecordingMode { get; set; } = "Auto";

        // Auto mode: pull the user's installed games from Steam/Epic/GOG so they're recognized instantly,
        // without waiting for the behavioral heuristic to ramp up.
        public bool ImportInstalledGames { get; set; } = true;

        // Auto mode: always treat these as games (force-record when focused). Process names, no ".exe".
        public System.Collections.Generic.List<string> AlwaysRecordProcesses { get; set; } = new();

        // Auto mode: never record these even if they go fullscreen / use the GPU. Browsers and video
        // players are the classic false positives - fullscreen YouTube/Netflix drives the GPU *video
        // decode* engine, not the 3D engine, but the denylist is a cheap, certain guard regardless.
        public System.Collections.Generic.List<string> NeverRecordProcesses { get; set; } = new()
        {
            "wisp",
            "chrome", "msedge", "firefox", "brave", "opera", "vivaldi",
            "vlc", "mpc-hc", "mpc-hc64", "mpv", "wmplayer", "obs64", "obs32", "spotify"
        };

        // One-time guard: marks that "wisp" has been seeded into NeverRecordProcesses for this install.
        // Lets existing users (whose saved list predates the default) pick it up once, without re-adding
        // it on every launch if they deliberately remove it. See Load().
        public bool NeverRecordSeededWisp { get; set; } = false;

        // Show a tray notification when auto-record starts/stops, so the buffer is never silently on or off.
        public bool AutoRecordNotify { get; set; } = true;

        // After a detected game disappears, keep recording this many seconds before stopping. Smooths over
        // alt-tabs, loading screens and brief focus changes so the buffer doesn't thrash on and off.
        public int AutoRecordStopGraceSeconds { get; set; } = 20;

        // ===== Kill detection =====
        // Detects the player's own kills in supported games and (a) stamps "kill markers" onto saved
        // clips' timelines, (b) optionally auto-saves a clip shortly after each kill. Entirely opt-in:
        // with the master toggle off NOTHING runs (no timers, no sockets, no screen sampling) and clip
        // saving behaves exactly as before. Detection is anti-cheat-safe by construction - official
        // local game APIs (LoL Live Client Data, CS2 Game State Integration) plus screen-region
        // sampling for the vision games; never injection, memory reading, or game hooks.
        public bool KillDetectionEnabled { get; set; } = false;   // master switch
        public bool KillDetectLol { get; set; } = true;           // effective only while the master is on
        public bool KillDetectCs2 { get; set; } = true;
        public bool KillDetectValorant { get; set; } = false;     // vision-based; experimental, opt-in
        public bool KillDetectOverwatch { get; set; } = false;    // vision-based; experimental, opt-in

        // Stamp detected kills as timeline markers on every saved clip that covers them.
        public bool KillMarkersEnabled { get; set; } = true;

        // Auto-save a clip after each kill. Off by default - not everybody wants auto clips. The clip
        // fires KillAutoClipDelaySeconds after the kill (so the aftermath is in the buffer) through the
        // exact same path as the hotkey, so clip chaining stitches multikills into one clip. When
        // chaining is off, KillAutoClipCooldownSeconds throttles back-to-back auto-clips instead.
        public bool KillAutoClipEnabled { get; set; } = false;
        public int KillAutoClipDelaySeconds { get; set; } = 3;    // clamped 0-10 in the UI
        public int KillAutoClipCooldownSeconds { get; set; } = 15; // clamped 5-60; unused while chaining is on

        // CS2 Game State Integration: the local loopback port the game POSTs to, and (when auto-locate
        // fails) the user-picked ...\game\csgo\cfg folder to install the GSI config file into.
        public int Cs2GsiPort { get; set; } = 47810;
        public string Cs2CfgDirOverride { get; set; } = "";

        // Dumps vision-detection ROI snapshots + scores to %APPDATA%\Wisp\kill-detect-debug for tuning
        // the experimental games. Off by default; capped so it can't fill a disk.
        public bool KillDetectionDiagnostics { get; set; } = false;

        // Appearance: the user-pickable accent. A non-empty, valid AccentColorHex (custom color) wins;
        // otherwise the named curated preset is used. Resolved by Services/ThemeManager.
        public string ThemePreset { get; set; } = "Wisp Cyan";
        public string AccentColorHex { get; set; } = "";

        // The selected full theme (built-in or plugin). Empty = the default look (accent-only). When set
        // and known, it wins over the accent above; plugin themes are re-applied once their plugin loads.
        public string ActiveThemeId { get; set; } = "";

        public bool TargetCursorEnabled { get; set; } = false;

        // Notifications
        public int NotificationMonitorIndex { get; set; } = 0; // 0 = Primary, 1 = Secondary, etc.
        public string NotificationPosX { get; set; } = "Right"; // Legacy: "Left", "Center", "Right"
        public string NotificationPosY { get; set; } = "Top";    // Legacy: "Top", "Center", "Bottom"
        public double NotificationPosPctX { get; set; } = 0.95;
        public double NotificationPosPctY { get; set; } = 0.05;

        // When true, the capture popup shows its "clipping…" spinner while the clip is assembled, then
        // flips to the result. When false, the popup stays hidden until the clip is ready and then
        // appears straight as the result ("Clip captured") - the loading phase is never shown.
        public bool ShowCaptureProgress { get; set; } = true;

        // Output formatting & retention
        public string FilenameTemplate { get; set; } = "{Game}_{Date}_{Time}";

        // ===== Auto clip deletion (managed from the Storage tab) =====
        // Two independent limits - either (or both) can be on. A clip is removed if it's older than the age
        // limit OR if the library exceeds the size budget. Favorited and explicitly-protected clips
        // (Clip.IsKept) are never auto-deleted by either limit.
        //   Age limit: delete clips older than RetentionDays.
        public bool RetentionEnabled { get; set; } = false;
        public int RetentionDays { get; set; } = 30;
        //   Show a small "auto-deletes in X days" countdown badge on library cards (age limit only).
        public bool ShowDeletionCountdown { get; set; } = true;
        //   Size budget: keep total clip storage at or under MaxStorageGB by trimming the oldest clips.
        public bool MaxStorageEnabled { get; set; } = false;
        public double MaxStorageGB { get; set; } = 50;

        // Delight (sound & onboarding)
        public bool CaptureSoundEnabled { get; set; } = true;
        public int CaptureSoundVolume { get; set; } = 70;
        public bool HasCompletedOnboarding { get; set; } = false;

        public System.Collections.Generic.List<int> GetHotkeyKeysList()
        {
            if (!string.IsNullOrEmpty(HotkeyKeys))
            {
                var list = new System.Collections.Generic.List<int>();
                foreach (var s in HotkeyKeys.Split(','))
                {
                    if (int.TryParse(s, out int vk))
                        list.Add(vk);
                }
                if (list.Count > 0) return list;
            }

            // Fallback to old format
            var legacyList = new System.Collections.Generic.List<int>();
            if ((HotkeyModifiers & 1) != 0) legacyList.Add(0x11); // Ctrl
            if ((HotkeyModifiers & 2) != 0) legacyList.Add(0x12); // Alt
            if ((HotkeyModifiers & 4) != 0) legacyList.Add(0x10); // Shift
            legacyList.Add(HotkeyVirtualKey);
            return legacyList;
        }

        public void SetHotkeyKeysList(System.Collections.Generic.List<int> keys, string text)
        {
            HotkeyKeys = string.Join(",", keys);
            HotkeyText = text;
            
            // For legacy backward compatibility, try to populate virtual key and modifiers if possible
            if (keys.Count > 0)
            {
                HotkeyVirtualKey = keys[keys.Count - 1]; // last key is usually the non-modifier
                int mods = 0;
                foreach (var k in keys)
                {
                    if (k == 0x11 || k == 0xA2 || k == 0xA3) mods |= 1; // Ctrl
                    if (k == 0x12 || k == 0xA4 || k == 0xA5) mods |= 2; // Alt
                    if (k == 0x10 || k == 0xA0 || k == 0xA1) mods |= 4; // Shift
                }
                HotkeyModifiers = mods;
            }
        }

        /// <summary>The social app process names as a comma-separated string for editing in the UI.</summary>
        public string GetSocialAppsText() => string.Join(", ", SocialAppProcesses);

        /// <summary>Parses a comma/semicolon/newline-separated list back into normalized process names.</summary>
        public void SetSocialAppsFromText(string text) => SocialAppProcesses = ParseProcessList(text);

        /// <summary>The "always record" process names as a comma-separated string for editing in the UI.</summary>
        public string GetAlwaysRecordText() => string.Join(", ", AlwaysRecordProcesses);
        public void SetAlwaysRecordFromText(string text) => AlwaysRecordProcesses = ParseProcessList(text);

        /// <summary>The "never record" process names as a comma-separated string for editing in the UI.</summary>
        public string GetNeverRecordText() => string.Join(", ", NeverRecordProcesses);
        public void SetNeverRecordFromText(string text) => NeverRecordProcesses = ParseProcessList(text);

        /// <summary>
        /// Shared parser for the editable process-name lists (social apps, always/never record): splits on
        /// comma/semicolon/newline, strips a typed ".exe", lower-cases, and de-duplicates.
        /// </summary>
        private static System.Collections.Generic.List<string> ParseProcessList(string text)
        {
            var list = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrWhiteSpace(text))
            {
                foreach (var raw in text.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string cleaned = raw.Trim();
                    if (cleaned.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        cleaned = cleaned.Substring(0, cleaned.Length - 4);
                    cleaned = cleaned.Trim().ToLowerInvariant();
                    if (!string.IsNullOrEmpty(cleaned) && !list.Contains(cleaned))
                        list.Add(cleaned);
                }
            }
            return list;
        }

        private static readonly string SettingsFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Wisp");
        private static readonly string SettingsPath = Path.Combine(SettingsFolder, "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    string json = File.ReadAllText(SettingsPath);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null)
                    {
                        // Ensure output directory is initialized if loaded setting is somehow empty
                        if (string.IsNullOrWhiteSpace(settings.OutputFolder))
                        {
                            settings.OutputFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Wisp");
                        }

                        // Migrate legacy positions if present and not migrated yet
                        if (!string.IsNullOrEmpty(settings.NotificationPosX) && settings.NotificationPosX != "Migrated")
                        {
                            settings.NotificationPosPctX = settings.NotificationPosX switch
                            {
                                "Left" => 0.05,
                                "Center" => 0.50,
                                _ => 0.95
                            };
                            settings.NotificationPosX = "Migrated";
                        }
                        if (!string.IsNullOrEmpty(settings.NotificationPosY) && settings.NotificationPosY != "Migrated")
                        {
                            settings.NotificationPosPctY = settings.NotificationPosY switch
                            {
                                "Center" => 0.50,
                                "Bottom" => 0.95,
                                _ => 0.05
                            };
                            settings.NotificationPosY = "Migrated";
                        }

                        // One-time migration: a settings file written before auto-detect existed has no
                        // "RecordingMode" key. Those are existing users - keep their always-on behavior so
                        // upgrading is invisible. Fresh installs (no file at all) take the "Auto" default.
                        if (!json.Contains("\"RecordingMode\""))
                        {
                            settings.RecordingMode = "AlwaysOn";
                        }

                        // One-time: ensure Wisp excludes itself from recording. Existing users' saved lists
                        // predate this default, so seed "wisp" once. Gated on the marker key's absence so a
                        // user who later removes it isn't overridden on the next launch.
                        if (!json.Contains("\"NeverRecordSeededWisp\""))
                        {
                            if (!settings.NeverRecordProcesses.Contains("wisp"))
                                settings.NeverRecordProcesses.Insert(0, "wisp");
                            settings.NeverRecordSeededWisp = true;
                        }

                        return settings;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load settings: {ex.Message}");
            }

            var defaultSettings = new AppSettings();
            defaultSettings.Save();
            return defaultSettings;
        }

        public void Save()
        {
            try
            {
                if (!Directory.Exists(SettingsFolder))
                {
                    Directory.CreateDirectory(SettingsFolder);
                }

                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(this, options);
                File.WriteAllText(SettingsPath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save settings: {ex.Message}");
            }
        }
    }
}
