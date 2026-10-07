using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Data.SqlClient;
using Starfall.Grove.Moba.Api.Game;

namespace Starfall.Grove.Moba.Api.Services;

/// <summary>One player's lasting progress: coins, level, the heroes and skins they own, their ratings and stats.
/// Lock the profile while changing it.</summary>
public sealed class Profile
{
    /// <summary>Where the store keeps it (a hash of the player's token). Not saved inside the profile.</summary>
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
    public DateTime Created { get; set; } = DateTime.UtcNow;

    public int RatingFor(string type) => Rating.TryGetValue(type, out var r) ? r : Economy.StartRating;
    public bool FirstWinReady(DateTime utc) => FirstWinDay != utc.ToString("yyyy-MM-dd");
    /// <summary>Heroes this player may pick: owned ones and this week's free ones.</summary>
    public HashSet<string> Playable(DateTime utc) => [.. Heroes, .. Economy.Rotation(utc)];
}

public sealed record MatchRecord(string Hero, string Type, int Mode, bool Won, int K, int D, int A, int Coins, bool Ranked, DateTime At);

/// <summary>A profile as its owner sees it.</summary>
public sealed record ProfileDto(
    string Id, string Name, int Coins, int Level, int Xp, int XpNext, List<string> Heroes, List<string> Skins,
    Dictionary<string, string> Equipped, string Charm, Dictionary<string, int> Rating, Dictionary<string, string> Rank,
    int Games, int Wins, int Kills, int Deaths, int Assists, Dictionary<string, int[]> HeroStats, bool FirstWinReady,
    string[] Rotation, List<MatchRecord> Recent)
{
    public static ProfileDto Of(Profile p)
    {
        var now = DateTime.UtcNow;
        lock (p)
            return new(p.Id, p.Name, p.Coins, p.Level, p.Xp, Economy.XpToNext(p.Level), [.. p.Heroes], [.. p.Skins], new(p.Equipped), p.Charm,
                new(p.Rating), p.Rating.ToDictionary(kv => kv.Key, kv => Economy.RankOf(kv.Value)), p.Games, p.Wins, p.Kills, p.Deaths, p.Assists,
                p.HeroStats.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()), p.FirstWinReady(now), Economy.Rotation(now), [.. p.Recent]);
    }
}

public sealed record LeaderRow(string Name, int Level, int Rating, string Rank, int Wins, int Games);

/// <summary>
/// Keeps profiles, keyed by a hash of the secret token each browser holds (the token itself is never stored). With a
/// database they live in dbo.MobaProfiles (created on first start); without one, as JSON files under App_Data, so
/// local play keeps its progress too. Profiles are cached once loaded; changes are written in the background.
/// </summary>
public sealed class ProfileStore(IConfiguration config, IWebHostEnvironment env, ILogger<ProfileStore> log)
{
    private readonly string? _cs = config.GetConnectionString("Moba");
    private readonly ConcurrentDictionary<string, Profile> _cache = new();
    private readonly Channel<(string Key, Profile P)> _writes = Channel.CreateUnbounded<(string, Profile)>();
    private bool _sql;
    private string _dir = "";
    private Task? _writer;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public string Mode => _sql ? "database" : "files";

    private const string Schema = """
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
        """;

    public async Task InitAsync()
    {
        if (!string.IsNullOrWhiteSpace(_cs))
        {
            try
            {
                await using var c = new SqlConnection(_cs);
                await c.OpenAsync();
                await using var cmd = new SqlCommand(Schema, c);
                await cmd.ExecuteNonQueryAsync();
                _sql = true;
                log.LogInformation("Profiles are kept in the database.");
            }
            catch (Exception e) { log.LogWarning(e, "Database unavailable; profiles are kept in files."); }
        }
        if (!_sql)
        {
            _dir = Path.Combine(env.ContentRootPath, "App_Data", "profiles");
            Directory.CreateDirectory(_dir);
        }
        _writer = Task.Run(WriteLoop);
    }

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("minirift:" + token))).ToLowerInvariant();

    public Profile? Cached(string token) => _cache.TryGetValue(Hash(token), out var p) ? p : null;

    /// <summary>The player's profile, loaded or created. Throws when the database can't be read, so a hiccup never
    /// replaces someone's progress with a fresh profile.</summary>
    public async Task<Profile> GetAsync(string token, string? name)
    {
        var key = Hash(token);
        if (_cache.TryGetValue(key, out var hit)) return hit;
        var loaded = await LoadAsync(key);
        if (loaded == null)
        {
            loaded = new Profile { Key = key, Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant(), Name = string.IsNullOrWhiteSpace(name) ? "Wanderer" : name };
            var p = _cache.GetOrAdd(key, loaded);
            if (p == loaded) Save(p);
            return p;
        }
        loaded.Key = key;
        Repair(loaded);
        return _cache.GetOrAdd(key, loaded);
    }

    /// <summary>Fills in anything an older saved profile lacks.</summary>
    private static void Repair(Profile p)
    {
        foreach (var s in Economy.Starters) if (!p.Heroes.Contains(s)) p.Heroes.Add(s);
        foreach (var t in new[] { "battle", "duel" }) p.Rating.TryAdd(t, Economy.StartRating);
        if (!Economy.CharmById.ContainsKey(p.Charm)) p.Charm = "flash";
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
                string json; string name, id; int level, rb, rd, wins, games;
                lock (p)
                {
                    json = JsonSerializer.Serialize(p, Json);
                    name = p.Name; id = p.Id; level = p.Level; rb = p.RatingFor("battle"); rd = p.RatingFor("duel"); wins = p.Wins; games = p.Games;
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
                                UPDATE dbo.MobaProfiles SET Name = @n, Level = @l, RatingBattle = @rb, RatingDuel = @rd, Wins = @w, Games = @g, Data = @d, UpdatedAt = SYSUTCDATETIME() WHERE TokenHash = @k;
                                IF @@ROWCOUNT = 0 INSERT INTO dbo.MobaProfiles (TokenHash, Id, Name, Level, RatingBattle, RatingDuel, Wins, Games, Data) VALUES (@k, @id, @n, @l, @rb, @rd, @w, @g, @d);
                                """, c);
                            cmd.Parameters.AddWithValue("@k", key); cmd.Parameters.AddWithValue("@id", id); cmd.Parameters.AddWithValue("@n", name);
                            cmd.Parameters.AddWithValue("@l", level); cmd.Parameters.AddWithValue("@rb", rb); cmd.Parameters.AddWithValue("@rd", rd);
                            cmd.Parameters.AddWithValue("@w", wins); cmd.Parameters.AddWithValue("@g", games); cmd.Parameters.AddWithValue("@d", json);
                            await cmd.ExecuteNonQueryAsync();
                        }
                        else await File.WriteAllTextAsync(Path.Combine(_dir, key + ".json"), json);
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
