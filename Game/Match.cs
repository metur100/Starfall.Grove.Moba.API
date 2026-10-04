namespace Starfall.Grove.Moba.Api.Game;

/// <summary>A player taking part in a match, as the room hands them over.</summary>
public sealed record MatchPlayer(string Id, string Name, string Hero, int Team, bool Bot);

/// <summary>
/// The authoritative simulation of one match. The room calls <see cref="Tick"/> 30 times a second and sends
/// snapshots from it; players only send intentions (move this way, attack, cast here, buy this upgrade).
/// Split over several files: this one has the world and the rules, Abilities.cs every hero's kit, Ai.cs the
/// minions, monsters, towers and bots.
/// </summary>
public sealed partial class Match
{
    public const float Dt = 1f / 30;
    public const float SuddenDeath1 = 360, SuddenDeath2 = 480, SuddenDeath3 = 600;
    public const float WaveInterval = 25, FirstWave = 8;
    public const float ObjectiveFirst = 90, ObjectiveRespawn = 100, CampRespawn = 50, PlantRespawn = 30;

    public readonly MapDef Map;
    public float Time;
    public int Winner;
    public readonly int[] Score = new int[3];

    private int _nextId = 1;
    private readonly Rng _rng;
    public readonly List<Unit> Units = [];
    private readonly Dictionary<int, Unit> _byId = [];
    public readonly List<Hero> Heroes = [];
    public readonly List<Projectile> Projectiles = [];
    public readonly List<Zone> Zones = [];
    private readonly List<Delayed> _delayed = [];
    private readonly List<FxDto> _fx = [];
    private readonly List<Unit> _spawnQueue = [];

    /// <summary>[team][lane][0] the lane's outer tower, [1] its inner one. Empty in a duel.</summary>
    private readonly Unit[][][] _towers = [[], [], []];
    private readonly Unit?[] _cores = new Unit?[3];
    public bool Duel => Map.Duel;
    private readonly float[] _plantAt;
    private readonly float[] _campAt;
    private float _objectiveAt = ObjectiveFirst;
    private Unit? _warden;
    private float _waveAt = FirstWave;
    private int _wave;
    private int _suddenStage;
    private readonly ObstacleGrid _grid;

    public Match(MapDef map, IEnumerable<MatchPlayer> players, int seed)
    {
        Map = map;
        _rng = new Rng(seed);
        _grid = new ObstacleGrid(map);
        _plantAt = new float[map.Plants.Count];
        _campAt = new float[map.Camps.Count];
        for (var i = 0; i < _campAt.Length; i++) _campAt[i] = 15;

        // More lanes means more towers to break, so each one is a little weaker.
        var lanes = map.Lanes.Count;
        var towerScale = lanes switch { 3 => .7f, 2 => .82f, _ => 1f };
        foreach (var team in new[] { 1, 2 })
        {
            if (map.Duel) continue;
            _towers[team] = new Unit[lanes][];
            for (var l = 0; l < lanes; l++)
            {
                _towers[team][l] = new Unit[2];
                for (var i = 0; i < 2; i++)
                {
                    var hp = (i == 0 ? 2600 : 3000) * towerScale;
                    _towers[team][l][i] = Add(new Unit
                    {
                        Kind = UnitKind.Tower, Sub = "tower", Team = team, Pos = map.Towers[team][l][i], Radius = 46, Lane = l,
                        Hp = hp, MaxHp = hp, AttackRange = 520, AttackDamage = 130, AttackCd = 1.1f,
                    });
                }
            }
            _cores[team] = Add(new Unit
            {
                Kind = UnitKind.Core, Sub = "core", Team = team, Pos = map.Core[team], Radius = 70,
                Hp = 4000, MaxHp = 4000, AttackRange = 450, AttackDamage = 90, AttackCd = 1.2f,
            });
        }

        var slot = new int[3];
        foreach (var p in players)
        {
            var def = Catalog.ById.GetValueOrDefault(p.Hero) ?? Catalog.Heroes[0];
            var h = new Hero
            {
                Kind = UnitKind.Hero, Sub = def.Id, Def = def, PlayerId = p.Id, Name = p.Name, Team = p.Team,
                Radius = 24, Armor = def.Armor, Speed = def.Speed, BaseHp = def.Hp,
                AttackRange = def.Basic.Range, AttackDamage = def.Basic.Power, AttackCd = def.Basic.Cooldown,
                MaxMana = def.Mana, Mana = def.Mana, ManaRegen = def.ManaRegen, GoldBank = map.Duel ? DuelStartGold : 150,
            };
            h.MaxHp = h.Hp = def.Hp;
            h.Lane = map.Duel ? 0 : slot[p.Team] % map.Lanes.Count;
            h.Pos = SpawnPoint(p.Team, slot[p.Team]++);
            h.Facing = new Vec(p.Team == 1 ? 1 : -1, 0);
            Add(h);
            Heroes.Add(h);
            if (def.Id == "wren")
            {
                var fenn = Add(new Unit { Kind = UnitKind.Pet, Sub = "fenn", Team = p.Team, Pos = h.Pos + new Vec(-30, 20), Radius = 18, Speed = 380, AttackRange = 60, AttackCd = 1f, OwnerId = h.Id, Hp = 1, MaxHp = 1 });
                h.FennId = fenn.Id;
            }
        }
        FlushSpawns();
        if (map.Duel)
        {
            // Duellists start part-way up the levels, with the ultimate ready.
            foreach (var h in Heroes)
            {
                // Duellists are much hardier than on the battlefield, so a round is a fight, not one combo.
                h.MaxHp *= DuelHealth; h.Hp = h.MaxHp;
                GiveXp(h, Enumerable.Range(1, DuelStartLevel - 1).Sum(Catalog.XpToNext), quiet: true);
            }
            StartRound();
        }
    }

    public Vec SpawnPoint(int team, int i) => Map.Spawn[team] + new Vec((team == 1 ? 1 : -1) * (i % 2) * 40, (i - 1) * 55);

    private T Add<T>(T u) where T : Unit
    {
        u.Id = _nextId++;
        _spawnQueue.Add(u);
        _byId[u.Id] = u;
        return u;
    }
    private void FlushSpawns() { Units.AddRange(_spawnQueue); _spawnQueue.Clear(); }

    public Unit? Get(int id) => id != 0 && _byId.TryGetValue(id, out var u) ? u : null;
    public Hero? HeroOf(string playerId) => Heroes.FirstOrDefault(h => h.PlayerId == playerId);
    public Hero? CreditOf(Unit? u) => u switch { Hero h => h, { Kind: UnitKind.Pet } p => Get(p.OwnerId) as Hero, _ => null };

    private void Fx(FxDto f) => _fx.Add(f);
    public List<FxDto> TakeFx() { var copy = _fx.ToList(); _fx.Clear(); return copy; }
    private static int R(float v) => (int)MathF.Round(v);

    // ───────────────────────────── the tick

    public void Tick()
    {
        if (Winner != 0) return;
        Time += Dt;

        if (Duel) UpdateRound();
        else
        {
            UpdateSuddenDeath();
            SpawnWaves();
            SpawnNeutrals();
            UpdatePlants();
        }

        foreach (var h in Heroes) UpdateHero(h);
        foreach (var u in Units)
        {
            if (u.Dead) continue;
            switch (u.Kind)
            {
                case UnitKind.Minion: MinionAi(u); break;
                case UnitKind.Monster: MonsterAi(u); break;
                case UnitKind.Tower: case UnitKind.Core: StructureAi(u); break;
                case UnitKind.Pet: PetAi(u); break;
            }
        }
        MoveUnits();
        UpdateProjectiles();
        UpdateZones();
        RunDelayed();
        UpdateTimers();

        // Dead minions, monsters and summons go; heroes, structures and Fenn stay (heroes respawn, ruins remain).
        Units.RemoveAll(u =>
        {
            if (!u.Dead || u.Kind is UnitKind.Hero or UnitKind.Tower or UnitKind.Core || u.Sub == "fenn") return false;
            _byId.Remove(u.Id);
            return true;
        });
        FlushSpawns();
    }

    private void UpdateSuddenDeath()
    {
        var stage = Time >= SuddenDeath3 ? 3 : Time >= SuddenDeath2 ? 2 : Time >= SuddenDeath1 ? 1 : 0;
        if (stage == _suddenStage) return;
        _suddenStage = stage;
        Fx(new FxDto { E = "notice", K = "sudden" + stage, Tm = FavouredTeam });
    }
    /// <summary>The team ahead: more enemy structures destroyed, then more kills, then whoever took the Warden last.</summary>
    public int FavouredTeam
    {
        get
        {
            int Down(int team) => _towers[team].Sum(lane => lane.Count(t => t.Dead));
            var lead = (Down(2) - Down(1)) * 100 + (Score[1] - Score[2]);
            return lead > 0 ? 1 : lead < 0 ? 2 : _lastWarden;
        }
    }
    private int _lastWarden = 1;

    /// <summary>Structures take more damage as the match drags on, so it ends in time.</summary>
    private float StructureMult => _suddenStage switch { 3 => 4f, 2 => 3f, 1 => 1.75f, _ => 1f };

    private void SpawnWaves()
    {
        if (Time < _waveAt) return;
        _waveAt += WaveInterval;
        _wave++;
        // Smaller waves when there are more lanes, so the total stays about the same.
        var kinds = Map.Lanes.Count switch
        {
            1 => new List<string> { "melee", "melee", "melee", "ranged", "ranged" },
            2 => ["melee", "melee", "ranged", "ranged"],
            _ => ["melee", "melee", "ranged"],
        };
        if (_wave % 3 == 0 || _suddenStage > 0) kinds.Insert(0, "heavy");
        if (_suddenStage >= 2) kinds.Insert(0, "heavy");
        if (_wave == 1) Fx(new FxDto { E = "notice", K = "minions" });
        foreach (var team in new[] { 1, 2 })
            for (var lane = 0; lane < Map.Lanes.Count; lane++)
            {
                var path = Map.PathFor(team, lane);
                var l = lane;
                for (var i = 0; i < kinds.Count; i++)
                {
                    var kind = kinds[i];
                    _delayed.Add(new Delayed { At = Time + i * .55f, Run = () => SpawnMinion(team, kind, path, l) });
                }
            }
    }

    private void SpawnMinion(int team, string kind, List<Vec> path, int lane)
    {
        var scale = 1 + .04f * (_wave - 1) + (_suddenStage * .25f);
        // Sudden death: the team ahead gets the Star's favour, so an even lane can't stall forever.
        if (_suddenStage > 0 && team == FavouredTeam) scale *= _suddenStage >= 2 ? 1.9f : 1.5f;
        var u = new Unit { Kind = UnitKind.Minion, Sub = kind, Team = team, Pos = path[1], Path = path, PathIndex = 2, Lane = lane };
        switch (kind)
        {
            case "heavy": u.MaxHp = 700; u.AttackDamage = 34; u.AttackCd = 1.6f; u.AttackRange = 90; u.Speed = 170; u.Radius = 26; u.Gold = 45; u.Xp = 60; break;
            case "ranged": u.MaxHp = 220; u.AttackDamage = 20; u.AttackCd = 1.4f; u.AttackRange = 340; u.Speed = 190; u.Radius = 18; u.Gold = 18; u.Xp = 25; break;
            default: u.MaxHp = 300; u.AttackDamage = 14; u.AttackCd = 1f; u.AttackRange = 70; u.Speed = 190; u.Radius = 18; u.Gold = 22; u.Xp = 30; break;
        }
        u.MaxHp *= scale; u.Hp = u.MaxHp; u.AttackDamage *= scale;
        u.Pos += new Vec(0, _rng.Range(-30, 30));
        Add(u);
    }

    private void SpawnNeutrals()
    {
        for (var c = 0; c < Map.Camps.Count; c++)
        {
            if (_campAt[c] <= 0 || Time < _campAt[c]) continue;
            _campAt[c] = 0;
            var camp = Map.Camps[c];
            var minute = Time / 60;
            for (var i = 0; i < camp.Kinds.Length; i++)
            {
                var kind = camp.Kinds[i];
                var big = kind == "boar";
                var pos = camp.Pos + new Vec(i == 0 ? 0 : (i % 2 == 0 ? -1 : 1) * 70, i == 0 ? 0 : 40);
                var u = new Unit
                {
                    Kind = UnitKind.Monster, Sub = kind, Team = 0, Pos = pos, Home = pos, CampIndex = c, Radius = big ? 30 : 22,
                    MaxHp = (big ? 650 : 380) + minute * (big ? 70 : 40), AttackDamage = big ? 30 : 24, AttackCd = big ? 1.2f : 1f,
                    AttackRange = 75, Speed = big ? 240 : 280, Gold = big ? 50 : 32, Xp = big ? 60 : 38,
                    Facing = new Vec(c == 0 ? 1 : -1, 0),
                };
                u.Hp = u.MaxHp;
                Add(u);
            }
        }
        if (_warden == null && Time >= _objectiveAt && Map.Objective is { } objective)
        {
            var minute = Time / 60;
            _warden = Add(new Unit
            {
                Kind = UnitKind.Monster, Sub = "warden", Team = 0, Pos = objective, Home = objective, Radius = 52,
                MaxHp = 2600 + minute * 200, Hp = 2600 + minute * 200, AttackDamage = 70, AttackCd = 1.6f, AttackRange = 280, Speed = 160, Xp = 120,
                Facing = new Vec(0, 1),
            });
            Fx(new FxDto { E = "notice", K = "warden" });
        }
    }

    private void UpdatePlants()
    {
        for (var i = 0; i < Map.Plants.Count; i++)
        {
            if (_plantAt[i] > Time) continue;
            var p = Map.Plants[i];
            var h = Heroes.FirstOrDefault(x => x.Alive && Vec.Dist(x.Pos, p) < 56 && (x.Hp < x.MaxHp || x.Mana < x.MaxMana));
            if (h == null) continue;
            Heal(h, h, h.MaxHp * .18f);
            h.Mana = MathF.Min(h.MaxMana, h.Mana + h.MaxMana * .12f);
            _plantAt[i] = Time + PlantRespawn;
            Fx(new FxDto { E = "plant", U = h.Id, X = R(p.X), Y = R(p.Y) });
        }
    }

    // ───────────────────────────── heroes

    private void UpdateHero(Hero h)
    {
        if (h.Dead)
        {
            if (Duel) return;
            h.RespawnT -= Dt;
            if (h.RespawnT <= 0) Respawn(h);
            return;
        }
        if (!Duel) h.GoldBank += 2.5f * Dt;
        h.Mana = MathF.Min(h.MaxMana, h.Mana + h.ManaRegen * Dt);
        if (Time - LastHurt(h) > 6) h.Hp = MathF.Min(h.MaxHp, h.Hp + h.MaxHp * .006f * Dt * 10);
        for (var i = 0; i < 5; i++) if (h.Cooldowns[i] > 0) h.Cooldowns[i] = MathF.Max(0, h.Cooldowns[i] - Dt);
        if (h.AttackTimer > 0) h.AttackTimer -= Dt;
        if (h.AggroT > 0) h.AggroT -= Dt;

        // The fountain: heals friends quickly and burns enemies who come too close.
        if (!Duel)
            foreach (var team in new[] { 1, 2 })
            {
                if (Vec.Dist(h.Pos, Map.Spawn[team]) > MapDef.FountainRadius) continue;
                if (team == h.Team) { h.Hp = MathF.Min(h.MaxHp, h.Hp + h.MaxHp * .12f * Dt); h.Mana = MathF.Min(h.MaxMana, h.Mana + h.MaxMana * .15f * Dt); }
                else Damage(_cores[team], h, 400 * Dt, true, quiet: true);
            }
        // A duel round that hasn't started (or has ended) holds everyone where they are.
        if (Duel && _roundPhase != RoundPhase.Fight) { h.MoveDir = Vec.Zero; return; }
        if (h.ShellT > 0) Heal(h, h, h.Power(3) / h.Def.Abilities[3].Duration * Dt, quiet: true);

        // Casting: the hero stands still until the windup ends.
        if (h.CastT > 0)
        {
            if (h.Stunned) { h.CastT = 0; h.OnCast = null; }
            else
            {
                h.CastT -= Dt;
                if (h.CastT <= 0) { var a = h.OnCast; h.OnCast = null; a?.Invoke(); }
                return;
            }
        }

        var moving = h.MoveDir.LenSq > .01f;
        h.IdleT = moving ? 0 : h.IdleT + Dt;
        if (moving && h.CanMove)
        {
            var dir = h.MoveDir.Len > 1 ? h.MoveDir.Norm() : h.MoveDir;
            h.Pos += dir * (h.MoveSpeed * Dt);
            h.Facing = dir.Norm();
        }

        if ((h.AttackHeld || !moving) && h.CanAct && h.AttackTimer <= 0)
        {
            var t = PickAttackTarget(h, h.AttackRange, h.AttackHeld || h.IdleT > .15f);
            if (t != null) BasicAttack(h, t);
        }
    }

    private static float LastHurt(Unit u) => u.Attackers.Count == 0 ? -99 : u.Attackers.Values.Max();

    private void Respawn(Hero h)
    {
        h.Dead = false;
        h.Hp = h.MaxHp; h.Mana = h.MaxMana;
        h.Pos = SpawnPoint(h.Team, Heroes.Where(x => x.Team == h.Team).ToList().IndexOf(h));
        h.StunT = h.RootT = h.SlowT = h.MarkT = h.DashT = h.PushT = 0;
        h.Attackers.Clear();
        h.InvulnT = 1.5f;
        if (Get(h.FennId) is { } fenn) { fenn.Dead = false; fenn.Pos = h.Pos + new Vec(-30, 20); }
        Fx(new FxDto { E = "respawn", U = h.Id, X = R(h.Pos.X), Y = R(h.Pos.Y) });
    }

    /// <summary>
    /// Who a hero's basic attack goes for: the target it already has, else the nearest enemy hero in range, else a
    /// minion, monster or structure. Structures and monsters only when the player means it (holding attack or idle).
    /// </summary>
    public Unit? PickAttackTarget(Unit h, float range, bool anything)
    {
        bool InRange(Unit u) => Vec.Dist(h.Pos, u.Pos) <= range + u.Radius && Sees(h, u);
        var cur = Get(h.TargetId);
        if (cur != null && Targetable(h, cur) && InRange(cur) && (anything || cur.Kind == UnitKind.Hero)) return cur;
        Unit? best = null; var bestScore = float.MaxValue;
        foreach (var u in Units)
        {
            if (!Targetable(h, u) || !InRange(u)) continue;
            var d = Vec.Dist(h.Pos, u.Pos);
            float score = u.Kind switch
            {
                UnitKind.Hero => d - 2000,
                UnitKind.Minion => d,
                UnitKind.Monster => anything ? d + 300 : float.MaxValue,
                _ => anything ? d + 1000 : float.MaxValue,
            };
            if (score < bestScore) { bestScore = score; best = u; }
        }
        return best;
    }

    /// <summary>Whether <paramref name="a"/> can attack <paramref name="b"/> at all.</summary>
    public bool Targetable(Unit a, Unit b)
    {
        if (b.Dead || b.Team == a.Team || b.Kind == UnitKind.Pet) return false;
        if (b.StealthT > 0) return false;
        if (b.IsStructure && Protected(b)) return false;
        return true;
    }

    /// <summary>Tower 2 can't be hurt while Tower 1 stands; the Core can't be hurt while Tower 2 stands.</summary>
    public bool Protected(Unit s)
    {
        // The last stage of sudden death: every structure is open to attack.
        if (_suddenStage >= 3) return false;
        return ProtectedByTowers(s);
    }
    /// <summary>A lane's inner tower can't be hurt while its outer tower stands. The Core can't be hurt while every
    /// lane's inner tower stands: breaking through one lane is enough.</summary>
    private bool ProtectedByTowers(Unit s)
    {
        if (s.Kind == UnitKind.Tower) { var lane = _towers[s.Team][s.Lane]; return s == lane[1] && lane[0].Alive; }
        if (s.Kind == UnitKind.Core) return _towers[s.Team].All(lane => lane[1].Alive);
        return false;
    }

    // ───────────────────────────── damage, healing, death

    public void Damage(Unit? src, Unit t, float amount, bool ability, bool crit = false, bool quiet = false, bool fromStar = false)
    {
        if (t.Dead || amount <= 0) return;
        if (t.InvulnT > 0 || t.ShellT > 0 || t.Kind == UnitKind.Pet) { if (!quiet) Fx(new FxDto { E = "block", U = t.Id }); return; }
        if (t.IsStructure && Protected(t)) return;
        if (t.GuardT > 0 && src != null) { Fx(new FxDto { E = "block", U = t.Id }); return; }
        if (t.StarCount > 0 && src != null && !fromStar && !src.IsStructure && t is Hero mira)
        {
            t.StarCount--;
            Fx(new FxDto { E = "star", U = t.Id, U2 = src.Id });
            Damage(t, src, mira.Power(3), true, fromStar: true);
            return;
        }
        if (src != null && src.BlessT > 0) amount *= t.IsStructure ? 1.3f : 1.2f;
        if (t.IsStructure) amount *= StructureMult * (src?.Sub == "heavy" ? 2 : 1);
        if (t.SpinT > 0) amount *= .5f;
        amount *= t.Armor;
        if (t.ShieldT > 0 && t.Shield > 0)
        {
            var absorbed = MathF.Min(t.Shield, amount);
            t.Shield -= absorbed; amount -= absorbed;
            if (amount <= 0) { if (!quiet) Fx(new FxDto { E = "dmg", U = t.Id, V = 0, K = "shield" }); return; }
        }
        t.Hp -= amount;
        var credit = CreditOf(src);
        if (credit != null)
        {
            t.Attackers[credit.Id] = Time;
            credit.DamageDealt += amount;
            if (t.Kind == UnitKind.Hero) credit.HeroDamage += amount;
            if (t.Kind == UnitKind.Hero) credit.AggroT = 2;
        }
        else if (src != null) t.Attackers[-1] = Time;
        if (t.StealthT > 0) EndStealth((Hero)t);
        if (t.Kind == UnitKind.Monster && src != null && src.Kind != UnitKind.Monster) { t.TargetId = (credit ?? src).Id; t.AggroT = 6; }
        if (!quiet) Fx(new FxDto { E = "dmg", U = t.Id, V = R(amount), K = crit ? "crit" : ability ? "spell" : null });
        if (t.Hp <= 0) Kill(t, src);
    }

    public void Heal(Unit? src, Unit t, float amount, bool quiet = false)
    {
        if (t.Dead || amount <= 0) return;
        var real = MathF.Min(amount, t.MaxHp - t.Hp);
        t.Hp += real;
        if (CreditOf(src) is { } h && h != t) h.Healing += real;
        if (!quiet && real >= 1) Fx(new FxDto { E = "heal", U = t.Id, V = R(real) });
    }

    private void Kill(Unit t, Unit? killer)
    {
        t.Dead = true; t.Hp = 0;
        Fx(new FxDto { E = "die", U = t.Id, K = t.Sub, X = R(t.Pos.X), Y = R(t.Pos.Y), Tm = t.Team });
        var credit = CreditOf(killer);
        switch (t.Kind)
        {
            case UnitKind.Minion:
                if (credit != null) GiveGold(credit, t.Gold, t.Pos);
                ShareXp(t.Pos, 3 - t.Team, t.Xp);
                break;
            case UnitKind.Monster:
                if (t.Sub == "warden")
                {
                    _warden = null; _objectiveAt = Time + ObjectiveRespawn;
                    var team = credit?.Team ?? (killer?.Team ?? 0);
                    if (team != 0) _lastWarden = team;
                    if (team != 0)
                    {
                        foreach (var h in Heroes.Where(h => h.Team == team)) { GiveGold(h, 100, h.Pos); if (h.Alive) h.BlessT = 60; }
                        foreach (var u in Units.Where(u => u.Team == team && u.Kind == UnitKind.Minion)) u.BlessT = 60;
                        Fx(new FxDto { E = "notice", K = "blessed", Tm = team });
                    }
                    ShareXp(t.Pos, team, t.Xp, 1200);
                }
                else
                {
                    if (credit != null) { GiveGold(credit, t.Gold, t.Pos); ShareXp(t.Pos, credit.Team, t.Xp); }
                    if (t.CampIndex >= 0 && !Units.Any(u => u.Alive && u.CampIndex == t.CampIndex && u != t)) _campAt[t.CampIndex] = Time + CampRespawn;
                }
                break;
            case UnitKind.Hero:
                KillHero((Hero)t, killer, credit);
                break;
            case UnitKind.Tower:
                foreach (var h in Heroes.Where(h => h.Team != t.Team)) { GiveGold(h, 100, h.Pos); GiveXp(h, 60); }
                Fx(new FxDto { E = "struct", U = t.Id, Tm = t.Team, K = t == _towers[t.Team][t.Lane][0] ? "tower1" : "tower2", V = t.Lane });
                break;
            case UnitKind.Core:
                Winner = 3 - t.Team;
                Fx(new FxDto { E = "struct", U = t.Id, Tm = t.Team, K = "core" });
                break;
        }
    }

    private void KillHero(Hero v, Unit? killer, Hero? credit)
    {
        v.Deaths++;
        v.Streak = 0;
        v.RespawnT = MathF.Min(22, 4 + 1.6f * v.Level + Time / 60);
        v.StealthT = v.ShellT = v.GuardT = v.SpinT = v.ShieldT = v.BlessT = v.CastT = v.StarCount = 0;
        v.Empowered = 0; v.OnCast = null;
        if (Get(v.FennId) is { } fenn) fenn.Dead = true;
        var killerTeam = 3 - v.Team;
        Score[killerTeam]++;
        var assisters = v.Attackers.Where(kv => kv.Key > 0 && Time - kv.Value < 10 && kv.Key != credit?.Id)
            .Select(kv => Get(kv.Key) as Hero).Where(h => h != null && h.Team == killerTeam).Cast<Hero>().ToList();
        if (credit != null)
        {
            credit.Kills++; credit.Streak++;
            GiveGold(credit, 150 + 10 * v.Level + Math.Min(3, credit.Streak - 1) * 25, v.Pos);
        }
        foreach (var a in assisters) { a.Assists++; GiveGold(a, 60, v.Pos); }
        ShareXp(v.Pos, killerTeam, 80 + 25 * v.Level, 1100);
        Fx(new FxDto { E = "kill", U = credit?.Id ?? killer?.Id ?? 0, U2 = v.Id, Tm = killerTeam, K = killer?.Sub });
    }

    private void GiveGold(Hero h, float amount, Vec at)
    {
        h.GoldBank += amount;
        Fx(new FxDto { E = "gold", U = h.Id, V = R(amount), X = R(at.X), Y = R(at.Y) });
    }

    private void ShareXp(Vec at, int team, float amount, float radius = 950)
    {
        var near = Heroes.Where(h => h.Team == team && h.Alive && Vec.Dist(h.Pos, at) < radius).ToList();
        if (near.Count == 0) return;
        var each = amount * (near.Count == 1 ? 1 : 1.4f / near.Count);
        foreach (var h in near) GiveXp(h, each);
    }

    public void GiveXp(Hero h, float amount, bool quiet = false)
    {
        if (h.Level >= Catalog.MaxLevel) return;
        h.Exp += amount;
        while (h.Level < Catalog.MaxLevel && h.Exp >= Catalog.XpToNext(h.Level))
        {
            h.Exp -= Catalog.XpToNext(h.Level);
            h.Level++;
            var gain = h.Def.HpPerLevel * (h.Picks[0].Contains("hp12") ? 1.12f : 1) * (Duel ? DuelHealth : 1);
            h.MaxHp += gain; h.Hp += gain;
            h.AttackDamage += h.Def.AdPerLevel;
            h.MaxMana += 12; h.Mana += 12;
            if (!quiet) Fx(new FxDto { E = "lvl", U = h.Id, V = h.Level });
        }
        if (h.Level >= Catalog.MaxLevel) h.Exp = 0;
    }

    // ───────────────────────────── upgrades

    public string? BuyUpgrade(Hero h, int slot, int choice)
    {
        if (slot is < 0 or > 4 || choice is < 0 or > 1) return "bad";
        var tier = h.Picks[slot].Count;
        var tiers = Upgrades.TiersFor(slot);
        if (tier >= tiers.Length) return "max";
        if (slot == 4 && h.Level < Catalog.UltLevel) return "locked";
        var cost = Upgrades.Cost(slot, tier);
        if (h.GoldBank < cost) return "gold";
        h.GoldBank -= cost;
        var pick = tiers[tier][choice];
        h.Picks[slot].Add(pick.Id);
        switch (pick.Id)
        {
            case "hp12": h.MaxHp *= 1.12f; h.Hp *= 1.12f; break;
            case "move8": h.Speed *= 1.08f; break;
        }
        Fx(new FxDto { E = "upgrade", U = h.Id, V = slot, K = pick.Id });
        return null;
    }

    // ───────────────────────────── movement and collision

    private void MoveUnits()
    {
        foreach (var u in Units)
        {
            if (u.Dead) continue;
            if (u.DashT > 0)
            {
                var step = MathF.Min(u.DashT, Dt);
                u.Pos += u.DashVel * step;
                u.DashT -= Dt;
                if (u.DashT <= 0) { u.DashT = 0; var end = u.OnDashEnd; u.OnDashEnd = null; end?.Invoke(); }
            }
            if (u.PushT > 0) { u.Pos += u.PushVel * Dt; u.PushT -= Dt; }
            if (u.IsStructure) continue;
            if (u.Kind != UnitKind.Pet) Collide(u);
            u.Pos = new Vec(Math.Clamp(u.Pos.X, 50, Map.W - 50), Math.Clamp(u.Pos.Y, 50, Map.H - 50));
        }
        // Minions and monsters keep a little apart, so a wave spreads out instead of stacking.
        for (var i = 0; i < Units.Count; i++)
        {
            var a = Units[i];
            if (a.Dead || a.Kind is not (UnitKind.Minion or UnitKind.Monster)) continue;
            for (var j = i + 1; j < Units.Count; j++)
            {
                var b = Units[j];
                if (b.Dead || b.Kind is not (UnitKind.Minion or UnitKind.Monster)) continue;
                var d = a.Pos - b.Pos; var min = a.Radius + b.Radius;
                var dsq = d.LenSq;
                if (dsq >= min * min || dsq < .01f) continue;
                var dist = MathF.Sqrt(dsq);
                var push = d * ((min - dist) / dist * .5f);
                a.Pos += push; b.Pos -= push;
            }
        }
    }

    /// <summary>Pushes a unit out of trees, rocks and standing structures.</summary>
    public void Collide(Unit u)
    {
        foreach (var o in _grid.Near(u.Pos))
        {
            var c = new Vec(o.X, o.Y);
            var min = u.Radius + o.R * .8f;
            var d = u.Pos - c;
            var dsq = d.LenSq;
            if (dsq >= min * min) continue;
            var dist = MathF.Sqrt(dsq);
            u.Pos = dist < .01f ? c + new Vec(min, 0) : c + d * (min / dist);
        }
        foreach (var s in Units)
        {
            if (!s.IsStructure || s.Dead) continue;
            var min = u.Radius + s.Radius;
            var d = u.Pos - s.Pos;
            if (d.LenSq >= min * min) continue;
            var dist = d.Len;
            u.Pos = dist < .01f ? s.Pos + new Vec(min, 0) : s.Pos + d * (min / dist);
        }
    }

    public bool Blocked(Vec p, float r) => _grid.Near(p).Any(o => Vec.Dist(p, new Vec(o.X, o.Y)) < r + o.R * .8f);

    /// <summary>How much of an obstacle stops sight and shots: its trunk or stone, not its outer leaves. Pools block
    /// walking but not sight.</summary>
    private static float SightRadius(Obstacle o) => o.K == "pool" ? 0 : o.R * .7f;

    /// <summary>Whether nothing stands between two points: no stone, pillar or tree trunk crosses the straight line.</summary>
    public bool LineOfSight(Vec a, Vec b)
    {
        var d = b - a;
        var lenSq = d.LenSq;
        if (lenSq < 1) return true;
        // Sample the line every 120 units; the 3×3 cells around each sample cover everything that could cross it.
        var steps = (int)(MathF.Sqrt(lenSq) / 120) + 1;
        for (var i = 0; i <= steps; i++)
            foreach (var o in _grid.Near(a + d * (i / (float)steps)))
            {
                var r = SightRadius(o);
                if (r <= 0) continue;
                var c = new Vec(o.X, o.Y);
                var k = Math.Clamp(Vec.Dot(c - a, d) / lenSq, 0, 1);
                if (Vec.DistSq(a + d * k, c) < r * r) return false;
            }
        return true;
    }

    /// <summary>Whether <paramref name="a"/> can see <paramref name="b"/> to aim at it. Structures are tall enough to
    /// be seen over anything.</summary>
    public bool Sees(Unit a, Unit b) => b.IsStructure || LineOfSight(a.Pos, b.Pos);

    /// <summary>The obstacle a point is inside (for shots), if any.</summary>
    private Obstacle? ObstacleAt(Vec p)
    {
        foreach (var o in _grid.Near(p))
        {
            var r = SightRadius(o);
            if (r > 0 && Vec.DistSq(p, new Vec(o.X, o.Y)) < r * r) return o;
        }
        return null;
    }

    /// <summary>A direction from <paramref name="from"/> toward <paramref name="to"/> that walks round the first
    /// obstacle in the way, for bots that lost sight of their target.</summary>
    public Vec SteerAround(Vec from, Vec to)
    {
        var d = to - from;
        var lenSq = d.LenSq;
        if (lenSq < 1) return Vec.Zero;
        var dir = d.Norm();
        Obstacle? first = null; var firstK = 2f;
        var steps = (int)(MathF.Sqrt(lenSq) / 120) + 1;
        for (var i = 0; i <= steps; i++)
            foreach (var o in _grid.Near(from + d * (i / (float)steps)))
            {
                var c = new Vec(o.X, o.Y);
                var k = Math.Clamp(Vec.Dot(c - from, d) / lenSq, 0, 1);
                var r = o.R * .8f + 30;
                if (Vec.DistSq(from + d * k, c) < r * r && k < firstK) { firstK = k; first = o; }
            }
        if (first is not { } ob) return dir;
        var toC = new Vec(ob.X, ob.Y) - from;
        // Go past on whichever side of the obstacle the line already leans to.
        var cross = dir.X * toC.Y - dir.Y * toC.X;
        var side = cross > 0 ? new Vec(dir.Y, -dir.X) : new Vec(-dir.Y, dir.X);
        return (dir * .35f + side).Norm();
    }

    /// <summary>Knocks a unit away from a point.</summary>
    public void Push(Unit u, Vec from, float distance, float time = .2f)
    {
        if (u.IsStructure || u.Dead) return;
        var dir = (u.Pos - from).Norm();
        if (dir == Vec.Zero) dir = new Vec(1, 0);
        u.PushVel = dir * (distance / time); u.PushT = time;
    }

    // ───────────────────────────── projectiles, zones, delayed effects

    public Projectile Shoot(Unit src, string kind, Vec dir, float speed, float range, Action<Unit>? onHit, int homing = 0, float radius = 14, int pierce = 0)
    {
        var p = new Projectile
        {
            Id = _nextId++, Kind = kind, OwnerId = src.Id, Team = src.Team, Pos = src.Pos + new Vec(0, -10), Vel = dir.Norm() * speed,
            Speed = speed, RangeLeft = range, HomingId = homing, OnHit = onHit, Radius = radius, Pierce = pierce, Solid = src.Kind == UnitKind.Hero,
        };
        Projectiles.Add(p);
        return p;
    }

    private void UpdateProjectiles()
    {
        foreach (var p in Projectiles)
        {
            if (p.Done) continue;
            if (p.HomingId != 0)
            {
                var target = Get(p.HomingId);
                if (target == null || target.Dead || (target.StealthT > 0 && target.Team != p.Team)) { p.HomingId = 0; }
                else p.Vel = (target.Pos + new Vec(0, -10) - p.Pos).Norm() * p.Speed;
            }
            var step = p.Vel * Dt;
            p.Pos += step;
            p.RangeLeft -= step.Len;
            // Stones, pillars and trunks stop every shot (a Sunflare bursts against them).
            if (p.Solid && ObstacleAt(p.Pos) is not null)
            {
                p.Done = true;
                Fx(new FxDto { E = "burst", K = "thud", X = R(p.Pos.X), Y = R(p.Pos.Y), R = 30 });
                p.OnExpire?.Invoke(p.Pos);
                continue;
            }
            foreach (var u in Units)
            {
                if (u.Dead || u.Kind == UnitKind.Pet || p.Hit.Contains(u.Id)) continue;
                if (p.HomingId != 0 && u.Id != p.HomingId) continue;
                var hitR = p.Radius + u.Radius;
                if (Vec.DistSq(u.Pos + new Vec(0, -10), p.Pos) > hitR * hitR) continue;
                if (u.Team == p.Team)
                {
                    if (p.OnAlly != null && u.Kind == UnitKind.Hero && u.Id != p.OwnerId) { p.Hit.Add(u.Id); p.OnAlly(u); }
                    continue;
                }
                if (u.StealthT > 0) continue;
                // Skillshots fly past structures; only attacks aimed at one (and arrows) hit it.
                if (u.IsStructure && (Protected(u) || (p.HomingId == 0 && p.Kind != "arrow"))) continue;
                if (u.GuardT > 0)
                {
                    // Bulwark bounces it straight back, now the knight's.
                    p.Team = u.Team; p.OwnerId = u.Id; p.Vel = p.Vel * -1; p.RangeLeft = MathF.Max(p.RangeLeft, 300);
                    p.HomingId = 0; p.Hit.Clear(); p.Hit.Add(u.Id); p.CastHit = null;
                    Fx(new FxDto { E = "reflect", U = u.Id });
                    break;
                }
                if (p.CastHit != null && p.CastHit.Contains(u.Id)) { p.Hit.Add(u.Id); continue; }
                p.Hit.Add(u.Id); p.CastHit?.Add(u.Id);
                p.OnHit?.Invoke(u);
                if (p.Pierce-- <= 0) { p.Done = true; break; }
            }
            if (!p.Done && (p.RangeLeft <= 0 || p.Pos.X < 0 || p.Pos.Y < 0 || p.Pos.X > Map.W || p.Pos.Y > Map.H))
            {
                p.Done = true;
                p.OnExpire?.Invoke(p.Pos);
            }
        }
        Projectiles.RemoveAll(p => p.Done);
    }

    public Zone AddZone(Unit owner, string kind, Vec pos, float radius, float duration, float interval, Action<Zone>? onTick, Action<Zone>? onFrame = null)
    {
        var z = new Zone { Id = _nextId++, Kind = kind, OwnerId = owner.Id, Team = owner.Team, Pos = pos, Radius = radius, TimeLeft = duration, Interval = interval, OnTick = onTick, OnFrame = onFrame };
        Zones.Add(z);
        return z;
    }

    private void UpdateZones()
    {
        foreach (var z in Zones)
        {
            z.TimeLeft -= Dt;
            z.OnFrame?.Invoke(z);
            z.Timer -= Dt;
            if (z.Timer <= 0 && z.TimeLeft > -Dt / 2) { z.Timer += z.Interval; z.OnTick?.Invoke(z); }
        }
        Zones.RemoveAll(z => z.TimeLeft <= 0);
    }

    public void Later(float delay, Action run) => _delayed.Add(new Delayed { At = Time + delay, Run = run });

    private void RunDelayed()
    {
        var due = _delayed.Where(d => d.At <= Time).ToList();
        if (due.Count == 0) return;
        _delayed.RemoveAll(d => d.At <= Time);
        foreach (var d in due) d.Run();
    }

    private void UpdateTimers()
    {
        foreach (var u in Units)
        {
            if (u.StunT > 0) u.StunT -= Dt;
            if (u.RootT > 0) u.RootT -= Dt;
            if (u.SlowT > 0) u.SlowT -= Dt; else u.SlowAmt = 0;
            if (u.InvulnT > 0) u.InvulnT -= Dt;
            if (u.GuardT > 0) u.GuardT -= Dt;
            if (u.SpinT > 0) u.SpinT -= Dt;
            if (u.ShieldT > 0) u.ShieldT -= Dt; else u.Shield = 0;
            if (u.BlessT > 0) u.BlessT -= Dt;
            if (u.FrenzyT > 0) u.FrenzyT -= Dt;
            if (u.MarkT > 0) u.MarkT -= Dt;
            if (u.ShellT > 0) { u.ShellT -= Dt; if (u.ShellT <= 0 && u is Hero lyra) EndShell(lyra); }
            if (u.StealthT > 0) { u.StealthT -= Dt; if (u.StealthT <= 0 && u is Hero riven) EndStealth(riven); }
            if (u.StarCount > 0) UpdateStars(u);
            if (u.LifeT > 0) { u.LifeT -= Dt; if (u.LifeT <= 0) { u.Dead = true; Fx(new FxDto { E = "die", U = u.Id, K = u.Sub, X = R(u.Pos.X), Y = R(u.Pos.Y), Tm = u.Team }); } }
        }
    }

    // ───────────────────────────── snapshots

    public SnapshotDto Snapshot(int team, List<FxDto> fx)
    {
        var units = new List<UnitDto>(Units.Count);
        foreach (var u in Units)
        {
            if (u.Kind == UnitKind.Pet && u.Dead) continue;
            if (u.Team != team && u.Team != 0 && u.StealthT > 0) continue;
            var st = u.Status(Time);
            if (u.IsStructure && Protected(u)) st |= St.Invulnerable;
            units.Add(new UnitDto(u.Id, u.Sub, u.Team, R(u.Pos.X), R(u.Pos.Y), (int)MathF.Ceiling(MathF.Max(0, u.Hp)), R(u.MaxHp),
                R(u.Facing.Angle * 180 / MathF.PI), (int)st, u is Hero h ? h.Level : 0, R(u.Shield), u.StarCount));
        }
        var plants = 0;
        for (var i = 0; i < _plantAt.Length; i++) if (_plantAt[i] <= Time) plants |= 1 << i;
        return new SnapshotDto
        {
            T = MathF.Round(Time, 3),
            U = units,
            P = Projectiles.Select(p => new ProjDto(p.Id, p.Kind, R(p.Pos.X), R(p.Pos.Y), R(p.Vel.X), R(p.Vel.Y), p.Team)).ToList(),
            Z = Zones.Select(z => new ZoneDto(z.Id, z.Kind, R(z.Pos.X), R(z.Pos.Y), R(z.Radius), z.Team, R(z.TimeLeft * 1000))).ToList(),
            Fx = fx.Where(f => VisibleTo(f, team)).ToList(),
            Sc = [Score[1], Score[2]],
            Ps = Heroes.Select(h => new PlayerStatDto(h.PlayerId, h.Id, h.Kills, h.Deaths, h.Assists, h.Level, h.Dead ? (int)MathF.Ceiling(h.RespawnT) : 0, R(h.HeroDamage), R(h.Healing))).ToList(),
            Pa = plants,
            Ob = _warden != null ? 0 : (int)MathF.Ceiling(MathF.Max(1, _objectiveAt - Time)),
            Sd = _suddenStage,
            Fv = _suddenStage > 0 ? FavouredTeam : 0,
            Rd = _round, Rw = [_roundWins[1], _roundWins[2]], Rp = (int)_roundPhase, Rt = MathF.Round(_roundTimer, 1), Rr = R(_ringR),
        };
    }

    /// <summary>Hides what a stealthed enemy does, and other players' gold.</summary>
    private bool VisibleTo(FxDto f, int team)
    {
        if (f.U is int id && Get(id) is { } u && u.Team != team && u.StealthT > 0 && f.E is "atk" or "cast") return false;
        return true;
    }

    public MeDto Me(Hero h)
    {
        var cd = new float[5]; var cm = new float[5]; var mc = new int[5];
        for (var i = 0; i < 5; i++)
        {
            var a = h.Def.Abilities[i]; var m = h.ModsFor(i);
            cd[i] = MathF.Round(h.Cooldowns[i], 1);
            cm[i] = MathF.Round(a.Cooldown * m.Cd, 2);
            mc[i] = R(a.Cost * m.Cost);
        }
        var moving = h.CanMove && h.CastT <= 0 && h.MoveDir.LenSq > .01f;
        var v = moving ? (h.MoveDir.Len > 1 ? h.MoveDir.Norm() : h.MoveDir) * h.MoveSpeed : Vec.Zero;
        return new MeDto
        {
            U = h.Id, G = (int)h.GoldBank, Lv = h.Level, Xp = R(h.Exp), Xn = Catalog.XpToNext(h.Level), Mp = R(h.Mana), Mm = R(h.MaxMana),
            Cd = cd, Cm = cm, Mc = mc, Up = h.Picks.Select(p => p.ToList()).ToArray(), Rs = MathF.Round(h.RespawnT, 1), Sp = R(h.MoveSpeed),
            Vx = R(v.X), Vy = R(v.Y), Ad = R(h.AttackDamage * h.ModsFor(0).Power),
        };
    }

    public MapDto MapDto() => new(Map.Id, Map.Name, Map.Theme, Map.Type, Map.W, Map.H, Map.LaneWidth,
        Map.Lanes.Select(l => l.Select(p => new[] { R(p.X), R(p.Y) }).ToList()).ToList(), Map.Obstacles,
        [[0, 0], [R(Map.Spawn[1].X), R(Map.Spawn[1].Y)], [R(Map.Spawn[2].X), R(Map.Spawn[2].Y)]],
        Map.Plants.Select(p => new[] { R(p.X), R(p.Y) }).ToList(),
        Map.Camps.Select(c => new[] { R(c.Pos.X), R(c.Pos.Y) }).ToList(),
        Map.Objective is { } o ? [R(o.X), R(o.Y)] : null, MapDef.FountainRadius, [R(Map.Center.X), R(Map.Center.Y)], Map.ArenaRadius);
}

/// <summary>Obstacles bucketed by their centre into a coarse grid, so collision only looks at what is nearby.
/// A cell is wider than any obstacle plus any unit, so the 3×3 cells around a point hold everything it can touch.</summary>
public sealed class ObstacleGrid
{
    private const float Cell = 160;
    private readonly Dictionary<(int, int), List<Obstacle>> _cells = [];
    public ObstacleGrid(MapDef map)
    {
        foreach (var o in map.Obstacles)
        {
            var key = ((int)(o.X / Cell), (int)(o.Y / Cell));
            if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = [];
            list.Add(o);
        }
    }
    public IEnumerable<Obstacle> Near(Vec p)
    {
        int cx = (int)(p.X / Cell), cy = (int)(p.Y / Cell);
        for (var x = cx - 1; x <= cx + 1; x++)
            for (var y = cy - 1; y <= cy + 1; y++)
                if (_cells.TryGetValue((x, y), out var list))
                    foreach (var o in list) yield return o;
    }
}
