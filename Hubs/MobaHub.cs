using Microsoft.AspNetCore.SignalR;
using Starfall.Grove.Moba.Api.Game;
using Starfall.Grove.Moba.Api.Rooms;
using Starfall.Grove.Moba.Api.Services;

namespace Starfall.Grove.Moba.Api.Hubs;

public sealed record JoinResult(bool Ok, string? Error, string? Code, string? PlayerId);
public sealed record HelloResult(bool Ok, string? Error, ProfileDto? Profile);
public sealed record ShopResult(string? Error, ProfileDto? Profile);
/// <summary>What can be bought and chosen: hero prices, skins, charms and the rank ladder.</summary>
public sealed record ShopDto(Dictionary<string, int> HeroPrices, SkinDef[] Skins, CharmDef[] Charms, RankDef[] Ranks, int FirstWinBonus, string[] Starters);
public sealed record CatalogDto(HeroDef[] Heroes, UpgradeOption[][] BasicTiers, UpgradeOption[][] AbilityTiers, int[] BasicCost, int[] AbilityCost, int[] UltCost, int UltLevel, int MaxLevel, object[] Maps, ShopDto Shop);

/// <summary>
/// The one SignalR hub. Clients call these methods; the server answers through "room", "matchStart", "snap",
/// "matchEnd", "rewards", "profile", "queue" and "matchFound" messages (most sent by <see cref="GameLoop"/> from each
/// room's outbox).
///
/// A browser keeps a secret token. <see cref="Hello"/> ties the connection to that token's profile (coins, heroes,
/// skins, level, rating); everything that touches the profile works on the connection's own one.
/// </summary>
public sealed class MobaHub(RoomManager rooms, Outbox outbox, ProfileStore profiles, Matchmaker matchmaker) : Hub
{
    private static string CleanName(string? name)
    {
        var n = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (n.Length > 16) n = n[..16];
        return n.Length == 0 ? "Wanderer" : n;
    }
    private static bool BadToken(string? token) => string.IsNullOrWhiteSpace(token) || token.Length is < 16 or > 64;

    public static CatalogDto BuildCatalog() => new(Catalog.Heroes, Upgrades.BasicTiers, Upgrades.AbilityTiers, Upgrades.BasicCost, Upgrades.AbilityCost,
        Upgrades.UltCost, Catalog.UltLevel, Catalog.MaxLevel, Maps.List.Select(m => (object)new { m.Id, m.Name, m.Theme, m.Type, m.Lanes, m.Blurb }).ToArray(),
        new ShopDto(Economy.HeroPrices, Economy.Skins, Economy.Charms, Economy.Ranks, Economy.FirstWinBonus, Economy.Starters));

    public CatalogDto GetCatalog() => BuildCatalog();

    public long Ping(long t) => t;

    // ───────────────────────────── profile and shop

    private string? Token => Context.Items.TryGetValue("token", out var t) ? t as string : null;
    private Profile? Me => Token is { } t ? profiles.Cached(t) : null;

    /// <summary>Ties this connection to the player's profile (made on first visit) and returns it.</summary>
    public async Task<HelloResult> Hello(string token, string name)
    {
        if (BadToken(token)) return new(false, "Bad token.", null);
        try
        {
            var p = await profiles.GetAsync(token, CleanName(name));
            Context.Items["token"] = token;
            return new(true, null, ProfileDto.Of(p));
        }
        catch (Exception)
        {
            return new(false, "Couldn't load your profile. Try again in a moment.", null);
        }
    }

    public ProfileDto? GetProfile() => Me is { } p ? ProfileDto.Of(p) : null;

    /// <summary>Changes something on the profile under its lock; saves and returns it when that went well.</summary>
    private ShopResult Change(Func<Profile, string?> change)
    {
        if (Me is not { } p) return new("Not signed in yet.", null);
        string? err;
        lock (p) err = change(p);
        if (err != null) return new(err, null);
        profiles.Save(p);
        return new(null, ProfileDto.Of(p));
    }

    public ShopResult SetName(string name) => Change(p => { p.Name = CleanName(name); return null; });

    public ShopResult BuyHero(string hero) => Change(p =>
    {
        if (!Economy.HeroPrices.TryGetValue(hero, out var price)) return "Unknown hero.";
        if (p.Heroes.Contains(hero)) return "You already own this hero.";
        if (p.Coins < price) return "Not enough coins.";
        p.Coins -= price;
        p.Heroes.Add(hero);
        return null;
    });

    public ShopResult BuySkin(string skin) => Change(p =>
    {
        if (!Economy.SkinById.TryGetValue(skin, out var s)) return "Unknown skin.";
        if (p.Skins.Contains(skin)) return "You already own this skin.";
        if (p.Coins < s.Price) return "Not enough coins.";
        p.Coins -= s.Price;
        p.Skins.Add(skin);
        p.Equipped[s.Hero] = skin;
        return null;
    });

    /// <summary>Which skin a hero wears from now on (empty: its own look).</summary>
    public ShopResult EquipSkin(string hero, string? skin) => Change(p =>
    {
        if (!Catalog.ById.ContainsKey(hero)) return "Unknown hero.";
        if (string.IsNullOrEmpty(skin)) { p.Equipped.Remove(hero); return null; }
        if (!Economy.SkinById.TryGetValue(skin, out var s) || s.Hero != hero) return "That skin isn't for this hero.";
        if (!p.Skins.Contains(skin)) return "You don't own this skin.";
        p.Equipped[hero] = skin;
        return null;
    });

    /// <summary>The charm to bring: saved on the profile, and used at once when choosing heroes in a room.</summary>
    public ShopResult SetCharm(string charm)
    {
        if (!Economy.CharmById.ContainsKey(charm)) return new("Unknown charm.", null);
        var r = Change(p => { p.Charm = charm; return null; });
        if (r.Error == null && rooms.SeatOf(Context.ConnectionId) is var (room, seat))
            lock (room.Lock) { room.SetCharm(seat, charm); outbox.Send(room.TakeOutbox()); }
        return r;
    }

    public Task<List<LeaderRow>> Leaderboard(string type) => profiles.LeaderboardAsync(type == "duel" ? "duel" : "battle");

    // ───────────────────────────── matchmaking

    /// <summary>Looks for a match of the given type and size. Leaves any room the player is in.</summary>
    public string? FindMatch(string type, int mode)
    {
        if (Token is not { } token || Me is not { } p) return "Not signed in yet.";
        LeaveCurrent();
        return matchmaker.Join(Context.ConnectionId, token, p, type, mode);
    }

    public void CancelMatch() => matchmaker.Leave(Context.ConnectionId);

    public string? AcceptMatch(bool accept) => matchmaker.Respond(Context.ConnectionId, accept);

    // ───────────────────────────── custom rooms

    private RoomPlayer NewSeat(string token, string name)
    {
        var p = new RoomPlayer { Id = Room.NewId(), Token = token, Name = CleanName(name), ConnectionId = Context.ConnectionId };
        if (Me is { } prof)
            lock (prof) { p.Profile = prof; p.Name = prof.Name; p.Level = prof.Level; p.Rating = prof.RatingFor("battle"); p.Charm = prof.Charm; }
        return p;
    }

    public JoinResult CreateRoom(string name, string token, int mode, string map)
    {
        if (BadToken(token)) return new(false, "Bad token.", null, null);
        matchmaker.Leave(Context.ConnectionId);
        LeaveCurrent();
        var room = rooms.Create();
        if (room == null) return new(false, "The server is full. Try again in a minute.", null, null);
        lock (room.Lock)
        {
            room.SetMode(Math.Clamp(mode, 1, 3));
            room.SetMap(Maps.List.Any(m => m.Id == map) ? map : "glade");
            var p = NewSeat(token, name);
            room.AddHuman(p);
            rooms.Seats[Context.ConnectionId] = (room.Code, p.Id);
            outbox.Send(room.TakeOutbox());
            return new(true, null, room.Code, p.Id);
        }
    }

    public JoinResult JoinRoom(string code, string name, string token)
    {
        if (BadToken(token)) return new(false, "Bad token.", null, null);
        var room = rooms.Find(code);
        if (room == null) return new(false, "No room with that code.", null, null);
        lock (room.Lock)
        {
            // The same browser coming back takes its old seat.
            var existing = room.ByToken(token);
            if (existing != null) return Reattach(room, existing);
            if (room.Matchmade) return new(false, "That match was made by matchmaking.", null, null);
        }
        matchmaker.Leave(Context.ConnectionId);
        LeaveCurrent();
        lock (room.Lock)
        {
            var p = NewSeat(token, name);
            var err = room.AddHuman(p);
            outbox.Send(room.TakeOutbox());
            if (err != null) return new(false, err, null, null);
            rooms.Seats[Context.ConnectionId] = (room.Code, p.Id);
            return new(true, null, room.Code, p.Id);
        }
    }

    /// <summary>Open public rooms that still have seats.</summary>
    public List<RoomListing> ListRooms()
    {
        var list = new List<RoomListing>();
        foreach (var room in rooms.Rooms.Values)
            lock (room.Lock) if (room.Listing() is { } l) list.Add(l);
        return list.OrderByDescending(l => l.Players).Take(30).ToList();
    }

    /// <summary>Reconnects to a seat after a dropped connection or a page reload.</summary>
    public JoinResult Rejoin(string code, string token)
    {
        var room = rooms.Find(code);
        if (room == null) return new(false, "That room is gone.", null, null);
        lock (room.Lock)
        {
            var p = room.ByToken(token);
            return p == null ? new(false, "You are no longer in that room.", null, null) : Reattach(room, p);
        }
    }

    private JoinResult Reattach(Room room, RoomPlayer p)
    {
        if (p.ConnectionId != null && p.ConnectionId != Context.ConnectionId) rooms.Seats.TryRemove(p.ConnectionId, out _);
        p.ConnectionId = Context.ConnectionId;
        p.DisconnectedAt = null;
        rooms.Seats[Context.ConnectionId] = (room.Code, p.Id);
        room.Resync(p);
        outbox.Send(room.TakeOutbox());
        return new(true, null, room.Code, p.Id);
    }

    public void LeaveRoom() => LeaveCurrent();

    private void LeaveCurrent()
    {
        if (rooms.SeatOf(Context.ConnectionId) is not var (room, p)) return;
        lock (room.Lock)
        {
            rooms.Seats.TryRemove(Context.ConnectionId, out _);
            if (room.Phase is Phase.Lobby or Phase.Ended) room.Remove(p);
            else { p.ConnectionId = null; p.DisconnectedAt = DateTime.UtcNow.AddSeconds(-Room.TakeoverSeconds); room.BroadcastRoom(); }
            outbox.Send(room.TakeOutbox());
        }
    }

    // ───────────────────────────── lobby and hero select

    private string? WithRoom(Func<Room, RoomPlayer, string?> act, bool hostOnly = false)
    {
        if (rooms.SeatOf(Context.ConnectionId) is not var (room, p)) return "You are not in a room.";
        lock (room.Lock)
        {
            if (hostOnly && room.HostId != p.Id) return "Only the host can do that.";
            var err = act(room, p);
            outbox.Send(room.TakeOutbox());
            return err;
        }
    }

    public string? SetReady(bool ready) => WithRoom((r, p) => { if (r.Phase != Phase.Lobby) return "Not in the lobby."; p.Ready = ready; r.BroadcastRoom(); return null; });
    public string? SwitchTeam() => WithRoom((r, p) => r.SwitchTeam(p));
    public string? AddBot(int team) => WithRoom((r, _) => r.AddBot(team), hostOnly: true);
    public string? FillBots() => WithRoom((r, _) => { if (r.Phase != Phase.Lobby) return "Not in the lobby."; r.FillWithBots(); return null; }, hostOnly: true);
    public string? RemovePlayer(string id) => WithRoom((r, me) =>
    {
        var p = r.ById(id);
        if (p == null || p == me || r.Phase != Phase.Lobby) return "Can't remove that player.";
        if (!p.Bot && p.ConnectionId != null) { rooms.Seats.TryRemove(p.ConnectionId, out _); r.TakeOutbox(); }
        r.Remove(p);
        return null;
    }, hostOnly: true);
    public string? SetMode(int mode) => WithRoom((r, _) => r.SetMode(mode), hostOnly: true);
    public string? SetMap(string map) => WithRoom((r, _) => r.SetMap(map), hostOnly: true);
    public string? SetPublic(bool open) => WithRoom((r, _) => { if (r.Phase != Phase.Lobby) return "Not in the lobby."; r.Public = open; r.BroadcastRoom(); return null; }, hostOnly: true);
    public string? StartMatch() => WithRoom((r, _) => r.StartHeroSelect(), hostOnly: true);
    public string? PickHero(string hero, bool lockIn) => WithRoom((r, p) => r.Pick(p, hero, lockIn));
    /// <summary>Wears a skin on the hero being picked, and keeps it on that hero from now on.</summary>
    public string? PickSkin(string? skin) => WithRoom((r, p) =>
    {
        var err = r.SetSkin(p, skin);
        if (err == null && p.Profile is { } prof && p.Hero is { } hero)
        {
            lock (prof) { if (string.IsNullOrEmpty(skin)) prof.Equipped.Remove(hero); else prof.Equipped[hero] = skin; }
            profiles.Save(prof);
        }
        return err;
    });
    public string? Loaded() => WithRoom((r, p) => { r.MarkLoaded(p); return null; });
    public string? BackToLobby() => WithRoom((r, _) => r.BackToLobby());

    // ───────────────────────────── in the match

    public void Input(float mx, float my, bool attack)
    {
        if (rooms.SeatOf(Context.ConnectionId) is not var (room, p)) return;
        lock (room.Lock)
        {
            if (room.Phase != Phase.Playing || room.Match?.HeroOf(p.Id) is not { } h) return;
            var v = new Vec(float.IsFinite(mx) ? mx : 0, float.IsFinite(my) ? my : 0);
            h.MoveDir = v.Len > 1 ? v.Norm() : v;
            h.AttackHeld = attack;
        }
    }

    /// <summary>Runs something on the caller's hero during a match; returns why not, or null.</summary>
    private string? WithHero(Func<Match, Hero, string?> act, bool starting = false)
    {
        if (rooms.SeatOf(Context.ConnectionId) is not var (room, p)) return "room";
        lock (room.Lock)
        {
            var ok = room.Phase == Phase.Playing || (starting && room.Phase == Phase.Starting);
            if (!ok || room.Match?.HeroOf(p.Id) is not { } h) return "phase";
            return act(room.Match, h);
        }
    }

    public string? Cast(int slot, float x, float y) =>
        !float.IsFinite(x) || !float.IsFinite(y) ? "bad" : WithHero((m, h) => m.Cast(h, slot, new Vec(x, y)));

    public string? Upgrade(int slot, int choice) => WithHero((m, h) => m.BuyUpgrade(h, slot, choice), starting: true);

    /// <summary>Spends a spell point to learn an ability (battles: one per level).</summary>
    public string? Learn(int slot) => WithHero((m, h) => m.Learn(h, slot), starting: true);

    public string? UseCharm(float x, float y) =>
        !float.IsFinite(x) || !float.IsFinite(y) ? "bad" : WithHero((m, h) => m.UseCharm(h, new Vec(x, y)));

    /// <summary>Starts (or stops) recalling home. Battles only.</summary>
    public string? Recall() => WithHero((m, h) => m.Recall(h));

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        matchmaker.Leave(Context.ConnectionId);
        if (rooms.SeatOf(Context.ConnectionId) is var (room, p))
        {
            lock (room.Lock)
            {
                if (p.ConnectionId == Context.ConnectionId) { p.ConnectionId = null; p.DisconnectedAt = DateTime.UtcNow; }
                room.BroadcastRoom();
                outbox.Send(room.TakeOutbox());
            }
        }
        rooms.Seats.TryRemove(Context.ConnectionId, out _);
        return base.OnDisconnectedAsync(exception);
    }
}
