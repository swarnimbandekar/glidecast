# Third-party notices

Glidecast is MIT-licensed (see `LICENSE`). It builds on the following
third-party software.

## Bundled with Glidecast

| Component | License | Notes |
|---|---|---|
| [.NET runtime & WPF](https://github.com/dotnet/runtime) | MIT | Included in self-contained builds. |
| [SkiaSharp](https://github.com/mono/SkiaSharp) | MIT | 2D rendering for the compositor. |
| [Skia](https://skia.org/) (native, via SkiaSharp) | BSD-3-Clause | |

## Used but not bundled

| Component | License | Notes |
|---|---|---|
| [FFmpeg](https://ffmpeg.org/) | LGPL-2.1+ / GPL depending on build | Glidecast runs `ffmpeg.exe` as a separate program for encoding and decoding. It is **not** shipped with Glidecast; the installer can install it from winget (`Gyan.FFmpeg.Essentials`), and its own license applies. |
| Segoe UI / Segoe Fluent Icons | Microsoft | System fonts used by the UI; not redistributed. |

"Windows" is a trademark of Microsoft Corporation. All other trademarks are
the property of their respective owners.
