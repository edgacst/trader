using System.Collections.Concurrent;

namespace StockCasterPlatform;

public sealed class LoginAttemptService
{
    private const int MaximumFailures = 5;
    private static readonly TimeSpan BlockDuration = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<string, AttemptState> _attempts = new(StringComparer.Ordinal);

    public bool IsBlocked(string key, out int retryAfterSeconds)
    {
        retryAfterSeconds = 0;
        if (!_attempts.TryGetValue(key, out AttemptState? state))
            return false;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (state.BlockedUntil > now)
        {
            retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((state.BlockedUntil - now).TotalSeconds));
            return true;
        }
        if (state.LastFailure + BlockDuration <= now)
            _attempts.TryRemove(key, out _);
        return false;
    }

    public void RegisterFailure(string key)
    {
        _attempts.AddOrUpdate(
            key,
            _ => new AttemptState(1, DateTimeOffset.UtcNow, DateTimeOffset.MinValue),
            (_, current) =>
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                int failures = current.LastFailure + BlockDuration <= now ? 1 : current.Failures + 1;
                return failures >= MaximumFailures
                    ? new AttemptState(failures, now, now + BlockDuration)
                    : new AttemptState(failures, now, DateTimeOffset.MinValue);
            });
    }

    public void RegisterSuccess(string key) => _attempts.TryRemove(key, out _);

    private sealed record AttemptState(int Failures, DateTimeOffset LastFailure, DateTimeOffset BlockedUntil);
}
