namespace Starfall.Grove.Moba.Api.Game;

// What the server sends during a match. Keys are short because a snapshot goes to every player 15 times a second.

/// <summary>A unit as one team sees it. F is facing in degrees, St the <see cref="St"/> flags, Sh the shield left.</summary>
public sealed record UnitDto(int I, string K, int Tm, int X, int Y, int Hp, int Mh, int F, int St, int Lv, int Sh);
public sealed record ProjDto(int I, string K, int X, int Y, int Vx, int Vy, int Tm);
/// <summary>A zone; T is milliseconds left.</summary>
public sealed record ZoneDto(int I, string K, int X, int Y, int R, int Tm, int T);

/// <summary>Something that happened this tick, for animations, sounds, numbers and the kill feed.</summary>
public sealed class FxDto
{
    public string E { get; set; } = "";
    public int? U { get; set; }
    public int? U2 { get; set; }
    public int? X { get; set; }
    public int? Y { get; set; }
    public int? X2 { get; set; }
    public int? Y2 { get; set; }
    public int? V { get; set; }
    public int? R { get; set; }
    public string? K { get; set; }
    public int? Tm { get; set; }
}

public sealed record PlayerStatDto(string Id, int U, int K, int D, int A, int Lv, int Rs);

public sealed class SnapshotDto
{
    public float T { get; set; }
    public required List<UnitDto> U { get; init; }
    public required List<ProjDto> P { get; init; }
    public required List<ZoneDto> Z { get; init; }
    public required List<FxDto> Fx { get; init; }
    public required int[] Sc { get; init; }
    public required List<PlayerStatDto> Ps { get; init; }
    /// <summary>Which healing plants are in bloom (bit i = plant i).</summary>
    public int Pa { get; set; }
    /// <summary>Seconds until the Star Warden returns (0 while it is up).</summary>
    public int Ob { get; set; }
    /// <summary>0 normal, 1 to 3 the stages of sudden death (3: every structure can be attacked).</summary>
    public int Sd { get; set; }
    /// <summary>During sudden death, the team with the Star's favour (stronger minions).</summary>
    public int Fv { get; set; }
}

/// <summary>The private part of a snapshot: what only this player needs about their own hero.</summary>
public sealed class MeDto
{
    public int U { get; set; }
    public int G { get; set; }
    public int Lv { get; set; }
    public int Xp { get; set; }
    public int Xn { get; set; }
    public int Mp { get; set; }
    public int Mm { get; set; }
    public required float[] Cd { get; init; }
    public required float[] Cm { get; init; }
    public required int[] Mc { get; init; }
    public required List<string>[] Up { get; init; }
    public float Rs { get; set; }
    public int Sp { get; set; }
    public int Vx { get; set; }
    public int Vy { get; set; }
    public int Ad { get; set; }
}

public sealed record MapDto(string Id, string Name, string Theme, float W, float H, float LaneWidth,
    List<int[]> Lane, List<Obstacle> Obstacles, int[][] Spawn, List<int[]> Plants, List<int[]> Camps, int[] Objective, float FountainRadius);

public sealed record MatchHeroDto(string PlayerId, string Name, string Hero, int Team, int U, bool Bot);

public sealed record MatchInitDto(MapDto Map, List<MatchHeroDto> Heroes, string You, int Team, float Tick);

public sealed record MatchEndDto(int Winner, float Duration, List<MatchEndPlayerDto> Players);
public sealed record MatchEndPlayerDto(string Id, string Name, string Hero, int Team, int K, int D, int A, int Lv, int Gold, int Damage, int Healing, bool Bot);
