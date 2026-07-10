using System.Numerics;

namespace VoxelMiner.UI;

using VoxelMiner.Engine;
using VoxelMiner.Entities;

/// Draws animals as sets of textured unit-cube parts with leg/head animation.
/// Each part samples its tiles from the animals texture atlas; the +X face of
/// a head part carries the face tile (eyes, snout).
public static class AnimalRenderer
{
    public static void Draw(Renderer renderer, CubeMeshCache meshes, TextureHandle animalTexture, IEnumerable<Animal> animals)
    {
        foreach (var animal in animals)
        {
            // row-vector CreateRotationY maps +X → (cos, 0, -sin), matching the
            // yaw convention in Animal (TargetYaw = atan2(-dz, dx))
            var baseTransform = Matrix4x4.CreateScale(animal.Def.Scale)
                              * Matrix4x4.CreateRotationY(animal.Yaw)
                              * Matrix4x4.CreateTranslation(animal.Pos);
            int legIndex = 0;
            foreach (var part in animal.Def.Parts)
            {
                float rot = part.Kind switch
                {
                    PartKind.Leg => legIndex++ % 2 == 0 ? -animal.LegSwing : animal.LegSwing,
                    PartKind.Head => animal.HeadDip,
                    _ => 0f,
                };
                var model = Matrix4x4.CreateScale(part.Size)
                          * Matrix4x4.CreateTranslation(part.LocalPos)
                          * Matrix4x4.CreateRotationZ(rot)
                          * Matrix4x4.CreateTranslation(part.PivotPos)
                          * baseTransform;
                var mesh = meshes.Get(part.SideTile, part.FrontTile);
                renderer.DrawMesh(mesh, model, Vector4.One, textured: true, animalTexture);
            }
        }
    }
}
