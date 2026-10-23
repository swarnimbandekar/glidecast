using System.Diagnostics;
using System.Runtime.InteropServices;
using Glidecast.Model;
using Glidecast.Native;

namespace Glidecast.Capture;

/// <summary>
/// Captures global mouse + keyboard activity with timestamps, on its own thread
/// running a Win32 message pump (low-level hooks require one). It produces the
/// move / click / key tracks that the auto-zoom author later consumes.
/// </summary>
public sealed class InputHook : IDisposable
{
    private readonly Stopwatch _clock;
    private readonly List<MoveEvent> _moves = new();
    private readonly List<ClickEvent> _clicks = new();
    private readonly List<KeyEvent> _keys = new();
    private readonly List<CaretEvent> _carets = new();
    private readonly object _gate = new();
    private UiProbe? _probe;
    private double _lastCaretProbeMs = -1000;

    private Thread? _thread;
    private uint _threadId;
    private IntPtr _mouseHook, _keyHook;
    private Win32.HookProc? _mouseProc, _keyProc; // keep delegates alive
    private double _lastMoveSampleMs = -1000;

    // Throttle move sampling so the track stays small (we store a sparse
    // move path, not every raw sample). ~120 Hz cap is plenty for smooth replay.
    private const double MoveSampleIntervalMs = 8;

    public InputHook(Stopwatch clock) => _clock = clock;

    /// <summary>Read the UI element under each click and the caret while
    /// typing (UI Automation). Set before <see cref="Start"/>.</summary>
    public bool ProbeUi { get; set; } = true;

    public void Start()
    {
        if (ProbeUi)
        {
            _probe = new UiProbe();
            _probe.ClickResolved = (i, el, panel) =>
            {
                lock (_gate)
                {
                    if (i >= _clicks.Count) return;
                    var c = _clicks[i];
                    c.Element = el;
                    c.Container = panel;
                    _clicks[i] = c;
                }
            };
            _probe.CaretResolved = k => { lock (_gate) _carets.Add(k); };
        }
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "Glidecast-InputHook" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void ThreadMain()
    {
        _threadId = Win32.GetCurrentThreadId();
        _mouseProc = MouseProc;
        _keyProc = KeyProc;
        IntPtr hMod = Win32.GetModuleHandle(null);
        _mouseHook = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, _mouseProc, hMod, 0);
        _keyHook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _keyProc, hMod, 0);

        while (Win32.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessage(ref msg);
        }

        if (_mouseHook != IntPtr.Zero) Win32.UnhookWindowsHookEx(_mouseHook);
        if (_keyHook != IntPtr.Zero) Win32.UnhookWindowsHookEx(_keyHook);
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
            double t = _clock.Elapsed.TotalMilliseconds;
            int msg = (int)wParam;
            switch (msg)
            {
                case Win32.WM_MOUSEMOVE:
                    if (t - _lastMoveSampleMs >= MoveSampleIntervalMs)
                    {
                        _lastMoveSampleMs = t;
                        lock (_gate) _moves.Add(new MoveEvent { T = t, X = data.pt.x, Y = data.pt.y });
                    }
                    break;
                case Win32.WM_LBUTTONDOWN:
                    AddClick(t, data.pt.x, data.pt.y, 0);
                    break;
                case Win32.WM_RBUTTONDOWN:
                    AddClick(t, data.pt.x, data.pt.y, 1);
                    break;
                case Win32.WM_MBUTTONDOWN:
                    AddClick(t, data.pt.x, data.pt.y, 2);
                    break;
            }
        }
        return Win32.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private void AddClick(double t, int x, int y, int button)
    {
        lock (_gate)
        {
            // Collapse a quick second press at the same spot into a double-click.
            int count = 1;
            if (_clicks.Count > 0)
            {
                var prev = _clicks[^1];
                if (prev.Button == button && t - prev.T < 400 &&
                    Math.Abs(prev.X - x) < 6 && Math.Abs(prev.Y - y) < 6)
                    count = prev.Count + 1;
            }
            _clicks.Add(new ClickEvent { T = t, X = x, Y = y, Button = button, Count = count });
            if (count == 1) _probe?.Click(_clicks.Count - 1, x, y);
        }
    }

    private IntPtr KeyProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            bool down = msg == Win32.WM_KEYDOWN || msg == Win32.WM_SYSKEYDOWN;
            bool up = msg == Win32.WM_KEYUP || msg == Win32.WM_SYSKEYUP;
            if (down || up)
            {
                var data = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
                double t = _clock.Elapsed.TotalMilliseconds;
                string sym = KeyName(data.vkCode, data.scanCode);
                lock (_gate) _keys.Add(new KeyEvent { T = t, Symbol = sym, Down = down });
                // Sample the caret a little after typing (the app moves it on key-down).
                if (down && _probe != null && !IsModifier(data.vkCode) && t - _lastCaretProbeMs > 120)
                {
                    _lastCaretProbeMs = t;
                    _probe.Caret(t);
                }
            }
        }
        return Win32.CallNextHookEx(_keyHook, nCode, wParam, lParam);
    }

    // Shift, Ctrl, Alt, Win (left/right variants included).
    private static bool IsModifier(uint vk) => vk is >= 0x10 and <= 0x12 or >= 0xA0 and <= 0xA5 or 0x5B or 0x5C;

    private static string KeyName(uint vk, uint scan)
    {
        var buf = new char[64];
        // Synthesized input (remote desktop, automation, some macro keyboards) carries no scan code.
        if (scan == 0) scan = Win32.MapVirtualKey(vk, 0 /* MAPVK_VK_TO_VSC */);
        int lParam = (int)(scan << 16);
        int n = Win32.GetKeyNameTextW(lParam, buf, buf.Length);
        if (n > 0) return new string(buf, 0, n);
        return "VK" + vk;
    }

    public void Stop()
    {
        if (_threadId != 0)
            Win32.PostThreadMessage(_threadId, Win32.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(2000);
        _probe?.Dispose(); // finishes queued lookups
        _probe = null;
    }

    /// <summary>Copies the tracks into the project, shifting positions so they
    /// are relative to a recorded region whose top-left is (originX, originY).</summary>
    public void CopyInto(Project p, int originX = 0, int originY = 0)
    {
        lock (_gate)
        {
            p.Moves = _moves.Select(m => new MoveEvent { T = m.T, X = m.X - originX, Y = m.Y - originY }).ToList();
            p.Clicks = _clicks.Select(c => Shift(c, originX, originY)).ToList();
            p.Keys = new List<KeyEvent>(_keys);
            p.Carets = _carets.Select(k => new CaretEvent { T = k.T, X = k.X - originX, Y = k.Y - originY, W = k.W, H = k.H }).ToList();
        }
    }

    private static ClickEvent Shift(ClickEvent c, int dx, int dy) => new()
    {
        T = c.T, X = c.X - dx, Y = c.Y - dy, Button = c.Button, Count = c.Count,
        Element = c.Element?.Offset(dx, dy), Container = c.Container?.Offset(dx, dy),
    };

    /// <summary>Position in each track up to which events have been handed out by <see cref="TakeNew"/>.</summary>
    public sealed class Cursor { public int Moves, Clicks, Keys, Carets; }

    /// <summary>Events recorded since the last call (for the crash-recovery
    /// journal). Clicks are held back until <paramref name="settledBeforeMs"/>
    /// so their UI element lookup has finished.</summary>
    public (List<MoveEvent> moves, List<ClickEvent> clicks, List<KeyEvent> keys, List<CaretEvent> carets)
        TakeNew(Cursor at, double settledBeforeMs)
    {
        lock (_gate)
        {
            var m = _moves.GetRange(at.Moves, _moves.Count - at.Moves);
            int ce = at.Clicks;
            while (ce < _clicks.Count && _clicks[ce].T < settledBeforeMs) ce++;
            var c = _clicks.GetRange(at.Clicks, ce - at.Clicks);
            var k = _keys.GetRange(at.Keys, _keys.Count - at.Keys);
            var r = _carets.GetRange(at.Carets, _carets.Count - at.Carets);
            at.Moves = _moves.Count; at.Clicks = ce; at.Keys = _keys.Count; at.Carets = _carets.Count;
            return (m, c, k, r);
        }
    }

    public void Dispose() => Stop();
}
