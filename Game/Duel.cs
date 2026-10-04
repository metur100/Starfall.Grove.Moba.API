namespace Starfall.Grove.Moba.Api.Game;

public enum RoundPhase { Countdown, Fight, Over }

// Duels: heroes only, in a small ring, no minions, towers or camps. A round ends when one side has nobody standing;
// the first team to win three rounds wins the duel. Between rounds everyone is restored, gets gold for upgrades, and
// goes back to their side. A ring of starfire closes in late in a round, so nobody can hide forever.
public sealed partial class Match
{
    public const int DuelStartGold = 500, DuelStartLevel = 6, RoundsToWin = 3;
    public const float CountdownTime = 3, BreakTime = 4, RingStart = 35, RingShrink = 25, RingMin = 170;
    public const int RoundGold = 300, LoserBonus = 150;
    public const float DuelHealth = 3f;

    private int _round;
    private readonly int[] _roundWins = new int[3];
    private RoundPhase _roundPhase;
    private float _roundTimer, _roundElapsed, _ringR;

    private void StartRound()
    {
        _round++;
        _roundPhase = RoundPhase.Countdown;
        _roundTimer = CountdownTime;
        _roundElapsed = 0;
        _ringR = Map.ArenaRadius + 40;
        Projectiles.Clear(); Zones.Clear(); _delayed.Clear();
        foreach (var w in Units.Where(u => u.Kind == UnitKind.Pet && u.Sub != "fenn")) w.Dead = true;
        var slot = new int[3];
        foreach (var h in Heroes)
        {
            h.Dead = false; h.Hp = h.MaxHp; h.Mana = h.MaxMana;
            h.Pos = SpawnPoint(h.Team, slot[h.Team]++);
            h.Facing = (Map.Center - h.Pos).Norm();
            h.StunT = h.RootT = h.SlowT = h.InvulnT = h.StealthT = h.GuardT = h.SpinT = h.ShieldT = h.Shield = h.ShellT = 0;
            h.BlessT = h.FrenzyT = h.MarkT = h.DashT = h.PushT = h.CastT = h.AttackTimer = h.Empowered = 0;
            h.StarCount = 0; h.TargetId = 0; h.OnCast = null; h.OnDashEnd = null;
            Array.Clear(h.Cooldowns);
            h.Attackers.Clear();
            if (Get(h.FennId) is { } fenn) { fenn.Dead = false; fenn.Pos = h.Pos + new Vec(-30, 20); fenn.TargetId = 0; fenn.FrenzyT = 0; }
        }
        Fx(new FxDto { E = "round", K = "start", V = _round });
    }

    private void UpdateRound()
    {
        switch (_roundPhase)
        {
            case RoundPhase.Countdown:
                _roundTimer -= Dt;
                if (_roundTimer > 0) return;
                _roundPhase = RoundPhase.Fight;
                Fx(new FxDto { E = "round", K = "fight", V = _round });
                return;

            case RoundPhase.Fight:
                _roundElapsed += Dt;
                if (_roundElapsed > RingStart)
                {
                    var full = Map.ArenaRadius + 40;
                    var k = Math.Clamp((_roundElapsed - RingStart) / RingShrink, 0, 1);
                    if (_ringR >= full - 1 && k > 0) Fx(new FxDto { E = "notice", K = "ring" });
                    _ringR = full - (full - RingMin) * k;
                    foreach (var h in Heroes.Where(h => h.Alive && Vec.Dist(h.Pos, Map.Center) > _ringR))
                        Damage(null, h, h.MaxHp * .1f * Dt, true, quiet: true);
                }
                bool Standing(int team) => Heroes.Any(h => h.Team == team && h.Alive);
                bool blue = Standing(1), red = Standing(2);
                if (blue && red) return;
                var winner = blue ? 1 : red ? 2 : 0;
                if (winner != 0) _roundWins[winner]++;
                Fx(new FxDto { E = "round", K = winner == 0 ? "draw" : "won", Tm = winner, V = _round });
                if (winner != 0 && _roundWins[winner] >= RoundsToWin) { Winner = winner; return; }
                foreach (var h in Heroes) h.GoldBank += RoundGold + (winner != 0 && h.Team != winner ? LoserBonus : 0);
                _roundPhase = RoundPhase.Over;
                _roundTimer = BreakTime;
                return;

            case RoundPhase.Over:
                _roundTimer -= Dt;
                if (_roundTimer <= 0) StartRound();
                return;
        }
    }

    /// <summary>A duel bot: go for the nearest enemy, keep a ranged hero at range, stay inside the ring.</summary>
    private void DuelBot(Hero h)
    {
        var foe = Heroes.Where(e => e.Team != h.Team && e.Alive && Targetable(h, e)).OrderBy(e => Vec.Dist(e.Pos, h.Pos)).FirstOrDefault();
        var fromCenter = Vec.Dist(h.Pos, Map.Center);
        if (foe == null || fromCenter > _ringR - 90)
        {
            h.MoveDir = fromCenter > 60 ? (Map.Center - h.Pos).Norm() : Vec.Zero;
            h.AttackHeld = foe != null;
            if (foe == null) return;
        }
        h.TargetId = foe.Id;
        TryBotCast(h, foe, escaping: h.Hp / h.MaxHp < .25f);
        var d = Vec.Dist(foe.Pos, h.Pos);
        var reach = h.AttackRange + foe.Radius - 10;
        if (d > reach) { h.MoveDir = (foe.Pos - h.Pos).Norm(); h.AttackHeld = false; }
        else if (!h.Def.Melee && d < reach * .55f && h.AttackTimer > .2f)
        {
            // Step back between shots.
            var away = (h.Pos - foe.Pos).Norm();
            h.MoveDir = Vec.Dist(h.Pos + away * 100, Map.Center) < _ringR - 100 ? away : new Vec(-away.Y, away.X);
            h.AttackHeld = true;
        }
        else { h.MoveDir = Vec.Zero; h.AttackHeld = true; }
    }
}
