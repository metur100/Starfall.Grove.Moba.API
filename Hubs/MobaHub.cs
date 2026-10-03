using Microsoft.AspNetCore.SignalR;
using Starfall.Grove.Moba.Api.Game;
using Starfall.Grove.Moba.Api.Rooms;

namespace Starfall.Grove.Moba.Api.Hubs;

public sealed record JoinResult(bool Ok, string? Error, string? Code, string? PlayerId);
public sealed record CatalogDto(HeroDef[] Heroes, UpgradeOption[][] BasicTiers, UpgradeOption[][] AbilityTiers, int[] BasicCost, int[] AbilityCost, int[] UltCost, int UltLevel, int MaxLevel, object[] Maps);

/// <summary>
/// The one SignalR hub. Clients call these methods; the server answers through "room", "matchStart", "snap" and
/// "matchEnd" messages (sent by <see cref="Services.GameLoop"/> from each room's outbox).
/// </summary>
public sealed class MobaHub(RoomManager rooms, Services.Outbox outbox) : Hub
{
    private static string CleanName(string? name)
    {
        var n = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (n.Length > 16) n = n[..16];
        return n.Length == 0 ? "Wanderer" : n;
    }

    public static CatalogDto BuildCatalog() => new(Catalog.Heroes, Upgrades.BasicTiers, Upgrades.AbilityTiers, Upgrades.BasicCost, Upgrades.AbilityCost,
        Upgrades.UltCost, Catalog.UltLevel, Catalog.MaxLevel, Maps.List.Select(m => (object)new { m.Id, m.Name, m.Theme, m.Blurb }).ToArray());

    public CatalogDto GetCatalog() => BuildCatalog();

    public long Ping(long t) => t;

    public JoinResult CreateRoom(string name, string token, int mode, string map)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 64) return new(false, "Bad token.", null, null);
        LeaveCurrent();
        var room = rooms.Create();
        if (room == null) return new(false, "The server is full. Try again in a minute.", null, null);
        lock (room.Lock)
        {
            room.SetMode(Math.Clamp(mode, 1, 3));
            room.SetMap(Maps.List.Any(m => m.Id == map) ? map : "glade");
            var p = new RoomPlayer { Id = Room.NewId(), Token = token, Name = CleanName(name), ConnectionId = Context.ConnectionId };
            room.AddHuman(p);
            rooms.Seats[Context.ConnectionId] = (room.Code, p.Id);
            outbox.Send(room.TakeOutbox());
            return new(true, null, room.Code, p.Id);
        }
    }

    public JoinResult JoinRoom(string code, string name, string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 64) return new(false, "Bad token.", null, null);
        var room = rooms.Find(code);
        if (room == null) return new(false, "No room with that code.", null, null);
        lock (room.Lock)
        {
            // The same browser coming back takes its old seat.
            var existing = room.ByToken(token);
            if (existing != null) return Reattach(room, existing);
        }
        LeaveCurrent();
        lock (room.Lock)
        {
            var p = new RoomPlayer { Id = Room.NewId(), Token = token, Name = CleanName(name), ConnectionId = Context.ConnectionId };
            var err = room.AddHuman(p);
            outbox.Send(room.TakeOutbox());
            if (err != null) return new(false, err, null, null);
            rooms.Seats[Context.ConnectionId] = (room.Code, p.Id);
            return new(true, null, room.Code, p.Id);
        }
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
    public string? StartMatch() => WithRoom((r, _) => r.StartHeroSelect(), hostOnly: true);
    public string? PickHero(string hero, bool lockIn) => WithRoom((r, p) => r.Pick(p, hero, lockIn));
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

    public string? Cast(int slot, float x, float y)
    {
        if (rooms.SeatOf(Context.ConnectionId) is not var (room, p)) return "room";
        lock (room.Lock)
        {
            if (room.Phase != Phase.Playing || room.Match?.HeroOf(p.Id) is not { } h) return "phase";
            if (!float.IsFinite(x) || !float.IsFinite(y)) return "bad";
            return room.Match.Cast(h, slot, new Vec(x, y));
        }
    }

    public string? Upgrade(int slot, int choice)
    {
        if (rooms.SeatOf(Context.ConnectionId) is not var (room, p)) return "room";
        lock (room.Lock)
        {
            if (room.Phase is not (Phase.Playing or Phase.Starting) || room.Match?.HeroOf(p.Id) is not { } h) return "phase";
            return room.Match.BuyUpgrade(h, slot, choice);
        }
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
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
