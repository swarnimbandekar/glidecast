using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Glidecast.App.Services;

internal static class NativeMethods
{
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    /// <summary>Keeps a window (the recording toolbar) out of screen captures,
    /// so it never shows up in the recorded video. Windows 10 2004+.</summary>
    public static void ExcludeFromCapture(Window w)
    {
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Dark native title bar to match the app theme (Windows 10 20H1+).</summary>
    public static void UseDarkTitleBar(Window w)
    {
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        int on = 1;
        DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
    }

    /// <summary>Dark title bar plus Windows 11 rounded corners.</summary>
    public static void StyleWindow(Window w)
    {
        UseDarkTitleBar(w);
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        int round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
    }

    // ---------------------------------------------------------------- windows on the desktop

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder s, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80;

    public sealed record DesktopWindow(IntPtr Handle, string Title, System.Drawing.Rectangle Bounds);

    /// <summary>Visible top-level windows of other apps, front to back, with
    /// their visible bounds (no invisible resize borders) in screen pixels.</summary>
    public static List<DesktopWindow> TopLevelWindows()
    {
        var list = new List<DesktopWindow>();
        uint self = (uint)Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == self || (GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
            // Suspended UWP apps and windows on other virtual desktops are "cloaked".
            if (DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            var sb = new System.Text.StringBuilder(256);
            GetWindowText(h, sb, sb.Capacity);
            if (sb.Length == 0) return true;
            if (DwmGetWindowAttribute(h, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out RECT r, Marshal.SizeOf<RECT>()) != 0) return true;
            var rect = System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            if (rect.Width >= 120 && rect.Height >= 80) list.Add(new DesktopWindow(h, sb.ToString(), rect));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>Lets mouse input pass through a (layered) window to whatever is under it.</summary>
    public static void MakeClickThrough(Window w)
    {
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW);
    }

    /// <summary>Physical pixels per WPF unit on the primary display.</summary>
    public static double PixelsPerDip()
    {
        var src = Application.Current?.MainWindow is { } mw ? PresentationSource.FromVisual(mw) : null;
        return src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
    }

    /// <summary>The primary screen in physical pixels.</summary>
    public static System.Drawing.Rectangle PrimaryScreen()
    {
        double k = PixelsPerDip();
        return new System.Drawing.Rectangle(0, 0, (int)Math.Round(SystemParameters.PrimaryScreenWidth * k), (int)Math.Round(SystemParameters.PrimaryScreenHeight * k));
    }

    public const int WM_HOTKEY = 0x0312;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>A WPF element's bounds in physical screen pixels.</summary>
    public static System.Drawing.Rectangle ScreenRect(FrameworkElement e)
    {
        var a = e.PointToScreen(new Point(0, 0));
        var b = e.PointToScreen(new Point(e.ActualWidth, e.ActualHeight));
        return System.Drawing.Rectangle.FromLTRB((int)a.X, (int)a.Y, (int)Math.Ceiling(b.X), (int)Math.Ceiling(b.Y));
    }
}
