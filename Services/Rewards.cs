using Starfall.Grove.Moba.Api.Game;
using Starfall.Grove.Moba.Api.Rooms;

namespace Starfall.Grove.Moba.Api.Services;

/// <summary>What a player got from a match, for the result screen: the coins line by line, the experience (and any
/// levels gained) and, from matchmaking, the rating change.</summary>
public sealed record RewardsDto(
    bool Won, int Coins, int Xp, List<RewardLine> Lines, int LevelFrom, int XpFrom, int XpNextFrom, int LevelTo, int XpTo, int XpNextTo,
    bool Ranked, string Type, int RatingDelta, int Rating, string Rank, string RankFrom);

/// <summary>Pays out a finished match: coins, experience, levels, rating and stats, then tells each player.</summary>
public sealed class Rewards(ProfileStore store, Outbox outbox, ILogger<Rewards> log)
{
    public void Grant(RewardBatch b)
    {
        var duel = b.Type == "duel";
        var now = DateTime.UtcNow;
        var messages = new List<Outgoing>();
        foreach (var j in b.Jobs)
        {
            try
            {
                var p = j.Profile;
                var won = j.Team == b.Winner;
                RewardsDto dto;
                lock (p)
                {
                    var firstWin = won && !j.Leaver && p.FirstWinReady(now);
                    var (coins, xp, lines) = Economy.MatchReward(won, duel, b.Matchmade, j.K, j.A, firstWin, j.Leaver);
                    if (firstWin) p.FirstWinDay = now.ToString("yyyy-MM-dd");
                    int lvFrom = p.Level, xpFrom = p.Xp, nextFrom = Economy.XpToNext(p.Level);
                    p.Xp += xp;
                    while (p.Level < Economy.MaxLevel && p.Xp >= Economy.XpToNext(p.Level))
                    {
                        p.Xp -= Economy.XpToNext(p.Level);
                        p.Level++;
                        var bonus = Economy.LevelReward(p.Level);
                        coins += bonus;
                        lines.Add(new($"Reached level {p.Level}", bonus));
                    }
                    p.Coins += coins;

                    // Only matchmaking moves the rating; a leaver always loses it.
                    var before = p.RatingFor(b.Type);
                    var delta = 0;
                    if (b.Matchmade)
                    {
                        delta = Economy.RatingChange(b.TeamRating[j.Team], b.TeamRating[3 - j.Team], won && !j.Leaver, b.WithBots);
                        p.Rating[b.Type] = Math.Max(0, before + delta);
                    }

                    p.Games++;
                    if (won) p.Wins++;
                    p.Kills += j.K; p.Deaths += j.D; p.Assists += j.A;
                    if (!p.HeroStats.TryGetValue(j.Hero, out var hs)) p.HeroStats[j.Hero] = hs = [0, 0];
                    hs[0]++; if (won) hs[1]++;
                    p.Recent.Insert(0, new MatchRecord(j.Hero, b.Type, b.Mode, won, j.K, j.D, j.A, coins, b.Matchmade, now));
                    if (p.Recent.Count > 12) p.Recent.RemoveRange(12, p.Recent.Count - 12);

                    dto = new RewardsDto(won, coins, xp, lines, lvFrom, xpFrom, nextFrom, p.Level, p.Xp, Economy.XpToNext(p.Level),
                        b.Matchmade, b.Type, delta, p.RatingFor(b.Type), Economy.RankOf(p.RatingFor(b.Type)), Economy.RankOf(before));
                }
                store.Save(p);
                if (j.ConnectionId != null)
                {
                    messages.Add(new Outgoing(j.ConnectionId, "rewards", [dto]));
                    messages.Add(new Outgoing(j.ConnectionId, "profile", [ProfileDto.Of(p)]));
                }
            }
            catch (Exception e) { log.LogError(e, "Could not pay out a match to {Id}", j.Profile.Id); }
        }
        outbox.Send(messages);
    }
}
