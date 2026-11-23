using Glidecast.Model;
using SkiaSharp;

namespace Glidecast.Render;

/// <summary>
/// Renders pressed keys and keyboard shortcuts as clean, grouped keycaps.
/// </summary>
public static class KeycapPainter
{
    private static readonly HashSet<string> Modifiers = new(StringComparer.OrdinalIgnoreCase)
        { "Ctrl", "Shift", "Alt", "Win" };

    private static readonly HashSet<string> SpecialKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Ctrl", "Shift", "Alt", "Win",
        "Enter", "Esc", "Tab", "Bksp", "Del", "Ins",
        "Home", "End", "PgUp", "PgDn", "Caps", "PrtSc", "Space",
        "↑", "↓", "←", "→",
        "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12",
        "F13", "F14", "F15", "F16", "F17", "F18", "F19", "F20", "F21", "F22", "F23", "F24",
    };

    private sealed class KeyCapItem
    {
        public required string Label { get; init; }
        public required double T { get; set; }
        public bool IsModifier { get; init; }
        public bool JoinedToPrev { get; init; }
    }

    public static string NormalizeSymbol(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "?";
        string s = raw.Trim();
        return s switch
        {
            "Right Ctrl" or "Left Ctrl" or "Ctrl" or "Control" => "Ctrl",
            "Right Shift" or "Left Shift" or "Shift" => "Shift",
            "Right Alt" or "Left Alt" or "Alt" => "Alt",
            "Left Windows" or "Right Windows" or "LWin" or "RWin" or "Win" => "Win",
            "Return" or "Enter" or "Num Enter" => "Enter",
            "Escape" or "Esc" => "Esc",
            "Backspace" or "Back" => "Bksp",
            "Delete" or "Del" => "Del",
            "Insert" or "Ins" => "Ins",
            "Page Up" or "Prior" => "PgUp",
            "Page Down" or "Next" => "PgDn",
            "Caps Lock" or "Capital" => "Caps",
            "Print Screen" or "Snapshot" => "PrtSc",
            "Up" => "↑",
            "Down" => "↓",
            "Left" => "←",
            "Right" => "→",
            _ => s.Length == 1 && char.IsLetter(s[0]) ? s.ToUpperInvariant() : s,
        };
    }

    public static void Draw(SKCanvas canvas, IReadOnlyList<KeyEvent> keys, StyleSettings st,
        int outW, int outH, float unit, double tMs)
    {
        if (keys.Count == 0) return;

        double windowMs = Math.Clamp(st.KeystrokeDuration, 0.4f, 5.0f) * 1000.0;
        var items = BuildItems(keys, tMs, windowMs, st.KeystrokeShortcutsOnly);
        if (items.Count == 0) return;

        double newestT = items[^1].T;
        float fadeOut = (float)Math.Clamp((windowMs - (tMs - newestT)) / 250.0, 0, 1);
        float trayIn = (float)Math.Clamp((tMs - items[0].T) / 100.0, 0, 1);
        float alpha = fadeOut * trayIn;
        if (alpha <= 0.01f) return;

        float sizeScale = Math.Clamp(st.KeystrokeSize, 0.5f, 2.0f);
        float u = unit * 1.15f * sizeScale;

        using var font = new SKPaint
        {
            IsAntialias = true,
            TextSize = 20 * u,
            Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold),
        };
        using var plusFont = new SKPaint
        {
            IsAntialias = true,
            TextSize = 16 * u,
            Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold),
        };

        float capH = 40 * u;
        float gap = 7 * u;
        float plusW = 14 * u;
        float padX = 13 * u;
        float trayPadX = 10 * u;
        float trayPadY = 9 * u;

        var widths = new float[items.Count];
        float totalW = 0;
        for (int i = 0; i < items.Count; i++)
        {
            float w = Math.Max(capH, font.MeasureText(items[i].Label) + 2 * padX);
            widths[i] = w;
            totalW += w;
            if (i > 0) totalW += items[i].JoinedToPrev ? (plusW + 2 * 3 * u) : gap;
        }

        float trayW = totalW + 2 * trayPadX;
        float trayH = capH + 2 * trayPadY + 2 * u;

        float marginX = Math.Max(28 * unit, outW * 0.035f);
        float marginY = Math.Max(28 * unit, outH * 0.035f);

        float trayLeft = st.KeystrokePosition switch
        {
            KeystrokePosition.BottomLeft or KeystrokePosition.TopLeft => marginX,
            KeystrokePosition.BottomRight or KeystrokePosition.TopRight => outW - marginX - trayW,
            _ => (outW - trayW) / 2f,
        };
        bool isTop = st.KeystrokePosition is KeystrokePosition.TopCenter or KeystrokePosition.TopLeft or KeystrokePosition.TopRight;
        float slideOffset = (1 - trayIn) * (isTop ? -8 * u : 8 * u);
        float trayTop = (isTop ? marginY : outH - marginY - trayH) + slideOffset;

        var accent = StyleSettings.ParseColor(st.Accent, new SKColor(0x3D, 0x7E, 0xFF));
        GetThemeColors(st.KeystrokeTheme, accent, out var trayBg, out var trayBorder,
            out var capBase, out var capFace, out var modFace, out var capBorder, out var textColor, out var plusColor);

        var trayRect = new SKRect(trayLeft, trayTop, trayLeft + trayW, trayTop + trayH);
        using (var tray = new SKPaint { Color = ScaleAlpha(trayBg, alpha), IsAntialias = true })
        {
            tray.ImageFilter = SKImageFilter.CreateDropShadow(0, 4 * u, 12 * u, 12 * u,
                new SKColor(0, 0, 0, (byte)(80 * alpha)));
            canvas.DrawRoundRect(new SKRoundRect(trayRect, 14 * u), tray);
        }
        using (var border = new SKPaint { Color = ScaleAlpha(trayBorder, alpha), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1f, 1 * u) })
            canvas.DrawRoundRect(new SKRoundRect(trayRect, 14 * u), border);

        var fm = font.FontMetrics;
        float textMidOffset = -(fm.Ascent + fm.Descent) / 2f;
        var pfm = plusFont.FontMetrics;
        float plusMidOffset = -(pfm.Ascent + pfm.Descent) / 2f;

        float x = trayLeft + trayPadX;
        float capTop = trayTop + trayPadY;

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            float keyIn = (float)Math.Clamp((tMs - item.T) / 90.0, 0, 1);
            float keyAlpha = alpha * keyIn;

            if (i > 0)
            {
                if (item.JoinedToPrev)
                {
                    x += 3 * u;
                    plusFont.Color = ScaleAlpha(plusColor, keyAlpha);
                    float pw = plusFont.MeasureText("+");
                    canvas.DrawText("+", x + (plusW - pw) / 2f, capTop + capH / 2f + plusMidOffset, plusFont);
                    x += plusW + 3 * u;
                }
                else
                {
                    x += gap;
                }
            }

            float w = widths[i];
            var cap = new SKRect(x, capTop, x + w, capTop + capH);

            // Subtle 3D keycap base/ledge.
            using (var bottom = new SKPaint { Color = ScaleAlpha(capBase, keyAlpha), IsAntialias = true })
                canvas.DrawRoundRect(new SKRoundRect(new SKRect(cap.Left, cap.Top + 2.5f * u, cap.Right, cap.Bottom + 2.5f * u), 8 * u), bottom);

            // Keycap face.
            using (var face = new SKPaint { Color = ScaleAlpha(item.IsModifier ? modFace : capFace, keyAlpha), IsAntialias = true })
                canvas.DrawRoundRect(new SKRoundRect(cap, 8 * u), face);

            // Crisp top/edge highlight border.
            using (var edge = new SKPaint { Color = ScaleAlpha(capBorder, keyAlpha), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1f, 1 * u) })
                canvas.DrawRoundRect(new SKRoundRect(cap, 8 * u), edge);

            font.Color = ScaleAlpha(textColor, keyAlpha);
            if (st.KeystrokeTheme == KeystrokeTheme.Glass)
                font.ImageFilter = SKImageFilter.CreateDropShadow(0, 1 * u, 2 * u, 2 * u, new SKColor(0, 0, 0, (byte)(110 * keyAlpha)));

            float tw = font.MeasureText(item.Label);
            canvas.DrawText(item.Label, cap.MidX - tw / 2f, cap.MidY + textMidOffset, font);
            x += w;
        }
    }

    private static List<KeyCapItem> BuildItems(IReadOnlyList<KeyEvent> keys, double tMs, double windowMs, bool shortcutsOnly)
    {
        var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lastSeen = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        bool hasUpEvents = keys.Any(k => !k.Down);
        var items = new List<KeyCapItem>();
        const int maxItems = 7;

        for (int i = 0; i < keys.Count; i++)
        {
            var k = keys[i];
            if (k.T > tMs) break;
            string sym = NormalizeSymbol(k.Symbol);

            if (!k.Down)
            {
                held.Remove(sym);
                continue;
            }

            // Ignore OS auto-repeat while key is held down.
            if (held.Contains(sym)) continue;
            if (!hasUpEvents && lastSeen.TryGetValue(sym, out double prevT) && k.T - prevT < 120) continue;

            held.Add(sym);
            lastSeen[sym] = k.T;

            if (tMs - k.T > windowMs) continue;

            bool isMod = Modifiers.Contains(sym);
            bool anyOtherModHeld = held.Any(h => Modifiers.Contains(h) && !string.Equals(h, sym, StringComparison.OrdinalIgnoreCase));
            bool anyModHeld = held.Any(Modifiers.Contains);

            if (shortcutsOnly)
            {
                // In shortcuts-only mode, skip plain typing (non-modifier, non-special without modifier).
                if (!isMod && !anyModHeld && !SpecialKeys.Contains(sym)) continue;
                // Don't show lone modifier tap if we're waiting for a combo; if it's held with another key, the combo will include it.
                if (isMod && !anyOtherModHeld)
                {
                    // Only show standalone modifier if it was pressed as part of a combo or if it's the newest key and still held.
                    bool followedByCombo = false;
                    for (int j = i + 1; j < keys.Count && keys[j].T <= tMs; j++)
                    {
                        if (keys[j].Down && keys[j].T - k.T < 800 && !Modifiers.Contains(NormalizeSymbol(keys[j].Symbol)))
                        {
                            followedByCombo = true;
                            break;
                        }
                        if (!keys[j].Down && string.Equals(NormalizeSymbol(keys[j].Symbol), sym, StringComparison.OrdinalIgnoreCase))
                            break;
                    }
                    if (!followedByCombo) continue;
                }
            }

            bool joined = false;
            if (items.Count > 0 && (anyOtherModHeld || (!isMod && anyModHeld)))
            {
                var prev = items[^1];
                if (prev.IsModifier && held.Contains(prev.Label) && k.T - prev.T < 1200)
                {
                    joined = true;
                }
            }

            // If a non-modifier is pressed while modifiers are held that aren't in `items` yet (e.g. modifier was pressed before window),
            // prepend the held modifiers so the shortcut is complete!
            if (!isMod && anyModHeld && !joined)
            {
                bool firstMod = true;
                foreach (var m in held.Where(Modifiers.Contains))
                {
                    items.Add(new KeyCapItem { Label = m, T = k.T, IsModifier = true, JoinedToPrev = !firstMod });
                    firstMod = false;
                }
                joined = !firstMod;
            }

            items.Add(new KeyCapItem { Label = sym, T = k.T, IsModifier = isMod, JoinedToPrev = joined });
            while (items.Count > maxItems)
                items.RemoveAt(0);
        }

        return items;
    }

    private static void GetThemeColors(KeystrokeTheme theme, SKColor accent,
        out SKColor trayBg, out SKColor trayBorder,
        out SKColor capBase, out SKColor capFace, out SKColor modFace, out SKColor capBorder,
        out SKColor textColor, out SKColor plusColor)
    {
        switch (theme)
        {
            case KeystrokeTheme.Light:
                trayBg = new SKColor(244, 246, 250, 225);
                trayBorder = new SKColor(0, 0, 0, 28);
                capBase = new SKColor(186, 192, 204, 230);
                capFace = SKColors.White;
                modFace = new SKColor(232, 237, 245);
                capBorder = new SKColor(0, 0, 0, 30);
                textColor = new SKColor(24, 27, 34);
                plusColor = new SKColor(110, 118, 132);
                break;

            case KeystrokeTheme.Glass:
                trayBg = new SKColor(255, 255, 255, 42);
                trayBorder = new SKColor(255, 255, 255, 85);
                capBase = new SKColor(0, 0, 0, 55);
                capFace = new SKColor(255, 255, 255, 52);
                modFace = new SKColor(255, 255, 255, 82);
                capBorder = new SKColor(255, 255, 255, 95);
                textColor = SKColors.White;
                plusColor = new SKColor(255, 255, 255, 210);
                break;

            case KeystrokeTheme.Accent:
                trayBg = new SKColor(14, 16, 22, 195);
                trayBorder = accent.WithAlpha(110);
                capBase = new SKColor(0, 0, 0, 155);
                capFace = new SKColor(40, 45, 58);
                modFace = accent;
                capBorder = new SKColor(255, 255, 255, 42);
                textColor = SKColors.White;
                plusColor = accent.WithAlpha(220);
                break;

            default: // Dark
                trayBg = new SKColor(14, 16, 22, 190);
                trayBorder = new SKColor(255, 255, 255, 30);
                capBase = new SKColor(0, 0, 0, 155);
                capFace = new SKColor(40, 45, 58);
                modFace = new SKColor(60, 68, 86);
                capBorder = new SKColor(255, 255, 255, 38);
                textColor = SKColors.White;
                plusColor = new SKColor(160, 170, 188);
                break;
        }
    }

    private static SKColor ScaleAlpha(SKColor c, float alpha) =>
        c.WithAlpha((byte)Math.Clamp(c.Alpha * alpha, 0, 255));
}
