using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using Starfall.Grove.Moba.Api.Hubs;
using Starfall.Grove.Moba.Api.Rooms;

namespace Starfall.Grove.Moba.Api.Services;

/// <summary>Sends room messages to their connections without waiting for them.</summary>
public sealed class Outbox(IHubContext<MobaHub> hub, ILogger<Outbox> log)
{
    public void Send(List<Outgoing> messages)
    {
        foreach (var m in messages)
        {
            var task = hub.Clients.Client(m.ConnectionId).SendCoreAsync(m.Method, m.Args);
            if (!task.IsCompletedSuccessfully)
                _ = task.ContinueWith(t => log.LogDebug(t.Exception, "Send {Method} failed", m.Method), TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}

/// <summary>
/// Drives every room about 30 times a second: hero select timers, loading, the match simulation and snapshots.
/// Also cleans up empty rooms and hands finished matches to the database.
/// </summary>
public sealed class GameLoop(RoomManager rooms, Outbox outbox, MatchStore store, ILogger<GameLoop> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / 30));
        var clock = Stopwatch.StartNew();
        var last = clock.Elapsed.TotalSeconds;
        var lastCleanup = 0.0;
        while (await timer.WaitForNextTickAsync(stop))
        {
            var now = clock.Elapsed.TotalSeconds;
            var dt = (float)Math.Min(.25, now - last);
            last = now;
            foreach (var room in rooms.Rooms.Values)
            {
                try
                {
                    List<Outgoing> outgoing;
                    Game.MatchEndDto? result;
                    string code;
                    lock (room.Lock)
                    {
                        room.Update(dt);
                        outgoing = room.TakeOutbox();
                        result = room.PendingResult;
                        room.PendingResult = null;
                        code = room.Code;
                    }
                    outbox.Send(outgoing);
                    if (result != null) _ = store.SaveAsync(code, room.Mode, room.MapId, result);
                }
                catch (Exception e)
                {
                    log.LogError(e, "Room {Code} failed; closing it", room.Code);
                    rooms.Rooms.TryRemove(room.Code, out _);
                }
            }
            if (now - lastCleanup > 5) { lastCleanup = now; rooms.Cleanup(); }
        }
    }
}
