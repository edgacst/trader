using System.IO.Pipes;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace StockCasterLive;

/// <summary>
/// PC 소리(데스크톱 오디오, 음악 등)를 "스테레오 믹스" 같은 Windows 녹음 장치 없이
/// 직접 캡처한다. SOOP/Apro 같은 방송 프로그램이 장치 선택 하나만으로 마이크+PC
/// 소리를 같이 보내는 것과 동일한 방식 — WASAPI 루프백으로 현재 기본 재생 장치가
/// 실제로 스피커로 내보내는 신호를 그대로 가져온다. 이 방식은 사운드카드 제조사나
/// 드라이버가 "스테레오 믹스"를 노출하는지 여부와 무관하게 항상 동작하고, 기본
/// 출력 장치가 바뀌어도(예: SB X-Fi ↔ Realtek) 자동으로 그 장치를 따라간다.
///
/// 캡처한 PCM은 이름 있는 파이프(named pipe)에 그대로 흘려보내고, ffmpeg가 그
/// 파이프를 원시 오디오 입력으로 읽어서 마이크 트랙과 섞는다(FfmpegStreamingService
/// 참고). 파이프 서버는 ffmpeg 프로세스를 시작하기 "전"에 미리 연결 대기 상태가
/// 되어 있어야 한다 — 그렇지 않으면 ffmpeg가 파이프를 열 때 상대가 없어 즉시
/// 실패한다.
/// </summary>
public sealed class SystemAudioCapture : IDisposable
{
    private readonly string _pipeName = $"stockcaster_pcaudio_{Guid.NewGuid():N}";
    private WasapiLoopbackCapture? _capture;
    private NamedPipeServerStream? _pipeServer;
    private bool _disposed;

    public string PipePath => $@"\\.\pipe\{_pipeName}";
    public int SampleRate { get; private set; }
    public int Channels { get; private set; }

    /// <summary>
    /// WASAPI 공유 모드 믹스 포맷은 거의 항상 32비트 IEEE float지만, 혹시 드라이버가
    /// 다른 포맷을 주더라도 깨지지 않도록 실제 WaveFormat을 보고 ffmpeg용 원시
    /// 샘플 포맷 문자열(-f 옵션 값)을 돌려준다.
    /// </summary>
    public string FfmpegSampleFormat { get; private set; } = "f32le";

    /// <summary>
    /// 파이프를 만들고 연결 대기를 시작한 뒤, WASAPI 루프백 캡처를 시작한다.
    /// ffmpeg 프로세스를 시작하기 전에 반드시 먼저 호출해야 한다.
    /// </summary>
    public void Start()
    {
        _capture = new WasapiLoopbackCapture();
        WaveFormat format = _capture.WaveFormat;
        SampleRate = format.SampleRate;
        Channels = format.Channels;
        FfmpegSampleFormat = (format.Encoding, format.BitsPerSample) switch
        {
            (WaveFormatEncoding.IeeeFloat, 32) => "f32le",
            (WaveFormatEncoding.Pcm, 16) => "s16le",
            (WaveFormatEncoding.Pcm, 32) => "s32le",
            (WaveFormatEncoding.Pcm, 8) => "u8",
            _ => "f32le"
        };

        // outBufferSize를 기본값(시스템 기본, 보통 수 KB)보다 넉넉하게(1MB ≈ 48kHz
        // 스테레오 32비트 float 기준 약 2.7초 분량) 잡아둔다. ffmpeg가 영상 인코딩
        // 등으로 잠깐 바빠서 이 파이프를 바로 못 읽어가더라도, 버퍼가 작으면 아래
        // pipe.Write가 즉시 블로킹되어 WASAPI 캡처 콜백 스레드(실시간 오디오 스레드)를
        // 멈춰 세운다 — 이게 반복되면 끊기는 소리로 들린다. 버퍼를 키워서 그 정도의
        // 일시적 지연을 블로킹 없이 흡수하게 한다.
        const int pcAudioPipeBufferBytes = 1024 * 1024;
        _pipeServer = new NamedPipeServerStream(
            _pipeName, PipeDirection.Out, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            inBufferSize: 0, outBufferSize: pcAudioPipeBufferBytes);

        // ffmpeg가 파이프를 열 때 상대가 이미 기다리고 있어야 하므로, 연결 수락을
        // 바로 백그라운드로 시작해 둔다(결과를 기다리지 않는다 — 데이터 쓰기 쪽에서
        // IsConnected로 확인한다).
        _ = _pipeServer.WaitForConnectionAsync();

        _capture.DataAvailable += OnDataAvailable;
        _capture.StartRecording();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        NamedPipeServerStream? pipe = _pipeServer;
        if (_disposed || pipe is null || !pipe.IsConnected)
            return;

        try
        {
            pipe.Write(e.Buffer, 0, e.BytesRecorded);
        }
        catch
        {
            // ffmpeg 쪽에서 이 입력을 더 이상 읽지 않으면(파이프가 끊기면) 이후 쓰기는
            // 조용히 무시한다 — PC 소리만 끊기고 방송 자체(영상/마이크)는 계속되게 둔다.
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            try { _capture.StopRecording(); } catch { /* 이미 멈췄으면 무시 */ }
            _capture.Dispose();
            _capture = null;
        }

        try { _pipeServer?.Dispose(); } catch { /* 이미 닫혔으면 무시 */ }
        _pipeServer = null;
    }
}
