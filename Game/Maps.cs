namespace Starfall.Grove.Moba.Api.Game;

/// <summary>Something that blocks movement: a tree, rock, crystal or pool. <c>K</c> names how the client draws it.</summary>
public sealed record Obstacle(float X, float Y, float R, string K, int S);

public sealed record CampDef(Vec Pos, string[] Kinds);

/// <summary>
/// One battlefield. Blue (team 1) starts on the left, Red (team 2) on the right. One lane runs between the two Cores;
/// walls of trees line it, with gaps into the side woods where the camps, healing plants and the central objective are.
/// Maps are mirrored left to right, so neither side has an advantage.
/// </summary>
public sealed class MapDef
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Theme { get; init; }
    public float W { get; init; } = 3200;
    public float H { get; init; } = 1600;
    public float LaneWidth { get; init; } = 300;
    public List<Vec> Lane { get; } = [];
    public List<Obstacle> Obstacles { get; } = [];
    public Vec[] Spawn { get; } = new Vec[3];
    public Vec[] Core { get; } = new Vec[3];
    /// <summary>[team][0] is the outer tower (Tower 1), [team][1] the inner one (Tower 2).</summary>
    public Vec[][] Towers { get; } = [[], new Vec[2], new Vec[2]];
    public List<Vec> Plants { get; } = [];
    public List<CampDef> Camps { get; } = [];
    public Vec Objective { get; set; }
    public const float FountainRadius = 240;

    /// <summary>The lane as Blue's minions walk it (Red's walk it backwards).</summary>
    public List<Vec> PathFor(int team) => team == 1 ? Lane : Enumerable.Reverse(Lane).ToList();
}

public static class Maps
{
    public static readonly (string Id, string Name, string Theme, string Blurb)[] List =
    [
        ("glade", "Starfall Glade", "meadow", "A sunny meadow lane. The Star Warden sleeps in the northern ruins."),
        ("frost", "Frostfang Pass", "summit", "A winding mountain pass. The Warden waits in the southern hollow."),
        ("ember", "Emberfall Hollow", "ember", "A scorched valley around a lava pool. Fights break out everywhere."),
    ];

    public static MapDef Build(string id)
    {
        return id switch
        {
            "frost" => Make("frost", "Frostfang Pass", "summit", amp: 120, waves: 2, objectiveTop: false, seed: 77,
                wall: ["pine", "pine", "rock"], scatter: ["pine", "rock", "crystal", "pine"]),
            "ember" => Make("ember", "Emberfall Hollow", "ember", amp: -140, waves: 1, objectiveTop: true, seed: 913,
                wall: ["deadtree", "rock", "rock"], scatter: ["rock", "deadtree", "crystal", "stump"]),
            _ => Make("glade", "Starfall Glade", "meadow", amp: 70, waves: 1, objectiveTop: true, seed: 5,
                wall: ["tree", "tree", "bush"], scatter: ["tree", "bush", "rock", "mushroom", "tree"]),
        };
    }

    private static MapDef Make(string id, string name, string theme, float amp, int waves, bool objectiveTop, int seed, string[] wall, string[] scatter)
    {
        var m = new MapDef { Id = id, Name = name, Theme = theme };
        var rng = new Rng(seed);
        float x0 = 300, x1 = m.W - 300, mid = m.W / 2;
        float LaneY(float x) { var u = (x - mid) / (x1 - x0); return m.H / 2 + amp * MathF.Cos(2 * MathF.PI * waves * u); }
        for (var x = x0; x <= x1 + .1f; x += 40) m.Lane.Add(new Vec(x, LaneY(x)));

        // Bases: the spawn (with its healing fountain) behind the Core, two towers up the lane.
        m.Spawn[1] = new Vec(120, LaneY(x0)); m.Spawn[2] = new Vec(m.W - 120, LaneY(x1));
        m.Core[1] = m.Lane[0]; m.Core[2] = m.Lane[^1];
        Vec At(float f) { var x = x0 + (x1 - x0) * f; return new Vec(x, LaneY(x)); }
        m.Towers[1][1] = At(.15f); m.Towers[1][0] = At(.33f);
        m.Towers[2][1] = At(.85f); m.Towers[2][0] = At(.67f);

        // The side woods: the objective in the middle of one side, both camps on the other, plants in each.
        float top = (60 + LaneY(mid) - m.LaneWidth / 2 - 90) / 2, bottom = (m.H - 60 + LaneY(mid) + m.LaneWidth / 2 + 90) / 2;
        float objY = objectiveTop ? top : bottom, campY = objectiveTop ? bottom : top;
        m.Objective = new Vec(mid, objY);
        var campKinds = new[] { "boar", "thornling" };
        m.Camps.Add(new CampDef(new Vec(mid - 620, campY), campKinds));
        m.Camps.Add(new CampDef(new Vec(mid + 620, campY), campKinds));
        m.Plants.Add(new Vec(mid - 820, objY)); m.Plants.Add(new Vec(mid + 820, objY));
        m.Plants.Add(new Vec(mid, campY));
        if (theme == "ember") m.Obstacles.Add(new Obstacle(mid, campY + (objectiveTop ? 90 : -90), 95, "pool", 1));

        var keepClear = new List<(Vec P, float R)>
        {
            (m.Spawn[1], 380), (m.Spawn[2], 380), (m.Core[1], 260), (m.Core[2], 260), (m.Objective, 210),
        };
        foreach (var c in m.Camps) keepClear.Add((c.Pos, 190));
        foreach (var p in m.Plants) keepClear.Add((p, 110));
        foreach (var o in m.Obstacles) keepClear.Add((new Vec(o.X, o.Y), o.R + 80));
        bool Clear(Vec p, float r) => keepClear.All(k => Vec.Dist(k.P, p) > k.R + r);

        // Walls of trees along both sides of the lane, with gaps into the woods (mirrored, so both sides match).
        var gaps = new[] { .2f, .4f };
        void Mirror(Obstacle o) { m.Obstacles.Add(o); m.Obstacles.Add(o with { X = m.W - o.X, S = o.S + 7 }); }
        for (var x = x0 + 120; x < mid - 20; x += 72)
        {
            var f = (x - x0) / (x1 - x0);
            if (f < .05f) continue;
            var y = LaneY(x);
            foreach (var side in new[] { -1, 1 })
            {
                var gapAt = side < 0 ? gaps : gaps.Select(g => g + .05f).ToArray();
                if (gapAt.Any(g => MathF.Abs(f - g) < .035f)) continue;
                var r = rng.Range(34, 46);
                var p = new Vec(x + rng.Range(-8, 8), y + side * (m.LaneWidth / 2 + r + rng.Range(4, 16)));
                if (!Clear(p, r)) continue;
                Mirror(new Obstacle(p.X, p.Y, r, wall[rng.Int(wall.Length)], rng.Int(9999)));
            }
        }
        // A thick woodland border around the edge of the map.
        for (var x = 0f; x < mid - 20; x += 85)
            foreach (var y in new[] { 20f, m.H - 20 })
                Mirror(new Obstacle(x + rng.Range(-10, 10), y + rng.Range(-8, 8), rng.Range(40, 52), wall[0], rng.Int(9999)));
        for (var y = 100f; y < m.H - 80; y += 85)
        {
            var o = new Obstacle(20 + rng.Range(-6, 6), y, rng.Range(40, 52), wall[0], rng.Int(9999));
            if (Vec.Dist(new Vec(o.X, o.Y), m.Spawn[1]) > 200) Mirror(o);
        }
        // Clusters in the woods, kept off the lane and away from camps, plants and bases.
        for (var i = 0; i < 260 && m.Obstacles.Count < 520; i++)
        {
            var x = rng.Range(120, mid - 50);
            var y = rng.Range(100, m.H - 100);
            var r = rng.Range(24, 44);
            var p = new Vec(x, y);
            if (MathF.Abs(y - LaneY(x)) < m.LaneWidth / 2 + 150 + r) continue;
            if (!Clear(p, r + 40)) continue;
            if (m.Obstacles.Any(o => Vec.Dist(new Vec(o.X, o.Y), p) < o.R + r + 95)) continue;
            Mirror(new Obstacle(x, y, r, scatter[rng.Int(scatter.Length)], rng.Int(9999)));
        }
        return m;
    }
}
