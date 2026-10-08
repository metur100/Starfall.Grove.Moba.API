namespace Starfall.Grove.Moba.Api.Game;

/// <summary>A hero's look that can be bought with coins. What it looks like is up to the client (by id).</summary>
public sealed record SkinDef(string Id, string Hero, string Name, string Tier, int Price);
/// <summary>A small extra spell every hero brings to a match, chosen in hero select.</summary>
public sealed record CharmDef(string Id, string Name, string Text, float Cooldown, float DuelCooldown);
public sealed record RankDef(string Name, int Min);
/// <summary>A daily quest: what to do (Goal times), and the coins it pays once done.</summary>
public sealed record QuestDef(string Id, string Text, int Goal, int Coins);

/// <summary>
/// Coins, player levels, ranks and the shop: what heroes and skins cost, what a match pays and how a player levels up.
/// Players start with three heroes and some coins; the others (and every skin) are bought with coins earned by playing.
/// </summary>
public static class Economy
{
    public const int StartCoins = 600;
    public const int StartRating = 1000;
    public static readonly string[] Starters = ["mira", "kael", "wren"];

    public static readonly Dictionary<string, int> HeroPrices = new()
    {
        ["mira"] = 0, ["kael"] = 0, ["wren"] = 0, ["lyra"] = 1200, ["elara"] = 1200, ["riven"] = 1600,
    };

    public const int Rare = 600, Epic = 900, Legendary = 1500;
    public static readonly SkinDef[] Skins =
    [
        new("mira_moon", "mira", "Moonlit Mira", "rare", Rare),
        new("mira_ember", "mira", "Ember Witch", "epic", Epic),
        new("mira_nebula", "mira", "Nebula Empress", "legendary", Legendary),
        new("kael_oak", "kael", "Oakheart", "rare", Rare),
        new("kael_frost", "kael", "Frostguard", "epic", Epic),
        new("kael_sun", "kael", "Sunforged Paladin", "legendary", Legendary),
        new("lyra_spring", "lyra", "Spring Thaw", "rare", Rare),
        new("lyra_aurora", "lyra", "Aurora", "epic", Epic),
        new("lyra_queen", "lyra", "Winter Queen", "legendary", Legendary),
        new("riven_crimson", "riven", "Crimson Fang", "rare", Rare),
        new("riven_ghost", "riven", "Ghost Lantern", "epic", Epic),
        new("riven_eclipse", "riven", "Eclipse", "legendary", Legendary),
        new("wren_autumn", "wren", "Autumn Ranger", "rare", Rare),
        new("wren_snow", "wren", "Snowtrail", "epic", Epic),
        new("wren_hunt", "wren", "Wild Hunt", "legendary", Legendary),
        new("elara_blossom", "elara", "Cherry Blossom", "rare", Rare),
        new("elara_shroom", "elara", "Toadstool Witch", "epic", Epic),
        new("elara_ancient", "elara", "Ancient Oak", "legendary", Legendary),
    ];
    public static readonly Dictionary<string, SkinDef> SkinById = Skins.ToDictionary(s => s.Id);

    public static readonly CharmDef[] Charms =
    [
        new("flash", "Flash", "Blink a short way toward where you aim. Over walls of thorn, out of a stun's reach.", 90, 40),
        new("heal", "Heal", "Heal yourself and the most hurt ally nearby for 15% of their health, and run faster for a moment.", 100, 45),
        new("ghost", "Ghost", "Run 32% faster for 5 seconds.", 75, 35),
        new("barrier", "Barrier", "A shield of starlight that soaks up 20% of your health for 2.5 seconds.", 90, 45),
    ];
    public static readonly Dictionary<string, CharmDef> CharmById = Charms.ToDictionary(c => c.Id);

    public static readonly RankDef[] Ranks =
    [
        new("Seedling", 0), new("Sapling", 950), new("Grovekeeper", 1100), new("Starlit", 1250), new("Astral", 1400), new("Celestial", 1600),
    ];
    public static string RankOf(int rating) => Ranks.Last(r => rating >= r.Min).Name;

    public const int MaxLevel = 100;
    /// <summary>Player experience needed to go from <paramref name="level"/> to the next.</summary>
    public static int XpToNext(int level) => 300 + 100 * (level - 1);
    /// <summary>Coins for reaching a level: more at every fifth.</summary>
    public static int LevelReward(int level) => level % 5 == 0 ? 400 : 100;

    /// <summary>
    /// The heroes everyone can play this week without owning them: one of the heroes that cost coins, changing every
    /// Monday (UTC).
    /// </summary>
    public static string[] Rotation(DateTime utc)
    {
        var paid = HeroPrices.Where(kv => kv.Value > 0).Select(kv => kv.Key).OrderBy(k => k).ToArray();
        var week = (int)((utc.Date - new DateTime(2024, 1, 1)).TotalDays / 7);
        return [paid[week % paid.Length]];
    }

    // ───────────────────────────── daily quests

    public const int QuestsPerDay = 3;
    public static readonly QuestDef[] Quests =
    [
        new("play3", "Play 3 matches", 3, 100),
        new("win2", "Win 2 matches", 2, 150),
        new("takedowns15", "Get 15 takedowns", 15, 120),
        new("assists8", "Get 8 assists", 8, 100),
        new("battle2", "Play 2 battles", 2, 100),
        new("duel2", "Play 2 duels", 2, 100),
        new("damage", "Deal 8,000 damage to heroes", 8000, 120),
    ];
    public static readonly Dictionary<string, QuestDef> QuestById = Quests.ToDictionary(q => q.Id);

    /// <summary>The player's three quests for a day (UTC): the same all day, different for every player and day.</summary>
    public static QuestDef[] DailyQuests(string profileId, DateTime utc)
    {
        // A stable hash: string.GetHashCode changes with every start of the server.
        var seed = 17;
        foreach (var c in profileId + utc.ToString("yyyy-MM-dd")) seed = unchecked(seed * 31 + c);
        var rng = new Random(seed);
        return [.. Quests.OrderBy(_ => rng.Next()).Take(QuestsPerDay)];
    }

    /// <summary>How far one match takes a quest.</summary>
    public static int QuestStep(string quest, bool won, bool duel, int kills, int assists, int heroDamage) => quest switch
    {
        "play3" => 1,
        "win2" => won ? 1 : 0,
        "takedowns15" => kills + assists,
        "assists8" => assists,
        "battle2" => duel ? 0 : 1,
        "duel2" => duel ? 1 : 0,
        "damage" => heroDamage,
        _ => 0,
    };

    // ───────────────────────────── match rewards

    public const int FirstWinBonus = 150;

    /// <summary>
    /// Coins and experience for one player's match. Winning pays more than losing; kills and assists add a little.
    /// Matches from matchmaking pay in full; custom rooms and practice against bots (<paramref name="cut"/> names which)
    /// three quarters. Leaving early pays nothing.
    /// </summary>
    public static (int Coins, int Xp, List<RewardLine> Lines) MatchReward(bool won, bool duel, string? cut, int kills, int assists, bool firstWin, bool leaver)
    {
        var lines = new List<RewardLine>();
        if (leaver) { lines.Add(new("Left the match", 0)); return (0, 0, lines); }
        var baseCoins = duel ? (won ? 90 : 35) : (won ? 120 : 50);
        lines.Add(new(won ? "Victory" : "Defeat", baseCoins));
        var play = Math.Min(40, kills * 4 + assists * 2);
        if (play > 0) lines.Add(new($"Takedowns ({kills} + {assists})", play));
        var coins = baseCoins + play;
        if (cut != null)
        {
            var less = coins - (int)MathF.Round(coins * .75f);
            if (less > 0) { lines.Add(new(cut, -less)); coins -= less; }
        }
        if (firstWin) { lines.Add(new("First win of the day", FirstWinBonus)); coins += FirstWinBonus; }
        var xp = (won ? 100 : 60) + Math.Min(30, kills * 3 + assists * 2);
        if (cut != null) xp = (int)MathF.Round(xp * .75f);
        return (coins, xp, lines);
    }

    /// <summary>The rating change for a team: Elo against the other team's average, smaller when bots took part.</summary>
    public static int RatingChange(float mine, float theirs, bool won, bool withBots)
    {
        var expected = 1 / (1 + MathF.Pow(10, (theirs - mine) / 400));
        var k = withBots ? 16 : 32;
        return (int)MathF.Round(k * ((won ? 1 : 0) - expected));
    }
}

public sealed record RewardLine(string Label, int Coins);
