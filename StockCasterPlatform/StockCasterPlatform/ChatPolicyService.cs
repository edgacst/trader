using System.Text.Json;

namespace StockCasterPlatform;

public sealed record ChatPolicy(
    string PinnedNotice,
    int SlowModeSeconds,
    string[] BlockedWords,
    int BurstLimit,
    int BurstWindowSeconds,
    DateTimeOffset UpdatedAt);

public sealed record PublicChatPolicy(string PinnedNotice, int SlowModeSeconds);

public sealed record ChatPolicyResult(bool Success, string? Error, ChatPolicy? Policy)
{
    public static ChatPolicyResult Ok(ChatPolicy policy) => new(true, null, policy);
    public static ChatPolicyResult Fail(string error) => new(false, error, null);
}

public sealed class ChatPolicyService
{
    private const string SettingKey = "chat-policy";
    private static readonly int[] AllowedSlowModes = [0, 3, 5, 10, 30, 60];
    private readonly MemberStore _members;
    private readonly ILogger<ChatPolicyService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ChatPolicy? _current;

    public ChatPolicyService(MemberStore members, ILogger<ChatPolicyService> logger)
    {
        _members = members;
        _logger = logger;
    }

    public async Task<ChatPolicy> GetAsync(CancellationToken cancellationToken)
    {
        ChatPolicy? cached = Volatile.Read(ref _current);
        if (cached is not null)
            return cached;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_current is not null)
                return _current;

            string? stored = await _members.GetSettingAsync(SettingKey, cancellationToken);
            if (!string.IsNullOrWhiteSpace(stored))
            {
                try
                {
                    ChatPolicy? parsed = JsonSerializer.Deserialize<ChatPolicy>(stored);
                    if (parsed is not null)
                    {
                        _current = NormalizeStored(parsed);
                        return _current;
                    }
                }
                catch (JsonException exception)
                {
                    _logger.LogWarning(exception, "저장된 채팅 운영정책을 읽을 수 없어 기본값으로 복구합니다.");
                }
            }

            _current = DefaultPolicy();
            await SaveCoreAsync(_current, cancellationToken);
            return _current;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ChatPolicyResult> UpdateAsync(
        string pinnedNotice,
        int slowModeSeconds,
        IEnumerable<string> blockedWords,
        CancellationToken cancellationToken)
    {
        pinnedNotice = pinnedNotice.Trim();
        if (pinnedNotice.Length > 160)
            return ChatPolicyResult.Fail("고정 공지는 160자 이하로 입력해 주세요.");
        if (!AllowedSlowModes.Contains(slowModeSeconds))
            return ChatPolicyResult.Fail("슬로우 모드는 0·3·5·10·30·60초 중에서 선택해 주세요.");

        string[] normalizedWords = blockedWords
            .Select(word => (word ?? string.Empty).Trim())
            .Where(word => word.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedWords.Length > 50)
            return ChatPolicyResult.Fail("금칙어는 최대 50개까지 등록할 수 있습니다.");
        if (normalizedWords.Any(word => word.Length > 30))
            return ChatPolicyResult.Fail("각 금칙어는 30자 이하로 입력해 주세요.");

        var policy = new ChatPolicy(pinnedNotice, slowModeSeconds, normalizedWords, 5, 10, DateTimeOffset.Now);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await SaveCoreAsync(policy, cancellationToken);
            Volatile.Write(ref _current, policy);
        }
        finally
        {
            _gate.Release();
        }

        return ChatPolicyResult.Ok(policy);
    }

    public static PublicChatPolicy ToPublic(ChatPolicy policy) =>
        new(policy.PinnedNotice, policy.SlowModeSeconds);

    private async Task SaveCoreAsync(ChatPolicy policy, CancellationToken cancellationToken)
    {
        await _members.SetSettingAsync(SettingKey, JsonSerializer.Serialize(policy), cancellationToken);
    }

    private static ChatPolicy NormalizeStored(ChatPolicy policy) => new(
        policy.PinnedNotice ?? string.Empty,
        AllowedSlowModes.Contains(policy.SlowModeSeconds) ? policy.SlowModeSeconds : 0,
        policy.BlockedWords ?? [],
        policy.BurstLimit is >= 2 and <= 20 ? policy.BurstLimit : 5,
        policy.BurstWindowSeconds is >= 3 and <= 60 ? policy.BurstWindowSeconds : 10,
        policy.UpdatedAt);

    private static ChatPolicy DefaultPolicy() => new("", 0, [], 5, 10, DateTimeOffset.Now);
}
