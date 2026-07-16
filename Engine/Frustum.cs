using System.Numerics;

namespace VoxelMiner.Engine;

/// View-frustum culling: the 6 clip planes of a row-vector (v * M)
/// view-projection, with AABB and sphere visibility tests. One instance is
/// reused frame to frame — call Set once per frame, then test everything
/// that's about to be submitted.
public sealed class Frustum
{
    readonly Vector4[] _planes = new Vector4[6];

    /// Extracts the 6 clip planes (Vulkan clip z in 0..1).
    public void Set(in Matrix4x4 m)
    {
        var c0 = new Vector4(m.M11, m.M21, m.M31, m.M41);
        var c1 = new Vector4(m.M12, m.M22, m.M32, m.M42);
        var c2 = new Vector4(m.M13, m.M23, m.M33, m.M43);
        var c3 = new Vector4(m.M14, m.M24, m.M34, m.M44);
        _planes[0] = c3 + c0; // left
        _planes[1] = c3 - c0; // right
        _planes[2] = c3 + c1; // bottom
        _planes[3] = c3 - c1; // top
        _planes[4] = c2;      // near
        _planes[5] = c3 - c2; // far
        // normalize so sphere tests can compare distances against radii
        for (int i = 0; i < 6; i++)
        {
            var p = _planes[i];
            float len = MathF.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z);
            if (len > 1e-8f) _planes[i] = p / len;
        }
    }

    /// Positive-vertex test: the AABB corner farthest along each plane
    /// normal must be on the inside, else the whole box is out.
    public bool BoxVisible(Vector3 min, Vector3 max)
    {
        foreach (var p in _planes)
        {
            float vx = p.X >= 0 ? max.X : min.X;
            float vy = p.Y >= 0 ? max.Y : min.Y;
            float vz = p.Z >= 0 ? max.Z : min.Z;
            if (vx * p.X + vy * p.Y + vz * p.Z + p.W < 0) return false;
        }
        return true;
    }

    /// Conservative sphere test against the normalized planes.
    public bool SphereVisible(Vector3 center, float radius)
    {
        foreach (var p in _planes)
            if (p.X * center.X + p.Y * center.Y + p.Z * center.Z + p.W < -radius)
                return false;
        return true;
    }
}
