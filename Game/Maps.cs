namespace Starfall.Grove.Moba.Api.Game;

/// <summary>Something that blocks movement: a tree, rock, crystal or pool. <c>K</c> names how the client draws it.</summary>
public sealed record Obstacle(float X, float Y, float R, string K, int S);

public sealed record CampDef(Vec Pos, string[] Kinds);

/// <summary>
/// One battlefield. Blue (team 1) starts on the left, Red (team 2) on the right. A battle map has one, two or three lanes
/// between the two Cores, each with an outer and an inner tower per team; walls of trees line the lanes, with gaps into
/// the woods where the camps, healing plants and the Star Warden are. A duel map is a small ring with no lanes at all.
/// Every map is mirrored left to right, so neither side has an advantage.
/// </summary>
public sealed class MapDef
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Theme { get; init; }
    /// <summary>"battle" or "duel".</summary>
    public string Type { get; init; } = "battle";
    public float W { get; init; } = 3200;
    public float H { get; init; } = 1600;
    public float LaneWidth { get; init; } = 300;
    /// <summary>Each lane from Blue's Core to Red's.</summary>
    public List<List<Vec>> Lanes { get; } = [];
    public List<Obstacle> Obstacles { get; } = [];
    public Vec[] Spawn { get; } = new Vec[3];
    public Vec[] Core { get; } = new Vec[3];
    /// <summary>[team][lane][0] is the lane's outer tower (Tower 1), [team][lane][1] its inner one (Tower 2).</summary>
    public Vec[][][] Towers { get; set; } = [[], [], []];
    public List<Vec> Plants { get; } = [];
    public List<CampDef> Camps { get; } = [];
    public Vec? Objective { get; set; }
    /// <summary>Duel maps: the middle of the ring and its radius.</summary>
    public Vec Center { get; set; }
    public float ArenaRadius { get; set; }
    public const float FountainRadius = 240;
    public bool Duel => Type == "duel";

    /// <summary>A lane as one team's minions walk it (Red's walk it backwards).</summary>
    public List<Vec> PathFor(int team, int lane) => team == 1 ? Lanes[lane] : Enumerable.Reverse(Lanes[lane]).ToList();
}

public static class Maps
{
    public sealed record Info(string Id, string Name, string Theme, string Type, int Lanes, string Blurb);

    public static readonly Info[] List =
    [
        new("glade", "Starfall Glade", "meadow", "battle", 1, "One sunny lane. The Star Warden sleeps in the northern ruins."),
        new("frost", "Frostfang Pass", "summit", "battle", 1, "One winding mountain lane. The Warden waits in the southern hollow."),
        new("twin", "Twinbrook Vale", "meadow", "battle", 2, "Two lanes around a wooded valley, with the Warden in its heart."),
        new("peaks", "Three Peaks", "summit", "battle", 3, "Three lanes across the snowy heights. Split up, or push together."),
        new("cinder3", "Cinder Crown", "ember", "battle", 3, "Three lanes through the ash fields, the Warden between top and middle."),
        new("moonring", "Moonpetal Ring", "meadow", "duel", 0, "A ring of standing stones in a meadow. Duel: no minions, just you."),
        new("frostring", "Frozen Circle", "summit", "duel", 0, "An icy arena under the peaks, with pillars to hide behind."),
        new("ashring", "Cinder Pit", "ember", "duel", 0, "A pit of ash and ember. The ring of fire closes in fast."),
    ];

    public static Info? Find(string id) => List.FirstOrDefault(m => m.Id == id);

    /// <summary>A map's layout for the little preview in room setup: lanes, rocks and trees, towers, Cores, camps and the
    /// Warden, in whole map units.</summary>
    public sealed record PreviewDto(string Id, string Theme, string Type, int W, int H, int LaneWidth, List<List<int[]>> Lanes,
        List<int[]> Obstacles, List<int[]> Cores, List<int[]> Towers, List<int[]> Camps, int[]? Objective, int[] Center, int ArenaRadius);

    private static readonly Lazy<PreviewDto[]> _previews = new(() => [.. List.Select(i =>
    {
        var m = Build(i.Id);
        static int R(float v) => (int)MathF.Round(v);
        return new PreviewDto(m.Id, m.Theme, m.Type, R(m.W), R(m.H), R(m.LaneWidth),
            m.Lanes.Select(l => l.Select(p => new[] { R(p.X), R(p.Y) }).ToList()).ToList(),
            m.Obstacles.Select(o => new[] { R(o.X), R(o.Y), R(o.R), o.K == "pool" ? 1 : 0 }).ToList(),
            m.Duel ? [] : [[R(m.Core[1].X), R(m.Core[1].Y), 1], [R(m.Core[2].X), R(m.Core[2].Y), 2]],
            [.. new[] { 1, 2 }.SelectMany(t => m.Towers[t].SelectMany(l => l).Select(p => new[] { R(p.X), R(p.Y), t }))],
            m.Camps.Select(c => new[] { R(c.Pos.X), R(c.Pos.Y) }).ToList(),
            m.Objective is { } o ? [R(o.X), R(o.Y)] : null, [R(m.Center.X), R(m.Center.Y)], R(m.ArenaRadius));
    })]);
    /// <summary>Every map's preview (built once).</summary>
    public static PreviewDto[] Previews => _previews.Value;

    public static MapDef Build(string id) => id switch
    {
        "frost" => OneLane("frost", "Frostfang Pass", "summit", amp: 120, waves: 2, objectiveTop: false, seed: 77,
            wall: ["pine", "pine", "rock"], scatter: ["pine", "rock", "crystal", "pine"]),
        "twin" => TwoLanes(),
        "peaks" => ThreeLanes("peaks", "Three Peaks", "summit", 41, ["pine", "pine", "rock"], ["pine", "rock", "crystal", "pine"]),
        "cinder3" => ThreeLanes("cinder3", "Cinder Crown", "ember", 59, ["deadtree", "rock", "rock"], ["rock", "deadtree", "crystal", "stump"]),
        "moonring" => Arena("moonring", "Moonpetal Ring", "meadow", 3, ["tree", "bush", "tree"], "rock"),
        "frostring" => Arena("frostring", "Frozen Circle", "summit", 9, ["pine", "pine", "rock"], "crystal"),
        "ashring" => Arena("ashring", "Cinder Pit", "ember", 15, ["deadtree", "rock"], "rock"),
        _ => OneLane("glade", "Starfall Glade", "meadow", amp: 70, waves: 1, objectiveTop: true, seed: 5,
            wall: ["tree", "tree", "bush"], scatter: ["tree", "bush", "rock", "mushroom", "tree"]),
    };

    // ───────────────────────────── one lane

    private static MapDef OneLane(string id, string name, string theme, float amp, int waves, bool objectiveTop, int seed, string[] wall, string[] scatter)
    {
        var m = new MapDef { Id = id, Name = name, Theme = theme };
        float x0 = 300, x1 = m.W - 300, mid = m.W / 2;
        float LaneY(float x) { var u = (x - mid) / (x1 - x0); return m.H / 2 + amp * MathF.Cos(2 * MathF.PI * waves * u); }
        var lane = new List<Vec>();
        for (var x = x0; x <= x1 + .1f; x += 40) lane.Add(new Vec(x, LaneY(x)));
        m.Lanes.Add(lane);
        Bases(m);

        float top = (60 + LaneY(mid) - m.LaneWidth / 2 - 90) / 2, bottom = (m.H - 60 + LaneY(mid) + m.LaneWidth / 2 + 90) / 2;
        float objY = objectiveTop ? top : bottom, campY = objectiveTop ? bottom : top;
        m.Objective = new Vec(mid, objY);
        m.Camps.Add(new CampDef(new Vec(mid - 620, campY), ["boar", "wolf"]));
        m.Camps.Add(new CampDef(new Vec(mid + 620, campY), ["boar", "wolf"]));
        m.Plants.Add(new Vec(mid - 820, objY)); m.Plants.Add(new Vec(mid + 820, objY));
        m.Plants.Add(new Vec(mid, campY));
        if (theme == "ember") m.Obstacles.Add(new Obstacle(mid, campY + (objectiveTop ? 90 : -90), 95, "pool", 1));
        Woods(m, new Rng(seed), wall, scatter);
        return m;
    }

    // ───────────────────────────── two lanes

    private static MapDef TwoLanes()
    {
        var m = new MapDef { Id = "twin", Name = "Twinbrook Vale", Theme = "meadow", W = 3600, H = 2000, LaneWidth = 280 };
        float mid = m.W / 2, cy = m.H / 2;
        var core = new Vec(300, cy);
        var top = Curve([core, new(620, 520), new(1300, 330), new(mid, 300), new(m.W - 1300, 330), new(m.W - 620, 520), new(m.W - 300, cy)]);
        m.Lanes.Add(top);
        m.Lanes.Add(top.Select(p => new Vec(p.X, m.H - p.Y)).ToList());
        Bases(m);
        m.Objective = new Vec(mid, cy);
        m.Camps.Add(new CampDef(new Vec(mid - 720, cy), ["boar", "wolf"]));
        m.Camps.Add(new CampDef(new Vec(mid + 720, cy), ["boar", "wolf"]));
        m.Plants.Add(new Vec(mid, cy - 360)); m.Plants.Add(new Vec(mid, cy + 360));
        m.Plants.Add(new Vec(mid - 1150, cy)); m.Plants.Add(new Vec(mid + 1150, cy));
        Woods(m, new Rng(23), ["tree", "tree", "bush"], ["tree", "bush", "rock", "mushroom", "tree"]);
        return m;
    }

    // ───────────────────────────── three lanes

    private static MapDef ThreeLanes(string id, string name, string theme, int seed, string[] wall, string[] scatter)
    {
        var m = new MapDef { Id = id, Name = name, Theme = theme, W = 3800, H = 2400, LaneWidth = 270 };
        float mid = m.W / 2, cy = m.H / 2;
        var core = new Vec(300, cy);
        var top = Curve([core, new(560, 620), new(1100, 340), new(mid, 300), new(m.W - 1100, 340), new(m.W - 560, 620), new(m.W - 300, cy)]);
        var middle = Curve([core, new(900, cy - 60), new(mid, cy), new(m.W - 900, cy + 60), new(m.W - 300, cy)]);
        // The middle lane is mirrored too: keep its wobble symmetric about the centre.
        middle = middle.Select(p => new Vec(p.X, cy + (p.Y - cy) * (p.X < mid ? 1 : -1))).ToList();
        m.Lanes.Add(top);
        m.Lanes.Add(middle);
        m.Lanes.Add(top.Select(p => new Vec(p.X, m.H - p.Y)).ToList());
        Bases(m);
        float upper = (300 + cy) / 2 + 20, lower = m.H - upper;
        m.Objective = new Vec(mid, upper);
        m.Camps.Add(new CampDef(new Vec(mid - 700, lower), ["boar", "wolf"]));
        m.Camps.Add(new CampDef(new Vec(mid + 700, lower), ["boar", "wolf"]));
        m.Plants.Add(new Vec(mid, lower)); m.Plants.Add(new Vec(mid - 800, upper)); m.Plants.Add(new Vec(mid + 800, upper));
        if (theme == "ember") m.Obstacles.Add(new Obstacle(mid, lower + 110, 90, "pool", 1));
        Woods(m, new Rng(seed), wall, scatter);
        return m;
    }

    // ───────────────────────────── duel arenas

    private static MapDef Arena(string id, string name, string theme, int seed, string[] wall, string cover)
    {
        var m = new MapDef { Id = id, Name = name, Theme = theme, Type = "duel", W = 1900, H = 1300, LaneWidth = 0 };
        var rng = new Rng(seed);
        m.Center = new Vec(m.W / 2, m.H / 2);
        m.ArenaRadius = 560;
        m.Spawn[1] = m.Center + new Vec(-400, 0); m.Spawn[2] = m.Center + new Vec(400, 0);
        // A wall of trees (or pines, or dead trees) all the way round.
        for (var a = 0f; a < MathF.PI * 2 - .01f; a += .12f)
        {
            var r = m.ArenaRadius + 70 + rng.Range(0, 30);
            Add(m, new Obstacle(m.Center.X + MathF.Cos(a) * r, m.Center.Y + MathF.Sin(a) * r * .95f, rng.Range(40, 52), wall[rng.Int(wall.Length)], rng.Int(9999)));
        }
        for (var a = .05f; a < MathF.PI * 2; a += .2f)
        {
            var r = m.ArenaRadius + 190 + rng.Range(0, 120);
            var p = m.Center + new Vec(MathF.Cos(a) * r, MathF.Sin(a) * r * .95f);
            if (p.X > 30 && p.X < m.W - 30 && p.Y > 30 && p.Y < m.H - 30) Add(m, new Obstacle(p.X, p.Y, rng.Range(36, 50), wall[rng.Int(wall.Length)], rng.Int(9999)));
        }
        // Stones to duck behind: they block movement, sight, shots and targeted spells. Four big ones round the middle
        // and two smaller ones on the far edges.
        foreach (var (dx, dy, r) in new[] { (-200f, -185f, 60f), (200f, 185f, 60f), (-200f, 185f, 60f), (200f, -185f, 60f), (0f, -360f, 50f), (0f, 360f, 50f) })
            Add(m, new Obstacle(m.Center.X + dx, m.Center.Y + dy * .95f, r, cover, rng.Int(9999)));
        return m;
    }

    private static void Add(MapDef m, Obstacle o) => m.Obstacles.Add(o);

    // ───────────────────────────── shared pieces

    /// <summary>Spawns behind each Core, and the two towers of every lane placed along it.</summary>
    private static void Bases(MapDef m)
    {
        var first = m.Lanes[0];
        m.Core[1] = first[0]; m.Core[2] = first[^1];
        m.Spawn[1] = new Vec(120, m.Core[1].Y); m.Spawn[2] = new Vec(m.W - 120, m.Core[2].Y);
        m.Towers = [[], new Vec[m.Lanes.Count][], new Vec[m.Lanes.Count][]];
        for (var l = 0; l < m.Lanes.Count; l++)
        {
            m.Towers[1][l] = [At(m.Lanes[l], .33f), At(m.Lanes[l], .15f)];
            m.Towers[2][l] = [At(m.Lanes[l], .67f), At(m.Lanes[l], .85f)];
        }
    }

    /// <summary>The point a fraction of the way along a path, by distance.</summary>
    public static Vec At(List<Vec> path, float f)
    {
        var total = 0f;
        for (var i = 1; i < path.Count; i++) total += Vec.Dist(path[i - 1], path[i]);
        var want = total * f;
        for (var i = 1; i < path.Count; i++)
        {
            var d = Vec.Dist(path[i - 1], path[i]);
            if (want <= d) return path[i - 1] + (path[i] - path[i - 1]) * (d < .01f ? 0 : want / d);
            want -= d;
        }
        return path[^1];
    }

    /// <summary>A smooth path through the given points (Catmull-Rom), with a point about every 40 units.</summary>
    private static List<Vec> Curve(Vec[] pts)
    {
        var outp = new List<Vec>();
        for (var i = 0; i < pts.Length - 1; i++)
        {
            Vec p0 = pts[Math.Max(0, i - 1)], p1 = pts[i], p2 = pts[i + 1], p3 = pts[Math.Min(pts.Length - 1, i + 2)];
            var steps = Math.Max(2, (int)(Vec.Dist(p1, p2) / 40));
            for (var s = 0; s < steps; s++)
            {
                var t = s / (float)steps; float t2 = t * t, t3 = t2 * t;
                outp.Add(new Vec(
                    .5f * (2 * p1.X + (-p0.X + p2.X) * t + (2 * p0.X - 5 * p1.X + 4 * p2.X - p3.X) * t2 + (-p0.X + 3 * p1.X - 3 * p2.X + p3.X) * t3),
                    .5f * (2 * p1.Y + (-p0.Y + p2.Y) * t + (2 * p0.Y - 5 * p1.Y + 4 * p2.Y - p3.Y) * t2 + (-p0.Y + 3 * p1.Y - 3 * p2.Y + p3.Y) * t3)));
            }
        }
        outp.Add(pts[^1]);
        return outp;
    }

    public static float DistToPath(List<Vec> path, Vec p)
    {
        var best = float.MaxValue;
        for (var i = 1; i < path.Count; i++)
        {
            Vec a = path[i - 1], b = path[i], ab = b - a;
            var t = Math.Clamp(Vec.Dot(p - a, ab) / MathF.Max(.01f, ab.LenSq), 0, 1);
            best = MathF.Min(best, Vec.Dist(p, a + ab * t));
        }
        return best;
    }

    /// <summary>Walls of trees along every lane (with gaps into the woods), a woodland border, and clusters in the woods,
    /// kept clear of lanes, bases, camps, plants and the Warden. Built on the left half and mirrored.</summary>
    private static void Woods(MapDef m, Rng rng, string[] wall, string[] scatter)
    {
        float mid = m.W / 2;
        var keepClear = new List<(Vec P, float R)> { (m.Spawn[1], 380), (m.Spawn[2], 380), (m.Core[1], 260), (m.Core[2], 260) };
        if (m.Objective is { } obj) keepClear.Add((obj, 210));
        foreach (var c in m.Camps) keepClear.Add((c.Pos, 190));
        foreach (var p in m.Plants) keepClear.Add((p, 110));
        foreach (var o in m.Obstacles) keepClear.Add((new Vec(o.X, o.Y), o.R + 80));
        float LaneDist(Vec p) => m.Lanes.Min(l => DistToPath(l, p));
        bool Clear(Vec p, float r) => keepClear.All(k => Vec.Dist(k.P, p) > k.R + r) && LaneDist(p) > m.LaneWidth / 2 + r;
        void Mirror(Obstacle o) { m.Obstacles.Add(o); m.Obstacles.Add(o with { X = m.W - o.X, S = o.S + 7 }); }

        foreach (var lane in m.Lanes)
        {
            var total = 0f; var lengths = new List<float> { 0 };
            for (var i = 1; i < lane.Count; i++) { total += Vec.Dist(lane[i - 1], lane[i]); lengths.Add(total); }
            for (var i = 1; i < lane.Count - 1; i++)
            {
                if (lane[i].X >= mid - 20) break;
                var f = lengths[i] / total;
                if (f < .06f || (i % 2 == 1)) continue;
                var dir = (lane[i + 1] - lane[i - 1]).Norm();
                var normal = new Vec(-dir.Y, dir.X);
                foreach (var side in new[] { -1, 1 })
                {
                    var gaps = side < 0 ? new[] { .2f, .4f } : new[] { .25f, .45f };
                    if (gaps.Any(g => MathF.Abs(f - g) < .035f)) continue;
                    var r = rng.Range(34, 46);
                    var p = lane[i] + normal * (side * (m.LaneWidth / 2 + r + rng.Range(4, 16)));
                    if (!Clear(p, r - 2)) continue;
                    if (m.Obstacles.Any(o => Vec.Dist(new Vec(o.X, o.Y), p) < o.R + r - 10)) continue;
                    Mirror(new Obstacle(p.X, p.Y, r, wall[rng.Int(wall.Length)], rng.Int(9999)));
                }
            }
        }
        for (var x = 0f; x < mid - 20; x += 85)
            foreach (var y in new[] { 20f, m.H - 20 })
                Mirror(new Obstacle(x + rng.Range(-10, 10), y + rng.Range(-8, 8), rng.Range(40, 52), wall[0], rng.Int(9999)));
        for (var y = 100f; y < m.H - 80; y += 85)
        {
            var o = new Obstacle(20 + rng.Range(-6, 6), y, rng.Range(40, 52), wall[0], rng.Int(9999));
            if (Vec.Dist(new Vec(o.X, o.Y), m.Spawn[1]) > 200) Mirror(o);
        }
        var target = (int)(m.W * m.H / 20000);
        for (var i = 0; i < target * 2 && m.Obstacles.Count < target * 2; i++)
        {
            var x = rng.Range(120, mid - 50);
            var y = rng.Range(100, m.H - 100);
            var r = rng.Range(24, 44);
            var p = new Vec(x, y);
            if (LaneDist(p) < m.LaneWidth / 2 + 150 + r) continue;
            if (!Clear(p, r + 40)) continue;
            if (m.Obstacles.Any(o => Vec.Dist(new Vec(o.X, o.Y), p) < o.R + r + 95)) continue;
            Mirror(new Obstacle(x, y, r, scatter[rng.Int(scatter.Length)], rng.Int(9999)));
        }
    }
}
