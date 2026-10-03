using Microsoft.Data.SqlClient;
using Starfall.Grove.Moba.Api.Game;

namespace Starfall.Grove.Moba.Api.Services;

/// <summary>
/// Keeps finished matches in SQL Server (tables MobaMatches and MobaMatchPlayers, created on first start). The game
/// runs fine without a database: with no connection string, or when it can't be reached, results are simply not kept.
/// </summary>
public sealed class MatchStore(IConfiguration config, ILogger<MatchStore> log)
{
    private readonly string? _cs = config.GetConnectionString("Moba");
    public bool Enabled => !string.IsNullOrWhiteSpace(_cs);
    public bool Ready { get; private set; }
    public string? LastError { get; private set; }
    /// <summary>Still trying to reach the database for the first time.</summary>
    public bool Connecting { get; private set; }

    private const string Schema = """
        IF OBJECT_ID(N'dbo.MobaMatches', N'U') IS NULL
        CREATE TABLE dbo.MobaMatches (
            Id INT IDENTITY(1,1) PRIMARY KEY,
            RoomCode NVARCHAR(8) NOT NULL,
            Mode INT NOT NULL,
            Map NVARCHAR(32) NOT NULL,
            Winner INT NOT NULL,
            DurationSeconds REAL NOT NULL,
            PlayedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
        );
        IF OBJECT_ID(N'dbo.MobaMatchPlayers', N'U') IS NULL
        CREATE TABLE dbo.MobaMatchPlayers (
            Id INT IDENTITY(1,1) PRIMARY KEY,
            MatchId INT NOT NULL REFERENCES dbo.MobaMatches(Id) ON DELETE CASCADE,
            PlayerName NVARCHAR(32) NOT NULL,
            Hero NVARCHAR(16) NOT NULL,
            Team INT NOT NULL,
            Kills INT NOT NULL, Deaths INT NOT NULL, Assists INT NOT NULL,
            HeroLevel INT NOT NULL, Damage INT NOT NULL, Healing INT NOT NULL,
            IsBot BIT NOT NULL,
            Won BIT NOT NULL
        );
        """;

    public async Task InitAsync()
    {
        if (!Enabled) { log.LogInformation("No ConnectionStrings:Moba set; match history is off."); return; }
        Connecting = true;
        try
        {
            await using var c = new SqlConnection(_cs);
            await c.OpenAsync();
            await using var cmd = new SqlCommand(Schema, c);
            await cmd.ExecuteNonQueryAsync();
            Ready = true;
            log.LogInformation("Match history database ready.");
        }
        catch (Exception e) { LastError = e.Message; log.LogWarning(e, "Database unavailable; match history is off."); }
        finally { Connecting = false; }
    }

    public async Task SaveAsync(string code, int mode, string map, MatchEndDto r)
    {
        if (!Ready) return;
        try
        {
            await using var c = new SqlConnection(_cs);
            await c.OpenAsync();
            await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
            await using var m = new SqlCommand("INSERT INTO dbo.MobaMatches (RoomCode, Mode, Map, Winner, DurationSeconds) OUTPUT INSERTED.Id VALUES (@c, @m, @map, @w, @d)", c, tx);
            m.Parameters.AddWithValue("@c", code); m.Parameters.AddWithValue("@m", mode); m.Parameters.AddWithValue("@map", map);
            m.Parameters.AddWithValue("@w", r.Winner); m.Parameters.AddWithValue("@d", r.Duration);
            var id = (int)(await m.ExecuteScalarAsync())!;
            foreach (var p in r.Players)
            {
                await using var pc = new SqlCommand("""
                    INSERT INTO dbo.MobaMatchPlayers (MatchId, PlayerName, Hero, Team, Kills, Deaths, Assists, HeroLevel, Damage, Healing, IsBot, Won)
                    VALUES (@id, @n, @h, @t, @k, @d, @a, @l, @dmg, @heal, @b, @won)
                    """, c, tx);
                pc.Parameters.AddWithValue("@id", id); pc.Parameters.AddWithValue("@n", p.Name); pc.Parameters.AddWithValue("@h", p.Hero);
                pc.Parameters.AddWithValue("@t", p.Team); pc.Parameters.AddWithValue("@k", p.K); pc.Parameters.AddWithValue("@d", p.D);
                pc.Parameters.AddWithValue("@a", p.A); pc.Parameters.AddWithValue("@l", p.Lv); pc.Parameters.AddWithValue("@dmg", p.Damage);
                pc.Parameters.AddWithValue("@heal", p.Healing); pc.Parameters.AddWithValue("@b", p.Bot); pc.Parameters.AddWithValue("@won", p.Team == r.Winner);
                await pc.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
        }
        catch (Exception e) { LastError = e.Message; log.LogWarning(e, "Could not save match {Code}", code); }
    }

    public async Task<object> RecentAsync(int count = 20)
    {
        if (!Ready) return Array.Empty<object>();
        var list = new List<object>();
        await using var c = new SqlConnection(_cs);
        await c.OpenAsync();
        await using var cmd = new SqlCommand("SELECT TOP (@n) Id, RoomCode, Mode, Map, Winner, DurationSeconds, PlayedAt FROM dbo.MobaMatches ORDER BY Id DESC", c);
        cmd.Parameters.AddWithValue("@n", Math.Clamp(count, 1, 100));
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
            list.Add(new { id = rd.GetInt32(0), room = rd.GetString(1), mode = rd.GetInt32(2), map = rd.GetString(3), winner = rd.GetInt32(4), duration = rd.GetFloat(5), playedAt = rd.GetDateTime(6) });
        return list;
    }

    /// <summary>Win rate per hero over human players' matches.</summary>
    public async Task<object> HeroStatsAsync()
    {
        if (!Ready) return Array.Empty<object>();
        var list = new List<object>();
        await using var c = new SqlConnection(_cs);
        await c.OpenAsync();
        await using var cmd = new SqlCommand("SELECT Hero, COUNT(*), SUM(CAST(Won AS INT)), AVG(CAST(Kills AS FLOAT)) FROM dbo.MobaMatchPlayers WHERE IsBot = 0 GROUP BY Hero ORDER BY Hero", c);
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
            list.Add(new { hero = rd.GetString(0), games = rd.GetInt32(1), wins = rd.GetInt32(2), avgKills = rd.GetDouble(3) });
        return list;
    }
}
