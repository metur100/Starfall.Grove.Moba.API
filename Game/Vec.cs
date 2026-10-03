namespace Starfall.Grove.Moba.Api.Game;

/// <summary>A 2D point or direction in world units (the map is a few thousand units across).</summary>
public readonly record struct Vec(float X, float Y)
{
    public static readonly Vec Zero = new(0, 0);
    public static Vec operator +(Vec a, Vec b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec operator -(Vec a, Vec b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec operator *(Vec a, float k) => new(a.X * k, a.Y * k);
    public static Vec operator /(Vec a, float k) => new(a.X / k, a.Y / k);
    public float Len => MathF.Sqrt(X * X + Y * Y);
    public float LenSq => X * X + Y * Y;
    public Vec Norm() { var l = Len; return l < 1e-4f ? Zero : new(X / l, Y / l); }
    public static float Dist(Vec a, Vec b) => (a - b).Len;
    public static float DistSq(Vec a, Vec b) => (a - b).LenSq;
    public static Vec FromAngle(float a) => new(MathF.Cos(a), MathF.Sin(a));
    public float Angle => MathF.Atan2(Y, X);
    public static float Dot(Vec a, Vec b) => a.X * b.X + a.Y * b.Y;
    /// <summary>Moves toward <paramref name="to"/> by at most <paramref name="step"/>.</summary>
    public Vec Toward(Vec to, float step)
    {
        var d = to - this; var l = d.Len;
        return l <= step || l < 1e-4f ? to : this + d * (step / l);
    }
}

/// <summary>Deterministic random numbers (one per match), so a map or a fight never depends on wall-clock luck.</summary>
public sealed class Rng(int seed)
{
    private uint _s = (uint)seed * 2654435761u + 1;
    public float Next() { _s ^= _s << 13; _s ^= _s >> 17; _s ^= _s << 5; return (_s & 0xFFFFFF) / (float)0x1000000; }
    public float Range(float a, float b) => a + Next() * (b - a);
    public int Int(int n) => (int)(Next() * n) % Math.Max(1, n);
}
