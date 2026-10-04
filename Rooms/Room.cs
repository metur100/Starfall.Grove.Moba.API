using Starfall.Grove.Moba.Api.Game;

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
    public bool Connected => Bot || ConnectionId != null;
    /// <summary>A bot drives this hero: a bot seat, or a human who has been gone too long.</summary>
    public bool BotControl => Bot || (DisconnectedAt is { } t && (DateTime.UtcNow - t).TotalSeconds > Room.TakeoverSeconds);
}

public sealed record RoomPlayerView(string Id, string Name, int Team, bool Ready, bool Bot, bool Connected, string? Hero, bool Locked);
public sealed record RoomView(string Code, string Phase, int Mode, string Map, string Type, string HostId, string You, float Timer, List<RoomPlayerView> Players, int Winner);

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
    /// <summary>Set when a finished match should be written to the database (picked up by the game loop).</summary>
    public MatchEndDto? PendingResult { get; set; }

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
        Players.Add(new RoomPlayer { Id = NewId(), Token = NewId(), Name = name + " (bot)", Team = team, Bot = true, Ready = true });
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
        if (Players.Any(x => x != p && x.Team == p.Team && x.Hero == hero && x.Locked)) return "A teammate already took that hero.";
        p.Hero = hero;
        if (lockIn) p.Locked = true;
        BroadcastRoom();
        if (Players.All(x => x.Locked)) BeginLoading();
        return null;
    }

    private void AutoPick(RoomPlayer p)
    {
        var taken = Players.Where(x => x.Team == p.Team && x.Locked).Select(x => x.Hero).ToHashSet();
        if (p.Hero == null || taken.Contains(p.Hero))
        {
            var free = Catalog.Heroes.Select(h => h.Id).Where(id => !taken.Contains(id)).ToList();
            p.Hero = free[_random.Next(free.Count)];
        }
        p.Locked = true;
    }

    private void BeginLoading()
    {
        foreach (var p in Players.Where(p => !p.Locked)) AutoPick(p);
        var players = Players.Select(p => new MatchPlayer(p.Id, p.Name, p.Hero!, p.Team, p.Bot));
        Match = new Match(Maps.Build(MapId), players, _random.Next());
        _acc = 0; _tick = 0;
        foreach (var p in Players) p.Loaded = p.Bot;
        Go(Phase.Loading, LoadingSeconds);
        var heroes = Match.Heroes.Select(h => new MatchHeroDto(h.PlayerId, h.Name, h.Def.Id, h.Team, h.Id, ById(h.PlayerId)?.Bot ?? true)).ToList();
        var map = Match.MapDto();
        foreach (var p in Humans.Where(h => h.ConnectionId != null))
            _out.Add(new Outgoing(p.ConnectionId!, "matchStart", [new MatchInitDto(map, heroes, p.Id, p.Team, Match.Dt)]));
    }

    public void MarkLoaded(RoomPlayer p)
    {
        if (Phase != Phase.Loading) return;
        p.Loaded = true;
        if (Humans.Where(h => h.Connected).All(h => h.Loaded)) Go(Phase.Starting, StartingSeconds);
    }

    public string? BackToLobby()
    {
        if (Phase != Phase.Ended) return "The match is still going.";
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
            (int)h.DamageDealt, (int)h.Healing, ById(h.PlayerId)?.Bot ?? true)).ToList());
        PendingResult = result;
        foreach (var p in Humans.Where(p => p.ConnectionId != null)) _out.Add(new Outgoing(p.ConnectionId!, "matchEnd", [result]));
        Go(Phase.Ended, 0);
    }

    private void Go(Phase phase, float timer)
    {
        Phase = phase; Timer = timer;
        BroadcastRoom();
    }

    // ───────────────────────────── views

    public RoomView View(RoomPlayer you) => new(Code, PhaseName(Phase), Mode, MapId, Maps.Find(MapId)?.Type ?? "battle", HostId, you.Id, MathF.Ceiling(Timer),
        Players.OrderBy(p => p.Team).Select(p => new RoomPlayerView(p.Id, p.Name, p.Team, p.Ready, p.Bot, p.Connected, p.Hero,
            // Opponents' picks stay hidden until they lock in.
            p.Locked)).ToList(), Winner);

    public void BroadcastRoom()
    {
        foreach (var p in Humans.Where(p => p.ConnectionId != null))
        {
            var v = View(p);
            if (Phase == Phase.HeroSelect)
                v = v with { Players = v.Players.Select(x => x.Team != p.Team && !x.Locked ? x with { Hero = null } : x).ToList() };
            _out.Add(new Outgoing(p.ConnectionId!, "room", [v]));
        }
    }

    /// <summary>Sends a reconnecting player everything they need to pick up where they were.</summary>
    public void Resync(RoomPlayer p)
    {
        BroadcastRoom();
        if (Match != null && p.ConnectionId != null && Phase is Phase.Loading or Phase.Starting or Phase.Playing)
        {
            var heroes = Match.Heroes.Select(h => new MatchHeroDto(h.PlayerId, h.Name, h.Def.Id, h.Team, h.Id, ById(h.PlayerId)?.Bot ?? true)).ToList();
            _out.Add(new Outgoing(p.ConnectionId, "matchStart", [new MatchInitDto(Match.MapDto(), heroes, p.Id, p.Team, Match.Dt)]));
        }
    }

    public static string PhaseName(Phase p) => p switch
    {
        Phase.Lobby => "lobby", Phase.HeroSelect => "heroSelect", Phase.Loading => "loading",
        Phase.Starting => "starting", Phase.Playing => "playing", _ => "ended",
    };

    public static string NewId() => Convert.ToHexString(Guid.NewGuid().ToByteArray())[..16].ToLowerInvariant();
}
