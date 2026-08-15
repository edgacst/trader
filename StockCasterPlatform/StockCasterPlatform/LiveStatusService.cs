using System.Text.Json;

namespace StockCasterPlatform;

public sealed record LiveBroadcastStatus(
    bool IsLive,
    bool LowLatencyReady,
    int ViewerCount,
    string State,
    string? VideoCodec,
    string? AudioCodec,
    int? VideoWidth,
    int? VideoHeight,
    int? AudioSampleRate,
    int? AudioChannels,
    double? InboundBitrateKbps,
    long InboundBytes,
    long InboundFramesInError,
    DateTimeOffset? StartedAt,
    long UptimeSeconds,
    bool IsRecording,
    string Health,
    DateTimeOffset CheckedAt);

public sealed class LiveStatusService
{
    private readonly HttpClient _client;
    private readonly ILogger<LiveStatusService> _logger;
    private readonly object _sampleLock = new();
    private DateTimeOffset? _lastSampleAt;
    private DateTimeOffset? _lastStartedAt;
    private long _lastInboundBytes;
    private double? _lastBitrateKbps;

    public LiveStatusService(IHttpClientFactory httpClientFactory, ILogger<LiveStatusService> logger)
    {
        _client = httpClientFactory.CreateClient("MediaStatus");
        _logger = logger;
    }

    public async Task<LiveBroadcastStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await _client.GetAsync("/v3/paths/get/live", cancellationToken);
            if (!response.IsSuccessStatusCode)
                return Offline("waiting");

            await using Stream responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using JsonDocument document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
            JsonElement root = document.RootElement;
            bool online = GetBoolean(root, "online") || GetBoolean(root, "ready");
            LowLatencyStatus lowLatency = online
                ? await GetLowLatencyStatusAsync(cancellationToken)
                : new LowLatencyStatus(false, 0);
            int relayReader = lowLatency.Ready ? 1 : 0;
            int viewers = Math.Max(0, CountReaders(root) - relayReader) + lowLatency.ViewerCount;
            TrackDetails tracks = GetTrackDetails(root);
            long inboundBytes = GetInt64(root, "inboundBytes", "bytesReceived");
            long inboundErrors = GetInt64(root, "inboundFramesInError");
            DateTimeOffset? startedAt = GetDateTimeOffset(root, "onlineTime", "readyTime");
            DateTimeOffset now = DateTimeOffset.Now;
            double? bitrateKbps = UpdateBitrate(online, inboundBytes, startedAt, now);
            long uptimeSeconds = online && startedAt is not null
                ? Math.Max(0, (long)(now - startedAt.Value).TotalSeconds)
                : 0;

            string health = !online
                ? "offline"
                : tracks.VideoCodec is null || tracks.AudioCodec is null || inboundErrors > 0
                    ? "warning"
                    : bitrateKbps is null
                        ? "starting"
                        : bitrateKbps > 0 ? "good" : "warning";

            return new LiveBroadcastStatus(
                online,
                lowLatency.Ready,
                viewers,
                online ? "live" : "waiting",
                tracks.VideoCodec,
                tracks.AudioCodec,
                tracks.VideoWidth,
                tracks.VideoHeight,
                tracks.AudioSampleRate,
                tracks.AudioChannels,
                bitrateKbps,
                inboundBytes,
                inboundErrors,
                startedAt,
                uptimeSeconds,
                online,
                health,
                now);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogDebug("방송 상태 조회 대기 중: {Message}", ex.Message);
            return Offline("server-starting");
        }
    }

    private double? UpdateBitrate(bool online, long inboundBytes, DateTimeOffset? startedAt, DateTimeOffset now)
    {
        lock (_sampleLock)
        {
            if (!online)
            {
                _lastSampleAt = null;
                _lastStartedAt = null;
                _lastInboundBytes = 0;
                _lastBitrateKbps = null;
                return null;
            }

            if (_lastSampleAt is null || _lastStartedAt != startedAt || inboundBytes < _lastInboundBytes)
            {
                _lastSampleAt = now;
                _lastStartedAt = startedAt;
                _lastInboundBytes = inboundBytes;
                _lastBitrateKbps = null;
                return null;
            }

            double seconds = (now - _lastSampleAt.Value).TotalSeconds;
            if (seconds >= 0.75)
            {
                _lastBitrateKbps = Math.Round((inboundBytes - _lastInboundBytes) * 8d / seconds / 1000d, 1);
                _lastSampleAt = now;
                _lastInboundBytes = inboundBytes;
            }

            return _lastBitrateKbps;
        }
    }

    private static LiveBroadcastStatus Offline(string state) => new(
        false,
        false,
        0,
        state,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        0,
        0,
        null,
        0,
        false,
        "offline",
        DateTimeOffset.Now);

    private async Task<LowLatencyStatus> GetLowLatencyStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await _client.GetAsync("/v3/paths/get/live-webrtc", cancellationToken);
            if (!response.IsSuccessStatusCode)
                return new LowLatencyStatus(false, 0);

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            JsonElement root = document.RootElement;
            bool ready = GetBoolean(root, "online") || GetBoolean(root, "ready");
            return new LowLatencyStatus(ready, ready ? CountReaders(root) : 0);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogDebug("저지연 방송 상태 조회 대기 중: {Message}", ex.Message);
            return new LowLatencyStatus(false, 0);
        }
    }

    private static bool GetBoolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    private static long GetInt64(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (element.TryGetProperty(name, out JsonElement value) && value.TryGetInt64(out long result))
                return result;
        }

        return 0;
    }

    private static DateTimeOffset? GetDateTimeOffset(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (element.TryGetProperty(name, out JsonElement value) &&
                value.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(value.GetString(), out DateTimeOffset result))
                return result;
        }

        return null;
    }

    private static int CountReaders(JsonElement root)
    {
        if (root.TryGetProperty("readers", out JsonElement readers) && readers.ValueKind == JsonValueKind.Array)
            return readers.GetArrayLength();

        return 0;
    }

    private static TrackDetails GetTrackDetails(JsonElement root)
    {
        string? video = null;
        string? audio = null;
        int? videoWidth = null;
        int? videoHeight = null;
        int? audioSampleRate = null;
        int? audioChannels = null;

        if (root.TryGetProperty("tracks2", out JsonElement tracks2) && tracks2.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement track in tracks2.EnumerateArray())
            {
                if (!track.TryGetProperty("codec", out JsonElement codecElement) || codecElement.ValueKind != JsonValueKind.String)
                    continue;

                string codec = codecElement.GetString() ?? string.Empty;
                JsonElement props = track.TryGetProperty("codecProps", out JsonElement codecProps) ? codecProps : default;
                if (IsVideoCodec(codec))
                {
                    video ??= codec;
                    videoWidth ??= GetNullableInt32(props, "width");
                    videoHeight ??= GetNullableInt32(props, "height");
                }
                else
                {
                    audio ??= codec;
                    audioSampleRate ??= GetNullableInt32(props, "sampleRate");
                    audioChannels ??= GetNullableInt32(props, "channelCount");
                }
            }
        }

        if (root.TryGetProperty("tracks", out JsonElement tracks) && tracks.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement track in tracks.EnumerateArray())
            {
                if (track.ValueKind != JsonValueKind.String)
                    continue;

                string codec = track.GetString() ?? string.Empty;
                if (IsVideoCodec(codec))
                    video ??= codec;
                else
                    audio ??= codec;
            }
        }

        return new TrackDetails(video, audio, videoWidth, videoHeight, audioSampleRate, audioChannels);
    }

    private static int? GetNullableInt32(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out JsonElement value) &&
        value.TryGetInt32(out int result)
            ? result
            : null;

    private static bool IsVideoCodec(string codec) =>
        codec.Contains("H264", StringComparison.OrdinalIgnoreCase) ||
        codec.Contains("H265", StringComparison.OrdinalIgnoreCase) ||
        codec.Contains("VP", StringComparison.OrdinalIgnoreCase) ||
        codec.Contains("AV1", StringComparison.OrdinalIgnoreCase) ||
        codec.Contains("Video", StringComparison.OrdinalIgnoreCase) ||
        codec.Contains("JPEG", StringComparison.OrdinalIgnoreCase);

    private sealed record TrackDetails(
        string? VideoCodec,
        string? AudioCodec,
        int? VideoWidth,
        int? VideoHeight,
        int? AudioSampleRate,
        int? AudioChannels);

    private sealed record LowLatencyStatus(bool Ready, int ViewerCount);
}
