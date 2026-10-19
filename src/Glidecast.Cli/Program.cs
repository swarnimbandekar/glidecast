using System.Diagnostics;
using Glidecast.Capture;
using Glidecast.Demo;
using Glidecast.Model;
using Glidecast.Render;

namespace Glidecast;

internal static class Program
{
    private static int Main(string[] args)
    {
        // Physical pixels everywhere, so capture size and hook coordinates agree.
        Glidecast.Native.Win32.SetProcessDpiAwarenessContext(Glidecast.Native.Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        try
        {
            string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
            return cmd switch
            {
                "record" => Record(args),
                "render" => RenderCmd(args),
                "demo" => DemoCmd(args),
                _ => Help(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            if (FFmpeg.LastError != null) Console.Error.WriteLine("ffmpeg: " + FFmpeg.LastError);
            return 1;
        }
    }

    private static int Help()
    {
        Console.WriteLine(
@"Glidecast - auto-zoom screen recorder (command line)

  glidecast-cli record [seconds] [outDir]
      Record the screen + global input. Stops after [seconds], or press ENTER.
      Produces <outDir>/source.mp4 and <outDir>/project.json.

  glidecast-cli render <projectDir> [out.mp4]
      Author an auto-zoom timeline from the recording and export a polished MP4.

  glidecast-cli demo [outDir]
      Generate a synthetic recording and render it end-to-end (self-test).

Environment:
  GLIDECAST_FFMPEG   full path to ffmpeg.exe (otherwise found on PATH).");
        return 0;
    }

    private static int Record(string[] args)
    {
        double seconds = args.Length > 1 && double.TryParse(args[1], out var s) ? s : 0;
        string dir = args.Length > 2 ? args[2] : Path.Combine(Directory.GetCurrentDirectory(), "recording_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(dir);
        string src = Path.Combine(dir, "source.mp4");

        var clock = Stopwatch.StartNew();
        using var screen = new ScreenRecorder(clock, src, 30);
        using var input = new InputHook(clock);
        Console.WriteLine($"Recording {screen.Width}x{screen.Height} -> {dir}");
        input.Start();
        screen.Start();

        if (seconds > 0)
        {
            Console.WriteLine($"Recording for {seconds:0.#}s...");
            Thread.Sleep((int)(seconds * 1000));
        }
        else
        {
            Console.WriteLine("Press ENTER to stop.");
            Console.ReadLine();
        }

        clock.Stop();
        screen.Stop();
        input.Stop();

        var p = new Project
        {
            Width = screen.Width,
            Height = screen.Height,
            Fps = screen.Fps,
            DurationMs = clock.Elapsed.TotalMilliseconds,
            SourceVideo = src,
            FrameTimes = screen.FrameTimes,
        };
        input.CopyInto(p);
        p.Save(Path.Combine(dir, "project.json"));
        Console.WriteLine($"Captured {screen.FrameCount} frames, {p.Clicks.Count} clicks, {p.Moves.Count} move samples.");
        Console.WriteLine($"Saved project: {Path.Combine(dir, "project.json")}");
        Console.WriteLine($"Render it with:  glidecast-cli render \"{dir}\"");
        return 0;
    }

    private static int RenderCmd(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("render needs a project directory."); return 1; }
        string dir = args[1];
        string projFile = File.Exists(dir) ? dir : Path.Combine(dir, "project.json");
        var p = Project.Load(projFile);
        // Resolve relative source path against the project folder.
        if (!Path.IsPathRooted(p.SourceVideo))
            p.SourceVideo = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(projFile))!, p.SourceVideo);

        string outPath = args.Length > 2 ? args[2] : Path.Combine(Path.GetDirectoryName(projFile)!, "final.mp4");
        Console.WriteLine("Rendering auto-zoom video...");
        VideoRenderer.Render(p, outPath, progress: pr =>
        {
            Console.Write($"\r  {pr * 100,5:0.0}%");
        });
        Console.WriteLine($"\nDone: {outPath}");
        return 0;
    }

    private static int DemoCmd(string[] args)
    {
        string dir = args.Length > 1 ? args[1] : Path.Combine(Directory.GetCurrentDirectory(), "demo_out");
        Console.WriteLine("Generating synthetic recording...");
        var p = DemoGenerator.Generate(dir);
        string outPath = Path.Combine(dir, "final.mp4");
        Console.WriteLine("Rendering auto-zoom video...");
        VideoRenderer.Render(p, outPath, progress: pr => Console.Write($"\r  {pr * 100,5:0.0}%"));
        Console.WriteLine($"\nDone: {outPath}");
        return 0;
    }
}
