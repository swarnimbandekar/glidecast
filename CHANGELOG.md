# Changelog

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
