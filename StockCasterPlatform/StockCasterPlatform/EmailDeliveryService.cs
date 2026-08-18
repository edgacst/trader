using System.Net;
using System.Net.Mail;

namespace StockCasterPlatform;

public sealed class EmailDeliveryService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<EmailDeliveryService> _logger;

    public EmailDeliveryService(IConfiguration configuration, ILogger<EmailDeliveryService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public bool RequireVerification => _configuration.GetValue<bool>("Email:RequireVerification");

    public async Task<bool> SendVerificationAsync(string email, string token, CancellationToken cancellationToken)
    {
        string baseUrl = GetBaseUrl();
        string link = $"{baseUrl}/verify-email.html?token={Uri.EscapeDataString(token)}";
        return await SendAsync(
            email,
            "StockCaster 이메일 주소를 인증해 주세요",
            $"StockCaster 회원가입을 완료하려면 다음 링크를 열어 이메일을 인증해 주세요.<br><br><a href=\"{link}\">이메일 인증하기</a><br><br>이 링크는 24시간 동안 유효합니다.",
            cancellationToken);
    }

    public async Task<bool> SendPasswordResetAsync(string email, string token, CancellationToken cancellationToken)
    {
        string baseUrl = GetBaseUrl();
        string link = $"{baseUrl}/forgot-password.html?token={Uri.EscapeDataString(token)}";
        return await SendAsync(
            email,
            "StockCaster 비밀번호 재설정 안내",
            $"비밀번호 재설정을 요청하셨습니다.<br><br><a href=\"{link}\">비밀번호 재설정하기</a><br><br>본인이 요청하지 않았다면 이 메일을 무시하세요. 링크는 30분 동안 유효합니다.",
            cancellationToken);
    }

    public Task<bool> SendOperationalAlertAsync(string email, string subject, string message, CancellationToken cancellationToken) =>
        SendAsync(email, subject, $"<p>{WebUtility.HtmlEncode(message)}</p>", cancellationToken);

    private async Task<bool> SendAsync(string recipient, string subject, string htmlBody, CancellationToken cancellationToken)
    {
        string host = _configuration["Email:SmtpHost"]?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(host))
        {
            _logger.LogWarning("이메일 SMTP가 설정되지 않아 이메일을 전송하지 못했습니다. 수신자: {Recipient}", recipient);
            return false;
        }

        int port = _configuration.GetValue("Email:SmtpPort", 587);
        string from = _configuration["Email:From"]?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(from))
            from = _configuration["Email:SmtpUser"]?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(from))
            return false;

#pragma warning disable SYSLIB0014
        using var client = new SmtpClient(host, port)
        {
            EnableSsl = _configuration.GetValue("Email:UseSsl", true),
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(
                _configuration["Email:SmtpUser"],
                _configuration["Email:SmtpPassword"])
        };
#pragma warning restore SYSLIB0014

        using var message = new MailMessage(from, recipient)
        {
            Subject = subject,
            Body = $"<html><body style=\"font-family:Arial,sans-serif;line-height:1.7\">{htmlBody}</body></html>",
            IsBodyHtml = true
        };
        try
        {
            await client.SendMailAsync(message, cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is SmtpException or InvalidOperationException)
        {
            _logger.LogError(exception, "이메일 전송에 실패했습니다. 수신자: {Recipient}", recipient);
            return false;
        }
    }

    private string GetBaseUrl() =>
        (_configuration["Email:PublicBaseUrl"] ?? "http://127.0.0.1:5075").TrimEnd('/');
}
