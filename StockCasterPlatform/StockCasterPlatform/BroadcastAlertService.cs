namespace StockCasterPlatform;

public sealed record BroadcastAlert(
    string State,
    string Message,
    DateTimeOffset OccurredAt,
    bool IsActive);

public sealed class BroadcastAlertService : BackgroundService
{
    private readonly LiveStatusService _statusService;
    private readonly EmailDeliveryService _emailDelivery;
    private readonly IConfiguration _configuration;
    private readonly ILogger<BroadcastAlertService> _logger;
    private readonly object _lock = new();
    private BroadcastAlert? _lastAlert;
    private bool _wasLive;
    private string? _lastSignalState;
    private DateTimeOffset _lastEmailAt = DateTimeOffset.MinValue;

    public BroadcastAlertService(
        LiveStatusService statusService,
        EmailDeliveryService emailDelivery,
        IConfiguration configuration,
        ILogger<BroadcastAlertService> logger)
    {
        _statusService = statusService;
        _emailDelivery = emailDelivery;
        _configuration = configuration;
        _logger = logger;
    }

    public BroadcastAlert? GetLatest()
    {
        lock (_lock) return _lastAlert;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await CheckAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { _logger.LogWarning(exception, "방송 장애 알림 점검에 실패했습니다."); }
        }
    }

    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        LiveBroadcastStatus status = await _statusService.GetStatusAsync(cancellationToken);
        string signalState = status.IsLive ? status.Health : "offline";
        if (signalState == _lastSignalState)
        {
            _wasLive = status.IsLive;
            return;
        }
        string? previousSignalState = _lastSignalState;
        _lastSignalState = signalState;
        BroadcastAlert? alert = null;
        if (_wasLive && !status.IsLive)
            alert = new BroadcastAlert("offline", "방송 송출이 끊겼습니다. 송출 프로그램과 네트워크를 확인해 주세요.", DateTimeOffset.Now, true);
        else if (status.IsLive && (status.Health is "warning" or "offline"))
            alert = new BroadcastAlert("warning", "방송 수신 상태에 문제가 감지되었습니다. 영상·음성·프레임 상태를 확인해 주세요.", DateTimeOffset.Now, true);
        else if (status.IsLive && status.Health == "good" && (previousSignalState is "warning" or "offline"))
            alert = new BroadcastAlert("good", "방송 송출이 정상으로 회복되었습니다.", DateTimeOffset.Now, false);

        _wasLive = status.IsLive;
        if (alert is null) return;
        lock (_lock) _lastAlert = alert;
        _logger.LogInformation("방송 알림: {Message}", alert.Message);

        string recipient = _configuration["Alerts:Email"]?.Trim() ?? string.Empty;
        bool enabled = _configuration.GetValue<bool>("Alerts:Enabled");
        int cooldownMinutes = Math.Max(1, _configuration.GetValue("Alerts:CooldownMinutes", 10));
        if (enabled && !string.IsNullOrWhiteSpace(recipient) && DateTimeOffset.UtcNow - _lastEmailAt >= TimeSpan.FromMinutes(cooldownMinutes))
        {
            if (await _emailDelivery.SendOperationalAlertAsync(recipient, $"StockCaster 방송 알림: {alert.State}", alert.Message, cancellationToken))
                _lastEmailAt = DateTimeOffset.UtcNow;
        }
    }
}
