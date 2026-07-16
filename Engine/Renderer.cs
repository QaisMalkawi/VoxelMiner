using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace VoxelMiner.Engine;

[StructLayout(LayoutKind.Sequential)]
public struct GlobalUbo
{
    public Matrix4x4 ViewProj;
    public Vector4 CamPos;
    public Vector4 FogColor;
    public Vector4 FogParams; // near, far, torch, baseLight
}

[StructLayout(LayoutKind.Sequential)]
struct WorldPush
{
    public Matrix4x4 Model;
    public Vector4 Tint;
    public Vector4 Params; // x = textured
}

/// A texture registered with the renderer: one descriptor set per frame in flight.
public sealed class TextureHandle
{
    public GpuTexture Texture;
    public DescriptorSet[] Sets;
}

/// High-level frame API over the Vulkan context: world meshes with push-constant
/// transforms, line meshes, and batched 2D HUD geometry.
public unsafe sealed class Renderer : IDisposable
{
    public readonly VulkanContext Ctx;
    public readonly Pipelines Pipelines;

    readonly GpuBuffer[] _ubos = new GpuBuffer[VulkanContext.FramesInFlight];
    readonly GpuBuffer[] _hudBuffers = new GpuBuffer[VulkanContext.FramesInFlight];
    const int HudBufferBytes = 4 * 1024 * 1024;

    DescriptorPool _descriptorPool;
    readonly List<TextureHandle> _textures = new();
    readonly List<IDisposable>[] _deferred;

    CommandBuffer _cmd;
    Pipeline _boundPipeline;
    TextureHandle _boundTexture;

    public Renderer(VulkanContext ctx)
    {
        Ctx = ctx;
        Pipelines = new Pipelines(ctx);
        for (int i = 0; i < VulkanContext.FramesInFlight; i++)
        {
            _ubos[i] = new GpuBuffer(ctx, (ulong)sizeof(GlobalUbo), BufferUsageFlags.UniformBufferBit);
            _hudBuffers[i] = new GpuBuffer(ctx, HudBufferBytes, BufferUsageFlags.VertexBufferBit);
        }
        _deferred = new List<IDisposable>[VulkanContext.FramesInFlight];
        for (int i = 0; i < _deferred.Length; i++) _deferred[i] = new List<IDisposable>();
        CreateDescriptorPool();
    }

    void CreateDescriptorPool()
    {
        var sizes = stackalloc DescriptorPoolSize[2];
        sizes[0] = new DescriptorPoolSize(DescriptorType.UniformBuffer, 32);
        sizes[1] = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 32);
        var createInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 2,
            PPoolSizes = sizes,
            MaxSets = 32,
        };
        VulkanContext.Check(Ctx.Vk.CreateDescriptorPool(Ctx.Device, in createInfo, null, out _descriptorPool), "CreateDescriptorPool");
    }

    /// Allocates per-frame descriptor sets binding the shared UBO and this texture.
    public TextureHandle RegisterTexture(GpuTexture texture)
    {
        var handle = new TextureHandle { Texture = texture, Sets = new DescriptorSet[VulkanContext.FramesInFlight] };
        var layouts = stackalloc DescriptorSetLayout[VulkanContext.FramesInFlight];
        for (int i = 0; i < VulkanContext.FramesInFlight; i++) layouts[i] = Pipelines.SetLayout;
        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = VulkanContext.FramesInFlight,
            PSetLayouts = layouts,
        };
        fixed (DescriptorSet* sets = handle.Sets)
            VulkanContext.Check(Ctx.Vk.AllocateDescriptorSets(Ctx.Device, in allocInfo, sets), "AllocateDescriptorSets");

        for (int i = 0; i < VulkanContext.FramesInFlight; i++)
        {
            var bufferInfo = new DescriptorBufferInfo(_ubos[i].Buffer, 0, (ulong)sizeof(GlobalUbo));
            var imageInfo = new DescriptorImageInfo(texture.Sampler, texture.View, ImageLayout.ShaderReadOnlyOptimal);
            var writes = stackalloc WriteDescriptorSet[2];
            writes[0] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = handle.Sets[i],
                DstBinding = 0,
                DescriptorType = DescriptorType.UniformBuffer,
                DescriptorCount = 1,
                PBufferInfo = &bufferInfo,
            };
            writes[1] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = handle.Sets[i],
                DstBinding = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                PImageInfo = &imageInfo,
            };
            Ctx.Vk.UpdateDescriptorSets(Ctx.Device, 2, writes, 0, null);
        }
        _textures.Add(handle);
        return handle;
    }

    /// Queue a GPU resource for disposal once this frame slot's fence has cycled.
    public void DeferDispose(IDisposable resource) => _deferred[Ctx.CurrentFrame].Add(resource);

    public bool BeginFrame(in GlobalUbo ubo, out CommandBuffer cmd)
    {
        if (!Ctx.BeginFrame(ubo.FogColor, out cmd)) return false;
        _cmd = cmd;

        // this slot's fence has been waited on — its deferred resources are safe to free
        foreach (var d in _deferred[Ctx.CurrentFrame]) d.Dispose();
        _deferred[Ctx.CurrentFrame].Clear();

        var data = ubo;
        _ubos[Ctx.CurrentFrame].Write(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
        _boundPipeline = default;
        _boundTexture = null;
        return true;
    }

    public void EndFrame() => Ctx.EndFrame();

    void BindPipeline(Pipeline pipeline)
    {
        if (_boundPipeline.Handle == pipeline.Handle) return;
        Ctx.Vk.CmdBindPipeline(_cmd, PipelineBindPoint.Graphics, pipeline);
        _boundPipeline = pipeline;
        _boundTexture = null; // sets are layout-compatible, but rebind for clarity
    }

    void BindTexture(TextureHandle texture, PipelineLayout layout)
    {
        if (_boundTexture == texture) return;
        var set = texture.Sets[Ctx.CurrentFrame];
        Ctx.Vk.CmdBindDescriptorSets(_cmd, PipelineBindPoint.Graphics, layout, 0, 1, in set, 0, null);
        _boundTexture = texture;
    }

    /// Global brightness for meshes whose light isn't baked per vertex
    /// (animals, boats, drops, particles). The frame loop sets it from the
    /// time-of-day daylight so entities dim at night with the world.
    public float EntityLight = 1f;

    public void DrawMesh(Mesh mesh, in Matrix4x4 model, Vector4 tint, bool textured, TextureHandle texture, bool lightmap = false)
    {
        BindPipeline(Pipelines.World);
        BindTexture(texture, Pipelines.WorldLayout);
        DrawMeshInner(mesh, model, tint, textured, lightmap);
    }

    public void DrawLineMesh(Mesh mesh, in Matrix4x4 model, Vector4 tint, TextureHandle texture)
    {
        BindPipeline(Pipelines.Lines);
        BindTexture(texture, Pipelines.WorldLayout);
        DrawMeshInner(mesh, model, tint, textured: false, lightmap: false);
    }

    void DrawMeshInner(Mesh mesh, in Matrix4x4 model, Vector4 tint, bool textured, bool lightmap)
    {
        // lightmapped meshes get daylight in the shader; plain meshes get it here
        if (!lightmap) tint = new Vector4(tint.X * EntityLight, tint.Y * EntityLight, tint.Z * EntityLight, tint.W);
        var push = new WorldPush { Model = model, Tint = tint, Params = new Vector4(textured ? 1 : 0, lightmap ? 1 : 0, 0, 0) };
        Ctx.Vk.CmdPushConstants(_cmd, Pipelines.WorldLayout,
            ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, Pipelines.WorldPushSize, &push);
        var vertexBuffer = mesh.Vertices.Buffer;
        ulong offset = 0;
        Ctx.Vk.CmdBindVertexBuffers(_cmd, 0, 1, in vertexBuffer, in offset);
        Ctx.Vk.CmdBindIndexBuffer(_cmd, mesh.Indices.Buffer, 0, IndexType.Uint32);
        Ctx.Vk.CmdDrawIndexed(_cmd, mesh.IndexCount, 1, 0, 0, 0);
    }

    /// Uploads the accumulated HUD geometry and draws it range by range.
    public void DrawHud(HudBatcher batcher, float screenW, float screenH)
    {
        if (batcher.VertexFloats == 0) return;
        BindPipeline(Pipelines.Hud);

        var screen = new Vector4(screenW, screenH, 0, 0);
        Ctx.Vk.CmdPushConstants(_cmd, Pipelines.HudLayout, ShaderStageFlags.VertexBit, 0, Pipelines.HudPushSize, &screen);

        var buffer = _hudBuffers[Ctx.CurrentFrame];
        int bytes = Math.Min(batcher.VertexFloats * sizeof(float), HudBufferBytes);
        buffer.Write<float>(batcher.Vertices.AsSpan(0, bytes / sizeof(float)));

        var vertexBuffer = buffer.Buffer;
        ulong offset = 0;
        Ctx.Vk.CmdBindVertexBuffers(_cmd, 0, 1, in vertexBuffer, in offset);
        foreach (var range in batcher.Ranges)
        {
            BindTexture(range.Texture, Pipelines.HudLayout);
            Ctx.Vk.CmdDraw(_cmd, (uint)range.VertexCount, 1, (uint)range.FirstVertex, 0);
        }
    }

    public void Dispose()
    {
        Ctx.Vk.DeviceWaitIdle(Ctx.Device);
        foreach (var list in _deferred)
        {
            foreach (var d in list) d.Dispose();
            list.Clear();
        }
        foreach (var ubo in _ubos) ubo.Dispose();
        foreach (var buf in _hudBuffers) buf.Dispose();
        Ctx.Vk.DestroyDescriptorPool(Ctx.Device, _descriptorPool, null);
        Pipelines.Dispose();
    }
}
