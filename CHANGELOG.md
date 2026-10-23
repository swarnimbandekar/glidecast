# Changelog

## 1.2.2 — 2026-10-24

- **Fixed "Could not start recording … untrusted mount point".** On some PCs
  Windows refuses to start ffmpeg through winget's `WinGet\Linksfmpeg.exe`
  symbolic link. Glidecast now uses the ffmpeg.exe bundled next to the app
  first, and starts the real file behind a symbolic link when it has to use
  another ffmpeg.

## 1.2.1 — 2026-10-24

- **Update check no longer fails silently.** GitHub's API allows only 60
  anonymous requests an hour per address and answers 403 beyond that (shared
  networks and VPNs hit it), so Glidecast could miss a new release without
  saying anything. It now falls back to github.com's release pages, which have
  no such limit.
- New launcher, get-ready bar and editor screenshots in the README.

## 1.2.0 — 2026-10-23

- **New launcher**: the home screen is now a compact window. Pick *Full screen*,
  *Custom area* or the new *Window* mode (hover a window to outline it, click to
  record it), then choose your camera, microphone and system sound from
  dropdowns. Projects, Open and the demo moved into a *Projects* menu in the
  title bar.
- **Get-ready step**: after you pick what to record, the area is outlined on
  screen and a bar shows the inputs (with a live microphone level), the camera
  shape, a 0/3/5 second countdown, 30 or 60 fps, and *Annotate as I record*.
  Nothing on it is captured.
- **Camera**: your webcam floats on the desktop as a circle, rounded square or
  oval. Drag it, resize it, and it appears in the video exactly where you left
  it. It is recorded separately, so the shape, size, position and mirroring can
  still be changed in the editor (new *Media* tab).
- **Audio**: record system sound (any output device) and a microphone. Each
  track has its own volume and mute in the editor, plays with the preview and
  is mixed into the export.
- **Annotate as I record**: every click becomes a numbered step with a box and
  a caption when you stop. Press **F9** during a take to highlight whatever is
  under the cursor at that moment.
- **Teleprompter**: a floating script that scrolls at your reading speed. It
  stays on top and is never recorded.
- **Undo and redo** in the editor (`Ctrl+Z`, `Ctrl+Y`, also buttons in the
  title bar) for zooms, annotations, style and audio settings.
- **Timeline zoom**: `Ctrl+scroll` zooms the time axis around the pointer,
  scrolling pans it, and a scroll bar shows where you are. Also `Ctrl+=`,
  `Ctrl+-` and `Ctrl+0`.
- **Annotation editing**: select an annotation by clicking it on the preview,
  drag it to move it, drag a box by its corner to resize it, and drag the ends
  of its bar on the Notes lane to retime it. Clicking one used to aim the
  selected zoom instead.
- Crash recovery now also restores the webcam and audio of an interrupted take.

## 1.1.0 — 2026-10-23

- **In-app updates**: Glidecast checks GitHub for a new release when it starts
  and offers *Update now* or *Skip this version*. Updating downloads the
  installer (or the portable zip), verifies it against the release's
  SHA256SUMS.txt, installs it and reopens the app. Set
  `GLIDECAST_NO_UPDATE_CHECK=1` to turn the check off.
- **New look**: the whole UI was redesigned around neutral graphite surfaces
  and a single blue accent (no gradients or glows), with Inter as the bundled
  typeface. Calmer home screen, an icon rail for the editor inspector, and a
  restyled export dialog, recording toolbar and region picker. The logo, icon,
  banner and installer art match, and new recordings default to the blue
  "Cobalt" background and accent.
- **Smart framing**: every click records the UI element under it (UI
  Automation), so a zoom fits the clicked button, or the whole menu or dialog,
  instead of a fixed factor around the click point.
- **Follow typing**: the text caret is sampled while you type; the camera zooms
  on it and follows the words, including typing that wasn't preceded by a click.
- **Zoom presets**: Subtle, Balanced, Dramatic and Tutorial.
- **Annotations** on a new Notes lane: step numbers, highlight boxes,
  spotlights, arrows and callouts. *Generate from clicks* builds a walkthrough
  automatically, with captions like Click “Save” from the app's own labels.
- **Reframing**: 16:9, 9:16 and 1:1 canvases with the camera re-planned for each
  shape; export all three in one go.
- **Cursor motion trail**.
- **Looks (brand kits)**: save background, frame, cursor, accent, logo and canvas
  as a look; six built-ins; set a default for new recordings. Plus a brand logo
  overlay.
- **Crash-safe recording**: fragmented MP4 while recording plus a journal of
  frame times and input; interrupted takes are recovered on the next launch.
- **GPU encoding** (NVENC, Quick Sync, AMF) with automatic CPU fallback, and a
  compositor that is about 4× faster per frame and renders frames in parallel
  (a 9 s 1080p export went from 51 s to 18 s on a 12-thread laptop).
- **CLI**: `batch`, `annotate`, `look`, `encoders`, `recover`, `bench`, and
  `render --aspect/--res/--fps/--quality/--encoder/--look/--preset`.
- **Plugin hooks**: run your own commands after recording or exporting
  (`hooks.json`).
- FFmpeg can be bundled with the installer and portable zip (`build.ps1`).
- Keycaps show real key names for synthesized input (no more `VK65`).

## 1.0.0 — 2026-10-20

First public release.

- Full-screen and region recording at 30/60 fps with per-frame timestamps.
- Auto-zoom from clicks and typing; spring-physics camera with cursor follow.
- Editor: live preview, film-strip timeline, draggable/resizable zooms.
- Backgrounds (gradients, solid, wallpaper with blur), padding, corners, shadow.
- Six cursor styles, four click effects, zero-lag smoothing, keystroke keycaps.
- MP4 export up to 4K / 60 fps.
- `glidecast-cli` for scripted record/render.
- Installer with optional FFmpeg setup via winget.
