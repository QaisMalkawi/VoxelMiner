using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace VoxelMiner.Engine;

/// Host-visible, persistently mapped buffer.
public unsafe sealed class GpuBuffer : IDisposable
{
    readonly VulkanContext _ctx;
    public VkBuffer Buffer;
    public DeviceMemory Memory;
    public void* Mapped;
    public ulong Size;

    public GpuBuffer(VulkanContext ctx, ulong size, BufferUsageFlags usage)
    {
        _ctx = ctx;
        Size = size;
        ctx.CreateBuffer(size, usage,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out Buffer, out Memory);
        void* mapped;
        ctx.Vk.MapMemory(ctx.Device, Memory, 0, size, 0, &mapped);
        Mapped = mapped;
    }

    public void Write<T>(ReadOnlySpan<T> data, ulong byteOffset = 0) where T : unmanaged
    {
        fixed (T* src = data)
        {
            System.Buffer.MemoryCopy(src, (byte*)Mapped + byteOffset,
                Size - byteOffset, (ulong)(data.Length * sizeof(T)));
        }
    }

    public void Dispose()
    {
        _ctx.Vk.UnmapMemory(_ctx.Device, Memory);
        _ctx.Vk.DestroyBuffer(_ctx.Device, Buffer, null);
        _ctx.Vk.FreeMemory(_ctx.Device, Memory, null);
    }
}

/// Indexed triangle/line mesh.
public sealed class Mesh : IDisposable
{
    public GpuBuffer Vertices;
    public GpuBuffer Indices;
    public uint IndexCount;

    public static Mesh Create(VulkanContext ctx, float[] vertices, uint[] indices)
    {
        var mesh = new Mesh
        {
            Vertices = new GpuBuffer(ctx, (ulong)(vertices.Length * sizeof(float)), BufferUsageFlags.VertexBufferBit),
            Indices = new GpuBuffer(ctx, (ulong)(indices.Length * sizeof(uint)), BufferUsageFlags.IndexBufferBit),
            IndexCount = (uint)indices.Length,
        };
        mesh.Vertices.Write<float>(vertices);
        mesh.Indices.Write<uint>(indices);
        return mesh;
    }

    public void Dispose()
    {
        Vertices.Dispose();
        Indices.Dispose();
    }
}

/// Sampled 2D texture uploaded from RGBA pixels.
public unsafe sealed class GpuTexture : IDisposable
{
    readonly VulkanContext _ctx;
    public Image Image;
    public DeviceMemory Memory;
    public ImageView View;
    public Sampler Sampler;
    public int Width, Height;

    public GpuTexture(VulkanContext ctx, byte[] rgba, int width, int height, bool srgb = true)
    {
        _ctx = ctx;
        Width = width;
        Height = height;
        var format = srgb ? Format.R8G8B8A8Srgb : Format.R8G8B8A8Unorm;

        ulong size = (ulong)rgba.Length;
        ctx.CreateBuffer(size, BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out var staging, out var stagingMemory);
        void* mapped;
        ctx.Vk.MapMemory(ctx.Device, stagingMemory, 0, size, 0, &mapped);
        Marshal.Copy(rgba, 0, (IntPtr)mapped, rgba.Length);
        ctx.Vk.UnmapMemory(ctx.Device, stagingMemory);

        ctx.CreateImage((uint)width, (uint)height, format,
            ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit, out Image, out Memory);

        var cmd = ctx.BeginOneTimeCommands();
        ctx.TransitionImageLayout(cmd, Image, ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
            PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit, 0, AccessFlags.TransferWriteBit);
        var region = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Extent3D((uint)width, (uint)height, 1),
        };
        ctx.Vk.CmdCopyBufferToImage(cmd, staging, Image, ImageLayout.TransferDstOptimal, 1, in region);
        ctx.TransitionImageLayout(cmd, Image, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit,
            AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit);
        ctx.EndOneTimeCommands(cmd);

        ctx.Vk.DestroyBuffer(ctx.Device, staging, null);
        ctx.Vk.FreeMemory(ctx.Device, stagingMemory, null);

        View = ctx.CreateImageView(Image, format, ImageAspectFlags.ColorBit);
        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Nearest,
            MinFilter = Filter.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MipmapMode = SamplerMipmapMode.Nearest,
        };
        VulkanContext.Check(ctx.Vk.CreateSampler(ctx.Device, in samplerInfo, null, out Sampler), "CreateSampler");
    }

    /// Re-uploads the texture contents (e.g. the 2D world map). Waits for the
    /// device to go idle first so no in-flight frame still samples the image
    /// mid-copy — fine for rare, user-triggered updates.
    public void Update(byte[] rgba)
    {
        _ctx.Vk.DeviceWaitIdle(_ctx.Device);

        ulong size = (ulong)rgba.Length;
        _ctx.CreateBuffer(size, BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out var staging, out var stagingMemory);
        void* mapped;
        _ctx.Vk.MapMemory(_ctx.Device, stagingMemory, 0, size, 0, &mapped);
        Marshal.Copy(rgba, 0, (IntPtr)mapped, rgba.Length);
        _ctx.Vk.UnmapMemory(_ctx.Device, stagingMemory);

        var cmd = _ctx.BeginOneTimeCommands();
        _ctx.TransitionImageLayout(cmd, Image, ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferDstOptimal,
            PipelineStageFlags.FragmentShaderBit, PipelineStageFlags.TransferBit,
            AccessFlags.ShaderReadBit, AccessFlags.TransferWriteBit);
        var region = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Extent3D((uint)Width, (uint)Height, 1),
        };
        _ctx.Vk.CmdCopyBufferToImage(cmd, staging, Image, ImageLayout.TransferDstOptimal, 1, in region);
        _ctx.TransitionImageLayout(cmd, Image, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit,
            AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit);
        _ctx.EndOneTimeCommands(cmd);

        _ctx.Vk.DestroyBuffer(_ctx.Device, staging, null);
        _ctx.Vk.FreeMemory(_ctx.Device, stagingMemory, null);
    }

    public void Dispose()
    {
        _ctx.Vk.DestroySampler(_ctx.Device, Sampler, null);
        _ctx.Vk.DestroyImageView(_ctx.Device, View, null);
        _ctx.Vk.DestroyImage(_ctx.Device, Image, null);
        _ctx.Vk.FreeMemory(_ctx.Device, Memory, null);
    }
}
