using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace StockCasterLive;

public sealed class WindowInfo
{
    public nint Handle { get; init; }
    public string Title { get; init; } = string.Empty;
    public string ProcessName { get; init; } = string.Empty;
    public int Width { get; init; }
    public int Height { get; init; }
    public CaptureRegion? Region { get; init; }
    public string DisplayName => Region is not null
        ? $"사용자 지정 영역  ·  {Region.Width} × {Region.Height}"
        : string.IsNullOrWhiteSpace(ProcessName) ? Title : $"{Title}  ·  {ProcessName}";
}

public sealed record DesktopCaptureTarget(int OutputIndex, int X, int Y, int Width, int Height);

public static class WindowCaptureService
{
    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);
    private delegate bool MonitorEnumProc(nint hMonitor, nint hdcMonitor, nint monitorRect, nint data);

    private const uint Srccopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;
    private const uint PwRenderFullContent = 0x00000002;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(nint hdc, nint clipRect, MonitorEnumProc callback, nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MonitorInfoEx monitorInfo);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern nint GetWindowDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hWnd, nint hDc);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(nint hWnd, nint hDcBlt, uint flags);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint hDc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleBitmap(nint hDc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hDc, nint hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint hDc);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(nint hdcDest, int xDest, int yDest, int width, int height,
        nint hdcSource, int xSource, int ySource, uint rasterOperation);

    [DllImport("gdi32.dll")]
    private static extern bool StretchBlt(nint hdcDest, int xDest, int yDest, int destWidth, int destHeight,
        nint hdcSource, int xSource, int ySource, int sourceWidth, int sourceHeight, uint rasterOperation);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(nint hdc, int mode);

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hWnd, uint flags);

    // System.Windows.Point(이 파일에서 using System.Windows;로 이미 들어와 있음)와
    // 헷갈리지 않도록, WinAPI용 정수 좌표 구조체는 이름을 따로 둔다.
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private const uint GaRoot = 2;

    /// <summary>
    /// 데스크톱 절대 좌표의 한 점에서 "실제로 화면에 보이는(맨 위에 있는)" 최상위 창의
    /// 핸들을 돌려준다. 오버레이 원본 창을 고를 때 예전에는 "이 사각형을 포함하는 창들
    /// 중 가장 작은 것"을 골랐는데, 화면에 안 보이는(다른 창 뒤에 가려진) 배경/런처
    /// 창이 우연히 더 작은 경우 그게 잘못 뽑힐 수 있었다. OS에 "여기 뭐가 보이니"라고
    /// 직접 묻는 이 방식이 항상 정확하다. WindowFromPoint는 자식 컨트롤의 핸들을 줄 수
    /// 있으므로 GetAncestor(GA_ROOT)로 그 최상위(top-level) 창까지 올라간다.
    /// </summary>
    public static nint GetTopLevelWindowAt(int x, int y)
    {
        nint hwnd = WindowFromPoint(new NativePoint { X = x, Y = y });
        if (hwnd == 0)
            return 0;

        nint root = GetAncestor(hwnd, GaRoot);
        return root != 0 ? root : hwnd;
    }

    /// <summary>창 핸들로 제목 텍스트만 조회한다(GetCapturableWindows의 160×100 최소 크기
    /// 필터를 거치지 않은, 더 작은 창에도 쓸 수 있도록 별도로 뺐다).</summary>
    public static string GetWindowTitle(nint handle)
    {
        int length = GetWindowTextLength(handle);
        if (length <= 0)
            return string.Empty;

        var builder = new StringBuilder(length + 1);
        GetWindowText(handle, builder, builder.Capacity);
        return builder.ToString().Trim();
    }

    public static List<WindowInfo> GetCapturableWindows()
    {
        var result = new List<WindowInfo>();
        uint currentProcessId = (uint)Environment.ProcessId;

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd) || IsIconic(hWnd))
                return true;

            int titleLength = GetWindowTextLength(hWnd);
            if (titleLength <= 0)
                return true;

            GetWindowThreadProcessId(hWnd, out uint processId);
            if (processId == currentProcessId)
                return true;

            if (!GetWindowRect(hWnd, out Rect rect))
                return true;

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width < 160 || height < 100)
                return true;

            var titleBuilder = new StringBuilder(titleLength + 1);
            GetWindowText(hWnd, titleBuilder, titleBuilder.Capacity);
            string title = titleBuilder.ToString().Trim();
            if (string.IsNullOrWhiteSpace(title) || title == "Program Manager")
                return true;

            string processName = string.Empty;
            try
            {
                processName = Process.GetProcessById((int)processId).ProcessName;
            }
            catch
            {
                // 권한이 다른 프로세스는 제목만 표시한다.
            }

            result.Add(new WindowInfo
            {
                Handle = hWnd,
                Title = title,
                ProcessName = processName,
                Width = width,
                Height = height
            });
            return true;
        }, 0);

        return result
            .OrderBy(window => window.ProcessName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(window => window.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static bool IsWindowAvailable(nint handle) => IsWindow(handle) && !IsIconic(handle);

    /// <summary>
    /// 창 선택 모드(사용자 지정 영역이 아닌 "창 선택")에서 방송 캡처가 미리보기와
    /// 동일한 좌표계를 쓰도록, 창의 현재 화면상 사각형을 데스크톱 기준 영역으로 반환합니다.
    /// gdigrab의 hwnd 캡처는 PrintWindow 기반 미리보기와 렌더링 결과가 달라질 수 있어
    /// (민감정보 모자이크가 실제 송출 화면에서 어긋나는 원인), 항상 이 영역을 통해
    /// 데스크톱 좌표로 캡처하면 미리보기·모자이크·실제 송출이 같은 픽셀을 기준으로 맞습니다.
    /// </summary>
    public static CaptureRegion? GetWindowRegion(nint handle)
    {
        if (!IsWindowAvailable(handle) || !GetWindowRect(handle, out Rect rect))
            return null;

        // GetWindowRect can include invisible resize borders that sit just outside the
        // virtual desktop (for example, a maximized window on a monitor to the left).
        // gdigrab rejects even a one-pixel overflow, so capture only the visible portion.
        int desktopLeft = GetSystemMetrics(SmXVirtualScreen);
        int desktopTop = GetSystemMetrics(SmYVirtualScreen);
        long desktopRight = (long)desktopLeft + GetSystemMetrics(SmCxVirtualScreen);
        long desktopBottom = (long)desktopTop + GetSystemMetrics(SmCyVirtualScreen);

        long left = Math.Max((long)rect.Left, desktopLeft);
        long top = Math.Max((long)rect.Top, desktopTop);
        long right = Math.Min((long)rect.Right, desktopRight);
        long bottom = Math.Min((long)rect.Bottom, desktopBottom);
        if (right <= left || bottom <= top)
            return null;

        return new CaptureRegion(
            (int)left,
            (int)top,
            (int)(right - left),
            (int)(bottom - top));
    }

    public static DesktopCaptureTarget? ResolveDesktopCapture(CaptureRegion region)
    {
        DesktopCaptureTarget? target = null;
        EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
        {
            var info = new MonitorInfoEx
            {
                Size = Marshal.SizeOf<MonitorInfoEx>(),
                DeviceName = string.Empty
            };
            if (!GetMonitorInfo(monitor, ref info))
                return true;

            Rect bounds = info.Monitor;
            bool containsRegion = region.X >= bounds.Left && region.Y >= bounds.Top &&
                                  region.X + region.Width <= bounds.Right &&
                                  region.Y + region.Height <= bounds.Bottom;
            const string displayPrefix = @"\\.\DISPLAY";
            if (!containsRegion || !info.DeviceName.StartsWith(displayPrefix, StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(info.DeviceName[displayPrefix.Length..], out int displayNumber))
                return true;

            target = new DesktopCaptureTarget(
                Math.Max(0, displayNumber - 1),
                region.X - bounds.Left,
                region.Y - bounds.Top,
                region.Width,
                region.Height);
            return false;
        }, 0);
        return target;
    }

    public static bool IsRegionAvailable(CaptureRegion region)
    {
        const int smXVirtualScreen = 76;
        const int smYVirtualScreen = 77;
        const int smCxVirtualScreen = 78;
        const int smCyVirtualScreen = 79;

        if (region.Width < 40 || region.Height < 40)
            return false;

        int left = GetSystemMetrics(smXVirtualScreen);
        int top = GetSystemMetrics(smYVirtualScreen);
        int right = left + GetSystemMetrics(smCxVirtualScreen);
        int bottom = top + GetSystemMetrics(smCyVirtualScreen);
        return region.X >= left && region.Y >= top &&
               region.X + region.Width <= right && region.Y + region.Height <= bottom;
    }

    public static BitmapSource? CaptureWindow(nint handle)
    {
        if (!IsWindowAvailable(handle) || !GetWindowRect(handle, out Rect rect))
            return null;

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
            return null;

        nint windowDc = GetWindowDC(handle);
        if (windowDc == 0)
            return null;

        nint memoryDc = CreateCompatibleDC(windowDc);
        nint bitmap = CreateCompatibleBitmap(windowDc, width, height);
        nint oldObject = 0;

        try
        {
            if (memoryDc == 0 || bitmap == 0)
                return null;

            oldObject = SelectObject(memoryDc, bitmap);
            bool captured = PrintWindow(handle, memoryDc, PwRenderFullContent);

            if (!captured)
            {
                nint desktopDc = GetDC(0);
                try
                {
                    if (desktopDc == 0 || !BitBlt(memoryDc, 0, 0, width, height,
                            desktopDc, rect.Left, rect.Top, Srccopy | CaptureBlt))
                        return null;
                }
                finally
                {
                    if (desktopDc != 0)
                        ReleaseDC(0, desktopDc);
                }
            }

            return CreateOpaqueBitmapSource(bitmap);
        }
        finally
        {
            if (oldObject != 0)
                SelectObject(memoryDc, oldObject);
            if (bitmap != 0)
                DeleteObject(bitmap);
            if (memoryDc != 0)
                DeleteDC(memoryDc);
            ReleaseDC(handle, windowDc);
        }
    }

    public static BitmapSource? CaptureScreenRegion(CaptureRegion region, int maxWidth = 0, int maxHeight = 0)
    {
        if (region.Width <= 0 || region.Height <= 0)
            return null;

        int targetWidth = region.Width;
        int targetHeight = region.Height;
        if (maxWidth > 0 && maxHeight > 0 && (targetWidth > maxWidth || targetHeight > maxHeight))
        {
            double scale = Math.Min(maxWidth / (double)targetWidth, maxHeight / (double)targetHeight);
            targetWidth = Math.Max(1, (int)Math.Round(targetWidth * scale));
            targetHeight = Math.Max(1, (int)Math.Round(targetHeight * scale));
        }

        nint desktopDc = GetDC(0);
        if (desktopDc == 0)
            return null;

        nint memoryDc = CreateCompatibleDC(desktopDc);
        nint bitmap = CreateCompatibleBitmap(desktopDc, targetWidth, targetHeight);
        nint oldObject = 0;

        try
        {
            if (memoryDc == 0 || bitmap == 0)
                return null;

            oldObject = SelectObject(memoryDc, bitmap);
            SetStretchBltMode(memoryDc, 4); // HALFTONE
            if (!StretchBlt(memoryDc, 0, 0, targetWidth, targetHeight,
                    desktopDc, region.X, region.Y, region.Width, region.Height, Srccopy | CaptureBlt))
                return null;

            return CreateOpaqueBitmapSource(bitmap);
        }
        finally
        {
            if (oldObject != 0)
                SelectObject(memoryDc, oldObject);
            if (bitmap != 0)
                DeleteObject(bitmap);
            if (memoryDc != 0)
                DeleteDC(memoryDc);
            ReleaseDC(0, desktopDc);
        }
    }

    private static BitmapSource CreateOpaqueBitmapSource(nint bitmap)
    {
        BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(
            bitmap, nint.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        int stride = source.PixelWidth * 4;
        byte[] pixels = GC.AllocateUninitializedArray<byte>(stride * source.PixelHeight);
        source.CopyPixels(pixels, stride, 0);

        BitmapSource opaque = BitmapSource.Create(
            source.PixelWidth,
            source.PixelHeight,
            source.DpiX,
            source.DpiY,
            PixelFormats.Bgr32,
            null,
            pixels,
            stride);
        opaque.Freeze();
        return opaque;
    }
}
