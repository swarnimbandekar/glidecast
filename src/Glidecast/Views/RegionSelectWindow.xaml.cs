using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Glidecast.App.Views;

/// <summary>Full-screen overlay on the primary display for dragging out a
/// capture rectangle. <see cref="Selection"/> is in physical screen pixels.</summary>
public partial class RegionSelectWindow : Window
{
    private Point? _anchor;
    private Rect _sel;

    public System.Drawing.Rectangle? Selection { get; private set; }

    public RegionSelectWindow()
    {
        InitializeComponent();
        var wa = SystemParameters.WorkArea;
        Left = wa.Left;
        Top = wa.Top;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        Loaded += (_, _) => { WindowState = WindowState.Maximized; UpdateDim(); Activate(); Focus(); };
        SizeChanged += (_, _) => UpdateDim();
        KeyDown += OnKey;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { DialogResult = false; }
        else if (e.Key == Key.Enter && _sel.Width >= 32 && _sel.Height >= 32) Accept();
    }

    private void Root_Down(object sender, MouseButtonEventArgs e)
    {
        _anchor = e.GetPosition(Root);
        Confirm.Visibility = Visibility.Collapsed;
        Help.Visibility = Visibility.Collapsed;
        Root.CaptureMouse();
    }

    private void Root_Move(object sender, MouseEventArgs e)
    {
        if (_anchor is not { } a || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(Root);
        _sel = new Rect(a, p);
        UpdateDim();
    }

    private void Root_Up(object sender, MouseButtonEventArgs e)
    {
        Root.ReleaseMouseCapture();
        _anchor = null;
        if (_sel.Width < 32 || _sel.Height < 32)
        {
            _sel = Rect.Empty;
            UpdateDim();
            Help.Visibility = Visibility.Visible;
            return;
        }
        // Put the confirm bar under the selection (or above if no room).
        Confirm.Visibility = Visibility.Visible;
        Confirm.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = Confirm.DesiredSize;
        double x = Math.Clamp(_sel.Right - size.Width, 8, Math.Max(8, ActualWidth - size.Width - 8));
        double y = _sel.Bottom + 10 + size.Height < ActualHeight ? _sel.Bottom + 10 : Math.Max(8, _sel.Top - size.Height - 10);
        Canvas.SetLeft(Confirm, x);
        Canvas.SetTop(Confirm, y);
    }

    private void UpdateDim()
    {
        var full = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
        if (_sel.IsEmpty || _sel.Width < 1)
        {
            Dim.Data = full;
            SelBorder.Visibility = SizeTag.Visibility = Visibility.Collapsed;
            return;
        }
        Dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(_sel));
        SelBorder.Visibility = SizeTag.Visibility = Visibility.Visible;
        Canvas.SetLeft(SelBorder, _sel.X - 1);
        Canvas.SetTop(SelBorder, _sel.Y - 1);
        SelBorder.Width = _sel.Width + 2;
        SelBorder.Height = _sel.Height + 2;
        var px = ToPixels(_sel);
        SizeText.Text = $"{px.Width & ~1} × {px.Height & ~1}";
        Canvas.SetLeft(SizeTag, _sel.X);
        Canvas.SetTop(SizeTag, Math.Max(0, _sel.Y - 28));
    }

    private System.Drawing.Rectangle ToPixels(Rect r)
    {
        var a = Root.PointToScreen(r.TopLeft);
        var b = Root.PointToScreen(r.BottomRight);
        return System.Drawing.Rectangle.FromLTRB((int)Math.Round(a.X), (int)Math.Round(a.Y), (int)Math.Round(b.X), (int)Math.Round(b.Y));
    }

    private void Accept()
    {
        Selection = ToPixels(_sel);
        DialogResult = true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Accept();
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
