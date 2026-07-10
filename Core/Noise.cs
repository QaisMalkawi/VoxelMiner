namespace VoxelMiner.Core;

/// Deterministic hash and value-noise functions used by terrain generation.
/// Faithful port of the JS version (32-bit integer math, same constants).
public static class Noise
{
    public static double Hash2(int x, int z)
    {
        unchecked
        {
            int n = x * 374761393 ^ z * 668265263 ^ Constants.Seed;
            n = (n ^ (int)((uint)n >> 13)) * 1274126177;
            return (uint)(n ^ (int)((uint)n >> 16)) / 4294967296.0;
        }
    }

    public static double Hash3(int x, int y, int z)
    {
        unchecked
        {
            int n = x * 374761393 ^ y * -2048144777 ^ z * 668265263 ^ Constants.Seed; // 2246822519 as int
            n = (n ^ (int)((uint)n >> 13)) * 1274126177;
            return (uint)(n ^ (int)((uint)n >> 16)) / 4294967296.0;
        }
    }

    public static double Noise2(double x, double z)
    {
        int ix = (int)Math.Floor(x), iz = (int)Math.Floor(z);
        double fx = x - ix, fz = z - iz;
        fx = fx * fx * (3 - 2 * fx);
        fz = fz * fz * (3 - 2 * fz);
        double a = Hash2(ix, iz), b = Hash2(ix + 1, iz), c = Hash2(ix, iz + 1), d = Hash2(ix + 1, iz + 1);
        return a + (b - a) * fx + (c - a) * fz + (a - b - c + d) * fx * fz;
    }

    public static double Fbm2(double x, double z)
    {
        return Noise2(x, z) * 0.55 + Noise2(x * 2.13, z * 2.13) * 0.27 +
               Noise2(x * 4.41, z * 4.41) * 0.13 + Noise2(x * 8.9, z * 8.9) * 0.05;
    }

    public static double Noise3(double x, double y, double z)
    {
        int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y), iz = (int)Math.Floor(z);
        double fx = x - ix, fy = y - iy, fz = z - iz;
        fx = fx * fx * (3 - 2 * fx);
        fy = fy * fy * (3 - 2 * fy);
        fz = fz * fz * (3 - 2 * fz);
        double c000 = Hash3(ix, iy, iz), c100 = Hash3(ix + 1, iy, iz);
        double c010 = Hash3(ix, iy + 1, iz), c110 = Hash3(ix + 1, iy + 1, iz);
        double c001 = Hash3(ix, iy, iz + 1), c101 = Hash3(ix + 1, iy, iz + 1);
        double c011 = Hash3(ix, iy + 1, iz + 1), c111 = Hash3(ix + 1, iy + 1, iz + 1);
        double x00 = c000 + (c100 - c000) * fx, x10 = c010 + (c110 - c010) * fx;
        double x01 = c001 + (c101 - c001) * fx, x11 = c011 + (c111 - c011) * fx;
        double y0 = x00 + (x10 - x00) * fy, y1 = x01 + (x11 - x01) * fy;
        return y0 + (y1 - y0) * fz;
    }
}
