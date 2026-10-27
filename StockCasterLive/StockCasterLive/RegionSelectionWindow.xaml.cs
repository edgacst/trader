using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Runtime.InteropServices;

namespace StockCasterLive;

public partial class RegionSelectionWindow : Window
{
    private Point _startPoint;
    private NativePoint _startPhysicalPoint;
    private bool _selecting;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetPhysicalCursorPos(out NativePoint point);

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
        _startPhysicalPoint = GetPhysicalCursorPos(out NativePoint physicalPoint)
            ? physicalPoint
            : ToPhysicalPoint(PointToScreen(_startPoint));
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

        // 오버레이 원본(예: 손익 숫자 한 줄)처럼 아주 작은 영역도 지정할 수 있어야 하므로
        // 최소 크기를 넉넉하게 낮춰 둔다(예전 40px는 메인 방송 영역 기준으로는 적당했지만,
        // 작은 텍스트 한 줄을 오버레이로 자르려는 경우에는 너무 커서 선택이 조용히 취소되곤 했다).
        if (width < 8 || height < 8)
        {
            DialogResult = false;
            return;
        }

        NativePoint physicalEnd = GetPhysicalCursorPos(out NativePoint cursorPoint)
            ? cursorPoint
            : ToPhysicalPoint(PointToScreen(endPoint));
        int x = Math.Min(_startPhysicalPoint.X, physicalEnd.X);
        int y = Math.Min(_startPhysicalPoint.Y, physicalEnd.Y);
        int pixelWidth = Math.Max(2, Math.Abs(physicalEnd.X - _startPhysicalPoint.X));
        int pixelHeight = Math.Max(2, Math.Abs(physicalEnd.Y - _startPhysicalPoint.Y));

        // H.264 인코더가 안정적으로 처리하도록 가로·세로를 짝수 픽셀로 맞춘다.
        pixelWidth -= pixelWidth % 2;
        pixelHeight -= pixelHeight % 2;
        SelectedRegion = new CaptureRegion(x, y, pixelWidth, pixelHeight);
        DialogResult = true;
    }

    private static NativePoint ToPhysicalPoint(Point point) => new()
    {
        X = (int)Math.Round(point.X),
        Y = (int)Math.Round(point.Y)
    };

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
