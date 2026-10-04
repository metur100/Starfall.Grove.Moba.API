namespace Starfall.Grove.Moba.Api.Game;

// Every hero's basic attack and abilities. Numbers live in Catalog.cs; this is what each one does.
public sealed partial class Match
{
    private static int Ri(float v) => (int)MathF.Round(v);
    private void FxAt(string e, string k, Vec p, float r = 0, int u = 0, int delayMs = 0) =>
        Fx(new FxDto { E = e, K = k, X = Ri(p.X), Y = Ri(p.Y), R = r > 0 ? Ri(r) : null, U = u != 0 ? u : null, V = delayMs > 0 ? delayMs : null });

    private IEnumerable<Unit> EnemiesNear(int team, Vec p, float r, bool structures = false) =>
        Units.Where(u => !u.Dead && u.Team != team && u.Kind != UnitKind.Pet && u.StealthT <= 0
            && (structures ? !Protected(u) : !u.IsStructure) && Vec.Dist(u.Pos, p) <= r + u.Radius).ToList();

    private IEnumerable<Hero> AlliesNear(int team, Vec p, float r) =>
        Heroes.Where(h => h.Alive && h.Team == team && Vec.Dist(h.Pos, p) <= r + h.Radius).ToList();

    private static void Stun(Unit u, float t) { if (!u.IsStructure && u.InvulnT <= 0 && u.ShellT <= 0) { u.StunT = MathF.Max(u.StunT, t); if (u is Hero h && h.CastT > 0) { h.CastT = 0; h.OnCast = null; } } }
    private static void Root(Unit u, float t) { if (!u.IsStructure && u.InvulnT <= 0 && u.ShellT <= 0) u.RootT = MathF.Max(u.RootT, t); }
    private static void Slow(Unit u, float amount, float t) { if (u.IsStructure) return; u.SlowAmt = MathF.Max(u.SlowT > 0 ? u.SlowAmt : 0, amount); u.SlowT = MathF.Max(u.SlowT, t); }

    // ───────────────────────────── basic attacks

    /// <summary>How long each hero winds up a basic attack before it lands or flies (a staff raised, a bow drawn).</summary>
    private static float AttackWindup(string hero) => hero switch { "mira" or "elara" => .3f, "lyra" => .32f, "wren" or "kael" => .2f, _ => .12f };

    private void BasicAttack(Hero h, Unit t)
    {
        var m = h.ModsFor(0);
        h.AttackTimer = h.Def.Basic.Cooldown * m.Cd;
        h.TargetId = t.Id;
        h.Facing = (t.Pos - h.Pos).Norm();
        var dmg = h.AttackDamage * m.Power;
        var crit = h.Empowered > 0 || _rng.Next() < h.CritChance;
        var mult = h.Empowered > 0 ? h.Empowered : crit ? (h.Def.Id == "riven" ? 2f : 1.75f) : 1f;
        h.Empowered = 0;
        if (h.StealthT > 0) EndStealth(h, keepEmpower: true);
        dmg *= mult;
        var windup = MathF.Min(AttackWindup(h.Def.Id), h.AttackTimer * .5f);
        Fx(new FxDto { E = "atk", U = h.Id, K = h.Def.Basic.Id, X = Ri(t.Pos.X), Y = Ri(t.Pos.Y), V = Ri(windup * 1000) });
        // The blow lands (or the shot leaves) when the windup ends, if the hero can still act.
        Later(windup, () => { if (h.Alive && h.CanAct && t.Alive) Strike(h, t, dmg, crit); });
    }

    private void Strike(Hero h, Unit t, float dmg, bool crit)
    {
        var dir = t.Pos - h.Pos;
        if (crit) Fx(new FxDto { E = "crit", U = h.Id });

        switch (h.Def.Id)
        {
            case "mira":
                Shoot(h, "spark", dir, h.Def.Basic.Speed, 900, u => Damage(h, u, dmg, false, crit), homing: t.Id);
                break;
            case "lyra":
                Shoot(h, "frostbolt", dir, h.Def.Basic.Speed, 900, u => { Damage(h, u, dmg, false, crit); Slow(u, .3f, h.Def.Basic.Cc); }, homing: t.Id);
                break;
            case "elara":
                Shoot(h, "seed", dir, h.Def.Basic.Speed, 900, u =>
                {
                    Damage(h, u, dmg, false, crit);
                    // Each seed sends a sliver of life to the most hurt ally nearby (less when that is Elara herself).
                    var ally = AlliesNear(h.Team, h.Pos, 520).Where(a => a.Hp < a.MaxHp).OrderBy(a => a.Hp / a.MaxHp).FirstOrDefault();
                    if (ally != null) Heal(h, ally, dmg * (ally == h ? .25f : .35f));
                }, homing: t.Id);
                break;
            case "wren":
                Shoot(h, "arrow", dir, h.Def.Basic.Speed, h.AttackRange + 120, u => Damage(h, u, dmg, false, crit), pierce: 1, radius: 16);
                FennAttack(h, t);
                break;
            case "kael":
                Damage(h, t, dmg, false, crit);
                foreach (var u in EnemiesNear(h.Team, h.Pos, h.Def.Basic.Radius))
                    if (u != t && Vec.Dot((u.Pos - h.Pos).Norm(), h.Facing) > .45f) Damage(h, u, dmg * .6f, false);
                break;
            default:
                Damage(h, t, dmg, false, crit);
                break;
        }
    }

    // ───────────────────────────── casting

    /// <summary>Tries to cast an ability toward <paramref name="aim"/>; returns why not, or null when it went off.</summary>
    public string? Cast(Hero h, int slot, Vec aim)
    {
        if (slot is < 1 or > 4 || h.Dead) return "bad";
        if (Duel && _roundPhase != RoundPhase.Fight) return "wait";
        var def = h.Def.Abilities[slot];
        var m = h.ModsFor(slot);
        if (def.Toggle)
        {
            if (def.Id == "iceBlock" && h.ShellT > 0) { EndShell(h); return null; }
            if (def.Id == "stealth" && h.StealthT > 0) { EndStealth(h, keepEmpower: true); return null; }
        }
        if (h.StunT > 0 || h.ShellT > 0 || h.DashT > 0 || h.CastT > 0) return "busy";
        if (slot == 4 && h.Level < Catalog.UltLevel) return "locked";
        if (h.Cooldowns[slot] > 0) return "cooldown";
        var cost = def.Cost * m.Cost;
        if (h.Mana < cost) return "mana";
        var range = def.Range * m.Reach;

        Unit? target = null;
        if (def.Target == Target.Enemy)
        {
            var inRange = Units.Where(u => Targetable(h, u) && !u.IsStructure && Vec.Dist(u.Pos, h.Pos) <= range + u.Radius + 30).ToList();
            // A stone or tree between the caster and the target blocks the spell: no charging or marking through it.
            target = inRange.Where(u => Sees(h, u)).OrderBy(u => Vec.Dist(u.Pos, aim) - (u.Kind == UnitKind.Hero ? 120 : 0)).FirstOrDefault();
            if (target == null) return inRange.Count > 0 ? "sight" : "target";
        }
        else if (def.Target == Target.Ally)
        {
            target = AlliesNear(h.Team, h.Pos, range).OrderBy(a => Vec.Dist(a.Pos, aim)).FirstOrDefault() ?? h;
        }
        if (def.Target == Target.Point && Vec.Dist(aim, h.Pos) > range) aim = h.Pos + (aim - h.Pos).Norm() * range;
        if ((aim - h.Pos).LenSq < 1) aim = h.Pos + h.Facing * 100;
        if (h.RootT > 0 && def.Effects.Contains("dash")) return "rooted";

        h.Mana -= cost;
        h.Cooldowns[slot] = def.Toggle ? .6f : def.Cooldown * m.Cd;
        if (h.StealthT > 0 && def.Id != "stealth") EndStealth(h, keepEmpower: true);
        if (target != null && target != h) h.Facing = (target.Pos - h.Pos).Norm();
        else if (def.Target != Target.Self) h.Facing = (aim - h.Pos).Norm();

        Fx(new FxDto { E = "cast", U = h.Id, K = def.Id, X = Ri(aim.X), Y = Ri(aim.Y), V = def.Windup > 0 ? Ri(def.Windup * 1000) : null });
        var t = target; var a = aim;
        if (def.Windup > 0) { h.CastT = def.Windup; h.OnCast = () => Execute(h, def, m, t, a); }
        else Execute(h, def, m, t, a);
        return null;
    }

    private void Execute(Hero h, AbilityDef def, Mods m, Unit? target, Vec aim)
    {
        var power = h.Power(def.Slot);
        var radius = def.Radius * m.Reach;
        var range = def.Range * m.Reach;
        var dur = def.Duration * m.Dur;
        var cc = def.Cc * m.Dur;
        var dir = (aim - h.Pos).Norm();
        if (dir == Vec.Zero) dir = h.Facing;

        switch (def.Id)
        {
            // ── Mira
            case "gravity":
                AddZone(h, "gravity", aim, radius, dur, .25f,
                    z => { foreach (var u in EnemiesNear(h.Team, z.Pos, z.Radius)) Damage(h, u, power, true, quiet: true); },
                    z =>
                    {
                        foreach (var u in EnemiesNear(h.Team, z.Pos, z.Radius))
                        {
                            if (u.DashT <= 0 && u.InvulnT <= 0) u.Pos = u.Pos.Toward(z.Pos, 150 * Dt);
                            // Wading out against the pull is slow.
                            Slow(u, .4f, .3f);
                        }
                    });
                break;
            case "sunfire":
            {
                void Explode(Vec p)
                {
                    FxAt("burst", "sunfire", p, radius);
                    // The blast doesn't reach round a stone: whoever hides behind one is safe.
                    foreach (var u in EnemiesNear(h.Team, p, radius)) if (LineOfSight(p, u.Pos)) Damage(h, u, power, true);
                }
                var pr = Shoot(h, "sunfire", dir, def.Speed, range, null, radius: 24);
                pr.OnHit = u => Explode(pr.Pos);
                pr.OnExpire = Explode;
                break;
            }
            case "starguard":
                h.StarCount = 3; h.StarT = dur; h.StarTick = 0;
                break;
            case "starfall":
            {
                var targets = EnemiesNear(h.Team, h.Pos, radius).Where(u => Sees(h, u)).OrderBy(u => u.Kind == UnitKind.Hero ? 0 : 1).ThenBy(u => Vec.Dist(u.Pos, h.Pos)).Take(8).ToList();
                var i = 0;
                foreach (var u in targets)
                {
                    var spot = u.Pos; var delay = .6f + i++ * .08f;
                    FxAt("comet", "starfall", spot, 95, delayMs: Ri(delay * 1000));
                    Later(delay, () =>
                    {
                        FxAt("burst", "comet", spot, 95);
                        foreach (var e in EnemiesNear(h.Team, spot, 95)) Damage(h, e, power, true);
                    });
                }
                if (targets.Count == 0) FxAt("burst", "starfall", h.Pos, radius);
                break;
            }

            // ── Kael
            case "charge":
            {
                var t = target!;
                var to = t.Pos - (t.Pos - h.Pos).Norm() * (t.Radius + h.Radius);
                var dist = Vec.Dist(h.Pos, to);
                h.DashT = MathF.Max(.05f, dist / def.Speed); h.DashVel = (to - h.Pos).Norm() * def.Speed; h.InvulnT = h.DashT + .05f;
                var start = h.Pos;
                h.OnDashEnd = () =>
                {
                    FxAt("burst", "charge", h.Pos, 90);
                    if (t.Alive) { Damage(h, t, power, true); Stun(t, cc); Push(t, start, 60, .15f); }
                    foreach (var u in EnemiesNear(h.Team, h.Pos, 90)) if (u != t) { Damage(h, u, power * .5f, true); Stun(u, cc * .6f); }
                };
                break;
            }
            case "slam":
                FxAt("burst", "slam", h.Pos, radius);
                foreach (var u in EnemiesNear(h.Team, h.Pos, radius)) { Damage(h, u, power, true); Stun(u, cc); }
                break;
            case "guard":
                h.GuardT = dur;
                FxAt("burst", "guard", h.Pos, radius);
                foreach (var u in EnemiesNear(h.Team, h.Pos, radius)) { Damage(h, u, power, true); Push(u, h.Pos, 200); }
                break;
            case "bladestorm":
                h.SpinT = dur;
                AddZone(h, "spin", h.Pos, radius, dur, .3f,
                    z => { foreach (var u in EnemiesNear(h.Team, z.Pos, z.Radius)) Damage(h, u, power, true); },
                    z => { z.Pos = h.Pos; if (h.Dead) z.TimeLeft = 0; });
                break;

            // ── Lyra
            case "blink":
            {
                var from = h.Pos;
                var dist = MathF.Min(range, Vec.Dist(h.Pos, aim));
                var to = h.Pos + dir * dist;
                for (var k = 0; k < 8 && Blocked(to, h.Radius); k++) to = to - dir * (dist / 8);
                h.Pos = to;
                Fx(new FxDto { E = "blink", K = "blink", U = h.Id, X = Ri(from.X), Y = Ri(from.Y), X2 = Ri(to.X), Y2 = Ri(to.Y) });
                FxAt("burst", "frost", from, radius);
                foreach (var u in EnemiesNear(h.Team, from, radius)) { Damage(h, u, power, true); Slow(u, .4f, cc); }
                break;
            }
            case "frostnova":
                FxAt("burst", "frostnova", h.Pos, radius);
                foreach (var u in EnemiesNear(h.Team, h.Pos, radius)) { Damage(h, u, power, true); Root(u, cc); }
                break;
            case "iceBlock":
                h.ShellT = dur;
                h.StunT = h.RootT = h.SlowT = 0;
                break;
            case "blizzard":
                AddZone(h, "blizzard", aim, radius, dur, .5f,
                    z => { foreach (var u in EnemiesNear(h.Team, z.Pos, z.Radius)) Damage(h, u, power, true, quiet: true); },
                    z => { foreach (var u in EnemiesNear(h.Team, z.Pos, z.Radius)) Slow(u, def.Cc, .3f); });
                break;

            // ── Riven
            case "shadowstep":
            {
                var t = target!;
                var from = h.Pos;
                var behind = t.Pos + (t.Pos - h.Pos).Norm() * (t.Radius + h.Radius + 8);
                if (Blocked(behind, h.Radius)) behind = t.Pos - (t.Pos - h.Pos).Norm() * (t.Radius + h.Radius + 8);
                h.Pos = behind; h.Facing = (t.Pos - h.Pos).Norm(); h.TargetId = t.Id; h.AttackTimer = 0;
                h.Empowered = def.Power * m.Power;
                Fx(new FxDto { E = "blink", K = "shadow", U = h.Id, X = Ri(from.X), Y = Ri(from.Y), X2 = Ri(behind.X), Y2 = Ri(behind.Y) });
                break;
            }
            case "knives":
                for (var i = 0; i < 10; i++)
                    Shoot(h, "knife", Vec.FromAngle(i / 10f * MathF.PI * 2), def.Speed, range, u => Damage(h, u, power, true), radius: 14);
                FxAt("burst", "knives", h.Pos, 60);
                break;
            case "stealth":
                h.StealthT = dur;
                h.Empowered = MathF.Max(h.Empowered, def.Power * m.Power);
                foreach (var u in Units) if (u.TargetId == h.Id) u.TargetId = 0;
                foreach (var p in Projectiles) if (p.HomingId == h.Id) p.HomingId = 0;
                FxAt("burst", "stealth", h.Pos, 60);
                break;
            case "deathmark":
            {
                var t = target!;
                t.MarkT = dur;
                Later(dur, () =>
                {
                    if (!t.Alive) return;
                    FxAt("burst", "doom", t.Pos, radius);
                    Damage(h, t, power, true);
                    foreach (var u in EnemiesNear(h.Team, t.Pos, radius)) if (u != t) Damage(h, u, power * .6f, true);
                });
                break;
            }

            // ── Wren
            case "command":
            {
                var t = target!;
                if (Get(h.FennId) is not { } fenn) break;
                if (fenn.Dead) { fenn.Dead = false; fenn.Pos = h.Pos; }
                if (Vec.Dist(fenn.Pos, h.Pos) > 400) fenn.Pos = h.Pos;
                var to = t.Pos - (t.Pos - fenn.Pos).Norm() * (t.Radius + fenn.Radius);
                var speed = 1300f;
                fenn.DashT = MathF.Max(.05f, Vec.Dist(fenn.Pos, to) / speed); fenn.DashVel = (to - fenn.Pos).Norm() * speed;
                fenn.TargetId = t.Id; fenn.Facing = fenn.DashVel.Norm();
                fenn.OnDashEnd = () =>
                {
                    FxAt("burst", "pounce", fenn.Pos, 60);
                    if (t.Alive) { Damage(fenn, t, power, true); Stun(t, cc); }
                };
                break;
            }
            case "volley":
            {
                var shared = new HashSet<int>();
                for (var i = -3; i <= 3; i++)
                {
                    var pr = Shoot(h, "arrow", Vec.FromAngle(dir.Angle + i * .15f), def.Speed, range, u => Damage(h, u, power, true), radius: 16);
                    pr.CastHit = shared;
                }
                break;
            }
            case "leap":
            {
                var nearest = EnemiesNear(h.Team, h.Pos, 650).Where(u => Sees(h, u)).OrderBy(u => u.Kind == UnitKind.Hero ? 0 : 1).ThenBy(u => Vec.Dist(u.Pos, h.Pos)).FirstOrDefault();
                var away = nearest != null ? (h.Pos - nearest.Pos).Norm() : (h.Pos - aim).Norm();
                if (away == Vec.Zero) away = h.Facing * -1;
                var to = h.Pos + away * range;
                for (var k = 0; k < 8 && Blocked(to, h.Radius); k++) to = to - away * (range / 8);
                var time = .35f;
                h.DashT = time; h.DashVel = (to - h.Pos) / time; h.InvulnT = time + .05f;
                if (nearest != null)
                    for (var i = 0; i < 3; i++)
                    {
                        var n = nearest;
                        Later(i * .08f, () => { if (n.Alive) Shoot(h, "arrow", n.Pos - h.Pos, 1200, 900, u => { Damage(h, u, power, true); Root(u, cc); }, homing: n.Id, radius: 16); });
                    }
                break;
            }
            case "wildcall":
            {
                if (Get(h.FennId) is { } fenn) fenn.FrenzyT = dur;
                for (var i = 0; i < 2; i++)
                {
                    var wolf = Add(new Unit
                    {
                        Kind = UnitKind.Pet, Sub = "wolf", Team = h.Team, Pos = h.Pos + new Vec(i == 0 ? -40 : 40, 30), Radius = 18, Speed = 400,
                        AttackRange = 60, AttackCd = .8f, AttackDamage = power, OwnerId = h.Id, LifeT = dur, Hp = 1, MaxHp = 1,
                    });
                    FxAt("burst", "howl", wolf.Pos, 50);
                }
                break;
            }

            // ── Elara
            case "naturebolt":
            {
                var pr = Shoot(h, "naturebolt", dir, def.Speed, range, u => { Damage(h, u, power, true); Root(u, cc); }, radius: 26);
                pr.OnAlly = a => Heal(h, a, power * .6f);
                break;
            }
            case "grove":
                AddZone(h, "grove", aim, radius, dur, .5f,
                    z => { foreach (var a in AlliesNear(h.Team, z.Pos, z.Radius)) Heal(h, a, power); },
                    z => { foreach (var u in EnemiesNear(h.Team, z.Pos, z.Radius)) Slow(u, def.Cc, .3f); });
                break;
            case "barkskin":
            {
                var t = target ?? h;
                t.Shield = power; t.ShieldT = dur;
                FxAt("burst", "barkskin", t.Pos, 50, t.Id);
                break;
            }
            case "awakening":
                FxAt("burst", "bloomcall", aim, radius, delayMs: 500);
                Later(.5f, () =>
                {
                    FxAt("burst", "bloom", aim, radius);
                    foreach (var a in AlliesNear(h.Team, aim, radius)) { a.StunT = a.RootT = a.SlowT = 0; Heal(h, a, power); }
                    foreach (var u in EnemiesNear(h.Team, aim, radius)) { Damage(h, u, power * .55f, true); Root(u, cc); }
                });
                break;
        }
    }

    // ───────────────────────────── lasting effects

    private void EndShell(Hero h)
    {
        h.ShellT = 0;
        h.Cooldowns[3] = h.Def.Abilities[3].Cooldown * h.ModsFor(3).Cd;
        FxAt("burst", "shellbreak", h.Pos, 60, h.Id);
    }

    private void EndStealth(Hero h, bool keepEmpower = false)
    {
        h.StealthT = 0;
        if (!keepEmpower) h.Empowered = 0;
        var slot = Array.FindIndex(h.Def.Abilities, a => a.Id == "stealth");
        if (slot > 0) h.Cooldowns[slot] = h.Def.Abilities[slot].Cooldown * h.ModsFor(slot).Cd;
        FxAt("burst", "unveil", h.Pos, 50, h.Id);
    }

    /// <summary>Guardian Stars: burn whatever they touch; any left when they fade fly at the nearest enemies.</summary>
    private void UpdateStars(Unit u)
    {
        if (u is not Hero h) return;
        if (h.Dead) { h.StarCount = 0; return; }
        h.StarT -= Dt;
        h.StarTick -= Dt;
        var power = h.Power(3);
        if (h.StarTick <= 0)
        {
            h.StarTick = .5f;
            var r = h.Def.Abilities[3].Radius * h.ModsFor(3).Reach;
            foreach (var e in EnemiesNear(h.Team, h.Pos, r + 16)) Damage(h, e, power * .5f, true, quiet: false, fromStar: true);
        }
        if (h.StarT > 0) return;
        var left = h.StarCount; h.StarCount = 0;
        var targets = EnemiesNear(h.Team, h.Pos, 600).Where(e => Sees(h, e)).OrderBy(e => Vec.Dist(e.Pos, h.Pos)).ToList();
        for (var i = 0; i < left && targets.Count > 0; i++)
        {
            var t = targets[i % targets.Count];
            Shoot(h, "star", t.Pos - h.Pos, 800, 900, e => Damage(h, e, power, true, fromStar: true), homing: t.Id);
        }
    }

    private void FennAttack(Hero wren, Unit target)
    {
        if (Get(wren.FennId) is { Dead: false } fenn && fenn.DashT <= 0 && !target.IsStructure) fenn.TargetId = target.Id;
    }
}
