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
