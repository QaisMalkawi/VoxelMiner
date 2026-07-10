using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Windowing;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace VoxelMiner.Engine;

/// Core Vulkan boilerplate: instance, device, swapchain, render pass,
/// per-frame command buffers and synchronization, plus small resource helpers.
public unsafe sealed class VulkanContext : IDisposable
{
    public const int FramesInFlight = 2;

    public Vk Vk;
    public Instance Instance;
    public PhysicalDevice Physical;
    public Device Device;
    public Queue Queue;
    public uint QueueFamily;
    public KhrSurface KhrSurface;
    public KhrSwapchain KhrSwapchain;
    public SurfaceKHR Surface;

    public SwapchainKHR Swapchain;
    public Image[] SwapImages;
    public ImageView[] SwapViews;
    public Format SwapFormat;
    public Extent2D SwapExtent;
    public RenderPass RenderPass;
    public Framebuffer[] Framebuffers;

    Image _depthImage;
    DeviceMemory _depthMemory;
    ImageView _depthView;

    public CommandPool CommandPool;
    public CommandBuffer[] CommandBuffers;
    VkSemaphore[] _imageAvailable;
    VkSemaphore[] _renderFinished;
    Fence[] _inFlight;

    public int CurrentFrame;
    public uint ImageIndex;
    public bool FramebufferResized;

    readonly IWindow _window;

    public VulkanContext(IWindow window)
    {
        _window = window;
        Vk = Vk.GetApi();
        CreateInstance();
        CreateSurface();
        PickPhysicalDevice();
        CreateDevice();
        CreateSwapchain();
        CreateRenderPass();
        CreateDepthResources();
        CreateFramebuffers();
        CreateCommandResources();
        CreateSyncObjects();
    }

    // ------------------------------------------------------------- setup

    void CreateInstance()
    {
        var appName = (byte*)SilkMarshal.StringToPtr("Voxel Miner");
        var engineName = (byte*)SilkMarshal.StringToPtr("VoxelMinerEngine");
        var appInfo = new ApplicationInfo
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = appName,
            ApplicationVersion = new Version32(1, 0, 0),
            PEngineName = engineName,
            EngineVersion = new Version32(1, 0, 0),
            ApiVersion = Vk.Version12,
        };
        var extensions = _window.VkSurface!.GetRequiredExtensions(out uint extensionCount);
        var createInfo = new InstanceCreateInfo
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &appInfo,
            EnabledExtensionCount = extensionCount,
            PpEnabledExtensionNames = extensions,
        };
        Check(Vk.CreateInstance(in createInfo, null, out Instance), "CreateInstance");
        SilkMarshal.Free((nint)appName);
        SilkMarshal.Free((nint)engineName);

        if (!Vk.TryGetInstanceExtension(Instance, out KhrSurface))
            throw new InvalidOperationException("VK_KHR_surface not available");
    }

    void CreateSurface()
    {
        Surface = _window.VkSurface!.Create<AllocationCallbacks>(Instance.ToHandle(), null).ToSurface();
    }

    void PickPhysicalDevice()
    {
        uint count = 0;
        Vk.EnumeratePhysicalDevices(Instance, ref count, null);
        if (count == 0) throw new InvalidOperationException("No Vulkan-capable GPU found");
        var devices = new PhysicalDevice[count];
        fixed (PhysicalDevice* p = devices) Vk.EnumeratePhysicalDevices(Instance, ref count, p);

        foreach (var dev in devices)
        {
            uint? family = FindQueueFamily(dev);
            if (family != null)
            {
                Physical = dev;
                QueueFamily = family.Value;
                return;
            }
        }
        throw new InvalidOperationException("No suitable GPU queue family found");
    }

    uint? FindQueueFamily(PhysicalDevice dev)
    {
        uint count = 0;
        Vk.GetPhysicalDeviceQueueFamilyProperties(dev, ref count, null);
        var props = new QueueFamilyProperties[count];
        fixed (QueueFamilyProperties* p = props) Vk.GetPhysicalDeviceQueueFamilyProperties(dev, ref count, p);
        for (uint i = 0; i < count; i++)
        {
            if ((props[i].QueueFlags & QueueFlags.GraphicsBit) == 0) continue;
            KhrSurface.GetPhysicalDeviceSurfaceSupport(dev, i, Surface, out var supported);
            if (supported) return i;
        }
        return null;
    }

    void CreateDevice()
    {
        float priority = 1f;
        var queueInfo = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = QueueFamily,
            QueueCount = 1,
            PQueuePriorities = &priority,
        };
        var extName = (byte*)SilkMarshal.StringToPtr(KhrSwapchain.ExtensionName);
        var extNames = stackalloc byte*[1];
        extNames[0] = extName;
        var features = new PhysicalDeviceFeatures();
        var createInfo = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo,
            QueueCreateInfoCount = 1,
            PQueueCreateInfos = &queueInfo,
            EnabledExtensionCount = 1,
            PpEnabledExtensionNames = extNames,
            PEnabledFeatures = &features,
        };
        Check(Vk.CreateDevice(Physical, in createInfo, null, out Device), "CreateDevice");
        SilkMarshal.Free((nint)extName);
        Vk.GetDeviceQueue(Device, QueueFamily, 0, out Queue);

        if (!Vk.TryGetDeviceExtension(Instance, Device, out KhrSwapchain))
            throw new InvalidOperationException("VK_KHR_swapchain not available");
    }

    void CreateSwapchain()
    {
        KhrSurface.GetPhysicalDeviceSurfaceCapabilities(Physical, Surface, out var caps);

        uint formatCount = 0;
        KhrSurface.GetPhysicalDeviceSurfaceFormats(Physical, Surface, ref formatCount, null);
        var formats = new SurfaceFormatKHR[formatCount];
        fixed (SurfaceFormatKHR* p = formats) KhrSurface.GetPhysicalDeviceSurfaceFormats(Physical, Surface, ref formatCount, p);
        var chosen = formats[0];
        foreach (var f in formats)
        {
            if (f.Format == Format.B8G8R8A8Srgb && f.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
            {
                chosen = f;
                break;
            }
        }
        SwapFormat = chosen.Format;

        SwapExtent = caps.CurrentExtent.Width != uint.MaxValue
            ? caps.CurrentExtent
            : new Extent2D(
                Math.Clamp((uint)_window.FramebufferSize.X, caps.MinImageExtent.Width, caps.MaxImageExtent.Width),
                Math.Clamp((uint)_window.FramebufferSize.Y, caps.MinImageExtent.Height, caps.MaxImageExtent.Height));

        uint imageCount = caps.MinImageCount + 1;
        if (caps.MaxImageCount > 0 && imageCount > caps.MaxImageCount) imageCount = caps.MaxImageCount;

        var usage = ImageUsageFlags.ColorAttachmentBit;
        if ((caps.SupportedUsageFlags & ImageUsageFlags.TransferSrcBit) != 0)
            usage |= ImageUsageFlags.TransferSrcBit; // for screenshots

        var createInfo = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = Surface,
            MinImageCount = imageCount,
            ImageFormat = chosen.Format,
            ImageColorSpace = chosen.ColorSpace,
            ImageExtent = SwapExtent,
            ImageArrayLayers = 1,
            ImageUsage = usage,
            ImageSharingMode = SharingMode.Exclusive,
            PreTransform = caps.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = PresentModeKHR.FifoKhr,
            Clipped = true,
        };
        Check(KhrSwapchain.CreateSwapchain(Device, in createInfo, null, out Swapchain), "CreateSwapchain");

        uint imgCount = 0;
        KhrSwapchain.GetSwapchainImages(Device, Swapchain, ref imgCount, null);
        SwapImages = new Image[imgCount];
        fixed (Image* p = SwapImages) KhrSwapchain.GetSwapchainImages(Device, Swapchain, ref imgCount, p);

        SwapViews = new ImageView[imgCount];
        for (int i = 0; i < imgCount; i++)
            SwapViews[i] = CreateImageView(SwapImages[i], SwapFormat, ImageAspectFlags.ColorBit);
    }

    void CreateRenderPass()
    {
        var attachments = stackalloc AttachmentDescription[2];
        attachments[0] = new AttachmentDescription
        {
            Format = SwapFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.PresentSrcKhr,
        };
        attachments[1] = new AttachmentDescription
        {
            Format = Format.D32Sfloat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.DontCare,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.DepthStencilAttachmentOptimal,
        };
        var colorRef = new AttachmentReference(0, ImageLayout.ColorAttachmentOptimal);
        var depthRef = new AttachmentReference(1, ImageLayout.DepthStencilAttachmentOptimal);
        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorRef,
            PDepthStencilAttachment = &depthRef,
        };
        var dependency = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
            SrcAccessMask = 0,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit,
        };
        var createInfo = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 2,
            PAttachments = attachments,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 1,
            PDependencies = &dependency,
        };
        Check(Vk.CreateRenderPass(Device, in createInfo, null, out RenderPass), "CreateRenderPass");
    }

    void CreateDepthResources()
    {
        CreateImage(SwapExtent.Width, SwapExtent.Height, Format.D32Sfloat,
            ImageUsageFlags.DepthStencilAttachmentBit, out _depthImage, out _depthMemory);
        _depthView = CreateImageView(_depthImage, Format.D32Sfloat, ImageAspectFlags.DepthBit);
    }

    void CreateFramebuffers()
    {
        Framebuffers = new Framebuffer[SwapViews.Length];
        for (int i = 0; i < SwapViews.Length; i++)
        {
            var views = stackalloc ImageView[2] { SwapViews[i], _depthView };
            var createInfo = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = RenderPass,
                AttachmentCount = 2,
                PAttachments = views,
                Width = SwapExtent.Width,
                Height = SwapExtent.Height,
                Layers = 1,
            };
            Check(Vk.CreateFramebuffer(Device, in createInfo, null, out Framebuffers[i]), "CreateFramebuffer");
        }
    }

    void CreateCommandResources()
    {
        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            QueueFamilyIndex = QueueFamily,
        };
        Check(Vk.CreateCommandPool(Device, in poolInfo, null, out CommandPool), "CreateCommandPool");

        CommandBuffers = new CommandBuffer[FramesInFlight];
        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = CommandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = FramesInFlight,
        };
        fixed (CommandBuffer* p = CommandBuffers)
            Check(Vk.AllocateCommandBuffers(Device, in allocInfo, p), "AllocateCommandBuffers");
    }

    void CreateSyncObjects()
    {
        _imageAvailable = new VkSemaphore[FramesInFlight];
        _renderFinished = new VkSemaphore[FramesInFlight];
        _inFlight = new Fence[FramesInFlight];
        var semInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo, Flags = FenceCreateFlags.SignaledBit };
        for (int i = 0; i < FramesInFlight; i++)
        {
            Check(Vk.CreateSemaphore(Device, in semInfo, null, out _imageAvailable[i]), "CreateSemaphore");
            Check(Vk.CreateSemaphore(Device, in semInfo, null, out _renderFinished[i]), "CreateSemaphore");
            Check(Vk.CreateFence(Device, in fenceInfo, null, out _inFlight[i]), "CreateFence");
        }
    }

    // ------------------------------------------------------------- frame lifecycle

    /// Waits for the frame slot, acquires an image, begins the command buffer
    /// and render pass. Returns false when the frame must be skipped (resize).
    public bool BeginFrame(System.Numerics.Vector4 clearColor, out CommandBuffer cmd)
    {
        cmd = default;
        if (SwapExtent.Width == 0 || SwapExtent.Height == 0)
        {
            RecreateSwapchain();
            return false;
        }
        Vk.WaitForFences(Device, 1, in _inFlight[CurrentFrame], true, ulong.MaxValue);

        uint imageIndex = 0;
        var result = KhrSwapchain.AcquireNextImage(Device, Swapchain, ulong.MaxValue, _imageAvailable[CurrentFrame], default, ref imageIndex);
        if (result == Result.ErrorOutOfDateKhr)
        {
            RecreateSwapchain();
            return false;
        }
        if (result != Result.Success && result != Result.SuboptimalKhr) throw new InvalidOperationException($"AcquireNextImage: {result}");
        ImageIndex = imageIndex;

        Vk.ResetFences(Device, 1, in _inFlight[CurrentFrame]);
        cmd = CommandBuffers[CurrentFrame];
        Vk.ResetCommandBuffer(cmd, 0);

        var beginInfo = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo };
        Check(Vk.BeginCommandBuffer(cmd, in beginInfo), "BeginCommandBuffer");

        var clears = stackalloc ClearValue[2];
        clears[0].Color = new ClearColorValue(clearColor.X, clearColor.Y, clearColor.Z, clearColor.W);
        clears[1].DepthStencil = new ClearDepthStencilValue(1f, 0);
        var rpInfo = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = RenderPass,
            Framebuffer = Framebuffers[ImageIndex],
            RenderArea = new Rect2D(new Offset2D(0, 0), SwapExtent),
            ClearValueCount = 2,
            PClearValues = clears,
        };
        Vk.CmdBeginRenderPass(cmd, in rpInfo, SubpassContents.Inline);

        var viewport = new Viewport(0, 0, SwapExtent.Width, SwapExtent.Height, 0, 1);
        var scissor = new Rect2D(new Offset2D(0, 0), SwapExtent);
        Vk.CmdSetViewport(cmd, 0, 1, in viewport);
        Vk.CmdSetScissor(cmd, 0, 1, in scissor);
        return true;
    }

    public void EndFrame()
    {
        var cmd = CommandBuffers[CurrentFrame];
        Vk.CmdEndRenderPass(cmd);
        Check(Vk.EndCommandBuffer(cmd), "EndCommandBuffer");

        var waitStage = PipelineStageFlags.ColorAttachmentOutputBit;
        var imageAvailable = _imageAvailable[CurrentFrame];
        var renderFinished = _renderFinished[CurrentFrame];
        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &imageAvailable,
            PWaitDstStageMask = &waitStage,
            CommandBufferCount = 1,
            PCommandBuffers = &cmd,
            SignalSemaphoreCount = 1,
            PSignalSemaphores = &renderFinished,
        };
        Check(Vk.QueueSubmit(Queue, 1, in submitInfo, _inFlight[CurrentFrame]), "QueueSubmit");

        var swapchain = Swapchain;
        var imageIndex = ImageIndex;
        var presentInfo = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &renderFinished,
            SwapchainCount = 1,
            PSwapchains = &swapchain,
            PImageIndices = &imageIndex,
        };
        var result = KhrSwapchain.QueuePresent(Queue, in presentInfo);
        if (result == Result.ErrorOutOfDateKhr || result == Result.SuboptimalKhr || FramebufferResized)
        {
            FramebufferResized = false;
            RecreateSwapchain();
        }
        else if (result != Result.Success)
        {
            throw new InvalidOperationException($"QueuePresent: {result}");
        }
        CurrentFrame = (CurrentFrame + 1) % FramesInFlight;
    }

    void RecreateSwapchain()
    {
        Vk.DeviceWaitIdle(Device);
        foreach (var fb in Framebuffers) Vk.DestroyFramebuffer(Device, fb, null);
        Vk.DestroyImageView(Device, _depthView, null);
        Vk.DestroyImage(Device, _depthImage, null);
        Vk.FreeMemory(Device, _depthMemory, null);
        foreach (var view in SwapViews) Vk.DestroyImageView(Device, view, null);
        KhrSwapchain.DestroySwapchain(Device, Swapchain, null);

        CreateSwapchain();
        CreateDepthResources();
        CreateFramebuffers();
    }

    // ------------------------------------------------------------- resource helpers

    public uint FindMemoryType(uint typeBits, MemoryPropertyFlags props)
    {
        Vk.GetPhysicalDeviceMemoryProperties(Physical, out var memProps);
        for (int i = 0; i < memProps.MemoryTypeCount; i++)
        {
            if ((typeBits & (1u << i)) != 0 && (memProps.MemoryTypes[i].PropertyFlags & props) == props)
                return (uint)i;
        }
        throw new InvalidOperationException("No suitable memory type");
    }

    public void CreateBuffer(ulong size, BufferUsageFlags usage, MemoryPropertyFlags props,
        out VkBuffer buffer, out DeviceMemory memory)
    {
        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
        };
        Check(Vk.CreateBuffer(Device, in bufferInfo, null, out buffer), "CreateBuffer");
        Vk.GetBufferMemoryRequirements(Device, buffer, out var req);
        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = req.Size,
            MemoryTypeIndex = FindMemoryType(req.MemoryTypeBits, props),
        };
        Check(Vk.AllocateMemory(Device, in allocInfo, null, out memory), "AllocateMemory");
        Vk.BindBufferMemory(Device, buffer, memory, 0);
    }

    public void CreateImage(uint width, uint height, Format format, ImageUsageFlags usage,
        out Image image, out DeviceMemory memory)
    {
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Extent = new Extent3D(width, height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Format = format,
            Tiling = ImageTiling.Optimal,
            InitialLayout = ImageLayout.Undefined,
            Usage = usage,
            Samples = SampleCountFlags.Count1Bit,
            SharingMode = SharingMode.Exclusive,
        };
        Check(Vk.CreateImage(Device, in imageInfo, null, out image), "CreateImage");
        Vk.GetImageMemoryRequirements(Device, image, out var req);
        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = req.Size,
            MemoryTypeIndex = FindMemoryType(req.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };
        Check(Vk.AllocateMemory(Device, in allocInfo, null, out memory), "AllocateMemory");
        Vk.BindImageMemory(Device, image, memory, 0);
    }

    public ImageView CreateImageView(Image image, Format format, ImageAspectFlags aspect)
    {
        var createInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange(aspect, 0, 1, 0, 1),
        };
        Check(Vk.CreateImageView(Device, in createInfo, null, out var view), "CreateImageView");
        return view;
    }

    public CommandBuffer BeginOneTimeCommands()
    {
        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = CommandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        Vk.AllocateCommandBuffers(Device, in allocInfo, out var cmd);
        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        Vk.BeginCommandBuffer(cmd, in beginInfo);
        return cmd;
    }

    public void EndOneTimeCommands(CommandBuffer cmd)
    {
        Vk.EndCommandBuffer(cmd);
        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &cmd,
        };
        Vk.QueueSubmit(Queue, 1, in submitInfo, default);
        Vk.QueueWaitIdle(Queue);
        Vk.FreeCommandBuffers(Device, CommandPool, 1, in cmd);
    }

    public void TransitionImageLayout(CommandBuffer cmd, Image image, ImageLayout from, ImageLayout to,
        PipelineStageFlags srcStage, PipelineStageFlags dstStage, AccessFlags srcAccess, AccessFlags dstAccess)
    {
        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = from,
            NewLayout = to,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            SrcAccessMask = srcAccess,
            DstAccessMask = dstAccess,
        };
        Vk.CmdPipelineBarrier(cmd, srcStage, dstStage, 0, 0, null, 0, null, 1, in barrier);
    }

    /// Copies the most recently presented swapchain image to a PNG on disk.
    public void SaveScreenshot(string path)
    {
        Vk.DeviceWaitIdle(Device);
        var image = SwapImages[ImageIndex];
        uint w = SwapExtent.Width, h = SwapExtent.Height;
        ulong size = (ulong)(w * h * 4);
        CreateBuffer(size, BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out var buffer, out var memory);

        var cmd = BeginOneTimeCommands();
        TransitionImageLayout(cmd, image, ImageLayout.PresentSrcKhr, ImageLayout.TransferSrcOptimal,
            PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit, AccessFlags.MemoryReadBit, AccessFlags.TransferReadBit);
        var region = new BufferImageCopy
        {
            BufferOffset = 0,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageOffset = new Offset3D(0, 0, 0),
            ImageExtent = new Extent3D(w, h, 1),
        };
        Vk.CmdCopyImageToBuffer(cmd, image, ImageLayout.TransferSrcOptimal, buffer, 1, in region);
        TransitionImageLayout(cmd, image, ImageLayout.TransferSrcOptimal, ImageLayout.PresentSrcKhr,
            PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit, AccessFlags.TransferReadBit, AccessFlags.MemoryReadBit);
        EndOneTimeCommands(cmd);

        void* mapped;
        Vk.MapMemory(Device, memory, 0, size, 0, &mapped);
        var pixels = new byte[size];
        Marshal.Copy((IntPtr)mapped, pixels, 0, (int)size);
        Vk.UnmapMemory(Device, memory);
        Vk.DestroyBuffer(Device, buffer, null);
        Vk.FreeMemory(Device, memory, null);

        // Swapchain format is BGRA, which matches Format32bppRgb byte order.
        using var bmp = new System.Drawing.Bitmap((int)w, (int)h, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, (int)w, (int)h),
            System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        for (int y = 0; y < h; y++)
            Marshal.Copy(pixels, y * (int)w * 4, data.Scan0 + y * data.Stride, (int)w * 4);
        bmp.UnlockBits(data);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    public static void Check(Result result, string what)
    {
        if (result != Result.Success) throw new InvalidOperationException($"{what} failed: {result}");
    }

    public void Dispose()
    {
        Vk.DeviceWaitIdle(Device);
        for (int i = 0; i < FramesInFlight; i++)
        {
            Vk.DestroySemaphore(Device, _imageAvailable[i], null);
            Vk.DestroySemaphore(Device, _renderFinished[i], null);
            Vk.DestroyFence(Device, _inFlight[i], null);
        }
        Vk.DestroyCommandPool(Device, CommandPool, null);
        foreach (var fb in Framebuffers) Vk.DestroyFramebuffer(Device, fb, null);
        Vk.DestroyImageView(Device, _depthView, null);
        Vk.DestroyImage(Device, _depthImage, null);
        Vk.FreeMemory(Device, _depthMemory, null);
        Vk.DestroyRenderPass(Device, RenderPass, null);
        foreach (var view in SwapViews) Vk.DestroyImageView(Device, view, null);
        KhrSwapchain.DestroySwapchain(Device, Swapchain, null);
        Vk.DestroyDevice(Device, null);
        KhrSurface.DestroySurface(Instance, Surface, null);
        Vk.DestroyInstance(Instance, null);
    }
}
