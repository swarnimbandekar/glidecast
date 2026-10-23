using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Glidecast.App.Services;

namespace Glidecast.App.Views;

/// <summary>A small themed prompt for a name, with an optional checkbox.</summary>
public sealed class NameDialog : Window
{
    private readonly TextBox _box;
    private readonly CheckBox? _option;

    public string Value => _box.Text;
    public bool Option => _option?.IsChecked == true;

    public NameDialog(string title, string prompt, string initial, string? option = null)
    {
        Title = title;
        Width = 400;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (System.Windows.Media.Brush)FindResource("Panel");
        Foreground = (System.Windows.Media.Brush)FindResource("Text");
        FontFamily = (System.Windows.Media.FontFamily)FindResource("UiFont");
        FontSize = 13;
        SourceInitialized += (_, _) => NativeMethods.StyleWindow(this);

        var root = new StackPanel { Margin = new Thickness(22, 20, 22, 20) };
        root.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 10) });
        _box = new TextBox { Text = initial };
        root.Children.Add(_box);
        if (option != null)
        {
            _option = new CheckBox { Content = option, Margin = new Thickness(0, 12, 0, 0), IsChecked = true };
            root.Children.Add(_option);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = new Button { Content = "Cancel", Margin = new Thickness(0, 0, 8, 0), IsCancel = true, Style = (Style)FindResource("GhostButton") };
        var ok = new Button { Content = "Save", IsDefault = true, Padding = new Thickness(20, 7, 20, 7), Style = (Style)FindResource("PrimaryButton") };
        ok.Click += (_, _) => { DialogResult = true; };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);
        Content = root;
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); Keyboard.Focus(_box); };
    }
}
