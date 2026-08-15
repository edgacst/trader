using System.Net.WebSockets;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using StockCasterPlatform;

string contentRoot = ResolveContentRoot();
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = contentRoot,
    WebRootPath = "wwwroot"
});

builder.WebHost.UseUrls(builder.Configuration["WebServer:Urls"] ?? "http://0.0.0.0:5075");
builder.Services.AddSingleton<BroadcastSecurityService>();
builder.Services.AddSingleton<MediaServerHostedService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<MediaServerHostedService>());
builder.Services.AddSingleton<MemberStore>();
builder.Services.AddSingleton<BroadcastInfoService>();
builder.Services.AddSingleton<LiveStatusService>();
builder.Services.AddSingleton<ChatPolicyService>();
builder.Services.AddSingleton<ChatService>();
builder.Services.AddSingleton<ReplayService>();
builder.Services.AddSingleton<TickerService>();
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "StockCaster.Session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddHttpClient("MediaStatus", client =>
{
    client.BaseAddress = new Uri("http://127.0.0.1:9997");
    client.Timeout = TimeSpan.FromSeconds(2);
});
builder.Services.AddHttpClient("MediaPlayback", client =>
{
    client.BaseAddress = new Uri("http://127.0.0.1:9996");
    client.Timeout = TimeSpan.FromMinutes(30);
});
builder.Services.AddHttpClient("MediaApi", client =>
{
    client.BaseAddress = new Uri("http://127.0.0.1:9997");
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddHttpClient("MediaHls", client =>
{
    client.BaseAddress = new Uri("http://127.0.0.1:8888");
    client.Timeout = TimeSpan.FromMinutes(5);
});

var app = builder.Build();

await app.Services.GetRequiredService<MemberStore>().InitializeAsync(CancellationToken.None);
await app.Services.GetRequiredService<BroadcastInfoService>().GetAsync(CancellationToken.None);
await app.Services.GetRequiredService<ChatPolicyService>().GetAsync(CancellationToken.None);
await app.Services.GetRequiredService<TickerService>().GetAsync(CancellationToken.None);

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        if (context.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
            context.File.Name.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
            context.File.Name.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
        {
            context.Context.Response.Headers.CacheControl = "no-store";
        }
    }
});
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/live", () => Results.Redirect("/live.html"));
app.MapGet("/replays", () => Results.Redirect("/replays.html"));
app.MapGet("/studio", () => Results.Redirect("/studio.html"));
app.MapGet("/admin", () => Results.Redirect("/admin.html"));

app.MapGet("/api/live/status", async (LiveStatusService statusService, CancellationToken cancellationToken) =>
    Results.Ok(await statusService.GetStatusAsync(cancellationToken)));

app.MapGet("/api/live/info", async (BroadcastInfoService broadcastInfo, CancellationToken cancellationToken) =>
    Results.Ok(await broadcastInfo.GetAsync(cancellationToken)));

app.MapGet("/api/chat/policy", async (ChatPolicyService chatPolicy, CancellationToken cancellationToken) =>
    Results.Ok(ChatPolicyService.ToPublic(await chatPolicy.GetAsync(cancellationToken))));

app.MapGet("/api/ticker", async (TickerService ticker, CancellationToken cancellationToken) =>
    Results.Ok(await ticker.GetAsync(cancellationToken)));

app.MapGet("/api/live/config", async (
    HttpContext context,
    MemberStore members,
    CancellationToken cancellationToken) =>
{
    Member? member = await GetCurrentMemberAsync(context, members, cancellationToken);
    if (member is null)
        return Results.Unauthorized();
    if (!MemberTiers.CanWatchLive(member))
        return Results.Forbid();

    return Results.Ok(new
    {
        streamName = "live",
        hlsUrl = "/api/live/hls/index.m3u8",
        webrtcUrl = BuildWebRtcUrl(context),
        tier = MemberTiers.Label(member)
    });
});

app.MapGet("/api/live/hls/{**assetPath}", StreamLiveAssetAsync)
    .RequireAuthorization();

app.MapGet("/api/studio/config", async (
    HttpContext context,
    BroadcastSecurityService broadcastSecurity,
    CancellationToken cancellationToken) =>
{
    BroadcastCredentials credentials = await broadcastSecurity.GetCredentialsAsync(cancellationToken);
    string host = context.Request.Host.Host;
    return Results.Ok(new
    {
        streamName = "live",
        rtmpServerUrl = $"rtmp://{host}:1935",
        streamKey = credentials.StreamKey
    });
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

app.MapGet("/api/replays", async (ReplayService replays, CancellationToken cancellationToken) =>
    Results.Ok(await replays.GetReplaysAsync(false, cancellationToken)));

app.MapGet("/api/replays/{id}/thumbnail.svg", async (
    string id,
    ReplayService replays,
    CancellationToken cancellationToken) =>
{
    ReplayItem? replay = await replays.GetReplayAsync(id, false, cancellationToken);
    return replay is null
        ? Results.NotFound()
        : Results.Content(ReplayService.BuildThumbnailSvg(replay), "image/svg+xml; charset=utf-8");
});

app.MapGet("/api/replays/{id}/video", StreamReplayAsync)
    .RequireAuthorization();

app.MapPost("/api/auth/register", async (
    RegisterRequest request,
    HttpContext context,
    MemberStore members,
    CancellationToken cancellationToken) =>
{
    MemberResult result = await members.RegisterAsync(
        request.Username ?? string.Empty,
        request.DisplayName ?? string.Empty,
        request.Password ?? string.Empty,
        cancellationToken);
    if (!result.Success || result.Member is null)
        return Results.BadRequest(new { error = result.Error });

    await SignInMemberAsync(context, result.Member);
    return Results.Ok(new { isAuthenticated = true, user = MemberStore.ToView(result.Member) });
});

app.MapPost("/api/auth/login", async (
    LoginRequest request,
    HttpContext context,
    MemberStore members,
    CancellationToken cancellationToken) =>
{
    Member? member = await members.AuthenticateAsync(
        request.Username ?? string.Empty,
        request.Password ?? string.Empty,
        cancellationToken);
    if (member is null)
        return Results.Json(new { error = "아이디 또는 비밀번호가 맞지 않습니다." }, statusCode: StatusCodes.Status401Unauthorized);

    await SignInMemberAsync(context, member);
    return Results.Ok(new { isAuthenticated = true, user = MemberStore.ToView(member) });
});

app.MapPost("/api/auth/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok(new { success = true });
});

app.MapGet("/api/auth/me", async (HttpContext context, MemberStore members, CancellationToken cancellationToken) =>
{
    Guid? memberId = GetMemberId(context.User);
    if (memberId is null)
        return Results.Ok(new { isAuthenticated = false });

    Member? member = await members.GetByIdAsync(memberId.Value, cancellationToken);
    return member is null
        ? Results.Ok(new { isAuthenticated = false })
        : Results.Ok(new { isAuthenticated = true, user = MemberStore.ToView(member) });
});

app.Map("/ws/chat", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    Guid? memberId = GetMemberId(context.User);
    if (memberId is null)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    var members = context.RequestServices.GetRequiredService<MemberStore>();
    Member? member = await members.GetByIdAsync(memberId.Value, context.RequestAborted);
    if (member is null)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
    var chat = context.RequestServices.GetRequiredService<ChatService>();
    await chat.HandleSocketAsync(socket, member, context.RequestAborted);
});

app.MapGet("/api/admin/summary", async (
    MemberStore members,
    ChatService chat,
    ReplayService replays,
    CancellationToken cancellationToken) =>
{
    IReadOnlyList<MemberView> memberList = await members.GetMembersAsync(cancellationToken);
    IReadOnlyList<ChatMessage> messages = await chat.GetMessagesAsync(cancellationToken);
    IReadOnlyList<ReplayItem> replayList = await replays.GetReplaysAsync(true, cancellationToken);
    return Results.Ok(new
    {
        memberCount = memberList.Count,
        mutedCount = memberList.Count(member => member.IsMuted),
        premiumCount = memberList.Count(member => member.IsAdmin || member.Tier == MemberTiers.Premium),
        messageCount = messages.Count,
        onlineCount = chat.OnlineCount,
        replayCount = replayList.Count
    });
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

app.MapPut("/api/admin/members/{memberId:guid}/tier", async (
    Guid memberId,
    MemberTierRequest request,
    MemberStore members,
    CancellationToken cancellationToken) =>
{
    MemberResult result = await members.SetTierAsync(memberId, request.Tier ?? string.Empty, cancellationToken);
    return result.Success && result.Member is not null
        ? Results.Ok(MemberStore.ToView(result.Member))
        : Results.BadRequest(new { error = result.Error });
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

app.MapDelete("/api/admin/members/{memberId:guid}", async (
    Guid memberId,
    MemberStore members,
    CancellationToken cancellationToken) =>
{
    bool deleted = await members.DeleteAsync(memberId, cancellationToken);
    return deleted
        ? Results.Ok(new { success = true })
        : Results.BadRequest(new { error = "운영자 계정은 삭제할 수 없습니다." });
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

app.MapGet("/api/admin/members", async (MemberStore members, CancellationToken cancellationToken) =>
    Results.Ok(await members.GetMembersAsync(cancellationToken)))
    .RequireAuthorization(policy => policy.RequireRole("Admin"));

app.MapPost("/api/admin/password", async (
    ChangePasswordRequest request,
    HttpContext context,
    MemberStore members,
    CancellationToken cancellationToken) =>
{
    Guid? memberId = GetMemberId(context.User);
    if (memberId is null)
        return Results.Unauthorized();

    MemberResult result = await members.ChangePasswordAsync(
        memberId.Value,
        request.CurrentPassword ?? string.Empty,
        request.NewPassword ?? string.Empty,
        cancellationToken);
    return result.Success
        ? Results.Ok(new { success = true })
        : Results.BadRequest(new { error = result.Error });
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

app.MapPost("/api/admin/broadcast-key/rotate", async (
    HttpContext context,
    BroadcastSecurityService broadcastSecurity,
    CancellationToken cancellationToken) =>
{
    try
    {
        BroadcastCredentials credentials = await broadcastSecurity.RotateAsync(cancellationToken);
        string host = context.Request.Host.Host;
        return Results.Ok(new
        {
            rtmpServerUrl = $"rtmp://{host}:1935",
            streamKey = credentials.StreamKey
        });
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

app.MapPut("/api/admin/live/info", async (
    BroadcastInfoUpdateRequest request,
    BroadcastInfoService broadcastInfo,
    CancellationToken cancellationToken) =>
{
    BroadcastInfoResult result = await broadcastInfo.UpdateAsync(
        request.Title ?? string.Empty,
        request.Description ?? string.Empty,
        request.Notice ?? string.Empty,
        request.ScheduledAt,
        cancellationToken);
    return result.Success && result.Info is not null
        ? Results.Ok(result.Info)
        : Results.BadRequest(new { error = result.Error });
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

app.MapGet("/api/admin/messages", async (ChatService chat, CancellationToken cancellationToken) =>
    Results.Ok(await chat.GetMessagesAsync(cancellationToken)))
    .RequireAuthorization(policy => policy.RequireRole("Admin"));

app.MapGet("/api/admin/chat/policy", async (ChatPolicyService chatPolicy, CancellationToken cancellationToken) =>
    Results.Ok(await chatPolicy.GetAsync(cancellationToken)))
    .RequireAuthorization(policy => policy.RequireRole("Admin"));

app.MapPut("/api/admin/chat/policy", async (
    ChatPolicyUpdateRequest request,
    ChatPolicyService chatPolicy,
    ChatService chat,
    CancellationToken cancellationToken) =>
{
    ChatPolicyResult result = await chatPolicy.UpdateAsync(
        request.PinnedNotice ?? string.Empty,
        request.SlowModeSeconds,
        request.BlockedWords ?? [],
        cancellationToken);
    if (!result.Success || result.Policy is null)
        return Results.BadRequest(new { error = result.Error });

    await chat.BroadcastPolicyAsync(result.Policy, cancellationToken);
    return Results.Ok(result.Policy);
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

app.MapGet("/api/admin/ticker", async (TickerService ticker, CancellationToken cancellationToken) =>
    Results.Ok(await ticker.GetAsync(cancellationToken)))
    .RequireAuthorization(policy => policy.RequireRole("Admin"));

app.MapPost("/api/admin/ticker", async (
    TickerCreateRequest request,
    TickerService ticker,
    CancellationToken cancellationToken) =>
{
    TickerResult result = await ticker.AddAsync(request.Text ?? string.Empty, cancellationToken);
    return result.Success
        ? Results.Ok(result.Message)
        : Results.BadRequest(new { error = result.Error });
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

app.MapDelete("/api/admin/ticker/{messageId:guid}", async (
    Guid messageId,
    TickerService ticker,
    CancellationToken cancellationToken) =>
{
    bool removed = await ticker.DeleteAsync(messageId, cancellationToken);
    return removed
        ? Results.Ok(new { success = true })
        : Results.NotFound(new { error = "스크롤 공지를 찾을 수 없습니다." });
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

app.MapPost("/api/admin/members/{memberId:guid}/mute", async (
    Guid memberId,
    MuteRequest request,
    MemberStore members,
    ChatService chat,
    CancellationToken cancellationToken) =>
{
    if (request.Muted && request.DurationMinutes is not null &&
        request.DurationMinutes is not (10 or 60 or 1440))
        return Results.BadRequest(new { error = "채팅 제한 시간은 10분·1시간·24시간 또는 영구 중에서 선택해 주세요." });

    DateTimeOffset? mutedUntil = request.Muted && request.DurationMinutes is int duration
        ? DateTimeOffset.Now.AddMinutes(duration)
        : null;
    Member? updated = await members.SetChatRestrictionAsync(memberId, request.Muted, mutedUntil, cancellationToken);
    if (updated is null)
        return Results.BadRequest(new { error = "운영자 계정은 채팅을 제한할 수 없습니다." });

    MemberView view = MemberStore.ToView(updated);
    await chat.BroadcastMemberStateAsync(memberId, view.IsMuted, view.MutedUntil, cancellationToken);
    return Results.Ok(view);
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

app.MapDelete("/api/admin/messages/{messageId:guid}", async (
    Guid messageId,
    ChatService chat,
    CancellationToken cancellationToken) =>
{
    bool removed = await chat.DeleteMessageAsync(messageId, cancellationToken);
    return removed ? Results.Ok(new { success = true }) : Results.NotFound(new { error = "메시지를 찾을 수 없습니다." });
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

app.MapGet("/api/admin/replays", async (ReplayService replays, CancellationToken cancellationToken) =>
    Results.Ok(await replays.GetReplaysAsync(true, cancellationToken)))
    .RequireAuthorization(policy => policy.RequireRole("Admin"));

app.MapPut("/api/admin/replays/{id}", async (
    string id,
    ReplayUpdateRequest request,
    ReplayService replays,
    CancellationToken cancellationToken) =>
{
    ReplayResult result = await replays.UpdateAsync(id, request.Title ?? string.Empty, request.IsPublished, cancellationToken);
    return result.Success ? Results.Ok(result.Replay) : Results.BadRequest(new { error = result.Error });
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

app.MapDelete("/api/admin/replays/{id}", async (
    string id,
    ReplayService replays,
    CancellationToken cancellationToken) =>
{
    ReplayResult result = await replays.DeleteAsync(id, cancellationToken);
    return result.Success ? Results.Ok(new { success = true }) : Results.BadRequest(new { error = result.Error });
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

app.MapGet("/health", (MediaServerHostedService mediaServer) => Results.Ok(new
{
    web = "ready",
    mediaServer = mediaServer.IsRunning ? "ready" : "starting",
    timestamp = DateTimeOffset.Now
}));

app.Run();

static async Task StreamReplayAsync(
    string id,
    HttpContext context,
    MemberStore members,
    ReplayService replays,
    CancellationToken cancellationToken)
{
    Member? member = await GetCurrentMemberAsync(context, members, cancellationToken);
    if (member is null)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }
    if (!MemberTiers.CanWatchReplay(member))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { error = "다시보기는 프리미엄 회원에게 제공됩니다." }, cancellationToken);
        return;
    }

    ReplayItem? replay = await replays.GetReplayAsync(id, context.User.IsInRole("Admin"), cancellationToken);
    if (replay is null)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    string? localVideoPath = replays.GetLocalVideoPath(replay);
    if (localVideoPath is not null)
    {
        context.Response.Headers.CacheControl = "private, no-store";
        await Results.File(
            localVideoPath,
            contentType: "video/mp4",
            enableRangeProcessing: true).ExecuteAsync(context);
        return;
    }

    using HttpResponseMessage upstream = await replays.OpenVideoAsync(replay, cancellationToken);
    context.Response.StatusCode = (int)upstream.StatusCode;
    context.Response.Headers.CacheControl = "private, no-store";
    context.Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "video/mp4";
    if (upstream.Content.Headers.ContentLength is long length)
        context.Response.ContentLength = length;

    if (!upstream.IsSuccessStatusCode)
        return;

    await upstream.Content.CopyToAsync(context.Response.Body, cancellationToken);
}

static async Task StreamLiveAssetAsync(
    string? assetPath,
    HttpContext context,
    MemberStore members,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken)
{
    Member? member = await GetCurrentMemberAsync(context, members, cancellationToken);
    if (member is null)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }
    if (!MemberTiers.CanWatchLive(member))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    string safePath = (assetPath ?? string.Empty).Replace('\\', '/').TrimStart('/');
    if (safePath.Contains("..", StringComparison.Ordinal))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    string upstreamPath = $"live/{safePath}{context.Request.QueryString}";
    using var request = new HttpRequestMessage(HttpMethod.Get, upstreamPath);
    if (context.Request.Headers.Range.Count > 0)
        request.Headers.TryAddWithoutValidation("Range", context.Request.Headers.Range.ToString());

    HttpClient client = httpClientFactory.CreateClient("MediaHls");
    using HttpResponseMessage upstream = await client.SendAsync(
        request,
        HttpCompletionOption.ResponseHeadersRead,
        cancellationToken);

    context.Response.StatusCode = (int)upstream.StatusCode;
    context.Response.Headers.CacheControl = "private, no-store";
    if (!upstream.IsSuccessStatusCode)
        return;

    context.Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
    if (upstream.Content.Headers.ContentLength is long length)
        context.Response.ContentLength = length;
    if (upstream.Content.Headers.ContentRange is not null)
        context.Response.Headers.ContentRange = upstream.Content.Headers.ContentRange.ToString();

    await upstream.Content.CopyToAsync(context.Response.Body, cancellationToken);
}

static async Task<Member?> GetCurrentMemberAsync(
    HttpContext context,
    MemberStore members,
    CancellationToken cancellationToken)
{
    Guid? memberId = GetMemberId(context.User);
    return memberId is null ? null : await members.GetByIdAsync(memberId.Value, cancellationToken);
}

static Guid? GetMemberId(ClaimsPrincipal user) =>
    Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out Guid id) ? id : null;

static string BuildWebRtcUrl(HttpContext context)
{
    string host = context.Request.Host.Host;
    string formattedHost = host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;
    string scheme = context.Request.IsHttps ? "https" : "http";
    return $"{scheme}://{formattedHost}:8889/live-webrtc/whep";
}

static async Task SignInMemberAsync(HttpContext context, Member member)
{
    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, member.Id.ToString()),
        new(ClaimTypes.Name, member.Username),
        new("display_name", member.DisplayName)
    };
    if (member.IsAdmin)
        claims.Add(new Claim(ClaimTypes.Role, "Admin"));

    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(identity),
        new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(12) });
}

static string ResolveContentRoot()
{
    string executableDirectory = AppContext.BaseDirectory;
    if (Directory.Exists(Path.Combine(executableDirectory, "wwwroot")))
        return executableDirectory;

    string sourceDirectory = Path.GetFullPath(Path.Combine(executableDirectory, "..", "..", ".."));
    if (Directory.Exists(Path.Combine(sourceDirectory, "wwwroot")))
        return sourceDirectory;

    return Directory.GetCurrentDirectory();
}
