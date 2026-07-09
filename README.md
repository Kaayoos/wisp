# Wisp

A Windows game clip recorder that gets out of your way. It runs quietly in the background, buffers your screen and audio on a loop, and saves a clip the instant you hit a hotkey, or the instant it catches you getting a kill, if you turn that on.

No ads, no account, no telemetry, no idea who you are. The only thing Wisp ever talks to over the network is GitHub, to check for updates, and you can turn that off too.

---

## What it does

Wisp buffers continuously, either all the time or automatically while a game is running, and merges that buffer into a clip in under a second when you hit the hotkey (`F9` by default, remap it however you like). No hitching, no dropped frames, no wondering afterward whether it was even recording.

Tap the hotkey again while your last clip is still fresh and Wisp chains them into one clip with a marker at each tap, instead of two separate files, for the runs where the first play wasn't the last one.

Turn on kill detection and Wisp does the tapping for you. It watches for your own kills in League of Legends and CS2 through each game's official local API, and experimentally in Valorant and Overwatch 2 by watching your screen for the game's own kill-confirmation UI. Timeline markers, auto-clip, or both, toggled per game, all off by default. See [Anti-cheat safety](#anti-cheat-safety) for why this doesn't put your account at risk.

Auto game detection starts and stops recording as you launch and quit games, so Wisp doesn't have to run all the time to catch things. Hardware encoding kicks in automatically on NVIDIA, AMD, or Intel GPUs, with a CPU encode as the fallback. System audio and mic get mixed independently, each with its own volume and a sync offset, and voice chat apps like Discord get isolated onto their own track instead of bleeding into your game audio.

There's also an offline voice trigger if you'd rather say a phrase than reach for a key; it runs a bundled speech model locally and never touches the internet to do it. The clip browser shows duration, size, and a generated thumbnail for everything you've saved, and lets you play, rename, delete, reveal in folder, search, and sort. The UI is dark by default with a customizable accent color, and both are themeable further by plugins. And yes, it lives in the tray, where a background app belongs.

---

## Anti-cheat safety

Wisp never injects into a game process, reads its memory, or hooks its rendering pipeline. Everything it does falls into two categories: plain desktop capture, the same class of technique OBS and Medal use, and official local APIs or on-screen sampling for kill detection, which uses that same non-invasive capture, just aimed at a small region of your screen instead of the whole thing.

No anti-cheat vendor hands out a written guarantee for anything. But every technique here is one already used by tools people trust their accounts to, and now the source is public, so you can check for yourself instead of taking our word for it.

---

## Tech stack

.NET 8 and WPF, targeting `net8.0-windows10.0.19041.0`. NAudio for audio capture and mixing, Microsoft.Data.Sqlite for the clip index, Velopack for auto-updates, Vosk for the offline voice trigger, Hardcodet.NotifyIcon.Wpf for the tray icon. FFmpeg is bundled and run as its own separate process, never linked into Wisp, and licensed GPLv3 - see [NOTICES.txt](NOTICES.txt).

---

## Building from source

You'll need the .NET 8 SDK and Windows 10 (1809+) or Windows 11. Run [`fetch-ffmpeg.ps1`](fetch-ffmpeg.ps1) once before your first build; FFmpeg isn't committed to the repo (it's around 100 MB), so the build will fail without it.

```
dotnet build Wisp.csproj -c Release
```

For a self-contained single-file build:

```
dotnet publish Wisp.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true
```

That lands in `bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\` and runs standalone on any Windows 10/11 machine, no admin rights or separate .NET install required.

Official releases go through [`build-release.ps1`](build-release.ps1), which builds and packs a Velopack installer into `.\releaseSetup`. The first run pulls the pinned build tool (`vpk`) from `.config/dotnet-tools.json` automatically.

---

## Configuration

Settings live in a plain JSON file at `%AppData%\Wisp\settings.json`. The clip library's index is a SQLite database at `%AppData%\Wisp\clips.db`.

---

## Plugins

Plugins are .NET class libraries that load in-process against a stable SDK covering theming, player overlays and timeline markers, export-time video layers, clip actions, and tray menu items. See [PLUGINS.md](PLUGINS.md) for the full guide and [`samples/`](samples/) for three working examples: a webcam overlay, a theme pack, and a clip-marker plugin.

---

## License

Wisp is free software under the GNU General Public License v3.0, or any later version at your option. See [LICENSE](LICENSE) for the full text and [NOTICES.txt](NOTICES.txt) for the third-party components it bundles. "Wisp" and the Wisp logo are trademarks of MinimalPulse - see [TRADEMARK.md](TRADEMARK.md) for what that does and doesn't restrict.

Copyright (C) 2026 MinimalPulse.
