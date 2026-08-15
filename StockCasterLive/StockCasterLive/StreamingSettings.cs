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
    public CaptureRegion? SavedCaptureRegion { get; set; }
    public List<PrivacyMask> SavedPrivacyMasks { get; set; } = new();

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EdgarCST", "StockCasterLive", "settings.json");

    public static StreamingSettings Load()
    {
        try
        {
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
