using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Shell;
using Glidecast.App.Services;
using Glidecast.App.Views;
using Glidecast.Model;

namespace Glidecast.App;

/// <summary>Shell: custom title bar, and either the home/record screen or the editor.</summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Fit the default size inside the work area (taskbar-safe at any DPI).
        var wa = SystemParameters.WorkArea;
        Width = Math.Min(Width, wa.Width - 40);
        Height = Math.Min(Height, wa.Height - 40);
        SourceInitialized += (_, _) => NativeMethods.StyleWindow(this);
        StateChanged += (_, _) => OnStateChanged();

        // Optional: Glidecast.exe <project.json | folder> opens straight into the editor.
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && ResolveProjectFile(args[1]) is { } file)
            ShowEditor(file);
        else
            ShowHome();
        Loaded += async (_, _) => await OfferUpdateAsync();
    }

    /// <summary>Shortly after launch, offers a newer release if there is one.
    /// Offline or rate-limited checks fail silently.</summary>
    private async Task OfferUpdateAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            if (await UpdateService.CheckAsync() is not { } release) return;
            if (!IsVisible || OwnedWindows.Count > 0) return; // recording, or another dialog is up
            new UpdateWindow(release) { Owner = this }.ShowDialog();
        }
        catch { /* no network, GitHub unavailable, ... */ }
    }

    public static string? ResolveProjectFile(string path)
    {
        if (File.Exists(path)) return path;
        string inDir = Path.Combine(path, "project.json");
        return File.Exists(inDir) ? inDir : null;
    }

    /// <summary>Lets the current page put its own controls in the title bar.</summary>
    public void SetTitleContent(UIElement? content)
    {
        if (content != null) WindowChrome.SetIsHitTestVisibleInChrome(content, true);
        TitleSlot.Content = content;
    }

    public void ShowHome()
    {
        CloseEditor();
        Title = "Glidecast";
        SetTitleContent(null);
        Host.Content = new HomeView(this);
    }

    public void ShowEditor(string projectFile)
    {
        CloseEditor();
        var p = Project.Load(projectFile);
        Title = "Glidecast — " + Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(projectFile)));
        Host.Content = new EditorView(this, p, projectFile);
    }

    private void CloseEditor()
    {
        if (Host.Content is EditorView ed) ed.Close();
    }

    private void OnStateChanged()
    {
        // A maximized chromeless window overhangs the screen by its resize border.
        Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
    }

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Max_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        CloseEditor();
        base.OnClosing(e);
    }
}
