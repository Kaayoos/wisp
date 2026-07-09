using System;
using System.Diagnostics;
using System.IO;
using Wisp.Plugins;
using Wisp.Plugins.Theming;
using Wisp.Plugins.UI;

namespace WispThemePack
{
    /// <summary>
    /// A small end-to-end sample of the Wisp v2 plugin API. It:
    ///   • registers two full themes (they show up in Settings → Appearance for the user to pick),
    ///   • adds a "Reveal in Explorer" command to every clip's right-click menu,
    ///   • adds a "Capture clip now" shortcut to the system-tray menu,
    ///   • shows a native toast when enabled.
    /// Nothing here needs WPF - it's pure API usage, the simplest possible reference.
    /// </summary>
    public sealed class ThemePackPlugin : WispPluginBase
    {
        public override string Id => "com.wisp.sample.themepack";
        public override string Name => "Wisp Theme Pack (Sample)";
        public override string Version => "1.0.0";
        public override string Author => "MinimalPulse";
        public override string Description => "Sample: registers extra themes and adds a clip action + tray shortcut.";

        public override void OnEnabled()
        {
            // 1) Register full themes. RegisterTheme only makes them AVAILABLE - the user selects one in
            //    Settings → Appearance. Re-registering here every OnEnabled means a theme the user picked
            //    last session is restored automatically when Wisp restarts and re-enables this plugin.
            Host.Theming.RegisterTheme(new WispTheme("com.wisp.sample.synthwave", "Synthwave")
            {
                Accent = "#FF4DD2",
                Background = "#160E26",
                Well = "#0F0A1C",
                Surface = "#241638",
                SurfaceHover = "#311E4C",
                SurfaceRaised = "#2B1A44",
                PanelBorder = "#46306B",
                BorderStrong = "#5E3F8F",
                TextPrimary = "#F6ECFF",
                TextMuted = "#C5AEE8",
            });

            Host.Theming.RegisterTheme(new WispTheme("com.wisp.sample.forest", "Forest")
            {
                Accent = "#5BD68A",
                Background = "#0E1512",
                Well = "#080D0B",
                Surface = "#16201B",
                SurfaceHover = "#1F2D26",
                SurfaceRaised = "#1B2722",
                PanelBorder = "#2C3F35",
                BorderStrong = "#3E5A4B",
                TextPrimary = "#E7F3EC",
                TextMuted = "#AFC8B9",
            });

            // 2) A command on every clip's right-click menu. Runs on the UI thread; keep it quick.
            Host.Ui.RegisterClipAction(new ClipAction("reveal", "Reveal in Explorer", clip =>
            {
                if (!string.IsNullOrEmpty(clip.FilePath) && File.Exists(clip.FilePath))
                    Process.Start("explorer.exe", $"/select,\"{clip.FilePath}\"");
            })
            {
                IconGlyph = "E838", // Segoe MDL2 folder glyph (hex or the glyph char both work)
            });

            // 3) A tray shortcut to grab a clip without opening the main window.
            Host.Ui.AddTrayMenuItem("capture", "Capture clip now", () => Host.Recorder.CaptureClipNow());

            Host.Ui.ShowToast("Theme Pack enabled", "Two themes added - see Settings → Appearance.", ToastKind.Success);
            Host.Log.Info("Theme Pack ready.");
        }

        public override void OnDisabled()
        {
            // Wisp auto-removes a plugin's contributions on disable, but undoing them explicitly is good
            // hygiene (and required if you add/remove them at runtime rather than only in OnEnabled).
            Host.Ui.UnregisterClipAction("reveal");
            Host.Ui.RemoveTrayMenuItem("capture");
            Host.Theming.UnregisterTheme("com.wisp.sample.synthwave");
            Host.Theming.UnregisterTheme("com.wisp.sample.forest");
        }
    }
}
