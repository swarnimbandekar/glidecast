using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Glidecast.Render;

namespace Glidecast.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        EnsureFFmpeg();
        base.OnStartup(e);
    }

    /// <summary>Glidecast drives FFmpeg (installed separately) for encoding and
    /// decoding. If it's missing, offer to install it with winget.</summary>
    private static void EnsureFFmpeg()
    {
        if (FFmpeg.IsAvailable()) return;
        var answer = MessageBox.Show(
            "Glidecast uses FFmpeg (free, open source) to record and export video, but it isn't installed yet.\n\n" +
            "Install it now with Windows Package Manager (winget)? A console window will show the progress.",
            "Glidecast — one more thing", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (answer == MessageBoxResult.Yes)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("winget",
                    "install --id Gyan.FFmpeg.Essentials -e --accept-source-agreements --accept-package-agreements")
                { UseShellExecute = true });
                p?.WaitForExit();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Couldn't start winget: " + ex.Message, "Glidecast", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            FFmpeg.Reset();
        }
        if (!FFmpeg.IsAvailable())
            MessageBox.Show(
                "FFmpeg still wasn't found. You can install it later with:\n\n    winget install Gyan.FFmpeg.Essentials\n\n" +
                "or place ffmpeg.exe next to Glidecast.exe, or set GLIDECAST_FFMPEG to its full path.",
                "Glidecast", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "Glidecast", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
