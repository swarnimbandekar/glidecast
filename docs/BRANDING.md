# Branding

<img src="../assets/logo-256.png" width="96" align="right" alt="Glidecast logo" />

**Name:** Glidecast (one word, capital G).
**Idea:** the camera glides to whatever your cursor is doing. The mark is a
flat blue tile with focus brackets and a cursor gliding into frame.

| Token | Value |
|---|---|
| Accent (blue) | `#3D7EFF` (hover `#5A92FF`, tile shade `#2F6BE8`) |
| Ink (background) | `#0F0F10` |
| Surfaces | `#151516` / `#1B1B1D` / `#232325` |
| Text | `#EDEDEF`, secondary `#9B9BA1` |
| Record / danger | `#E5484D` |
| Default video background | "Cobalt" gradient `#2F5FD0` → `#0B1226` |
| Typeface | Inter (bundled, SIL OFL) |

The UI uses no gradients or glows; the accent is the only saturated color.
All UI tokens live in `src/Glidecast/Theme.xaml`.

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
