using Glidecast.Model;
using Glidecast.Render;
using SkiaSharp;

namespace Glidecast.Demo;

/// <summary>
/// Produces a synthetic recording (source video + matching input tracks) so the
/// auto-zoom + compositor + export pipeline can be exercised end-to-end without
/// a live desktop. Not part of the real recorder — a self-test fixture.
/// </summary>
public static class DemoGenerator
{
    public static Project Generate(string dir)
    {
        Directory.CreateDirectory(dir);
        int w = 1280, h = 720;
        double fps = 30;
        double durMs = 9000;
        string srcPath = Path.Combine(dir, "source.mp4");

        var p = new Project { Width = w, Height = h, Fps = fps, DurationMs = durMs, SourceVideo = srcPath };

        // Three "buttons" the fake user visits and clicks.
        var targets = new (float x, float y, string label)[]
        {
            (230, 180, "File"),
            (1040, 250, "Build"),
            (640, 560, "Run"),
        };
        // Visit schedule: arrive time (ms) -> click.
        double[] clickTimes = { 1500, 4200, 7000 };

        // Build a cursor path: start center, move to each target in turn.
        var path = new List<(double t, float x, float y)>
        {
            (0, w / 2f, h / 2f),
        };
        for (int i = 0; i < targets.Length; i++)
            path.Add((clickTimes[i], targets[i].x, targets[i].y));
        path.Add((durMs, w / 2f, h / 2f));

        // Sample moves at ~120 Hz along the path.
        for (double t = 0; t <= durMs; t += 8)
        {
            var (x, y) = SamplePath(path, t);
            p.Moves.Add(new MoveEvent { T = t, X = (int)x, Y = (int)y });
        }
        for (int i = 0; i < targets.Length; i++)
            p.Clicks.Add(new ClickEvent { T = clickTimes[i], X = (int)targets[i].x, Y = (int)targets[i].y, Button = 0, Count = 1 });
        // A couple of keystrokes to show the overlay.
        p.Keys.Add(new KeyEvent { T = 4300, Symbol = "Ctrl", Down = true });
        p.Keys.Add(new KeyEvent { T = 4350, Symbol = "B", Down = true });

        // Render synthetic source frames (NO cursor — renderer adds it) to ffmpeg.
        string encArgs =
            $"-y -f rawvideo -pixel_format bgra -video_size {w}x{h} -framerate {fps} -i - " +
            $"-an -c:v libx264 -preset veryfast -pix_fmt yuv420p -crf 20 \"{srcPath}\"";
        var enc = FFmpeg.Start(encArgs, redirectIn: true, redirectOut: false);
        var stream = enc.StandardInput.BaseStream;

        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        var dst = new byte[w * h * 4];
        int totalFrames = (int)(durMs / 1000.0 * fps);

        using var bgPaint = new SKPaint();
        bgPaint.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, h),
            new[] { new SKColor(0xFA, 0xFB, 0xFF), new SKColor(0xE9, 0xED, 0xF5) }, null, SKShaderTileMode.Clamp);

        for (int f = 0; f < totalFrames; f++)
        {
            double t = f / fps * 1000.0;
            canvas.Clear(SKColors.White);
            canvas.DrawRect(new SKRect(0, 0, w, h), bgPaint);

            // Title bar.
            using (var bar = new SKPaint { Color = new SKColor(0x2B, 0x6C, 0xF6) })
                canvas.DrawRect(new SKRect(0, 0, w, 56), bar);
            using (var title = new SKPaint { Color = SKColors.White, TextSize = 24, IsAntialias = true })
                canvas.DrawText("Glidecast Demo App", 24, 37, title);

            // Buttons; flash the one being clicked.
            foreach (var (bx, by, label) in targets)
            {
                bool active = p.Clicks.Any(c => Math.Abs(c.X - bx) < 2 && Math.Abs(c.Y - by) < 2 && Math.Abs(c.T - t) < 180);
                var rect = new SKRect(bx - 90, by - 34, bx + 90, by + 34);
                using var btn = new SKPaint { Color = active ? new SKColor(0x1E, 0x9E, 0x5A) : new SKColor(0xDD, 0xE4, 0xF0), IsAntialias = true };
                canvas.DrawRoundRect(new SKRoundRect(rect, 12, 12), btn);
                using var txt = new SKPaint { Color = active ? SKColors.White : new SKColor(0x33, 0x3A, 0x46), TextSize = 26, IsAntialias = true, TextAlign = SKTextAlign.Center };
                canvas.DrawText(label, bx, by + 9, txt);
            }

            canvas.Flush();
            using (var snap = surface.Snapshot())
            using (var pm = snap.PeekPixels())
                System.Runtime.InteropServices.Marshal.Copy(pm.GetPixels(), dst, 0, dst.Length);
            stream.Write(dst, 0, dst.Length);
        }

        stream.Flush();
        stream.Close();
        enc.WaitForExit(30000);

        p.Save(Path.Combine(dir, "project.json"));
        return p;
    }

    private static (double x, double y) SamplePath(List<(double t, float x, float y)> path, double t)
    {
        if (t <= path[0].t) return (path[0].x, path[0].y);
        if (t >= path[^1].t) return (path[^1].x, path[^1].y);
        int i = 0;
        while (i < path.Count - 1 && path[i + 1].t <= t) i++;
        var a = path[i];
        var b = path[i + 1];
        double f = (t - a.t) / (b.t - a.t);
        f = f * f * (3 - 2 * f); // ease so the cursor accelerates/decelerates
        return (a.x + (b.x - a.x) * f, a.y + (b.y - a.y) * f);
    }
}
