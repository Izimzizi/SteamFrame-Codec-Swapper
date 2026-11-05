using System.Numerics;

namespace CodecSwapper;

/// <summary>
/// A rigid transform in OpenVR's convention: three rotation columns and a translation, applied as x' = R·x + T. Converts to and from the
/// row-major 3x4 matrix (HmdMatrix34_t) the overlay API uses.
/// </summary>
public readonly record struct Pose(Vector3 X, Vector3 Y, Vector3 Z, Vector3 T)
{
    public static readonly Pose Identity = new(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, Vector3.Zero);

    public Vector3 Rotate(Vector3 v) => X * v.X + Y * v.Y + Z * v.Z;
    public Vector3 Apply(Vector3 v) => Rotate(v) + T;

    /// <summary>this ∘ other: other is applied first, then this.</summary>
    public Pose Then(Pose other) => new(Rotate(other.X), Rotate(other.Y), Rotate(other.Z), Apply(other.T));

    public Pose Inverse()
    {
        var x = new Vector3(X.X, Y.X, Z.X);
        var y = new Vector3(X.Y, Y.Y, Z.Y);
        var z = new Vector3(X.Z, Y.Z, Z.Z);
        var inverse = new Pose(x, y, z, Vector3.Zero);
        return inverse with { T = -inverse.Rotate(T) };
    }

    /// <summary>Re-squares the rotation (Gram-Schmidt) so repeated composition never drifts into a skew.</summary>
    public Pose Orthonormal()
    {
        var x = Vector3.Normalize(X);
        var y = Vector3.Normalize(Y - x * Vector3.Dot(Y, x));
        return new(x, y, Vector3.Cross(x, y), T);
    }

    public static Pose Rotation(float yawDegrees, float pitchDegrees, float rollDegrees, Vector3 position)
    {
        static Pose Axis(Vector3 axis, float degrees)
        {
            var angle = degrees * MathF.PI / 180f;
            var (s, c) = MathF.SinCos(angle);
            // Rodrigues for the three unit columns.
            Vector3 Turn(Vector3 v) => v * c + Vector3.Cross(axis, v) * s + axis * Vector3.Dot(axis, v) * (1 - c);
            return new(Turn(Vector3.UnitX), Turn(Vector3.UnitY), Turn(Vector3.UnitZ), Vector3.Zero);
        }
        var rotation = Axis(Vector3.UnitY, yawDegrees).Then(Axis(Vector3.UnitX, pitchDegrees)).Then(Axis(Vector3.UnitZ, rollDegrees));
        return rotation with { T = position };
    }

    /// <summary>Row-major 3x4 as OpenVR stores it: row r holds (X[r], Y[r], Z[r], T[r]).</summary>
    public float[] ToMatrix34() =>
    [
        X.X, Y.X, Z.X, T.X,
        X.Y, Y.Y, Z.Y, T.Y,
        X.Z, Y.Z, Z.Z, T.Z
    ];

    public static bool TryFromMatrix34(IReadOnlyList<float>? m, out Pose pose)
    {
        pose = Identity;
        if (m is null || m.Count != 12 || m.Any(v => !float.IsFinite(v))) return false;
        var candidate = new Pose(new(m[0], m[4], m[8]), new(m[1], m[5], m[9]), new(m[2], m[6], m[10]), new(m[3], m[7], m[11]));
        // A usable pose has a real rotation; reject a zero or wildly scaled matrix.
        if (MathF.Abs(candidate.X.Length() - 1) > .05f || MathF.Abs(candidate.Y.Length() - 1) > .05f || MathF.Abs(candidate.Z.Length() - 1) > .05f) return false;
        pose = candidate.Orthonormal();
        return true;
    }
}

/// <summary>Where a pointing ray meets a flat overlay, and the geometry of the laser drawn along it. Used when SteamVR draws no laser of its own (the dashboard is closed).</summary>
public static class HudAim
{
    /// <summary>The laser is still drawn this far (metres) outside an overlay's edge, so it can be seen approaching; presses only count inside.</summary>
    public const float NearMargin = .03f;

    /// <summary>
    /// The ray starts at the tip pose and runs along its -Z. The overlay faces +Z of its pose, is centred on it and is
    /// <paramref name="widthMeters"/> wide with the aspect of its picture. x and y are picture pixels with the origin bottom-left (the way SteamVR reports laser events).
    /// False when the ray is parallel, behind, too far, or outside the edge by more than <paramref name="marginMeters"/>; x and y are clamped to the picture.
    /// </summary>
    public static bool TryHit(Pose tip, Pose overlay, float widthMeters, int pixelsWide, int pixelsHigh, float marginMeters, out float x, out float y, out float distance, out bool inside, out Vector3 point, bool allowBack = false)
    {
        x = y = distance = 0; inside = false; point = default;
        if (widthMeters <= 0 || pixelsWide <= 0 || pixelsHigh <= 0) return false;
        var direction = tip.Rotate(-Vector3.UnitZ);
        var normal = overlay.Rotate(Vector3.UnitZ);
        var facing = Vector3.Dot(direction, normal);
        if (MathF.Abs(facing) < 1e-4f) return false;
        if (facing > 0 && !allowBack) return false;   // the ray runs along the normal: it meets the back of the HUD, which does not count
        distance = Vector3.Dot(overlay.T - tip.T, normal) / facing;
        if (distance <= 0 || distance > 2f) return false;
        point = tip.T + direction * distance;
        var offset = point - overlay.T;
        var height = widthMeters * pixelsHigh / pixelsWide;
        var across = Vector3.Dot(offset, overlay.Rotate(Vector3.UnitX));
        var up = Vector3.Dot(offset, overlay.Rotate(Vector3.UnitY));
        if (MathF.Abs(across) > widthMeters / 2 + marginMeters || MathF.Abs(up) > height / 2 + marginMeters) return false;
        inside = MathF.Abs(across) <= widthMeters / 2 && MathF.Abs(up) <= height / 2;
        x = Math.Clamp((across / widthMeters + .5f) * pixelsWide, 0, pixelsWide);
        y = Math.Clamp((up / height + .5f) * pixelsHigh, 0, pixelsHigh);
        return true;
    }

    /// <summary>
    /// The pose of a flat strip between two points: X runs along it (an overlay as wide as the line is long, with a very flat picture, is the line),
    /// Z turns toward the viewer so it never shows edge-on, and it is centred between the points.
    /// </summary>
    public static Pose Line(Vector3 from, Vector3 to, Vector3 viewer, out float length)
    {
        var along = to - from;
        length = along.Length();
        var x = length > 1e-4f ? along / length : Vector3.UnitX;
        var middle = (from + to) / 2;
        var toViewer = viewer - middle;
        var z = toViewer - x * Vector3.Dot(toViewer, x);
        if (z.LengthSquared() < 1e-6f) z = Vector3.Cross(x, Math.Abs(x.Y) < .9f ? Vector3.UnitY : Vector3.UnitZ);
        z = Vector3.Normalize(z);
        return new(x, Vector3.Cross(z, x), z, middle);
    }
}
