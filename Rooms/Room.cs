using Starfall.Grove.Moba.Api.Game;
using Starfall.Grove.Moba.Api.Services;

namespace Starfall.Grove.Moba.Api.Rooms;

/// <summary>Where a room is. A room only moves forward through these (and from Ended back to Lobby for a rematch).</summary>
public enum Phase { Lobby, HeroSelect, Loading, Starting, Playing, Ended }

public sealed class RoomPlayer
{
    /// <summary>Public id, shown to everyone in the room.</summary>
    public required string Id { get; init; }
    /// <summary>Secret the player's browser keeps, used to reconnect to the same seat.</summary>
    public required string Token { get; init; }
    public required string Name { get; set; }
    public int Team { get; set; }
    public bool Ready { get; set; }
    public bool Bot { get; init; }
    public string? ConnectionId { get; set; }
    public DateTime? DisconnectedAt { get; set; }
    public string? Hero { get; set; }
    public bool Locked { get; set; }
    public bool Loaded { get; set; }
    public float BotPickAt { get; set; }
    /// <summary>The player's profile (null for bots), and what it brings: level, rating, the skin on the chosen hero
    /// and the charm.</summary>
    public Profile? Profile { get; set; }
    public int Level { get; set; } = 1;
    public int Rating { get; set; } = Economy.StartRating;
    public string? Skin { get; set; }
    public string Charm { get; set; } = "flash";
    public bool Connected => Bot || ConnectionId != null;
    /// <summary>A bot drives this hero: a bot seat, or a human who has been gone too long.</summary>
    public bool BotControl => Bot || (DisconnectedAt is { } t && (DateTime.UtcNow - t).TotalSeconds > Room.TakeoverSeconds);
}

public sealed record RoomPlayerView(string Id, string Name, int Team, bool Ready, bool Bot, bool Connected, string? Hero, bool Locked, string? Skin, int Level, string Charm);
public sealed record RoomView(string Code, string Phase, int Mode, string Map, string Type, string HostId, string You, float Timer, List<RoomPlayerView> Players, int Winner, bool Matchmade, bool Public);
/// <summary>An open public room, as the room list shows it.</summary>
public sealed record RoomListing(string Code, string Host, int Mode, string Map, string Type, int Players, int Seats);

/// <summary>What a finished match means for one player's profile (handed to <see cref="Services.Rewards"/>).</summary>
public sealed record RewardJob(Profile Profile, string? ConnectionId, string Hero, int Team, bool Leaver, int K, int D, int A, int Rating);
public sealed record RewardBatch(string Type, int Mode, bool Matchmade, int Winner, bool WithBots, List<RewardJob> Jobs, float[] TeamRating);

/// <summary>An outgoing message: to one connection.</summary>
public sealed record Outgoing(string ConnectionId, string Method, object?[] Args);

/// <summary>
/// A private room: its seats, its phase and (once heroes are chosen) its match. Every method expects the caller to
/// hold <see cref="Lock"/>; messages are collected into an outbox and sent after the lock is released.
/// </summary>
public sealed class Room
{
    public const float HeroSelectSeconds = 40, LoadingSeconds = 15, StartingSeconds = 3, TakeoverSeconds = 45;
    public const int SnapshotEvery = 2;

    public readonly object Lock = new();
    public required string Code { get; init; }
    public string HostId { get; set; } = "";
    public int Mode { get; set; } = 3;
    public string MapId { get; set; } = "glade";
    public Phase Phase { get; private set; } = Phase.Lobby;
    public float Timer { get; private set; }
    public List<RoomPlayer> Players { get; } = [];
    public Match? Match { get; private set; }
    public DateTime EmptySince { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; } = DateTime.UtcNow;
    public int Winner { get; private set; }
    /// <summary>Made by matchmaking: no lobby, no host, and it pays (and rates) in full.</summary>
    public bool Matchmade { get; private set; }
    /// <summary>A custom room anyone can find in the room list.</summary>
    public bool Public { get; set; }
    public string Type => Maps.Find(MapId)?.Type ?? "battle";
    /// <summary>Set when a finished match should be written to the database (picked up by the game loop).</summary>
    public MatchEndDto? PendingResult { get; set; }
    /// <summary>Set when a finished match should pay its players (picked up by the game loop).</summary>
    public RewardBatch? PendingRewards { get; set; }

    private readonly List<Outgoing> _out = [];
    private float _acc;
    private int _tick;
    private readonly Random _random = new();

    public List<Outgoing> TakeOutbox() { var o = _out.ToList(); _out.Clear(); return o; }
    public IEnumerable<RoomPlayer> Humans => Players.Where(p => !p.Bot);
    public RoomPlayer? ByToken(string token) => Players.FirstOrDefault(p => p.Token == token);
    public RoomPlayer? ById(string id) => Players.FirstOrDefault(p => p.Id == id);

    // ───────────────────────────── lobby

    public string? AddHuman(RoomPlayer p)
    {
        if (Phase != Phase.Lobby) return "This match has already started.";
        var team = TeamWithRoom();
        if (team == 0) return "This room is full.";
        p.Team = team;
        Players.Add(p);
        if (Humans.Count() == 1) HostId = p.Id;
        BroadcastRoom();
        return null;
    }

    private int TeamWithRoom()
    {
        int c1 = Players.Count(p => p.Team == 1), c2 = Players.Count(p => p.Team == 2);
        if (c1 >= Mode && c2 >= Mode) return 0;
        if (c1 >= Mode) return 2;
        if (c2 >= Mode) return 1;
        return c1 <= c2 ? 1 : 2;
    }

    public string? AddBot(int team)
    {
        if (Phase != Phase.Lobby) return "Not in the lobby.";
        if (team is not (1 or 2)) team = TeamWithRoom();
        if (team == 0 || Players.Count(p => p.Team == team) >= Mode) return "That team is full.";
        var names = new[] { "Bramble", "Thistle", "Pip", "Moss", "Juniper", "Sorrel", "Fern", "Rowan", "Clove", "Wisp" };
        var used = Players.Select(p => p.Name).ToHashSet();
        var name = names.FirstOrDefault(n => !used.Contains(n + " (bot)")) ?? "Sprout";
        // Bots are about as experienced as the players they meet.
        var level = Math.Max(1, (int)Humans.Select(h => h.Level).DefaultIfEmpty(1).Average() + _random.Next(-2, 3));
        Players.Add(new RoomPlayer
        {
            Id = NewId(), Token = NewId(), Name = name + " (bot)", Team = team, Bot = true, Ready = true, Level = level,
            Charm = Economy.Charms[_random.Next(Economy.Charms.Length)].Id,
        });
        BroadcastRoom();
        return null;
    }

    public void FillWithBots()
    {
        foreach (var team in new[] { 1, 2 })
            while (Players.Count(p => p.Team == team) < Mode) AddBot(team);
    }

    public void Remove(RoomPlayer p)
    {
        Players.Remove(p);
        if (HostId == p.Id) HostId = Humans.FirstOrDefault(h => h.Connected)?.Id ?? Humans.FirstOrDefault()?.Id ?? "";
        if (Phase is Phase.Lobby) BroadcastRoom();
    }

    public string? SwitchTeam(RoomPlayer p)
    {
        if (Phase != Phase.Lobby) return "Not in the lobby.";
        var other = 3 - p.Team;
        if (Players.Count(x => x.Team == other) >= Mode)
        {
            // Swap with a bot on the other side if there is one.
            var bot = Players.FirstOrDefault(x => x.Team == other && x.Bot);
            if (bot == null) return "The other team is full.";
            bot.Team = p.Team;
        }
        p.Team = other; p.Ready = false;
        BroadcastRoom();
        return null;
    }

    public string? SetMode(int mode)
    {
        if (Phase != Phase.Lobby) return "Not in the lobby.";
        if (mode is < 1 or > 3) return "Mode must be 1v1, 2v2 or 3v3.";
        if (Players.Count(p => p.Team == 1) > mode || Players.Count(p => p.Team == 2) > mode)
        {
            // Drop bots first to make the room fit.
            foreach (var team in new[] { 1, 2 })
                while (Players.Count(p => p.Team == team) > mode && Players.LastOrDefault(p => p.Team == team && p.Bot) is { } bot) Players.Remove(bot);
            if (Players.Count(p => p.Team == 1) > mode || Players.Count(p => p.Team == 2) > mode) return "Too many players for that mode.";
        }
        Mode = mode;
        BroadcastRoom();
        return null;
    }

    public string? SetMap(string map)
    {
        if (Phase != Phase.Lobby) return "Not in the lobby.";
        if (!Maps.List.Any(m => m.Id == map)) return "Unknown map.";
        MapId = map;
        BroadcastRoom();
        return null;
    }

    /// <summary>Sets up a room made by matchmaking: the players (already on their teams), bots in any empty seats, and
    /// straight on to hero select.</summary>
    public void BeginMatchmade(int mode, string map, IEnumerable<RoomPlayer> humans)
    {
        Matchmade = true; Mode = mode; MapId = map; HostId = "";
        foreach (var p in humans) { p.Ready = true; Players.Add(p); }
        FillWithBots();
        StartHeroSelect();
    }

    public string? StartHeroSelect()
    {
        if (Phase != Phase.Lobby) return "Not in the lobby.";
        if (Players.Count(p => p.Team == 1) != Mode || Players.Count(p => p.Team == 2) != Mode) return $"Both teams need {Mode} players (add bots to fill seats).";
        if (Humans.Any(p => !p.Ready && p.Id != HostId)) return "Everyone must be ready.";
        if (Humans.Any(p => !p.Connected)) return "Someone is disconnected.";
        foreach (var p in Players) { p.Hero = null; p.Locked = false; p.Loaded = false; p.BotPickAt = 2 + (float)_random.NextDouble() * 6; }
        Go(Phase.HeroSelect, HeroSelectSeconds);
        return null;
    }

    // ───────────────────────────── hero select

    public string? Pick(RoomPlayer p, string hero, bool lockIn)
    {
        if (Phase != Phase.HeroSelect) return "Not choosing heroes now.";
        if (!Catalog.ById.ContainsKey(hero)) return "Unknown hero.";
        if (p.Locked) return "Already locked in.";
        if (!CanPlay(p, hero)) return "You don't own that hero yet.";
        if (Players.Any(x => x != p && x.Team == p.Team && x.Hero == hero && x.Locked)) return "A teammate already took that hero.";
        if (p.Hero != hero) p.Skin = EquippedSkin(p, hero);
        p.Hero = hero;
        if (lockIn) p.Locked = true;
        BroadcastRoom();
        if (Players.All(x => x.Locked)) BeginLoading();
        return null;
    }

    private void AutoPick(RoomPlayer p)
    {
        var taken = Players.Where(x => x.Team == p.Team && x.Locked).Select(x => x.Hero).ToHashSet();
        if (p.Hero == null || taken.Contains(p.Hero) || !CanPlay(p, p.Hero))
        {
            var free = Catalog.Heroes.Select(h => h.Id).Where(id => !taken.Contains(id)).ToList();
            var mine = free.Where(id => CanPlay(p, id)).ToList();
            if (mine.Count > 0) free = mine;
            p.Hero = free[_random.Next(free.Count)];
            p.Skin = EquippedSkin(p, p.Hero);
        }
        // Bots now and then show off a skin.
        if (p.Bot && p.Skin == null && _random.NextDouble() < .4)
        {
            var skins = Economy.Skins.Where(s => s.Hero == p.Hero).ToList();
            p.Skin = skins[_random.Next(skins.Count)].Id;
        }
        p.Locked = true;
    }

    /// <summary>Whether a player may pick a hero: bots and players without a profile always; others when they own it
    /// or it is free this week.</summary>
    private static bool CanPlay(RoomPlayer p, string hero)
    {
        if (p.Profile is not { } prof) return true;
        lock (prof) return prof.Playable(DateTime.UtcNow).Contains(hero);
    }

    private static string? EquippedSkin(RoomPlayer p, string hero)
    {
        if (p.Profile is not { } prof) return null;
        lock (prof) return prof.Equipped.TryGetValue(hero, out var s) && prof.Skins.Contains(s) ? s : null;
    }

    /// <summary>Wears an owned skin (null: the hero's own look) on the hero being picked.</summary>
    public string? SetSkin(RoomPlayer p, string? skin)
    {
        if (Phase != Phase.HeroSelect) return "Not choosing heroes now.";
        if (p.Hero == null) return "Pick a hero first.";
        if (string.IsNullOrEmpty(skin)) { p.Skin = null; BroadcastRoom(); return null; }
        if (!Economy.SkinById.TryGetValue(skin, out var def) || def.Hero != p.Hero) return "That skin isn't for this hero.";
        if (p.Profile is { } prof) lock (prof) if (!prof.Skins.Contains(skin)) return "You don't own that skin.";
        p.Skin = skin;
        BroadcastRoom();
        return null;
    }

    public string? SetCharm(RoomPlayer p, string charm)
    {
        if (Phase is not (Phase.Lobby or Phase.HeroSelect)) return "Too late to change your charm.";
        if (!Economy.CharmById.ContainsKey(charm)) return "Unknown charm.";
        p.Charm = charm;
        BroadcastRoom();
        return null;
    }

    private void BeginLoading()
    {
        foreach (var p in Players.Where(p => !p.Locked)) AutoPick(p);
        var players = Players.Select(p => new MatchPlayer(p.Id, p.Name, p.Hero!, p.Team, p.Bot, p.Charm));
        Match = new Match(Maps.Build(MapId), players, _random.Next());
        _acc = 0; _tick = 0;
        foreach (var p in Players) p.Loaded = p.Bot;
        Go(Phase.Loading, LoadingSeconds);
        var heroes = HeroDtos();
        var map = Match.MapDto();
        foreach (var p in Humans.Where(h => h.ConnectionId != null))
            _out.Add(new Outgoing(p.ConnectionId!, "matchStart", [new MatchInitDto(map, heroes, p.Id, p.Team, Match.Dt, Matchmade)]));
    }

    private List<MatchHeroDto> HeroDtos() => Match!.Heroes.Select(h =>
    {
        var rp = ById(h.PlayerId);
        return new MatchHeroDto(h.PlayerId, h.Name, h.Def.Id, h.Team, h.Id, rp?.Bot ?? true, rp?.Skin, rp?.Level ?? 1);
    }).ToList();

    public void MarkLoaded(RoomPlayer p)
    {
        if (Phase != Phase.Loading) return;
        p.Loaded = true;
        if (Humans.Where(h => h.Connected).All(h => h.Loaded)) Go(Phase.Starting, StartingSeconds);
    }

    public string? BackToLobby()
    {
        if (Phase != Phase.Ended) return "The match is still going.";
        if (Matchmade) return "This match was made by matchmaking: find a new one from the menu.";
        Match = null; Winner = 0;
        foreach (var p in Players) { p.Ready = p.Bot; p.Hero = null; p.Locked = false; p.Loaded = false; }
        // Players who left during the match give up their seat now.
        Players.RemoveAll(p => !p.Bot && !p.Connected);
        if (ById(HostId) == null) HostId = Humans.FirstOrDefault()?.Id ?? "";
        Go(Phase.Lobby, 0);
        return null;
    }

    // ───────────────────────────── the loop

    /// <summary>Advances the room by <paramref name="dt"/> real seconds.</summary>
    public void Update(float dt)
    {
        if (Timer > 0) Timer = MathF.Max(0, Timer - dt);
        var lobbyKick = DateTime.UtcNow.AddSeconds(-20);
        switch (Phase)
        {
            case Phase.Lobby:
                var gone = Humans.Where(p => p.DisconnectedAt < lobbyKick).ToList();
                foreach (var p in gone) Remove(p);
                break;
            case Phase.HeroSelect:
                foreach (var p in Players.Where(p => p.Bot && !p.Locked && HeroSelectSeconds - Timer > p.BotPickAt).ToList()) { AutoPick(p); BroadcastRoom(); }
                if (Players.All(p => p.Locked) || Timer <= 0) BeginLoading();
                break;
            case Phase.Loading:
                if (Timer <= 0) Go(Phase.Starting, StartingSeconds);
                break;
            case Phase.Starting:
                if (Timer <= 0) Go(Phase.Playing, 0);
                else if (_tick++ % 15 == 0) SendSnapshots(Match!.TakeFx());
                break;
            case Phase.Playing:
                PlayMatch(dt);
                break;
        }
    }

    private void PlayMatch(float dt)
    {
        var m = Match!;
        _acc = MathF.Min(_acc + dt, Match.Dt * 4);
        while (_acc >= Match.Dt && m.Winner == 0)
        {
            _acc -= Match.Dt;
            foreach (var p in Players)
            {
                var h = m.HeroOf(p.Id);
                if (h == null) continue;
                if (p.BotControl) m.DriveBot(h);
                else if (!p.Connected) { h.MoveDir = Vec.Zero; h.AttackHeld = false; }
            }
            m.Tick();
            _tick++;
            if (_tick % SnapshotEvery == 0) SendSnapshots(m.TakeFx());
        }
        if (m.Winner != 0) EndMatch();
    }

    private void SendSnapshots(List<FxDto> fx)
    {
        var m = Match!;
        var snaps = new[] { null, m.Snapshot(1, fx), m.Snapshot(2, fx) };
        foreach (var p in Humans)
        {
            if (p.ConnectionId == null) continue;
            var h = m.HeroOf(p.Id);
            if (h == null) continue;
            _out.Add(new Outgoing(p.ConnectionId, "snap", [snaps[p.Team], m.Me(h)]));
        }
    }

    private void EndMatch()
    {
        var m = Match!;
        SendSnapshots(m.TakeFx());
        Winner = m.Winner;
        var result = new MatchEndDto(m.Winner, MathF.Round(m.Time, 1), m.Heroes.Select(h => new MatchEndPlayerDto(
            h.PlayerId, h.Name, h.Def.Id, h.Team, h.Kills, h.Deaths, h.Assists, h.Level, (int)h.GoldBank,
            (int)h.DamageDealt, (int)h.Healing, ById(h.PlayerId)?.Bot ?? true, (int)h.HeroDamage)).ToList());
        PendingResult = result;
        foreach (var p in Humans.Where(p => p.ConnectionId != null)) _out.Add(new Outgoing(p.ConnectionId!, "matchEnd", [result]));

        // Coins, experience and rating for everyone with a profile. Bots count as slightly below a new player.
        float TeamRating(int team) => Players.Where(p => p.Team == team).Select(p => p.Profile != null ? p.Rating : Economy.StartRating - 50f).DefaultIfEmpty(Economy.StartRating).Average();
        var jobs = Humans.Where(p => p.Profile != null).Select(p =>
        {
            var h = m.HeroOf(p.Id)!;
            return new RewardJob(p.Profile!, p.ConnectionId, h.Def.Id, p.Team, p.BotControl, h.Kills, h.Deaths, h.Assists, p.Rating);
        }).ToList();
        if (jobs.Count > 0) PendingRewards = new RewardBatch(Type, Mode, Matchmade, m.Winner, Players.Any(p => p.Bot), jobs, [0, TeamRating(1), TeamRating(2)]);
        Go(Phase.Ended, 0);
    }

    private void Go(Phase phase, float timer)
    {
        Phase = phase; Timer = timer;
        BroadcastRoom();
    }

    // ───────────────────────────── views

    public RoomView View(RoomPlayer you) => new(Code, PhaseName(Phase), Mode, MapId, Type, HostId, you.Id, MathF.Ceiling(Timer),
        Players.OrderBy(p => p.Team).Select(p => new RoomPlayerView(p.Id, p.Name, p.Team, p.Ready, p.Bot, p.Connected, p.Hero,
            p.Locked, p.Skin, p.Level, p.Charm)).ToList(), Winner, Matchmade, Public);

    public RoomListing? Listing()
    {
        if (!Public || Matchmade || Phase != Phase.Lobby) return null;
        var humans = Humans.Count();
        if (humans >= Mode * 2) return null;
        return new RoomListing(Code, ById(HostId)?.Name ?? "?", Mode, MapId, Type, humans, Mode * 2);
    }

    public void BroadcastRoom()
    {
        foreach (var p in Humans.Where(p => p.ConnectionId != null))
        {
            var v = View(p);
            // Opponents' picks stay hidden until they lock in.
            if (Phase == Phase.HeroSelect)
                v = v with { Players = v.Players.Select(x => x.Team != p.Team && !x.Locked ? x with { Hero = null, Skin = null } : x).ToList() };
            _out.Add(new Outgoing(p.ConnectionId!, "room", [v]));
        }
    }

    /// <summary>Sends a reconnecting player everything they need to pick up where they were.</summary>
    public void Resync(RoomPlayer p)
    {
        BroadcastRoom();
        if (Match != null && p.ConnectionId != null && Phase is Phase.Loading or Phase.Starting or Phase.Playing)
            _out.Add(new Outgoing(p.ConnectionId, "matchStart", [new MatchInitDto(Match.MapDto(), HeroDtos(), p.Id, p.Team, Match.Dt, Matchmade)]));
    }

    public static string PhaseName(Phase p) => p switch
    {
        Phase.Lobby => "lobby", Phase.HeroSelect => "heroSelect", Phase.Loading => "loading",
        Phase.Starting => "starting", Phase.Playing => "playing", _ => "ended",
    };

    public static string NewId() => Convert.ToHexString(Guid.NewGuid().ToByteArray())[..16].ToLowerInvariant();
}
