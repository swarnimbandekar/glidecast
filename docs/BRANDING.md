# Branding

<img src="../assets/logo-256.png" width="96" align="right" alt="Glidecast logo" />

**Name:** Glidecast (one word, capital G).
**Idea:** the camera glides to whatever your cursor is doing. The mark is a
violet→blue tile with focus brackets and a cursor gliding into frame.

| Token | Value |
|---|---|
| Violet | `#8A5CFF` |
| Blue | `#3D7BFF` / `#4F8CFF` |
| Accent (UI) | `#7C5CFF` |
| Ink (background) | `#0E1013` |
| Typeface | Segoe UI Variable Display / Text |

## Regenerating assets

Everything in `assets/` is drawn in code by `tools/BrandKit`:

```powershell
dotnet run --project tools/BrandKit -c Release -- assets
```

This writes `glidecast.ico` (16–256 px), `logo-*.png`, `banner.png` (1280×640,
also used as the GitHub social preview) and the installer art. After changing
the icon, copy `assets/glidecast.ico` and `assets/logo-64.png` into
`src/Glidecast/Assets/` and regenerate the installer bitmaps:

```powershell
ffmpeg -y -i assets/installer-side.png -vf scale=164:314 -pix_fmt bgr24 installer/wizard-side.bmp
ffmpeg -y -i assets/installer-side.png -pix_fmt bgr24 installer/wizard-side@2x.bmp
ffmpeg -y -i assets/installer-small.png -vf scale=55:55 -pix_fmt bgr24 installer/wizard-small.bmp
ffmpeg -y -i assets/installer-small.png -pix_fmt bgr24 installer/wizard-small@2x.bmp
```
