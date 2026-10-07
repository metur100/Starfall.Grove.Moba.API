namespace Starfall.Grove.Moba.Api.Game;

// The charm every hero brings (Flash, Heal, Ghost or Barrier, picked in hero select), recalling home on the
// battlefield, and the Starshard that falls into the middle of a duel ring partway through every round.
public sealed partial class Match
{
    public const float RecallTime = 4.5f, FlashRange = 260;
    /// <summary>The Starshard falls this long into a round; whoever reaches it first is blessed for a few seconds.</summary>
    public const float ShardAt = 14, ShardBless = 6, ShardRadius = 60;
    private bool _shardUp, _shardTaken;

    public float CharmCooldown(Hero h) => Economy.CharmById.TryGetValue(h.Charm, out var c) ? (Duel ? c.DuelCooldown : c.Cooldown) : 90;

    /// <summary>Uses the hero's charm toward <paramref name="aim"/>; returns why not, or null when it went off.</summary>
    public string? UseCharm(Hero h, Vec aim)
    {
        if (h.Dead) return "bad";
        if (Duel && _roundPhase != RoundPhase.Fight) return "wait";
        if (h.CharmCd > 0) return "cooldown";
        if (h.StunT > 0 || h.ShellT > 0 || h.DashT > 0 || h.CastT > 0) return "busy";
        CancelRecall(h, quiet: true);
        switch (h.Charm)
        {
            case "flash":
            {
                // Works while rooted: that is what it is for.
                var dir = (aim - h.Pos).Norm();
                if (dir == Vec.Zero) dir = h.Facing;
                var dist = Math.Clamp(Vec.Dist(aim, h.Pos), 120, FlashRange);
                var from = h.Pos;
                var to = h.Pos + dir * dist;
                for (var k = 0; k < 10 && Blocked(to, h.Radius); k++) to -= dir * (dist / 10);
                to = new Vec(Math.Clamp(to.X, 50, Map.W - 50), Math.Clamp(to.Y, 50, Map.H - 50));
                h.Pos = to; h.Facing = dir;
                Fx(new FxDto { E = "blink", K = "flash", U = h.Id, X = Ri(from.X), Y = Ri(from.Y), X2 = Ri(to.X), Y2 = Ri(to.Y) });
                break;
            }
            case "heal":
            {
                Heal(h, h, h.MaxHp * .15f);
                h.HasteT = 1.5f; h.HasteAmt = MathF.Max(h.HasteAmt, .25f);
                var ally = AlliesNear(h.Team, h.Pos, 600).Where(a => a != h && a.Hp < a.MaxHp).OrderBy(a => a.Hp / a.MaxHp).FirstOrDefault();
                if (ally != null) { Heal(h, ally, ally.MaxHp * .15f); ally.HasteT = 1.5f; ally.HasteAmt = MathF.Max(ally.HasteAmt, .25f); FxAt("burst", "heal", ally.Pos, 70, ally.Id); }
                FxAt("burst", "heal", h.Pos, 70, h.Id);
                break;
            }
            case "ghost":
                h.HasteT = 5; h.HasteAmt = .32f;
                FxAt("burst", "ghost", h.Pos, 60, h.Id);
                break;
            case "barrier":
                h.Shield = MathF.Max(h.ShieldT > 0 ? h.Shield : 0, h.MaxHp * .2f); h.ShieldT = MathF.Max(h.ShieldT, 2.5f);
                FxAt("burst", "barrier", h.Pos, 70, h.Id);
                break;
            default: return "bad";
        }
        h.CharmCd = CharmCooldown(h);
        Fx(new FxDto { E = "charm", K = h.Charm, U = h.Id });
        return null;
    }

    // ───────────────────────────── recall

    /// <summary>Starts recalling home (or stops, when already recalling). Battles only; moving, attacking, casting or
    /// being hurt breaks it.</summary>
    public string? Recall(Hero h)
    {
        if (Duel) return "duel";
        if (h.Dead) return "bad";
        if (h.RecallT > 0) { CancelRecall(h); return null; }
        if (h.StunT > 0 || h.CastT > 0 || h.DashT > 0 || h.ShellT > 0) return "busy";
        if (Vec.Dist(h.Pos, Map.Spawn[h.Team]) < MapDef.FountainRadius) return "home";
        h.RecallT = RecallTime;
        h.MoveDir = Vec.Zero; h.AttackHeld = false;
        Fx(new FxDto { E = "recall", K = "start", U = h.Id, V = Ri(RecallTime * 1000) });
        return null;
    }

    private void CancelRecall(Hero h, bool quiet = false)
    {
        if (h.RecallT <= 0) return;
        h.RecallT = 0;
        if (!quiet) Fx(new FxDto { E = "recall", K = "cancel", U = h.Id });
    }

    /// <summary>Counts a recall down; true while the hero is busy recalling (and does nothing else).</summary>
    private bool UpdateRecall(Hero h)
    {
        if (h.RecallT <= 0) return false;
        if (h.MoveDir.LenSq > .01f || h.AttackHeld || h.StunT > 0) { CancelRecall(h); return false; }
        h.RecallT -= Dt;
        if (h.RecallT > 0) return true;
        h.RecallT = 0;
        var from = h.Pos;
        h.Pos = SpawnPoint(h.Team, Heroes.Where(x => x.Team == h.Team).ToList().IndexOf(h));
        if (Get(h.FennId) is { Dead: false } fenn) fenn.Pos = h.Pos + new Vec(-30, 20);
        Fx(new FxDto { E = "recall", K = "done", U = h.Id, X = Ri(from.X), Y = Ri(from.Y), X2 = Ri(h.Pos.X), Y2 = Ri(h.Pos.Y) });
        return true;
    }

    // ───────────────────────────── the duel's Starshard

    private void UpdateShard()
    {
        if (!_shardTaken && !_shardUp && _roundElapsed >= ShardAt)
        {
            _shardUp = true;
            Fx(new FxDto { E = "notice", K = "shard" });
        }
        if (!_shardUp) return;
        var h = Heroes.Where(x => x.Alive && Vec.Dist(x.Pos, Map.Center) < ShardRadius + x.Radius).OrderBy(x => Vec.Dist(x.Pos, Map.Center)).FirstOrDefault();
        if (h == null) return;
        _shardUp = false; _shardTaken = true;
        h.BlessT = MathF.Max(h.BlessT, ShardBless);
        Heal(h, h, h.MaxHp * .15f);
        Fx(new FxDto { E = "shard", U = h.Id, Tm = h.Team, X = Ri(Map.Center.X), Y = Ri(Map.Center.Y) });
    }

    // ───────────────────────────── bots

    /// <summary>Bots use their charm to escape, to save themselves or a friend, or to run down a fleeing enemy.</summary>
    private void TryBotCharm(Hero h)
    {
        if (h.CharmCd > 0 || h.Dead || (Duel && _roundPhase != RoundPhase.Fight)) return;
        var foe = Heroes.Where(e => e.Team != h.Team && e.Alive && Targetable(h, e)).OrderBy(e => Vec.Dist(e.Pos, h.Pos)).FirstOrDefault();
        var hp = h.Hp / h.MaxHp;
        var near = foe != null && Vec.Dist(foe.Pos, h.Pos) < 450;
        var want = h.Charm switch
        {
            "heal" => (hp < .3f && near) || Heroes.Any(a => a.Team == h.Team && a != h && a.Alive && a.Hp / a.MaxHp < .25f && Vec.Dist(a.Pos, h.Pos) < 500),
            "barrier" => hp < .3f && near,
            "ghost" => (hp < .3f && near) || (foe != null && hp > .5f && foe.Hp / foe.MaxHp < .25f && Vec.Dist(foe.Pos, h.Pos) > h.AttackRange + 80 && Vec.Dist(foe.Pos, h.Pos) < 700),
            "flash" => hp < .2f && near,
            _ => false,
        };
        if (!want) return;
        var aim = h.Charm == "flash" && foe != null ? h.Pos + (h.Pos - foe.Pos).Norm() * FlashRange : h.Pos;
        UseCharm(h, aim);
    }
}
