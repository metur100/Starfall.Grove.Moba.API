using System.Collections.Concurrent;

namespace Starfall.Grove.Moba.Api.Rooms;

/// <summary>All rooms on this server, and which connection sits in which room.</summary>
public sealed class RoomManager
{
    public const int MaxRooms = 200;
    private const string CodeChars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public ConcurrentDictionary<string, Room> Rooms { get; } = new();
    /// <summary>connection id → (room code, player id)</summary>
    public ConcurrentDictionary<string, (string Code, string PlayerId)> Seats { get; } = new();

    public Room? Create()
    {
        if (Rooms.Count >= MaxRooms) return null;
        for (var i = 0; i < 50; i++)
        {
            var code = string.Concat(Enumerable.Range(0, 5).Select(_ => CodeChars[Random.Shared.Next(CodeChars.Length)]));
            var room = new Room { Code = code };
            if (Rooms.TryAdd(code, room)) return room;
        }
        return null;
    }

    public Room? Find(string? code) => code != null && Rooms.TryGetValue(code.Trim().ToUpperInvariant(), out var r) ? r : null;

    /// <summary>The room and seat of a connection, if it sits anywhere.</summary>
    public (Room Room, RoomPlayer Player)? SeatOf(string connectionId)
    {
        if (!Seats.TryGetValue(connectionId, out var s) || Find(s.Code) is not { } room) return null;
        var p = room.ById(s.PlayerId);
        return p == null ? null : (room, p);
    }

    /// <summary>Removes rooms nobody is using any more: empty lobbies after 30 s, abandoned matches after 2 min,
    /// finished ones after 5 min.</summary>
    public void Cleanup()
    {
        var now = DateTime.UtcNow;
        foreach (var (code, room) in Rooms)
        {
            lock (room.Lock)
            {
                var anyone = room.Humans.Any(p => p.Connected);
                if (anyone) { room.EmptySince = now; continue; }
                var limit = room.Phase switch { Phase.Lobby => 30, Phase.Ended => 300, _ => 120 };
                if ((now - room.EmptySince).TotalSeconds < limit && room.Humans.Any()) continue;
                Rooms.TryRemove(code, out _);
                foreach (var (conn, seat) in Seats) if (seat.Code == code) Seats.TryRemove(conn, out _);
            }
        }
    }
}
