# Camera Overlay - Sample Wisp Plugin

Records your webcam **in the background** while Wisp's buffer is live, but shows it **only inside saved clips** - as a movable, playback-synced picture-in-picture in the player, and (optionally) burned into exports. No live always-on-top window, so your face isn't on screen the whole time you play.

It's also the reference plugin for the **v4 plugin API** (`minApiVersion: 4`): it uses two generic primitives that know nothing about cameras -
- **`IWispPlayer.AddOverlay`** - host your own WPF content over the player video, movable/resizable, synced to playback.
- **`WispPluginBase.GetExportLayers`** - hand the host an image/video to composite into an export.

All camera-specific logic (capture, the rolling buffer, trimming to the clip, file cleanup) lives in the plugin.

## How it works

1. **Record** - on `RecordingStarted`, `CameraRecorder` captures the webcam headlessly into a ring of short on-disk segments, keeping ~`BufferSeconds` of footage (RAM stays flat; old segments are pruned).
2. **Snapshot** - on `ClipSaved`, it assembles the matching last *N* seconds into `cam_<clipId>.mp4` in the plugin's data folder.
3. **Preview** - on `Player.ClipOpened`, if a camera file exists for that clip it's shown as a `PlayerOverlay` (a muted `MediaElement`) kept in sync with the main video; drag/resize to taste - the position is persisted.
4. **Export** - `GetExportLayers` returns that file at the saved spot, so "Burn in overlays" bakes it into the output.
5. **Cleanup** - on `ClipRemoved`, the camera file is deleted.

## Building

```bash
cd samples/WispWebcamOverlay
dotnet build
```

## Installing

Copy the build output into Wisp's plugins directory, then open Wisp → **Plugins** → **Reload** → enable "Camera Overlay":

```
%AppData%\Wisp\plugins\WispWebcamOverlay\
    WispWebcamOverlay.dll
    plugin.json
    OpenCvSharp.dll
    OpenCvSharpExtern.dll
    runtimes\...
```

## Settings

Configure from the plugin's Settings dialog (or edit `%AppData%\Wisp\plugins\.data\wisp.webcam-overlay\settings.json`):

| Key | Default | Description |
|---|---|---|
| `CameraIndex` | `0` | OpenCV camera index (0 = default webcam) |
| `Mirror` | `true` | Horizontal flip (selfie), in preview and export |
| `Opacity` | `1.0` | Overlay opacity (0.1–1.0) |
| `BufferSeconds` | `120` | How much camera footage to keep ready - set ≥ your replay length |
| `SyncOffsetMs` | `0` | Nudge timing if the face leads/lags the action |
| `PosX`/`PosY`/`SizeW`/`SizeH` | bottom-right | Normalised PiP rectangle (updated when you drag/resize in the player) |

> The webcam is active whenever Wisp is buffering (so footage exists for clips) - its LED will be on during gameplay even though nothing is shown live.

## Project Structure

| File | Role |
|---|---|
| `WebcamOverlayPlugin.cs` | Entry point (`IWispPlugin`): lifecycle, the player overlay + sync, `GetExportLayers` |
| `CameraRecorder.cs` | Headless OpenCV capture → rolling on-disk buffer → assemble the clip's tail |
| `OverlaySettings.cs` | Settings DTO (incl. the normalised overlay rect) persisted via `IPluginStorage` |
| `plugin.json` | Manifest read by `PluginManager` (`minApiVersion: 4`) |
