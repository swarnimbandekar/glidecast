using System.Collections.Concurrent;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using Glidecast.Model;
using Glidecast.Native;

namespace Glidecast.Capture;

/// <summary>
/// Reads what the user is interacting with, off the input-hook thread (a hook
/// callback must return within milliseconds, UI Automation can take tens):
///  - for a click, the UI element under the pointer and a meaningful enclosing
///    panel, so a zoom can frame the whole button / menu / dialog;
///  - while typing, the text caret (or the focused field), so the camera can
///    follow the words as they appear.
/// Everything is best effort: an app that exposes nothing just yields no data.
/// </summary>
public sealed class UiProbe : IDisposable
{
    private abstract record Job;
    private sealed record ClickJob(int Index, int X, int Y) : Job;
    private sealed record CaretJob(double T) : Job;

    private readonly BlockingCollection<Job> _jobs = new(new ConcurrentQueue<Job>(), 256);
    private readonly Thread _thread;
    private readonly int _screenArea;

    /// <summary>Called on the probe thread with (click index, element, container).</summary>
    public Action<int, UiElement?, UiElement?>? ClickResolved { get; set; }
    /// <summary>Called on the probe thread with a caret sample in screen pixels.</summary>
    public Action<CaretEvent>? CaretResolved { get; set; }

    public UiProbe()
    {
        _screenArea = Math.Max(1, Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN) * Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN));
        _thread = new Thread(Run) { IsBackground = true, Name = "Glidecast-UiProbe" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>Queue a lookup; drops the request rather than blocking when busy.</summary>
    public void Click(int index, int x, int y) => _jobs.TryAdd(new ClickJob(index, x, y));
    public void Caret(double t) => _jobs.TryAdd(new CaretJob(t));

    private void Run()
    {
        // Warm up: the first UI Automation call loads providers and is slow.
        try { _ = AutomationElement.RootElement.Current.ProcessId; } catch { }

        foreach (var job in _jobs.GetConsumingEnumerable())
        {
            try
            {
                switch (job)
                {
                    case ClickJob c:
                        var (el, panel) = ElementsAt(c.X, c.Y);
                        ClickResolved?.Invoke(c.Index, el, panel);
                        break;
                    case CaretJob k:
                        if (CaretRect() is { } r)
                            CaretResolved?.Invoke(new CaretEvent { T = k.T, X = r.left, Y = r.top, W = r.right - r.left, H = r.bottom - r.top });
                        break;
                }
            }
            catch { /* the target app vanished or refused; skip */ }
        }
    }

    // Roles that read as "a panel" when framing several clicks together.
    private static readonly HashSet<ControlType> PanelTypes = new()
    {
        ControlType.Menu, ControlType.MenuBar, ControlType.List, ControlType.Tree, ControlType.DataGrid,
        ControlType.Table, ControlType.ToolBar, ControlType.Tab, ControlType.Group, ControlType.Window,
        ControlType.Pane, ControlType.Document, ControlType.Header, ControlType.StatusBar,
    };

    private (UiElement? element, UiElement? container) ElementsAt(int x, int y)
    {
        var ae = AutomationElement.FromPoint(new System.Windows.Point(x, y));
        if (ae == null) return (null, null);
        var leaf = ToUi(ae);
        // Containers that span most of the screen (desktop, maximized windows) frame nothing.
        if (leaf != null && leaf.W * (double)leaf.H > _screenArea * 0.6) leaf = null;

        UiElement? container = null;
        var walker = TreeWalker.ControlViewWalker;
        var cur = ae;
        double leafArea = leaf == null ? 0 : leaf.W * (double)leaf.H;
        for (int depth = 0; depth < 12 && cur != null; depth++)
        {
            cur = walker.GetParent(cur);
            if (cur == null || cur == AutomationElement.RootElement) break;
            var info = cur.Current;
            if (!PanelTypes.Contains(info.ControlType)) continue;
            var ui = ToUi(cur);
            if (ui == null) continue;
            double area = ui.W * (double)ui.H;
            if (area > _screenArea * 0.45) break;      // too big to be "the panel"
            if (area < leafArea * 2.5) continue;       // basically the control itself
            container = ui;
            break;
        }
        return (leaf, container);
    }

    private static UiElement? ToUi(AutomationElement ae)
    {
        var info = ae.Current;
        var r = info.BoundingRectangle;
        if (r.IsEmpty || double.IsInfinity(r.Width) || r.Width < 2 || r.Height < 2) return null;
        string? name = string.IsNullOrWhiteSpace(info.Name) ? null : info.Name.Trim();
        // Long names are usually document text, not a control label.
        if (name is { Length: > 80 }) name = null;
        string role = info.ControlType.ProgrammaticName.Replace("ControlType.", "").ToLowerInvariant();
        return new UiElement { X = (int)r.X, Y = (int)r.Y, W = (int)r.Width, H = (int)r.Height, Name = name, Role = role };
    }

    /// <summary>The caret in screen pixels: the system caret if the app has one,
    /// else the UI Automation selection, else the focused field if it is small.</summary>
    private Win32.RECT? CaretRect()
    {
        var fg = Win32.GetForegroundWindow();
        uint tid = Win32.GetWindowThreadProcessId(fg, out _);
        var gti = new Win32.GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.GUITHREADINFO>() };
        if (tid != 0 && Win32.GetGUIThreadInfo(tid, ref gti) && gti.hwndCaret != IntPtr.Zero &&
            gti.rcCaret.bottom > gti.rcCaret.top)
        {
            var tl = new Win32.POINT { x = gti.rcCaret.left, y = gti.rcCaret.top };
            var br = new Win32.POINT { x = gti.rcCaret.right, y = gti.rcCaret.bottom };
            Win32.ClientToScreen(gti.hwndCaret, ref tl);
            Win32.ClientToScreen(gti.hwndCaret, ref br);
            return new Win32.RECT { left = tl.x, top = tl.y, right = Math.Max(br.x, tl.x + 2), bottom = br.y };
        }

        var focused = AutomationElement.FocusedElement;
        if (focused == null) return null;
        if (focused.TryGetCurrentPattern(TextPattern.Pattern, out var pat) && pat is TextPattern text)
        {
            var sel = text.GetSelection();
            if (sel.Length > 0)
            {
                var range = sel[0].Clone();
                range.ExpandToEnclosingUnit(TextUnit.Character);
                var rects = range.GetBoundingRectangles();
                if (rects.Length > 0 && rects[^1].Height > 0)
                {
                    var r = rects[^1];
                    return new Win32.RECT { left = (int)r.Right, top = (int)r.Top, right = (int)r.Right + 2, bottom = (int)r.Bottom };
                }
            }
        }
        var b = focused.Current.BoundingRectangle;
        if (b.IsEmpty || b.Width * b.Height > _screenArea * 0.15) return null;
        return new Win32.RECT { left = (int)b.X, top = (int)b.Y, right = (int)b.Right, bottom = (int)b.Bottom };
    }

    public void Dispose()
    {
        _jobs.CompleteAdding();
        _thread.Join(1500);
    }
}
