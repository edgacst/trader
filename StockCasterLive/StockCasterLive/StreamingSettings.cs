using System.IO;
using System.Text.Json;

namespace StockCasterLive;

public sealed class StreamingSettings
{
    public int QualityProfileVersion { get; set; }
    public string ServerUrl { get; set; } = "rtmp://";
    public bool RememberStreamKey { get; set; }
    public string StreamKey { get; set; } = string.Empty;
    public int OutputHeight { get; set; } = 1080;
    public int FrameRate { get; set; } = 30;
    public bool UseMicrophone { get; set; } = true;
    public string AudioDeviceName { get; set; } = string.Empty;
    // PC 소리(음악 등)를 마이크와 함께 방송하기 위한 두 번째 오디오 장치 선택.
    // 기본값은 꺼짐(false) — 이 장치가 없는 PC에서는 목록에 아무것도 뜨지 않는다.
    public bool UseSecondaryAudio { get; set; }
    public string SecondaryAudioDeviceName { get; set; } = string.Empty;
    // 방송과 동시에 PC에 녹화 파일을 저장할지 여부. 폴더를 한 번도 고르지 않았으면
    // RecordingFolderPath는 비어 있고, 이때는 내 문서\StockCaster 녹화 를 기본값으로 쓴다.
    public bool EnableLocalRecording { get; set; }
    public string RecordingFolderPath { get; set; } = string.Empty;
    public CaptureRegion? SavedCaptureRegion { get; set; }
    public List<PrivacyMask> SavedPrivacyMasks { get; set; } = new();
    // 모자이크가 저장된 방송 화면(지정 영역 또는 특정 창)을 식별하는 키.
    // 창 선택 모드에서는 창 핸들이 재실행할 때마다 바뀌므로 프로세스명+제목으로 식별해
    // 다음 실행에서도 같은 창을 다시 고르면 모자이크가 자동으로 복원되게 한다.
    public string SavedMaskSourceKey { get; set; } = string.Empty;

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EdgarCST", "EdgarCasterLive", "settings.json");

    // 프로그램 이름을 StockCasterLive → EdgarCasterLive로 바꾸면서 설정 저장 폴더
    // 이름도 같이 바뀐다. 아무 조치 없이 이름만 바꾸면, 이미 저장해 둔 방송 서버
    // 주소/스트림 키/녹화 폴더/모자이크 위치 등이 새 폴더에는 없어서 전부 기본값으로
    // "리셋된 것처럼" 보이게 된다. 그래서 새 경로에 설정 파일이 아직 없고 예전
    // 경로에는 있으면, 처음 실행될 때 딱 한 번 그대로 복사해 옮겨 온다.
    private static string LegacySettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EdgarCST", "StockCasterLive", "settings.json");

    public static StreamingSettings Load()
    {
        try
        {
            MigrateLegacySettingsIfNeeded();

            if (!File.Exists(SettingsPath))
                return new StreamingSettings();

            return JsonSerializer.Deserialize<StreamingSettings>(File.ReadAllText(SettingsPath))
                   ?? new StreamingSettings();
        }
        catch
        {
            return new StreamingSettings();
        }
    }

    private static void MigrateLegacySettingsIfNeeded()
    {
        try
        {
            if (File.Exists(SettingsPath) || !File.Exists(LegacySettingsPath))
                return;

            string? directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            File.Copy(LegacySettingsPath, SettingsPath);
        }
        catch
        {
            // 옛 설정을 옮기지 못해도 치명적이지 않다 — 새 설정으로 시작할 뿐이다.
        }
    }

    public void Save()
    {
        string? directory = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }
}
