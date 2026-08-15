using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace StockCasterPlatform;

public static class MemberTiers
{
    public const string Free = "Free";
    public const string Premium = "Premium";

    public static bool IsValid(string? value) =>
        value is not null &&
        (value.Equals(Free, StringComparison.OrdinalIgnoreCase) ||
         value.Equals(Premium, StringComparison.OrdinalIgnoreCase));

    public static string Normalize(string? value) =>
        value?.Equals(Premium, StringComparison.OrdinalIgnoreCase) == true ? Premium : Free;

    public static string Label(Member member) => member.IsAdmin
        ? "운영자"
        : member.Tier == Premium ? "프리미엄 회원" : "무료 회원";

    public static bool CanWatchLive(Member member) => member.IsAdmin || member.Tier is Free or Premium;
    public static bool CanWatchReplay(Member member) => member.IsAdmin || member.Tier == Premium;
}

public sealed record Member(
    Guid Id,
    string Username,
    string DisplayName,
    string PasswordHash,
    string PasswordSalt,
    bool IsAdmin,
    bool IsMuted,
    DateTimeOffset? MutedUntil,
    string Tier,
    DateTimeOffset CreatedAt);

public sealed record MemberView(
    Guid Id,
    string Username,
    string DisplayName,
    bool IsAdmin,
    bool IsMuted,
    DateTimeOffset? MutedUntil,
    string Tier,
    string TierLabel,
    bool CanWatchLive,
    bool CanWatchReplay,
    DateTimeOffset CreatedAt);

public sealed record MemberResult(bool Success, string? Error, Member? Member)
{
    public static MemberResult Fail(string error) => new(false, error, null);
    public static MemberResult Ok(Member member) => new(true, null, member);
}

public sealed partial class MemberStore
{
    private const int PasswordIterations = 210_000;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly string _databasePath;
    private readonly string _legacyMembersPath;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MemberStore> _logger;
    private bool _initialized;

    public MemberStore(IWebHostEnvironment environment, IConfiguration configuration, ILogger<MemberStore> logger)
    {
        _configuration = configuration;
        _logger = logger;
        string dataDirectory = Path.Combine(environment.ContentRootPath, "App_Data");
        _databasePath = Path.Combine(dataDirectory, "stockcaster.db");
        _legacyMembersPath = Path.Combine(dataDirectory, "members.json");
    }

    public string DatabasePath => _databasePath;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
            return;

        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
            await using SqliteConnection connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);

            await using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = """
                    PRAGMA journal_mode = WAL;
                    PRAGMA foreign_keys = ON;
                    CREATE TABLE IF NOT EXISTS Members (
                        Id TEXT PRIMARY KEY,
                        Username TEXT NOT NULL COLLATE NOCASE UNIQUE,
                        DisplayName TEXT NOT NULL COLLATE NOCASE UNIQUE,
                        PasswordHash TEXT NOT NULL,
                        PasswordSalt TEXT NOT NULL,
                        IsAdmin INTEGER NOT NULL DEFAULT 0,
                        IsMuted INTEGER NOT NULL DEFAULT 0,
                        MutedUntil TEXT NULL,
                        Tier TEXT NOT NULL DEFAULT 'Free' CHECK (Tier IN ('Free', 'Premium')),
                        CreatedAt TEXT NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS AppMetadata (
                        Key TEXT PRIMARY KEY,
                        Value TEXT NOT NULL
                    );
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await EnsureColumnAsync(connection, "Members", "MutedUntil", "TEXT NULL", cancellationToken);

            await ImportLegacyMembersAsync(connection, cancellationToken);
            await EnsureAdminAsync(connection, cancellationToken);
            _initialized = true;
            _logger.LogInformation("SQLite 회원 데이터베이스를 준비했습니다: {DatabasePath}", _databasePath);
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    public async Task<MemberResult> RegisterAsync(string username, string displayName, string password, CancellationToken cancellationToken)
    {
        username = NormalizeUsername(username);
        displayName = displayName.Trim();

        if (!UsernamePattern().IsMatch(username))
            return MemberResult.Fail("아이디는 영문 소문자, 숫자, 마침표, 밑줄을 사용해 4~20자로 입력해 주세요.");
        if (displayName.Length is < 2 or > 20)
            return MemberResult.Fail("닉네임은 2~20자로 입력해 주세요.");
        if (!IsValidPassword(password))
            return MemberResult.Fail("비밀번호는 영문과 숫자를 포함해 8자 이상으로 입력해 주세요.");

        await InitializeAsync(cancellationToken);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using SqliteConnection connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);

            await using (SqliteCommand duplicateCommand = connection.CreateCommand())
            {
                duplicateCommand.CommandText = "SELECT Username, DisplayName FROM Members WHERE Username = $username OR DisplayName = $displayName LIMIT 1";
                duplicateCommand.Parameters.AddWithValue("$username", username);
                duplicateCommand.Parameters.AddWithValue("$displayName", displayName);
                await using SqliteDataReader duplicateReader = await duplicateCommand.ExecuteReaderAsync(cancellationToken);
                if (await duplicateReader.ReadAsync(cancellationToken))
                {
                    if (duplicateReader.GetString(0).Equals(username, StringComparison.OrdinalIgnoreCase))
                        return MemberResult.Fail("이미 사용 중인 아이디입니다.");
                    return MemberResult.Fail("이미 사용 중인 닉네임입니다.");
                }
            }

            (string hash, string salt) = HashPassword(password);
            var member = new Member(Guid.NewGuid(), username, displayName, hash, salt, false, false, null, MemberTiers.Free, DateTimeOffset.Now);
            await InsertMemberAsync(connection, member, false, cancellationToken);
            return MemberResult.Ok(member);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            return MemberResult.Fail("이미 사용 중인 아이디 또는 닉네임입니다.");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<Member?> AuthenticateAsync(string username, string password, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        Member? member = await FindMemberAsync(connection, "Username = $value", NormalizeUsername(username), cancellationToken);
        return member is not null && VerifyPassword(password, member.PasswordHash, member.PasswordSalt) ? member : null;
    }

    public async Task<Member?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        return await FindMemberAsync(connection, "Id = $value", id.ToString("D"), cancellationToken);
    }

    public async Task<IReadOnlyList<MemberView>> GetMembersAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Username, DisplayName, PasswordHash, PasswordSalt, IsAdmin, IsMuted, MutedUntil, Tier, CreatedAt FROM Members ORDER BY IsAdmin DESC, CreatedAt";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        var members = new List<MemberView>();
        while (await reader.ReadAsync(cancellationToken))
            members.Add(ToView(ReadMember(reader)));
        return members;
    }

    public async Task<Member?> SetChatRestrictionAsync(
        Guid id,
        bool muted,
        DateTimeOffset? mutedUntil,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using SqliteConnection connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE Members SET IsMuted = $isMuted, MutedUntil = $mutedUntil WHERE Id = $id AND IsAdmin = 0";
            command.Parameters.AddWithValue("$isMuted", muted && mutedUntil is null ? 1 : 0);
            command.Parameters.AddWithValue("$mutedUntil", mutedUntil is null ? DBNull.Value : mutedUntil.Value.ToString("O"));
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
                return null;
            return await FindMemberAsync(connection, "Id = $value", id.ToString("D"), cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<MemberResult> SetTierAsync(Guid id, string tier, CancellationToken cancellationToken)
    {
        if (!MemberTiers.IsValid(tier))
            return MemberResult.Fail("회원 등급은 Free 또는 Premium으로 설정해 주세요.");

        Member? member = await UpdateMemberAsync(id, "Tier", MemberTiers.Normalize(tier), cancellationToken);
        return member is null
            ? MemberResult.Fail("운영자 계정의 등급은 변경할 수 없습니다.")
            : MemberResult.Ok(member);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using SqliteConnection connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DELETE FROM Members WHERE Id = $id AND IsAdmin = 0";
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<MemberResult> ChangePasswordAsync(
        Guid id,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken)
    {
        if (!IsValidPassword(newPassword))
            return MemberResult.Fail("새 비밀번호는 영문과 숫자를 포함해 8자 이상으로 입력해 주세요.");
        if (string.Equals(currentPassword, newPassword, StringComparison.Ordinal))
            return MemberResult.Fail("현재 비밀번호와 다른 새 비밀번호를 입력해 주세요.");

        await InitializeAsync(cancellationToken);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using SqliteConnection connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            Member? member = await FindMemberAsync(connection, "Id = $value", id.ToString("D"), cancellationToken);
            if (member is null || !member.IsAdmin || !VerifyPassword(currentPassword, member.PasswordHash, member.PasswordSalt))
                return MemberResult.Fail("현재 비밀번호가 올바르지 않습니다.");

            (string hash, string salt) = HashPassword(newPassword);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE Members SET PasswordHash = $hash, PasswordSalt = $salt WHERE Id = $id AND IsAdmin = 1";
            command.Parameters.AddWithValue("$hash", hash);
            command.Parameters.AddWithValue("$salt", salt);
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            await command.ExecuteNonQueryAsync(cancellationToken);
            return MemberResult.Ok(member with { PasswordHash = hash, PasswordSalt = salt });
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM AppMetadata WHERE Key = $key";
        command.Parameters.AddWithValue("$key", key);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task SetSettingAsync(string key, string value, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using SqliteConnection connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "INSERT INTO AppMetadata (Key, Value) VALUES ($key, $value) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public static bool IsChatRestricted(Member member) =>
        member.IsMuted || member.MutedUntil is not null && member.MutedUntil > DateTimeOffset.Now;

    public static MemberView ToView(Member member)
    {
        bool restricted = IsChatRestricted(member);
        DateTimeOffset? activeUntil = !member.IsMuted && restricted ? member.MutedUntil : null;
        return new MemberView(
            member.Id,
            member.Username,
            member.DisplayName,
            member.IsAdmin,
            restricted,
            activeUntil,
            member.Tier,
            MemberTiers.Label(member),
            MemberTiers.CanWatchLive(member),
            MemberTiers.CanWatchReplay(member),
            member.CreatedAt);
    }

    private async Task<Member?> UpdateMemberAsync(Guid id, string column, object value, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using SqliteConnection connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"UPDATE Members SET {column} = $value WHERE Id = $id AND IsAdmin = 0";
            command.Parameters.AddWithValue("$value", value);
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
                return null;
            return await FindMemberAsync(connection, "Id = $value", id.ToString("D"), cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ImportLegacyMembersAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using (SqliteCommand markerCommand = connection.CreateCommand())
        {
            markerCommand.CommandText = "SELECT Value FROM AppMetadata WHERE Key = 'members-json-imported'";
            if (await markerCommand.ExecuteScalarAsync(cancellationToken) is not null)
                return;
        }

        int importedCount = 0;
        if (File.Exists(_legacyMembersPath))
        {
            try
            {
                string json = await File.ReadAllTextAsync(_legacyMembersPath, cancellationToken);
                List<LegacyMember> legacyMembers = JsonSerializer.Deserialize<List<LegacyMember>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? [];

                foreach (LegacyMember legacy in legacyMembers)
                {
                    var member = new Member(
                        legacy.Id,
                        legacy.Username,
                        legacy.DisplayName,
                        legacy.PasswordHash,
                        legacy.PasswordSalt,
                        legacy.IsAdmin,
                        legacy.IsMuted,
                        null,
                        legacy.IsAdmin ? MemberTiers.Premium : MemberTiers.Free,
                        legacy.CreatedAt);
                    importedCount += await InsertMemberAsync(connection, member, true, cancellationToken);
                }
            }
            catch (Exception exception) when (exception is JsonException or IOException or SqliteException)
            {
                _logger.LogError(exception, "기존 회원 JSON 데이터를 SQLite로 가져오지 못했습니다.");
                throw new InvalidOperationException("기존 회원 데이터를 이전할 수 없습니다.", exception);
            }
        }

        await using SqliteCommand completeCommand = connection.CreateCommand();
        completeCommand.CommandText = "INSERT OR REPLACE INTO AppMetadata (Key, Value) VALUES ('members-json-imported', $value)";
        completeCommand.Parameters.AddWithValue("$value", DateTimeOffset.UtcNow.ToString("O"));
        await completeCommand.ExecuteNonQueryAsync(cancellationToken);

        if (importedCount > 0)
            _logger.LogInformation("기존 회원 {MemberCount}명을 SQLite로 이전했습니다. 원본 JSON 파일은 보존했습니다.", importedCount);
    }

    private async Task EnsureAdminAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using (SqliteCommand countCommand = connection.CreateCommand())
        {
            countCommand.CommandText = "SELECT COUNT(*) FROM Members WHERE IsAdmin = 1";
            if (Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken)) > 0)
                return;
        }

        string username = NormalizeUsername(_configuration["AdminBootstrap:Username"] ?? "admin");
        Member? existing = await FindMemberAsync(connection, "Username = $value", username, cancellationToken);
        if (existing is not null)
        {
            await using SqliteCommand promoteCommand = connection.CreateCommand();
            promoteCommand.CommandText = "UPDATE Members SET IsAdmin = 1, Tier = 'Premium' WHERE Id = $id";
            promoteCommand.Parameters.AddWithValue("$id", existing.Id.ToString("D"));
            await promoteCommand.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        string displayName = _configuration["AdminBootstrap:DisplayName"] ?? "운영자";
        string password = _configuration["AdminBootstrap:Password"] ?? string.Empty;
        if (!IsValidPassword(password))
        {
            throw new InvalidOperationException(
                "운영자 계정이 없습니다. 최초 실행 전에 AdminBootstrap__Password 환경 변수에 " +
                "영문과 숫자를 포함한 8자 이상의 비밀번호를 설정해 주세요.");
        }

        (string hash, string salt) = HashPassword(password);
        var admin = new Member(Guid.NewGuid(), username, displayName, hash, salt, true, false, null, MemberTiers.Premium, DateTimeOffset.Now);
        await InsertMemberAsync(connection, admin, false, cancellationToken);
        _logger.LogInformation("초기 운영자 계정을 생성했습니다. 아이디: {Username}", username);
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string table,
        string column,
        string definition,
        CancellationToken cancellationToken)
    {
        await using (SqliteCommand columnsCommand = connection.CreateCommand())
        {
            columnsCommand.CommandText = $"PRAGMA table_info({table})";
            await using SqliteDataReader reader = await columnsCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase))
                    return;
            }
        }

        await using SqliteCommand alterCommand = connection.CreateCommand();
        alterCommand.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        await alterCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private SqliteConnection CreateConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 5
        };
        return new SqliteConnection(builder.ToString());
    }

    private static async Task<int> InsertMemberAsync(
        SqliteConnection connection,
        Member member,
        bool ignoreConflict,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT {(ignoreConflict ? "OR IGNORE " : string.Empty)}INTO Members
                (Id, Username, DisplayName, PasswordHash, PasswordSalt, IsAdmin, IsMuted, MutedUntil, Tier, CreatedAt)
            VALUES
                ($id, $username, $displayName, $passwordHash, $passwordSalt, $isAdmin, $isMuted, $mutedUntil, $tier, $createdAt)
            """;
        command.Parameters.AddWithValue("$id", member.Id.ToString("D"));
        command.Parameters.AddWithValue("$username", member.Username);
        command.Parameters.AddWithValue("$displayName", member.DisplayName);
        command.Parameters.AddWithValue("$passwordHash", member.PasswordHash);
        command.Parameters.AddWithValue("$passwordSalt", member.PasswordSalt);
        command.Parameters.AddWithValue("$isAdmin", member.IsAdmin ? 1 : 0);
        command.Parameters.AddWithValue("$isMuted", member.IsMuted ? 1 : 0);
        command.Parameters.AddWithValue("$mutedUntil", member.MutedUntil is null ? DBNull.Value : member.MutedUntil.Value.ToString("O"));
        command.Parameters.AddWithValue("$tier", MemberTiers.Normalize(member.Tier));
        command.Parameters.AddWithValue("$createdAt", member.CreatedAt.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Member?> FindMemberAsync(SqliteConnection connection, string predicate, string value, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT Id, Username, DisplayName, PasswordHash, PasswordSalt, IsAdmin, IsMuted, MutedUntil, Tier, CreatedAt FROM Members WHERE {predicate} LIMIT 1";
        command.Parameters.AddWithValue("$value", value);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadMember(reader) : null;
    }

    private static Member ReadMember(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetInt32(5) != 0,
        reader.GetInt32(6) != 0,
        reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7), null, System.Globalization.DateTimeStyles.RoundtripKind),
        MemberTiers.Normalize(reader.GetString(8)),
        DateTimeOffset.Parse(reader.GetString(9), null, System.Globalization.DateTimeStyles.RoundtripKind));

    private static string NormalizeUsername(string value) => (value ?? string.Empty).Trim().ToLowerInvariant();

    private static bool IsValidPassword(string password) =>
        password is { Length: >= 8 and <= 72 } &&
        password.Any(char.IsLetter) &&
        password.Any(char.IsDigit);

    private static (string Hash, string Salt) HashPassword(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, PasswordIterations, HashAlgorithmName.SHA256, 32);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    private static bool VerifyPassword(string password, string expectedHash, string encodedSalt)
    {
        try
        {
            byte[] salt = Convert.FromBase64String(encodedSalt);
            byte[] expected = Convert.FromBase64String(expectedHash);
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, PasswordIterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private sealed record LegacyMember(
        Guid Id,
        string Username,
        string DisplayName,
        string PasswordHash,
        string PasswordSalt,
        bool IsAdmin,
        bool IsMuted,
        DateTimeOffset CreatedAt);

    [GeneratedRegex("^[a-z0-9._]{4,20}$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();
}
