<p align="center">
  <img src="assets/banner.png" alt="Glidecast — screen recordings that zoom, glide and follow your cursor" width="100%" />
</p>

<p align="center">
  <a href="https://github.com/swarnimbandekar/glidecast/releases/latest"><img alt="Download" src="https://img.shields.io/github/v/release/swarnimbandekar/glidecast?label=download&color=7C5CFF&style=flat-square"></a>
  <a href="LICENSE"><img alt="MIT license" src="https://img.shields.io/badge/license-MIT-4F8CFF?style=flat-square"></a>
  <img alt="Windows 10/11" src="https://img.shields.io/badge/Windows-10%20%7C%2011-1F2430?style=flat-square&logo=windows">
  <img alt=".NET 8" src="https://img.shields.io/badge/.NET-8-512BD4?style=flat-square&logo=dotnet">
  <a href="https://github.com/swarnimbandekar/glidecast/actions"><img alt="Build" src="https://img.shields.io/github/actions/workflow/status/swarnimbandekar/glidecast/build.yml?style=flat-square"></a>
</p>

**Glidecast** is a free, open-source screen recorder for Windows that turns raw
captures into polished demo videos — automatically. It records your screen and
your mouse/keyboard separately, then plays them back through a virtual camera
that **zooms in on every click, glides between actions, and follows your
cursor**, framed on a beautiful background with a crisp, smooth cursor.

No timeline wrangling, no keyframes: record, tweak if you like, export.

<p align="center">
  <img src="docs/media/demo.gif" alt="A Glidecast export: the camera zooms onto each click and follows the cursor" width="88%" />
</p>

<p align="center">
  <img src="docs/media/editor.png" alt="The Glidecast editor" width="100%" />
</p>

## Features

- 🎯 **Auto-zoom** — clicks (and the typing that follows) become zoom focuses; nearby clicks merge into one smooth shot.
- 🎥 **Camera that feels filmed** — spring physics, log-space zoom, early lead-in so the zoom lands *on* the click, direct pans between focuses, and optional cursor follow that never lets the pointer leave the frame.
- 🖱️ **Beautiful cursor** — six styles (Classic, Midnight, Soft, Dot, Ring, Spotlight), S–XL sizes, zero-lag smoothing, click squeeze, hide-when-idle.
- ✨ **Click effects** — Ripple, Pulse, Burst or none, in your accent color.
- ⌨️ **Keystroke overlay** — keycaps for the shortcuts you press.
- 🖼️ **Backgrounds** — 12 gradient presets, solid colors, or any wallpaper (with blur); padding, rounded corners and layered shadow.
- ✂️ **Editable zoom timeline** — film strip, drag to move, drag edges to resize, double-click to add, click the preview to aim.
- 📐 **Full screen or any region**, 30/60 fps capture with per-frame timestamps, so video, cursor and zooms stay in sync even when a busy PC drops frames.
- 📤 **Export** to H.264 MP4 at source, 720p, 1080p, 1440p or 4K; 24/30/60 fps.
- 🧰 **CLI** for scripting: `glidecast-cli record | render | demo`.

<p align="center">
  <img src="docs/media/home.png" alt="Home screen" width="49%" />
  <img src="docs/media/cursor.png" alt="Cursor styles" width="49%" />
</p>

## Install

1. Download **`Glidecast-Setup-x.y.z-x64.exe`** from the [latest release](https://github.com/swarnimbandekar/glidecast/releases/latest)
   (or the portable `.zip`).
2. Run it. No admin rights needed. It can install **FFmpeg** for you via
   `winget` — Glidecast uses FFmpeg to encode and decode video.

> **"Windows protected your PC"?** Release builds aren't code-signed yet, so
> SmartScreen may warn on first run. Click **More info → Run anyway**, or build
> from source.

Requirements: Windows 10 1809+ / Windows 11 (x64), FFmpeg
(`winget install Gyan.FFmpeg.Essentials`).

## How to use

1. **Record** — pick *Full screen* or *Custom area*, press **Start recording**.
   After a 3-2-1 countdown a small toolbar shows the timer; it never appears in
   your video. Press **Stop** when done.
2. **Edit** — the editor opens with zooms already placed on your clicks.
   Style the background, frame and cursor in the inspector; adjust zooms on the
   timeline.
3. **Export** — choose resolution, frame rate and quality.

| Shortcut | Action |
|---|---|
| `Space` | Play / pause |
| `←` / `→` | Step one frame (`Shift`: 5 s) |
| `Z` | Add a zoom at the playhead |
| `Delete` | Delete the selected zoom |
| `Ctrl+E` | Export |

Projects live in `Documents\Glidecast` (or `%LOCALAPPDATA%\Glidecast\Recordings`
if Windows *Controlled Folder Access* blocks Documents). Each project is a
folder with `source.mp4` and a human-readable `project.json`.

### Command line

```powershell
glidecast-cli record 30 .\my-take          # record 30 s (or press Enter to stop)
glidecast-cli render .\my-take out.mp4     # auto-zoom and export
glidecast-cli demo .\demo                  # synthetic self-test
```

## Build from source

```powershell
git clone https://github.com/swarnimbandekar/glidecast
cd glidecast
dotnet build Glidecast.sln -c Release
dotnet run --project src/Glidecast          # launch the app
./build.ps1                                 # portable zip + installer (needs Inno Setup 6)
```

Needs the .NET 8 SDK and FFmpeg on `PATH` (or `GLIDECAST_FFMPEG=C:\path\to\ffmpeg.exe`).

## How it works

Glidecast never bakes the cursor into the video. It records three things on one
clock — screen frames (each with its own capture timestamp), mouse moves/clicks,
and key presses — and composites the final video afterwards with SkiaSharp:

```
 screen ──▶ source.mp4 + frame timestamps ─┐
 mouse  ──▶ moves / clicks ────────────────┼─▶ ZoomAuthor ─▶ CameraTrack (springs)
 keys   ──▶ key presses ───────────────────┘                     │
                                                                  ▼
             background ▸ rounded card ▸ screen ▸ cursor ▸ effects ▸ keycaps ─▶ FFmpeg ─▶ MP4
```

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the details.

## Contributing

Issues and pull requests are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md).
Ideas on the roadmap: audio capture, webcam overlay, undo/redo, multi-monitor
region picking, GPU encoding, trimming.

## License

[MIT](LICENSE) © 2026 Swarnim Bandekar and Glidecast contributors.
Third-party components are listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
