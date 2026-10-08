using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Data.SqlClient;
using Starfall.Grove.Moba.Api.Game;

namespace Starfall.Grove.Moba.Api.Services;

/// <summary>One player's lasting progress (coins, level, heroes and skins, ratings, stats), their account (username,
/// email, password) and their friends. Lock the profile while changing it.</summary>
public sealed class Profile
{
    /// <summary>Where the store keeps it (a hash of the token of the device that made it). Not saved inside.</summary>
    [JsonIgnore] public string Key { get; set; } = "";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "Wanderer";
    public int Coins { get; set; } = Economy.StartCoins;
    public int Level { get; set; } = 1;
    public int Xp { get; set; }
    public List<string> Heroes { get; set; } = [.. Economy.Starters];
    public List<string> Skins { get; set; } = [];
    /// <summary>hero id → the skin it wears (missing: its own look).</summary>
    public Dictionary<string, string> Equipped { get; set; } = [];
    public string Charm { get; set; } = "flash";
    /// <summary>"battle" / "duel" → rating from matchmaking.</summary>
    public Dictionary<string, int> Rating { get; set; } = new() { ["battle"] = Economy.StartRating, ["duel"] = Economy.StartRating };
    public int Games { get; set; }
    public int Wins { get; set; }
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Assists { get; set; }
    /// <summary>hero id → [games, wins].</summary>
    public Dictionary<string, int[]> HeroStats { get; set; } = [];
    /// <summary>The UTC day (yyyy-MM-dd) of the last first-win bonus.</summary>
    public string? FirstWinDay { get; set; }
    public List<MatchRecord> Recent { get; set; } = [];
    /// <summary>The day (UTC, yyyy-MM-dd) the quest progress belongs to, and how far each of that day's quests is.</summary>
    public string? QuestDay { get; set; }
    public Dictionary<string, int> QuestProgress { get; set; } = [];
    public DateTime Created { get; set; } = DateTime.UtcNow;

    // ── the account (null until the player registers)
    public string? Username { get; set; }
    public string? Email { get; set; }
    public string? PasswordHash { get; set; }
    /// <summary>A password reset in progress: the hash of the emailed code and when it stops working.</summary>
    public string? ResetHash { get; set; }
    public DateTime? ResetExpires { get; set; }
    /// <summary>Whether the player opened the link from the welcome email (or a reset email, which proves the same).</summary>
    public bool EmailConfirmed { get; set; }
    /// <summary>The hash of the emailed confirmation code, when it stops working, and when it was last sent.</summary>
    public string? ConfirmHash { get; set; }
    public DateTime? ConfirmExpires { get; set; }
    public DateTime? ConfirmSent { get; set; }

    // ── friends (profile ids)
    public List<string> Friends { get; set; } = [];
    /// <summary>Friend requests waiting for this player's answer.</summary>
    public List<string> Requests { get; set; } = [];
    /// <summary>Players whose chat and requests this player doesn't want to see.</summary>
    public List<string> Blocked { get; set; } = [];

    [JsonIgnore] public bool Registered => Username != null;
    public int RatingFor(string type) => Rating.TryGetValue(type, out var r) ? r : Economy.StartRating;
    public bool FirstWinReady(DateTime utc) => FirstWinDay != utc.ToString("yyyy-MM-dd");
    /// <summary>Heroes this player may pick: owned ones and this week's free ones.</summary>
    public HashSet<string> Playable(DateTime utc) => [.. Heroes, .. Economy.Rotation(utc)];

    /// <summary>Today's quests with their progress (a new day starts them over).</summary>
    public List<QuestDto> QuestsFor(DateTime utc)
    {
        var day = utc.ToString("yyyy-MM-dd");
        if (QuestDay != day) { QuestDay = day; QuestProgress = []; }
        return [.. Economy.DailyQuests(Id, utc).Select(q => new QuestDto(q.Id, q.Text, Math.Min(q.Goal, QuestProgress.GetValueOrDefault(q.Id)), q.Goal, q.Coins))];
    }
}

public sealed record QuestDto(string Id, string Text, int Progress, int Goal, int Coins);

public sealed record MatchRecord(string Hero, string Type, int Mode, bool Won, int K, int D, int A, int Coins, bool Ranked, DateTime At);

/// <summary>A profile as its owner sees it.</summary>
public sealed record ProfileDto(
    string Id, string Name, int Coins, int Level, int Xp, int XpNext, List<string> Heroes, List<string> Skins,
    Dictionary<string, string> Equipped, string Charm, Dictionary<string, int> Rating, Dictionary<string, string> Rank,
    int Games, int Wins, int Kills, int Deaths, int Assists, Dictionary<string, int[]> HeroStats, bool FirstWinReady,
    string[] Rotation, List<MatchRecord> Recent, string? Username, string? Email, int Requests, List<QuestDto> Quests, bool EmailConfirmed)
{
    public static ProfileDto Of(Profile p)
    {
        var now = DateTime.UtcNow;
        lock (p)
            return new(p.Id, p.Name, p.Coins, p.Level, p.Xp, Economy.XpToNext(p.Level), [.. p.Heroes], [.. p.Skins], new(p.Equipped), p.Charm,
                new(p.Rating), p.Rating.ToDictionary(kv => kv.Key, kv => Economy.RankOf(kv.Value)), p.Games, p.Wins, p.Kills, p.Deaths, p.Assists,
                p.HeroStats.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()), p.FirstWinReady(now), Economy.Rotation(now), [.. p.Recent],
                p.Username, p.Email, p.Requests.Count, p.QuestsFor(now), p.EmailConfirmed);
    }
}

public sealed record LeaderRow(string Name, int Level, int Rating, string Rank, int Wins, int Games);

/// <summary>Passwords are kept as salted PBKDF2-SHA256 hashes, never as they are typed.</summary>
public static class Passwords
{
    private const int Iterations = 100_000;
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }
    public static bool Verify(string password, string? stored)
    {
        var parts = stored?.Split('$');
        if (parts is not { Length: 4 } || parts[0] != "pbkdf2" || !int.TryParse(parts[1], out var it)) return false;
        var salt = Convert.FromBase64String(parts[2]); var want = Convert.FromBase64String(parts[3]);
        var got = Rfc2898DeriveBytes.Pbkdf2(password, salt, it, HashAlgorithmName.SHA256, want.Length);
        return CryptographicOperations.FixedTimeEquals(got, want);
    }
}

/// <summary>
/// Keeps profiles. A profile is stored under the hash of the token of the device that first made it; a device that
/// signs in to an account later gets its own token, linked to the profile as a session. Tokens themselves are never
/// stored, only their hashes. With a database, profiles live in dbo.MobaProfiles (with dbo.MobaSessions and
/// dbo.MobaReports); without one, as JSON files under App_Data, so local play works too. Profiles are cached once
/// loaded; changes are written in the background.
/// </summary>
public sealed class ProfileStore(IConfiguration config, IWebHostEnvironment env, ILogger<ProfileStore> log)
{
    private readonly string? _cs = config.GetConnectionString("Moba");
    private readonly ConcurrentDictionary<string, Profile> _cache = new();
    /// <summary>session token hash → profile key, and profile id → profile key.</summary>
    private readonly ConcurrentDictionary<string, string> _sessions = new(), _byId = new();
    private readonly Channel<(string Key, Profile P)> _writes = Channel.CreateUnbounded<(string, Profile)>();
    /// <summary>Held while an account is made or renamed, so two players can't take one username at once.</summary>
    public readonly SemaphoreSlim AccountLock = new(1, 1);
    private bool _sql;
    private string _dir = "";
    private Task? _writer;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public string Mode => _sql ? "database" : "files";

    private static readonly string[] Schema =
    [
        """
        IF OBJECT_ID(N'dbo.MobaProfiles', N'U') IS NULL
        CREATE TABLE dbo.MobaProfiles (
            TokenHash CHAR(64) NOT NULL PRIMARY KEY,
            Id NVARCHAR(16) NOT NULL,
            Name NVARCHAR(32) NOT NULL,
            Level INT NOT NULL,
            RatingBattle INT NOT NULL,
            RatingDuel INT NOT NULL,
            Wins INT NOT NULL,
            Games INT NOT NULL,
            Data NVARCHAR(MAX) NOT NULL,
            UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
        );
        """,
        "IF COL_LENGTH('dbo.MobaProfiles', 'Username') IS NULL ALTER TABLE dbo.MobaProfiles ADD Username NVARCHAR(32) NULL, Email NVARCHAR(256) NULL;",
        "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_MobaProfiles_Username') CREATE UNIQUE INDEX UX_MobaProfiles_Username ON dbo.MobaProfiles(Username) WHERE Username IS NOT NULL;",
        "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_MobaProfiles_Email') CREATE UNIQUE INDEX UX_MobaProfiles_Email ON dbo.MobaProfiles(Email) WHERE Email IS NOT NULL;",
        "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_MobaProfiles_Id') CREATE INDEX IX_MobaProfiles_Id ON dbo.MobaProfiles(Id);",
        """
        IF OBJECT_ID(N'dbo.MobaSessions', N'U') IS NULL
        CREATE TABLE dbo.MobaSessions (
            TokenHash CHAR(64) NOT NULL PRIMARY KEY,
            ProfileKey CHAR(64) NOT NULL,
            CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
        );
        """,
        """
        IF OBJECT_ID(N'dbo.MobaReports', N'U') IS NULL
        CREATE TABLE dbo.MobaReports (
            Id INT IDENTITY(1,1) PRIMARY KEY,
            Reporter NVARCHAR(16) NOT NULL,
            Target NVARCHAR(16) NOT NULL,
            TargetName NVARCHAR(32) NOT NULL,
            Reason NVARCHAR(200) NOT NULL,
            Message NVARCHAR(400) NULL,
            CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
        );
        """,
    ];

    public async Task InitAsync()
    {
        if (!string.IsNullOrWhiteSpace(_cs))
        {
            try
            {
                await using var c = new SqlConnection(_cs);
                await c.OpenAsync();
                foreach (var sql in Schema) { await using var cmd = new SqlCommand(sql, c); await cmd.ExecuteNonQueryAsync(); }
                _sql = true;
                log.LogInformation("Profiles are kept in the database.");
            }
            catch (Exception e) { log.LogWarning(e, "Database unavailable; profiles are kept in files."); }
        }
        if (!_sql)
        {
            _dir = Path.Combine(env.ContentRootPath, "App_Data", "profiles");
            Directory.CreateDirectory(_dir);
            var sessions = Path.Combine(env.ContentRootPath, "App_Data", "sessions.json");
            if (File.Exists(sessions))
                foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(sessions)) ?? []) _sessions[k] = v;
        }
        _writer = Task.Run(WriteLoop);
    }

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("minirift:" + token))).ToLowerInvariant();
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>The profile a token belongs to: its own, or the account it signed in to.</summary>
    private async Task<string> KeyOfAsync(string token)
    {
        var h = Hash(token);
        if (_sessions.TryGetValue(h, out var linked)) return linked;
        if (_cache.ContainsKey(h) || !_sql) return h;
        await using var c = new SqlConnection(_cs);
        await c.OpenAsync();
        await using var cmd = new SqlCommand("SELECT ProfileKey FROM dbo.MobaSessions WHERE TokenHash = @h", c);
        cmd.Parameters.AddWithValue("@h", h);
        if (await cmd.ExecuteScalarAsync() is string key) { _sessions[h] = key; return key; }
        return h;
    }

    /// <summary>The profile of an already connected token, if it is loaded.</summary>
    public Profile? Cached(string token)
    {
        var h = Hash(token);
        return _cache.TryGetValue(_sessions.TryGetValue(h, out var k) ? k : h, out var p) ? p : null;
    }

    /// <summary>The player's profile, loaded or (for a new device) created. Throws when the database can't be read, so
    /// a hiccup never replaces someone's progress with a fresh profile.</summary>
    public async Task<Profile> GetAsync(string token, string? name)
    {
        var key = await KeyOfAsync(token);
        if (await ByKeyAsync(key) is { } found) return found;
        if (key != Hash(token)) throw new InvalidOperationException("The account for this session is gone.");
        var made = new Profile { Key = key, Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant(), Name = Names.Clean(name) };
        var p = _cache.GetOrAdd(key, made);
        _byId[p.Id] = key;
        if (p == made) Save(p);
        return p;
    }

    /// <summary>The token's profile if there is one (null for a device that has never played or signed in).</summary>
    public async Task<Profile?> TryGetAsync(string token) => await ByKeyAsync(await KeyOfAsync(token));

    /// <summary>A profile by its storage key, from the cache or storage.</summary>
    public async Task<Profile?> ByKeyAsync(string key)
    {
        if (_cache.TryGetValue(key, out var hit)) return hit;
        var loaded = await LoadAsync(key);
        if (loaded == null) return null;
        loaded.Key = key;
        Repair(loaded);
        var p = _cache.GetOrAdd(key, loaded);
        _byId[p.Id] = key;
        return p;
    }

    /// <summary>A profile by its public id (friends, reports).</summary>
    public async Task<Profile?> ByIdAsync(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 16) return null;
        if (_byId.TryGetValue(id, out var key)) return await ByKeyAsync(key);
        return await FindAsync("Id", id, p => p.Id == id);
    }

    /// <summary>A registered profile by username or email (both compared without case).</summary>
    public Task<Profile?> ByUsernameAsync(string username) { var u = username.Trim().ToLowerInvariant(); return FindAsync("Username", u, p => p.Username?.ToLowerInvariant() == u); }
    public Task<Profile?> ByEmailAsync(string email) { var e = email.Trim().ToLowerInvariant(); return FindAsync("Email", e, p => p.Email?.ToLowerInvariant() == e); }

    private async Task<Profile?> FindAsync(string column, string value, Func<Profile, bool> match)
    {
        var cached = _cache.Values.FirstOrDefault(p => { lock (p) return match(p); });
        if (cached != null) return cached;
        if (_sql)
        {
            await using var c = new SqlConnection(_cs);
            await c.OpenAsync();
            await using var cmd = new SqlCommand($"SELECT TOP 1 TokenHash FROM dbo.MobaProfiles WHERE {column} = @v", c);
            cmd.Parameters.AddWithValue("@v", value);
            // The row can be a moment behind (a rename not written yet): only a profile that still matches counts.
            if (await cmd.ExecuteScalarAsync() is not string key || await ByKeyAsync(key.Trim()) is not { } found) return null;
            lock (found) return match(found) ? found : null;
        }
        foreach (var f in Directory.EnumerateFiles(_dir, "*.json"))
        {
            try
            {
                var p = JsonSerializer.Deserialize<Profile>(await File.ReadAllTextAsync(f), Json);
                if (p != null && match(p)) return await ByKeyAsync(Path.GetFileNameWithoutExtension(f));
            }
            catch { /* skip a broken file */ }
        }
        return null;
    }

    /// <summary>Fills in anything an older saved profile lacks.</summary>
    private static void Repair(Profile p)
    {
        foreach (var s in Economy.Starters) if (!p.Heroes.Contains(s)) p.Heroes.Add(s);
        foreach (var t in new[] { "battle", "duel" }) p.Rating.TryAdd(t, Economy.StartRating);
        if (!Economy.CharmById.ContainsKey(p.Charm)) p.Charm = "flash";
        p.Friends ??= []; p.Requests ??= []; p.Blocked ??= []; p.QuestProgress ??= [];
    }

    private async Task<Profile?> LoadAsync(string key)
    {
        if (_sql)
        {
            await using var c = new SqlConnection(_cs);
            await c.OpenAsync();
            await using var cmd = new SqlCommand("SELECT Data FROM dbo.MobaProfiles WHERE TokenHash = @k", c);
            cmd.Parameters.AddWithValue("@k", key);
            var data = await cmd.ExecuteScalarAsync() as string;
            return data == null ? null : JsonSerializer.Deserialize<Profile>(data, Json);
        }
        var file = Path.Combine(_dir, key + ".json");
        return File.Exists(file) ? JsonSerializer.Deserialize<Profile>(await File.ReadAllTextAsync(file), Json) : null;
    }

    // ───────────────────────────── sessions

    /// <summary>Links a new device token to a profile (signing in). Returns the token for the device to keep.</summary>
    public async Task<string> NewSessionAsync(Profile p)
    {
        var token = NewToken();
        var h = Hash(token);
        _sessions[h] = p.Key;
        if (_sql)
        {
            await using var c = new SqlConnection(_cs);
            await c.OpenAsync();
            await using var cmd = new SqlCommand("INSERT INTO dbo.MobaSessions (TokenHash, ProfileKey) VALUES (@h, @k)", c);
            cmd.Parameters.AddWithValue("@h", h); cmd.Parameters.AddWithValue("@k", p.Key);
            await cmd.ExecuteNonQueryAsync();
        }
        else await SaveSessionsFileAsync();
        return token;
    }

    /// <summary>Forgets one device's sign-in (logging out).</summary>
    public async Task EndSessionAsync(string token)
    {
        var h = Hash(token);
        if (!_sessions.TryRemove(h, out _)) return;
        if (_sql)
        {
            await using var c = new SqlConnection(_cs);
            await c.OpenAsync();
            await using var cmd = new SqlCommand("DELETE FROM dbo.MobaSessions WHERE TokenHash = @h", c);
            cmd.Parameters.AddWithValue("@h", h);
            await cmd.ExecuteNonQueryAsync();
        }
        else await SaveSessionsFileAsync();
    }

    /// <summary>Signs every other device out of a profile (after a password reset or when it is deleted).</summary>
    public async Task EndAllSessionsAsync(string key)
    {
        foreach (var (h, k) in _sessions) if (k == key) _sessions.TryRemove(h, out _);
        if (_sql)
        {
            await using var c = new SqlConnection(_cs);
            await c.OpenAsync();
            await using var cmd = new SqlCommand("DELETE FROM dbo.MobaSessions WHERE ProfileKey = @k", c);
            cmd.Parameters.AddWithValue("@k", key);
            await cmd.ExecuteNonQueryAsync();
        }
        else await SaveSessionsFileAsync();
    }

    private readonly SemaphoreSlim _sessionsFile = new(1, 1);
    /// <summary>Without a database, sessions are one small file, written by one caller at a time.</summary>
    private async Task SaveSessionsFileAsync()
    {
        await _sessionsFile.WaitAsync();
        try { await File.WriteAllTextAsync(Path.Combine(env.ContentRootPath, "App_Data", "sessions.json"), JsonSerializer.Serialize(_sessions.ToDictionary(kv => kv.Key, kv => kv.Value))); }
        finally { _sessionsFile.Release(); }
    }

    /// <summary>Removes a profile for good: from the cache, from storage, and every device signed in to it.</summary>
    public async Task DeleteAsync(Profile p)
    {
        var key = p.Key;
        _cache.TryRemove(key, out _);
        _byId.TryRemove(p.Id, out _);
        await EndAllSessionsAsync(key);
        await RemoveStoredAsync(key);
    }

    private async Task RemoveStoredAsync(string key)
    {
        if (_sql)
        {
            await using var c = new SqlConnection(_cs);
            await c.OpenAsync();
            await using var cmd = new SqlCommand("DELETE FROM dbo.MobaProfiles WHERE TokenHash = @k", c);
            cmd.Parameters.AddWithValue("@k", key);
            await cmd.ExecuteNonQueryAsync();
        }
        else File.Delete(Path.Combine(_dir, key + ".json"));
    }

    // ───────────────────────────── reports

    /// <summary>Keeps a player's report about another player (a name or a chat message) for review.</summary>
    public async Task ReportAsync(string reporter, string target, string targetName, string reason, string? message)
    {
        if (_sql)
        {
            await using var c = new SqlConnection(_cs);
            await c.OpenAsync();
            await using var cmd = new SqlCommand("INSERT INTO dbo.MobaReports (Reporter, Target, TargetName, Reason, Message) VALUES (@r, @t, @n, @why, @m)", c);
            cmd.Parameters.AddWithValue("@r", reporter); cmd.Parameters.AddWithValue("@t", target); cmd.Parameters.AddWithValue("@n", targetName);
            cmd.Parameters.AddWithValue("@why", reason); cmd.Parameters.AddWithValue("@m", (object?)message ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }
        else await File.AppendAllTextAsync(Path.Combine(env.ContentRootPath, "App_Data", "reports.log"),
            JsonSerializer.Serialize(new { at = DateTime.UtcNow, reporter, target, targetName, reason, message }) + "\n");
    }

    // ───────────────────────────── writing

    /// <summary>Writes a profile soon (in the background).</summary>
    public void Save(Profile p) => _writes.Writer.TryWrite((p.Key, p));

    private async Task WriteLoop()
    {
        var reader = _writes.Reader;
        while (await reader.WaitToReadAsync())
        {
            // Gather everything waiting, so a burst of changes to one profile is one write.
            var batch = new Dictionary<string, Profile>();
            while (reader.TryRead(out var w)) batch[w.Key] = w.P;
            foreach (var (key, p) in batch)
            {
                // Deleted while the write waited: don't bring it back.
                if (!_cache.ContainsKey(key)) continue;
                string json; string name, id; string? user, email; int level, rb, rd, wins, games;
                lock (p)
                {
                    json = JsonSerializer.Serialize(p, Json);
                    name = p.Name; id = p.Id; level = p.Level; rb = p.RatingFor("battle"); rd = p.RatingFor("duel"); wins = p.Wins; games = p.Games;
                    user = p.Username?.ToLowerInvariant(); email = p.Email?.ToLowerInvariant();
                }
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        if (_sql)
                        {
                            await using var c = new SqlConnection(_cs);
                            await c.OpenAsync();
                            await using var cmd = new SqlCommand("""
                                UPDATE dbo.MobaProfiles SET Name = @n, Level = @l, RatingBattle = @rb, RatingDuel = @rd, Wins = @w, Games = @g, Data = @d, Username = @u, Email = @e, UpdatedAt = SYSUTCDATETIME() WHERE TokenHash = @k;
                                IF @@ROWCOUNT = 0 INSERT INTO dbo.MobaProfiles (TokenHash, Id, Name, Level, RatingBattle, RatingDuel, Wins, Games, Data, Username, Email) VALUES (@k, @id, @n, @l, @rb, @rd, @w, @g, @d, @u, @e);
                                """, c);
                            cmd.Parameters.AddWithValue("@k", key); cmd.Parameters.AddWithValue("@id", id); cmd.Parameters.AddWithValue("@n", name);
                            cmd.Parameters.AddWithValue("@l", level); cmd.Parameters.AddWithValue("@rb", rb); cmd.Parameters.AddWithValue("@rd", rd);
                            cmd.Parameters.AddWithValue("@w", wins); cmd.Parameters.AddWithValue("@g", games); cmd.Parameters.AddWithValue("@d", json);
                            cmd.Parameters.AddWithValue("@u", (object?)user ?? DBNull.Value); cmd.Parameters.AddWithValue("@e", (object?)email ?? DBNull.Value);
                            await cmd.ExecuteNonQueryAsync();
                        }
                        else await File.WriteAllTextAsync(Path.Combine(_dir, key + ".json"), json);
                        // Deleted while this write was under way: take it out again.
                        if (!_cache.ContainsKey(key)) await RemoveStoredAsync(key);
                        break;
                    }
                    catch (Exception e)
                    {
                        log.LogWarning(e, "Could not save profile {Id} (attempt {N})", id, attempt + 1);
                        await Task.Delay(1000 * (attempt + 1));
                    }
                }
            }
        }
    }

    /// <summary>The best players by rating for a match type.</summary>
    public async Task<List<LeaderRow>> LeaderboardAsync(string type, int count = 20)
    {
        var col = type == "duel" ? "RatingDuel" : "RatingBattle";
        if (_sql)
        {
            var list = new List<LeaderRow>();
            await using var c = new SqlConnection(_cs);
            await c.OpenAsync();
            await using var cmd = new SqlCommand($"SELECT TOP (@n) Name, Level, {col}, Wins, Games FROM dbo.MobaProfiles WHERE Games > 0 ORDER BY {col} DESC, Wins DESC", c);
            cmd.Parameters.AddWithValue("@n", Math.Clamp(count, 1, 100));
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
                list.Add(new(rd.GetString(0), rd.GetInt32(1), rd.GetInt32(2), Economy.RankOf(rd.GetInt32(2)), rd.GetInt32(3), rd.GetInt32(4)));
            return list;
        }
        // Without a database: everyone saved on this machine.
        var all = new List<Profile>();
        foreach (var f in Directory.EnumerateFiles(_dir, "*.json"))
            try { if (JsonSerializer.Deserialize<Profile>(await File.ReadAllTextAsync(f), Json) is { } p) all.Add(_cache.GetValueOrDefault(Path.GetFileNameWithoutExtension(f)) ?? p); } catch { /* skip a broken file */ }
        return all.Where(p => p.Games > 0).OrderByDescending(p => p.RatingFor(type)).ThenByDescending(p => p.Wins).Take(count)
            .Select(p => new LeaderRow(p.Name, p.Level, p.RatingFor(type), Economy.RankOf(p.RatingFor(type)), p.Wins, p.Games)).ToList();
    }
}
