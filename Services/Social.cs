using System.Collections.Concurrent;
using Starfall.Grove.Moba.Api.Game;
using Starfall.Grove.Moba.Api.Rooms;

namespace Starfall.Grove.Moba.Api.Services;

/// <summary>A friend (or a request, or a blocked player) as a list shows them.</summary>
public sealed record FriendDto(string Id, string Name, int Level, string Rank, bool Online, string Status, string Avatar);
public sealed record FriendsDto(List<FriendDto> Friends, List<FriendDto> Requests, List<FriendDto> Blocked);
/// <summary>A chat message. Scope: "all" (everyone in the room or match), "team", or "friend" (a private message;
/// To is the other player's id).</summary>
public sealed record ChatDto(string Id, string Scope, string FromId, string From, string Text, long At, int Team, string? To);
/// <summary>A friend asks you to join their custom room.</summary>
public sealed record InviteDto(string FromId, string From, string Code, string Type, int Mode);

/// <summary>
/// Who is online, and everything players say to each other: chat in rooms and matches, private messages between
/// friends, friend requests and invites. Messages are passed on, never stored. Every message is cleaned of slurs and
/// insults, limited in length and rate, and never reaches a player who blocked its sender.
/// </summary>
public sealed class Social(ProfileStore profiles, Outbox outbox, RoomManager rooms)
{
    public const int MaxChat = 200;
    /// <summary>profile id → its connections, and connection → profile id.</summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _online = new();
    private readonly ConcurrentDictionary<string, string> _profileOf = new();
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _sent = new();

    public void Online(string conn, Profile p)
    {
        Offline(conn);
        var first = !_online.ContainsKey(p.Id);
        _online.GetOrAdd(p.Id, _ => new()).TryAdd(conn, 0);
        _profileOf[conn] = p.Id;
        if (first) _ = TellFriendsAsync(p);
    }

    public void Offline(string conn)
    {
        if (!_profileOf.TryRemove(conn, out var id)) return;
        if (_online.TryGetValue(id, out var set) && set.TryRemove(conn, out _) && set.IsEmpty)
        {
            _online.TryRemove(id, out _);
            _ = Task.Run(async () => { if (await profiles.ByIdAsync(id) is { } p) await TellFriendsAsync(p); });
        }
    }

    public bool IsOnline(string id) => _online.ContainsKey(id);
    public int OnlineCount => _online.Count;
    private IEnumerable<string> ConnsOf(string id) => _online.TryGetValue(id, out var s) ? s.Keys : [];
    public void Send(string id, string method, object arg) => outbox.Send(ConnsOf(id).Select(c => new Outgoing(c, method, [arg])).ToList());

    /// <summary>What a friend is doing, from where their connection sits.</summary>
    private string StatusOf(string id)
    {
        foreach (var c in ConnsOf(id))
            if (rooms.SeatOf(c) is var (room, _))
                return room.Phase switch { Phase.Lobby => "In a lobby", Phase.HeroSelect => "Choosing a hero", Phase.Ended => "Online", _ => "In a match" };
        return IsOnline(id) ? "Online" : "Offline";
    }

    // ───────────────────────────── friends

    public async Task<FriendsDto> FriendsOfAsync(Profile p)
    {
        List<string> friends, requests, blocked;
        lock (p) { friends = [.. p.Friends]; requests = [.. p.Requests]; blocked = [.. p.Blocked]; }
        async Task<List<FriendDto>> Load(IEnumerable<string> ids)
        {
            var list = new List<FriendDto>();
            foreach (var id in ids)
                if (await profiles.ByIdAsync(id) is { } f)
                    lock (f) list.Add(new FriendDto(f.Id, f.Name, f.Level, Economy.RankOf(f.RatingFor("battle")), IsOnline(f.Id), StatusOf(f.Id), f.Picture));
            return list;
        }
        var fl = await Load(friends);
        return new FriendsDto([.. fl.OrderByDescending(f => f.Online).ThenBy(f => f.Name)], await Load(requests), await Load(blocked));
    }

    /// <summary>Sends a player their friend list again (after it changed), if they are online.</summary>
    public async Task PushFriendsAsync(Profile p) { if (IsOnline(p.Id)) Send(p.Id, "friends", await FriendsOfAsync(p)); }

    /// <summary>Tells a player's online friends that they came online or went offline.</summary>
    private async Task TellFriendsAsync(Profile p)
    {
        List<string> ids; lock (p) ids = [.. p.Friends];
        foreach (var id in ids.Where(IsOnline))
            if (await profiles.ByIdAsync(id) is { } f) await PushFriendsAsync(f);
    }

    // ───────────────────────────── chat

    /// <summary>Whether a player may send another message now: at most 5 in 6 seconds.</summary>
    public bool CanSend(string id)
    {
        var q = _sent.GetOrAdd(id, _ => new());
        lock (q)
        {
            var now = DateTime.UtcNow;
            while (q.Count > 0 && (now - q.Peek()).TotalSeconds > 6) q.Dequeue();
            if (q.Count >= 5) return false;
            q.Enqueue(now);
            return true;
        }
    }

    /// <summary>Cleans a message: no control characters, at most <see cref="MaxChat"/> characters, rude words masked.</summary>
    public static string? Clean(string? text)
    {
        var t = new string((text ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (t.Length == 0) return null;
        if (t.Length > MaxChat) t = t[..MaxChat];
        return Names.Censor(t);
    }

    /// <summary>Delivers a message to the given players, leaving out anyone who blocked the sender.</summary>
    public async Task DeliverAsync(ChatDto msg, IEnumerable<(string? ProfileId, string? Conn)> to)
    {
        var outgoing = new List<Outgoing>();
        foreach (var (pid, conn) in to)
        {
            if (pid != null && pid != msg.FromId && await profiles.ByIdAsync(pid) is { } r)
                lock (r) if (r.Blocked.Contains(msg.FromId)) continue;
            if (conn != null) outgoing.Add(new Outgoing(conn, "chat", [msg]));
            else if (pid != null) outgoing.AddRange(ConnsOf(pid).Select(c => new Outgoing(c, "chat", [msg])));
        }
        outbox.Send(outgoing);
    }
}
