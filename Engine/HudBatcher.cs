using System.Numerics;

namespace VoxelMiner.Engine;

/// CPU-side accumulator for 2D quads, grouped into draw ranges by texture
/// in submission (painter) order. Vertex layout: pos2, uv2, rgba4 (8 floats).
public sealed class HudBatcher
{
    public struct Range
    {
        public TextureHandle Texture;
        public int FirstVertex;
        public int VertexCount;
    }

    public const int FloatsPerVertex = 8;

    public float[] Vertices = new float[65536];
    public int VertexFloats;
    public readonly List<Range> Ranges = new();
    TextureHandle _current;

    public void Clear()
    {
        VertexFloats = 0;
        Ranges.Clear();
        _current = null;
    }

    public void Quad(TextureHandle tex, float x, float y, float w, float h,
        float u0, float v0, float u1, float v1, Vector4 color)
    {
        if (_current != tex)
        {
            Ranges.Add(new Range { Texture = tex, FirstVertex = VertexFloats / FloatsPerVertex, VertexCount = 0 });
            _current = tex;
        }
        EnsureCapacity(6 * FloatsPerVertex);
        Vertex(x, y, u0, v0, color);
        Vertex(x, y + h, u0, v1, color);
        Vertex(x + w, y, u1, v0, color);
        Vertex(x + w, y, u1, v0, color);
        Vertex(x, y + h, u0, v1, color);
        Vertex(x + w, y + h, u1, v1, color);
        var last = Ranges[^1];
        last.VertexCount += 6;
        Ranges[^1] = last;
    }

    public void SolidQuad(TextureHandle white, float x, float y, float w, float h, Vector4 color) =>
        Quad(white, x, y, w, h, 0, 0, 1, 1, color);

    void Vertex(float x, float y, float u, float v, Vector4 c)
    {
        int i = VertexFloats;
        Vertices[i] = x;
        Vertices[i + 1] = y;
        Vertices[i + 2] = u;
        Vertices[i + 3] = v;
        Vertices[i + 4] = c.X;
        Vertices[i + 5] = c.Y;
        Vertices[i + 6] = c.Z;
        Vertices[i + 7] = c.W;
        VertexFloats += FloatsPerVertex;
    }

    void EnsureCapacity(int extra)
    {
        if (VertexFloats + extra <= Vertices.Length) return;
        Array.Resize(ref Vertices, Math.Max(Vertices.Length * 2, VertexFloats + extra));
    }
}
