using System.Text.Json;

namespace StockCasterPlatform;

public sealed record BroadcastInfo(
    string Title,
    string Description,
    string Notice,
    DateTimeOffset? ScheduledAt,
    DateTimeOffset UpdatedAt);

public sealed record BroadcastInfoResult(bool Success, string? Error, BroadcastInfo? Info)
{
    public static BroadcastInfoResult Ok(BroadcastInfo info) => new(true, null, info);
    public static BroadcastInfoResult Fail(string error) => new(false, error, null);
}

public sealed class BroadcastInfoService
{
    private const string SettingKey = "broadcast-info";
    private readonly MemberStore _members;
    private readonly ILogger<BroadcastInfoService> _logger;

    public BroadcastInfoService(MemberStore members, ILogger<BroadcastInfoService> logger)
    {
        _members = members;
        _logger = logger;
    }

    public async Task<BroadcastInfo> GetAsync(CancellationToken cancellationToken)
    {
        string? stored = await _members.GetSettingAsync(SettingKey, cancellationToken);
        if (!string.IsNullOrWhiteSpace(stored))
        {
            try
            {
                BroadcastInfo? info = JsonSerializer.Deserialize<BroadcastInfo>(stored);
                if (info is not null)
                    return info;
            }
            catch (JsonException exception)
            {
                _logger.LogWarning(exception, "저장된 방송 안내를 읽을 수 없어 기본값으로 복구합니다.");
            }
        }

        BroadcastInfo defaultInfo = DefaultInfo();
        await SaveAsync(defaultInfo, cancellationToken);
        return defaultInfo;
    }

    public async Task<BroadcastInfoResult> UpdateAsync(
        string title,
        string description,
        string notice,
        DateTimeOffset? scheduledAt,
        CancellationToken cancellationToken)
    {
        title = title.Trim();
        description = description.Trim();
        notice = notice.Trim();

        if (title.Length is < 2 or > 80)
            return BroadcastInfoResult.Fail("방송 제목은 2~80자로 입력해 주세요.");
        if (description.Length > 240)
            return BroadcastInfoResult.Fail("방송 설명은 240자 이하로 입력해 주세요.");
        if (notice.Length > 160)
            return BroadcastInfoResult.Fail("고정 공지는 160자 이하로 입력해 주세요.");
        if (scheduledAt is not null && scheduledAt.Value > DateTimeOffset.Now.AddYears(2))
            return BroadcastInfoResult.Fail("방송 예정 시간은 2년 이내로 설정해 주세요.");

        var info = new BroadcastInfo(title, description, notice, scheduledAt, DateTimeOffset.Now);
        await SaveAsync(info, cancellationToken);
        return BroadcastInfoResult.Ok(info);
    }

    private async Task SaveAsync(BroadcastInfo info, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(info);
        await _members.SetSettingAsync(SettingKey, json, cancellationToken);
    }

    private static BroadcastInfo DefaultInfo() => new(
        "실시간 증권 차트 방송",
        "실시간 차트 분석과 시장 흐름을 전달합니다.",
        "",
        null,
        DateTimeOffset.Now);
}
