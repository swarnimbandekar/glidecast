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
    private readonly object _gate = new();

    private Thread? _thread;
    private uint _threadId;
    private IntPtr _mouseHook, _keyHook;
    private Win32.HookProc? _mouseProc, _keyProc; // keep delegates alive
    private double _lastMoveSampleMs = -1000;

    // Throttle move sampling so the track stays small (we store a sparse
    // move path, not every raw sample). ~120 Hz cap is plenty for smooth replay.
    private const double MoveSampleIntervalMs = 8;

    public InputHook(Stopwatch clock) => _clock = clock;

    public void Start()
    {
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
            }
        }
        return Win32.CallNextHookEx(_keyHook, nCode, wParam, lParam);
    }

    private static string KeyName(uint vk, uint scan)
    {
        var buf = new char[64];
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
    }

    /// <summary>Copies the tracks into the project, shifting positions so they
    /// are relative to a recorded region whose top-left is (originX, originY).</summary>
    public void CopyInto(Project p, int originX = 0, int originY = 0)
    {
        lock (_gate)
        {
            p.Moves = _moves.Select(m => new MoveEvent { T = m.T, X = m.X - originX, Y = m.Y - originY }).ToList();
            p.Clicks = _clicks.Select(c => new ClickEvent { T = c.T, X = c.X - originX, Y = c.Y - originY, Button = c.Button, Count = c.Count }).ToList();
            p.Keys = new List<KeyEvent>(_keys);
        }
    }

    public void Dispose() => Stop();
}
