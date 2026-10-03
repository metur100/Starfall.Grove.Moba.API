namespace Starfall.Grove.Moba.Api.Game;

/// <summary>How an ability is aimed. Enemy and Ally pick the unit nearest the aimed point (within range).</summary>
public enum Target { Self, Point, Direction, Enemy, Ally }

/// <summary>
/// One ability's numbers. Slot 0 is the basic attack (Power = attack damage, Cooldown = time between attacks).
/// Power is the ability's main magnitude: damage, healing or shield, whichever it does. Duration is how long a zone,
/// buff or summon lasts and Cc how long its stun, root or slow holds. Upgrades scale these (see <see cref="Upgrades"/>).
/// </summary>
public sealed class AbilityDef
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public int Slot { get; init; }
    public float Cooldown { get; init; }
    public float Cost { get; init; }
    public float Range { get; init; }
    public float Radius { get; init; }
    public float Power { get; init; }
    public float Duration { get; init; }
    public float Cc { get; init; }
    public float Speed { get; init; }
    public float Windup { get; init; }
    public Target Target { get; init; }
    public bool Toggle { get; init; }
    public string Effects { get; init; } = "";
}

public sealed class HeroDef
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Title { get; init; }
    public required string Role { get; init; }
    public int Difficulty { get; init; }
    public float Hp { get; init; }
    public float HpPerLevel { get; init; }
    public string Resource { get; init; } = "Mana";
    public float Mana { get; init; }
    public float ManaRegen { get; init; }
    /// <summary>Damage taken is multiplied by this: 0.7 means 30% less.</summary>
    public float Armor { get; init; }
    public float Speed { get; init; }
    public float AdPerLevel { get; init; }
    public float Crit { get; init; }
    public bool Melee { get; init; }
    public required AbilityDef[] Abilities { get; init; }
    public AbilityDef Basic => Abilities[0];
}

/// <summary>
/// The six heroes. Five come from Starfall Grove's valley with their own spells, re-tuned for short team fights; Elara,
/// the Bloomwarden, is new. Each hero has a basic attack and four abilities: Q, W and E from the start, R (the
/// ultimate) from level <see cref="Catalog.UltLevel"/>.
/// </summary>
public static class Catalog
{
    public const int UltLevel = 3;
    public const int MaxLevel = 10;

    public static readonly HeroDef[] Heroes =
    [
        new()
        {
            Id = "mira", Name = "Mira", Title = "Astralmancer", Role = "Mage", Difficulty = 3,
            Hp = 520, HpPerLevel = 58, Mana = 320, ManaRegen = 7, Armor = 1f, Speed = 300, AdPerLevel = 4, Crit = .15f,
            Abilities =
            [
                new() { Id = "spark", Name = "Spark", Slot = 0, Cooldown = .95f, Range = 480, Power = 40, Speed = 900, Target = Target.Enemy, Effects = "homing" },
                new() { Id = "gravity", Name = "Gravity Well", Slot = 1, Cooldown = 11, Cost = 60, Range = 650, Radius = 170, Power = 12, Duration = 2, Target = Target.Point, Effects = "zone,pull" },
                new() { Id = "sunfire", Name = "Sunflare", Slot = 2, Cooldown = 6, Cost = 55, Range = 800, Radius = 150, Power = 130, Speed = 760, Windup = .3f, Target = Target.Direction, Effects = "projectile,area" },
                new() { Id = "starguard", Name = "Guardian Stars", Slot = 3, Cooldown = 14, Cost = 70, Radius = 85, Power = 30, Duration = 6, Target = Target.Self, Effects = "buff,block" },
                new() { Id = "starfall", Name = "Comet Shower", Slot = 4, Cooldown = 40, Cost = 100, Radius = 600, Power = 190, Windup = .25f, Target = Target.Self, Effects = "area" },
            ],
        },
        new()
        {
            Id = "kael", Name = "Kael", Title = "Knight", Role = "Tank", Difficulty = 2,
            Hp = 780, HpPerLevel = 90, Resource = "Stamina", Mana = 200, ManaRegen = 12, Armor = .7f, Speed = 285, AdPerLevel = 5, Melee = true,
            Abilities =
            [
                new() { Id = "slash", Name = "Slash", Slot = 0, Cooldown = 1f, Range = 105, Radius = 120, Power = 52, Target = Target.Enemy, Effects = "melee,cleave" },
                new() { Id = "charge", Name = "Lion's Rush", Slot = 1, Cooldown = 9, Cost = 30, Range = 380, Power = 70, Cc = .8f, Speed = 1400, Target = Target.Enemy, Effects = "dash,stun" },
                new() { Id = "slam", Name = "Earthsplitter", Slot = 2, Cooldown = 9, Cost = 45, Radius = 210, Power = 110, Cc = 1f, Target = Target.Self, Effects = "area,stun" },
                new() { Id = "guard", Name = "Bulwark", Slot = 3, Cooldown = 14, Cost = 40, Radius = 160, Power = 40, Duration = 1.6f, Target = Target.Self, Effects = "buff,block,reflect" },
                new() { Id = "bladestorm", Name = "Steel Cyclone", Slot = 4, Cooldown = 35, Cost = 80, Radius = 170, Power = 30, Duration = 3, Target = Target.Self, Effects = "buff,area" },
            ],
        },
        new()
        {
            Id = "lyra", Name = "Lyra", Title = "Frostweaver", Role = "Controller", Difficulty = 3,
            Hp = 540, HpPerLevel = 60, Mana = 320, ManaRegen = 7, Armor = .9f, Speed = 295, AdPerLevel = 4,
            Abilities =
            [
                new() { Id = "frostbolt", Name = "Rime Shard", Slot = 0, Cooldown = 1f, Range = 460, Power = 40, Cc = 1.2f, Speed = 850, Target = Target.Enemy, Effects = "homing,slow" },
                new() { Id = "blink", Name = "Frost Step", Slot = 1, Cooldown = 9, Cost = 40, Range = 320, Radius = 140, Power = 60, Cc = 1.5f, Target = Target.Point, Effects = "blink,slow" },
                new() { Id = "frostnova", Name = "Glacial Burst", Slot = 2, Cooldown = 10, Cost = 60, Radius = 220, Power = 110, Cc = 1.4f, Target = Target.Self, Effects = "area,root" },
                new() { Id = "iceBlock", Name = "Glacier Shell", Slot = 3, Cooldown = 18, Cost = 50, Power = 150, Duration = 2.5f, Target = Target.Self, Toggle = true, Effects = "buff,heal" },
                new() { Id = "blizzard", Name = "Whiteout", Slot = 4, Cooldown = 40, Cost = 110, Range = 750, Radius = 260, Power = 45, Duration = 4, Cc = .45f, Windup = .4f, Target = Target.Point, Effects = "zone,slow" },
            ],
        },
        new()
        {
            Id = "riven", Name = "Riven", Title = "Assassin", Role = "Assassin", Difficulty = 4,
            Hp = 640, HpPerLevel = 70, Resource = "Energy", Mana = 200, ManaRegen = 14, Armor = .75f, Speed = 320, AdPerLevel = 4.5f, Crit = .2f, Melee = true,
            Abilities =
            [
                new() { Id = "stab", Name = "Twin Daggers", Slot = 0, Cooldown = .7f, Range = 95, Power = 38, Target = Target.Enemy, Effects = "melee,crit" },
                new() { Id = "shadowstep", Name = "Shade Step", Slot = 1, Cooldown = 8, Cost = 35, Range = 420, Power = 2.2f, Target = Target.Enemy, Effects = "blink" },
                new() { Id = "knives", Name = "Dagger Burst", Slot = 2, Cooldown = 7, Cost = 45, Range = 380, Power = 55, Speed = 900, Target = Target.Self, Effects = "projectile" },
                new() { Id = "stealth", Name = "Nightveil", Slot = 3, Cooldown = 16, Cost = 50, Power = 1.8f, Duration = 5, Target = Target.Self, Toggle = true, Effects = "buff,stealth" },
                new() { Id = "deathmark", Name = "Doom Sigil", Slot = 4, Cooldown = 38, Cost = 90, Range = 600, Radius = 180, Power = 320, Duration = 2, Target = Target.Enemy, Effects = "area" },
            ],
        },
        new()
        {
            Id = "wren", Name = "Wren", Title = "Ranger", Role = "Marksman", Difficulty = 2,
            Hp = 560, HpPerLevel = 62, Resource = "Focus", Mana = 220, ManaRegen = 10, Armor = .88f, Speed = 315, AdPerLevel = 4.5f, Crit = .1f,
            Abilities =
            [
                new() { Id = "arrow", Name = "Swift Arrow", Slot = 0, Cooldown = .85f, Range = 560, Power = 42, Speed = 1150, Target = Target.Enemy, Effects = "projectile,pierce" },
                new() { Id = "command", Name = "Fenn: Pounce", Slot = 1, Cooldown = 8, Cost = 30, Range = 550, Power = 60, Cc = 1f, Target = Target.Enemy, Effects = "summon,stun" },
                new() { Id = "volley", Name = "Arrow Fan", Slot = 2, Cooldown = 7, Cost = 50, Range = 600, Power = 55, Speed = 1100, Target = Target.Direction, Effects = "projectile" },
                new() { Id = "leap", Name = "Hawk Leap", Slot = 3, Cooldown = 12, Cost = 45, Range = 300, Power = 40, Cc = 1f, Target = Target.Direction, Effects = "dash,root" },
                new() { Id = "wildcall", Name = "Howl of the Pack", Slot = 4, Cooldown = 40, Cost = 90, Power = 30, Duration = 8, Target = Target.Self, Effects = "summon,buff" },
            ],
        },
        new()
        {
            Id = "elara", Name = "Elara", Title = "Bloomwarden", Role = "Support", Difficulty = 2,
            Hp = 580, HpPerLevel = 62, Mana = 340, ManaRegen = 8, Armor = .9f, Speed = 300, AdPerLevel = 3.5f,
            Abilities =
            [
                new() { Id = "seed", Name = "Thorn Seed", Slot = 0, Cooldown = 1f, Range = 470, Power = 36, Speed = 800, Target = Target.Enemy, Effects = "homing,heal" },
                new() { Id = "naturebolt", Name = "Nature Bolt", Slot = 1, Cooldown = 8, Cost = 50, Range = 750, Power = 95, Cc = 1.2f, Speed = 900, Target = Target.Direction, Effects = "projectile,root,heal" },
                new() { Id = "grove", Name = "Healing Grove", Slot = 2, Cooldown = 13, Cost = 70, Range = 600, Radius = 200, Power = 25, Duration = 4, Cc = .25f, Target = Target.Point, Effects = "zone,heal,slow" },
                new() { Id = "barkskin", Name = "Barkskin", Slot = 3, Cooldown = 12, Cost = 50, Range = 600, Power = 140, Duration = 3, Target = Target.Ally, Effects = "buff,shield" },
                new() { Id = "awakening", Name = "Verdant Awakening", Slot = 4, Cooldown = 45, Cost = 110, Range = 700, Radius = 300, Power = 220, Cc = 1.5f, Windup = .3f, Target = Target.Point, Effects = "area,heal,root" },
            ],
        },
    ];

    public static readonly Dictionary<string, HeroDef> ById = Heroes.ToDictionary(h => h.Id);

    public static int XpToNext(int level) => 80 + 50 * (level - 1);
    /// <summary>Abilities grow 6% stronger with every hero level.</summary>
    public static float LevelScale(int level) => 1 + .06f * (level - 1);
}

/// <summary>An upgrade a player can buy for one ability: three tiers, each a choice between two options.</summary>
public sealed record UpgradeOption(string Id, string Name, string Text);

public static class Upgrades
{
    public static readonly UpgradeOption[][] BasicTiers =
    [
        [new("dmg15", "Sharpened", "+15% attack damage"), new("aspd15", "Quickened", "+15% attack speed")],
        [new("crit10", "Keen Eye", "+10% critical chance"), new("hp12", "Vitality", "+12% max health")],
        [new("dmg20", "Fury", "+20% attack damage"), new("move8", "Swiftness", "+8% move speed")],
    ];
    public static readonly UpgradeOption[][] AbilityTiers =
    [
        [new("power20", "Potency", "+20% power"), new("haste15", "Haste", "-15% cooldown")],
        [new("reach20", "Reach", "+20% range and area"), new("endure35", "Endurance", "+35% duration and control")],
        [new("power30", "Mastery", "+30% power"), new("flow", "Flow", "-40% cost, -10% cooldown")],
    ];
    public static readonly int[] BasicCost = [90, 160, 240];
    public static readonly int[] AbilityCost = [100, 175, 260];
    public static readonly int[] UltCost = [160, 240, 340];

    public static UpgradeOption[][] TiersFor(int slot) => slot == 0 ? BasicTiers : AbilityTiers;
    public static int Cost(int slot, int tier) => (slot == 0 ? BasicCost : slot == 4 ? UltCost : AbilityCost)[tier];
}

/// <summary>Multipliers an ability has from its bought upgrades.</summary>
public struct Mods
{
    public float Power, Cd, Reach, Dur, Cost;
    public static Mods One => new() { Power = 1, Cd = 1, Reach = 1, Dur = 1, Cost = 1 };
    public static Mods From(IEnumerable<string> picks)
    {
        var m = One;
        foreach (var p in picks)
            switch (p)
            {
                case "power20": m.Power *= 1.2f; break;
                case "power30": m.Power *= 1.3f; break;
                case "dmg15": m.Power *= 1.15f; break;
                case "dmg20": m.Power *= 1.2f; break;
                case "haste15": m.Cd *= .85f; break;
                case "aspd15": m.Cd /= 1.15f; break;
                case "reach20": m.Reach *= 1.2f; break;
                case "endure35": m.Dur *= 1.35f; break;
                case "flow": m.Cost *= .6f; m.Cd *= .9f; break;
            }
        return m;
    }
}
