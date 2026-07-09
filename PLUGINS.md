# Wisp Plugin Development Guide

> Build extensions for Wisp - webcam overlays, auto-uploaders, custom themes, new tabs, clip actions, player tools… anything you can write in .NET.

**This document and the SDK's XML doc-comments are the contract.** Everything a plugin can do is here. If something you need isn't exposed, it's a feature request - open an issue rather than reaching into internals the SDK doesn't expose.

- Plugin API version: **4** (`WispPluginSdk.ApiVersion`) - additive; v1/v2/v3 plugins still load. v3 added the [player surface](#player-iwispplayer); v4 adds [player video overlays](#player-iwispplayer) and [export layers](#export-layers-getexportlayers).
- SDK assembly: `Wisp.Plugins.Abstractions.dll` (ships next to `Wisp.exe`)
- Target framework: `net8.0-windows` (or plain `net8.0` if you use no Windows/WPF types)

## Table of Contents

- [Quick Start](#quick-start)
- [Concepts](#concepts)
- [Trust Model](#trust-model)
- [Project Setup](#project-setup)
- [Plugin Manifest (`plugin.json`)](#plugin-manifest)
- [Implementing `IWispPlugin`](#implementing-iwispplugin)
- [Lifecycle](#lifecycle)
- [API Surface](#api-surface)
  - [`IWispHost`](#iwisphost)
  - [Events (`IWispEvents`)](#events-iwispevents)
  - [`ClipInfo`](#clipinfo)
  - [Clip Library (`IClipLibrary`)](#clip-library-icliplibrary)
  - [Recorder Control (`IRecorderControl`)](#recorder-control-irecordercontrol)
  - [Plugin Storage (`IPluginStorage`)](#plugin-storage-ipluginstorage)
  - [Logging (`IWispLog`)](#logging-iwisplog)
  - [Host Info (`HostInfo`)](#host-info-hostinfo)
- [Declarative Settings](#declarative-settings)
- [UI Integration](#ui-integration)
  - [Toasts](#toasts)
  - [Clip actions](#clip-actions)
  - [Tray menu items](#tray-menu-items)
  - [Sidebar tabs & settings blocks](#sidebar-tabs--settings-blocks)
  - [Custom dialogs](#custom-dialogs)
  - [Raw window access (escape hatch)](#raw-window-access-escape-hatch)
- [Player (`IWispPlayer`)](#player-iwispplayer)
- [Export layers (`GetExportLayers`)](#export-layers-getexportlayers)
- [Theming](#theming)
  - [Recolour the accent](#recolour-the-accent)
  - [Register full themes](#register-full-themes)
  - [The `WispTheme` palette](#the-wisptheme-palette)
  - [Raw resource override](#raw-resource-override)
- [Installation](#installation)
- [Chaining & Clip Events](#chaining--clip-events)
- [Examples](#examples)
- [Troubleshooting](#troubleshooting)

---

## Quick Start

```bash
# 1. Create a class library
dotnet new classlib -n MyWispPlugin -f net8.0-windows
cd MyWispPlugin

# 2. Reference the SDK (the DLL shipped next to Wisp.exe, or the project if you have it)
dotnet add reference ../path/to/Wisp.Plugins.Abstractions.csproj

# 3. Implement IWispPlugin (below) and add a plugin.json

# 4. Build, then copy the output into %AppData%\Wisp\plugins\MyWispPlugin\

# 5. Open Wisp → Plugins tab → Reload → enable it
```

## Concepts

A **plugin** is a .NET class library (DLL) that implements `IWispPlugin`. Wisp scans `%AppData%\Wisp\plugins\` at startup for subfolders containing a `plugin.json`.

Each plugin loads **in-process** into its own collectible `AssemblyLoadContext` (ALC). That gives you:

- The full Wisp SDK - events, clip library, recorder control, the UI thread, theming.
- Your own isolated dependency graph (your NuGet packages won't clash with Wisp's).
- Clean unload when disabled or reloaded.

## Trust Model

> **Plugins run with full trust**, in Wisp's process, with the same permissions as the app. There is no OS sandbox.

- Plugins are **disabled by default** - the user enables each one explicitly.
- Every call into your code is **crash-isolated**: a throw is caught, logged with your plugin id, and counted. After 3 consecutive failures the plugin is auto-disabled. Wisp never crashes because of a plugin.
- **For users:** only enable plugins from authors you trust. Enabling one is like running an executable.

## Project Setup

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>            <!-- only if your plugin has WPF UI (overlays, custom controls) -->
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <!-- Private=false: the host already has the SDK loaded; don't ship a second copy (see Troubleshooting). -->
    <ProjectReference Include="..\path\to\Wisp.Plugins.Abstractions.csproj">
      <Private>false</Private>
    </ProjectReference>
  </ItemGroup>
</Project>
```

> **Why `Private=false`?** The host's ALC deliberately shares the already-loaded `Wisp.Plugins.Abstractions` so your `IWispPlugin` is the *same type* the host expects. Shipping your own copy of that DLL can cause `IWispPlugin`-to-`IWispPlugin` cast errors.

## Plugin Manifest

Every plugin folder needs a `plugin.json`:

```json
{
  "id": "com.myname.my-plugin",
  "name": "My Cool Plugin",
  "version": "1.0.0",
  "author": "Your Name",
  "description": "What this plugin does.",
  "entryAssembly": "MyWispPlugin.dll",
  "minApiVersion": 2
}
```

| Field | Required | Description |
|---|---|---|
| `id` | Yes | Stable unique id (data folder, log prefix, enabled-state key). |
| `name` | No | Display name in the Plugins tab (defaults to `id`). |
| `version` | No | Display version. |
| `author` | No | Author / vendor. |
| `description` | No | One-line description. |
| `entryAssembly` | No | Your DLL filename. Auto-detected from the folder if omitted. |
| `minApiVersion` | No | Minimum host API version. Use `2` for theming / clip actions / toasts / tray; `3` for the [player surface](#player-iwispplayer); `4` for player video overlays + [export layers](#export-layers-getexportlayers). A plugin requiring a newer version than the host shows as such and stays inactive. |

## Implementing `IWispPlugin`

The easiest path is to extend `WispPluginBase`, which gives no-op virtuals for everything so you override only what you need:

```csharp
using Wisp.Plugins;

public class MyPlugin : WispPluginBase
{
    public override string Id => "com.myname.my-plugin";
    public override string Name => "My Cool Plugin";
    public override string Description => "Does cool things.";

    public override void OnEnabled()
    {
        Host.Log.Info("Plugin enabled!");
        Host.Events.ClipSaved += OnClipSaved;
    }

    public override void OnDisabled()
    {
        Host.Events.ClipSaved -= OnClipSaved;
    }

    private void OnClipSaved(object? sender, ClipSavedEventArgs e)
    {
        // ClipInfo has Filename / GameName / FilePath - there is no "Title".
        Host.Log.Info($"Clip saved: {e.Clip.Filename} ({e.Clip.GameName}) at {e.Clip.FilePath}");
    }
}
```

`Host` (an `IWispHost`) is available after `OnLoaded` - `WispPluginBase` stashes it for you.

## Lifecycle

```
 Construction (parameterless ctor)
       │
       ▼
 OnLoaded(host)   ← stash the host. Do NOT start work yet.
       │
       ▼
 OnEnabled()      ← subscribe to events; register themes / clip actions / tray items; spin up windows.
       │
       ▼
   (active - events flow, the user interacts)
       │
       ▼
 OnDisabled()     ← unsubscribe; stop/hide everything you started.
       │
       ▼
 OnShutdown()     ← final teardown (app exit / reload). Dispose every resource, thread and window.
```

Each step is crash-isolated. Contributions you make through `Host.Ui` (clip actions, tray items) and `Host.Theming` (themes) are **removed automatically when your plugin is disabled** - but unsubscribing/unregistering in `OnDisabled` yourself is good hygiene.

## API Surface

### `IWispHost`

The façade handed to you in `OnLoaded`:

| Property | Type | Description |
|---|---|---|
| `Log` | `IWispLog` | Attributed logging (`[Plugin:your-id]` in `wisp.log`). |
| `Events` | `IWispEvents` | Subscribe to lifecycle events. |
| `Clips` | `IClipLibrary` | Query and manage the clip library. |
| `Recorder` | `IRecorderControl` | Observe and drive the recorder. |
| `Storage` | `IPluginStorage` | Per-plugin settings + private data folder. |
| `Ui` | `IUiBridge` | UI thread, toasts, clip actions, tray items, tabs, dialogs. |
| `Theming` | `IWispTheming` | Accent, full themes, raw resource override. |
| `Player` | `IWispPlayer` | Clip-player control-bar buttons, timeline markers, video overlays, read/seek playback. |
| `Info` | `HostInfo` | App version, API version, folders. |

### Events (`IWispEvents`)

Handlers run on a **background thread** (so a slow handler - e.g. an upload - never stalls capture or the UI). Marshal to the UI thread with `Host.Ui.RunOnUiThread(...)` before touching WPF. Each plugin has an isolated hub; a throw in your handler is caught and logged.

| Event | Args | When |
|---|---|---|
| `ClipSaved` | `ClipSavedEventArgs` | A final, user-facing clip was saved. |
| `ClipRemoved` | `ClipRemovedEventArgs` | A clip was deleted, or a first-tap clip was superseded by a chain (`Superseded == true`). |
| `RecordingStarted` | `RecordingEventArgs` | Rolling buffer started. |
| `RecordingStopped` | `RecordingEventArgs` | Rolling buffer stopped. |
| `GameDetected` | `GameEventArgs` | Auto-detection recognised a game. |
| `GameStopped` | `GameEventArgs` | Game exited / grace period lapsed. |
| `HotkeyTriggered` | `HotkeyEventArgs` | Capture hotkey (or voice phrase) fired. |

`ClipSavedEventArgs.Clip` and `ClipRemovedEventArgs.Clip` are `ClipInfo`. `RecordingEventArgs.Recording` is a `RecordingInfo`; `GameEventArgs.Game` / `HotkeyEventArgs.Game` are `GameInfo`.

### `ClipInfo`

An immutable, read-only projection of a clip. Fields (there is **no `Title`** - use `Filename` or `GameName`):

| Member | Type | Notes |
|---|---|---|
| `Id` | `int` | Library id; use with `IClipLibrary`. |
| `FilePath` | `string` | Absolute path to the shareable `.mp4`. |
| `Filename` | `string` | File name only, e.g. `Valorant_20260619_213045.mp4`. |
| `CreatedAt` | `DateTime` | Local time. |
| `DurationSeconds` | `double` | |
| `FileSizeBytes` | `long` | |
| `GameName` | `string` | Foreground game/app at capture, or `""`. |
| `Tags` | `string` | **Comma-separated** user tags (`""` if none). |
| `IsFavorite` | `bool` | |
| `ThumbnailPath` | `string` | Gallery thumbnail JPEG (`""` if none). |
| `SystemTrackPath` / `MicTrackPath` / `SocialTrackPath` | `string` | Optional per-source audio sidecars. |
| `ChainMarkers` | `string` | CSV of clip-relative second offsets; ≥2 means a stitched chain. |

### Clip Library (`IClipLibrary`)

```csharp
IReadOnlyList<ClipInfo> GetClips();                  // newest first
ClipInfo? GetClip(int id);
void SetTags(int id, string tags);                   // tags is a COMMA-SEPARATED string, e.g. "ace, clutch"
void SetFavorite(int id, bool isFavorite);
void Delete(int id);                                 // removes the row and deletes files (best-effort)
ClipInfo? ImportClip(string filePath, string gameName = "");  // import an .mp4 you produced
```

### Recorder Control (`IRecorderControl`)

```csharp
bool IsRecording { get; }
GameInfo CurrentGame { get; }     // CurrentGame.Name is "" for desktop/unknown
void Start();
void Stop();
void CaptureClipNow();            // same path as the capture hotkey (honours clip-chaining)
```

### Plugin Storage (`IPluginStorage`)

```csharp
string DataDirectory { get; }            // %AppData%\Wisp\plugins\.data\<your-id>\  (created on first access)
T? LoadSettings<T>() where T : class;    // deserializes settings.json, or null if none/unreadable
void SaveSettings<T>(T settings) where T : class;     // serializes to settings.json
```

Use `DataDirectory` for caches, downloaded models, logs - anything private to your plugin.

### Logging (`IWispLog`)

```csharp
void Info(string message);
void Warn(string message);
void Error(string message, Exception? ex = null);
```

All messages are prefixed `[Plugin:your-id]` in `%AppData%\Wisp\wisp.log`.

### Host Info (`HostInfo`)

```csharp
string AppVersion { get; }        // e.g. "1.0"
int    ApiVersion { get; }        // host's plugin-API version (currently 4)
string ClipOutputFolder { get; }  // where finished .mp4s are written
string AppDataFolder { get; }     // %AppData%\Wisp
string PluginsFolder { get; }     // %AppData%\Wisp\plugins
```

## Declarative Settings

Don't want to build a settings window? Override `GetSettings()` / `OnSettingsSaved()` and Wisp renders a **native, themed** settings dialog for you (a gear button appears next to your enabled plugin in the Plugins tab).

```csharp
using Wisp.Plugins.Settings;

public override IReadOnlyList<PluginSettingField>? GetSettings()
{
    var cfg = Host.Storage.LoadSettings<MyConfig>() ?? new MyConfig();
    return new PluginSettingField[]
    {
        new BoolSettingField("enabled", "Enable feature", cfg.Enabled) { Description = "Turns on the magic." },
        new StringSettingField("apiKey", "API key", cfg.ApiKey),
        new NumberSettingField("volume", "Volume", cfg.Volume) { Min = 0, Max = 100, Step = 1 },
        new ChoiceSettingField("corner", "Corner", cfg.Corner, new[] { "Top-left", "Top-right", "Bottom-left", "Bottom-right" }),
    };
}

public override void OnSettingsSaved(IReadOnlyDictionary<string, object> values)
{
    var cfg = new MyConfig();
    if (values.TryGetValue("enabled", out var en)) cfg.Enabled = (bool)en;
    if (values.TryGetValue("apiKey",  out var ak)) cfg.ApiKey = (string)ak;
    if (values.TryGetValue("volume",  out var vo)) cfg.Volume = Convert.ToDouble(vo);
    if (values.TryGetValue("corner",  out var co)) cfg.Corner = (string)co;
    Host.Storage.SaveSettings(cfg);
    // apply the new config to your running plugin…
}
```

Field types: `BoolSettingField` (toggle), `StringSettingField` (text box), `NumberSettingField` (slider, with `Min`/`Max`/`Step`), `ChoiceSettingField` (dropdown). Values come back keyed by the field `Key`: `bool` for bool, `string` for string/choice, `double` for number.

## UI Integration

All `IUiBridge` members are safe to call from any thread (Wisp marshals UI work for you). Get it from `Host.Ui`.

### Toasts

Show the same transient popup Wisp uses for "Clip saved", so your feedback looks built-in:

```csharp
using Wisp.Plugins.UI;
Host.Ui.ShowToast("Uploaded", "Clip is live on your server.", ToastKind.Success);
// ToastKind: Info (accent-tinted), Success, Warning, Error
```

### Clip actions

Add a command to the right-click menu of **every clip** in the library - the highest-value hook for a clip app:

```csharp
using Wisp.Plugins.UI;

Host.Ui.RegisterClipAction(new ClipAction("upload", "Upload to server", clip =>
{
    // Runs on the UI thread with the targeted clip (a ClipInfo). Kick heavy work to a background thread.
    _ = UploadAsync(clip.FilePath);
})
{
    IconGlyph = "E898",                       // optional Segoe MDL2 glyph (hex or the glyph char)
    CanShow = clip => clip.DurationSeconds > 5 // optional: hide for some clips
});

// later / in OnDisabled:
Host.Ui.UnregisterClipAction("upload");
```

### Tray menu items

Add quick actions to the system-tray menu (they appear above **Quit**):

```csharp
Host.Ui.AddTrayMenuItem("capture", "Capture clip now", () => Host.Recorder.CaptureClipNow());
// onClick runs on the UI thread
Host.Ui.RemoveTrayMenuItem("capture");
```

### Sidebar tabs & settings blocks

Own a whole page, or append a block to Wisp's Settings:

```csharp
// A new sidebar tab showing your WPF content. iconHex: a Segoe MDL2 glyph char OR hex ("E10B"/"&#xE10B;").
var page = new System.Windows.Controls.Grid(); // build your UI…
Host.Ui.AddSidebarTab("myplugin", "My Plugin", "E10B", page);

// A titled card injected into the Settings tab.
var panel = new System.Windows.Controls.StackPanel(); // build your settings UI…
Host.Ui.AddSettingsCategory("My Plugin", panel);
```

Build your content with the app palette so it matches: use `{DynamicResource AccentBrush}`, `SurfaceBrush`, `TextPrimaryBrush`, `PanelBorderBrush`, `FontDisplay`, `FontMono`, etc. (see [Theming](#theming) for the full key list).

### Custom dialogs

```csharp
var content = new System.Windows.Controls.TextBlock { Text = "Hello from my plugin!" };
Host.Ui.ShowCustomDialog("My Plugin", content, width: 360, height: 220);
```

### Raw window access (escape hatch)

`Host.Ui.MainWindow` returns Wisp's `System.Windows.Window` as `object`. It bypasses every stability guarantee and **will break across Wisp updates** - prefer the typed hooks above. If you find yourself needing it, that's a good signal to request a real API.

## Player (`IWispPlayer`)

When the user watches a clip, Wisp opens a built-in player. `Host.Player` lets your plugin extend it: add buttons to the control bar, drop clickable markers on the timeline, **host your own content over the video**, and read or drive playback. It's deliberately generic - clip bookmarking, A/B loops, chapter export, frame screenshots, coaching annotations, a webcam PiP, a stats HUD, subtitles… all fall out of the same primitives. Buttons / markers / seek need API v3; **video overlays need API v4** (`minApiVersion: 4`).

**Threading:** unlike `IWispEvents`, the player's events *and* your button/marker callbacks all run on the **UI thread** (they're tied to the on-screen player), so you can call straight back into `Host.Player` from a handler without marshalling. Keep handlers quick.

| Member | Description |
|---|---|
| `IsOpen` / `CurrentClip` | Whether a clip is open, and which `ClipInfo` it is. |
| `PositionSeconds` / `DurationSeconds` / `IsPlaying` | Live playback state. |
| `Seek(seconds)` / `Play()` / `Pause()` | Drive playback. |
| `AddButton(PlayerButton)` / `RemoveButton(id)` | Add/remove a control-bar button. |
| `SetMarkers(IEnumerable<TimelineMarker>)` / `ClearMarkers()` | Replace/clear *your* timeline markers (other plugins' are untouched). |
| `AddOverlay(PlayerOverlay)` / `RemoveOverlay(id)` / `ClearOverlays()` | Host your own WPF content over the video - movable/resizable, normalised geometry (v4). |
| `ClipOpened` / `Closed` | Fired (UI thread) when a clip opens / the player closes. |

**Buttons** (`PlayerButton`) sit on the control bar next to play/pause/export and show whenever the player is open. Wisp removes them automatically when your plugin is disabled.

```csharp
Host.Player.AddButton(new PlayerButton("snapshot", "Snapshot", () =>
{
    double t = Host.Player.PositionSeconds;
    Host.Log.Info($"Grab a frame at {t:0.0}s of {Host.Player.CurrentClip?.Filename}");
})
{
    IconGlyph = "E722",            // optional Segoe MDL2 glyph (hex or the glyph char)
    Tooltip = "Save the current frame"
});
```

**Markers** (`TimelineMarker`) are painted on the scrubber at a clip-relative time. They're *transient* - they belong to the clip that's open. Set them (usually from `ClipOpened`) and Wisp draws a clickable flag for each: **left-click seeks** to it for you; set `OnRightClick` for a secondary action like "remove". Persist them yourself with `Host.Storage`, keyed by clip id, and re-apply on the next open.

```csharp
Host.Player.ClipOpened += (s, e) =>
{
    var offsets = LoadOffsetsFor(e.Clip.Id);          // your persistence (e.g. Host.Storage)
    Host.Player.SetMarkers(offsets.Select((t, i) => new TimelineMarker(t)
    {
        Label = (i + 1).ToString(),
        ColorHex = "#FFC857",                         // null = follow the user's accent
        Tooltip = $"Bookmark {i + 1}",
        OnRightClick = () => RemoveOffset(e.Clip.Id, t)
    }));
};
```

**Video overlays** (`PlayerOverlay`, v4) host your *own* WPF content on top of the clip video - a webcam PiP, a stats HUD, subtitles, a drawing layer. Geometry is **normalised**: `X`/`Y`/`Width`/`Height` are fractions (0..1) of the displayed video, so the overlay scales with the window and the *same* rectangle can drive an [export layer](#export-layers-getexportlayers). Set `Movable`/`Resizable` to let the user reposition it and persist the new rect from `OnRectChanged`. Add overlays from `ClipOpened`; Wisp clears them when the player closes or the plugin is disabled. Content crosses as `object` (the SDK has no WPF dependency) - pass a `FrameworkElement`.

```csharp
Host.Player.ClipOpened += (s, e) =>
{
    var cam = new MediaElement { LoadedBehavior = MediaState.Manual, Volume = 0,
                                 Source = new Uri(MyCamFileFor(e.Clip.Id)) };
    Host.Player.AddOverlay(new PlayerOverlay("camera", cam)
    {
        X = 0.74, Y = 0.70, Width = 0.24, Height = 0.28,   // normalised to the video rect
        Movable = true, Resizable = true,
        OnRectChanged = r => SaveRect(r)                   // persist where the user dragged it
    });
    // then keep `cam` synced to Host.Player.PositionSeconds / IsPlaying via a DispatcherTimer
};
```

See **`samples/WispClipMarkers/`** for a persistent bookmarking plugin, and **`samples/WispWebcamOverlay/`** (Camera Overlay) for a movable, playback-synced video overlay built on this surface.

## Export layers (`GetExportLayers`)

Plugins can **burn visual layers into an exported clip** - composited over the video by Wisp's ffmpeg when the user exports with *Burn in overlays* enabled. It's the export-time companion to a [player video overlay](#player-iwispplayer): the **same normalised geometry**, so what the user positioned in the player is exactly what bakes into the file. Override `GetExportLayers` (a no-op on `WispPluginBase`) and return one `ExportLayer` per image/video file to draw. Requires API v4.

```csharp
public override IReadOnlyList<ExportLayer>? GetExportLayers(ClipInfo clip)
{
    string face = MyCamFileFor(clip.Id);
    if (!File.Exists(face)) return null;
    return new[] { new ExportLayer(face)
    {
        X = 0.74, Y = 0.70, Width = 0.24, Height = 0.28,   // same rect as the player overlay
        Opacity = 1.0, Mirror = true
    }};
}
```

`SourcePath` can be a **video** (Wisp keeps it in sync with the trimmed clip) or an **image** (e.g. a watermark/logo). `Height = 0` keeps the source's own aspect ratio. The host re-encodes the video when layers are present (a filtered stream can't be a lossless copy).

## Theming

Wisp's identity is its look, so theming is a first-class plugin capability via `Host.Theming` (`IWispTheming`). Changes apply **live** to the whole app. Three levels:

### Recolour the accent

```csharp
Host.Theming.SetAccent("#FF7A45");                 // persists; clears any active full theme
string current = Host.Theming.CurrentAccentHex;
```

### Register full themes

A theme restyles **every surface plus the fonts**. `RegisterTheme` makes it *available*; the **user picks it in Settings → Appearance** (or you call `ApplyTheme`). Re-register in `OnEnabled` so a theme the user selected last session is restored on startup (Wisp re-applies it automatically once you register it).

```csharp
using Wisp.Plugins.Theming;

public override void OnEnabled()
{
    Host.Theming.RegisterTheme(new WispTheme("com.acme.midnight", "Midnight")
    {
        Accent       = "#B98CFF",
        Background    = "#0E0B1A",
        Well          = "#08060F",
        Surface       = "#1A1530",
        SurfaceHover  = "#241D40",
        SurfaceRaised = "#201A38",
        PanelBorder   = "#3A2F5C",
        BorderStrong  = "#50447A",
        TextPrimary   = "#ECE8FF",
        TextMuted     = "#B9AEDC",
        // unset slots keep Wisp's default for that surface; fonts optional
    });
}

public override void OnDisabled() => Host.Theming.UnregisterTheme("com.acme.midnight");
```

Other members:

```csharp
void ApplyTheme(string themeId);                 // switch + persist
void ResetToDefault();                           // back to the default palette + the user's accent
IReadOnlyList<WispThemeInfo> GetThemes();        // built-ins + all registered (Id, Name, IsBuiltIn, IsDark)
string? ActiveThemeId { get; }                   // null = default look (accent-only)
event EventHandler ThemeChanged;                 // recolour your own custom UI when the look changes
```

### The `WispTheme` palette

Every colour is a hex string (`"#RRGGBB"` / `"#AARRGGBB"`); leave any `null` to inherit Wisp's default. These are also the `DynamicResource` keys to bind to from any WPF UI you create.

| `WispTheme` property | Resource key | Surface |
|---|---|---|
| `Accent` | `AccentBrush` / `AccentColor` | Signature colour: nav highlight, primary buttons, focus. |
| `AccentHover` | `AccentHoverBrush` | Lit accent (auto-derived if null). |
| `Background` | `AppBackgroundBrush` | Window base canvas. |
| `Well` | `AppWellBrush` | Deepest recessed insets (thumbnail beds, fields). |
| `Surface` | `SurfaceBrush` | Panels and cards. |
| `SurfaceHover` | `SurfaceHoverBrush` | Hovered surface. |
| `SurfaceRaised` | `SurfaceRaisedBrush` | Surfaces lifted above the panel. |
| `PanelBorder` | `PanelBorderBrush` | Default borders/dividers. |
| `BorderStrong` | `BorderStrongBrush` | Emphasised borders / active states. |
| `TextPrimary` | `TextPrimaryBrush` | Headings and values. |
| `TextMuted` | `TextMutedBrush` | Labels and captions. |
| `Success` / `Warning` / `Error` | `SuccessBrush` / `WarningBrush` / `ErrorBrush` | Semantic states. |
| `DisplayFont` | `FontDisplay` | Heading font family. |
| `MonoFont` | `FontMono` | Readout/mono font family. |

There are also accent-tint brushes you can bind to (managed by Wisp, derived from the accent): `AccentDimBrush` (~14% accent) and `AccentSoftBrush` (~20%).

> **Colours apply live; fonts apply on next launch.** Switching theme mid-session re-colours the whole app immediately. The `DisplayFont`/`MonoFont` families, however, are bound once when Wisp starts - a theme active at launch gets its fonts, but a font change made mid-session shows after a restart.

> Wisp is **dark-first** - its layout and contrast assume dark surfaces. You can ship a light theme, but you own its legibility; set `IsDark = false` on the `WispTheme`.

### Raw resource override

For total control (custom control templates, gradients, anything), merge a WPF `ResourceDictionary` straight over the app's resources. Unguarded - test it, since a bad merge can make the UI unusable until you switch themes.

```csharp
var rd = new System.Windows.ResourceDictionary
{
    Source = new Uri("pack://application:,,,/MyPlugin;component/MySkin.xaml")
};
Host.Theming.ApplyRawResources(rd);   // passed as object; Wisp casts it
```

## Installation

1. Build: `dotnet build`.
2. Copy the **output folder contents** into a folder under the plugins root:
   ```
   %AppData%\Wisp\plugins\MyPlugin\
       MyPlugin.dll
       plugin.json
       (any other dependency DLLs / native libs)
   ```
3. Open Wisp → **Plugins** → **Reload** → enable your plugin. The **Open Folder** button opens the plugins root for you.

## Chaining & Clip Events

When the user taps the capture hotkey several times quickly, Wisp stitches the clips into one. The event order:

1. **First tap** → `ClipSaved` fires with the first clip.
2. **Further taps within the window** → the chain finalises:
   - `ClipRemoved` fires for the first clip with `Superseded == true`.
   - `ClipSaved` fires for the final stitched clip.

If you're auto-uploading, check `ClipRemovedEventArgs.Superseded` to skip a clip that was immediately replaced.

## Examples

- **`samples/WispWebcamOverlay/`** (Camera Overlay) - the v4 surface end to end: records the webcam headlessly into a rolling on-disk buffer while recording, snapshots the matching tail into `cam_<id>.mp4` on `ClipSaved`, shows it as a movable, playback-synced `PlayerOverlay` (a `MediaElement`) inside the player, and burns it into exports via `GetExportLayers`. Handles a missing camera gracefully; all camera-specific logic lives in the plugin.
- **`samples/WispThemePack/`** - the v2 surface end to end: registers two full themes, adds a "Reveal in Explorer" clip action and a "Capture clip now" tray item, and shows a toast. No WPF - the simplest reference.
- **`samples/WispClipMarkers/`** - the v3 player surface end to end: an "Add marker" control-bar button, persistent per-clip bookmarks painted as clickable timeline flags (left-click jumps, right-click removes), via `Host.Player` + `Host.Storage`. No WPF.

### Auto-uploader (clip-saved → HTTP POST)

```csharp
using System.IO;
using System.Net.Http;
using Wisp.Plugins;

public class AutoUploader : WispPluginBase
{
    public override string Id => "com.example.auto-uploader";
    public override string Name => "Auto Uploader";
    public override string Description => "Uploads saved clips to your server.";

    private HttpClient? _http;

    public override void OnEnabled()
    {
        var cfg = Host.Storage.LoadSettings<UploaderConfig>() ?? new UploaderConfig();
        _http = new HttpClient { BaseAddress = new Uri(cfg.ServerUrl) };
        Host.Events.ClipSaved += OnClipSaved;
    }

    public override void OnDisabled()
    {
        Host.Events.ClipSaved -= OnClipSaved;
        _http?.Dispose();
    }

    private async void OnClipSaved(object? sender, ClipSavedEventArgs e)
    {
        var clip = e.Clip;                       // ClipInfo - use Filename / FilePath, not "Title"
        try
        {
            using var stream = File.OpenRead(clip.FilePath);
            using var content = new StreamContent(stream);
            var resp = await _http!.PostAsync("/api/clips", content);
            resp.EnsureSuccessStatusCode();
            Host.Log.Info($"Uploaded {clip.Filename}.");
            Host.Ui.ShowToast("Upload complete", clip.Filename, Wisp.Plugins.UI.ToastKind.Success);
        }
        catch (Exception ex)
        {
            Host.Log.Error($"Upload of {clip.Filename} failed.", ex);
            Host.Ui.ShowToast("Upload failed", clip.Filename, Wisp.Plugins.UI.ToastKind.Error);
        }
    }
}

public class UploaderConfig { public string ServerUrl { get; set; } = "https://clips.example.com"; }
```

## Troubleshooting

| Problem | Solution |
|---|---|
| Plugin doesn't appear | Ensure `plugin.json` exists in the plugin's folder and is valid JSON, then click **Reload**. |
| "Requires a newer Wisp" | Your `minApiVersion` exceeds the host's `HostInfo.ApiVersion`. Lower it or update Wisp. |
| Auto-disabled | It threw 3+ times. Check `wisp.log` for `[Plugin:your-id]` entries. |
| `IWispPlugin` cast error | You shipped your own `Wisp.Plugins.Abstractions.dll`. Reference it with `<Private>false</Private>` so the host's copy is shared. |
| Sidebar-tab icon shows odd characters | Pass a real Segoe MDL2 glyph - the glyph char or a hex code (`"E10B"`); Wisp normalises it. |
| Theme not restored after restart | Re-register it in `OnEnabled` (the user's selection persists by id; Wisp re-applies once it's registered). |
| Clip action / toast does nothing | They need API v2. Confirm `HostInfo.ApiVersion >= 2`. |
| UI work throws "wrong thread" | Event handlers run on a background thread - wrap WPF work in `Host.Ui.RunOnUiThread(...)`. |
