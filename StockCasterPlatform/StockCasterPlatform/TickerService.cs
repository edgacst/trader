using System.Text.Json;

namespace StockCasterPlatform;

public sealed record TickerMessage(Guid Id, string Text, DateTimeOffset CreatedAt);

public sealed record TickerResult(bool Success, string? Error, TickerMessage? Message)
{
    public static TickerResult Ok(TickerMessage message) => new(true, null, message);
    public static TickerResult Fail(string error) => new(false, error, null);
}

public sealed class TickerService
{
    private const string SettingKey = "ticker-messages";
    private const int MaximumMessages = 20;
    private readonly MemberStore _members;
    private readonly ILogger<TickerService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<TickerMessage>? _current;

    public TickerService(MemberStore members, ILogger<TickerService> logger)
    {
        _members = members;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TickerMessage>> GetAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<TickerMessage>? cached = Volatile.Read(ref _current);
        if (cached is not null)
            return cached;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_current is not null)
                return _current;

            _current = await LoadCoreAsync(cancellationToken);
            return _current;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TickerResult> AddAsync(string text, CancellationToken cancellationToken)
    {
        text = (text ?? string.Empty).Trim();
        if (text.Length is < 2 or > 180)
            return TickerResult.Fail("스크롤 공지는 2~180자로 입력해 주세요.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            IReadOnlyList<TickerMessage> current = _current ?? await LoadCoreAsync(cancellationToken);
            if (current.Count >= MaximumMessages)
                return TickerResult.Fail($"스크롤 공지는 최대 {MaximumMessages}개까지 등록할 수 있습니다.");

            var message = new TickerMessage(Guid.NewGuid(), text, DateTimeOffset.Now);
            IReadOnlyList<TickerMessage> updated = [.. current, message];
            await SaveCoreAsync(updated, cancellationToken);
            Volatile.Write(ref _current, updated);
            return TickerResult.Ok(message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            IReadOnlyList<TickerMessage> current = _current ?? await LoadCoreAsync(cancellationToken);
            IReadOnlyList<TickerMessage> updated = current.Where(message => message.Id != id).ToList();
            if (updated.Count == current.Count)
                return false;

            await SaveCoreAsync(updated, cancellationToken);
            Volatile.Write(ref _current, updated);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<TickerMessage>> LoadCoreAsync(CancellationToken cancellationToken)
    {
        string? stored = await _members.GetSettingAsync(SettingKey, cancellationToken);
        if (!string.IsNullOrWhiteSpace(stored))
        {
            try
            {
                List<TickerMessage>? parsed = JsonSerializer.Deserialize<List<TickerMessage>>(stored);
                if (parsed is not null)
                    return Normalize(parsed);
            }
            catch (JsonException exception)
            {
                _logger.LogWarning(exception, "저장된 스크롤 공지를 읽을 수 없어 기본값으로 복구합니다.");
            }
        }

        return [DefaultMessage()];
    }

    private async Task SaveCoreAsync(IReadOnlyList<TickerMessage> messages, CancellationToken cancellationToken)
    {
        await _members.SetSettingAsync(SettingKey, JsonSerializer.Serialize(messages), cancellationToken);
    }

    private static IReadOnlyList<TickerMessage> Normalize(IEnumerable<TickerMessage> messages) => messages
        .Where(message => message.Id != Guid.Empty && !string.IsNullOrWhiteSpace(message.Text))
        .Select(message => message with { Text = message.Text.Trim() })
        .Where(message => message.Text.Length <= 180)
        .Take(MaximumMessages)
        .ToList();

    private static TickerMessage DefaultMessage() => new(
        Guid.NewGuid(),
        "⚠ 투자위험 고지 | 본 방송의 정보는 투자 참고용이며 투자 권유나 수익을 보장하지 않습니다. 투자 판단과 그에 따른 손익 책임은 투자자 본인에게 있습니다.",
        DateTimeOffset.Now);
}
