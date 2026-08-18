namespace StockCasterPlatform;

public sealed record RegisterRequest(
    string? Username,
    string? DisplayName,
    string? Email,
    string? Password,
    bool TermsAccepted,
    bool PrivacyAccepted);
public sealed record LoginRequest(string Username, string Password);
public sealed record MuteRequest(bool Muted, int? DurationMinutes);
public sealed record MemberTierRequest(string Tier);
public sealed record MemberRoleRequest(string Role);
public sealed record ReplayUpdateRequest(string Title, bool IsPublished);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record ForgotPasswordRequest(string? Email);
public sealed record ResetPasswordRequest(string? Token, string? NewPassword);
public sealed record VerifyEmailRequest(string? Token);
public sealed record WithdrawRequest(string? Password);
public sealed record ReportRequest(Guid? MessageId, Guid? TargetMemberId, string? Category, string? Details);
public sealed record ReportStatusRequest(string? Status);
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
