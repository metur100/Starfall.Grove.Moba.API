# Starfall.Grove.Moba.API

The server for **Mini Rift**, the 3v3 battle mode of Starfall Grove. ASP.NET Core 8 + SignalR, with optional
match history in SQL Server. The client lives in [Starfall.Grove.Moba.UI](https://github.com/metur100/Starfall.Grove.Moba.UI).

The server owns the match: it runs the simulation 30 times a second and sends each player a snapshot 15 times a
second. Clients only send what they want to do (move, attack, cast, buy an upgrade).

## Run it locally

```bash
dotnet run --launch-profile http     # http://localhost:5080
```

- `GET /api/health` shows rooms, players and database state (`off`, `connecting`, `ready` or `unavailable`).
- `GET /api/catalog` lists heroes, abilities, upgrades and maps.
- `GET /api/matches/recent`, `GET /api/stats/heroes` read match history (empty without a database).
- `/hub` is the SignalR hub.

## Configuration

`appsettings.json`:

| Key | Meaning |
| --- | --- |
| `Cors:Origins` | Sites allowed to connect. GitHub Pages (`https://metur100.github.io`) and Vite dev (`http://localhost:5173`) are in by default. |
| `ConnectionStrings:Moba` | SQL Server for match history. Empty = no history; the game works without it. |

The real connection string goes in **`appsettings.Production.json`**, which is in `.gitignore` so the password never
reaches GitHub. Keep that file on your machine; `dotnet publish` includes it in the deployment.

Tables `dbo.MobaMatches` and `dbo.MobaMatchPlayers` are created on first start if they don't exist. Nothing else in
the database is touched.

## Deploy to MonsterASP (free plan)

**By hand (Visual Studio):** open `Starfall.Grove.Moba.API.sln` → right-click the project → *Publish* → *Import
profile* → pick the Web Deploy profile downloaded from the MonsterASP panel (Websites → your site → *Deploy* → *Web
Deploy*). Publish.

**By hand (FTP):** `dotnet publish -c Release -o publish`, then upload the contents of `publish/` to the site's
`wwwroot` with the FTP login from the MonsterASP panel.

**Automatically (GitHub Actions):** add these repository secrets and every push to `master` deploys:
`WEBSITE_NAME`, `SERVER_COMPUTER_NAME`, `SERVER_USERNAME`, `SERVER_PASSWORD` (all from the Web Deploy details in the
MonsterASP panel) and `MOBA_CONNECTION_STRING`.

In the MonsterASP panel make sure the site runs **.NET 8** and that **WebSockets** are enabled. Without WebSockets,
SignalR falls back to slower transports and the game will feel laggy.

## Code map

| Folder | What's there |
| --- | --- |
| `Game/Catalog.cs` | The six heroes, their abilities' numbers, and the upgrade paths. Balance lives here. |
| `Game/Maps.cs` | The three battlefields: lane, towers, Cores, camps, plants, Star Warden, woods. |
| `Game/Match.cs` | The rules: ticking, damage, deaths, rewards, levels, upgrades, sudden death, snapshots. |
| `Game/Abilities.cs` | What every basic attack and ability does. |
| `Game/Ai.cs` | Minions, monsters, towers, Fenn and bots. |
| `Rooms/` | Private rooms: seats, ready, hero select, loading, reconnect, cleanup. |
| `Hubs/MobaHub.cs` | Everything a client can call. |
| `Services/` | The 30 Hz game loop, sending messages, match history. |

## Match rules in short

- Blue starts left, Red right. One lane with Tower 1, Tower 2 and a Core on each side.
- Tower 2 can't be hurt while Tower 1 stands; the Core can't be hurt (and doesn't fight) while Tower 2 stands.
- Minion waves every 25 s (3 melee, 2 ranged, a heavy every third wave).
- Two neutral camps, three healing Moonblooms, and the Star Warden (from 1:30), which blesses the team that defeats it.
- Sudden death from 6:00 (structures take more damage, the leading team's minions grow stronger) and at 10:00 all
  structures lose their protection, so every match ends. Bot-only matches take 6–9 minutes.
