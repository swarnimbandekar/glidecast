namespace Glidecast.Model;

public enum AnnotationKind { Step, Highlight, Spotlight, Arrow, Callout }

/// <summary>
/// An overlay pinned to the recording: drawn in source-screen space, so it
/// rides along with the camera. Lengths are source pixels; times are project ms.
/// </summary>
public sealed class Annotation
{
    public AnnotationKind Kind { get; set; }
    public double Start { get; set; }
    public double End { get; set; }

    /// <summary>Anchor: the badge center, arrow tip, callout pointer, or box top-left.</summary>
    public double X { get; set; }
    public double Y { get; set; }
    /// <summary>Box size (Highlight / Spotlight).</summary>
    public double W { get; set; }
    public double H { get; set; }
    /// <summary>Arrow tail.</summary>
    public double X2 { get; set; }
    public double Y2 { get; set; }

    public string? Text { get; set; }
    public int Number { get; set; }
    /// <summary>"#RRGGBB"; null uses the style accent.</summary>
    public string? Color { get; set; }
    /// <summary>Created by <see cref="AnnotationAuthor"/>; regenerating replaces these but keeps hand-made ones.</summary>
    public bool Auto { get; set; }

    public Annotation Clone() => (Annotation)MemberwiseClone();

    public string Label => Kind switch
    {
        AnnotationKind.Step => $"Step {Number}",
        AnnotationKind.Callout => string.IsNullOrWhiteSpace(Text) ? "Callout" : Text!,
        _ => Kind.ToString(),
    };
}

public sealed class AnnotateOptions
{
    public bool Steps { get; set; } = true;
    public bool Highlights { get; set; } = true;
    /// <summary>Captions such as Click “Save”, from the clicked element's accessible name.</summary>
    public bool Captions { get; set; } = true;
}

/// <summary>
/// Turns the click track into a step-by-step walkthrough: a numbered badge on
/// every click, a box around the clicked control, and a caption naming it —
/// all read from UI Automation at record time, so no typing is needed.
/// </summary>
public static class AnnotationAuthor
{
    public static List<Annotation> Build(Project p, AnnotateOptions? o = null)
    {
        o ??= new AnnotateOptions();
        var list = new List<Annotation>();
        double screenArea = Math.Max(1.0, p.Width * (double)p.Height);
        int step = 0;
        ClickEvent? prev = null;

        foreach (var c in p.Clicks.OrderBy(c => c.T))
        {
            // The second press of a double-click is the same step.
            if (prev is { } pc && c.Button == pc.Button && c.T - pc.T < 450 &&
                Math.Abs(c.X - pc.X) < 12 && Math.Abs(c.Y - pc.Y) < 12)
            {
                prev = c;
                continue;
            }
            prev = c;
            step++;

            var el = c.Element;
            bool boxable = el is { W: >= 8, H: >= 8 } && el.W * (double)el.H < screenArea * 0.2;

            if (o.Steps)
            {
                // Sit the badge off the top-right corner of the control, or just above-right of the click.
                double bx = boxable ? el!.X + el.W : c.X + 26;
                double by = boxable ? el!.Y : c.Y - 26;
                list.Add(new Annotation
                {
                    Kind = AnnotationKind.Step, Number = step, Auto = true,
                    Start = c.T - 120, End = c.T + 1800,
                    X = Math.Clamp(bx, 24, p.Width - 24), Y = Math.Clamp(by, 24, p.Height - 24),
                });
            }

            if (o.Highlights && boxable)
            {
                const double pad = 6;
                list.Add(new Annotation
                {
                    Kind = AnnotationKind.Highlight, Auto = true,
                    Start = c.T - 120, End = c.T + 1500,
                    X = el!.X - pad, Y = el.Y - pad, W = el.W + 2 * pad, H = el.H + 2 * pad,
                });
            }

            // Only name real controls; "Click “Text editor”" on a document says nothing.
            if (o.Captions && boxable && !string.IsNullOrWhiteSpace(el?.Name))
            {
                string verb = c.Button == 1 ? "Right-click" : c.Count >= 2 ? "Double-click" : "Click";
                string name = el!.Name!.Trim();
                if (name.Length > 40) name = name[..39] + "…";
                // Pointer at the bottom-center of the control; the bubble sits below it.
                list.Add(new Annotation
                {
                    Kind = AnnotationKind.Callout, Auto = true,
                    Start = c.T - 60, End = c.T + 2000,
                    X = boxable ? el.Cx : c.X, Y = boxable ? el.Y + el.H + 4 : c.Y + 18,
                    Text = $"{verb} “{name}”",
                });
            }
        }
        return list;
    }

    /// <summary>Replaces the auto-made annotations and keeps the hand-made ones.</summary>
    public static void Regenerate(Project p, AnnotateOptions? o = null)
    {
        var kept = (p.Annotations ?? new()).Where(a => !a.Auto);
        p.Annotations = kept.Concat(Build(p, o)).OrderBy(a => a.Start).ToList();
    }

    /// <summary>A hand-made annotation of <paramref name="kind"/> at time t, aimed at (x, y).</summary>
    public static Annotation Create(Project p, AnnotationKind kind, double t, double x, double y)
    {
        var a = new Annotation { Kind = kind, Start = t, End = Math.Min(p.DurationMs, t + 2500), X = x, Y = y };
        switch (kind)
        {
            case AnnotationKind.Step:
                a.Number = (p.Annotations?.Where(n => n.Kind == AnnotationKind.Step).Select(n => n.Number).DefaultIfEmpty(0).Max() ?? 0) + 1;
                break;
            case AnnotationKind.Highlight:
            case AnnotationKind.Spotlight:
                a.W = p.Width * 0.22; a.H = p.Height * 0.14;
                a.X = Math.Clamp(x - a.W / 2, 0, p.Width - a.W);
                a.Y = Math.Clamp(y - a.H / 2, 0, p.Height - a.H);
                break;
            case AnnotationKind.Arrow:
                // Tail up-left of the tip, flipped when that would leave the screen.
                a.X2 = x - 180 < 0 ? x + 180 : x - 180;
                a.Y2 = y - 120 < 0 ? y + 120 : y - 120;
                break;
            case AnnotationKind.Callout:
                a.Text = "Add a note";
                break;
        }
        return a;
    }
}
