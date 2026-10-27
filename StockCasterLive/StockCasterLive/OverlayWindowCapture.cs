using System.Runtime.InteropServices;
using WinRT;
using Windows.Storage.Streams;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace StockCasterLive;

/// <summary>
/// Captures a single HWND with Windows.Graphics.Capture. Unlike screen-pixel copying,
/// this keeps updating while another window covers the source window.
/// </summary>
internal sealed class OverlayWindowCapture : IDisposable
{
    private const int MinimumCaptureBuild = 18362;
    private const int D3dDriverTypeHardware = 1;
    private const int D3dDriverTypeWarp = 5;
    private const uint D3d11CreateDeviceBgraSupport = 0x20;
    private const uint D3d11SdkVersion = 7;

    private static readonly Lazy<IDirect3DDevice> SharedDevice = new(CreateDirect3DDevice);
    private static readonly Guid GraphicsCaptureItemId = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid DxgiDeviceId = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");

    private readonly object _frameLock = new();
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _framePool;
    private readonly GraphicsCaptureSession _session;
    private PixelFrame? _latestFrame;
    private int _frameProcessing;
    private int _disposed;
    private int _reportedFrameError;
    private int _poolWidth;
    private int _poolHeight;

    public event Action<string>? Warning;

    public sealed record PixelFrame(int Width, int Height, byte[] Bgra);

    public static bool IsSupported
    {
        get
        {
            try
            {
                return OperatingSystem.IsWindowsVersionAtLeast(10, 0, MinimumCaptureBuild) &&
                       GraphicsCaptureSession.IsSupported();
            }
            catch
            {
                return false;
            }
        }
    }

    public OverlayWindowCapture(nint windowHandle)
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException("이 PC에서는 Windows 창 캡처 기능을 사용할 수 없습니다.");

        _item = CreateCaptureItem(windowHandle);
        _poolWidth = Math.Max(1, _item.Size.Width);
        _poolHeight = Math.Max(1, _item.Size.Height);
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            SharedDevice.Value,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            new SizeInt32 { Width = _poolWidth, Height = _poolHeight });
        _session = _framePool.CreateCaptureSession(_item);
        _framePool.FrameArrived += OnFrameArrived;
        _session.StartCapture();
    }

    public PixelFrame? GetLatestFrame()
    {
        lock (_frameLock)
            return _latestFrame;
    }

    private async void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        if (Interlocked.Exchange(ref _frameProcessing, 1) != 0)
        {
            // Keep the small frame pool drained while a previous GPU-to-CPU copy finishes.
            try { using var skipped = sender.TryGetNextFrame(); } catch { }
            return;
        }

        Direct3D11CaptureFrame? frame = null;
        try
        {
            frame = sender.TryGetNextFrame();
            if (frame is null)
                return;

            SizeInt32 contentSize = frame.ContentSize;
            if (contentSize.Width <= 0 || contentSize.Height <= 0)
                return;

            if (contentSize.Width != _poolWidth || contentSize.Height != _poolHeight)
            {
                frame.Dispose();
                frame = null;
                sender.Recreate(
                    SharedDevice.Value,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    2,
                    contentSize);
                _poolWidth = contentSize.Width;
                _poolHeight = contentSize.Height;
                return;
            }

            using SoftwareBitmap source = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface);
            SoftwareBitmap? converted = null;
            try
            {
                SoftwareBitmap bitmap = source;
                if (source.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
                {
                    converted = SoftwareBitmap.Convert(
                        source,
                        BitmapPixelFormat.Bgra8,
                        BitmapAlphaMode.Ignore);
                    bitmap = converted;
                }

                PixelFrame? copiedFrame = CopyPixels(bitmap);
                if (copiedFrame is not null && Volatile.Read(ref _disposed) == 0)
                {
                    lock (_frameLock)
                        _latestFrame = copiedFrame;
                }
            }
            finally
            {
                converted?.Dispose();
            }
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _reportedFrameError, 1) == 0)
                Warning?.Invoke($"가려진 창 캡처 프레임을 읽지 못했습니다: {ex.Message}");
        }
        finally
        {
            frame?.Dispose();
            Interlocked.Exchange(ref _frameProcessing, 0);
        }
    }

    private static PixelFrame? CopyPixels(SoftwareBitmap bitmap)
    {
        int width = bitmap.PixelWidth;
        int height = bitmap.PixelHeight;
        if (width <= 0 || height <= 0 || (long)width * height > 32_000_000)
            return null;

        int byteCount = checked(width * height * 4);
        var pixelBuffer = new Windows.Storage.Streams.Buffer((uint)byteCount);
        bitmap.CopyToBuffer(pixelBuffer);
        using var reader = DataReader.FromBuffer(pixelBuffer);
        var pixels = GC.AllocateUninitializedArray<byte>(byteCount);
        reader.ReadBytes(pixels);
        return new PixelFrame(width, height, pixels);
    }

    private static GraphicsCaptureItem CreateCaptureItem(nint windowHandle)
    {
        using var activationFactory = ActivationFactory.Get(
            "Windows.Graphics.Capture.GraphicsCaptureItem",
            typeof(IGraphicsCaptureItemInterop).GUID);
        var interop = activationFactory.AsInterface<IGraphicsCaptureItemInterop>();
        nint itemPointer = interop.CreateForWindow(windowHandle, GraphicsCaptureItemId);
        try
        {
            return MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPointer)
                ?? throw new InvalidOperationException("Windows 창 캡처 대상을 만들지 못했습니다.");
        }
        finally
        {
            Marshal.Release(itemPointer);
        }
    }

    private static IDirect3DDevice CreateDirect3DDevice()
    {
        int result = CreateD3dDevice(D3dDriverTypeHardware, out nint d3dDevice, out nint immediateContext);
        if (result < 0)
            result = CreateD3dDevice(D3dDriverTypeWarp, out d3dDevice, out immediateContext);
        if (result < 0)
            Marshal.ThrowExceptionForHR(result);

        try
        {
            if (immediateContext != 0)
                Marshal.Release(immediateContext);

            Guid dxgiDeviceId = DxgiDeviceId;
            Marshal.QueryInterface(d3dDevice, ref dxgiDeviceId, out nint dxgiDevice);
            try
            {
                int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out nint graphicsDevice);
                Marshal.ThrowExceptionForHR(hr);
                try
                {
                    return MarshalInterface<IDirect3DDevice>.FromAbi(graphicsDevice) as IDirect3DDevice
                        ?? throw new InvalidOperationException("Direct3D 캡처 장치를 연결하지 못했습니다.");
                }
                finally
                {
                    Marshal.Release(graphicsDevice);
                }
            }
            finally
            {
                Marshal.Release(dxgiDevice);
            }
        }
        finally
        {
            Marshal.Release(d3dDevice);
        }
    }

    private static int CreateD3dDevice(int driverType, out nint device, out nint immediateContext)
    {
        return D3D11CreateDevice(
            0,
            driverType,
            0,
            D3d11CreateDeviceBgraSupport,
            0,
            0,
            D3d11SdkVersion,
            out device,
            out _,
            out immediateContext);
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        nint adapter,
        int driverType,
        nint software,
        uint flags,
        nint featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out nint device,
        out uint featureLevel,
        out nint immediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow(nint window, [In] Guid iid);
        nint CreateForMonitor(nint monitor, [In] Guid iid);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _framePool.FrameArrived -= OnFrameArrived;
        try { _session.Dispose(); } catch { }
        try { _framePool.Dispose(); } catch { }
        lock (_frameLock)
            _latestFrame = null;
    }
}
