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
- `GET /api/leaderboard?type=battle|duel` lists the best players by rating.
- `/hub` is the SignalR hub.

## Configuration

`appsettings.json`:

| Key | Meaning |
| --- | --- |
| `Cors:Origins` | Sites allowed to connect. GitHub Pages (`https://metur100.github.io`) and Vite dev (`http://localhost:5173`) are in by default. |
| `ConnectionStrings:Moba` | SQL Server for match history and player profiles. Empty = no history, and profiles are kept as JSON files under `App_Data/profiles` (fine for local play). |
| `Matchmaking:BotOfferSeconds` | How long a player searches before being asked whether to play against bots instead (default 30; asked again every minute after a no). |
| `Match:SurrenderAfterSeconds` | How long a battle runs before a team may vote to surrender (default 300). |
| `App:UiUrl` | The game's address, used in password reset links. |
| `Smtp:Host`, `Port`, `EnableSsl`, `User`, `Password`, `From`, `FromName`, `ReportsTo` | The mail server for password reset emails and player reports. **Without a Host no email is sent** (the link is only written to the log). Put the real values in `appsettings.Production.json`, like the connection string. With Gmail: host `smtp.gmail.com`, port 587, your address as User and From, and an *app password* (Google account → Security → App passwords) as Password. |

The real connection string goes in **`appsettings.Production.json`**, which is in `.gitignore` so the password never
reaches GitHub. Keep that file on your machine; `dotnet publish` includes it in the deployment.

Tables `dbo.MobaMatches`, `dbo.MobaMatchPlayers` and `dbo.MobaProfiles` are created on first start if they don't
exist. Nothing else in the database is touched.

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
| `Game/Maps.cs` | The five battlefields (1, 2 and 3 lanes) and three duel arenas. |
| `Game/Duel.cs` | Duel rounds, the closing ring and duel bots. |
| `Game/Match.cs` | The rules: ticking, damage, deaths, rewards, levels, upgrades, sudden death, snapshots. |
| `Game/Charms.cs` | Charms (Flash, Heal, Ghost, Barrier), recall, and the duel's Starshard. |
| `Game/Economy.cs` | Coins, prices of heroes and skins, player levels, ranks, match rewards. |
| `Game/Abilities.cs` | What every basic attack and ability does. |
| `Game/Ai.cs` | Minions, monsters, towers, Fenn and bots. |
| `Rooms/` | Private rooms: seats, ready, hero select, loading, reconnect, cleanup. |
| `Hubs/MobaHub.cs` | Everything a client can call. |
| `Services/` | The 30 Hz game loop, sending messages, match history, profiles (`Profiles.cs`), matchmaking (`Matchmaker.cs`) and match rewards (`Rewards.cs`). |

## Accounts, friends and chat

Playing needs an account. `Register(username, email, password)` turns the device's profile into one (any progress it
already has is kept); `Login(username or email, password)` signs a device in and gives it its own token (a row in
`dbo.MobaSessions`); `Logout` forgets it. Passwords are stored as salted PBKDF2-SHA256 hashes. Five wrong passwords lock a
username for five minutes. `ForgotPassword(email)` emails a link (`App:UiUrl?reset=<id>.<code>`, valid one hour, one use);
`ResetPassword(code, password)` sets the new password and signs every other device out. `DeleteProfile` deletes the
account and profile for good.

Friends: `AddFriend(username)`, `AnswerFriend`, `RemoveFriend`, `Friends()`; `InviteFriend(id)` from a custom room's
lobby. Chat: `Chat("all" | "team", text)` in a room or match, `Chat("friend", text, id)` to a friend. Messages are passed
on, never stored, at most 200 characters and 5 per 6 seconds, with slurs and insults masked (`Game/Names.cs`).
`Block(id)` hides a player's chat and requests and ends the friendship; `Report(id, reason, message)` keeps a report in
`dbo.MobaReports` and emails it to `Smtp:ReportsTo`.

## Players, coins and matchmaking

A browser keeps a secret token; `Hello` ties a connection to that token's **profile** (stored by a hash of the token).
A new player gets 600 coins and three heroes (Mira, Kael, Wren); one other hero is free each week. The rest, and three
skins per hero (rare 600, epic 900, legendary 1500), are bought with coins.

Every match pays coins and player experience: a battle win 120 / loss 50, a duel win 90 / loss 35, plus a little for
kills and assists, 150 for the first win of the day and 100 per player level gained (400 every fifth). Custom rooms pay
¾; leaving early pays nothing. Matchmade games also move a battle or duel **rating** (Elo; smaller steps when bots
played), shown as a rank from Seedling to Celestial.

**Matchmaking** (`FindMatch(type, mode)`): players near each other's rating are grouped (the window widens while they
wait), everyone must accept within 12 s, then they get a room with balanced teams and go straight to hero select.
Whoever declines leaves the queue; the rest go back to its front. When nobody fits after `BotOfferSeconds`, the player
is asked (`AnswerBots(yes)`): yes starts a match at once with anyone else in range who is searching (they accept as usual) and bots in the empty
seats; no keeps searching. `PlayBots(type, mode)` starts a **practice match** against bots straight away: it pays like a
custom room and isn't rated. **Custom rooms** keep their room codes; a host can also list a room publicly.

**In a match**: `Signal(kind, x, y)` pings the map for the team (attack, danger, omw, help, go; three every four
seconds). `Surrender(yes)` starts or answers a team surrender vote in a battle after five minutes: it passes with every
player (two of three in a 3v3), bots and players who have left don't vote, and a failed vote waits 90 s.

**Daily quests**: three a day per player from a pool of seven (play, win, takedowns, assists, battles, duels, hero
damage), paid in coins when a match finishes them. They are part of the profile (`quests`).

## Match rules in short

**Battle** (1v1 to 3v3) on one of five maps: Starfall Glade and Frostfang Pass have one lane, Twinbrook Vale has two,
Three Peaks and Cinder Crown have three.

- Heroes start with only their basic attack and learn one ability per level, in any order; the ultimate from level 4.
  Gold from kills and over time buys upgrades in the Spellbook.
- Blue starts left, Red right. Every lane has an outer and an inner tower per team, and each team has one Core.
- A lane's inner tower can't be hurt while its outer tower stands. The Core can't be hurt (and doesn't fight) while
  every lane's inner tower stands: breaking through one lane is enough.
- Minion waves every 25 s down every lane (smaller waves on maps with more lanes); minions walk round towers. Bots
  spread over the lanes.
- Two neutral camps, healing Moonblooms, and the Star Warden (from 1:30), which blesses the team that defeats it.
- Sudden death from 6:00; at 10:00 every structure loses its protection, so every match ends. Bot matches take 7–9 minutes.

**Duel** (1v1 to 3v3) on one of three arenas: heroes only, no minions or structures.

- Everyone starts at level 6, with every ability learned and 2.2× their usual health so a round is a real fight.
- There is no gold. Each hero has two duel abilities (`DuelSlots` in `Game/Catalog.cs`); before every round each
  duellist picks one free upgrade path for each of them (20 s before the first round, 14 s before the others; the round
  starts 3 s after everyone has picked, and whoever runs out of time gets the first path).
- A round ends when one side has nobody standing; the first team to win 3 rounds wins.
- Between rounds everyone is restored and grows a level.
- From 60 s into a round a ring of starfire closes in over 45 s and burns anyone outside it (4% of health a second,
  more the longer it goes on). Most rounds are over in 15–40 s, long before that.

**Charms and recall:** every hero brings a charm chosen in hero select — Flash (blink 260), Heal (15% to you and the
most hurt ally nearby), Ghost (+32% speed for 5 s) or Barrier (a shield of 20% health) — with a long cooldown that
resets every duel round. In a battle, Recall takes a hero home after 4.5 s standing still; moving, attacking, casting
or being hurt breaks it.

**Kills:** a hero on a streak of 3+ carries a bounty (shutdown gold); kill events carry multikill counts and streaks
for the announcer, and an "ace" when a whole team is down. **Duels** drop a Starshard into the middle of the ring 14 s
into each round: whoever reaches it first is blessed for 6 s and healed a little. Rounds now pause 5 s between them
for the round summary.

**Line of sight** (every map): stones, pillars and tree trunks block sight. Heroes can't attack, or cast a targeted
spell (Lion's Rush, Shade Step, Fenn's Pounce, Doom Sigil…) at, anyone behind one; heroes' shots stop when they hit
one, and Comet Shower and a Sunflare's blast don't reach round them. Towers and minions shoot over them.

**Balance** is checked with bot duels: every hero against every other, on every arena (numbers in `Game/Catalog.cs`).
After the last pass each hero wins 40–60% of its 1v1 duels overall.

Spells and slower attacks have a wind-up (Sunflare, Whiteout, Comet Shower, Verdant Awakening, Mira's and Lyra's
bolts…); clients show it as a cast bar.
