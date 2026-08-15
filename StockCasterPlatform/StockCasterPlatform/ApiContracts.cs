namespace StockCasterPlatform;

public sealed record RegisterRequest(string Username, string DisplayName, string Password);
public sealed record LoginRequest(string Username, string Password);
public sealed record MuteRequest(bool Muted, int? DurationMinutes);
public sealed record MemberTierRequest(string Tier);
public sealed record ReplayUpdateRequest(string Title, bool IsPublished);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record BroadcastInfoUpdateRequest(
    string? Title,
    string? Description,
    string? Notice,
    DateTimeOffset? ScheduledAt);
public sealed record ChatPolicyUpdateRequest(
    string? PinnedNotice,
    int SlowModeSeconds,
    string[]? BlockedWords);
public sealed record TickerCreateRequest(string? Text);
