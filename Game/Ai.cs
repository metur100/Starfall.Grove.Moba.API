namespace Starfall.Grove.Moba.Api.Game;

// Simple, deterministic behaviour for everything no player controls: minions, neutral monsters, towers and Cores,
// Fenn and the spirit wolves, and bots that stand in for missing or disconnected players.
public sealed partial class Match
{
    // ───────────────────────────── minions

    private void MinionAi(Unit u)
    {
        if (u.AttackTimer > 0) u.AttackTimer -= Dt;
        if (!u.CanAct) return;
        var t = Get(u.TargetId);
        if (t == null || !Targetable(u, t) || t.Team == 0 || Vec.Dist(t.Pos, u.Pos) > 520) t = MinionTarget(u);
        u.TargetId = t?.Id ?? 0;
        if (t != null)
        {
            if (Vec.Dist(u.Pos, t.Pos) <= u.AttackRange + t.Radius + u.Radius * .5f) UnitAttack(u, t);
            else if (u.CanMove) Step(u, t.Pos);
            return;
        }
        if (u.Path == null || !u.CanMove) return;
        // Move on to the next waypoint when close, or once past it (a waypoint can sit inside a tower).
        while (u.PathIndex < u.Path.Count - 1)
        {
            var wp = u.Path[u.PathIndex];
            var ahead = u.Path[u.PathIndex + 1] - wp;
            if (Vec.Dist(u.Pos, wp) < 90 || Vec.Dot(u.Pos - wp, ahead) > 0) u.PathIndex++;
            else break;
        }
        Step(u, u.Path[u.PathIndex]);
    }

    /// <summary>Minions fight minions first, then heroes who hurt their heroes, then structures, then other heroes.</summary>
    private Unit? MinionTarget(Unit u)
    {
        Unit? best = null; var bestScore = float.MaxValue;
        foreach (var e in Units)
        {
            if (e.Team == 0 || !Targetable(u, e)) continue;
            var d = Vec.Dist(u.Pos, e.Pos);
            if (d > 430 + e.Radius) continue;
            var score = e.Kind switch
            {
                UnitKind.Minion => d,
                UnitKind.Hero => e.AggroT > 0 ? d - 80 : d + 260,
                _ => d + 120,
            };
            if (score < bestScore) { bestScore = score; best = e; }
        }
        return best;
    }

    private void Step(Unit u, Vec goal)
    {
        var d = goal - u.Pos;
        if (d.LenSq < 4) return;
        u.Facing = d.Norm();
        u.Pos = u.Pos.Toward(goal, u.MoveSpeed * Dt);
    }

    /// <summary>A plain attack by a minion, monster, tower or pet: melee hits at once, ranged attackers shoot.</summary>
    private void UnitAttack(Unit u, Unit t)
    {
        if (u.AttackTimer > 0) return;
        u.AttackTimer = u.AttackCd * (u.FrenzyT > 0 ? .5f : 1);
        u.Facing = (t.Pos - u.Pos).Norm();
        var dmg = u.AttackDamage * (u.FrenzyT > 0 ? 1.6f : 1);
        Fx(new FxDto { E = "atk", U = u.Id, K = u.Sub, X = Ri(t.Pos.X), Y = Ri(t.Pos.Y) });
        switch (u.Sub)
        {
            case "ranged": Shoot(u, "bolt", t.Pos - u.Pos, 700, 700, e => Damage(u, e, dmg, false), homing: t.Id, radius: 10); break;
            case "thornling": Shoot(u, "thorn", t.Pos - u.Pos, 650, 600, e => Damage(u, e, dmg, false), homing: t.Id, radius: 10); break;
            case "warden":
                Shoot(u, "wardenbolt", t.Pos - u.Pos, 620, 700, e =>
                {
                    FxAt("burst", "warden", e.Pos, 110);
                    foreach (var x in Units.Where(x => x.Alive && x.Team != 0 && !x.IsStructure && x.Kind != UnitKind.Pet && Vec.Dist(x.Pos, e.Pos) < 110 + x.Radius).ToList())
                        Damage(u, x, x == e ? dmg : dmg * .5f, true);
                }, homing: t.Id, radius: 18);
                break;
            default: Damage(u, t, dmg, false); break;
        }
    }

    // ───────────────────────────── neutral monsters

    private void MonsterAi(Unit u)
    {
        if (u.AttackTimer > 0) u.AttackTimer -= Dt;
        if (u.AggroT > 0) u.AggroT -= Dt;
        var t = Get(u.TargetId);
        var leash = u.Sub == "warden" ? 380 : 430;
        if (t != null && (!t.Alive || !Targetable(u, t) || u.AggroT <= 0 || Vec.Dist(t.Pos, u.Home) > leash + 150 || Vec.Dist(u.Pos, u.Home) > leash))
        {
            t = null; u.TargetId = 0;
        }
        if (!u.CanAct) return;
        if (t == null)
        {
            // Walk home and heal up; a pulled monster resets.
            if (Vec.Dist(u.Pos, u.Home) > 8) { Step(u, u.Home); u.Hp = MathF.Min(u.MaxHp, u.Hp + u.MaxHp * .2f * Dt); }
            else { u.Hp = MathF.Min(u.MaxHp, u.Hp + u.MaxHp * .1f * Dt); u.Attackers.Clear(); }
            return;
        }
        if (Vec.Dist(u.Pos, t.Pos) <= u.AttackRange + t.Radius + u.Radius * .5f) UnitAttack(u, t);
        else if (u.CanMove) Step(u, t.Pos);
    }

    // ───────────────────────────── towers and Cores

    private void StructureAi(Unit s)
    {
        if (s.AttackTimer > 0) s.AttackTimer -= Dt;
        // The Core sleeps until its last tower falls, so a wave that breaks Tower 2 can reach it.
        if (s.Kind == UnitKind.Core && Protected(s)) { s.TargetId = 0; return; }
        var range = s.AttackRange;
        bool Valid(Unit? t) => t != null && t.Team != 0 && Targetable(s, t) && Vec.Dist(t.Pos, s.Pos) <= range + t.Radius;
        var cur = Get(s.TargetId);
        // An enemy hero who hurts a hero under this tower becomes its target at once.
        var bully = Heroes.FirstOrDefault(h => h.Team != s.Team && h.AggroT > 0 && Valid(h) && Heroes.Any(a => a.Team == s.Team && a.Alive && Vec.Dist(a.Pos, s.Pos) < range + 100));
        if (bully != null && cur != bully) { cur = bully; s.Hits = 0; }
        if (!Valid(cur))
        {
            cur = Units.Where(u => u.Kind == UnitKind.Minion && Valid(u)).OrderBy(u => Vec.Dist(u.Pos, s.Pos)).FirstOrDefault()
                ?? (Unit?)Heroes.Where(h => Valid(h)).OrderBy(h => Vec.Dist(h.Pos, s.Pos)).FirstOrDefault();
            s.Hits = 0;
        }
        s.TargetId = cur?.Id ?? 0;
        if (cur == null || s.AttackTimer > 0) return;
        s.AttackTimer = s.AttackCd;
        var t = cur;
        var dmg = t.Kind == UnitKind.Hero ? s.AttackDamage * (1 + .25f * Math.Min(4, s.Hits)) : s.Kind == UnitKind.Core ? 80 : 95;
        if (t.Kind == UnitKind.Hero) s.Hits++;
        Fx(new FxDto { E = "atk", U = s.Id, K = s.Sub, X = Ri(t.Pos.X), Y = Ri(t.Pos.Y) });
        var pr = Shoot(s, "towerbolt", t.Pos - s.Pos, 760, range + 300, e => Damage(s, e, dmg, false), homing: t.Id, radius: 14);
        pr.Pos = s.Pos + new Vec(0, s.Kind == UnitKind.Core ? -90 : -110);
    }

    // ───────────────────────────── Fenn and the spirit wolves

    private void PetAi(Unit p)
    {
        var owner = Get(p.OwnerId) as Hero;
        if (owner == null || owner.Dead) { if (p.Sub == "fenn") p.Dead = true; else p.LifeT = Math.Min(p.LifeT, .01f); return; }
        if (p.DashT > 0) return;
        if (p.AttackTimer > 0) p.AttackTimer -= Dt;
        var leash = 650f;
        bool Valid(Unit? t) => t != null && Targetable(p, t) && !t.IsStructure && Vec.Dist(t.Pos, owner.Pos) < leash;
        var t = Get(p.TargetId);
        if (!Valid(t))
        {
            t = null;
            var ot = Get(owner.TargetId);
            if (Valid(ot) && owner.AttackTimer > -2) t = ot;
            if (t == null && p.Sub == "wolf")
                t = Units.Where(u => Valid(u) && Vec.Dist(u.Pos, owner.Pos) < 450).OrderBy(u => u.Kind == UnitKind.Hero ? 0 : 1).ThenBy(u => Vec.Dist(u.Pos, p.Pos)).FirstOrDefault();
        }
        p.TargetId = t?.Id ?? 0;
        if (t != null)
        {
            if (Vec.Dist(p.Pos, t.Pos) <= p.AttackRange + t.Radius)
            {
                if (p.Sub == "fenn") p.AttackDamage = 8 + 2 * owner.Level;
                UnitAttack(p, t);
            }
            else Step(p, t.Pos);
            return;
        }
        var side = owner.Facing.X >= 0 ? -1 : 1;
        var heel = owner.Pos + new Vec(side * 40, 24);
        var d = Vec.Dist(p.Pos, heel);
        if (d > 900) p.Pos = heel;
        else if (d > 40) { p.Facing = (heel - p.Pos).Norm(); p.Pos = p.Pos.Toward(heel, (d > 200 ? p.Speed * 1.4f : p.Speed) * Dt); }
        else p.Facing = owner.Facing;
    }

    // ───────────────────────────── bots

    private sealed class BotState { public float ThinkT; public bool Retreat; public Vec LastPos; public float StuckT; public Vec Nudge; public float NudgeT; }
    private readonly Dictionary<int, BotState> _bots = [];

    /// <summary>Steers a hero no human is driving: lane with the minions, fight enemy heroes when it can win, fall
    /// back to heal when hurt, and spend gold on upgrades.</summary>
    public void DriveBot(Hero h)
    {
        if (!_bots.TryGetValue(h.Id, out var s)) _bots[h.Id] = s = new BotState { ThinkT = _rng.Range(0, .3f), LastPos = h.Pos };
        if (h.Dead) { h.MoveDir = Vec.Zero; h.AttackHeld = false; return; }
        if (s.NudgeT > 0) { s.NudgeT -= Dt; h.MoveDir = s.Nudge; return; }
        s.ThinkT -= Dt;
        if (s.ThinkT > 0) return;
        s.ThinkT = .2f + _rng.Next() * .1f;

        // Unstick: if it meant to move but barely did, side-step for a moment.
        if (h.MoveDir.LenSq > .1f && Vec.Dist(h.Pos, s.LastPos) < 8) s.StuckT += .25f; else s.StuckT = 0;
        s.LastPos = h.Pos;
        if (s.StuckT > .75f) { s.StuckT = 0; s.Nudge = new Vec(-h.MoveDir.Y, h.MoveDir.X) * (_rng.Next() < .5f ? 1 : -1); s.NudgeT = .5f; return; }

        BotUpgrade(h);
        if (Duel) { DuelBot(h); return; }
        var hp = h.Hp / h.MaxHp;
        if (hp < .3f) s.Retreat = true;
        if (hp > .9f) s.Retreat = false;
        var forward = new Vec(h.Team == 1 ? 1 : -1, 0);
        var foe = Heroes.Where(e => e.Team != h.Team && Targetable(h, e) && Vec.Dist(e.Pos, h.Pos) < 700).OrderBy(e => Vec.Dist(e.Pos, h.Pos)).FirstOrDefault();

        if (s.Retreat)
        {
            h.AttackHeld = false;
            if (foe != null && Vec.Dist(foe.Pos, h.Pos) < 300) TryBotCast(h, foe, escaping: true);
            MoveAlongLane(h, Map.Spawn[h.Team]);
            return;
        }

        if (foe != null && (hp > .45f || foe.Hp / foe.MaxHp < .3f) && !(UnderEnemyTower(foe.Pos, h.Team) && !MinionsTanking(foe.Pos, h.Team)))
        {
            h.TargetId = foe.Id;
            TryBotCast(h, foe, escaping: false);
            var d = Vec.Dist(foe.Pos, h.Pos);
            if (!LineOfSight(h.Pos, foe.Pos)) { h.MoveDir = SteerAround(h.Pos, foe.Pos); h.AttackHeld = false; }
            else if (d > h.AttackRange + foe.Radius - 10) { h.MoveDir = (foe.Pos - h.Pos).Norm(); h.AttackHeld = false; }
            else { h.MoveDir = Vec.Zero; h.AttackHeld = true; }
            return;
        }

        // Laning: stand just behind the front of our wave, or behind our outer tower when there is no wave.

        Unit? front = Units.Where(u => u.Kind == UnitKind.Minion && u.Team == h.Team && u.Alive && u.Lane == h.Lane).OrderByDescending(u => u.PathIndex).ThenByDescending(u => u.Pos.X * forward.X).FirstOrDefault();
        Vec goal;
        if (front != null) goal = front.Pos - forward * (h.Def.Melee ? 40 : 170);
        else
        {
            var tower = _towers[h.Team][h.Lane].FirstOrDefault(t => t.Alive) ?? _cores[h.Team]!;
            goal = tower.Pos - forward * 120;
        }
        if (UnderEnemyTower(goal, h.Team) && !MinionsTanking(goal, h.Team)) goal -= forward * 380;

        // Clear a big group of minions with an ability when there is magic to spare.
        if (h.Mana > h.MaxMana * .65f)
        {
            var pack = Units.Where(u => u.Kind == UnitKind.Minion && u.Team != h.Team && u.Alive && Vec.Dist(u.Pos, h.Pos) < 380).ToList();
            if (pack.Count >= 3) { var c = new Vec(pack.Average(u => u.Pos.X), pack.Average(u => u.Pos.Y)); TryBotCast(h, null, false, c, onlySlot: 2); }
        }

        if (Vec.Dist(h.Pos, goal) > 90) { MoveAlongLane(h, goal); h.AttackHeld = false; }
        else { h.MoveDir = Vec.Zero; h.AttackHeld = true; }
    }

    private bool UnderEnemyTower(Vec p, int team) =>
        Units.Any(u => u.IsStructure && u.Alive && u.Team != team && Vec.Dist(u.Pos, p) < u.AttackRange + 40);

    private bool MinionsTanking(Vec p, int team) =>
        Units.Count(u => u.Kind == UnitKind.Minion && u.Team == team && u.Alive && Vec.Dist(u.Pos, p) < 520) >= 2;

    private void MoveAlongLane(Hero h, Vec goal)
    {
        var lane = Map.Lanes[h.Lane];
        int Nearest(Vec p) { var best = 0; var bd = float.MaxValue; for (var i = 0; i < lane.Count; i++) { var d = Vec.DistSq(lane[i], p); if (d < bd) { bd = d; best = i; } } return best; }
        var ih = Nearest(h.Pos); var ig = Nearest(goal);
        Vec next;
        if (Vec.Dist(h.Pos, lane[ih]) > 200 && Math.Abs(ih - ig) > 2) next = lane[ih];
        else if (Math.Abs(ih - ig) <= 2) next = goal;
        else next = lane[Math.Clamp(ih + Math.Sign(ig - ih) * 3, 0, lane.Count - 1)];
        h.MoveDir = (next - h.Pos).Norm();
    }

    private void TryBotCast(Hero h, Hero? foe, bool escaping, Vec? at = null, int onlySlot = 0)
    {
        for (var slot = 4; slot >= 1; slot--)
        {
            if (onlySlot != 0 && slot != onlySlot) continue;
            var def = h.Def.Abilities[slot];
            if (h.Cooldowns[slot] > 0 || h.Mana < def.Cost * h.ModsFor(slot).Cost || (slot == 4 && h.Level < Catalog.UltLevel)) continue;
            var target = foe?.Pos ?? at ?? h.Pos + h.Facing * 200;
            var d = Vec.Dist(target, h.Pos);
            var range = def.Range * h.ModsFor(slot).Reach;
            var radius = def.Radius * h.ModsFor(slot).Reach;
            var want = def.Id switch
            {
                "iceBlock" => h.Hp / h.MaxHp < .3f && foe != null,
                "leap" or "blink" => escaping || (def.Id == "blink" && d < 200 && foe != null),
                "stealth" => foe != null && !escaping && d < 450,
                "guard" => foe != null && d < 200 || h.Hp / h.MaxHp < .4f && foe != null,
                "starguard" or "wildcall" or "bladestorm" => foe != null && d < (def.Id == "bladestorm" ? 200 : 450),
                "barkskin" => Heroes.Any(a => a.Team == h.Team && a.Alive && a.Hp / a.MaxHp < .7f && Vec.Dist(a.Pos, h.Pos) < range),
                "grove" => Heroes.Any(a => a.Team == h.Team && a.Alive && a.Hp / a.MaxHp < .75f && Vec.Dist(a.Pos, h.Pos) < range),
                _ when def.Target == Target.Self => d < MathF.Max(radius, 120) * .85f,
                _ => d < MathF.Max(range, 150),
            };
            if (escaping && def.Id is not ("leap" or "blink" or "iceBlock" or "guard" or "stealth" or "barkskin")) want = false;
            if (!want) continue;
            var aim = def.Id == "barkskin" || def.Id == "grove"
                ? (Heroes.Where(a => a.Team == h.Team && a.Alive && Vec.Dist(a.Pos, h.Pos) < range).OrderBy(a => a.Hp / a.MaxHp).FirstOrDefault()?.Pos ?? h.Pos)
                : def.Id == "blink" && escaping ? h.Pos + (h.Pos - target).Norm() * 300
                : target;
            if (Cast(h, slot, aim) == null) return;
        }
    }

    private void BotUpgrade(Hero h)
    {
        foreach (var slot in new[] { 4, 1, 2, 0, 3 })
        {
            var tier = h.Picks[slot].Count;
            if (tier >= 3 || (slot == 4 && h.Level < Catalog.UltLevel)) continue;
            if (h.GoldBank < Upgrades.Cost(slot, tier)) continue;
            BuyUpgrade(h, slot, (h.Id + tier) % 2);
            return;
        }
    }
}
