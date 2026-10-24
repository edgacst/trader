using System.IO.Pipes;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace StockCasterLive;

/// <summary>
/// 마이크 + PC 소리(WASAPI 루프백)를 ffmpeg 프로세스 "밖"(이 앱, NAudio)에서 미리
/// 섞어서 이미 합쳐진 PCM 한 줄기만 ffmpeg에 넘긴다.
///
/// 왜 이렇게 바꿨는지: 예전에는 ffmpeg 한 프로세스 안에 (1) 화면 캡처(gdigrab),
/// (2) 마이크(dshow 실시간 장치), (3) PC 소리(파이프로 들어오는 실시간 PCM) —
/// 이렇게 "실시간으로 계속 흘러들어오는 입력 2개"를 두고, 그 둘을 amix 필터로
/// 섞어서 영상 인코딩과 같은 -filter_complex 안에 넣었다. 직접 분리 테스트로
/// 확인한 결과, 영상만 있을 때(캡처+모자이크+인코딩)는 항상 완벽한 30fps였지만
/// 거기에 "실시간 입력 2개 + amix"를 더하면 전체가 초당 1~2fps로 주저앉았다 —
/// asetpts 같은 특정 서브 필터를 빼도 동일했고, 마이크를 진짜 장치 대신 가짜
/// 무음 소스(anullsrc)로 바꿔도 동일했다. 즉 특정 필터 문법의 버그가 아니라,
/// "실시간 소스 2개를 한 필터그래프 안에서 섞는 것" 자체가 ffmpeg의 프레임
/// 펌프(filtergraph scheduler)를 막아버리는 구조적 문제였다 — 그리고 그 필터
/// 그래프를 화면 캡처/인코딩과 공유하기 때문에 영상까지 같이 멈춘 것이다.
///
/// 반면 "PC 소리만" 켰을 때(파이프 입력 1개, 필터 없이 그대로 -map)는 계속
/// 멀쩡했다. 그래서 마이크+PC 소리를 둘 다 켤 때도 ffmpeg에는 그 "입력 1개,
/// 필터 없음" 모양만 주도록, 섞는 작업 자체를 이 앱 쪽(C#/NAudio)으로 옮긴다.
/// 마이크(WasapiCapture)와 PC 소리(WasapiLoopbackCapture)를 각각 캡처해 48kHz/
/// 스테레오로 맞춘 뒤 NAudio의 MixingSampleProvider로 더하고, 그 결과를 이름
/// 있는 파이프로 흘려보낸다 — SystemAudioCapture가 PC 소리 하나를 흘려보내던
/// 것과 정확히 같은 모양이다.
/// </summary>
public sealed class AudioMixer : IDisposable
{
    private const int TargetSampleRate = 48000;
    private const int TargetChannels = 2;
    // 20ms마다 섞은 샘플을 뽑아 파이프로 내보낸다 — 방송용 오디오 버퍼링 기준으로
    // 흔히 쓰는 간격이면서, 너무 잦지도 너무 뜸하지도 않다.
    private const int PumpIntervalMs = 20;

    // 마이크와 PC 소리(배경 음악 등)를 그냥 그대로 더하면, 배경 음악은 보통
    // 끊김 없이 계속 높은 레벨로 흐르는 반면 음성은 말할 때만 순간적으로
    // 올라갔다 내려가기를 반복하기 때문에, 음악이 음성을 거의 묻어버리는
    // 느낌이 난다 — 실제 진단 로그로도 확인됨(배경 음악 피크가 계속 0.40~0.45로
    // 유지되는 동안, 말하는 구간의 마이크 피크는 0.15~0.22 정도에 그침). 그래서
    // 믹싱 직전에 마이크는 키우고 PC 소리는 살짝 낮춰서, 실제 방송처럼 해설/음성이
    // 배경 음악 위로 또렷하게 들리게 한다.
    //
    // 기본값은 방송용 인스턴스 기준이다. 생성자에서 다른 값을 넘기면(현재는 녹화용
    // 인스턴스가 마이크를 조금 더 키워서 넘긴다) 이 인스턴스에만 적용되고, 이미
    // 정상 동작 중인 다른 인스턴스(예: 방송)의 레벨에는 전혀 영향을 주지 않는다 —
    // AudioMixer가 방송용/녹화용으로 각각 독립된 인스턴스로 만들어지기 때문이다.
    private const float DefaultMicGain = 1.6f;
    private const float DefaultSystemAudioGain = 0.55f;
    private readonly float _micGain;
    private readonly float _systemAudioGain;

    public AudioMixer(float micGain = DefaultMicGain, float systemAudioGain = DefaultSystemAudioGain)
    {
        _micGain = micGain;
        _systemAudioGain = systemAudioGain;
    }

    // 방송 오디오 끊김("핑 빠지듯" 하는 짧은 공백)을 흡수하기 위한 여유분(jitter
    // buffer) 목표치. 펌프 시작 전 두 캡처 버퍼가 이만큼 쌓일 때까지 기다린다.
    private const int PrimingBufferMs = 200;
    private const int PrimingTimeoutMs = 1500;

    private readonly string _pipeName = $"stockcaster_mixedaudio_{Guid.NewGuid():N}";
    private WasapiCapture? _micCapture;
    private WasapiLoopbackCapture? _systemCapture;
    private BufferedWaveProvider? _micBuffer;
    private BufferedWaveProvider? _systemBuffer;
    private MixingSampleProvider? _mixer;
    private NamedPipeServerStream? _pipeServer;
    private Timer? _pumpTimer;
    private float[]? _pumpSampleBuffer;
    private byte[]? _pumpByteBuffer;
    private bool _pumpErrorLogged;
    private bool _disposed;

    // 마이크/PC 소리 각각이 실제로 데이터를 보내고 있는지, 그리고 그 데이터가
    // 진짜 소리 신호인지(그냥 무음 버퍼가 계속 들어오는 중인지) 눈으로 확인하기
    // 위한 진단용 카운터. WASAPI 루프백은 재생 중인 소리가 없어도 "무음 버퍼"를
    // 계속 흘려보내므로, 바이트 수만으로는 "캡처가 죽었는지"만 알 수 있고 "실제
    // 소리가 찍히는지"는 알 수 없다 — 그래서 피크 진폭도 같이 추적한다.
    private long _micBytesSinceLog;
    private long _systemBytesSinceLog;
    private readonly object _micPeakLock = new();
    private readonly object _systemPeakLock = new();
    private float _micPeakSinceLog;
    private float _systemPeakSinceLog;
    private bool _micPeakReadable;
    private bool _systemPeakReadable;
    private Timer? _diagnosticTimer;

    // 펌프가 읽으려는 순간 버퍼에 그 구간만큼의 데이터가 없었던(=무음으로 채워진)
    // 횟수. 0이 아니면 실제로 끊김이 발생했다는 직접적인 증거가 된다.
    private long _micUnderrunCount;
    private long _systemUnderrunCount;

    public string PipePath => $@"\\.\pipe\{_pipeName}";
    public int SampleRate => TargetSampleRate;
    public int Channels => TargetChannels;
    public string FfmpegSampleFormat => "f32le";

    public event Action<string>? MixerWarning;

    // Start()에서 captureSystemAudio=false로 호출됐는지 기억해 둔다 — 이후
    // WaitForBufferPriming/LogAudioActivity/PumpMixedAudio가 "PC 소리 캡처 자체를
    // 안 쓰는 것"과 "PC 소리 캡처를 썼는데 데이터가 안 들어오는 문제"를 구분할 수
    // 있어야 하기 때문이다.
    private bool _captureSystemAudio = true;

    /// <summary>
    /// 마이크(장치 표시 이름으로 찾음)와, captureSystemAudio가 true면 PC 소리(기본
    /// 재생 장치 루프백)까지 캡처를 시작하고, 섞은 결과를 내보낼 파이프를 만들어
    /// 연결 대기 상태로 둔다. ffmpeg 프로세스를 시작하기 전에 반드시 먼저 호출해야
    /// 한다.
    ///
    /// captureSystemAudio=false(마이크만)일 때도 dshow가 아니라 항상 이 WASAPI
    /// 경로를 쓰는 이유: 녹화는 방송이 이미 진행 중일 때만 시작되는데, 방송
    /// ffmpeg와 녹화 ffmpeg가 "마이크만" 모드에서 각각 dshow로 같은 물리 마이크
    /// 장치를 동시에 열면 — WASAPI 공유 모드와 달리 dshow 쪽은 동시 오픈에
    /// 안전하지 않아 — 녹화본에 "지지직" 하는 잡음이 계속 섞여 들어가는 문제가
    /// 실제로 확인됐다. 마이크를 쓰는 모든 경우에 이 WASAPI 경로(AudioMixer)로
    /// 통일하면 녹화 쪽이 dshow를 아예 열지 않으므로 그 충돌 자체가 없어진다.
    /// </summary>
    public void Start(string micDisplayName, bool captureSystemAudio = true)
    {
        _captureSystemAudio = captureSystemAudio;

        MMDevice micDevice = FindCaptureDevice(micDisplayName)
            ?? throw new InvalidOperationException($"마이크 장치를 찾지 못했습니다: {micDisplayName}");

        _micCapture = new WasapiCapture(micDevice);

        WaveFormat targetFormat = WaveFormat.CreateIeeeFloatWaveFormat(TargetSampleRate, TargetChannels);

        _micBuffer = new BufferedWaveProvider(_micCapture.WaveFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(3)
        };

        ISampleProvider micSample = new VolumeSampleProvider(
            ToTargetFormat(_micBuffer.ToSampleProvider(), _micCapture.WaveFormat, targetFormat))
        { Volume = _micGain };

        // 피크 진폭은 원본 버퍼를 32비트 IEEE float로 직접 해석해서 계산한다 —
        // WASAPI 공유 모드의 기본 믹스 포맷은 거의 항상 float32이고, 실제로 그런
        // 경우에만 계산하고 아니면 "N/A"로 표시해 잘못된 숫자를 보여주지 않는다.
        _micPeakReadable = _micCapture.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat
            && _micCapture.WaveFormat.BitsPerSample == 32;

        var sources = new List<ISampleProvider> { micSample };

        if (captureSystemAudio)
        {
            _systemCapture = new WasapiLoopbackCapture();
            _systemBuffer = new BufferedWaveProvider(_systemCapture.WaveFormat)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(3)
            };
            ISampleProvider systemSample = new VolumeSampleProvider(
                ToTargetFormat(_systemBuffer.ToSampleProvider(), _systemCapture.WaveFormat, targetFormat))
            { Volume = _systemAudioGain };
            sources.Add(systemSample);

            _systemPeakReadable = _systemCapture.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat
                && _systemCapture.WaveFormat.BitsPerSample == 32;
            MixerWarning?.Invoke(
                $"[오디오 진단] 장치 포맷 — 마이크: {_micCapture.WaveFormat} / PC소리(루프백): {_systemCapture.WaveFormat}");
        }
        else
        {
            MixerWarning?.Invoke($"[오디오 진단] 장치 포맷 — 마이크: {_micCapture.WaveFormat} (PC 소리는 사용 안 함)");
        }

        // ReadFully=true: 소스 중 하나가 아직 데이터가 없어도(예: 마이크가 막
        // 시작돼서 첫 버퍼가 비어 있을 때) 그 구간을 무음(0)으로 채워서 계속
        // 진행한다 — 한쪽이 잠깐 끊겼다고 전체 믹스가 멈추면 안 된다.
        _mixer = new MixingSampleProvider(sources) { ReadFully = true };

        const int pipeBufferBytes = 1024 * 1024;
        _pipeServer = new NamedPipeServerStream(
            _pipeName, PipeDirection.Out, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            inBufferSize: 0, outBufferSize: pipeBufferBytes);

        // ffmpeg가 파이프를 열 때 상대가 이미 기다리고 있어야 하므로, 연결 수락을
        // 바로 백그라운드로 시작해 둔다(결과를 기다리지 않는다).
        _ = _pipeServer.WaitForConnectionAsync();

        _micCapture.DataAvailable += OnMicDataAvailable;
        _micCapture.StartRecording();

        if (_systemCapture is not null)
        {
            _systemCapture.DataAvailable += OnSystemDataAvailable;
            _systemCapture.StartRecording();
        }

        // WASAPI 캡처가 매 순간 정확히 실시간 속도로 들어오는 건 아니다 — 화면
        // 캡처/인코딩이 순간적으로 CPU를 많이 쓰거나 Windows 오디오 엔진이 아주
        // 잠깐 지연되면, 버퍼에 짧은 공백이 생길 수 있다. 펌프가 하필 그 순간과
        // 맞물려 읽으면 MixingSampleProvider(ReadFully)가 그 구간을 무음으로
        // 채우게 되는데, 이게 방송에서 "핑 빠지듯" 들리는 짧은 끊김의 정체로
        // 의심된다. 펌프를 시작하기 전에 두 버퍼 모두 최소 PrimingBufferMs만큼
        // 미리 쌓아 두면 그만큼의 여유(jitter buffer)가 생겨서, 순간적인 지연은
        // 이 여유분으로 흡수되고 끊김 없이 넘어간다 — 그 대가로 방송 오디오
        // 지연이 PrimingBufferMs만큼 늘어나지만, 이미 송출 자체에 훨씬 큰 지연이
        // 있는 라이브 방송에서는 체감되지 않는다.
        WaitForBufferPriming();

        int samplesPerPump = TargetSampleRate * PumpIntervalMs / 1000 * TargetChannels;
        _pumpSampleBuffer = new float[samplesPerPump];
        _pumpByteBuffer = new byte[samplesPerPump * sizeof(float)];
        _pumpTimer = new Timer(PumpMixedAudio, null, 0, PumpIntervalMs);

        // 2초마다 "최근 2초간 마이크/PC 소리가 각각 몇 바이트 들어왔는지"를 로그로
        // 남긴다. PC 소리 쪽이 계속 0B면 WASAPI 루프백이 애초에 아무것도 못 받고
        // 있다는 뜻이고, 0B가 아닌데도 녹화/방송에 소리가 안 들어간다면 문제가
        // 믹싱 이후 단계에 있다는 뜻이다.
        _diagnosticTimer = new Timer(LogAudioActivity, null, 2000, 2000);
    }

    private void OnMicDataAvailable(object? sender, WaveInEventArgs e)
    {
        _micBuffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
        Interlocked.Add(ref _micBytesSinceLog, e.BytesRecorded);
        if (_micPeakReadable)
        {
            float peak = ComputePeak(e.Buffer, e.BytesRecorded);
            lock (_micPeakLock)
            {
                if (peak > _micPeakSinceLog)
                    _micPeakSinceLog = peak;
            }
        }
    }

    private void OnSystemDataAvailable(object? sender, WaveInEventArgs e)
    {
        _systemBuffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
        Interlocked.Add(ref _systemBytesSinceLog, e.BytesRecorded);
        if (_systemPeakReadable)
        {
            float peak = ComputePeak(e.Buffer, e.BytesRecorded);
            lock (_systemPeakLock)
            {
                if (peak > _systemPeakSinceLog)
                    _systemPeakSinceLog = peak;
            }
        }
    }

    // 버퍼를 32비트 IEEE float 샘플로 해석해 이번 콜백에서의 최대 절대 진폭을
    // 구한다(0.0=완전 무음 ~ 1.0=풀스케일). 호출 전에 _xxxPeakReadable로 포맷을
    // 확인했다고 가정한다.
    private static float ComputePeak(byte[] buffer, int bytesRecorded)
    {
        float peak = 0f;
        int sampleCount = bytesRecorded / sizeof(float);
        for (int i = 0; i < sampleCount; i++)
        {
            float abs = Math.Abs(BitConverter.ToSingle(buffer, i * sizeof(float)));
            if (abs > peak)
                peak = abs;
        }
        return peak;
    }

    // 펌프 타이머를 시작하기 전에, 두 캡처 버퍼가 PrimingBufferMs만큼 쌓일 때까지
    // 짧게 기다린다. 장치가 예상보다 느리게 응답해도 PrimingTimeoutMs 이상은
    // 기다리지 않고 그냥 진행한다(여유 없이 시작할 뿐, 방송 자체를 막지는 않는다).
    private void WaitForBufferPriming()
    {
        long startTick = Environment.TickCount64;
        while (Environment.TickCount64 - startTick < PrimingTimeoutMs)
        {
            double micMs = _micBuffer?.BufferedDuration.TotalMilliseconds ?? 0;
            // PC 소리를 아예 안 쓰면(_systemBuffer가 null) 기다릴 버퍼가 없으므로
            // 이미 다 찼다고 취급한다 — 그래야 마이크만으로도 정상적으로 시작된다.
            double systemMs = _systemBuffer?.BufferedDuration.TotalMilliseconds ?? PrimingBufferMs;
            if (micMs >= PrimingBufferMs && systemMs >= PrimingBufferMs)
                return;

            Thread.Sleep(10);
        }

        MixerWarning?.Invoke("[오디오 진단] 초기 버퍼링이 예상보다 오래 걸려 여유(jitter buffer) 없이 방송을 시작합니다.");
    }

    private void LogAudioActivity(object? state)
    {
        long mic = Interlocked.Exchange(ref _micBytesSinceLog, 0);
        long system = Interlocked.Exchange(ref _systemBytesSinceLog, 0);
        long micUnderruns = Interlocked.Exchange(ref _micUnderrunCount, 0);
        long systemUnderruns = Interlocked.Exchange(ref _systemUnderrunCount, 0);

        float micPeak;
        lock (_micPeakLock) { micPeak = _micPeakSinceLog; _micPeakSinceLog = 0f; }
        float systemPeak;
        lock (_systemPeakLock) { systemPeak = _systemPeakSinceLog; _systemPeakSinceLog = 0f; }

        string micPeakText = _micPeakReadable ? micPeak.ToString("0.000") : "N/A";

        string micNote = mic == 0
            ? " ※ 마이크 데이터가 전혀 들어오지 않고 있습니다."
            : string.Empty;

        if (!_captureSystemAudio)
        {
            // PC 소리를 아예 안 쓰는 인스턴스(마이크만)에서는 PC 소리 관련 수치를
            // 아예 찍지 않는다 — 0B라고 찍으면 "PC 소리가 안 들어온다"는 문제처럼
            // 오해할 수 있다. 끊김(underrun) 집계도 마이크 쪽만 의미가 있다.
            string micOnlyUnderrunNote = micUnderruns > 0
                ? $" ※ 버퍼 부족(끊김 발생) — 마이크 {micUnderruns}회"
                : string.Empty;
            MixerWarning?.Invoke(
                $"[오디오 진단] 최근 2초간 — 마이크 {mic}B(피크 {micPeakText}){micNote}{micOnlyUnderrunNote}");
            return;
        }

        string systemPeakText = _systemPeakReadable ? systemPeak.ToString("0.000") : "N/A";
        string systemNote = system == 0
            ? " ※ PC 소리 데이터가 전혀 들어오지 않고 있습니다(WASAPI 루프백이 아무것도 못 받는 중)."
            : (_systemPeakReadable && systemPeak < 0.001f
                ? " ※ PC 소리는 들어오고 있지만 거의 완전한 무음입니다(실제 소리 신호가 찍히지 않음)."
                : string.Empty);
        // 0이 아니면 그 횟수만큼 실제로 짧은 무음 구간이 방송/녹화에 섞여 들어갔다는
        // 뜻이다 — "핑 빠지듯" 들리는 끊김의 직접적인 증거.
        string underrunNote = micUnderruns > 0 || systemUnderruns > 0
            ? $" ※ 버퍼 부족(끊김 발생) — 마이크 {micUnderruns}회, PC소리 {systemUnderruns}회"
            : string.Empty;

        MixerWarning?.Invoke(
            $"[오디오 진단] 최근 2초간 — 마이크 {mic}B(피크 {micPeakText}), PC소리 {system}B(피크 {systemPeakText}){micNote}{systemNote}{underrunNote}");
    }

    private void PumpMixedAudio(object? state)
    {
        MixingSampleProvider? mixer = _mixer;
        NamedPipeServerStream? pipe = _pipeServer;
        float[]? sampleBuffer = _pumpSampleBuffer;
        byte[]? byteBuffer = _pumpByteBuffer;
        if (_disposed || mixer is null || pipe is null || sampleBuffer is null || byteBuffer is null || !pipe.IsConnected)
            return;

        try
        {
            // 이번에 읽으려는 만큼(PumpIntervalMs)의 데이터가 버퍼에 이미 쌓여
            // 있었는지 먼저 확인한다 — 부족했다면 MixingSampleProvider가 그만큼을
            // 무음으로 채워 넣는다는 뜻이고, 그게 바로 방송에서 들리는 짧은 끊김이다.
            if ((_micBuffer?.BufferedDuration.TotalMilliseconds ?? 0) < PumpIntervalMs)
                Interlocked.Increment(ref _micUnderrunCount);
            // PC 소리를 아예 안 쓰면(_systemBuffer가 null) 비교할 버퍼가 없으므로
            // 끊김으로 셀 것이 없다 — null이면 건너뛴다(그냥 0 < PumpIntervalMs로
            // 매번 참이 되어 끊김이 계속 세어지는 것을 막는다).
            if (_systemBuffer is not null && _systemBuffer.BufferedDuration.TotalMilliseconds < PumpIntervalMs)
                Interlocked.Increment(ref _systemUnderrunCount);

            int read = mixer.Read(sampleBuffer, 0, sampleBuffer.Length);
            if (read <= 0)
                return;

            // 마이크/PC 소리에 각각 게인을 적용한 뒤 단순히 더하기만 하므로, 둘 다
            // 동시에 큰 소리를 내는 드문 순간에는 합이 [-1, 1] 범위를 넘어갈 수
            // 있다 — 그대로 내보내면 디코더 쪽에서 지글거리는 클리핑 잡음이 될 수
            // 있으므로 여기서 안전하게 잘라낸다.
            for (int i = 0; i < read; i++)
                sampleBuffer[i] = Math.Clamp(sampleBuffer[i], -1f, 1f);

            Buffer.BlockCopy(sampleBuffer, 0, byteBuffer, 0, read * sizeof(float));
            pipe.Write(byteBuffer, 0, read * sizeof(float));
        }
        catch (Exception ex)
        {
            // ffmpeg가 더 이상 안 읽거나 파이프가 끊겼으면 조용히 무시한다 — 영상
            // 송출 자체는 계속되게 둔다. 다만 첫 실패는 한 번만 바깥에 알린다.
            if (!_pumpErrorLogged)
            {
                _pumpErrorLogged = true;
                MixerWarning?.Invoke($"오디오 믹서 파이프 쓰기 실패: {ex.Message}");
            }
        }
    }

    private static ISampleProvider ToTargetFormat(ISampleProvider source, WaveFormat sourceFormat, WaveFormat targetFormat)
    {
        ISampleProvider result = source;

        if (sourceFormat.Channels != targetFormat.Channels)
        {
            result = sourceFormat.Channels == 1
                ? new MonoToStereoSampleProvider(result)
                : new StereoToMonoSampleProvider(result);
        }

        if (sourceFormat.SampleRate != targetFormat.SampleRate)
            result = new WdlResamplingSampleProvider(result, targetFormat.SampleRate);

        return result;
    }

    private static MMDevice? FindCaptureDevice(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return null;

        // NAudio의 MMDeviceEnumerator는 IDisposable이 아니므로 using 없이 그냥
        // 쓰고 GC에 맡긴다.
        var enumerator = new MMDeviceEnumerator();
        MMDeviceCollection devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);

        foreach (MMDevice device in devices)
        {
            if (string.Equals(device.FriendlyName, displayName, StringComparison.OrdinalIgnoreCase))
                return device;
        }

        // dshow가 보여주는 이름과 WASAPI FriendlyName의 표기가 완전히 같지 않을 수
        // 있으므로(괄호, 공백 등 차이), 부분 포함으로 한 번 더 찾아본다.
        foreach (MMDevice device in devices)
        {
            if (device.FriendlyName.Contains(displayName, StringComparison.OrdinalIgnoreCase) ||
                displayName.Contains(device.FriendlyName, StringComparison.OrdinalIgnoreCase))
                return device;
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _pumpTimer?.Dispose();
        _pumpTimer = null;

        _diagnosticTimer?.Dispose();
        _diagnosticTimer = null;

        if (_micCapture is not null)
        {
            _micCapture.DataAvailable -= OnMicDataAvailable;
            try { _micCapture.StopRecording(); } catch { /* 이미 멈췄으면 무시 */ }
            _micCapture.Dispose();
            _micCapture = null;
        }

        if (_systemCapture is not null)
        {
            _systemCapture.DataAvailable -= OnSystemDataAvailable;
            try { _systemCapture.StopRecording(); } catch { /* 이미 멈췄으면 무시 */ }
            _systemCapture.Dispose();
            _systemCapture = null;
        }

        try { _pipeServer?.Dispose(); } catch { /* 이미 닫혔으면 무시 */ }
        _pipeServer = null;
    }
}
