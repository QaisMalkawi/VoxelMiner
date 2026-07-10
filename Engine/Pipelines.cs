using Silk.NET.Core.Native;
using Silk.NET.Shaderc;
using Silk.NET.Vulkan;

namespace VoxelMiner.Engine;

/// Descriptor set layout, pipeline layouts, and the three graphics pipelines:
/// world (triangles), lines (block highlight), and HUD (2D overlay).
public unsafe sealed class Pipelines : IDisposable
{
    public const uint WorldPushSize = 96; // mat4 model + vec4 tint + vec4 params
    public const uint HudPushSize = 16;   // vec4 screen

    readonly VulkanContext _ctx;
    public DescriptorSetLayout SetLayout;
    public PipelineLayout WorldLayout;
    public PipelineLayout HudLayout;
    public Pipeline World;
    public Pipeline Lines;
    public Pipeline Hud;

    ShaderModule _worldVert, _worldFrag, _hudVert, _hudFrag;

    public Pipelines(VulkanContext ctx)
    {
        _ctx = ctx;
        CreateSetLayout();
        CreatePipelineLayouts();
        _worldVert = CreateModule(Shaders.Compile(Shaders.WorldVert, ShaderKind.VertexShader));
        _worldFrag = CreateModule(Shaders.Compile(Shaders.WorldFrag, ShaderKind.FragmentShader));
        _hudVert = CreateModule(Shaders.Compile(Shaders.HudVert, ShaderKind.VertexShader));
        _hudFrag = CreateModule(Shaders.Compile(Shaders.HudFrag, ShaderKind.FragmentShader));

        World = CreatePipeline(_worldVert, _worldFrag, WorldLayout, PrimitiveTopology.TriangleList, depth: true, worldVertexFormat: true);
        Lines = CreatePipeline(_worldVert, _worldFrag, WorldLayout, PrimitiveTopology.LineList, depth: true, worldVertexFormat: true);
        Hud = CreatePipeline(_hudVert, _hudFrag, HudLayout, PrimitiveTopology.TriangleList, depth: false, worldVertexFormat: false);
    }

    void CreateSetLayout()
    {
        var bindings = stackalloc DescriptorSetLayoutBinding[2];
        bindings[0] = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.UniformBuffer,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
        };
        bindings[1] = new DescriptorSetLayoutBinding
        {
            Binding = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
        };
        var createInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 2,
            PBindings = bindings,
        };
        VulkanContext.Check(_ctx.Vk.CreateDescriptorSetLayout(_ctx.Device, in createInfo, null, out SetLayout), "CreateDescriptorSetLayout");
    }

    void CreatePipelineLayouts()
    {
        var setLayout = SetLayout;

        var worldPush = new PushConstantRange(ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, WorldPushSize);
        var worldInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &setLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &worldPush,
        };
        VulkanContext.Check(_ctx.Vk.CreatePipelineLayout(_ctx.Device, in worldInfo, null, out WorldLayout), "CreatePipelineLayout");

        var hudPush = new PushConstantRange(ShaderStageFlags.VertexBit, 0, HudPushSize);
        var hudInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &setLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &hudPush,
        };
        VulkanContext.Check(_ctx.Vk.CreatePipelineLayout(_ctx.Device, in hudInfo, null, out HudLayout), "CreatePipelineLayout");
    }

    ShaderModule CreateModule(byte[] spirv)
    {
        fixed (byte* code = spirv)
        {
            var createInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)spirv.Length,
                PCode = (uint*)code,
            };
            VulkanContext.Check(_ctx.Vk.CreateShaderModule(_ctx.Device, in createInfo, null, out var module), "CreateShaderModule");
            return module;
        }
    }

    Pipeline CreatePipeline(ShaderModule vert, ShaderModule frag, PipelineLayout layout,
        PrimitiveTopology topology, bool depth, bool worldVertexFormat)
    {
        var entry = (byte*)SilkMarshal.StringToPtr("main");
        var stages = stackalloc PipelineShaderStageCreateInfo[2];
        stages[0] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.VertexBit,
            Module = vert,
            PName = entry,
        };
        stages[1] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.FragmentBit,
            Module = frag,
            PName = entry,
        };

        // world: pos3 uv2 color3 (stride 32) — hud: pos2 uv2 color4 (stride 32)
        var binding = new VertexInputBindingDescription(0, 32, VertexInputRate.Vertex);
        var attrs = stackalloc VertexInputAttributeDescription[3];
        if (worldVertexFormat)
        {
            attrs[0] = new VertexInputAttributeDescription(0, 0, Format.R32G32B32Sfloat, 0);
            attrs[1] = new VertexInputAttributeDescription(1, 0, Format.R32G32Sfloat, 12);
            attrs[2] = new VertexInputAttributeDescription(2, 0, Format.R32G32B32Sfloat, 20);
        }
        else
        {
            attrs[0] = new VertexInputAttributeDescription(0, 0, Format.R32G32Sfloat, 0);
            attrs[1] = new VertexInputAttributeDescription(1, 0, Format.R32G32Sfloat, 8);
            attrs[2] = new VertexInputAttributeDescription(2, 0, Format.R32G32B32A32Sfloat, 16);
        }
        var vertexInput = new PipelineVertexInputStateCreateInfo
        {
            SType = StructureType.PipelineVertexInputStateCreateInfo,
            VertexBindingDescriptionCount = 1,
            PVertexBindingDescriptions = &binding,
            VertexAttributeDescriptionCount = 3,
            PVertexAttributeDescriptions = attrs,
        };
        var inputAssembly = new PipelineInputAssemblyStateCreateInfo
        {
            SType = StructureType.PipelineInputAssemblyStateCreateInfo,
            Topology = topology,
        };
        var viewportState = new PipelineViewportStateCreateInfo
        {
            SType = StructureType.PipelineViewportStateCreateInfo,
            ViewportCount = 1,
            ScissorCount = 1,
        };
        var rasterizer = new PipelineRasterizationStateCreateInfo
        {
            SType = StructureType.PipelineRasterizationStateCreateInfo,
            PolygonMode = PolygonMode.Fill,
            LineWidth = 1f,
            CullMode = CullModeFlags.BackBit,
            FrontFace = FrontFace.CounterClockwise,
        };
        var multisampling = new PipelineMultisampleStateCreateInfo
        {
            SType = StructureType.PipelineMultisampleStateCreateInfo,
            RasterizationSamples = SampleCountFlags.Count1Bit,
        };
        var depthStencil = new PipelineDepthStencilStateCreateInfo
        {
            SType = StructureType.PipelineDepthStencilStateCreateInfo,
            DepthTestEnable = depth,
            DepthWriteEnable = depth,
            DepthCompareOp = CompareOp.Less,
        };
        var colorBlendAttachment = new PipelineColorBlendAttachmentState
        {
            ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            BlendEnable = true,
            SrcColorBlendFactor = BlendFactor.SrcAlpha,
            DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
            ColorBlendOp = BlendOp.Add,
            SrcAlphaBlendFactor = BlendFactor.One,
            DstAlphaBlendFactor = BlendFactor.Zero,
            AlphaBlendOp = BlendOp.Add,
        };
        var colorBlend = new PipelineColorBlendStateCreateInfo
        {
            SType = StructureType.PipelineColorBlendStateCreateInfo,
            AttachmentCount = 1,
            PAttachments = &colorBlendAttachment,
        };
        var dynamicStates = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
        var dynamicState = new PipelineDynamicStateCreateInfo
        {
            SType = StructureType.PipelineDynamicStateCreateInfo,
            DynamicStateCount = 2,
            PDynamicStates = dynamicStates,
        };
        var createInfo = new GraphicsPipelineCreateInfo
        {
            SType = StructureType.GraphicsPipelineCreateInfo,
            StageCount = 2,
            PStages = stages,
            PVertexInputState = &vertexInput,
            PInputAssemblyState = &inputAssembly,
            PViewportState = &viewportState,
            PRasterizationState = &rasterizer,
            PMultisampleState = &multisampling,
            PDepthStencilState = &depthStencil,
            PColorBlendState = &colorBlend,
            PDynamicState = &dynamicState,
            Layout = layout,
            RenderPass = _ctx.RenderPass,
            Subpass = 0,
        };
        VulkanContext.Check(_ctx.Vk.CreateGraphicsPipelines(_ctx.Device, default, 1, in createInfo, null, out var pipeline), "CreateGraphicsPipelines");
        SilkMarshal.Free((nint)entry);
        return pipeline;
    }

    public void Dispose()
    {
        var vk = _ctx.Vk;
        var device = _ctx.Device;
        vk.DestroyPipeline(device, World, null);
        vk.DestroyPipeline(device, Lines, null);
        vk.DestroyPipeline(device, Hud, null);
        vk.DestroyShaderModule(device, _worldVert, null);
        vk.DestroyShaderModule(device, _worldFrag, null);
        vk.DestroyShaderModule(device, _hudVert, null);
        vk.DestroyShaderModule(device, _hudFrag, null);
        vk.DestroyPipelineLayout(device, WorldLayout, null);
        vk.DestroyPipelineLayout(device, HudLayout, null);
        vk.DestroyDescriptorSetLayout(device, SetLayout, null);
    }
}
