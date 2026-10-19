# Architecture

Glidecast is three .NET 8 projects plus a branding tool:

| Project | What it is |
|---|---|
| `src/Glidecast.Engine` | Class library: capture, auto-zoom, camera, compositor, export. No UI. |
| `src/Glidecast` | WPF desktop app (`Glidecast.exe`): home/record screen, editor, export dialog. |
| `src/Glidecast.Cli` | Console app (`glidecast-cli.exe`): `record`, `render`, `demo`. |
| `tools/BrandKit` | Regenerates the logo, icon, banner and installer art in `assets/`. |

## Recording (`Engine/Capture`)

- **`ScreenRecorder`** grabs the screen (or a region) with GDI `BitBlt` on a
  capture thread and hands frames to a separate encoder thread that pipes raw
  BGRA into `ffmpeg` (`libx264 ultrafast`). The OS cursor is *not* captured.
  Every frame is stamped with the shared `Stopwatch` clock
  (`Project.FrameTimes`). If the machine can't hold the target rate, ticks are
  dropped rather than bunched, so timestamps stay honest.
- **`InputHook`** installs low-level mouse and keyboard hooks on its own
  message-pump thread and logs moves (~120 Hz), clicks and key presses on the
  same clock.

A project is a folder: `source.mp4` + `project.json` (`Model/Project.cs`).

## Timing (`Render/FrameClock`)

All editing and rendering happens in **project time** (the input clock).
`FrameClock` maps a project time to the source frame captured at or just before
it, using `FrameTimes`. Older projects without timestamps get them estimated
from the real frame count. This is what keeps the picture, cursor, clicks and
zooms in sync.

## Auto-zoom and camera (`Engine/AutoZoom`)

- **`ZoomAuthor.BuildSegments`** clusters clicks by time and distance into
  `ZoomSegment`s (start, end, scale, focus point), extended through typing that
  follows a click. These are what the editor's zoom lane edits.
- **`CameraTrack`** turns segments into motion by simulating critically damped
  springs at 480 Hz and storing 120 Hz samples:
  - zoom is simulated in log space, so 1×→2× feels like 2×→4×;
  - a focus becomes the target `4/ω` before its start, so the zoom has ~95%
    settled when the click lands;
  - consecutive focuses pan directly instead of zooming out and back in;
  - with *Follow cursor*, a dead-zone keeps the cursor framed, and a hard
    constraint guarantees it never leaves the view;
  - the view is clamped so it never shows past the screen edge.

## Cursor (`Render/CursorTrack`, `Render/CursorPainter`)

The cursor path is smoothed with a centred (zero-phase) Gaussian, so it glides
without lagging, then pulled back onto the exact point around each click.
`CursorPainter` draws six vector cursor styles and four click effects, scaled
with the zoom so they stay crisp.

## Compositing and export (`Render/Compositor`, `Render/VideoRenderer`)

For each output frame: background (gradient with bloom, solid, or blurred
wallpaper) → layered shadow → the screen as a rounded card under the camera
matrix → hairline edge → click effects → cursor → keycaps. `VideoRenderer`
decodes the source with ffmpeg, picks frames through `FrameClock`, composites,
and pipes to an encoder ffmpeg.

## App (`src/Glidecast`)

- `PreviewPlayer` runs on a worker thread: it decodes (`FrameReader`),
  composites at 60 Hz on the wall clock and writes into a `WriteableBitmap`.
- `TimelineControl` is a custom-drawn control: ruler, film strip, zoom lane,
  input lane, playhead.
- `Theme.xaml` holds the design system (colors, typography, controls).
