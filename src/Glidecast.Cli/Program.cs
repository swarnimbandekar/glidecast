using System.Diagnostics;
using Glidecast.AutoZoom;
using Glidecast.Capture;
using Glidecast.Demo;
using Glidecast.Model;
using Glidecast.Plugins;
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
            var a = new Args(args.Skip(1));
            return cmd switch
            {
                "record" => Record(a),
                "render" => RenderCmd(a),
                "batch" => Batch(a),
                "annotate" => Annotate(a),
                "look" or "looks" => LookCmd(a),
                "encoders" => EncodersCmd(),
                "recover" => Recover(a),
                "demo" => DemoCmd(a),
                "bench" => Bench(a),
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

  glidecast-cli render <projectDir> [out.mp4] [options]
      Export a polished MP4 (hand-edited zooms are kept).

  glidecast-cli batch <folder> [options]
      Render every project found under <folder> (one level deep, or the folder itself).

  glidecast-cli annotate <projectDir> [--no-steps] [--no-boxes] [--no-captions]
      Add step numbers, highlight boxes and captions from the recorded clicks.

  glidecast-cli look list | save <name> <projectDir> | apply <name|file.json> <projectDir>
               | default [name|none] | delete <name>
      Saved looks (brand kits): background, frame, cursor, accent, logo, canvas.

  glidecast-cli encoders        List working GPU encoders.
  glidecast-cli recover [dir]   Rebuild takes interrupted by a crash (default: current folder).
  glidecast-cli demo [outDir]   Generate a synthetic recording and render it (self-test).

Render options:
  --aspect source|16:9|9:16|1:1|all   canvas shape; 'all' exports one file per shape
  --res 720|1080|1440|2160            short side of the output (default: source)
  --fps 24|30|60                      output frame rate (default: source)
  --quality draft|standard|high
  --encoder auto|cpu|nvenc|qsv|amf    H.264 encoder (auto picks a working GPU)
  --look <name|file.json>             apply a look before rendering (not saved)
  --preset subtle|balanced|dramatic|tutorial   re-author zooms with a preset
  --out <dir>                         (batch) write outputs here

Environment:
  GLIDECAST_FFMPEG   full path to ffmpeg.exe (otherwise found on PATH / next to the app).
  GLIDECAST_LOOKS    folder of saved looks.  GLIDECAST_HOOKS  path of hooks.json.");
        return 0;
    }

    /// <summary>Positional arguments plus --flags (with or without values).</summary>
    private sealed class Args
    {
        public readonly List<string> Pos = new();
        public readonly Dictionary<string, string?> Flags = new(StringComparer.OrdinalIgnoreCase);

        public Args(IEnumerable<string> raw)
        {
            var list = raw.ToList();
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].StartsWith("--"))
                {
                    string key = list[i][2..];
                    string? val = i + 1 < list.Count && !list[i + 1].StartsWith("--") && !key.StartsWith("no-") ? list[++i] : null;
                    Flags[key] = val;
                }
                else Pos.Add(list[i]);
            }
        }

        public string? this[int i] => i < Pos.Count ? Pos[i] : null;
        public bool Has(string f) => Flags.ContainsKey(f);
        public string? Get(string f) => Flags.TryGetValue(f, out var v) ? v : null;
    }

    private static int Record(Args a)
    {
        double seconds = a[0] is { } s0 && double.TryParse(s0, out var s) ? s : 0;
        string dir = a[1] ?? Path.Combine(Directory.GetCurrentDirectory(), "recording_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(dir);
        string src = Path.Combine(dir, "source.mp4");

        var clock = Stopwatch.StartNew();
        using var screen = new ScreenRecorder(clock, src, 30);
        using var input = new InputHook(clock);
        Console.WriteLine($"Recording {screen.Width}x{screen.Height} -> {dir}");
        input.Start();
        screen.Start();
        using var journal = new RecordingJournal(dir, screen, input, clock);

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
        LookLibrary.ApplyDefault(p);
        p.Save(Path.Combine(dir, "project.json"));
        journal.Complete();
        Console.WriteLine($"Captured {screen.FrameCount} frames, {p.Clicks.Count} clicks ({p.Clicks.Count(c => c.Element != null)} with UI element), {p.Moves.Count} move samples.");
        Console.WriteLine($"Saved project: {Path.Combine(dir, "project.json")}");
        Console.WriteLine($"Render it with:  glidecast-cli render \"{dir}\"");
        WaitFor(Hooks.Fire(Hooks.AfterRecord, Path.GetFullPath(dir)));
        return 0;
    }

    private static string ProjectFile(string dirOrFile) =>
        File.Exists(dirOrFile) ? dirOrFile : Path.Combine(dirOrFile, "project.json");

    private static int RenderCmd(Args a)
    {
        if (a[0] == null) { Console.Error.WriteLine("render needs a project directory."); return 1; }
        string projFile = ProjectFile(a[0]!);
        string outPath = a[1] ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(projFile))!, "final.mp4");
        RenderOne(projFile, outPath, a);
        return 0;
    }

    private static void RenderOne(string projFile, string outPath, Args a)
    {
        var p = Project.Load(projFile);
        if (a.Get("look") is { } lookName)
        {
            var look = LookLibrary.Find(lookName) ?? throw new ArgumentException($"No look named '{lookName}'. Try: glidecast-cli look list");
            LookLibrary.Apply(p, look);
        }
        if (a.Get("preset") is { } presetName)
        {
            if (!Enum.TryParse<ZoomPreset>(presetName, true, out var preset)) throw new ArgumentException("Unknown preset: " + presetName);
            p.Zoom = ZoomSettings.For(preset, p.Zoom);
            p.ZoomSegments = ZoomAuthor.BuildSegments(p, p.Zoom);
        }

        var opts = new ExportOptions();
        if (a.Get("fps") is { } fps) opts.Fps = double.Parse(fps, System.Globalization.CultureInfo.InvariantCulture);
        if (a.Get("quality") is { } q)
            opts.Quality = Enum.TryParse<ExportQuality>(q, true, out var eq) ? eq : throw new ArgumentException("Unknown quality: " + q);
        if (a.Get("encoder") is { } e)
            opts.Encoder = Encoders.TryParse(e, out var ve) ? ve : throw new ArgumentException("Unknown encoder: " + e);
        int shortSide = a.Get("res") is { } res ? int.Parse(res.TrimEnd('p', 'P')) : 0;

        var aspects = new List<CanvasAspect>();
        string aspectArg = a.Get("aspect") ?? "";
        if (aspectArg.Equals("all", StringComparison.OrdinalIgnoreCase))
            aspects.AddRange(new[] { CanvasAspect.Wide, CanvasAspect.Vertical, CanvasAspect.Square });
        else if (aspectArg.Length > 0)
            aspects.Add(Canvas.TryParse(aspectArg, out var ca) ? ca : throw new ArgumentException("Unknown aspect: " + aspectArg));

        var sw = Stopwatch.StartNew();
        Action<double> progress = pr => Console.Write($"\r  {pr * 100,5:0.0}%");
        string projDir = Path.GetDirectoryName(Path.GetFullPath(projFile))!;
        var outputs = new List<string>();
        if (aspects.Count > 1)
        {
            if (shortSide > 0) (opts.Width, opts.Height) = (shortSide, shortSide); // RenderAll derives each shape from the short side
            Console.WriteLine($"Rendering {aspects.Count} formats: {string.Join(", ", aspects.Select(Canvas.Label))}");
            outputs.AddRange(VideoRenderer.RenderAll(p, outPath, aspects, opts, progress));
        }
        else
        {
            var aspect = aspects.Count == 1 ? aspects[0] : p.Style?.Aspect ?? CanvasAspect.Source;
            opts.Aspect = aspect;
            if (shortSide > 0) (opts.Width, opts.Height) = Canvas.OutputSize(aspect, p.Width, p.Height, shortSide);
            Console.WriteLine($"Rendering {Canvas.Label(aspect)}...");
            var used = VideoRenderer.Render(p, outPath, style: p.Style, options: opts, progress: progress);
            Console.Write($"\r  100.0%  ({Encoders.Label(used)})");
            outputs.Add(outPath);
        }
        Console.WriteLine($"\nDone in {sw.Elapsed:m\\:ss}:");
        foreach (var o in outputs)
        {
            Console.WriteLine("  " + o);
            WaitFor(Hooks.Fire(Hooks.AfterExport, projDir, Path.GetFullPath(o)));
        }
    }

    private static int Batch(Args a)
    {
        string root = a[0] ?? Directory.GetCurrentDirectory();
        var projects = File.Exists(Path.Combine(root, "project.json"))
            ? new List<string> { Path.Combine(root, "project.json") }
            : Directory.EnumerateDirectories(root).Select(d => Path.Combine(d, "project.json")).Where(File.Exists).OrderBy(f => f).ToList();
        if (projects.Count == 0) { Console.Error.WriteLine("No projects (folders with project.json) under " + root); return 1; }
        string? outDir = a.Get("out");
        if (outDir != null) Directory.CreateDirectory(outDir);

        int ok = 0;
        for (int i = 0; i < projects.Count; i++)
        {
            string dir = Path.GetDirectoryName(projects[i])!;
            string name = Path.GetFileName(dir);
            string outPath = Path.Combine(outDir ?? dir, (outDir != null ? name : "final") + ".mp4");
            Console.WriteLine($"[{i + 1}/{projects.Count}] {name}");
            try { RenderOne(projects[i], outPath, a); ok++; }
            catch (Exception ex) { Console.Error.WriteLine($"\n  failed: {ex.Message}"); }
        }
        Console.WriteLine($"Rendered {ok} of {projects.Count} projects.");
        return ok == projects.Count ? 0 : 1;
    }

    private static int Annotate(Args a)
    {
        if (a[0] == null) { Console.Error.WriteLine("annotate needs a project directory."); return 1; }
        string projFile = ProjectFile(a[0]!);
        var p = Project.Load(projFile);
        AnnotationAuthor.Regenerate(p, new AnnotateOptions
        {
            Steps = !a.Has("no-steps"),
            Highlights = !a.Has("no-boxes"),
            Captions = !a.Has("no-captions"),
        });
        p.Save(projFile);
        var auto = p.Annotations!.Where(x => x.Auto).ToList();
        Console.WriteLine($"{auto.Count(x => x.Kind == AnnotationKind.Step)} steps, {auto.Count(x => x.Kind == AnnotationKind.Highlight)} boxes, " +
                          $"{auto.Count(x => x.Kind == AnnotationKind.Callout)} captions.");
        return 0;
    }

    private static int LookCmd(Args a)
    {
        string sub = (a[0] ?? "list").ToLowerInvariant();
        switch (sub)
        {
            case "list":
                string? def = LookLibrary.DefaultName;
                foreach (var l in LookLibrary.All())
                {
                    bool isDef = string.Equals(l.Name, def, StringComparison.OrdinalIgnoreCase);
                    Console.WriteLine($"  {(isDef ? "*" : " ")} {l.Name,-20} {(l.BuiltIn ? "built-in" : l.FilePath)}");
                }
                Console.WriteLine($"Saved looks live in {LookLibrary.Dir}  (* = default for new recordings)");
                return 0;
            case "save" when a[1] != null && a[2] != null:
            {
                var p = Project.Load(ProjectFile(a[2]!));
                string path = LookLibrary.Save(LookLibrary.FromProject(p, a[1]!, includeZoom: true));
                Console.WriteLine("Saved " + path);
                return 0;
            }
            case "apply" when a[1] != null && a[2] != null:
            {
                var look = LookLibrary.Find(a[1]!) ?? throw new ArgumentException($"No look named '{a[1]}'.");
                string projFile = ProjectFile(a[2]!);
                var p = Project.Load(projFile);
                LookLibrary.Apply(p, look);
                p.Save(projFile);
                Console.WriteLine($"Applied '{look.Name}' to {projFile}");
                return 0;
            }
            case "default":
                if (a[1] is { } n)
                {
                    if (n.Equals("none", StringComparison.OrdinalIgnoreCase)) LookLibrary.DefaultName = null;
                    else if (LookLibrary.Find(n) is { } look) LookLibrary.DefaultName = look.Name;
                    else throw new ArgumentException($"No look named '{n}'.");
                }
                Console.WriteLine("Default look: " + (LookLibrary.DefaultName ?? "(none)"));
                return 0;
            case "delete" when a[1] != null:
                LookLibrary.Delete(a[1]!);
                Console.WriteLine("Deleted " + a[1]);
                return 0;
            default:
                return Help();
        }
    }

    private static int EncodersCmd()
    {
        Console.WriteLine("Probing H.264 encoders...");
        foreach (var e in Encoders.Hardware)
            Console.WriteLine($"  {Encoders.Label(e),-18} {(Encoders.IsAvailable(e) ? "available" : "-")}");
        Console.WriteLine($"  {Encoders.Label(VideoEncoder.Cpu),-18} available");
        Console.WriteLine($"Auto uses: {Encoders.Label(Encoders.Resolve(VideoEncoder.Auto))}");
        return 0;
    }

    private static int Recover(Args a)
    {
        string root = a[0] ?? Directory.GetCurrentDirectory();
        var dirs = RecordingRecovery.IsUnfinished(root) ? new List<string> { root } : RecordingRecovery.FindUnfinished(root);
        if (dirs.Count == 0) { Console.WriteLine("No interrupted recordings found."); return 0; }
        foreach (var d in dirs)
        {
            var p = RecordingRecovery.Recover(d);
            Console.WriteLine(p == null
                ? $"  {d}: nothing usable was captured"
                : $"  {d}: recovered {TimeSpan.FromMilliseconds(p.DurationMs):m\\:ss} ({p.FrameTimes!.Count} frames, {p.Clicks.Count} clicks)");
        }
        return 0;
    }

    private static int DemoCmd(Args a)
    {
        string dir = a[0] ?? Path.Combine(Directory.GetCurrentDirectory(), "demo_out");
        Console.WriteLine("Generating synthetic recording...");
        var p = DemoGenerator.Generate(dir);
        string outPath = Path.Combine(dir, "final.mp4");
        Console.WriteLine("Rendering auto-zoom video...");
        var used = VideoRenderer.Render(p, outPath, progress: pr => Console.Write($"\r  {pr * 100,5:0.0}%"));
        Console.WriteLine($"\nDone ({Encoders.Label(used)}): {outPath}");
        return 0;
    }

    /// <summary>Compositor speed on a project (for performance work): ms per frame, one thread.</summary>
    private static int Bench(Args a)
    {
        if (a[0] == null) { Console.Error.WriteLine("bench needs a project directory."); return 1; }
        var p = Project.Load(ProjectFile(a[0]!));
        var cam = p.BuildCamera();
        var src = new byte[p.Width * p.Height * 4];
        new Random(1).NextBytes(src);
        var dst = new byte[p.Width * p.Height * 4];
        using var comp = new Compositor(p, cam, p.Style, p.Width, p.Height);
        comp.RenderInto(src, 0, dst); // warm-up
        int n = 0;
        var sw = Stopwatch.StartNew();
        for (double t = 0; t < p.DurationMs; t += 100, n++) comp.RenderInto(src, t, dst);
        Console.WriteLine($"{p.Width}x{p.Height}: {sw.Elapsed.TotalMilliseconds / n:0.0} ms/frame over {n} frames (single thread)");
        return 0;
    }

    /// <summary>Hooks run in the background; a CLI process shouldn't exit and orphan them silently.</summary>
    private static void WaitFor(List<Process> hooks)
    {
        foreach (var h in hooks)
        {
            if (!h.WaitForExit(120000)) Console.Error.WriteLine("hook still running after 2 min; leaving it.");
        }
    }
}
