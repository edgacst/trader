using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace StockCasterPlatform;

public sealed record ChatMessage(
    Guid Id,
    Guid MemberId,
    string Username,
    string DisplayName,
    string Text,
    bool IsAdmin,
    DateTimeOffset SentAt);

public sealed class ChatService
{
    private const int MaximumMessages = 300;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<Guid, ChatConnection> _connections = new();
    private readonly ConcurrentDictionary<Guid, ChatRateState> _rateStates = new();
    private readonly SemaphoreSlim _messageGate = new(1, 1);
    private readonly MemberStore _memberStore;
    private readonly ChatPolicyService _policyService;
    private readonly ILogger<ChatService> _logger;
    private readonly string _dataPath;
    private List<ChatMessage> _messages = [];
    private bool _loaded;

    public ChatService(
        MemberStore memberStore,
        ChatPolicyService policyService,
        IWebHostEnvironment environment,
        ILogger<ChatService> logger)
    {
        _memberStore = memberStore;
        _policyService = policyService;
        _logger = logger;
        _dataPath = Path.Combine(environment.ContentRootPath, "App_Data", "chat-messages.json");
    }

    public int OnlineCount => _connections.Values.Select(connection => connection.MemberId).Distinct().Count();

    public async Task HandleSocketAsync(WebSocket socket, Member member, CancellationToken cancellationToken)
    {
        var connection = new ChatConnection(socket, member.Id);
        Guid connectionId = Guid.NewGuid();
        _connections[connectionId] = connection;

        try
        {
            IReadOnlyList<ChatMessage> messages = await GetMessagesAsync(cancellationToken);
            ChatPolicy policy = await _policyService.GetAsync(cancellationToken);
            await connection.SendAsync(new
            {
                type = "connected",
                user = MemberStore.ToView(member),
                messages,
                onlineCount = OnlineCount,
                policy = ChatPolicyService.ToPublic(policy)
            }, cancellationToken);
            await BroadcastPresenceAsync(cancellationToken);

            byte[] buffer = new byte[4096];
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var payload = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "채팅 종료", CancellationToken.None);
                        return;
                    }

                    if (payload.Length + result.Count > 4096)
                    {
                        await connection.SendAsync(new { type = "error", message = "메시지가 너무 깁니다." }, cancellationToken);
                        break;
                    }

                    payload.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text || payload.Length == 0 || payload.Length > 4096)
                    continue;

                string json = Encoding.UTF8.GetString(payload.ToArray());
                await HandleClientMessageAsync(connection, member.Id, json, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (WebSocketException ex)
        {
            _logger.LogDebug("채팅 연결이 종료되었습니다: {Message}", ex.Message);
        }
        finally
        {
            _connections.TryRemove(connectionId, out _);
            connection.Dispose();
            if (!_connections.Values.Any(item => item.MemberId == member.Id))
                _rateStates.TryRemove(member.Id, out _);
            await BroadcastPresenceAsync(CancellationToken.None);
        }
    }

    public async Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(CancellationToken cancellationToken)
    {
        await _messageGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedCoreAsync(cancellationToken);
            return _messages.ToArray();
        }
        finally
        {
            _messageGate.Release();
        }
    }

    public async Task<bool> DeleteMessageAsync(Guid messageId, CancellationToken cancellationToken)
    {
        bool removed;
        await _messageGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedCoreAsync(cancellationToken);
            removed = _messages.RemoveAll(message => message.Id == messageId) > 0;
            if (removed)
                await SaveCoreAsync(cancellationToken);
        }
        finally
        {
            _messageGate.Release();
        }

        if (removed)
            await BroadcastAsync(new { type = "deleted", messageId }, cancellationToken);
        return removed;
    }

    public Task BroadcastMemberStateAsync(
        Guid memberId,
        bool muted,
        DateTimeOffset? mutedUntil,
        CancellationToken cancellationToken) =>
        BroadcastAsync(new { type = "memberState", memberId, muted, mutedUntil }, cancellationToken);

    public Task BroadcastPolicyAsync(ChatPolicy policy, CancellationToken cancellationToken) =>
        BroadcastAsync(new { type = "policy", policy = ChatPolicyService.ToPublic(policy) }, cancellationToken);

    private async Task HandleClientMessageAsync(ChatConnection connection, Guid memberId, string json, CancellationToken cancellationToken)
    {
        ChatClientPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<ChatClientPayload>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return;
        }

        if (payload is null || !string.Equals(payload.Type, "send", StringComparison.OrdinalIgnoreCase))
            return;

        string text = (payload.Text ?? string.Empty).Trim();
        if (text.Length is < 1 or > 300)
        {
            await connection.SendAsync(new { type = "error", message = "채팅은 1~300자로 입력해 주세요." }, cancellationToken);
            return;
        }

        Member? currentMember = await _memberStore.GetByIdAsync(memberId, cancellationToken);
        if (currentMember is null)
        {
            await connection.SendAsync(new { type = "error", message = "로그인 정보를 확인할 수 없습니다." }, cancellationToken);
            return;
        }

        if (MemberStore.IsChatRestricted(currentMember))
        {
            string message = currentMember.IsMuted
                ? "관리자에 의해 채팅이 무기한 제한되었습니다."
                : $"{currentMember.MutedUntil?.ToLocalTime():MM월 dd일 HH:mm}까지 채팅이 제한되었습니다.";
            await connection.SendAsync(new
            {
                type = "muted",
                message,
                mutedUntil = currentMember.IsMuted ? null : currentMember.MutedUntil
            }, cancellationToken);
            return;
        }

        ChatPolicy policy = await _policyService.GetAsync(cancellationToken);
        if (!MemberRoles.CanModerateChat(currentMember) && policy.BlockedWords.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase)))
        {
            await connection.SendAsync(new { type = "error", message = "사용할 수 없는 표현이 포함되어 있습니다." }, cancellationToken);
            return;
        }

        if (!MemberRoles.CanModerateChat(currentMember))
        {
            ChatRateCheck rateCheck = _rateStates
                .GetOrAdd(memberId, _ => new ChatRateState())
                .TryAccept(text, policy, DateTimeOffset.UtcNow);
            if (!rateCheck.Allowed)
            {
                await connection.SendAsync(new
                {
                    type = "error",
                    message = rateCheck.Error,
                    retryAfterSeconds = rateCheck.RetryAfterSeconds
                }, cancellationToken);
                return;
            }
        }

        var messageItem = new ChatMessage(
            Guid.NewGuid(),
            currentMember.Id,
            currentMember.Username,
            currentMember.DisplayName,
            text,
            currentMember.Role != MemberRoles.Member,
            DateTimeOffset.Now);

        await _messageGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedCoreAsync(cancellationToken);
            _messages.Add(messageItem);
            if (_messages.Count > MaximumMessages)
                _messages.RemoveRange(0, _messages.Count - MaximumMessages);
            await SaveCoreAsync(cancellationToken);
        }
        finally
        {
            _messageGate.Release();
        }

        await BroadcastAsync(new { type = "message", message = messageItem }, cancellationToken);
    }

    private async Task EnsureLoadedCoreAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(_dataPath)!);
        if (File.Exists(_dataPath))
        {
            try
            {
                string json = await File.ReadAllTextAsync(_dataPath, cancellationToken);
                _messages = JsonSerializer.Deserialize<List<ChatMessage>>(json, JsonOptions) ?? [];
                if (_messages.Count > MaximumMessages)
                    _messages = _messages.TakeLast(MaximumMessages).ToList();
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "채팅 기록 파일을 읽을 수 없어 새 기록으로 시작합니다.");
                _messages = [];
            }
        }
        _loaded = true;
    }

    private async Task SaveCoreAsync(CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(_dataPath)!;
        Directory.CreateDirectory(directory);
        string temporaryPath = _dataPath + ".tmp";
        string json = JsonSerializer.Serialize(_messages, new JsonSerializerOptions(JsonOptions) { WriteIndented = true });
        await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
        File.Move(temporaryPath, _dataPath, true);
    }

    private Task BroadcastPresenceAsync(CancellationToken cancellationToken) =>
        BroadcastAsync(new { type = "presence", onlineCount = OnlineCount }, cancellationToken);

    private async Task BroadcastAsync(object payload, CancellationToken cancellationToken)
    {
        ChatConnection[] connections = _connections.Values.ToArray();
        await Task.WhenAll(connections.Select(connection => connection.SendSafelyAsync(payload, cancellationToken)));
    }

    private sealed record ChatClientPayload(string? Type, string? Text);
    private sealed record ChatRateCheck(bool Allowed, string? Error, int RetryAfterSeconds)
    {
        public static ChatRateCheck Accept() => new(true, null, 0);
        public static ChatRateCheck Reject(string error, int retryAfterSeconds) => new(false, error, retryAfterSeconds);
    }

    private sealed class ChatRateState
    {
        private readonly object _gate = new();
        private readonly Queue<DateTimeOffset> _recentMessages = new();
        private DateTimeOffset _lastAcceptedAt = DateTimeOffset.MinValue;
        private DateTimeOffset _lastDuplicateAt = DateTimeOffset.MinValue;
        private string _lastText = string.Empty;

        public ChatRateCheck TryAccept(string text, ChatPolicy policy, DateTimeOffset now)
        {
            lock (_gate)
            {
                double minimumSeconds = Math.Max(.7, policy.SlowModeSeconds);
                double elapsed = (now - _lastAcceptedAt).TotalSeconds;
                if (elapsed < minimumSeconds)
                {
                    int retry = Math.Max(1, (int)Math.Ceiling(minimumSeconds - elapsed));
                    return ChatRateCheck.Reject($"슬로우 모드입니다. {retry}초 후 다시 보내 주세요.", retry);
                }

                while (_recentMessages.Count > 0 &&
                       now - _recentMessages.Peek() > TimeSpan.FromSeconds(policy.BurstWindowSeconds))
                    _recentMessages.Dequeue();
                if (_recentMessages.Count >= policy.BurstLimit)
                {
                    int retry = Math.Max(1, (int)Math.Ceiling(
                        policy.BurstWindowSeconds - (now - _recentMessages.Peek()).TotalSeconds));
                    return ChatRateCheck.Reject($"메시지가 너무 빠릅니다. {retry}초 후 다시 보내 주세요.", retry);
                }

                string normalized = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                if (normalized.Equals(_lastText, StringComparison.OrdinalIgnoreCase) &&
                    now - _lastDuplicateAt < TimeSpan.FromSeconds(30))
                    return ChatRateCheck.Reject("같은 메시지를 반복해서 보낼 수 없습니다.", 3);

                _lastAcceptedAt = now;
                _lastDuplicateAt = now;
                _lastText = normalized;
                _recentMessages.Enqueue(now);
                return ChatRateCheck.Accept();
            }
        }
    }

    private sealed class ChatConnection : IDisposable
    {
        private readonly WebSocket _socket;
        private readonly SemaphoreSlim _sendGate = new(1, 1);

        public ChatConnection(WebSocket socket, Guid memberId)
        {
            _socket = socket;
            MemberId = memberId;
        }

        public Guid MemberId { get; }

        public async Task SendAsync(object payload, CancellationToken cancellationToken)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
            await _sendGate.WaitAsync(cancellationToken);
            try
            {
                if (_socket.State == WebSocketState.Open)
                    await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
            }
            finally
            {
                _sendGate.Release();
            }
        }

        public async Task SendSafelyAsync(object payload, CancellationToken cancellationToken)
        {
            try
            {
                await SendAsync(payload, cancellationToken);
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
            {
            }
        }

        public void Dispose()
        {
            _sendGate.Dispose();
            _socket.Dispose();
        }
    }
}
