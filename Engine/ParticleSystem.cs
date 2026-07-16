using System.Numerics;

namespace VoxelMiner.Engine;

/// Short-lived debris cubes spawned when a block breaks.
public sealed class ParticleSystem
{
    const float Gravity = 14f;
    const float Lifetime = 0.55f;

    sealed class Particle
    {
        public Vector3 Pos, Vel, Color;
        public float Life, RotX, RotY;
    }

    readonly List<Particle> _particles = new();
    static readonly Random Rng = new();

    public void Burst(float x, float y, float z, Vector3 color)
    {
        for (int i = 0; i < 10; i++)
        {
            _particles.Add(new Particle
            {
                Pos = new Vector3(x + 0.2f + (float)Rng.NextDouble() * 0.6f,
                                  y + 0.2f + (float)Rng.NextDouble() * 0.6f,
                                  z + 0.2f + (float)Rng.NextDouble() * 0.6f),
                Vel = new Vector3(((float)Rng.NextDouble() - .5f) * 4, 2 + (float)Rng.NextDouble() * 3, ((float)Rng.NextDouble() - .5f) * 4),
                Color = color,
                Life = Lifetime,
            });
        }
    }

    public void Update(float dt)
    {
        for (int i = _particles.Count - 1; i >= 0; i--)
        {
            var p = _particles[i];
            p.Life -= dt;
            if (p.Life <= 0)
            {
                _particles.RemoveAt(i);
                continue;
            }
            p.Vel.Y -= Gravity * dt;
            p.Pos += p.Vel * dt;
            p.RotX += dt * 6;
            p.RotY += dt * 5;
        }
    }

    public void Draw(Renderer renderer, Mesh cube, TextureHandle white, Frustum frustum)
    {
        foreach (var p in _particles)
        {
            if (!frustum.SphereVisible(p.Pos, 0.25f)) continue;
            var model = Matrix4x4.CreateScale(0.12f)
                      * Matrix4x4.CreateRotationX(p.RotX)
                      * Matrix4x4.CreateRotationY(p.RotY)
                      * Matrix4x4.CreateTranslation(p.Pos);
            renderer.DrawMesh(cube, model, new Vector4(p.Color, 1f), textured: false, white);
        }
    }
}
