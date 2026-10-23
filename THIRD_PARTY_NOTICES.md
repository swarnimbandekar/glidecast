# Third-party notices

Glidecast is MIT-licensed (see `LICENSE`). It builds on the following
third-party software.

## Bundled with Glidecast

| Component | License | Notes |
|---|---|---|
| [.NET runtime & WPF](https://github.com/dotnet/runtime) | MIT | Included in self-contained builds. |
| [SkiaSharp](https://github.com/mono/SkiaSharp) | MIT | 2D rendering for the compositor. |
| [Skia](https://skia.org/) (native, via SkiaSharp) | BSD-3-Clause | |
| [NAudio](https://github.com/naudio/NAudio) 2.2.1 by Mark Heath | MIT | System sound (WASAPI loopback) and microphone capture. |
| [Inter](https://github.com/rsms/inter) 3.19 by Rasmus Andersson | SIL Open Font License 1.1 | UI typeface, embedded in `Glidecast.exe` (`src/Glidecast/Fonts`). |

## FFmpeg

[FFmpeg](https://ffmpeg.org/) (LGPL-2.1+ or GPL, depending on the build) is
used as a separate program (`ffmpeg.exe`) for encoding and decoding; Glidecast
does not link to it. Release builds made with `build.ps1` bundle an
`ffmpeg.exe` next to `Glidecast.exe`, with that build's license as
`FFMPEG-LICENSE.txt`. The source code of the bundled build is available from
its publisher (for the gyan.dev builds, see https://www.gyan.dev/ffmpeg/builds/
and https://github.com/FFmpeg/FFmpeg). Builds made with `-NoFFmpeg` don't
include it; the installer then offers to install it from winget
(`Gyan.FFmpeg.Essentials`).

## Used but not bundled

| Component | License | Notes |
|---|---|---|
| Segoe Fluent Icons / Segoe UI | Microsoft | System icon font (and text fallback) used by the UI; not redistributed. |

"Windows" is a trademark of Microsoft Corporation. All other trademarks are
the property of their respective owners.
