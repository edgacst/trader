using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace StockCasterLive;

public partial class RegionSelectionWindow : Window
{
    private Point _startPoint;
    private bool _selecting;

    public CaptureRegion? SelectedRegion { get; private set; }

    public RegionSelectionWindow()
    {
        InitializeComponent();
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
    }

    private void OverlayCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _startPoint = e.GetPosition(OverlayCanvas);
        _selecting = true;
        GuidePanel.Visibility = Visibility.Collapsed;
        SelectionRectangle.Visibility = Visibility.Visible;
        SizeBadge.Visibility = Visibility.Visible;
        UpdateSelection(_startPoint);
        OverlayCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void OverlayCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_selecting)
            UpdateSelection(e.GetPosition(OverlayCanvas));
    }

    private void OverlayCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_selecting)
            return;

        _selecting = false;
        OverlayCanvas.ReleaseMouseCapture();
        Point endPoint = e.GetPosition(OverlayCanvas);
        double left = Math.Min(_startPoint.X, endPoint.X);
        double top = Math.Min(_startPoint.Y, endPoint.Y);
        double width = Math.Abs(endPoint.X - _startPoint.X);
        double height = Math.Abs(endPoint.Y - _startPoint.Y);

        if (width < 40 || height < 40)
        {
            DialogResult = false;
            return;
        }

        Point physicalTopLeft = PointToScreen(new Point(left, top));
        Point physicalBottomRight = PointToScreen(new Point(left + width, top + height));
        int x = (int)Math.Round(Math.Min(physicalTopLeft.X, physicalBottomRight.X));
        int y = (int)Math.Round(Math.Min(physicalTopLeft.Y, physicalBottomRight.Y));
        int pixelWidth = Math.Max(2, (int)Math.Round(Math.Abs(physicalBottomRight.X - physicalTopLeft.X)));
        int pixelHeight = Math.Max(2, (int)Math.Round(Math.Abs(physicalBottomRight.Y - physicalTopLeft.Y)));

        // H.264 인코더가 안정적으로 처리하도록 가로·세로를 짝수 픽셀로 맞춘다.
        pixelWidth -= pixelWidth % 2;
        pixelHeight -= pixelHeight % 2;
        SelectedRegion = new CaptureRegion(x, y, pixelWidth, pixelHeight);
        DialogResult = true;
    }

    private void UpdateSelection(Point current)
    {
        double left = Math.Min(_startPoint.X, current.X);
        double top = Math.Min(_startPoint.Y, current.Y);
        double width = Math.Abs(current.X - _startPoint.X);
        double height = Math.Abs(current.Y - _startPoint.Y);

        Canvas.SetLeft(SelectionRectangle, left);
        Canvas.SetTop(SelectionRectangle, top);
        SelectionRectangle.Width = width;
        SelectionRectangle.Height = height;

        SizeText.Text = $"{Math.Round(width):0} × {Math.Round(height):0}";
        Canvas.SetLeft(SizeBadge, Math.Min(Math.Max(8, left), Math.Max(8, ActualWidth - 120)));
        Canvas.SetTop(SizeBadge, Math.Max(8, top - 36));
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            DialogResult = false;
    }
}
