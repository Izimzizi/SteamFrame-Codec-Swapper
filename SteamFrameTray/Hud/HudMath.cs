using System.Numerics;

namespace SteamFrameTray;

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
