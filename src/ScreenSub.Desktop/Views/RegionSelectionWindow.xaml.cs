using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ScreenSub.Core.Models;

namespace ScreenSub.Desktop.Views;

public partial class RegionSelectionWindow : Window
{
    private Point _startPoint;
    private bool _isSelecting;

    public ScreenRegion SelectedRegion { get; private set; } = ScreenRegion.Empty;
    public bool IsCancelled { get; private set; } = true;

    public RegionSelectionWindow()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Cover all connected monitors including secondary screens with negative coordinates
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        Activate();
        Focus();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            IsCancelled = true;
            Close();
        }
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            _startPoint = e.GetPosition(SelectionCanvas);
            _isSelecting = true;

            SelectionBox.Visibility = Visibility.Visible;
            DimensionBadge.Visibility = Visibility.Visible;

            Canvas.SetLeft(SelectionBox, _startPoint.X);
            Canvas.SetTop(SelectionBox, _startPoint.Y);
            SelectionBox.Width = 0;
            SelectionBox.Height = 0;

            CaptureMouse();
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isSelecting) return;

        var currentPoint = e.GetPosition(SelectionCanvas);

        double x = Math.Min(_startPoint.X, currentPoint.X);
        double y = Math.Min(_startPoint.Y, currentPoint.Y);
        double width = Math.Abs(currentPoint.X - _startPoint.X);
        double height = Math.Abs(currentPoint.Y - _startPoint.Y);

        Canvas.SetLeft(SelectionBox, x);
        Canvas.SetTop(SelectionBox, y);
        SelectionBox.Width = width;
        SelectionBox.Height = height;

        // Position dimension badge just above the selection box or inside
        double badgeX = x;
        double badgeY = y > 35 ? y - 35 : y + height + 8;
        Canvas.SetLeft(DimensionBadge, badgeX);
        Canvas.SetTop(DimensionBadge, badgeY);

        // Convert current rectangle DIP to physical screen pixels for accurate pixel counter
        try
        {
            var p1 = PointToScreen(new Point(x, y));
            var p2 = PointToScreen(new Point(x + width, y + height));
            int physW = (int)Math.Max(0, p2.X - p1.X);
            int physH = (int)Math.Max(0, p2.Y - p1.Y);
            DimensionText.Text = $"{physW} × {physH} px";
        }
        catch
        {
            DimensionText.Text = $"{(int)width} × {(int)height} px";
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isSelecting) return;

        _isSelecting = false;
        ReleaseMouseCapture();

        var endPoint = e.GetPosition(SelectionCanvas);
        double x = Math.Min(_startPoint.X, endPoint.X);
        double y = Math.Min(_startPoint.Y, endPoint.Y);
        double width = Math.Abs(endPoint.X - _startPoint.X);
        double height = Math.Abs(endPoint.Y - _startPoint.Y);

        // Require minimum selection to avoid accidental single clicks
        if (width > 20 && height > 10)
        {
            var screenTopLeft = PointToScreen(new Point(x, y));
            var screenBottomRight = PointToScreen(new Point(x + width, y + height));

            int physX = (int)Math.Round(screenTopLeft.X);
            int physY = (int)Math.Round(screenTopLeft.Y);
            int physW = (int)Math.Round(screenBottomRight.X - screenTopLeft.X);
            int physH = (int)Math.Round(screenBottomRight.Y - screenTopLeft.Y);

            SelectedRegion = new ScreenRegion(physX, physY, physW, physH);
            IsCancelled = false;
        }
        else
        {
            IsCancelled = true;
        }

        Close();
    }
}
