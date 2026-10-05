namespace Starfall.Grove.Moba.Api.Game;

public enum UnitKind { Hero, Minion, Tower, Core, Monster, Pet }

/// <summary>Status flags sent to clients so they can draw stuns, shields and the like.</summary>
[Flags]
public enum St
{
    None = 0, Stun = 1, Root = 2, Slow = 4, Shell = 8, Stealth = 16, Guard = 32, Spin = 64, Shield = 128,
    Stars = 256, Dead = 512, Blessed = 1024, Marked = 2048, Empowered = 4096, Casting = 8192, Frenzy = 16384,
    Invulnerable = 32768, Dashing = 65536,
}

/// <summary>Anything that fights: heroes, minions, towers, Cores, neutral monsters and pets.</summary>
public class Unit
{
    public int Id;
    public UnitKind Kind;
    /// <summary>What the client draws: a hero id, "melee", "ranged", "heavy", "tower", "core", "boar", "warden", ...</summary>
    public string Sub = "";
    public int Team;
    public Vec Pos;
    public Vec Facing = new(1, 0);
    public float Radius = 22;
    public float Hp, MaxHp;
    public float Armor = 1;
    public float Speed;
    public float AttackRange, AttackDamage, AttackCd, AttackTimer;
    public int TargetId;
    public bool Dead;

    // Timers in seconds.
    public float StunT, RootT, SlowT, SlowAmt, InvulnT, StealthT, GuardT, SpinT, ShieldT, Shield, ShellT, BlessT, FrenzyT, MarkT, DashT;
    public float AggroT;
    /// <summary>A tower's hits in a row on the same hero (each one hurts more).</summary>
    public int Hits;
    public int StarCount;
    public float StarT, StarTick;

    /// <summary>Who hurt this unit recently (hero unit id → match time), for assists.</summary>
    public readonly Dictionary<int, float> Attackers = [];

    // Knockback: a short push that ignores the unit's own movement.
    public Vec PushVel; public float PushT;
    // A dash in progress: moves at DashVel until DashT runs out, then runs OnDashEnd.
    public Vec DashVel; public Action? OnDashEnd;

    // Minions follow the lane; monsters guard their home.
    public List<Vec>? Path; public int PathIndex;
    public Vec Home; public int CampIndex = -1;
    public int OwnerId;
    public float LifeT = -1;
    public float Gold, Xp;
    /// <summary>Which lane a minion walks, a tower guards, or a bot plays.</summary>
    public int Lane;

    public bool Alive => !Dead;
    public bool Stunned => StunT > 0;
    public bool CanMove => !Dead && StunT <= 0 && RootT <= 0 && ShellT <= 0 && DashT <= 0;
    public bool CanAct => !Dead && StunT <= 0 && ShellT <= 0 && DashT <= 0;
    public bool IsStructure => Kind is UnitKind.Tower or UnitKind.Core;
    public float MoveSpeed => Speed * (SlowT > 0 ? 1 - SlowAmt : 1) * (StealthT > 0 ? 1.2f : 1) * (SpinT > 0 ? 1.1f : 1);

    public virtual St Status(float now)
    {
        var s = St.None;
        if (StunT > 0) s |= St.Stun;
        if (RootT > 0) s |= St.Root;
        if (SlowT > 0) s |= St.Slow;
        if (ShellT > 0) s |= St.Shell;
        if (StealthT > 0) s |= St.Stealth;
        if (GuardT > 0) s |= St.Guard;
        if (SpinT > 0) s |= St.Spin;
        if (ShieldT > 0 && Shield > 0) s |= St.Shield;
        if (StarCount > 0) s |= St.Stars;
        if (Dead) s |= St.Dead;
        if (BlessT > 0) s |= St.Blessed;
        if (MarkT > 0) s |= St.Marked;
        if (FrenzyT > 0) s |= St.Frenzy;
        if (InvulnT > 0) s |= St.Invulnerable;
        if (DashT > 0) s |= St.Dashing;
        return s;
    }
}

/// <summary>A player's hero (human or bot).</summary>
public sealed class Hero : Unit
{
    public required HeroDef Def;
    public required string PlayerId;
    public string Name = "";
    public int Level = 1;
    public float Exp;
    public float Mana, MaxMana, ManaRegen;
    public float BaseHp;
    public float GoldBank;
    public readonly float[] Cooldowns = new float[5];
    /// <summary>The upgrade ids bought for each slot, in tier order.</summary>
    public readonly List<string>[] Picks = [[], [], [], [], []];
    /// <summary>Which abilities the hero has learned (the basic attack always). On the battlefield one per level.</summary>
    public readonly bool[] Learned = [true, false, false, false, false];
    public int LearnedCount => Learned.Count(l => l) - 1;
    /// <summary>Duels: the abilities still waiting for this round's upgrade pick.</summary>
    public readonly List<int> DuelPending = [];
    public float RespawnT;
    public int Kills, Deaths, Assists, Streak;
    public float DamageDealt, HeroDamage, Healing;

    // Input from the player (or the bot driving them).
    public Vec MoveDir;
    public bool AttackHeld;
    public float IdleT;

    /// <summary>The next basic attack is a certain critical hit with this multiplier (Shade Step, Nightveil).</summary>
    public float Empowered;
    public float CastT; public Action? OnCast;
    public int FennId;
    public float CritChance => Def.Crit + (Picks[0].Contains("crit10") ? .1f : 0);

    public Mods ModsFor(int slot)
    {
        var m = Mods.From(Picks[slot]);
        if (slot == 0) return m;
        var a = Def.Abilities[slot];
        // A path that wouldn't change this ability (more reach on a self buff, more duration on a plain blast, a later
        // Doom Sigil) makes it 15% stronger instead.
        if (Picks[slot].Contains("reach20") && a.Range <= 0 && a.Radius <= 0) { m.Reach = 1; m.Power *= 1.15f; }
        if (Picks[slot].Contains("endure35") && ((a.Duration <= 0 && a.Cc <= 0) || a.Id == "deathmark")) { m.Dur = 1; m.Power *= 1.15f; }
        return m;
    }
    public float Power(int slot) => Def.Abilities[slot].Power * ModsFor(slot).Power * Catalog.LevelScale(Level) * (BlessT > 0 ? 1.2f : 1);

    public override St Status(float now)
    {
        var s = base.Status(now);
        if (Empowered > 0) s |= St.Empowered;
        if (CastT > 0) s |= St.Casting;
        return s;
    }
}

public sealed class Projectile
{
    public int Id;
    public string Kind = "";
    public int OwnerId, Team;
    public Vec Pos, Vel;
    public float Radius = 14, RangeLeft, Speed;
    public int HomingId;
    public int Pierce;
    public bool HitsAllies;
    /// <summary>Stopped by stones and trunks: heroes' shots are, towers' and minions' (fired over the lane) aren't.</summary>
    public bool Solid;
    public readonly HashSet<int> Hit = [];
    /// <summary>Shared by every projectile of one cast so a fan of arrows only hits each target once.</summary>
    public HashSet<int>? CastHit;
    public Action<Unit>? OnHit;
    public Action<Vec>? OnExpire;
    public Action<Unit>? OnAlly;
    public bool Done;
}

/// <summary>A lasting area: a gravity well, a whiteout, a healing grove.</summary>
public sealed class Zone
{
    public int Id;
    public string Kind = "";
    public int OwnerId, Team;
    public Vec Pos;
    public float Radius, TimeLeft, Interval, Timer;
    public Action<Zone>? OnTick;
    public Action<Zone>? OnFrame;
}

public sealed class Delayed
{
    public float At;
    public required Action Run;
}
