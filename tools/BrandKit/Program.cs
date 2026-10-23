using SkiaSharp;

// Glidecast brand kit. The mark: a flat blue tile, focus brackets, and a
// cursor gliding into frame — "the camera follows your cursor".
string outDir = args.Length > 0 ? args[0] : "assets";
Directory.CreateDirectory(outDir);

// Same tokens as the app (src/Glidecast/Theme.xaml).
var blue = new SKColor(0x3D, 0x7E, 0xFF);
var blueDeep = new SKColor(0x2F, 0x6B, 0xE8);
var ink = new SKColor(0x0F, 0x0F, 0x10);
var subtext = new SKColor(0xB4, 0xB4, 0xBA);
var muted = new SKColor(0x80, 0x80, 0x86);

// ---------------------------------------------------------------- the mark
void DrawMark(SKCanvas c, float size, bool detail)
{
    c.Save();
    c.Scale(size / 256f);
    var tile = new SKRoundRect(new SKRect(8, 8, 248, 248), 58, 58);
    using (var bg = new SKPaint { IsAntialias = true })
    {
        // One hue, top to bottom: reads as flat, keeps a little depth at icon sizes.
        bg.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 8), new SKPoint(0, 248),
            new[] { blue, blueDeep }, null, SKShaderTileMode.Clamp);
        c.DrawRoundRect(tile, bg);
    }

    if (detail)
    {
        // Focus brackets.
        using var br = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 13, StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round, Color = new SKColor(255, 255, 255, 150),
        };
        float a = 54, b = 202, l = 36;
        void Corner(float x, float y, float dx, float dy)
        {
            using var p = new SKPath();
            p.MoveTo(x, y + dy * l);
            p.LineTo(x, y);
            p.LineTo(x + dx * l, y);
            c.DrawPath(p, br);
        }
        Corner(a, a, 1, 1); Corner(b, a, -1, 1); Corner(a, b, 1, -1); Corner(b, b, -1, -1);
    }

    // Glide trail behind the cursor.
    using (var trail = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeWidth = detail ? 11 : 16 })
    {
        trail.Color = new SKColor(255, 255, 255, 110);
        if (detail) c.DrawLine(150, 168, 176, 194, trail);
        else c.DrawLine(158, 176, 192, 210, trail);
        if (detail)
        {
            trail.Color = new SKColor(255, 255, 255, 60);
            c.DrawLine(170, 150, 188, 168, trail);
        }
    }

    // Cursor arrow.
    using var arrow = new SKPath();
    float s = detail ? 5.9f : 7.2f;
    float ox = detail ? 96 : 84, oy = detail ? 70 : 52;
    (float X, float Y)[] pts = { (0, 0), (0, 17.5f), (4.4f, 13.6f), (7.4f, 20.4f), (10.6f, 19f), (7.7f, 12.4f), (13.4f, 12f) };
    arrow.MoveTo(ox + pts[0].X * s, oy + pts[0].Y * s);
    foreach (var (x, y) in pts.Skip(1)) arrow.LineTo(ox + x * s, oy + y * s);
    arrow.Close();
    using (var sh = new SKPaint { IsAntialias = true, Color = new SKColor(0x08, 0x1A, 0x4D, 110), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 7) })
    {
        c.Save();
        c.Translate(3, 7);
        c.DrawPath(arrow, sh);
        c.Restore();
    }
    using (var fill = new SKPaint { IsAntialias = true, Color = SKColors.White, StrokeJoin = SKStrokeJoin.Round })
    {
        c.DrawPath(arrow, fill);
        fill.Style = SKPaintStyle.Stroke;
        fill.StrokeWidth = 5; // soften the tips
        c.DrawPath(arrow, fill);
    }
    c.Restore();
}

SKBitmap Mark(int px)
{
    var bmp = new SKBitmap(new SKImageInfo(px, px, SKColorType.Bgra8888, SKAlphaType.Premul));
    using var c = new SKCanvas(bmp);
    c.Clear(SKColors.Transparent);
    DrawMark(c, px, detail: px >= 48);
    return bmp;
}

byte[] Png(SKBitmap b)
{
    using var d = b.Encode(SKEncodedImageFormat.Png, 100);
    return d.ToArray();
}

// ---------------------------------------------------------------- icon (.ico with PNG entries)
int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
var images = sizes.Select(sz => Png(Mark(sz))).ToList();
using (var ico = new BinaryWriter(File.Create(Path.Combine(outDir, "glidecast.ico"))))
{
    ico.Write((short)0);
    ico.Write((short)1);
    ico.Write((short)sizes.Length);
    int offset = 6 + 16 * sizes.Length;
    for (int i = 0; i < sizes.Length; i++)
    {
        byte dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
        ico.Write(dim);
        ico.Write(dim);
        ico.Write((byte)0);
        ico.Write((byte)0);
        ico.Write((short)1);
        ico.Write((short)32);
        ico.Write(images[i].Length);
        ico.Write(offset);
        offset += images[i].Length;
    }
    foreach (var img in images) ico.Write(img);
}

foreach (int sz in new[] { 64, 256, 512, 1024 })
    File.WriteAllBytes(Path.Combine(outDir, $"logo-{sz}.png"), Png(Mark(sz)));

// ---------------------------------------------------------------- wordmark helpers
// The app's typeface (Inter, bundled in src/Glidecast/Fonts); Segoe UI if run elsewhere.
SKTypeface Face(SKFontStyleWeight w)
{
    string file = Path.Combine("src", "Glidecast", "Fonts", w >= SKFontStyleWeight.SemiBold ? "Inter-SemiBold.ttf" : "Inter-Regular.ttf");
    return (File.Exists(file) ? SKTypeface.FromFile(file) : null)
        ?? SKTypeface.FromFamilyName("Segoe UI", w, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
}

void Backdrop(SKCanvas c, int w, int h)
{
    c.Clear(ink);
    using var glow = new SKPaint();
    glow.Shader = SKShader.CreateRadialGradient(new SKPoint(w * 0.2f, h * 0.1f), w * 0.75f,
        new[] { blue.WithAlpha(46), blue.WithAlpha(0) }, null, SKShaderTileMode.Clamp);
    c.DrawRect(new SKRect(0, 0, w, h), glow);
}

// ---------------------------------------------------------------- social preview / README banner (1280x640)
{
    int w = 1280, h = 640;
    using var bmp = new SKBitmap(w, h);
    using var c = new SKCanvas(bmp);
    Backdrop(c, w, h);
    c.Save();
    c.Translate(120, 200);
    DrawMark(c, 240, true);
    c.Restore();
    using var title = new SKPaint { IsAntialias = true, Color = SKColors.White, TextSize = 112, Typeface = Face(SKFontStyleWeight.SemiBold) };
    c.DrawText("Glidecast", 420, 318, title);
    using var sub = new SKPaint { IsAntialias = true, Color = subtext, TextSize = 38, Typeface = Face(SKFontStyleWeight.Normal) };
    c.DrawText("Screen recordings that zoom, glide and", 424, 384, sub);
    c.DrawText("follow your cursor — automatically.", 424, 432, sub);
    using var tag = new SKPaint { IsAntialias = true, Color = muted, TextSize = 26, Typeface = Face(SKFontStyleWeight.Normal) };
    c.DrawText("Free & open source  ·  Windows  ·  MIT", 424, 500, tag);
    File.WriteAllBytes(Path.Combine(outDir, "banner.png"), Png(bmp));
}

// ---------------------------------------------------------------- installer art (Inno Setup side 164x314, small 55x55; drawn at 2x)
{
    int w = 328, h = 628;
    using var bmp = new SKBitmap(w, h);
    using var c = new SKCanvas(bmp);
    Backdrop(c, w, h);
    c.Save();
    c.Translate(64, 150);
    DrawMark(c, 200, true);
    c.Restore();
    using var title = new SKPaint { IsAntialias = true, Color = SKColors.White, TextSize = 52, Typeface = Face(SKFontStyleWeight.SemiBold), TextAlign = SKTextAlign.Center };
    c.DrawText("Glidecast", w / 2f, 440, title);
    using var sub = new SKPaint { IsAntialias = true, Color = subtext, TextSize = 22, Typeface = Face(SKFontStyleWeight.Normal), TextAlign = SKTextAlign.Center };
    c.DrawText("auto-zoom screen recorder", w / 2f, 482, sub);
    File.WriteAllBytes(Path.Combine(outDir, "installer-side.png"), Png(bmp));
}
{
    using var bmp = new SKBitmap(110, 110);
    using var c = new SKCanvas(bmp);
    c.Clear(SKColors.White);
    DrawMark(c, 110, true);
    File.WriteAllBytes(Path.Combine(outDir, "installer-small.png"), Png(bmp));
}
Console.WriteLine("Brand assets written to " + Path.GetFullPath(outDir));
