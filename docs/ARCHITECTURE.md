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

## Smart framing and typing (`Capture/UiProbe`)

`InputHook` hands each click and (throttled) key press to `UiProbe`, which runs
UI Automation on its own thread (a hook callback must return in milliseconds):

- for a click, `AutomationElement.FromPoint` gives the control's bounds, name
  and role, plus the nearest enclosing menu / list / dialog / toolbar that is
  meaningfully bigger than the control and smaller than ~45% of the screen;
- for typing, the caret comes from `GetGUIThreadInfo` (system caret), else the
  UI Automation text selection, else the focused field if it is small.

`ZoomAuthor` clusters clicks *and* caret samples. With smart framing a cluster
is framed by the panel that contains all its clicks (if that fits at ≥1.3×), or
the union of the clicked controls; elements too big to frame (a whole document)
count as plain points. `CameraTrack` follows the caret instead of the mouse while
the latest caret sample is newer than the last mouse movement.

## Canvas shapes (`Render/Canvas`)

A canvas other than *Source* shows a `ViewW × ViewH` slice of the screen (the
largest rect of that shape). `CameraTrack` clamps to that view, keeps following
the action while not zoomed, and tones zooms down so a 2× focus shows about the
same screen area as it would on the full canvas. The compositor's style unit
follows the view, so cursor and badges stay proportionate.

## Annotations (`Model/Annotation`, `Render/AnnotationPainter`)

Annotations live in source-screen space (they ride along with the camera).
`AnnotationAuthor` makes a step badge, a highlight box and a caption per click
from the recorded UI element; regenerating replaces only `Auto` ones.

## Compositing and export (`Render/Compositor`, `Render/VideoRenderer`)

For each output frame: background (gradient with bloom, solid, or blurred
wallpaper; rendered once and cached) → layered shadow (mask-filter blurs, Skia's
fast path) → the screen as a rounded card under the camera matrix (an image
shader; bicubic only when magnified) → hairline edge → annotations → click
effects → trail → cursor → keycaps → logo. When the card covers the frame the
background and shadow are skipped. `VideoRenderer` decodes the source with
ffmpeg, picks frames through `FrameClock`, composites a batch of frames in
parallel (one `Compositor` per worker), and pipes them in order to an encoder
ffmpeg: NVENC / AMF when available, else x264, retried on the CPU if a GPU
encoder fails. `glidecast-cli bench <project>` prints compositor ms/frame.

## Crash safety (`Capture/RecordingJournal`)

While recording, `source.mp4` is a fragmented MP4 (a fragment per 2 s keyframe
interval), and once a second `RecordingJournal` appends new frame timestamps and
input events to `frames.log` / `input.log`. A clean stop remuxes the video to a
regular MP4 and deletes the journal. On launch (or `glidecast-cli recover`),
`RecordingRecovery` rebuilds `project.json` for any folder with a journal but no
project, trimmed to the last complete fragment.

## Looks and hooks (`Model/Looks`, `Plugins/Hooks`)

Looks are `StyleSettings` (+ optional `ZoomSettings`) saved as JSON in
`%APPDATA%\Glidecast\looks`; `settings.json` names the default look applied to new
recordings. Hooks are commands from `%APPDATA%\Glidecast\hooks.json` run after
recording and export; see [EXTENDING.md](EXTENDING.md).

## App (`src/Glidecast`)

- `PreviewPlayer` runs on a worker thread: it decodes (`FrameReader`),
  composites at 60 Hz on the wall clock and writes into a `WriteableBitmap`.
- `TimelineControl` is a custom-drawn control: ruler, film strip, zoom lane,
  notes (annotation) lane, input lane, playhead.
- `Theme.xaml` holds the design system (colors, typography, controls).
