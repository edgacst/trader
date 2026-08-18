using System.Text.Json;

namespace StockCasterPlatform;

public sealed record ReportItem(
    Guid Id,
    Guid ReporterId,
    string ReporterName,
    Guid? TargetMemberId,
    string? TargetMemberName,
    Guid? MessageId,
    string Category,
    string Details,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt,
    string? ResolvedBy);

public sealed record ActivityLogItem(
    Guid Id,
    Guid? ActorId,
    string ActorName,
    string Action,
    string Target,
    string Details,
    DateTimeOffset CreatedAt);

public sealed class ModerationService
{
    private const int MaximumReports = 500;
    private const int MaximumLogs = 1000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _reportsPath;
    private readonly string _logsPath;
    private List<ReportItem>? _reports;
    private List<ActivityLogItem>? _logs;

    public ModerationService(IWebHostEnvironment environment)
    {
        string dataDirectory = Path.Combine(environment.ContentRootPath, "App_Data");
        Directory.CreateDirectory(dataDirectory);
        _reportsPath = Path.Combine(dataDirectory, "reports.json");
        _logsPath = Path.Combine(dataDirectory, "activity-logs.json");
    }

    public async Task<ReportItem> AddReportAsync(
        Member reporter,
        Guid? messageId,
        Member? target,
        string category,
        string details,
        CancellationToken cancellationToken)
    {
        category = string.IsNullOrWhiteSpace(category) ? "기타" : category.Trim()[..Math.Min(40, category.Trim().Length)];
        details = (details ?? string.Empty).Trim();
        if (details.Length > 500)
            details = details[..500];
        var report = new ReportItem(
            Guid.NewGuid(), reporter.Id, reporter.DisplayName, target?.Id, target?.DisplayName,
            messageId, category, details, "Open", DateTimeOffset.Now, null, null);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            _reports!.Add(report);
            if (_reports.Count > MaximumReports)
                _reports.RemoveRange(0, _reports.Count - MaximumReports);
            await SaveAsync(_reportsPath, _reports, cancellationToken);
            return report;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<ReportItem>> GetReportsAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            return _reports!.OrderByDescending(item => item.CreatedAt).ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task<ReportItem?> SetReportStatusAsync(Guid id, string status, Member actor, CancellationToken cancellationToken)
    {
        status = status is "Resolved" or "Dismissed" ? status : "Open";
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            int index = _reports!.FindIndex(item => item.Id == id);
            if (index < 0) return null;
            ReportItem updated = _reports[index] with
            {
                Status = status,
                ResolvedAt = status == "Open" ? null : DateTimeOffset.Now,
                ResolvedBy = status == "Open" ? null : actor.DisplayName
            };
            _reports[index] = updated;
            await SaveAsync(_reportsPath, _reports, cancellationToken);
            return updated;
        }
        finally { _gate.Release(); }
    }

    public async Task RecordActivityAsync(Member? actor, string action, string target, string details, CancellationToken cancellationToken)
    {
        var item = new ActivityLogItem(Guid.NewGuid(), actor?.Id, actor?.DisplayName ?? "시스템", action, target, details, DateTimeOffset.Now);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            _logs!.Add(item);
            if (_logs.Count > MaximumLogs)
                _logs.RemoveRange(0, _logs.Count - MaximumLogs);
            await SaveAsync(_logsPath, _logs, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<ActivityLogItem>> GetActivityLogsAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            return _logs!.OrderByDescending(item => item.CreatedAt).Take(300).ToArray();
        }
        finally { _gate.Release(); }
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_reports is null)
            _reports = await LoadAsync<ReportItem>(_reportsPath, cancellationToken);
        if (_logs is null)
            _logs = await LoadAsync<ActivityLogItem>(_logsPath, cancellationToken);
    }

    private static async Task<List<T>> LoadAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return [];
        try
        {
            await using FileStream stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<List<T>>(stream, JsonOptions, cancellationToken) ?? [];
        }
        catch (JsonException) { return []; }
    }

    private static async Task SaveAsync<T>(string path, List<T> items, CancellationToken cancellationToken)
    {
        string temporaryPath = path + ".tmp";
        await using (FileStream stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, items, JsonOptions, cancellationToken);
        File.Move(temporaryPath, path, true);
    }
}
