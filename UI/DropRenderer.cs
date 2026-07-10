using System.Numerics;

namespace VoxelMiner.UI;

using VoxelMiner.Engine;
using VoxelMiner.Gameplay.Drops;
using VoxelMiner.World;

/// Draws item drops as small spinning cubes textured with the dropped
/// block's side tile from the world atlas (non-block items fall back to the
/// planks tile). Stacks of more than one draw a second offset cube.
public static class DropRenderer
{
    public static void Draw(Renderer renderer, CubeMeshCache blockCubes, TextureHandle atlas, IEnumerable<ItemDrop> drops)
    {
        foreach (var d in drops)
        {
            int tile = BlockRegistry.Blocks.TryGetValue(d.Id, out var def)
                ? def.Tiles[2]
                : BlockRegistry.Blocks[Core.BlockId.Planks].Tiles[2];
            var mesh = blockCubes.Get(tile, tile);
            float bob = MathF.Sin(d.Age * 2.4f) * 0.03f;
            var model = Matrix4x4.CreateScale(0.26f)
                      * Matrix4x4.CreateRotationY(d.Age * 1.9f)
                      * Matrix4x4.CreateTranslation(d.Pos + new Vector3(0, bob, 0));
            renderer.DrawMesh(mesh, model, Vector4.One, textured: true, atlas);
            if (d.Count > 1)
            {
                var model2 = Matrix4x4.CreateScale(0.26f)
                           * Matrix4x4.CreateRotationY(d.Age * 1.9f + 0.9f)
                           * Matrix4x4.CreateTranslation(d.Pos + new Vector3(0.09f, 0.07f + bob, 0.06f));
                renderer.DrawMesh(mesh, model2, Vector4.One, textured: true, atlas);
            }
        }
    }
}
