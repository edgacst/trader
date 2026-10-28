using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace StockCasterLive;

/// <summary>
/// 메인 캡처 영역 위에 오버레이 영역들(OverlayRegion)을 겹쳐서 매 프레임 하나의
/// 화면으로 합친 뒤, ffmpeg가 읽을 수 있는 named pipe로 raw bgra 프레임을 흘려보낸다.
///
/// 왜 ffmpeg 필터그래프(overlay/xstack)로 합치지 않고 여기서 미리 합치는가:
/// 이 프로젝트는 예전에 "마이크+PC 소리, 실시간 입력 2개를 영상 캡처/인코딩과 같은
/// 필터그래프 안에서 섞으려" 했다가 전체 방송이 1~2fps로 멈춰버리는 현상을 실제로
/// 겪었고(AudioMixer.cs 상단 주석 참고), 그래서 오디오 믹싱을 ffmpeg 밖으로 빼서
/// 이 앱 프로세스 안에서 먼저 섞은 뒤 ffmpeg에는 이미 합쳐진 입력 '하나'만 주는
/// 구조로 바꿨다. 화면도 같은 이유로, ffmpeg에 gdigrab 입력을 여러 개(메인+오버레이
/// 개수만큼) 동시에 열어 필터그래프 안에서 합치게 하지 않고, 이 앱이 GDI로 각 영역을
/// 메인 화면은 GDI, 오버레이 창은 Windows Graphics Capture로 받아 합성한 뒤 ffmpeg에는 완성된 화면 '하나'만 raw 프레임으로 준다.
/// ffmpeg 입장에서는 지금의 단일 gdigrab 입력과 완전히 동일하게 보이므로, 뒤쪽의
/// 민감정보 모자이크 필터/인코더 설정은 전혀 바뀌지 않는다.
/// </summary>
public sealed class VideoCompositor : IDisposable
{
    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hWnd, nint hDc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint hDc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(nint hdc, ref BitmapInfoHeader bmi, uint usage,
        out nint bits, nint hSection, uint offset);

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
    private static extern int StretchDIBits(nint hdcDest, int xDest, int yDest, int destWidth, int destHeight,
        int xSource, int ySource, int sourceWidth, int sourceHeight, nint bits,
        ref BitmapInfoHeader bitmapInfo, uint usage, uint rasterOperation);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(nint hdc, int mode);

    // 오버레이 원본 창을 화면(screenDc)이 아니라 창 자신에게서 직접 그려 받아오는 데
    // 쓴다. 화면을 그대로 복사하는 BitBlt/StretchBlt 방식은 그 창이 다른 창에 가려져
    // 있으면 가린 창의 내용을 대신 캡쳐해버리는데, PrintWindow는 창이 화면에서 실제로
    // 보이는지와 무관하게 그 창 자신의 내용을 가져온다 — OBS의 "창 캡쳐"가 쓰는 것과
    // 같은 계열의 방식이다. PW_RENDERFULLCONTENT(2)는 하드웨어 가속(DirectX 등)으로
    // 그려지는 최신 앱에서도 검은 화면 대신 실제 내용이 나오게 하는 플래그다(Windows
    // 8.1 이상). 단, 완전히 최소화된 창은 이 방식으로도 가져올 수 없다 — Windows가
    // 최소화된 창은 아예 그리지 않기 때문으로, OBS를 포함한 어떤 캡쳐 방식도 마찬가지다.
    [DllImport("user32.dll")]
    private static extern bool PrintWindow(nint hWnd, nint hdcBlt, uint flags);

    private const uint PwRenderFullContent = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    private const uint Srccopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;
    private const int HalftoneStretchMode = 4;

    private readonly CaptureRegion _mainRegion;
    // readonly가 아니다 — 방송 중에 오버레이를 추가/삭제/드래그로 옮기거나 크기를
    // 바꿔도 그게 실제 송출 화면에 반영되게 하려면, 이미 시작된 합성기가 들고 있는
    // 목록 자체를 바꿀 수 있어야 한다. UpdateOverlays()로 참조를 통째로 교체하는
    // 방식이라(필드에 새 리스트를 대입) UI 스레드가 교체하는 동안 타이머 스레드가
    // foreach로 읽고 있어도 안전하다 — 한쪽은 옛 리스트를, 다른 쪽은 새 리스트를
    // 보게 될 뿐 깨진 상태를 보는 일은 없다.
    private volatile IReadOnlyList<OverlayRegion> _overlays;
    private readonly object _overlayCaptureSync = new();
    private readonly Dictionary<nint, OverlayWindowCapture> _overlayCaptures = new();
    private readonly Dictionary<OverlayCropKey, OverlayWindowCapture.PixelFrame> _lastGoodOverlayFrames = new();
    private readonly HashSet<OverlayCropKey> _reportedOccludedOverlays = new();
    private readonly HashSet<OverlayCropKey> _reportedPrintWindowOverlays = new();
    private readonly HashSet<OverlayCropKey> _reportedFrozenOverlays = new();
    private readonly int _frameRate;
    private readonly string _pipeName = $"stockcaster_videocomposite_{Guid.NewGuid():N}";

    private nint _memoryDc;
    private nint _dibSection;
    private nint _dibBits;
    private Timer? _pumpTimer;
    private NamedPipeServerStream? _pipeServer;
    private byte[]? _frameBuffer;
    private int _pumping; // 0/1 — 이전 틱이 아직 처리 중이면 이번 틱은 건너뛴다(밀림 누적 방지).
    private bool _disposed;
    private long _frameCount;
    private long _captureFailureCount;

    // 오버레이 원본 창마다 PrintWindow로 그려 받을 전용 메모리 DC/DIB를 창 핸들별로
    // 캐싱해 둔다 — 매 프레임 새로 만들면(초당 수십 번 GDI 객체 생성/삭제) 느려지고
    // 깜빡일 수 있어서, 창 크기가 그대로면 같은 버퍼를 계속 재사용하고, 창 크기가
    // 바뀌었을 때만 다시 만든다.
    private readonly Dictionary<nint, OverlayScratchBuffer> _overlayScratchBuffers = new();

    private sealed class OverlayScratchBuffer
    {
        public nint Dc;
        public nint DibSection;
        public nint Bits;
        public int Width;
        public int Height;
    }

    private readonly record struct OverlayCropKey(
        nint WindowHandle,
        double X,
        double Y,
        double Width,
        double Height);

    public event Action<string>? CompositorWarning;

    public VideoCompositor(CaptureRegion mainRegion, IReadOnlyList<OverlayRegion> overlays, int frameRate)
    {
        _mainRegion = mainRegion;
        _overlays = overlays;
        _frameRate = frameRate;
    }

    public int Width => _mainRegion.Width;
    public int Height => _mainRegion.Height;
    public string FfmpegPixelFormat => "bgra";
    public string PipePath => $@"\\.\pipe\{_pipeName}";

    /// <summary>
    /// 방송이 이미 시작된 뒤에 오버레이 목록이 바뀌었을 때(추가/삭제/드래그 이동/
    /// 크기 조절) 호출한다. 다음 프레임부터 바로 반영된다.
    /// </summary>
    public void UpdateOverlays(IReadOnlyList<OverlayRegion> overlays)
    {
        SyncOverlayCaptureSessions(overlays);
    }

    private void SyncOverlayCaptureSessions(IReadOnlyList<OverlayRegion> overlays)
    {
        OverlayRegion[] snapshot = overlays.ToArray();
        _overlays = snapshot;
        if (_disposed)
            return;

        lock (_overlayCaptureSync)
        {
            var activeHandles = snapshot
                .Select(overlay => overlay.SourceWindowHandle)
                .Where(handle => handle != 0)
                .ToHashSet();
            var activeCrops = snapshot
                .Select(overlay => new OverlayCropKey(
                    overlay.SourceWindowHandle,
                    overlay.SourceCropX,
                    overlay.SourceCropY,
                    overlay.SourceCropWidth,
                    overlay.SourceCropHeight))
                .ToHashSet();

            foreach (OverlayCropKey staleKey in _lastGoodOverlayFrames.Keys
                         .Where(key => !activeCrops.Contains(key)).ToArray())
            {
                _lastGoodOverlayFrames.Remove(staleKey);
                _reportedOccludedOverlays.Remove(staleKey);
                _reportedPrintWindowOverlays.Remove(staleKey);
                _reportedFrozenOverlays.Remove(staleKey);
            }

            foreach (nint staleHandle in _overlayCaptures.Keys.Where(handle => !activeHandles.Contains(handle)).ToArray())
            {
                _overlayCaptures[staleHandle].Dispose();
                _overlayCaptures.Remove(staleHandle);
            }

            if (!OverlayWindowCapture.IsSupported)
                return;

            foreach (nint handle in activeHandles)
            {
                if (_overlayCaptures.ContainsKey(handle))
                    continue;

                try
                {
                    var capture = new OverlayWindowCapture(handle);
                    capture.Warning += warning => CompositorWarning?.Invoke(warning);
                    _overlayCaptures.Add(handle, capture);
                    string title = WindowCaptureService.GetWindowTitle(handle);
                    CompositorWarning?.Invoke(
                        $"오버레이 원본 창 캡처 시작: '{title}' HWND=0x{handle.ToInt64():X}");
                }
                catch (Exception ex)
                {
                    CompositorWarning?.Invoke($"창 캡처를 시작하지 못해 PrintWindow 방식으로 전환합니다: {ex.Message}");
                }
            }
        }
    }

    public void Start()
    {
        nint screenDc = GetDC(0);
        if (screenDc == 0)
            throw new InvalidOperationException("화면 장치 컨텍스트를 가져오지 못했습니다.");

        try
        {
            _memoryDc = CreateCompatibleDC(screenDc);
            if (_memoryDc == 0)
                throw new InvalidOperationException("합성용 메모리 DC를 만들지 못했습니다.");

            // 높이를 음수로 주면 top-down(위에서 아래 순서) DIB가 되어, ffmpeg의 rawvideo
            // 입력이 기대하는 행 순서와 그대로 맞는다 — 별도로 뒤집을 필요가 없다.
            var header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = Width,
                Height = -Height,
                Planes = 1,
                BitCount = 32,
                Compression = 0 // BI_RGB
            };

            _dibSection = CreateDIBSection(_memoryDc, ref header, 0, out _dibBits, 0, 0);
            if (_dibSection == 0 || _dibBits == 0)
                throw new InvalidOperationException("합성용 화면 버퍼를 만들지 못했습니다.");

            SelectObject(_memoryDc, _dibSection);
        }
        finally
        {
            ReleaseDC(0, screenDc);
        }

        SyncOverlayCaptureSessions(_overlays);

        int frameBytes = Width * Height * 4;
        _frameBuffer = GC.AllocateUninitializedArray<byte>(frameBytes);

        const int pipeBufferBytes = 1024 * 1024;
        _pipeServer = new NamedPipeServerStream(
            _pipeName, PipeDirection.Out, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            inBufferSize: 0, outBufferSize: pipeBufferBytes);
        _ = _pipeServer.WaitForConnectionAsync();

        int intervalMs = Math.Max(1, 1000 / _frameRate);
        _pumpTimer = new Timer(PumpFrame, null, 0, intervalMs);
    }

    private void PumpFrame(object? state)
    {
        if (_disposed)
            return;

        if (Interlocked.Exchange(ref _pumping, 1) == 1)
            return; // 이전 프레임 합성이 아직 안 끝났으면 이번 틱은 건너뛴다.

        try
        {
            NamedPipeServerStream? pipe = _pipeServer;
            byte[]? buffer = _frameBuffer;
            if (pipe is null || buffer is null || !pipe.IsConnected)
                return;

            if (!ComposeFrame())
            {
                Interlocked.Increment(ref _captureFailureCount);
                return;
            }

            Marshal.Copy(_dibBits, buffer, 0, buffer.Length);

            try
            {
                pipe.Write(buffer, 0, buffer.Length);
                Interlocked.Increment(ref _frameCount);
            }
            catch (IOException)
            {
                // 상대쪽(ffmpeg)이 아직 파이프를 다 안 읽었거나 연결이 끊긴 경우 —
                // 다음 틱에서 다시 시도한다. 오디오 쪽(AudioMixer)과 달리 비디오는
                // 프레임 하나를 통째로 건너뛰어도 체감 차이가 거의 없다.
            }
        }
        catch (Exception ex)
        {
            CompositorWarning?.Invoke($"화면 합성 중 오류: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _pumping, 0);
        }
    }

    // overlay.SourceWindowHandle 창 전체를 PrintWindow로 그려 받을 메모리 DC를 준다.
    // 이미 그 창 핸들용으로 만들어 둔 버퍼가 있고 크기도 그대로면 재사용하고, 없거나
    // 창 크기가 바뀌었으면(또는 바뀌어서 기존 버퍼가 너무 작으면) 새로 만든다.
    private nint GetOrCreateOverlayScratch(nint windowHandle, int width, int height, nint referenceDc)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        if (_overlayScratchBuffers.TryGetValue(windowHandle, out OverlayScratchBuffer? existing))
        {
            if (existing.Width == width && existing.Height == height)
                return existing.Dc;

            // 창 크기가 바뀌었다 — 기존 버퍼를 지우고 새로 만든다.
            SelectObject(existing.Dc, 0);
            DeleteObject(existing.DibSection);
            DeleteDC(existing.Dc);
            _overlayScratchBuffers.Remove(windowHandle);
        }

        nint dc = CreateCompatibleDC(referenceDc);
        if (dc == 0)
            return 0;

        var header = new BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            Width = width,
            Height = -height,
            Planes = 1,
            BitCount = 32,
            Compression = 0
        };

        nint dib = CreateDIBSection(dc, ref header, 0, out nint bits, 0, 0);
        if (dib == 0 || bits == 0)
        {
            DeleteDC(dc);
            return 0;
        }

        SelectObject(dc, dib);
        _overlayScratchBuffers[windowHandle] = new OverlayScratchBuffer
        {
            Dc = dc,
            DibSection = dib,
            Bits = bits,
            Width = width,
            Height = height
        };
        return dc;
    }

    private static double GetCropDifferenceRatio(
        byte[] candidate,
        OverlayWindowCapture.PixelFrame reference,
        int cropX,
        int cropY,
        int cropWidth,
        int cropHeight)
    {
        if (candidate.Length != reference.Bgra.Length)
            return 1d;

        int sampleColumns = Math.Min(24, cropWidth);
        int sampleRows = Math.Min(12, cropHeight);
        int changed = 0;
        int total = sampleColumns * sampleRows;
        for (int row = 0; row < sampleRows; row++)
        {
            int y = cropY + Math.Min(cropHeight - 1, (row * 2 + 1) * cropHeight / (sampleRows * 2));
            for (int column = 0; column < sampleColumns; column++)
            {
                int x = cropX + Math.Min(cropWidth - 1, (column * 2 + 1) * cropWidth / (sampleColumns * 2));
                int offset = (y * reference.Width + x) * 4;
                if (PixelsDiffer(candidate, offset, reference.Bgra, offset))
                    changed++;
            }
        }

        return total == 0 ? 1d : changed / (double)total;
    }

    private static double GetNativeCropDifferenceRatio(
        nint candidateBits,
        int candidateWidth,
        OverlayWindowCapture.PixelFrame reference,
        int cropX,
        int cropY,
        int cropWidth,
        int cropHeight)
    {
        if (candidateBits == 0 || candidateWidth != reference.Width)
            return 1d;

        int sampleColumns = Math.Min(24, cropWidth);
        int sampleRows = Math.Min(12, cropHeight);
        int changed = 0;
        int total = sampleColumns * sampleRows;
        for (int row = 0; row < sampleRows; row++)
        {
            int y = cropY + Math.Min(cropHeight - 1, (row * 2 + 1) * cropHeight / (sampleRows * 2));
            for (int column = 0; column < sampleColumns; column++)
            {
                int x = cropX + Math.Min(cropWidth - 1, (column * 2 + 1) * cropWidth / (sampleColumns * 2));
                int offset = (y * candidateWidth + x) * 4;
                if (Math.Abs(Marshal.ReadByte(candidateBits, offset) - reference.Bgra[offset]) > 48 ||
                    Math.Abs(Marshal.ReadByte(candidateBits, offset + 1) - reference.Bgra[offset + 1]) > 48 ||
                    Math.Abs(Marshal.ReadByte(candidateBits, offset + 2) - reference.Bgra[offset + 2]) > 48)
                    changed++;
            }
        }

        return total == 0 ? 1d : changed / (double)total;
    }

    private static bool PixelsDiffer(byte[] first, int firstOffset, byte[] second, int secondOffset) =>
        Math.Abs(first[firstOffset] - second[secondOffset]) > 48 ||
        Math.Abs(first[firstOffset + 1] - second[secondOffset + 1]) > 48 ||
        Math.Abs(first[firstOffset + 2] - second[secondOffset + 2]) > 48;

    private bool ComposeFrame()
    {
        nint screenDc = GetDC(0);
        if (screenDc == 0)
            return false;

        try
        {
            // 1) 메인 영역을 캔버스 전체에 그대로(1:1) 그린다.
            if (!BitBlt(_memoryDc, 0, 0, Width, Height,
                    screenDc, _mainRegion.X, _mainRegion.Y, Srccopy | CaptureBlt))
                return false;

            // 2) 오버레이들을 등록된 순서대로 그 위에 겹쳐 그린다 — 나중에 등록한
            //    오버레이가 먼저 그려진 것 위를 덮는다(화면 겹침은 자연스럽게 처리됨).
            //    오버레이 원본은 '화면 절대 좌표'가 아니라 '해당 창 기준 상대 위치'로
            //    저장되어 있으므로, 매 프레임 그 창의 현재 위치를 다시 확인해 창을
            //    옮기거나 크기를 바꿔도 오버레이가 따라오게 한다. 창이 닫혔거나
            //    최소화되어 있으면 이번 프레임엔 그 오버레이만 건너뛴다.
            SetStretchBltMode(_memoryDc, HalftoneStretchMode);
            foreach (OverlayRegion overlay in _overlays)
            {
                if (!WindowCaptureService.IsWindowAvailable(overlay.SourceWindowHandle))
                    continue;

                int destX = (int)Math.Round(Math.Clamp(overlay.X, 0, 1) * Width);
                int destY = (int)Math.Round(Math.Clamp(overlay.Y, 0, 1) * Height);
                int destW = Math.Max(1, (int)Math.Round(Math.Clamp(overlay.Width, 0, 1) * Width));
                int destH = Math.Max(1, (int)Math.Round(Math.Clamp(overlay.Height, 0, 1) * Height));

                OverlayWindowCapture? windowCapture;
                OverlayWindowCapture.PixelFrame? capturedFrame;
                lock (_overlayCaptureSync)
                {
                    _overlayCaptures.TryGetValue(overlay.SourceWindowHandle, out windowCapture);
                    capturedFrame = windowCapture?.GetLatestFrame();
                }

                OverlayCropKey cacheKey = new(
                    overlay.SourceWindowHandle,
                    overlay.SourceCropX,
                    overlay.SourceCropY,
                    overlay.SourceCropWidth,
                    overlay.SourceCropHeight);
                CaptureRegion? windowRegion = capturedFrame is null
                    ? WindowCaptureService.GetWindowRegion(overlay.SourceWindowHandle)
                    : null;
                if (capturedFrame is null && windowRegion is null)
                    continue;
                if (capturedFrame is null && windowCapture is not null)
                    continue;

                int sourceWidth = capturedFrame is not null ? capturedFrame.Width : windowRegion!.Width;
                int sourceHeight = capturedFrame is not null ? capturedFrame.Height : windowRegion!.Height;
                int cropX = Math.Clamp((int)Math.Round(Math.Clamp(overlay.SourceCropX, 0, 1) * sourceWidth), 0, sourceWidth - 1);
                int cropY = Math.Clamp((int)Math.Round(Math.Clamp(overlay.SourceCropY, 0, 1) * sourceHeight), 0, sourceHeight - 1);
                int cropW = Math.Clamp((int)Math.Round(Math.Clamp(overlay.SourceCropWidth, 0, 1) * sourceWidth), 1, sourceWidth - cropX);
                int cropH = Math.Clamp((int)Math.Round(Math.Clamp(overlay.SourceCropHeight, 0, 1) * sourceHeight), 1, sourceHeight - cropY);

                bool usePrintWindowFrame = false;
                nint scratchDc = 0;
                if (capturedFrame is not null)
                {
                    CaptureRegion? captureBounds = WindowCaptureService.GetWindowCaptureBounds(
                        overlay.SourceWindowHandle, sourceWidth, sourceHeight);
                    if (windowCapture is not null && captureBounds is not null)
                    {
                        int screenCropX = captureBounds.X + (int)Math.Round(cropX * captureBounds.Width / (double)sourceWidth);
                        int screenCropY = captureBounds.Y + (int)Math.Round(cropY * captureBounds.Height / (double)sourceHeight);
                        int screenCropWidth = Math.Max(1, (int)Math.Round(cropW * captureBounds.Width / (double)sourceWidth));
                        int screenCropHeight = Math.Max(1, (int)Math.Round(cropH * captureBounds.Height / (double)sourceHeight));
                        bool occluded = WindowCaptureService.IsRegionOccluded(
                            overlay.SourceWindowHandle,
                            new CaptureRegion(screenCropX, screenCropY, screenCropWidth, screenCropHeight));

                        OverlayWindowCapture.PixelFrame? lastGoodFrame;
                        bool reportOcclusion = false;
                        lock (_overlayCaptureSync)
                        {
                            _lastGoodOverlayFrames.TryGetValue(cacheKey, out lastGoodFrame);
                            if (lastGoodFrame is not null &&
                                (lastGoodFrame.Width != sourceWidth || lastGoodFrame.Height != sourceHeight))
                                lastGoodFrame = null;

                            if (occluded)
                                reportOcclusion = _reportedOccludedOverlays.Add(cacheKey);
                            else
                            {
                                _lastGoodOverlayFrames[cacheKey] = capturedFrame;
                                _reportedOccludedOverlays.Remove(cacheKey);
                                _reportedPrintWindowOverlays.Remove(cacheKey);
                                _reportedFrozenOverlays.Remove(cacheKey);
                            }
                        }

                        if (reportOcclusion)
                            CompositorWarning?.Invoke($"오버레이 원본 영역 가려짐 감지: '{overlay.SourceWindowTitle}'");

                        if (occluded)
                        {
                            double wgcDifference = lastGoodFrame is null
                                ? 1d
                                : GetCropDifferenceRatio(capturedFrame.Bgra, lastGoodFrame, cropX, cropY, cropW, cropH);

                            // 일부 증권사 화면은 가려지면 WGC 프레임의 내용이 흰색으로
                            // 비워진다. 이때 WM_PRINT를 요청해 한 번 더 그리게 한다.
                            scratchDc = GetOrCreateOverlayScratch(
                                overlay.SourceWindowHandle, sourceWidth, sourceHeight, _memoryDc);
                            bool printed = scratchDc != 0 &&
                                PrintWindow(overlay.SourceWindowHandle, scratchDc, PwRenderFullContent);

                            if (printed && (lastGoodFrame is null ||
                                GetNativeCropDifferenceRatio(
                                    _overlayScratchBuffers[overlay.SourceWindowHandle].Bits,
                                    sourceWidth,
                                    lastGoodFrame,
                                    cropX,
                                    cropY,
                                    cropW,
                                    cropH) + 0.05 < wgcDifference))
                            {
                                usePrintWindowFrame = true;
                                bool reportPrintWindow;
                                lock (_overlayCaptureSync)
                                    reportPrintWindow = _reportedPrintWindowOverlays.Add(cacheKey);
                                if (reportPrintWindow)
                                    CompositorWarning?.Invoke($"가려진 오버레이를 PrintWindow로 다시 그렸습니다: '{overlay.SourceWindowTitle}'");
                            }
                            else if (lastGoodFrame is not null && wgcDifference >= 0.55)
                            {
                                // 대상 앱이 가려진 프레임을 제공하면 빈 화면 대신
                                // 마지막으로 정상 수신한 프레임을 유지한다.
                                capturedFrame = lastGoodFrame;
                                bool reportFrozen;
                                lock (_overlayCaptureSync)
                                    reportFrozen = _reportedFrozenOverlays.Add(cacheKey);
                                if (reportFrozen)
                                    CompositorWarning?.Invoke($"가려진 창 프레임이 비어 마지막 정상 화면을 유지합니다: '{overlay.SourceWindowTitle}'");
                            }
                            else if (lastGoodFrame is null || wgcDifference < 0.55)
                            {
                                lock (_overlayCaptureSync)
                                    _lastGoodOverlayFrames[cacheKey] = capturedFrame;
                            }
                        }
                    }

                    if (usePrintWindowFrame)
                    {
                        StretchBlt(_memoryDc, destX, destY, destW, destH,
                            scratchDc, cropX, cropY, cropW, cropH, Srccopy);
                        continue;
                    }

                    // Use the newest valid HWND frame, including while the source is covered.
                    var info = new BitmapInfoHeader
                    {
                        Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                        Width = capturedFrame.Width,
                        Height = -capturedFrame.Height,
                        Planes = 1,
                        BitCount = 32,
                        Compression = 0
                    };
                    GCHandle pinnedPixels = GCHandle.Alloc(capturedFrame.Bgra, GCHandleType.Pinned);
                    try
                    {
                        StretchDIBits(_memoryDc, destX, destY, destW, destH,
                            cropX, cropY, cropW, cropH,
                            pinnedPixels.AddrOfPinnedObject(), ref info, 0, Srccopy);
                    }
                    finally
                    {
                        pinnedPixels.Free();
                    }

                    continue;
                }

                // Older Windows builds or apps that reject Graphics Capture can still use
                // PrintWindow. Never copy desktop pixels here: that would capture the window
                // currently covering the source instead of the selected source window.
                scratchDc = GetOrCreateOverlayScratch(
                    overlay.SourceWindowHandle, windowRegion!.Width, windowRegion.Height, _memoryDc);
                bool fallbackPrinted = scratchDc != 0 &&
                    PrintWindow(overlay.SourceWindowHandle, scratchDc, PwRenderFullContent);
                if (fallbackPrinted)
                {
                    StretchBlt(_memoryDc, destX, destY, destW, destH,
                        scratchDc, cropX, cropY, cropW, cropH, Srccopy);
                }
            }

            return true;
        }
        finally
        {
            ReleaseDC(0, screenDc);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _pumpTimer?.Dispose();
        _pumpTimer = null;

        try { _pipeServer?.Dispose(); } catch { /* 이미 끊겼으면 무시 */ }
        _pipeServer = null;

        lock (_overlayCaptureSync)
        {
            foreach (OverlayWindowCapture capture in _overlayCaptures.Values)
                capture.Dispose();
            _overlayCaptures.Clear();
            _lastGoodOverlayFrames.Clear();
            _reportedOccludedOverlays.Clear();
            _reportedPrintWindowOverlays.Clear();
            _reportedFrozenOverlays.Clear();
        }

        foreach (OverlayScratchBuffer scratch in _overlayScratchBuffers.Values)
        {
            SelectObject(scratch.Dc, 0);
            DeleteObject(scratch.DibSection);
            DeleteDC(scratch.Dc);
        }
        _overlayScratchBuffers.Clear();

        if (_dibSection != 0)
        {
            SelectObject(_memoryDc, 0);
            DeleteObject(_dibSection);
            _dibSection = 0;
            _dibBits = 0;
        }
        if (_memoryDc != 0)
        {
            DeleteDC(_memoryDc);
            _memoryDc = 0;
        }
    }
}
