using Microsoft.AspNetCore.SignalR;
using Starfall.Grove.Moba.Api.Game;
using Starfall.Grove.Moba.Api.Rooms;
using Starfall.Grove.Moba.Api.Services;

namespace Starfall.Grove.Moba.Api.Hubs;

public sealed record JoinResult(bool Ok, string? Error, string? Code, string? PlayerId);
public sealed record HelloResult(bool Ok, string? Error, ProfileDto? Profile);
/// <summary>The answer to signing up, in or resetting a password: an error, or the profile and (when this device
/// should switch to a new sign-in) the token to keep.</summary>
public sealed record AuthResult(string? Error, ProfileDto? Profile, string? Token);
public sealed record ShopResult(string? Error, ProfileDto? Profile);
/// <summary>What can be bought and chosen: hero prices, skins, charms and the rank ladder.</summary>
public sealed record ShopDto(Dictionary<string, int> HeroPrices, SkinDef[] Skins, CharmDef[] Charms, RankDef[] Ranks, int FirstWinBonus, string[] Starters, AvatarDef[] Avatars);
public sealed record CatalogDto(HeroDef[] Heroes, UpgradeOption[][] BasicTiers, UpgradeOption[][] AbilityTiers, int[] BasicCost, int[] AbilityCost, int[] UltCost, int UltLevel, int MaxLevel, object[] Maps, ShopDto Shop);

/// <summary>
/// The one SignalR hub. Clients call these methods; the server answers through "room", "matchStart", "snap",
/// "matchEnd", "rewards", "profile", "queue" and "matchFound" messages (most sent by <see cref="GameLoop"/> from each
/// room's outbox).
///
/// A browser keeps a secret token. <see cref="Hello"/> ties the connection to that token's profile (coins, heroes,
/// skins, level, rating); everything that touches the profile works on the connection's own one. Playing needs an
/// account: <see cref="Register"/> turns the device's profile into one (username, email, password), and
/// <see cref="Login"/> signs a device in to an existing one with its username and password.
///
/// Chat ("chat"), friends ("friends") and invites ("invite") go through <see cref="Social"/>.
/// </summary>
public sealed class MobaHub(RoomManager rooms, Outbox outbox, ProfileStore profiles, Matchmaker matchmaker, Social social, Mailer mailer, IConfiguration config, ILogger<MobaHub> log) : Hub
{
    private static string CleanName(string? name) => Names.Clean(name);
    private static bool BadToken(string? token) => string.IsNullOrWhiteSpace(token) || token.Length is < 16 or > 64;

    public static CatalogDto BuildCatalog() => new(Catalog.Heroes, Upgrades.BasicTiers, Upgrades.AbilityTiers, Upgrades.BasicCost, Upgrades.AbilityCost,
        Upgrades.UltCost, Catalog.UltLevel, Catalog.MaxLevel, Maps.List.Select(m => (object)new { m.Id, m.Name, m.Theme, m.Type, m.Lanes, m.Blurb }).ToArray(),
        new ShopDto(Economy.HeroPrices, Economy.Skins, Economy.Charms, Economy.Ranks, Economy.FirstWinBonus, Economy.Starters, Economy.Avatars));

    public CatalogDto GetCatalog() => BuildCatalog();

    public long Ping(long t) => t;

    // ───────────────────────────── profile and shop

    private string? Token => Context.Items.TryGetValue("token", out var t) ? t as string : null;
    private Profile? Me => Token is { } t ? profiles.Cached(t) : null;

    /// <summary>Ties this connection to the token's profile and returns it: null when this device has none yet (then
    /// the player signs up or logs in), or a profile without a username (progress from before accounts, kept when
    /// the player signs up).</summary>
    public async Task<HelloResult> Hello(string token, string name)
    {
        if (BadToken(token)) return new(false, "Bad token.", null);
        try
        {
            var p = await profiles.TryGetAsync(token);
            Context.Items["token"] = token;
            if (p is { Registered: true }) social.Online(Context.ConnectionId, p);
            return new(true, null, p == null ? null : ProfileDto.Of(p));
        }
        catch (Exception)
        {
            return new(false, "Couldn't load your profile. Try again in a moment.", null);
        }
    }

    public ProfileDto? GetProfile() => Me is { } p ? ProfileDto.Of(p) : null;

    // ───────────────────────────── accounts

    private static readonly System.Text.RegularExpressions.Regex UsernameRx = new("^[A-Za-z0-9_]{3,16}$");
    private static readonly System.Text.RegularExpressions.Regex EmailRx = new(@"^[^@\s]{1,64}@[^@\s]+\.[^@\s]{2,}$");
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Fails, DateTime Until)> LoginFails = new();

    /// <summary>Makes an account: the device's profile (with any progress it has) gets a username, email and password.</summary>
    public async Task<AuthResult> Register(string username, string email, string password)
    {
        if (Token is not { } token) return new("Connect first.", null, null);
        username = (username ?? "").Trim(); email = (email ?? "").Trim();
        if (!UsernameRx.IsMatch(username)) return new("Usernames are 3 to 16 letters, digits or _.", null, null);
        if (Names.Offensive(username)) return new("Please choose a different username.", null, null);
        if (email.Length > 254 || !EmailRx.IsMatch(email)) return new("That email address doesn't look right.", null, null);
        if ((password ?? "").Length < 8 || password!.Length > 128) return new("Passwords need at least 8 characters.", null, null);
        await profiles.AccountLock.WaitAsync();
        try
        {
            if (await profiles.ByUsernameAsync(username) != null) return new("That username is taken.", null, null);
            if (await profiles.ByEmailAsync(email) != null) return new("An account with that email already exists. Log in, or reset your password.", null, null);
            var p = await profiles.GetAsync(token, username);
            lock (p)
            {
                if (p.Registered) return new("This device is already signed in.", null, null);
                p.Username = username; p.Name = username; p.Email = email; p.PasswordHash = Passwords.Hash(password);
            }
            Func<Task> welcome;
            lock (p) welcome = ConfirmationEmail(p, Emails.Welcome);
            profiles.Save(p);
            social.Online(Context.ConnectionId, p);
            log.LogInformation("New account {User}", username);
            // The thank-you email goes out in the background, so signing up doesn't wait for the mail server.
            _ = Task.Run(async () => { try { await welcome(); } catch (Exception e) { log.LogError(e, "Welcome email failed"); } });
            return new(null, ProfileDto.Of(p), null);
        }
        catch (Exception e) { log.LogError(e, "Register failed"); return new("Couldn't create your account. Try again in a moment.", null, null); }
        finally { profiles.AccountLock.Release(); }
    }

    /// <summary>Signs this device in with a username (or email) and password. Returns the token the device keeps from
    /// now on.</summary>
    public async Task<AuthResult> Login(string username, string password)
    {
        var who = (username ?? "").Trim().ToLowerInvariant();
        if (who.Length == 0 || string.IsNullOrEmpty(password)) return new("Enter your username and password.", null, null);
        if (LoginFails.TryGetValue(who, out var f) && f.Fails >= 5 && f.Until > DateTime.UtcNow) return new("Too many attempts. Wait a few minutes and try again.", null, null);
        try
        {
            var p = who.Contains('@') ? await profiles.ByEmailAsync(who) : await profiles.ByUsernameAsync(who);
            string? hash; lock (p ?? new Profile()) hash = p?.PasswordHash;
            if (p == null || !Passwords.Verify(password, hash))
            {
                LoginFails.AddOrUpdate(who, _ => (1, DateTime.UtcNow.AddMinutes(5)), (_, v) => (v.Fails + 1, DateTime.UtcNow.AddMinutes(5)));
                return new("Wrong username or password.", null, null);
            }
            LoginFails.TryRemove(who, out _);
            social.Offline(Context.ConnectionId);
            var token = await profiles.NewSessionAsync(p);
            Context.Items["token"] = token;
            social.Online(Context.ConnectionId, p);
            return new(null, ProfileDto.Of(p), token);
        }
        catch (Exception e) { log.LogError(e, "Login failed"); return new("Couldn't log in. Try again in a moment.", null, null); }
    }

    /// <summary>Signs this device out. The device then makes a new token and shows the login screen.</summary>
    public async Task Logout()
    {
        matchmaker.Leave(Context.ConnectionId);
        LeaveCurrent();
        social.Offline(Context.ConnectionId);
        if (Token is { } token) { try { await profiles.EndSessionAsync(token); } catch { /* it ends on the device anyway */ } }
        Context.Items.Remove("token");
    }

    /// <summary>Emails a link to choose a new password. Always answers the same, so nobody learns which emails have
    /// an account.</summary>
    public async Task<string?> ForgotPassword(string email)
    {
        email = (email ?? "").Trim();
        if (!EmailRx.IsMatch(email)) return "That email address doesn't look right.";
        try
        {
            if (await profiles.ByEmailAsync(email) is { Registered: true } p)
            {
                var code = ProfileStore.NewToken() + ProfileStore.NewToken();
                string id, name;
                lock (p) { p.ResetHash = ProfileStore.Hash(code); p.ResetExpires = DateTime.UtcNow.AddHours(1); id = p.Id; name = p.Name; }
                profiles.Save(p);
                var link = $"{config["App:UiUrl"] ?? "https://metur100.github.io/Starfall.Grove.Moba.UI/"}?reset={id}.{code}";
                var (subject, text, html) = Emails.Reset(name, link);
                await mailer.SendAsync(email, subject, text, html);
            }
        }
        catch (Exception e) { log.LogError(e, "Forgot password failed"); }
        return null;
    }

    /// <summary>Sets a new password with the code from the reset email, signs every other device out, and signs this
    /// one in.</summary>
    public async Task<AuthResult> ResetPassword(string code, string password)
    {
        var parts = (code ?? "").Split('.', 2);
        if (parts.Length != 2) return new("That reset link isn't valid.", null, null);
        if ((password ?? "").Length < 8 || password!.Length > 128) return new("Passwords need at least 8 characters.", null, null);
        try
        {
            if (await profiles.ByIdAsync(parts[0]) is not { } p) return new("That reset link isn't valid.", null, null);
            lock (p)
            {
                if (p.ResetHash == null || p.ResetExpires < DateTime.UtcNow || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                        System.Text.Encoding.UTF8.GetBytes(p.ResetHash), System.Text.Encoding.UTF8.GetBytes(ProfileStore.Hash(parts[1]))))
                    return new("That reset link has expired or was already used. Ask for a new one.", null, null);
                p.PasswordHash = Passwords.Hash(password); p.ResetHash = null; p.ResetExpires = null;
                // The link came to their inbox, so the address is theirs.
                p.EmailConfirmed = true; p.ConfirmHash = null; p.ConfirmExpires = null;
            }
            profiles.Save(p);
            await profiles.EndAllSessionsAsync(p.Key);
            social.Offline(Context.ConnectionId);
            var token = await profiles.NewSessionAsync(p);
            Context.Items["token"] = token;
            social.Online(Context.ConnectionId, p);
            return new(null, ProfileDto.Of(p), token);
        }
        catch (Exception e) { log.LogError(e, "Reset failed"); return new("Couldn't reset your password. Try again in a moment.", null, null); }
    }

    /// <summary>Makes a new confirmation code for the profile (call under its lock or before anyone else sees it) and
    /// returns the job that emails it, built with <paramref name="email"/> (the welcome email or the plain reminder).</summary>
    private Func<Task> ConfirmationEmail(Profile p, Func<string, string, (string, string, string)> email)
    {
        var code = ProfileStore.NewToken() + ProfileStore.NewToken();
        p.ConfirmHash = ProfileStore.Hash(code); p.ConfirmExpires = DateTime.UtcNow.AddDays(7); p.ConfirmSent = DateTime.UtcNow;
        var link = $"{config["App:UiUrl"] ?? "https://metur100.github.io/Starfall.Grove.Moba.UI/"}?confirm={p.Id}.{code}";
        var (to, name) = (p.Email!, p.Name);
        return async () => { var (subject, text, html) = email(name, link); await mailer.SendAsync(to, subject, text, html); };
    }

    /// <summary>Confirms the account's email with the code from the welcome email's link. Works signed in or not
    /// (the link may open on another device); a signed-in player gets their profile back.</summary>
    public async Task<ShopResult> ConfirmEmail(string code)
    {
        var parts = (code ?? "").Split('.', 2);
        if (parts.Length != 2) return new("That confirmation link isn't valid.", null);
        try
        {
            if (await profiles.ByIdAsync(parts[0]) is not { Registered: true } p) return new("That confirmation link isn't valid.", null);
            lock (p)
            {
                if (!p.EmailConfirmed)
                {
                    if (p.ConfirmHash == null || p.ConfirmExpires < DateTime.UtcNow || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                            System.Text.Encoding.UTF8.GetBytes(p.ConfirmHash), System.Text.Encoding.UTF8.GetBytes(ProfileStore.Hash(parts[1]))))
                        return new("That confirmation link isn't valid or has expired. Sign in and ask for a new one on your profile.", null);
                    p.EmailConfirmed = true; p.ConfirmHash = null; p.ConfirmExpires = null;
                }
            }
            profiles.Save(p);
            return new(null, Account?.Id == p.Id ? ProfileDto.Of(p) : null);
        }
        catch (Exception e) { log.LogError(e, "Confirm failed"); return new("Couldn't confirm your email. Try again in a moment.", null); }
    }

    /// <summary>Sends the confirmation link again (at most once a minute).</summary>
    public async Task<string?> ResendConfirmation()
    {
        if (Account is not { } p) return "Sign in first.";
        Func<Task> send;
        lock (p)
        {
            if (p.EmailConfirmed) return "Your email is already confirmed.";
            if (p.ConfirmSent > DateTime.UtcNow.AddMinutes(-1)) return "We just sent one. Check your inbox (and spam), or try again in a minute.";
            send = ConfirmationEmail(p, Emails.Welcome);
        }
        profiles.Save(p);
        try { await send(); return null; }
        catch (Exception e) { log.LogError(e, "Resend confirmation failed"); return "Couldn't send the email. Try again in a moment."; }
    }

    /// <summary>Changes the account's username, if the new one is free. Friends, chat and the leaderboard show the new
    /// name; logging in works with the new one (or the email).</summary>
    public async Task<ShopResult> ChangeUsername(string username)
    {
        if (Account is not { } p) return new("Sign in first.", null);
        username = (username ?? "").Trim();
        if (!UsernameRx.IsMatch(username)) return new("Usernames are 3 to 16 letters, digits or _.", null);
        if (Names.Offensive(username)) return new("Please choose a different username.", null);
        await profiles.AccountLock.WaitAsync();
        try
        {
            if (await profiles.ByUsernameAsync(username) is { } other && other.Id != p.Id) return new("That username is taken.", null);
            lock (p)
            {
                if (p.Username == username) return new("That's already your username.", null);
                p.Username = username; p.Name = username;
            }
            profiles.Save(p);
            log.LogInformation("Account {Id} renamed to {User}", p.Id, username);
            return new(null, ProfileDto.Of(p));
        }
        catch (Exception e) { log.LogError(e, "Rename failed"); return new("Couldn't change your username. Try again in a moment.", null); }
        finally { profiles.AccountLock.Release(); }
    }

    // ───────────────────────────── friends and chat

    private Profile? Account => Me is { Registered: true } p ? p : null;

    public async Task<FriendsDto?> Friends() => Account is { } p ? await social.FriendsOfAsync(p) : null;

    /// <summary>Sends a friend request by username (or accepts theirs, if they already asked).</summary>
    public async Task<string?> AddFriend(string username)
    {
        if (Account is not { } me) return "Sign in first.";
        var them = await profiles.ByUsernameAsync(username ?? "");
        if (them is not { Registered: true } || them.Id == me.Id) return "No player with that username.";
        bool already, theyAsked, blocked;
        lock (me) { already = me.Friends.Contains(them.Id); theyAsked = me.Requests.Contains(them.Id); }
        lock (them) blocked = them.Blocked.Contains(me.Id);
        if (already) return $"{them.Name} is already your friend.";
        if (theyAsked) return await AnswerFriend(them.Id, true);
        if (!blocked) { lock (them) if (!them.Requests.Contains(me.Id)) them.Requests.Add(me.Id); profiles.Save(them); await social.PushFriendsAsync(them); social.Send(them.Id, "profile", ProfileDto.Of(them)); }
        return null;
    }

    public async Task<string?> AnswerFriend(string id, bool accept)
    {
        if (Account is not { } me) return "Sign in first.";
        bool had; lock (me) had = me.Requests.Remove(id);
        if (!had) return "That request is gone.";
        if (accept && await profiles.ByIdAsync(id) is { } them)
        {
            lock (me) if (!me.Friends.Contains(id)) me.Friends.Add(id);
            lock (them) { if (!them.Friends.Contains(me.Id)) them.Friends.Add(me.Id); them.Requests.Remove(me.Id); }
            profiles.Save(them);
            await social.PushFriendsAsync(them);
        }
        profiles.Save(me);
        await social.PushFriendsAsync(me);
        social.Send(me.Id, "profile", ProfileDto.Of(me));
        return null;
    }

    public async Task<string?> RemoveFriend(string id)
    {
        if (Account is not { } me) return "Sign in first.";
        lock (me) me.Friends.Remove(id);
        profiles.Save(me);
        if (await profiles.ByIdAsync(id) is { } them) { lock (them) them.Friends.Remove(me.Id); profiles.Save(them); await social.PushFriendsAsync(them); }
        await social.PushFriendsAsync(me);
        return null;
    }

    /// <summary>Blocks a player: no chat or friend requests from them reach you, and any friendship ends.</summary>
    public async Task<string?> Block(string id, bool block)
    {
        if (Account is not { } me) return "Sign in first.";
        if (id == me.Id) return "That's you.";
        lock (me)
        {
            if (block) { if (!me.Blocked.Contains(id)) me.Blocked.Add(id); me.Friends.Remove(id); me.Requests.Remove(id); }
            else me.Blocked.Remove(id);
        }
        profiles.Save(me);
        if (block && await profiles.ByIdAsync(id) is { } them) { lock (them) { them.Friends.Remove(me.Id); them.Requests.Remove(me.Id); } profiles.Save(them); await social.PushFriendsAsync(them); }
        await social.PushFriendsAsync(me);
        return null;
    }

    /// <summary>Reports a player (their name or a chat message) for us to review.</summary>
    public async Task<string?> Report(string id, string reason, string? message)
    {
        if (Account is not { } me) return "Sign in first.";
        if (await profiles.ByIdAsync(id) is not { } them) return "No such player.";
        reason = (reason ?? "").Trim(); if (reason.Length > 200) reason = reason[..200];
        message = message?.Trim(); if (message?.Length > 400) message = message[..400];
        try
        {
            await profiles.ReportAsync(me.Id, them.Id, them.Name, reason.Length == 0 ? "No reason given" : reason, message);
            if (mailer.ReportsTo is { } to)
                _ = mailer.SendAsync(to, $"Mini Rift report: {them.Name}", $"{me.Name} ({me.Id}) reported {them.Name} ({them.Id}).\nReason: {reason}\nMessage: {message}",
                    $"<p><b>{System.Net.WebUtility.HtmlEncode(me.Name)}</b> ({me.Id}) reported <b>{System.Net.WebUtility.HtmlEncode(them.Name)}</b> ({them.Id}).</p><p>Reason: {System.Net.WebUtility.HtmlEncode(reason)}</p><p>Message: {System.Net.WebUtility.HtmlEncode(message ?? "")}</p>");
            return null;
        }
        catch (Exception e) { log.LogError(e, "Report failed"); return "Couldn't send the report. Try again in a moment."; }
    }

    /// <summary>Says something: "all" or "team" in your room or match, or "friend" to one friend.</summary>
    public async Task<string?> Chat(string scope, string text, string? to)
    {
        if (Account is not { } me) return "Sign in first.";
        var clean = Social.Clean(text);
        if (clean == null) return null;
        if (!social.CanSend(me.Id)) return "Slow down a little.";
        if (scope == "friend")
        {
            bool friends; lock (me) friends = to != null && me.Friends.Contains(to);
            if (!friends) return "You can only message your friends.";
            if (!social.IsOnline(to!)) return "Your friend is offline.";
            var msg = new ChatDto(Room.NewId(), "friend", me.Id, me.Name, clean, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 0, to);
            await social.DeliverAsync(msg, [(to, null), (me.Id, null)]);
            return null;
        }
        if (rooms.SeatOf(Context.ConnectionId) is not var (room, seat)) return "You're not in a room.";
        List<(string?, string?)> targets;
        int team;
        lock (room.Lock)
        {
            team = seat.Team;
            targets = room.Humans.Where(h => h.ConnectionId != null && (scope != "team" || h.Team == seat.Team)).Select(h => (h.Profile?.Id, h.ConnectionId)).ToList();
        }
        await social.DeliverAsync(new ChatDto(Room.NewId(), scope == "team" ? "team" : "all", me.Id, me.Name, clean, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), team, null), targets);
        return null;
    }

    /// <summary>Invites a friend to the custom room you are in.</summary>
    public async Task<string?> InviteFriend(string id)
    {
        if (Account is not { } me) return "Sign in first.";
        bool friends; lock (me) friends = me.Friends.Contains(id);
        if (!friends) return "You can only invite your friends.";
        if (!social.IsOnline(id)) return "Your friend is offline.";
        if (rooms.SeatOf(Context.ConnectionId) is not var (room, _)) return "Make a room first.";
        InviteDto invite;
        lock (room.Lock)
        {
            if (room.Matchmade || room.Phase != Phase.Lobby) return "You can only invite friends to a custom room's lobby.";
            invite = new InviteDto(me.Id, me.Name, room.Code, room.Type, room.Mode);
        }
        if (await profiles.ByIdAsync(id) is { } them) { bool blocked; lock (them) blocked = them.Blocked.Contains(me.Id); if (!blocked) social.Send(id, "invite", invite); }
        return null;
    }

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

    /// <summary>Deletes the player's account and profile for good: username, email, coins, heroes, skins, ratings,
    /// friends and history. Every device signed in to it is signed out.</summary>
    public async Task<string?> DeleteProfile()
    {
        if (Me is not { } p) return "Not signed in yet.";
        matchmaker.Leave(Context.ConnectionId);
        LeaveCurrent();
        social.Offline(Context.ConnectionId);
        try { await profiles.DeleteAsync(p); }
        catch (Exception) { return "Couldn't delete your profile. Try again in a moment."; }
        Context.Items.Remove("token");
        return null;
    }

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

    /// <summary>Chooses the profile picture: an emblem or creature (some unlock at a level), or the portrait of a hero
    /// the player owns.</summary>
    public ShopResult SetAvatar(string avatar) => Change(p =>
    {
        avatar ??= "";
        if (avatar.StartsWith("hero:"))
        { if (!p.Heroes.Contains(avatar[5..])) return "Unlock that hero first."; }
        else if (!Economy.AvatarById.TryGetValue(avatar, out var a)) return "Unknown picture.";
        else if (p.Level < a.Level) return $"Reach level {a.Level} to use this picture.";
        p.Avatar = avatar;
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
        if (Token is not { } token || Account is not { } p) return "Sign in first.";
        LeaveCurrent();
        return matchmaker.Join(Context.ConnectionId, token, p, type, mode);
    }

    /// <summary>A practice match against bots, straight away: pays like a custom room and isn't rated.</summary>
    public string? PlayBots(string type, int mode)
    {
        if (Token is not { } token || Account is not { } p) return "Sign in first.";
        LeaveCurrent();
        return matchmaker.Join(Context.ConnectionId, token, p, type, mode, bots: true);
    }

    /// <summary>Answers "nobody found yet: play against bots?".</summary>
    public string? AnswerBots(bool yes) => matchmaker.AnswerBots(Context.ConnectionId, yes);

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
        if (Account == null) return new(false, "Sign in first.", null, null);
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
        if (Account == null) return new(false, "Sign in first.", null, null);
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

    /// <summary>Starts a surrender vote, or votes in the team's running one.</summary>
    public string? Surrender(bool yes) => WithRoom((r, p) => r.Surrender(p, yes));

    /// <summary>Pings the map for the team.</summary>
    public string? Signal(string kind, float x, float y) => WithRoom((r, p) => r.Signal(p, kind ?? "", x, y));

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        matchmaker.Leave(Context.ConnectionId);
        social.Offline(Context.ConnectionId);
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
