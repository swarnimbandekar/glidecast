# Extending Glidecast

Glidecast keeps everything in plain files, so you can script it, share looks,
and hook it into your own tools.

## Projects

A project is a folder:

| File | What it is |
|---|---|
| `source.mp4` | The raw screen capture (no cursor). |
| `project.json` | Everything else, human-readable: size, frame timestamps, mouse moves, clicks (with the UI element under each: bounds, name, role), key presses, caret samples, zoom segments, annotations, style. |
| `thumb.jpg` | Home-screen thumbnail (regenerated if missing). |

While a take is being recorded the folder also holds `recording.json`,
`frames.log` and `input.log`, the crash-recovery journal. They are deleted on a
clean stop.

Edit `project.json` by hand or with a script, then `glidecast-cli render` it.
Fields you leave out take their defaults; `ZoomSegments` absent means "author
the zooms automatically".

## Looks (brand kits)

A look is a JSON file in `%APPDATA%\Glidecast\looks` (or `GLIDECAST_LOOKS`):

```json
{
  "Name": "Acme",
  "Style": {
    "Background": "Gradient", "BgTop": "#1E293B", "BgBottom": "#0B1018",
    "PaddingFraction": 0.08, "CornerRadius": 20, "ShadowBlur": 60,
    "Accent": "#FF4D6A", "CursorStyle": "Midnight", "CursorTrail": true,
    "LogoImage": "C:\\Brand\\acme-logo.png", "LogoCorner": "BottomRight",
    "Aspect": "Source"
  },
  "Zoom": { "Preset": "Tutorial", "MaxScale": 2.2, "Speed": "Gentle" },
  "Steps": true
}
```

Drop files in to share them with a team. `glidecast-cli look default Acme`
applies it to every new recording; `render --look Acme` uses it for one export
without changing the project.

## Hooks

Put commands in `%APPDATA%\Glidecast\hooks.json` (or the file named by
`GLIDECAST_HOOKS`) to run them after Glidecast events:

```json
{
  // after a take is saved
  "afterRecord": [ "powershell -NoProfile -Command Copy-Item -Recurse '{project}' 'D:\\Backups\\'" ],
  // after each exported file (one per shape when exporting several)
  "afterExport": [
    "powershell -NoProfile -File C:\\scripts\\upload.ps1 -Path \"{output}\"",
    "C:\\tools\\notify.exe \"Exported {output}\""
  ]
}
```

- `{project}` is the project folder, `{output}` the exported MP4.
- The environment also carries `GLIDECAST_EVENT`, `GLIDECAST_PROJECT` and
  `GLIDECAST_OUTPUT`.
- Commands run through `cmd.exe`, in the background, with the project folder as
  the working directory. The app doesn't wait for them; the CLI waits up to two
  minutes each.
- Their output and exit codes go to `%LOCALAPPDATA%\Glidecast\hooks.log`.
- Hooks are your own commands: Glidecast never installs or downloads any.

## Command line

`glidecast-cli help` lists everything. Useful in scripts:

```powershell
glidecast-cli batch .\takes --aspect all --res 1080 --encoder auto --out .\exports
glidecast-cli render .\take out.mp4 --preset tutorial --look "Clean light"
glidecast-cli annotate .\take --no-captions
glidecast-cli bench .\take        # compositor ms/frame, for performance work
```
