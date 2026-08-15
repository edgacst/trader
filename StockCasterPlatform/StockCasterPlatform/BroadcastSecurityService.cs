using System.Net.Http.Json;
using System.Security.Cryptography;

namespace StockCasterPlatform;

public sealed record BroadcastCredentials(string Username, string Password)
{
    public string StreamKey => $"live?user={Username}&pass={Password}";
}

public sealed class BroadcastSecurityService
{
    private const string PublisherUsername = "stockcaster";
    private const string PublishKeySetting = "broadcast-publish-key";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly MemberStore _memberStore;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<BroadcastSecurityService> _logger;

    public BroadcastSecurityService(
        MemberStore memberStore,
        IHttpClientFactory httpClientFactory,
        ILogger<BroadcastSecurityService> logger)
    {
        _memberStore = memberStore;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<BroadcastCredentials> GetCredentialsAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await GetOrCreateCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<BroadcastCredentials> RotateAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            BroadcastCredentials previous = await GetOrCreateCoreAsync(cancellationToken);
            var updated = new BroadcastCredentials(PublisherUsername, GenerateKey());
            await _memberStore.SetSettingAsync(PublishKeySetting, updated.Password, cancellationToken);

            try
            {
                await ApplyToRunningServerCoreAsync(updated, cancellationToken);
                _logger.LogInformation("RTMP 송출 보안키를 재발급했습니다.");
                return updated;
            }
            catch
            {
                await _memberStore.SetSettingAsync(PublishKeySetting, previous.Password, CancellationToken.None);
                try
                {
                    await ApplyToRunningServerCoreAsync(previous, CancellationToken.None);
                }
                catch (Exception rollbackException)
                {
                    _logger.LogError(rollbackException, "RTMP 송출 보안키 롤백에 실패했습니다.");
                }
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ApplyToRunningServerAsync(CancellationToken cancellationToken)
    {
        BroadcastCredentials credentials = await GetCredentialsAsync(cancellationToken);
        await ApplyToRunningServerCoreAsync(credentials, cancellationToken);
    }

    private async Task<BroadcastCredentials> GetOrCreateCoreAsync(CancellationToken cancellationToken)
    {
        string? key = await _memberStore.GetSettingAsync(PublishKeySetting, cancellationToken);
        if (string.IsNullOrWhiteSpace(key))
        {
            key = GenerateKey();
            await _memberStore.SetSettingAsync(PublishKeySetting, key, cancellationToken);
            _logger.LogInformation("최초 RTMP 송출 보안키를 생성했습니다.");
        }
        return new BroadcastCredentials(PublisherUsername, key);
    }

    private async Task ApplyToRunningServerCoreAsync(BroadcastCredentials credentials, CancellationToken cancellationToken)
    {
        var payload = new
        {
            authInternalUsers = BuildAuthUsers(credentials)
        };
        HttpClient client = _httpClientFactory.CreateClient("MediaApi");
        using var request = new HttpRequestMessage(HttpMethod.Patch, "/v3/config/global/patch")
        {
            Content = JsonContent.Create(payload)
        };
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string detail = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"방송 서버에 보안키를 적용하지 못했습니다: {(int)response.StatusCode} {detail}");
        }
    }

    public static object[] BuildAuthUsers(BroadcastCredentials credentials) =>
    [
        new
        {
            user = credentials.Username,
            pass = credentials.Password,
            ips = Array.Empty<string>(),
            permissions = new[] { new { action = "publish", path = "live" } }
        },
        new
        {
            user = "any",
            pass = string.Empty,
            ips = Array.Empty<string>(),
            permissions = new[]
            {
                new { action = "read", path = "live" },
                new { action = "playback", path = "live" }
            }
        },
        new
        {
            user = "any",
            pass = string.Empty,
            ips = new[] { "127.0.0.1", "::1" },
            permissions = new[]
            {
                new { action = "api", path = string.Empty },
                new { action = "metrics", path = string.Empty },
                new { action = "pprof", path = string.Empty }
            }
        }
    ];

    private static string GenerateKey()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(24);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
