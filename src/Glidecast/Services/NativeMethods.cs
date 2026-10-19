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

    /// <summary>A WPF element's bounds in physical screen pixels.</summary>
    public static System.Drawing.Rectangle ScreenRect(FrameworkElement e)
    {
        var a = e.PointToScreen(new Point(0, 0));
        var b = e.PointToScreen(new Point(e.ActualWidth, e.ActualHeight));
        return System.Drawing.Rectangle.FromLTRB((int)a.X, (int)a.Y, (int)Math.Ceiling(b.X), (int)Math.Ceiling(b.Y));
    }
}
