using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace StockCasterLive;

public sealed class AudioDevice
{
    public string DisplayName { get; init; } = string.Empty;
    public string DevicePath { get; init; } = string.Empty;
}

public sealed class StreamingOptions
{
    public WindowInfo SourceWindow { get; init; } = new();
    public string ServerUrl { get; init; } = string.Empty;
    public string StreamKey { get; init; } = string.Empty;
    public int OutputHeight { get; init; } = 1080;
    public int FrameRate { get; init; } = 30;
    public AudioDevice? AudioDevice { get; init; }
    // PC 내부 소리(음악 등)를 마이크와 함께 방송할지 여부. 켜지면 WASAPI 루프백으로
    // 현재 기본 재생 장치의 소리를 직접 캡처해서(SystemAudioCapture) 마이크와 함께
    // ffmpeg의 amix 필터로 섞어 내보낸다. "스테레오 믹스" 같은 별도 녹음 장치가
    // 필요 없다 — Apro/SOOP처럼 장치 선택 없이 항상 동작한다.
    public bool CaptureSystemAudio { get; init; }
    public IReadOnlyList<PrivacyMask> PrivacyMasks { get; init; } = Array.Empty<PrivacyMask>();
    // 비어 있지 않으면 방송과 동시에 이 폴더에 녹화 파일도 저장한다(ffmpeg tee 먹서로
    // 같은 인코딩 결과를 방송 서버와 로컬 파일에 동시에 내보낸다 — 인코딩을 두 번 하지
    // 않아 PC 부하가 거의 늘지 않는다).
    public string? RecordingFolderPath { get; init; }
}

public sealed class FfmpegStreamingService
{
    private readonly object _sync = new();
    private readonly Queue<string> _recentErrors = new();
    private readonly Queue<string> _relayErrors = new();
    private Process? _process;
    private Process? _lowLatencyProcess;
    private Process? _recordingProcess;
    private readonly Queue<string> _recordingErrors = new();
    private SystemAudioCapture? _systemAudioCapture;
    private AudioMixer? _audioMixer;
    private bool _stopping;
    private bool _starting;
    private bool? _nvencAvailable;
    private readonly Dictionary<int, bool> _desktopDuplicationAvailability = new();
    private string _secretToRedact = string.Empty;
    // 방송이 시작될 때 받은 옵션(캡처 영역/해상도/프레임레이트/오디오 장치/모자이크
    // 등)을 기억해 둔다 — 녹화는 방송용 메인 ffmpeg와 완전히 독립된 화면/오디오
    // 캡처+인코딩이라, 방송 도중 사용자가 "녹화 시작"을 눌러도 StartAsync를 다시
    // 거치지 않고 이 옵션 그대로 녹화용 ffmpeg를 띄울 수 있어야 한다. (예전에는 이미
    // 서버에 올라간 스트림을 다시 읽어오는 RTMP pull 방식을 썼는데, 유튜브 등 대부분의
    // 공개 플랫폼이 올린 스트림을 다시 읽어가는 것 자체를 막아서 전혀 동작하지
    // 않았다 — 그래서 서버를 거치지 않는 이 방식으로 바꿨다.)
    private StreamingOptions? _currentOptions;
    // 녹화 전용 오디오 캡처 인스턴스. 방송용(_audioMixer/_systemAudioCapture)과는
    // 완전히 별개로 새로 띄운다 — WASAPI는 같은 장치를 공유 모드로 여러 클라이언트가
    // 동시에 열 수 있어 서로 간섭하지 않는다.
    private AudioMixer? _recordingAudioMixer;
    private SystemAudioCapture? _recordingSystemAudioCapture;
    // StopRecordingAsync로 "녹화만" 멈출 때 RecordingProcess_Exited가 "예기치 않게
    // 종료됨"으로 오인하지 않도록 쓰는 플래그. 전체 방송을 멈출 때는 이미 _stopping이
    // 그 역할을 하므로, 이 플래그는 방송은 계속 켜둔 채 녹화만 끌 때만 의미가 있다.
    private bool _stoppingRecordingOnly;

    // FFmpeg 오류 창구(_recentErrors)는 마지막 12줄만 화면에 요약해서 보여주기 때문에,
    // "Conversion failed!" 같은 FFmpeg의 마지막 종료 메시지에 진짜 원인 줄이 가려질 수 있다.
    // 방송 시작마다 전체 stderr/stdout을 파일로 남겨서 문제가 생겼을 때 실제 원인을 확인할 수 있게 한다.
    private static string LogFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EdgarCST", "EdgarCasterLive", "logs", "ffmpeg-last.log");

    private void ResetLogFile()
    {
        try
        {
            string? directory = Path.GetDirectoryName(LogFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(LogFilePath,
                $"=== StockCaster Live ffmpeg 로그 — {DateTime.Now:yyyy-MM-dd HH:mm:ss} 시작 ==={Environment.NewLine}");
        }
        catch
        {
            // 로그 파일을 만들지 못해도 방송 자체는 계속 진행한다.
        }
    }

    private void AppendLog(string tag, string line)
    {
        try
        {
            File.AppendAllText(LogFilePath, $"[{tag}] {line}{Environment.NewLine}");
        }
        catch
        {
            // 로그 기록 실패는 방송에 영향을 주지 않는다.
        }
    }

    public bool IsStreaming
    {
        get
        {
            lock (_sync)
                return _process is { HasExited: false };
        }
    }

    // 현재(또는 방금 끝난) 방송의 녹화 파일 전체 경로. 녹화를 켜지 않았거나 녹화 폴더
    // 준비에 실패해 방송만 진행 중이면 null이다. 녹화 중에는 .ts 경로를 가리키고,
    // StopAsync가 끝나면 mp4 변환 결과 경로(실패 시에는 원본 .ts 경로)로 바뀐다.
    // CleanupProcess에서는 더 이상 비우지 않는다 — MainWindow가 StopAsync를 기다린
    // 뒤에 이 값을 읽어 "녹화 파일이 저장되었습니다: ..." 안내에 쓰기 때문이다.
    // 다음 방송을 시작하면(StartAsync) 새 값으로 다시 채워진다.
    private string? _currentRecordingFilePath;
    public string? CurrentRecordingFilePath
    {
        get { lock (_sync) return _currentRecordingFilePath; }
        private set { lock (_sync) _currentRecordingFilePath = value; }
    }

    public event Action<string>? StatusReceived;
    public event Action<string?>? StreamEnded;

    public static string ResolveFfmpegPath()
    {
        string packaged = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        if (File.Exists(packaged))
            return packaged;

        return "ffmpeg.exe";
    }

    public List<AudioDevice> ListAudioDevices()
    {
        var devices = new List<AudioDevice>();
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveFfmpegPath(),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-list_devices");
        startInfo.ArgumentList.Add("true");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("dshow");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add("dummy");

        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null)
                return devices;

            string output = process.StandardError.ReadToEnd();
            process.WaitForExit(5000);
            string[] lines = output.Split('\n');

            for (int index = 0; index < lines.Length; index++)
            {
                string line = lines[index].Trim();
                if (!line.Contains("(audio)", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Alternative name", StringComparison.OrdinalIgnoreCase))
                    continue;

                Match nameMatch = Regex.Match(line, "\"(.+?)\"\\s*\\(audio\\)", RegexOptions.IgnoreCase);
                if (!nameMatch.Success)
                    continue;

                string name = nameMatch.Groups[1].Value;
                string path = name;
                if (index + 1 < lines.Length)
                {
                    Match pathMatch = Regex.Match(lines[index + 1], "Alternative name \\\"(.+?)\\\"", RegexOptions.IgnoreCase);
                    if (pathMatch.Success)
                        path = pathMatch.Groups[1].Value;
                }

                if (devices.All(device => !string.Equals(device.DevicePath, path, StringComparison.OrdinalIgnoreCase)))
                    devices.Add(new AudioDevice { DisplayName = name, DevicePath = path });
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "FFmpeg를 실행할 수 없습니다. 프로젝트의 ffmpeg 폴더를 확인해 주세요.", ex);
        }

        return devices;
    }

    public async Task StartAsync(StreamingOptions options)
    {
        lock (_sync)
        {
            if (_process is { HasExited: false })
                throw new InvalidOperationException("이미 방송을 송출하고 있습니다.");
        }

        if (!File.Exists(ResolveFfmpegPath()) && ResolveFfmpegPath() == "ffmpeg.exe")
        {
            // PATH에 설치된 FFmpeg는 Process.Start 단계에서 확인된다.
        }

        if (options.SourceWindow.Region is null && !WindowCaptureService.IsWindowAvailable(options.SourceWindow.Handle))
            throw new InvalidOperationException("선택한 차트 창이 닫혔거나 최소화되어 있습니다.");

        int outputWidth = options.OutputHeight == 1080 ? 1920 : 1280;
        // Screen captures contain fine chart lines and small text. The previous
        // 8 Mbps/1080p cap was tuned for camera video and made those details look
        // soft after the RTMP -> WebRTC/HLS remux. Keep latency low, but allocate
        // enough CBR headroom for crisp screen content at both frame rates.
        int videoBitrate = options.OutputHeight == 1080
            ? (options.FrameRate == 60 ? 14000 : 11000)
            : (options.FrameRate == 60 ? 7500 : 5000);
        bool useHardwareEncoder = await IsNvencAvailableAsync();

        // Always resolve a desktop-coordinate capture region, even in "select window"
        // mode. That mode used to capture via ffmpeg's own `hwnd=` gdigrab source,
        // which renders the window independently of this app's PrintWindow-based live
        // preview and is not guaranteed to agree with it on window bounds/content.
        // Because privacy masks are drawn and normalized against the preview frame,
        // that mismatch let a mosaic look correctly placed in the operator's preview
        // while landing on the wrong pixels (or missing entirely) in the actual
        // broadcast. Capturing the same desktop rectangle for both preview and stream
        // keeps them pixel-aligned so the mask always covers what it covers on screen.
        CaptureRegion? effectiveRegion = options.SourceWindow.Region
            ?? WindowCaptureService.GetWindowRegion(options.SourceWindow.Handle);
        if (effectiveRegion is null)
            throw new InvalidOperationException("선택한 차트 창의 화면 위치를 확인하지 못했습니다. 창을 다시 선택해 주세요.");

        // GPU 데스크톱 복제(ddagrab) 캡처는 항상 GPU→CPU(hwdownload)→GPU(NVENC 업로드) 왕복을
        // 거치는데, 이 하드웨어 조합에서는 그 왕복이 실시간보다 3~5배 느려서(speed=0.2~0.6x)
        // 인코딩이 밀리다가 결국 방송 서버 연결이 끊기는 것이 로그로 확인됐다. 이 경로는 창 선택
        // 모드에서 방금 전 좌표 수정으로 처음 시도되기 시작한 것이라 실전 검증이 안 된 상태였으므로,
        // 검증된 gdigrab(CPU) 캡처 경로만 쓰도록 당분간 비활성화한다.
        bool useDesktopDuplication = false;
        DesktopCaptureTarget? desktopCapture = null;
        string outputUrl = BuildOutputUrl(options.ServerUrl, options.StreamKey);
        _currentOptions = options;
        _secretToRedact = ExtractQueryValue(options.StreamKey, "pass") ?? options.StreamKey;
        _recentErrors.Clear();
        _stopping = false;
        _starting = true;
        ResetLogFile();
        AppendLog("INFO", $"captureRegion={effectiveRegion.Width}x{effectiveRegion.Height}, " +
            $"desktopDuplication={useDesktopDuplication}, hwEncoder={useHardwareEncoder}, masks={options.PrivacyMasks.Count}, " +
            $"mic={options.AudioDevice is not null}, pcAudio={options.CaptureSystemAudio}");

        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveFfmpegPath(),
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8
        };

        Add(startInfo, "-hide_banner", "-loglevel", "info");

        if (!useDesktopDuplication)
        {
            Add(startInfo,
                "-thread_queue_size", "1024",
                "-f", "gdigrab",
                "-framerate", options.FrameRate.ToString(CultureInfo.InvariantCulture),
                "-draw_mouse", "1",
                "-use_wallclock_as_timestamps", "1",
                "-offset_x", effectiveRegion.X.ToString(CultureInfo.InvariantCulture),
                "-offset_y", effectiveRegion.Y.ToString(CultureInfo.InvariantCulture),
                "-video_size", $"{effectiveRegion.Width}x{effectiveRegion.Height}",
                "-i", "desktop");
        }

        // 오디오 입력은 붙인 순서대로 ffmpeg 입력 인덱스가 매겨진다(영상이 항상 0번).
        // 마이크를 먼저, PC 소리(2번째 오디오 장치)를 그다음에 붙이므로 각 장치가 실제로
        // 몇 번 입력이 되는지 여기서 추적해 뒤에서 -map/-filter_complex에 그대로 쓴다.
        // GPU 데스크톱 복제(ddagrab) 경로는 영상을 별도 "-i"가 아니라 필터 그래프 안의
        // 소스로 만들기 때문에 그 경로에서는 오디오가 0번 입력부터 시작한다(원래 코드의
        // "0:a:0" 매핑과 동일). 그 외에는 gdigrab "-i desktop"이 항상 0번을 차지하므로
        // 오디오는 1번부터 시작한다.
        int micInputIndex = -1;
        int secondaryInputIndex = -1;
        int nextInputIndex = useDesktopDuplication ? 0 : 1;

        // === 오디오 입력 구성 ===
        // 마이크 캡처는 방송과 녹화 모두 WASAPI AudioMixer로 처리한다.
        // PC 소리도 켠 경우 루프백을 믹서에 추가하고, FFmpeg에는 단일 PCM 입력만 전달한다.
        // 이전 FFmpeg amix 방식은 영상 인코딩과 같은 실시간 필터그래프에서 두 입력을 섞어
        // 프레임 처리를 크게 늦출 수 있어 믹싱을 앱 쪽으로 옮겼다.
        _audioMixer?.Dispose();
        _audioMixer = null;
        _systemAudioCapture?.Dispose();
        _systemAudioCapture = null;

        bool wantsMic = options.AudioDevice is not null;
        bool wantsSystemAudio = options.CaptureSystemAudio;

        if (wantsMic)
        {
            try
            {
                var mixer = new AudioMixer();
                mixer.MixerWarning += warning => AppendLog("INFO", warning);
                mixer.Start(options.AudioDevice!.DisplayName, captureSystemAudio: wantsSystemAudio);
                _audioMixer = mixer;
                AppendLog("INFO", wantsSystemAudio ? "마이크+PC 소리를 이 앱에서 미리 섞어(AudioMixer) 단일 입력으로 ffmpeg에 전달합니다." : "마이크를 WASAPI AudioMixer 단일 입력으로 ffmpeg에 전달합니다.");
            }
            catch (Exception ex)
            {
                AppendLog("INFO", $"마이크/PC 소리 WASAPI 캡처(AudioMixer)를 시작하지 못해 대체 입력으로 전환합니다: {ex.Message}");
            }
        }

        if (_audioMixer is not null)
        {
            // AudioMixer가 이미 48kHz/스테레오 float(f32le)로 섞어서 내보내므로,
            // ffmpeg 쪽은 raw PCM 파이프를 그대로 읽어 들이기만 하면 된다 — 필터도,
            // 두 번째 입력도 필요 없다.
            Add(startInfo,
                "-thread_queue_size", "1024",
                "-f", _audioMixer.FfmpegSampleFormat,
                "-ar", _audioMixer.SampleRate.ToString(CultureInfo.InvariantCulture),
                "-ac", _audioMixer.Channels.ToString(CultureInfo.InvariantCulture),
                "-i", _audioMixer.PipePath);
            micInputIndex = nextInputIndex++;
        }
        else
        {
            if (wantsMic)
            {
                // WASAPI AudioMixer 시작 실패 시에만 쓰는 대체 경로다.
                // dshow는 ffmpeg가 오디오를 읽어가는 것과 무관하게 자체 내부 링버퍼(기본
                // ~3MB, 로그의 "rtbufsize parameter" 값)에 캡처한 오디오를 계속 쌓는다.
                // 화면 캡처+CPU x264 인코딩 스레드가 잠깐이라도 밀리면 ffmpeg 메인
                // 루프가 그 버퍼를 제때 못 비우고, 그러면 "real-time buffer ... too
                // full ... frame dropped!" 경고와 함께 오디오 프레임이 그냥 버려진다.
                // -rtbufsize로 dshow 쪽 링버퍼 자체를 키워서(기본 3MB -> 200MB) 짧은
                // 스톨을 흡수하게 하면 이 드롭이 사라진다.
                Add(startInfo,
                    "-thread_queue_size", "1024",
                    "-f", "dshow",
                    "-rtbufsize", "200M");
                Add(startInfo, "-use_wallclock_as_timestamps", "1");
                Add(startInfo, "-i", $"audio={options.AudioDevice!.DevicePath}");
                micInputIndex = nextInputIndex++;
            }

            if (wantsSystemAudio)
            {
                // PC 소리는 "스테레오 믹스" 같은 dshow 녹음 장치 대신 WASAPI 루프백으로
                // 직접 캡처한다(SystemAudioCapture). ffmpeg가 이 파이프를 열 때 반드시
                // 서버가 먼저 연결 대기 중이어야 하므로, ffmpeg 프로세스를 시작하기 전인
                // 지금 미리 Start()한다. 루프백 시작 자체가 실패하면(예: 재생 장치가
                // 하나도 없음) PC 소리만 포기하고 마이크/영상 방송은 계속 진행한다.
                try
                {
                    var capture = new SystemAudioCapture();
                    capture.Start();
                    _systemAudioCapture = capture;
                }
                catch (Exception ex)
                {
                    AppendLog("INFO", $"PC 소리 캡처(WASAPI 루프백)를 시작하지 못해 건너뜁니다: {ex.Message}");
                }
            }

            if (_systemAudioCapture is not null)
            {
                // WASAPI 루프백이 흘려보내는 원시 PCM을 이름 있는 파이프에서 그대로
                // 읽는다. 장치가 아니라 파일(파이프)을 여는 것이므로 -f dshow가 아니라
                // 실제 샘플 포맷(f32le 등)을 지정한다. 여기에는
                // -use_wallclock_as_timestamps를 켜면 안 된다 — raw PCM 파이프
                // 입력에서는 그 옵션이 "현재 시각"을 그대로 프레임 간 시간차로
                // 취급해버려 타임스탬프가 깨진다. raw PCM은 샘플 수만으로 정확한
                // 경과 시간이 나오므로 기본 샘플 기반 타임스탬프로 충분하다.
                Add(startInfo,
                    "-thread_queue_size", "1024",
                    "-f", _systemAudioCapture.FfmpegSampleFormat,
                    "-ar", _systemAudioCapture.SampleRate.ToString(CultureInfo.InvariantCulture),
                    "-ac", _systemAudioCapture.Channels.ToString(CultureInfo.InvariantCulture),
                    "-i", _systemAudioCapture.PipePath);
                secondaryInputIndex = nextInputIndex++;
            }
        }

        int singleAudioInputIndex = micInputIndex >= 0 ? micInputIndex : secondaryInputIndex;

        IReadOnlyList<PrivacyMask> validMasks = options.PrivacyMasks.Where(IsValidPrivacyMask).ToList();
        if (useDesktopDuplication && desktopCapture is not null)
        {
            string videoFilter = BuildDesktopDuplicationFilter(
                desktopCapture, validMasks, outputWidth, options.OutputHeight, options.FrameRate);
            Add(startInfo, "-filter_complex", videoFilter, "-map", "[video_out]");
            if (singleAudioInputIndex >= 0)
                Add(startInfo, "-map", $"{singleAudioInputIndex}:a:0");
        }
        else if (validMasks.Count > 0)
        {
            string videoFilter = BuildVideoFilterComplex("[0:v]", validMasks, outputWidth, options.OutputHeight);
            Add(startInfo, "-filter_complex", videoFilter, "-map", "[video_out]");
            if (singleAudioInputIndex >= 0)
                Add(startInfo, "-map", $"{singleAudioInputIndex}:a:0");
        }
        else
        {
            Add(startInfo, "-vf", BuildScaleFilter(outputWidth, options.OutputHeight));
        }

        if (useHardwareEncoder)
        {
            Add(startInfo,
                "-c:v", "h264_nvenc",
                "-preset", "p5",
                "-tune", "ll",
                "-rc", "cbr");
        }
        else
        {
            Add(startInfo,
                "-c:v", "libx264",
                "-preset", "faster",
                "-tune", "zerolatency",
                "-sc_threshold", "0");
        }

        Add(startInfo,
            "-b:v", $"{videoBitrate}k",
            "-maxrate", $"{videoBitrate}k",
            "-bufsize", $"{videoBitrate * 2}k",
            "-pix_fmt", "yuv420p",
            "-r", options.FrameRate.ToString(CultureInfo.InvariantCulture),
            "-fps_mode", "cfr",
            "-g", options.FrameRate.ToString(CultureInfo.InvariantCulture),
            "-keyint_min", options.FrameRate.ToString(CultureInfo.InvariantCulture));

        if (micInputIndex >= 0 || secondaryInputIndex >= 0)
            Add(startInfo, "-c:a", "aac", "-b:a", "160k", "-ar", "44100", "-ac", "2");
        else
            Add(startInfo, "-an");

        // 녹화는 ffmpeg의 tee 먹서로 방송 인코딩과 "같은 프로세스 안"에서 같이 내보내지
        //않는다. h264_nvenc + tee 조합에서 로컬 파일 슬레이브(mkv/mp4/ts 어떤 포맷이든)를
        // 추가하면 방송 서버 연결 자체가 "Error number -10053(연결 중단)"으로 끊기는 걸
        // 실제 테스트로 재현/확인했다(use_fifo, onfail=ignore 등 tee 옵션을 다 써봐도
        // 동일) — tee가 인코더 출력을 두 슬레이브에 나눠 쓰는 과정 자체가 이 PC의
        // NVENC/네트워크 조합에서 방송 타이밍을 깨는 것으로 보인다. 그래서 방송용 ffmpeg는
        // 녹화 유무와 상관없이 항상 지금까지 안정적으로 검증된 단일 "-f flv" 출력만 쓰고
        // (아래), 녹화는 완전히 별개의 두 번째 ffmpeg 프로세스가 "방금 이 PC가 방송
        // 서버에 올린 바로 그 스트림"을 다시 읽어 들여(RTMP pull, 재인코딩 없이
        // -c copy) 로컬 .ts 파일로 저장하는 방식으로 뗀다(StartRecordingRelay). 이렇게
        // 완전히 분리하면 녹화 쪽에 어떤 문제가 생겨도(네트워크 재연결, 디스크 문제 등)
        // 방송 프로세스 자체는 구조적으로 전혀 영향을 받지 않는다.
        Add(startInfo, "-f", "flv", "-flvflags", "no_duration_filesize", outputUrl);

        string? recordingFilePath = null;
        if (!string.IsNullOrWhiteSpace(options.RecordingFolderPath))
        {
            try
            {
                Directory.CreateDirectory(options.RecordingFolderPath);
                string fileName = $"stockcaster-{DateTime.Now:yyyyMMdd-HHmmss}.ts";
                recordingFilePath = Path.Combine(options.RecordingFolderPath, fileName);
            }
            catch (Exception ex)
            {
                AppendLog("INFO", $"녹화 폴더를 준비하지 못해 녹화 없이 방송만 진행합니다: {ex.Message}");
            }
        }

        CurrentRecordingFilePath = recordingFilePath;

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        process.ErrorDataReceived += Process_ErrorDataReceived;
        process.Exited += Process_Exited;

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("FFmpeg 방송 프로세스를 시작하지 못했습니다.");

            // PC 소리(WASAPI 루프백)를 추가로 섞기 시작하면서 오디오 리샘플링/믹싱이
            // 실제로 매 프레임 동작하게 됐는데(이전엔 타임스탬프 버그로 사실상 멈춰
            // 있었다), 그 추가 연산 때문에 이 PC에서는 ffmpeg가 실시간보다 느리게
            // (speed 0.90~0.94x) 밀리는 경우가 로그로 확인됐다 — 이렇게 계속 뒤처지면
            // 송출 버퍼가 쌓이다가 결국 음성이 끊기는 형태로 터진다. ffmpeg 프로세스의
            // 스케줄링 우선순위를 높여서, 같은 PC에서 돌아가는 다른 프로그램(브라우저 등)
            // 때문에 CPU 타임슬라이스를 못 받아 밀리는 상황을 줄인다. 우선순위 설정은
            // 일부 환경(권한 제한 등)에서 실패할 수 있으므로 실패해도 방송 자체는
            // 계속 진행한다.
            // High로도 복제 프레임(dup)이 방송 내내 계속 쌓이는 경우가 있어(실시간
            // 캡처+인코딩이 다른 프로그램에 CPU를 계속 뺏기는 상황), 먼저 RealTime을
            // 시도하고 그게 막히면(권한 등) High로 물러난다. RealTime은 시스템 전체
            // 응답성에 영향을 줄 수 있는 설정이지만, ffmpeg는 짧게 끊어 도는 정상적인
            // 인코딩 루프라 폭주할 위험이 낮고, 실시간 방송처럼 지연이 곧 서비스 품질인
            // 상황에서는 흔히 쓰는 설정이다.
            try
            {
                process.PriorityClass = ProcessPriorityClass.RealTime;
                AppendLog("INFO", $"ffmpeg 프로세스 우선순위를 RealTime으로 올렸습니다. " +
                    $"논리 코어 수={Environment.ProcessorCount}");
            }
            catch (Exception realtimeEx)
            {
                try
                {
                    process.PriorityClass = ProcessPriorityClass.High;
                    AppendLog("INFO", $"RealTime 설정 실패({realtimeEx.Message})로 High로 대신 올렸습니다. " +
                        $"논리 코어 수={Environment.ProcessorCount}");
                }
                catch (Exception highEx)
                {
                    // 우선순위를 못 올려도 치명적이지 않다 — 기본 우선순위로 계속 진행.
                    // 다만 복제 프레임이 계속 쌓이는 원인 중 하나일 수 있으므로 로그에는 남긴다.
                    AppendLog("INFO", $"ffmpeg 프로세스 우선순위 상향에 실패했습니다: {highEx.Message}. " +
                        $"논리 코어 수={Environment.ProcessorCount}");
                }
            }

            lock (_sync)
                _process = process;

            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            await Task.Delay(1400);
            if (process.HasExited)
            {
                string detail = RecentErrorSummary();
                CleanupProcess(process);
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                    ? "방송 서버 연결에 실패했습니다. 서버 주소와 스트림 키를 확인해 주세요."
                    : detail);
            }

            if (recordingFilePath is not null)
                await StartIndependentRecordingAsync(options, recordingFilePath);

            bool lowLatencyReady = await TryStartLowLatencyRelayAsync(options);

            lock (_sync)
                _starting = false;
            string captureLabel = useDesktopDuplication ? "GPU 캡처" : "호환 캡처";
            string encoderLabel = useHardwareEncoder ? "GPU 인코딩" : "CPU 인코딩";
            string latencyLabel = lowLatencyReady ? "WebRTC 실시간" : "HLS 안정 모드";
            string recordingLabel = recordingFilePath is not null ? " · 녹화 중" : string.Empty;
            StatusReceived?.Invoke($"최대 {options.OutputHeight}p · {options.FrameRate}fps · {videoBitrate / 1000.0:0.#}Mbps · {captureLabel}/{encoderLabel} · {latencyLabel}{recordingLabel}");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            CleanupProcess(process);
            throw new InvalidOperationException("FFmpeg를 실행할 수 없습니다. ffmpeg.exe 파일을 확인해 주세요.", ex);
        }
    }

    private async Task<bool> IsNvencAvailableAsync()
    {
        if (_nvencAvailable is bool cached)
            return cached;

        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveFfmpegPath(),
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        Add(startInfo,
            "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", "color=size=64x64:rate=1",
            "-frames:v", "1",
            "-c:v", "h264_nvenc",
            "-preset", "p5",
            "-f", "null", "NUL");

        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                _nvencAvailable = false;
                return false;
            }

            Task<string> errorRead = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            await errorRead;
            bool available = process.ExitCode == 0;
            _nvencAvailable = available;
            return available;
        }
        catch
        {
            _nvencAvailable = false;
            return false;
        }
    }

    private async Task<bool> IsDesktopDuplicationAvailableAsync(int outputIndex)
    {
        if (_desktopDuplicationAvailability.TryGetValue(outputIndex, out bool cached))
            return cached;

        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveFfmpegPath(),
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        Add(startInfo,
            "-hide_banner", "-loglevel", "error",
            "-filter_complex", $"ddagrab=output_idx={outputIndex}:framerate=1,hwdownload,format=bgra",
            "-frames:v", "1",
            "-f", "null", "NUL");

        bool available = false;
        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is not null)
            {
                Task<string> errorRead = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }

                await errorRead;
                available = process.ExitCode == 0;
            }
        }
        catch
        {
            available = false;
        }

        _desktopDuplicationAvailability[outputIndex] = available;
        return available;
    }

    public async Task StopAsync()
    {
        Process? process;
        Process? lowLatencyProcess;
        Process? recordingProcess;
        lock (_sync)
        {
            process = _process;
            lowLatencyProcess = _lowLatencyProcess;
            recordingProcess = _recordingProcess;
            _stopping = true;
        }

        if (process is null && lowLatencyProcess is null && recordingProcess is null)
            return;

        if (lowLatencyProcess is not null)
            await StopProcessAsync(lowLatencyProcess, CleanupLowLatencyProcess);

        if (recordingProcess is not null)
            await StopRecordingInternalAsync();

        if (process is not null)
            await StopProcessAsync(process, CleanupProcess);
    }

    // 방송 중일 때만 녹화를 시작할 수 있다 — 녹화는 "방금 이 PC가 방송 서버에 올린
    // 바로 그 스트림"을 되읽는 방식(RTMP pull)이라, 서버에 스트림이 이미 올라가
    // 있어야 한다. 체크박스 하나로 방송 시작과 동시에 녹화를 켜고 끄던 예전 방식은
    // "녹화 체크를 풀어도 실제로는 멈추지 않는다"는 문제가 있었다 — 그 체크박스는
    // StartAsync가 시작될 때 딱 한 번만 읽혔을 뿐, 방송 도중 값이 바뀌는 걸 지켜보는
    // 코드가 전혀 없었기 때문이다. 이 메서드는 그 대신 방송 도중 언제든 호출해
    // 즉시 녹화를 시작할 수 있게 한다.
    public async Task<string?> StartRecordingAsync(string recordingFolderPath)
    {
        if (!IsStreaming)
            throw new InvalidOperationException("방송 중일 때만 녹화를 시작할 수 있습니다.");

        if (string.IsNullOrWhiteSpace(recordingFolderPath))
            throw new InvalidOperationException("녹화 폴더를 먼저 선택해 주세요.");

        bool alreadyRecording;
        StreamingOptions? currentOptions;
        lock (_sync)
        {
            alreadyRecording = _recordingProcess is not null;
            currentOptions = _currentOptions;
        }

        if (alreadyRecording)
            return CurrentRecordingFilePath;

        if (currentOptions is null)
            throw new InvalidOperationException("방송 설정을 확인하지 못해 녹화를 시작할 수 없습니다.");

        try
        {
            Directory.CreateDirectory(recordingFolderPath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"녹화 폴더를 만들 수 없습니다: {ex.Message}", ex);
        }

        string fileName = $"stockcaster-{DateTime.Now:yyyyMMdd-HHmmss}.ts";
        string recordingFilePath = Path.Combine(recordingFolderPath, fileName);

        bool started = await StartIndependentRecordingAsync(currentOptions, recordingFilePath);
        if (!started)
            return null;

        CurrentRecordingFilePath = recordingFilePath;
        return recordingFilePath;
    }

    // 녹화만 멈춘다(방송은 계속 진행). 멈추면서 .ts를 바로 mp4로 변환하고, 그
    // 결과(또는 변환 실패 시 원본 .ts) 경로를 돌려준다. 녹화 중이 아니면 null.
    public Task<string?> StopRecordingAsync() => StopRecordingInternalAsync();

    // StopAsync(전체 방송 종료)와 StopRecordingAsync(녹화만 종료) 양쪽에서 공유하는
    // 실제 정지+변환 로직. 녹화용 보조 프로세스를 멈추고, 그 결과물(.ts)을 업로드
    // 가능한 mp4로 재인코딩 없이 변환한 뒤 CurrentRecordingFilePath를 갱신한다.
    private async Task<string?> StopRecordingInternalAsync()
    {
        Process? recordingProcess;
        string? recordingTsPath;
        lock (_sync)
        {
            recordingProcess = _recordingProcess;
            recordingTsPath = _currentRecordingFilePath;
        }

        if (recordingProcess is null)
            return null;

        lock (_sync)
            _stoppingRecordingOnly = true;
        try
        {
            await StopProcessAsync(recordingProcess, CleanupRecordingProcess);
        }
        finally
        {
            lock (_sync)
                _stoppingRecordingOnly = false;
        }

        if (recordingTsPath is null)
            return null;

        string? convertedPath = await ConvertRecordingToMp4Async(recordingTsPath);
        string finalPath = convertedPath ?? recordingTsPath;
        CurrentRecordingFilePath = finalPath;
        return finalPath;
    }

    private static async Task StopProcessAsync(Process process, Action<Process> cleanup)
    {
        try
        {
            if (!process.HasExited)
            {
                await process.StandardInput.WriteLineAsync("q");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
        }
        catch
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        finally
        {
            cleanup(process);
        }
    }

    private async Task<bool> TryStartLowLatencyRelayAsync(StreamingOptions options)
    {
        string streamName = options.StreamKey.Split('?', 2)[0].Trim().Trim('/');
        if (string.IsNullOrWhiteSpace(streamName) ||
            !Uri.TryCreate(options.ServerUrl.Trim().TrimEnd('/'), UriKind.Absolute, out Uri? serverUri))
            return false;

        string baseUrl = options.ServerUrl.Trim().TrimEnd('/');
        string inputUrl = $"{baseUrl}/{streamName}";
        string host = serverUri.HostNameType == UriHostNameType.IPv6
            ? $"[{serverUri.Host}]"
            : serverUri.Host;
        // 서버가 방송자 PC와 같은 컴퓨터에 있을 때만 통하던 "루프백 IP 허용" 방식 대신,
        // 원격 서버에서도 항상 통하도록 RTMP 게시 때 쓰는 것과 같은 stockcaster 계정으로
        // WHIP에 인증한다. "Authorization: Bearer user:pass" 헤더는 실제로 MediaMTX가
        // 401로 거부하는 것을 확인했고(직접 curl 테스트), "Authorization: Basic ..."
        // 헤더는 정상 통과하는 것도 확인했다. ffmpeg의 whip 먹서에는 Basic 인증 전용
        // 옵션이 없지만, URL에 "http://user:pass@host/..." 형태로 자격증명을 넣으면
        // ffmpeg의 http 프로토콜 계층이 자동으로 Basic 인증 헤더를 붙여 보낸다.
        string? relayUser = ExtractQueryValue(options.StreamKey, "user");
        string? relayPass = ExtractQueryValue(options.StreamKey, "pass");
        string outputUrl = !string.IsNullOrEmpty(relayUser) && !string.IsNullOrEmpty(relayPass)
            ? $"http://{Uri.EscapeDataString(relayUser)}:{Uri.EscapeDataString(relayPass)}@{host}:8889/live-webrtc/whip"
            : $"http://{host}:8889/live-webrtc/whip";

        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveFfmpegPath(),
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8
        };
        Add(startInfo,
            "-hide_banner", "-loglevel", "warning",
            "-flags", "low_delay",
            // RTMP readers can begin between keyframes. Give FFmpeg enough
            // probe data to receive the H.264 sequence header and dimensions;
            // otherwise WHIP fails with "dimensions not set" and the viewer
            // falls back to multi-second HLS playback.
            "-probesize", "5M",
            "-analyzeduration", "5000000",
            "-i", inputUrl,
            "-map", "0:v:0",
            "-map", "0:a:0?",
            // "-c:v copy"로 RTMP의 H.264를 그대로 넘기면 WHIP 먹서가 SDP용
            // profile-level-id를 만들 extradata(SPS/PPS)를 제때 못 얻어서
            // "Failed to read message"/연결 리셋으로 계속 실패하는 것을 확인했다.
            // NVENC로 짧게 재인코딩하면 먹서가 항상 유효한 파라미터를 갖게 되어
            // 안정적으로 협상된다. 메인 인코딩과 별개 GPU 세션이라 부담은 작다.
            "-c:v", "h264_nvenc",
            "-preset", "p1",
            "-tune", "ll",
            "-profile:v", "baseline",
            "-g", "60",
            "-b:v", "6000k",
            "-c:a", "libopus",
            "-b:a", "128k",
            "-ar", "48000",
            "-ac", "2",
            "-f", "whip",
            "-handshake_timeout", "5000",
            outputUrl);

        _relayErrors.Clear();

        for (int attempt = 0; attempt < 5; attempt++)
        {
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.ErrorDataReceived += LowLatencyProcess_ErrorDataReceived;
            process.Exited += LowLatencyProcess_Exited;

            try
            {
                if (!process.Start())
                {
                    CleanupLowLatencyProcess(process);
                    continue;
                }

                lock (_sync)
                    _lowLatencyProcess = process;
                process.BeginErrorReadLine();
                process.BeginOutputReadLine();

                await Task.Delay(1200);
                if (!process.HasExited)
                    return true;

                CleanupLowLatencyProcess(process);
            }
            catch
            {
                CleanupLowLatencyProcess(process);
            }

            if (attempt < 4)
                await Task.Delay(600);
        }

        StatusReceived?.Invoke("저지연 연결을 시작하지 못해 HLS 안정 모드로 송출합니다.");
        return false;
    }

    // 녹화는 방송용 메인 ffmpeg와 완전히 독립된 두 번째 화면/오디오 캡처+인코딩으로
    // 처리한다. 예전에는 이미 서버에 올라간 스트림을 다시 읽어오는 방식(RTMP pull)을
    // 썼는데, 유튜브처럼 publish된 스트림을 다시 읽어가는 것 자체를 막는 플랫폼에서는
    // "Server response validating failed" 에러로 전혀 동작하지 않았다(실제 로그로
    // 확인 — 유튜브·트위치·페이스북 등 대부분의 공개 플랫폼이 이렇고, 직접 운영하는
    // RTMP 서버에서만 되던 것이었다). 그래서 어떤 방송 서버로 송출하든 항상 되도록,
    // 방송 서버를 전혀 거치지 않고 화면/오디오를 처음부터 다시 독립적으로 캡처해서
    // 바로 로컬 파일에 쓴다.
    //
    // 중요: 방송용 메인 ffmpeg의 출력 쪽은 여기서 전혀 건드리지 않는다(tee 등으로
    // 나눠 보내지 않음). StartAsync 위쪽 주석에 적힌 대로, 예전에 h264_nvenc + tee로
    // 방송 인코딩 결과를 로컬 파일에도 동시에 내보내려다가 방송 서버 연결 자체가
    // 끊기는 문제(Error -10053)를 실제로 겪었던 적이 있다 — 이 방식은 그 문제를
    // 되풀이할 여지가 구조적으로 없다(완전히 별개의 ffmpeg 프로세스, 별개의 캡처,
    // 별개의 인코더이므로 이 프로세스가 어떤 이유로 실패/종료되어도 메인 방송
    // 프로세스에는 전혀 영향이 없다). 대신 화면 캡처와 인코딩을 한 번 더 하므로 PC
    // 부하가 그만큼 늘어난다.
    private async Task<bool> StartIndependentRecordingAsync(StreamingOptions options, string recordingFilePath)
    {
        CaptureRegion? effectiveRegion = options.SourceWindow.Region
            ?? WindowCaptureService.GetWindowRegion(options.SourceWindow.Handle);
        if (effectiveRegion is null)
        {
            AppendLog("INFO", "녹화를 시작하지 못했습니다: 선택한 차트 창의 화면 위치를 확인할 수 없습니다.");
            StatusReceived?.Invoke("녹화를 시작하지 못해 녹화 없이 방송만 진행합니다.");
            return false;
        }

        int outputWidth = options.OutputHeight == 1080 ? 1920 : 1280;
        int videoBitrate = options.OutputHeight == 1080
            ? (options.FrameRate == 60 ? 14000 : 11000)
            : (options.FrameRate == 60 ? 7500 : 5000);

        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveFfmpegPath(),
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8
        };

        Add(startInfo, "-hide_banner", "-loglevel", "warning");

        Add(startInfo,
            "-thread_queue_size", "1024",
            "-f", "gdigrab",
            "-framerate", options.FrameRate.ToString(CultureInfo.InvariantCulture),
            "-draw_mouse", "1",
            "-use_wallclock_as_timestamps", "1",
            "-offset_x", effectiveRegion.X.ToString(CultureInfo.InvariantCulture),
            "-offset_y", effectiveRegion.Y.ToString(CultureInfo.InvariantCulture),
            "-video_size", $"{effectiveRegion.Width}x{effectiveRegion.Height}",
            "-i", "desktop");

        int micInputIndex = -1;
        int secondaryInputIndex = -1;
        int nextInputIndex = 1;

        // 방송용과 동일하게 — 마이크가 켜져 있으면 이 앱(AudioMixer)에서
        // 캡처하고, PC 소리가 켜져 있을 때만 루프백도 함께 섞는다. 녹화 전용으로 새 인스턴스를
        // 띄우므로 방송용 AudioMixer/SystemAudioCapture와는 서로 간섭하지 않는다.
        _recordingAudioMixer?.Dispose();
        _recordingAudioMixer = null;
        _recordingSystemAudioCapture?.Dispose();
        _recordingSystemAudioCapture = null;

        bool wantsMic = options.AudioDevice is not null;
        bool wantsSystemAudio = options.CaptureSystemAudio;

        if (wantsMic)
        {
            try
            {
                // 방송용 믹서(기본 마이크 게인 1.6배)와 별개로, 녹화본에서 "마이크를
                // 조금 더 키워달라"는 요청에 따라 녹화용 인스턴스에만 더 높은 마이크
                // 게인(2.2배)을 준다 — 방송 쪽 AudioMixer는 전혀 다른 인스턴스라
                // 이 변경의 영향을 받지 않는다.
                //
                // 방송과 녹화 모두 마이크를 WASAPI 공유 모드로 캡처한다.
                // 같은 장치를 두 캡처 인스턴스가 열어도 공유 모드로 동작하며,
                // dshow를 동시에 여는 조합에서 발생하던 잡음을 피한다.
                var mixer = new AudioMixer(micGain: 2.2f);
                mixer.MixerWarning += warning => AppendLog("INFO", $"[녹화] {warning}");
                mixer.Start(options.AudioDevice!.DisplayName, captureSystemAudio: wantsSystemAudio);
                _recordingAudioMixer = mixer;
            }
            catch (Exception ex)
            {
                AppendLog("INFO", $"[녹화] 마이크 사전 믹싱(WASAPI)을 시작하지 못해 대체 방식으로 전환합니다: {ex.Message}");
            }
        }

        if (_recordingAudioMixer is not null)
        {
            Add(startInfo,
                "-thread_queue_size", "1024",
                "-f", _recordingAudioMixer.FfmpegSampleFormat,
                "-ar", _recordingAudioMixer.SampleRate.ToString(CultureInfo.InvariantCulture),
                "-ac", _recordingAudioMixer.Channels.ToString(CultureInfo.InvariantCulture),
                "-i", _recordingAudioMixer.PipePath);
            micInputIndex = nextInputIndex++;
        }
        else if (wantsMic)
        {
            // AudioMixer(WASAPI) 자체를 시작하지 못했을 때만 쓰는 최후의 대체 경로.
            // 이 경우는 지지직 잡음의 원인이었던 dshow 동시 열기 위험이 있지만, 아예
            // 마이크 소리가 없는 것보다는 나으므로 남겨둔다.
            Add(startInfo,
                "-thread_queue_size", "1024",
                "-f", "dshow",
                "-rtbufsize", "200M");
            Add(startInfo, "-use_wallclock_as_timestamps", "1");
            Add(startInfo, "-i", $"audio={options.AudioDevice!.DevicePath}");
            micInputIndex = nextInputIndex++;
        }

        // PC 소리는 위에서 AudioMixer가 이미 같이 캡처했다면(마이크+PC 소리를 함께
        // 믹서에 태운 경우) 따로 또 캡처할 필요가 없다 — 마이크가 없거나 믹서가
        // 실패했을 때만 별도의 SystemAudioCapture(WASAPI 루프백)를 쓴다.
        if (wantsSystemAudio && _recordingAudioMixer is null)
        {
            try
            {
                var capture = new SystemAudioCapture();
                capture.Start();
                _recordingSystemAudioCapture = capture;
            }
            catch (Exception ex)
            {
                AppendLog("INFO", $"[녹화] PC 소리 캡처(WASAPI 루프백)를 시작하지 못해 건너뜁니다: {ex.Message}");
            }
        }

        if (_recordingSystemAudioCapture is not null)
        {
            Add(startInfo,
                "-thread_queue_size", "1024",
                "-f", _recordingSystemAudioCapture.FfmpegSampleFormat,
                "-ar", _recordingSystemAudioCapture.SampleRate.ToString(CultureInfo.InvariantCulture),
                "-ac", _recordingSystemAudioCapture.Channels.ToString(CultureInfo.InvariantCulture),
                "-i", _recordingSystemAudioCapture.PipePath);
            secondaryInputIndex = nextInputIndex++;
        }

        int singleAudioInputIndex = micInputIndex >= 0 ? micInputIndex : secondaryInputIndex;

        IReadOnlyList<PrivacyMask> validMasks = options.PrivacyMasks.Where(IsValidPrivacyMask).ToList();
        if (validMasks.Count > 0)
        {
            string videoFilter = BuildVideoFilterComplex("[0:v]", validMasks, outputWidth, options.OutputHeight);
            Add(startInfo, "-filter_complex", videoFilter, "-map", "[video_out]");
            if (singleAudioInputIndex >= 0)
                Add(startInfo, "-map", $"{singleAudioInputIndex}:a:0");
        }
        else
        {
            Add(startInfo, "-vf", BuildScaleFilter(outputWidth, options.OutputHeight));
        }

        // 방송용 인코더(h264_nvenc)와는 별도로 CPU 인코더(libx264)를 쓴다 — GPU 인코딩
        // 세션을 하나 더 띄워 방송 쪽 NVENC 자원과 경합할 가능성을 아예 없애기
        // 위해서다. 녹화는 실시간 지연이 중요하지 않으므로 veryfast 정도면 충분하다.
        Add(startInfo,
            "-c:v", "libx264",
            "-preset", "veryfast",
            "-b:v", $"{videoBitrate}k",
            "-maxrate", $"{videoBitrate}k",
            "-bufsize", $"{videoBitrate * 2}k",
            "-pix_fmt", "yuv420p",
            "-r", options.FrameRate.ToString(CultureInfo.InvariantCulture),
            "-fps_mode", "cfr",
            "-g", options.FrameRate.ToString(CultureInfo.InvariantCulture));

        if (singleAudioInputIndex >= 0)
        {
            // 마이크 단독 녹화에서 loudnorm이 낮은 마이크 잡음 바닥까지 키우는 것을 막기 위해
            // 녹화에는 음량 정규화를 적용하지 않는다. 캡처된 레벨과 믹서 게인을 그대로 유지한다.
            Add(startInfo, "-c:a", "aac", "-b:a", "160k", "-ar", "44100", "-ac", "2");
        }
        else
            Add(startInfo, "-an");

        // 방송 중 끊겨도 파일이 깨지지 않는 .ts(MPEG-TS)로 먼저 쓰고, 녹화를 멈출 때
        // ConvertRecordingToMp4Async가 재인코딩 없이 mp4로 바꾼다(기존과 동일한 흐름).
        Add(startInfo, "-f", "mpegts", recordingFilePath);

        _recordingErrors.Clear();

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.ErrorDataReceived += RecordingProcess_ErrorDataReceived;
        process.Exited += RecordingProcess_Exited;

        try
        {
            if (!process.Start())
            {
                CleanupRecordingProcess(process);
                AppendLog("INFO", "녹화용 독립 캡처 ffmpeg를 시작하지 못했습니다.");
                StatusReceived?.Invoke("녹화를 시작하지 못해 녹화 없이 방송만 진행합니다.");
                return false;
            }

            lock (_sync)
                _recordingProcess = process;
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            await Task.Delay(1200);
            if (process.HasExited)
            {
                AppendLog("INFO", $"녹화용 독립 캡처 ffmpeg가 바로 종료되었습니다. 요약: {RecentRecordingErrorSummary()}");
                StatusReceived?.Invoke("녹화를 시작하지 못해 녹화 없이 방송만 진행합니다.");
                CleanupRecordingProcess(process);
                return false;
            }

            AppendLog("INFO", $"녹화용 독립 캡처 ffmpeg 시작 -> {recordingFilePath}");
            return true;
        }
        catch (Exception ex)
        {
            AppendLog("INFO", $"녹화용 독립 캡처 ffmpeg를 시작하지 못했습니다: {ex.Message}");
            StatusReceived?.Invoke("녹화를 시작하지 못해 녹화 없이 방송만 진행합니다.");
            CleanupRecordingProcess(process);
            return false;
        }
    }

    private string RecentRecordingErrorSummary()
    {
        string[] lines;
        lock (_sync)
            lines = _recordingErrors.ToArray();

        string? useful = lines.LastOrDefault(line =>
            line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("invalid", StringComparison.OrdinalIgnoreCase));

        return Redact(useful ?? lines.LastOrDefault() ?? string.Empty);
    }

    // 방송 중에는 끊겨도 파일이 깨지지 않는 .ts(MPEG-TS)로 녹화해 두고, 방송이 끝난
    // 뒤에만 유튜브 등 업로드 사이트가 요구하는 mp4/webm/mov/m4v 중 mp4로 변환한다.
    // 영상/오디오를 다시 인코딩하지 않고 컨테이너만 바꿔 담으므로(-c copy) 화질 손실이
    // 없고, 수 GB 파일도 보통 수 초~수십 초면 끝난다. 변환에 성공하면 원본 .ts는 지워
    // 용량을 아끼고, 실패해도 .ts 파일은 그대로 남겨 녹화 내용을 잃지 않는다.
    private async Task<string?> ConvertRecordingToMp4Async(string tsPath)
    {
        if (!File.Exists(tsPath))
            return null;

        string mp4Path = Path.ChangeExtension(tsPath, ".mp4");

        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveFfmpegPath(),
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8
        };
        Add(startInfo,
            "-hide_banner", "-loglevel", "warning", "-y",
            "-i", tsPath,
            "-c", "copy",
            "-movflags", "+faststart",
            mp4Path);

        AppendLog("INFO", $"녹화 파일을 mp4로 변환 중 -> {mp4Path}");
        StatusReceived?.Invoke("녹화 파일을 업로드 가능한 mp4로 변환하는 중입니다...");

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                return null;

            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            process.StandardInput.Close();

            // 스트림 복사(재인코딩 없음)라 보통 금방 끝나지만, 아주 긴 녹화본을 대비해
            // 넉넉히 10분까지 기다린 뒤에도 안 끝나면 포기하고 .ts를 그대로 남긴다.
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                AppendLog("INFO", "mp4 변환이 시간 초과로 중단되었습니다. 원본 .ts 파일은 그대로 남아있습니다.");
                StatusReceived?.Invoke("mp4 변환이 너무 오래 걸려 중단했습니다. 녹화 파일(.ts)은 그대로 남아있습니다.");
                return null;
            }

            string stderr = await stderrTask;

            if (process.ExitCode == 0 && File.Exists(mp4Path) && new FileInfo(mp4Path).Length > 0)
            {
                try
                {
                    File.Delete(tsPath);
                }
                catch
                {
                    // mp4 변환 자체는 성공했으니 원본 삭제만 실패해도 치명적이지 않다.
                }

                AppendLog("INFO", $"mp4 변환 완료 -> {mp4Path}");
                StatusReceived?.Invoke("녹화 파일이 mp4로 변환되었습니다.");
                return mp4Path;
            }

            AppendLog("ERROR", $"mp4 변환 실패 (exit {process.ExitCode}): {Redact(stderr)}");
            StatusReceived?.Invoke("mp4 변환에 실패했습니다. 녹화 파일(.ts)은 그대로 남아있습니다.");
            return null;
        }
        catch (Exception ex)
        {
            AppendLog("ERROR", $"mp4 변환 중 예외 발생: {ex.Message}");
            StatusReceived?.Invoke("mp4 변환 중 오류가 발생했습니다. 녹화 파일(.ts)은 그대로 남아있습니다.");
            return null;
        }
    }

    private void RecordingProcess_ErrorDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data))
            return;

        string line = Redact(e.Data);
        lock (_sync)
        {
            _recordingErrors.Enqueue(line);
            while (_recordingErrors.Count > 12)
                _recordingErrors.Dequeue();
        }

        AppendLog("REC", line);
    }

    private void RecordingProcess_Exited(object? sender, EventArgs e)
    {
        if (sender is not Process process)
            return;

        bool expected;
        lock (_sync)
            expected = _stopping || _starting || _stoppingRecordingOnly;

        CleanupRecordingProcess(process);

        if (!expected && IsStreaming)
        {
            // 녹화 보조 프로세스가 혼자 죽어도 방송 자체는 계속 진행 중이다 — 사용자에게
            // 녹화만 끊겼다는 것을 알리고, 방송 상태 텍스트는 건드리지 않는다.
            AppendLog("INFO", "녹화용 보조 ffmpeg가 예기치 않게 종료되었습니다. 방송은 계속 진행됩니다.");
            StatusReceived?.Invoke("녹화가 중단되었습니다 (방송은 계속 진행 중입니다).");
        }
    }

    private void CleanupRecordingProcess(Process process)
    {
        AudioMixer? recordingAudioMixer;
        SystemAudioCapture? recordingSystemAudioCapture;
        lock (_sync)
        {
            if (ReferenceEquals(_recordingProcess, process))
                _recordingProcess = null;
            recordingAudioMixer = _recordingAudioMixer;
            _recordingAudioMixer = null;
            recordingSystemAudioCapture = _recordingSystemAudioCapture;
            _recordingSystemAudioCapture = null;
        }

        process.ErrorDataReceived -= RecordingProcess_ErrorDataReceived;
        process.Exited -= RecordingProcess_Exited;
        process.Dispose();
        recordingAudioMixer?.Dispose();
        recordingSystemAudioCapture?.Dispose();
    }

    private void Process_ErrorDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data))
            return;

        string line = Redact(e.Data);
        lock (_sync)
        {
            _recentErrors.Enqueue(line);
            while (_recentErrors.Count > 12)
                _recentErrors.Dequeue();
        }

        AppendLog("MAIN", line);

        Match frame = Regex.Match(line, @"frame=\s*(\d+).*?fps=\s*([\d.]+).*?bitrate=\s*([^\s]+)");
        if (frame.Success)
            StatusReceived?.Invoke($"프레임 {frame.Groups[1].Value} · {frame.Groups[2].Value}fps · {frame.Groups[3].Value}");
    }

    private void Process_Exited(object? sender, EventArgs e)
    {
        if (sender is not Process process)
            return;

        bool expected;
        lock (_sync)
            expected = _stopping || _starting;

        if (!expected)
        {
            string error = RecentErrorSummary();
            AppendLog("INFO", $"메인 FFmpeg 프로세스가 예기치 않게 종료됨 (exit code {process.ExitCode}). 요약: {error}");
            CleanupProcess(process);
            StreamEnded?.Invoke(string.IsNullOrWhiteSpace(error)
                ? "방송 서버와의 연결이 종료되었습니다."
                : error);
        }
    }

    private void LowLatencyProcess_ErrorDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data))
            return;

        string line = Redact(e.Data);
        lock (_sync)
        {
            _relayErrors.Enqueue(line);
            while (_relayErrors.Count > 12)
                _relayErrors.Dequeue();
        }

        AppendLog("RELAY", line);
    }

    private void LowLatencyProcess_Exited(object? sender, EventArgs e)
    {
        if (sender is not Process process)
            return;

        bool expected;
        lock (_sync)
            expected = _stopping || _starting;

        if (expected)
            return;

        CleanupLowLatencyProcess(process);
        if (IsStreaming)
            StatusReceived?.Invoke("WebRTC 저지연 경로가 종료되어 HLS 안정 모드로 자동 전환됩니다.");
    }

    private string RecentErrorSummary()
    {
        string[] lines;
        lock (_sync)
            lines = _recentErrors.ToArray();

        string? useful = lines.LastOrDefault(line =>
            line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("refused", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("invalid", StringComparison.OrdinalIgnoreCase));

        return Redact(useful ?? lines.LastOrDefault() ?? string.Empty);
    }

    private string Redact(string value)
    {
        return string.IsNullOrWhiteSpace(_secretToRedact)
            ? value
            : value.Replace(_secretToRedact, "••••", StringComparison.Ordinal);
    }

    private void CleanupProcess(Process process)
    {
        SystemAudioCapture? systemAudioCapture;
        AudioMixer? audioMixer;
        lock (_sync)
        {
            if (ReferenceEquals(_process, process))
            {
                _process = null;
                // CurrentRecordingFilePath는 여기서 비우지 않는다 — StopAsync가 이
                // 메서드 이후에 mp4 변환을 마치고 그 결과 경로를 넣어 두면, 다음
                // 방송을 시작할 때(StartAsync)에야 새 값으로 다시 채워진다.
                _currentOptions = null;
            }
            _starting = false;
            systemAudioCapture = _systemAudioCapture;
            _systemAudioCapture = null;
            audioMixer = _audioMixer;
            _audioMixer = null;
        }

        process.ErrorDataReceived -= Process_ErrorDataReceived;
        process.Exited -= Process_Exited;
        process.Dispose();
        systemAudioCapture?.Dispose();
        audioMixer?.Dispose();
    }

    private void CleanupLowLatencyProcess(Process process)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_lowLatencyProcess, process))
                _lowLatencyProcess = null;
        }

        process.ErrorDataReceived -= LowLatencyProcess_ErrorDataReceived;
        process.Exited -= LowLatencyProcess_Exited;
        process.Dispose();
    }

    private static string BuildOutputUrl(string serverUrl, string streamKey)
    {
        string baseUrl = serverUrl.Trim().TrimEnd('/');
        string key = streamKey.Trim().TrimStart('/');
        return string.IsNullOrWhiteSpace(key) ? baseUrl : $"{baseUrl}/{key}";
    }

    private static string? ExtractQueryValue(string value, string name)
    {
        int queryIndex = value.IndexOf('?');
        if (queryIndex < 0 || queryIndex == value.Length - 1)
            return null;

        foreach (string item in value[(queryIndex + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = item.Split('=', 2);
            if (pair.Length == 2 && string.Equals(pair[0], name, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(pair[1]);
        }

        return null;
    }

    internal static string BuildMosaicFilterComplex(IReadOnlyList<PrivacyMask> privacyMasks, int outputWidth, int outputHeight)
        => BuildVideoFilterComplex("[0:v]", privacyMasks, outputWidth, outputHeight);

    private static string BuildDesktopDuplicationFilter(
        DesktopCaptureTarget target,
        IReadOnlyList<PrivacyMask> privacyMasks,
        int outputWidth,
        int outputHeight,
        int frameRate)
    {
        string source = $"ddagrab=output_idx={target.OutputIndex}:framerate={frameRate}:draw_mouse=1," +
                        $"hwdownload,format=bgra,crop={target.Width}:{target.Height}:{target.X}:{target.Y}[desktop_capture]";
        return $"{source};{BuildVideoFilterComplex("[desktop_capture]", privacyMasks, outputWidth, outputHeight)}";
    }

    private static string BuildVideoFilterComplex(
        string sourceLabel,
        IReadOnlyList<PrivacyMask> privacyMasks,
        int outputWidth,
        int outputHeight)
    {
        var filters = new List<string>();
        string currentVideo = sourceLabel;
        int index = 0;

        foreach (PrivacyMask mask in privacyMasks.Where(IsValidPrivacyMask))
        {
            double x = Math.Clamp(mask.X, 0, 1);
            double y = Math.Clamp(mask.Y, 0, 1);
            double width = Math.Clamp(mask.Width, 0, 1 - x);
            double height = Math.Clamp(mask.Height, 0, 1 - y);

            string xValue = x.ToString("0.######", CultureInfo.InvariantCulture);
            string yValue = y.ToString("0.######", CultureInfo.InvariantCulture);
            string widthValue = width.ToString("0.######", CultureInfo.InvariantCulture);
            string heightValue = height.ToString("0.######", CultureInfo.InvariantCulture);
            string baseLabel = $"base_{index}";
            string cropLabel = $"crop_{index}";
            string mosaicLabel = $"mosaic_{index}";
            string maskedLabel = $"masked_{index}";

            filters.Add($"{currentVideo}split=2[{baseLabel}][{cropLabel}]");
            filters.Add($"[{cropLabel}]crop=w=iw*{widthValue}:h=ih*{heightValue}:x=iw*{xValue}:y=ih*{yValue}," +
                        $"pixelize=w=16:h=16:mode=avg[{mosaicLabel}]");
            filters.Add($"[{baseLabel}][{mosaicLabel}]overlay=x=main_w*{xValue}:y=main_h*{yValue}[{maskedLabel}]");
            currentVideo = $"[{maskedLabel}]";
            index++;
        }

        filters.Add($"{currentVideo}{BuildScaleFilter(outputWidth, outputHeight)}[video_out]");
        return string.Join(';', filters);
    }

    // 예전에는 여기에 마이크+PC 소리를 ffmpeg의 amix 필터로 직접 섞는
    // BuildAudioMixFilterComplex()가 있었다. 격리 테스트로 "실시간 입력 2개를
    // 영상 캡처/인코딩과 같은 필터그래프 안에서 amix로 섞는 것" 자체가 전체
    // 파이프라인을 1~2fps로 멈춰 세운다는 게 확인되어, 그 믹싱을 AudioMixer.cs
    // (NAudio, 이 앱 프로세스 안)로 옮겼다. ffmpeg는 이제 이미 섞인 단일 PCM
    // 입력만 받으므로 이 필터는 더 이상 쓰이지 않는다.

    private static string BuildScaleFilter(int outputWidth, int outputHeight) =>
        // Keep the complete captured frame without upscaling it into a fixed
        // 16:9 canvas. Upscaling a smaller chart window makes text look soft,
        // while a fixed pad adds unused bars around ultrawide captures. The
        // viewer adapts to the resulting even dimensions from the stream.
        $"scale=w='min(iw,{outputWidth})':h='min(ih,{outputHeight})':" +
        $"force_original_aspect_ratio=decrease:force_divisible_by=2:flags=lanczos,format=yuv420p";

    private static bool IsValidPrivacyMask(PrivacyMask mask) =>
        mask.X >= 0 && mask.Y >= 0 && mask.Width > 0 && mask.Height > 0 &&
        mask.X + mask.Width <= 1.000001 && mask.Y + mask.Height <= 1.000001;

    private static void Add(ProcessStartInfo startInfo, params string[] arguments)
    {
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);
    }
}
