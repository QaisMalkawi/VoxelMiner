using System.Numerics;

namespace VoxelMiner.UI;

using VoxelMiner.Engine;
using VoxelMiner.Gameplay;

/// Draws boats as a handful of tinted shaded-cube boxes: a hull floor and
/// four low walls, with the bow along the boat's +X (yaw) direction.
public static class BoatRenderer
{
    static readonly Vector4 Hull = new(0.55f, 0.38f, 0.20f, 1f);
    static readonly Vector4 Trim = new(0.42f, 0.28f, 0.14f, 1f);

    public static void Draw(Renderer renderer, Mesh cube, TextureHandle white, IEnumerable<Boat> boats)
    {
        foreach (var boat in boats)
        {
            var baseTransform = Matrix4x4.CreateRotationY(boat.Yaw)
                              * Matrix4x4.CreateTranslation(boat.Pos);
            void Box(float sx, float sy, float sz, float ox, float oy, float oz, Vector4 color) =>
                renderer.DrawMesh(cube,
                    Matrix4x4.CreateScale(sx, sy, sz) * Matrix4x4.CreateTranslation(ox, oy, oz) * baseTransform,
                    color, textured: false, white);

            // the deck top sits just above the waterline (Pos.Y) so the
            // translucent water plane never shows through the hull interior
            Box(1.5f, 0.12f, 0.9f, 0, -0.02f, 0, Hull);            // floor
            Box(1.5f, 0.28f, 0.12f, 0, 0.16f, -0.39f, Trim);       // port wall
            Box(1.5f, 0.28f, 0.12f, 0, 0.16f, 0.39f, Trim);        // starboard wall
            Box(0.12f, 0.28f, 0.66f, 0.69f, 0.16f, 0, Trim);       // bow
            Box(0.12f, 0.28f, 0.66f, -0.69f, 0.16f, 0, Trim);      // stern
        }
    }
}
