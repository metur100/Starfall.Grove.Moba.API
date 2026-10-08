using Starfall.Grove.Moba.Api.Game;
using Starfall.Grove.Moba.Api.Rooms;

namespace Starfall.Grove.Moba.Api.Services;

/// <summary>Where a player stands in matchmaking: idle, searching (for how long, how many others) or found. While
/// searching, <c>Offer</c> asks whether to play against bots instead, and <c>OfferIn</c> says when that question comes.</summary>
public sealed record QueueDto(string State, string? Type, int Mode, float Waited, int Searching, float OfferIn, bool Offer = false);
/// <summary>A match was found: how many have accepted so far, and how long is left to accept.</summary>
public sealed record MatchFoundDto(string Id, string Type, int Mode, int Accepted, int Total, float TimeLeft, bool YouAccepted, int Bots);

/// <summary>
/// Real matchmaking: players say what they want to play (battle or duel, 1v1 to 3v3) and wait. Players close in rating
/// are grouped (the window widens the longer they wait), everyone has to accept, then a room is made for them with
/// balanced teams and they go straight to hero select. When too few people are looking, bots fill the empty seats after
/// a while, so nobody waits forever. Whoever declines or doesn't answer leaves the queue; the others go back to the
/// front of it.
/// When nobody fits after a while (Matchmaking:BotOfferSeconds, 30), the player is asked whether to fight bots instead.
/// Yes starts a match right away, with anyone else in range who is searching (they accept as usual) and bots in the empty seats; no keeps
/// searching and asks again a minute later. A player can also ask for bots straight away: a practice match, which pays
/// like a custom room and doesn't move the rating.
/// </summary>
public sealed class Matchmaker(RoomManager rooms, Outbox outbox, IConfiguration config, ILogger<Matchmaker> log)
{
    public const float AcceptSeconds = 12;
    public const float AskAgainSeconds = 60;
    private readonly float _offerAfter = config.GetValue("Matchmaking:BotOfferSeconds", 30f);
    private readonly object _lock = new();
    private readonly List<Ticket> _queue = [];
    private readonly List<Proposal> _proposals = [];
    private readonly List<Outgoing> _out = [];
    private DateTime _lastStatus;

    private sealed class Ticket
    {
        public required string Conn, Token, Name, Type, Charm;
        public required Profile Profile;
        public int Mode, Rating, Level;
        public DateTime Since, OfferAt;
        /// <summary>The bot question is showing; the player said yes to bots.</summary>
        public bool Asking, BotsOk;
    }
    private sealed class Proposal
    {
        public required string Id, Type;
        public int Mode;
        public required List<Ticket> Tickets;
        public readonly HashSet<string> Accepted = [];
        public DateTime Deadline;
        public bool Practice;
        public int Bots => Mode * 2 - Tickets.Count;
    }

    public int Searching { get { lock (_lock) return _queue.Count; } }

    public string? Join(string conn, string token, Profile p, string type, int mode, bool bots = false)
    {
        if (type is not ("battle" or "duel")) return "Unknown match type.";
        if (mode is < 1 or > 3) return "Mode must be 1v1, 2v2 or 3v3.";
        lock (_lock)
        {
            if (_proposals.Any(x => x.Tickets.Any(t => t.Conn == conn))) return "A match was already found for you.";
            // The same player on another tab or device gives up that place.
            foreach (var old in _queue.Where(t => t.Token == token && t.Conn != conn).ToList()) { _queue.Remove(old); Status(old.Conn, null); }
            _queue.RemoveAll(t => t.Conn == conn);
            Ticket t;
            var now = DateTime.UtcNow;
            lock (p) t = new Ticket { Conn = conn, Token = token, Name = p.Name, Type = type, Mode = mode, Charm = p.Charm, Profile = p, Rating = p.RatingFor(type), Level = p.Level, Since = now, OfferAt = now.AddSeconds(_offerAfter) };
            if (bots) Launch(new Proposal { Id = Room.NewId(), Type = type, Mode = mode, Tickets = [t], Practice = true });
            else
            {
                _queue.Add(t);
                Status(conn, t);
            }
        }
        Flush();
        return null;
    }

    /// <summary>Takes a player out of matchmaking (cancel, disconnect, or joining a room another way).</summary>
    public void Leave(string conn)
    {
        lock (_lock)
        {
            if (_queue.RemoveAll(t => t.Conn == conn) > 0) Status(conn, null);
            var pr = _proposals.FirstOrDefault(x => x.Tickets.Any(t => t.Conn == conn));
            if (pr != null) Cancel(pr, [conn]);
        }
        Flush();
    }

    public string? Respond(string conn, bool accept)
    {
        lock (_lock)
        {
            var pr = _proposals.FirstOrDefault(x => x.Tickets.Any(t => t.Conn == conn));
            if (pr == null) return "No match is waiting for you.";
            if (!accept) Cancel(pr, [conn]);
            else
            {
                pr.Accepted.Add(conn);
                if (pr.Accepted.Count == pr.Tickets.Count) Launch(pr);
                else Found(pr);
            }
        }
        Flush();
        return null;
    }

    /// <summary>The answer to "play against bots?": yes starts a match with bots (see <see cref="Tick"/>), no searches on.</summary>
    public string? AnswerBots(string conn, bool yes)
    {
        lock (_lock)
        {
            var t = _queue.FirstOrDefault(x => x.Conn == conn);
            if (t == null) return "You aren't searching.";
            t.Asking = false;
            if (yes) t.BotsOk = true;
            else t.OfferAt = DateTime.UtcNow.AddSeconds(AskAgainSeconds);
            Status(conn, t);
        }
        Tick();
        return null;
    }

    /// <summary>Called by the game loop a few times a second: forms matches, times out unanswered ones, and keeps
    /// everyone's search timer up to date.</summary>
    public void Tick()
    {
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            foreach (var pr in _proposals.Where(p => now >= p.Deadline).ToList())
                Cancel(pr, pr.Tickets.Where(t => !pr.Accepted.Contains(t.Conn)).Select(t => t.Conn).ToHashSet());

            foreach (var group in _queue.GroupBy(t => (t.Type, t.Mode)).ToList())
            {
                var list = group.OrderBy(t => t.Since).ToList();
                while (list.Count > 0)
                {
                    var anchor = list[0];
                    var waited = (float)(now - anchor.Since).TotalSeconds;
                    var window = 150 + 20 * waited;
                    var fit = list.Where(t => Math.Abs(t.Rating - anchor.Rating) <= window).Take(anchor.Mode * 2).ToList();
                    if (fit.Count < anchor.Mode * 2) { list.Remove(anchor); continue; }
                    foreach (var t in fit) { list.Remove(t); _queue.Remove(t); t.Asking = false; }
                    var pr = new Proposal { Id = Room.NewId(), Type = anchor.Type, Mode = anchor.Mode, Tickets = fit, Deadline = now.AddSeconds(AcceptSeconds) };
                    _proposals.Add(pr);
                    Found(pr);
                }
            }

            // Whoever said yes to bots goes now, together with everyone else in range who is still searching, and bots
            // in the empty seats: real players first. Those who said yes have already accepted; anyone else gets the
            // usual accept step. When everyone in the group said yes, it starts at once.
            foreach (var anchor in _queue.Where(t => t.BotsOk).OrderBy(t => t.Since).ToList())
            {
                if (!_queue.Contains(anchor)) continue;
                var window = 150 + 20 * (float)(now - anchor.Since).TotalSeconds;
                var group = _queue.Where(t => t.Type == anchor.Type && t.Mode == anchor.Mode && Math.Abs(t.Rating - anchor.Rating) <= window)
                    .OrderByDescending(t => t.BotsOk).ThenBy(t => t.Since).Take(anchor.Mode * 2).ToList();
                foreach (var t in group) { _queue.Remove(t); t.Asking = false; }
                var pr = new Proposal { Id = Room.NewId(), Type = anchor.Type, Mode = anchor.Mode, Tickets = group, Deadline = now.AddSeconds(AcceptSeconds) };
                foreach (var t in group.Where(t => t.BotsOk)) pr.Accepted.Add(t.Conn);
                if (pr.Accepted.Count == group.Count) { Launch(pr); continue; }
                _proposals.Add(pr);
                Found(pr);
            }

            // Nobody fits after a while: ask.
            foreach (var t in _queue.Where(t => !t.Asking && !t.BotsOk && now >= t.OfferAt))
            {
                t.Asking = true;
                Status(t.Conn, t);
            }

            if ((now - _lastStatus).TotalSeconds >= 1)
            {
                _lastStatus = now;
                foreach (var t in _queue) Status(t.Conn, t);
                foreach (var pr in _proposals) Found(pr);
            }
        }
        Flush();
    }

    // ───────────────────────────── inside the lock

    private void Status(string conn, Ticket? t)
    {
        if (t == null) { _out.Add(new Outgoing(conn, "queue", [new QueueDto("idle", null, 0, 0, 0, 0)])); return; }
        var waited = (float)(DateTime.UtcNow - t.Since).TotalSeconds;
        var searching = _queue.Count(x => x.Type == t.Type && x.Mode == t.Mode);
        var offerIn = MathF.Max(0, MathF.Ceiling((float)(t.OfferAt - DateTime.UtcNow).TotalSeconds));
        _out.Add(new Outgoing(conn, "queue", [new QueueDto("searching", t.Type, t.Mode, MathF.Round(waited, 1), searching, offerIn, t.Asking)]));
    }

    private void Found(Proposal pr)
    {
        var left = MathF.Max(0, (float)(pr.Deadline - DateTime.UtcNow).TotalSeconds);
        foreach (var t in pr.Tickets)
        {
            _out.Add(new Outgoing(t.Conn, "queue", [new QueueDto("found", pr.Type, pr.Mode, 0, 0, 0)]));
            _out.Add(new Outgoing(t.Conn, "matchFound", [new MatchFoundDto(pr.Id, pr.Type, pr.Mode, pr.Accepted.Count, pr.Tickets.Count, MathF.Round(left, 1), pr.Accepted.Contains(t.Conn), pr.Bots)]));
        }
    }

    /// <summary>Calls a proposal off: whoever declined (or didn't answer) leaves the queue, the rest go back to the
    /// front of it.</summary>
    private void Cancel(Proposal pr, HashSet<string> leaving)
    {
        _proposals.Remove(pr);
        foreach (var t in pr.Tickets)
        {
            if (leaving.Contains(t.Conn)) { Status(t.Conn, null); continue; }
            t.Asking = false;
            _queue.Add(t);
            Status(t.Conn, t);
        }
    }

    private static readonly int[] Snake = [1, 2, 2, 1, 1, 2];

    /// <summary>Everyone accepted (or asked for bots): make the room, put the players on balanced teams (strongest first, in snake order)
    /// and fill the empty seats with bots.</summary>
    private void Launch(Proposal pr)
    {
        _proposals.Remove(pr);
        var room = rooms.Create();
        if (room == null)
        {
            log.LogWarning("No room for a found match; back to the queue.");
            foreach (var t in pr.Tickets) { t.BotsOk = false; _queue.Add(t); Status(t.Conn, t); }
            return;
        }
        var map = PickMap(pr.Type, pr.Mode);
        var sorted = pr.Tickets.OrderByDescending(t => t.Rating).ToList();
        lock (room.Lock)
        {
            var humans = sorted.Select((t, i) => new RoomPlayer
            {
                Id = Room.NewId(), Token = t.Token, Name = t.Name, ConnectionId = t.Conn, Team = Snake[i % Snake.Length],
                Profile = t.Profile, Level = t.Level, Rating = t.Rating, Charm = t.Charm,
            }).ToList();
            room.BeginMatchmade(pr.Mode, map, humans, pr.Practice);
            foreach (var h in humans)
            {
                rooms.Seats[h.ConnectionId!] = (room.Code, h.Id);
                _out.Add(new Outgoing(h.ConnectionId!, "queue", [new QueueDto("idle", null, 0, 0, 0, 0)]));
            }
            _out.AddRange(room.TakeOutbox());
        }
        log.LogInformation("Matchmaking: {Type} {Mode}v{Mode} on {Map} with {Humans} players and {Bots} bots{Practice}", pr.Type, pr.Mode, pr.Mode, map, pr.Tickets.Count, pr.Bots, pr.Practice ? " (practice)" : "");
    }

    /// <summary>A battlefield that suits the team size (no three-lane maps for a 1v1), or any arena for a duel.</summary>
    private static string PickMap(string type, int mode)
    {
        var maps = Maps.List.Where(m => m.Type == type && (type == "duel" || m.Lanes <= Math.Max(1, mode))).ToList();
        if (maps.Count == 0) maps = Maps.List.Where(m => m.Type == type).ToList();
        return maps[Random.Shared.Next(maps.Count)].Id;
    }

    private void Flush()
    {
        List<Outgoing> o;
        lock (_lock) { o = [.. _out]; _out.Clear(); }
        outbox.Send(o);
    }
}
