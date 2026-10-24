using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace StockCasterLive;

/// <summary>
/// 화면에 보여줄 마이크/PC 소리 음량 막대만을 위한 독립적인 오디오 레벨 측정기.
/// 방송 인코딩에 쓰이는 AudioMixer/SystemAudioCapture/ffmpeg dshow 입력과는 완전히
/// 별개의 WASAPI 캡처 인스턴스를 연다 — WASAPI 공유 모드는 같은 장치를 여러
/// 클라이언트가 동시에 열어도 서로 간섭하지 않으므로, 방송 중이든 아니든(마이크
/// 테스트 등) 언제나 켜 둘 수 있다. 캡처한 오디오는 순간 최대 진폭(피크)만 계산하고
/// 바로 버린다 — 어디에도 저장/전송하지 않는다.
/// </summary>
public sealed class AudioLevelMeter : IDisposable
{
    private readonly object _sync = new();
    private WasapiCapture? _micCapture;
    private WasapiLoopbackCapture? _systemCapture;
    private string? _currentMicName;
    private volatile float _micPeak;
    private volatile float _systemPeak;
    private bool _micReadable;
    private bool _systemReadable;
    private bool _disposed;

    /// <summary>
    /// 레벨 막대에 쓸 마이크 장치를 지정한다. null/빈 문자열이면 마이크 측정을 끈다.
    /// 이미 같은 장치가 켜져 있으면 아무 것도 하지 않는다. 장치를 열지 못해도(다른
    /// 프로그램 점유 등) 조용히 무시한다 — 레벨 막대는 보조 기능일 뿐 방송/녹화에는
    /// 영향이 없어야 한다.
    /// </summary>
    public void SetMicDevice(string? micDisplayName)
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            if (string.Equals(_currentMicName, micDisplayName, StringComparison.OrdinalIgnoreCase))
                return;

            StopMicLocked();

            if (string.IsNullOrWhiteSpace(micDisplayName))
                return;

            try
            {
                MMDevice? device = FindCaptureDevice(micDisplayName);
                if (device is null)
                    return;

                var capture = new WasapiCapture(device);
                _micReadable = capture.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat
                    && capture.WaveFormat.BitsPerSample == 32;
                capture.DataAvailable += OnMicData;
                capture.StartRecording();
                _micCapture = capture;
                _currentMicName = micDisplayName;
            }
            catch
            {
                // 측정기 시작 실패는 막대가 0으로 보이는 것으로 충분하다.
            }
        }
    }

    public void SetSystemAudioEnabled(bool enabled)
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            if (enabled == (_systemCapture is not null))
                return;

            if (!enabled)
            {
                StopSystemLocked();
                return;
            }

            try
            {
                var capture = new WasapiLoopbackCapture();
                _systemReadable = capture.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat
                    && capture.WaveFormat.BitsPerSample == 32;
                capture.DataAvailable += OnSystemData;
                capture.StartRecording();
                _systemCapture = capture;
            }
            catch
            {
            }
        }
    }

    // 마이크 막대만 이만큼 키워서 보여준다 — 실제 녹화/방송에 쓰는 AudioMixer의
    // 마이크 게인(1.6~2.2배)과 비슷한 체감이 들도록 맞춘 값이다. 사용자가 보통
    // 톤으로 말해도 막대가 절반 정도에서 멈춰 "마이크가 약한가?" 싶게 보인다는
    // 피드백이 있어서, 측정(피크) 자체를 키워 화면에서 더 또렷하게 움직이게
    // 한다 — 1.0을 넘는 값은 의미가 없으므로(이미 풀스케일) 클램프한다. PC 소리
    // 막대는 보통 음악 등으로 이미 잘 보이는 편이라 그대로 둔다.
    private const float MicDisplayGain = 1.8f;

    private void OnMicData(object? sender, WaveInEventArgs e)
    {
        if (!_micReadable)
            return;

        float peak = Math.Min(ComputePeak(e.Buffer, e.BytesRecorded) * MicDisplayGain, 1f);
        if (peak > _micPeak)
            _micPeak = peak;
    }

    private void OnSystemData(object? sender, WaveInEventArgs e)
    {
        if (!_systemReadable)
            return;

        float peak = ComputePeak(e.Buffer, e.BytesRecorded);
        if (peak > _systemPeak)
            _systemPeak = peak;
    }

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

    // UI 타이머가 주기적으로(예: 80ms마다) 호출한다. 호출될 때마다 지금까지의 최대
    // 피크를 돌려주고 스스로 약하게 감쇠시킨다 — 그래서 막대가 뚝 끊기지 않고
    // 자연스럽게 흘러내리듯 내려가며, 다음 순간의 새 피크가 그 위로 다시 덮어쓴다.
    public float ReadMicLevel()
    {
        float value = _micPeak;
        _micPeak *= 0.7f;
        return value;
    }

    public float ReadSystemLevel()
    {
        float value = _systemPeak;
        _systemPeak *= 0.7f;
        return value;
    }

    private void StopMicLocked()
    {
        if (_micCapture is null)
            return;

        _micCapture.DataAvailable -= OnMicData;
        try { _micCapture.StopRecording(); } catch { /* 이미 멈췄으면 무시 */ }
        _micCapture.Dispose();
        _micCapture = null;
        _currentMicName = null;
        _micPeak = 0f;
    }

    private void StopSystemLocked()
    {
        if (_systemCapture is null)
            return;

        _systemCapture.DataAvailable -= OnSystemData;
        try { _systemCapture.StopRecording(); } catch { /* 이미 멈췄으면 무시 */ }
        _systemCapture.Dispose();
        _systemCapture = null;
        _systemPeak = 0f;
    }

    private static MMDevice? FindCaptureDevice(string displayName)
    {
        var enumerator = new MMDeviceEnumerator();
        MMDeviceCollection devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);

        foreach (MMDevice device in devices)
        {
            if (string.Equals(device.FriendlyName, displayName, StringComparison.OrdinalIgnoreCase))
                return device;
        }

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
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            StopMicLocked();
            StopSystemLocked();
        }
    }
}
