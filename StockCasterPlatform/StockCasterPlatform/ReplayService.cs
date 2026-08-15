using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StockCasterPlatform;

public sealed record ReplayItem(
    string Id,
    string Title,
    DateTimeOffset StartedAt,
    double DurationSeconds,
    bool IsPublished,
    string VideoUrl,
    string ThumbnailUrl);

public sealed record ReplayResult(bool Success, string? Error, ReplayItem? Replay)
{
    public static ReplayResult Fail(string error) => new(false, error, null);
    public static ReplayResult Ok(ReplayItem replay) => new(true, null, replay);
}

public sealed class ReplayService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SemaphoreSlim _metadataGate = new(1, 1);
    private readonly string _metadataPath;
    private readonly string _recordingsPath;
    private readonly ILogger<ReplayService> _logger;
    private Dictionary<string, ReplayMetadata> _metadata = new(StringComparer.OrdinalIgnoreCase);
    private bool _metadataLoaded;

    public ReplayService(
        IHttpClientFactory httpClientFactory,
        IWebHostEnvironment environment,
        ILogger<ReplayService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _metadataPath = Path.Combine(environment.ContentRootPath, "App_Data", "replay-metadata.json");
        _recordingsPath = Path.Combine(environment.ContentRootPath, "App_Data", "recordings", "live");
    }

    public async Task<IReadOnlyList<ReplayItem>> GetReplaysAsync(bool includePrivate, CancellationToken cancellationToken)
    {
        IReadOnlyList<RecordingSpan> spans = await GetRecordingSpansAsync(cancellationToken);
        await _metadataGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureMetadataLoadedCoreAsync(cancellationToken);
            bool changed = false;
            var items = new List<ReplayItem>(spans.Count);
            foreach (RecordingSpan span in spans.OrderByDescending(item => item.StartedAt))
            {
                string id = CreateId(span.StartedAt);
                if (!_metadata.TryGetValue(id, out ReplayMetadata? metadata))
                {
                    metadata = new ReplayMetadata(
                        id,
                        DefaultTitle(span.StartedAt),
                        true,
                        span.StartedAt,
                        DateTimeOffset.Now);
                    _metadata[id] = metadata;
                    changed = true;
                }

                if (includePrivate || metadata.IsPublished)
                    items.Add(ToItem(span, metadata));
            }

            if (changed)
                await SaveMetadataCoreAsync(cancellationToken);
            return items;
        }
        finally
        {
            _metadataGate.Release();
        }
    }

    public async Task<ReplayItem?> GetReplayAsync(string id, bool includePrivate, CancellationToken cancellationToken) =>
        (await GetReplaysAsync(includePrivate, cancellationToken))
            .FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public async Task<ReplayResult> UpdateAsync(
        string id,
        string title,
        bool isPublished,
        CancellationToken cancellationToken)
    {
        title = (title ?? string.Empty).Trim();
        if (title.Length is < 2 or > 80)
            return ReplayResult.Fail("다시보기 제목은 2~80자로 입력해 주세요.");

        ReplayItem? existing = await GetReplayAsync(id, true, cancellationToken);
        if (existing is null)
            return ReplayResult.Fail("다시보기 영상을 찾을 수 없습니다.");

        await _metadataGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureMetadataLoadedCoreAsync(cancellationToken);
            ReplayMetadata current = _metadata[id];
            ReplayMetadata updated = current with { Title = title, IsPublished = isPublished, UpdatedAt = DateTimeOffset.Now };
            _metadata[id] = updated;
            await SaveMetadataCoreAsync(cancellationToken);
            return ReplayResult.Ok(existing with { Title = updated.Title, IsPublished = updated.IsPublished });
        }
        finally
        {
            _metadataGate.Release();
        }
    }

    public async Task<ReplayResult> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        ReplayItem? existing = await GetReplayAsync(id, true, cancellationToken);
        if (existing is null)
            return ReplayResult.Fail("다시보기 영상을 찾을 수 없습니다.");

        HttpClient api = _httpClientFactory.CreateClient("MediaApi");
        string segmentStart = existing.StartedAt.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture);
        string query = $"/v3/recordings/deletesegment?path=live&start={Uri.EscapeDataString(segmentStart)}";
        using HttpResponseMessage response = await api.DeleteAsync(query, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string detail = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("녹화 삭제 실패: {StatusCode} {Detail}", response.StatusCode, detail);
            return ReplayResult.Fail("녹화 파일을 삭제하지 못했습니다.");
        }

        await _metadataGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureMetadataLoadedCoreAsync(cancellationToken);
            _metadata.Remove(id);
            await SaveMetadataCoreAsync(cancellationToken);
        }
        finally
        {
            _metadataGate.Release();
        }
        return ReplayResult.Ok(existing);
    }

    public Task<HttpResponseMessage> OpenVideoAsync(ReplayItem replay, CancellationToken cancellationToken)
    {
        HttpClient playback = _httpClientFactory.CreateClient("MediaPlayback");
        string start = Uri.EscapeDataString(replay.StartedAt.ToString("O", CultureInfo.InvariantCulture));
        string duration = replay.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        string url = $"/get?path=live&start={start}&duration={duration}";
        return playback.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    public string? GetLocalVideoPath(ReplayItem replay)
    {
        string fileName = replay.StartedAt.ToLocalTime()
            .ToString("yyyy-MM-dd_HH-mm-ss-ffffff", CultureInfo.InvariantCulture) + ".mp4";
        string path = Path.Combine(_recordingsPath, fileName);
        return File.Exists(path) ? path : null;
    }

    public static string BuildThumbnailSvg(ReplayItem replay)
    {
        string title = WebUtility.HtmlEncode(replay.Title);
        string date = WebUtility.HtmlEncode(replay.StartedAt.ToLocalTime().ToString("yyyy.MM.dd HH:mm"));
        return $$"""
            <svg xmlns="http://www.w3.org/2000/svg" width="800" height="450" viewBox="0 0 800 450">
              <defs>
                <linearGradient id="bg" x1="0" x2="1" y1="0" y2="1">
                  <stop offset="0" stop-color="#171930"/><stop offset="0.55" stop-color="#34245d"/><stop offset="1" stop-color="#843a7b"/>
                </linearGradient>
                <linearGradient id="line" x1="0" x2="1"><stop stop-color="#79f6d0"/><stop offset="1" stop-color="#8168ff"/></linearGradient>
              </defs>
              <rect width="800" height="450" rx="22" fill="url(#bg)"/>
              <path d="M0 340 C80 305 110 335 175 274 S295 245 350 280 460 194 525 222 650 125 800 156" fill="none" stroke="url(#line)" stroke-width="8" opacity=".9"/>
              <path d="M0 370 C105 318 145 370 220 314 S360 350 430 270 565 270 620 225 730 245 800 205" fill="none" stroke="#ec5cab" stroke-width="3" opacity=".45"/>
              <rect x="40" y="36" width="92" height="30" rx="15" fill="#ff4169"/><text x="86" y="56" text-anchor="middle" fill="white" font-family="Arial,sans-serif" font-size="14" font-weight="700">REPLAY</text>
              <text x="40" y="115" fill="white" font-family="Arial,sans-serif" font-size="31" font-weight="700">{{title}}</text>
              <text x="40" y="151" fill="#b8bdd4" font-family="Arial,sans-serif" font-size="18">{{date}} · StockCaster</text>
            </svg>
            """;
    }

    private async Task<IReadOnlyList<RecordingSpan>> GetRecordingSpansAsync(CancellationToken cancellationToken)
    {
        try
        {
            HttpClient playback = _httpClientFactory.CreateClient("MediaPlayback");
            using HttpResponseMessage response = await playback.GetAsync("/list?path=live", cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return [];
            response.EnsureSuccessStatusCode();

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return [];

            var spans = new List<RecordingSpan>();
            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                if (!element.TryGetProperty("start", out JsonElement startElement) ||
                    !DateTimeOffset.TryParse(startElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset start) ||
                    !element.TryGetProperty("duration", out JsonElement durationElement) ||
                    !durationElement.TryGetDouble(out double duration) || duration <= 0)
                    continue;
                spans.Add(new RecordingSpan(start, duration));
            }
            return spans;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogDebug("다시보기 목록 대기 중: {Message}", ex.Message);
            return [];
        }
    }

    private async Task EnsureMetadataLoadedCoreAsync(CancellationToken cancellationToken)
    {
        if (_metadataLoaded)
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(_metadataPath)!);
        if (File.Exists(_metadataPath))
        {
            try
            {
                string json = await File.ReadAllTextAsync(_metadataPath, cancellationToken);
                _metadata = JsonSerializer.Deserialize<Dictionary<string, ReplayMetadata>>(json, JsonOptions)
                    ?? new Dictionary<string, ReplayMetadata>(StringComparer.OrdinalIgnoreCase);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "다시보기 메타데이터를 읽지 못해 새로 시작합니다.");
            }
        }
        _metadataLoaded = true;
    }

    private async Task SaveMetadataCoreAsync(CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(_metadataPath)!;
        Directory.CreateDirectory(directory);
        string temporary = _metadataPath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(_metadata, JsonOptions), cancellationToken);
        File.Move(temporary, _metadataPath, true);
    }

    private static ReplayItem ToItem(RecordingSpan span, ReplayMetadata metadata) =>
        new(
            metadata.Id,
            metadata.Title,
            span.StartedAt,
            span.DurationSeconds,
            metadata.IsPublished,
            $"/api/replays/{metadata.Id}/video",
            $"/api/replays/{metadata.Id}/thumbnail.svg");

    private static string CreateId(DateTimeOffset startedAt)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"live|{startedAt:O}"));
        return Convert.ToHexString(hash.AsSpan(0, 9)).ToLowerInvariant();
    }

    private static string DefaultTitle(DateTimeOffset startedAt) =>
        $"{startedAt.ToLocalTime():MM월 dd일 HH:mm} 라이브 방송";

    private sealed record RecordingSpan(DateTimeOffset StartedAt, double DurationSeconds);
    private sealed record ReplayMetadata(
        string Id,
        string Title,
        bool IsPublished,
        DateTimeOffset StartedAt,
        DateTimeOffset UpdatedAt);
}
