using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Core.Native;
using Silk.NET.Shaderc;

namespace VoxelMiner.Engine;

/// GLSL sources for the three pipelines, compiled to SPIR-V at startup via shaderc.
public static unsafe class Shaders
{
    public const string WorldVert = @"
#version 450
layout(location = 0) in vec3 inPos;
layout(location = 1) in vec2 inUV;
layout(location = 2) in vec3 inColor;
layout(set = 0, binding = 0) uniform GlobalUbo {
    mat4 viewProj;
    vec4 camPos;
    vec4 fogColor;
    vec4 fogParams; // near, far, torch, baseLight
} ubo;
layout(push_constant) uniform Push {
    mat4 model;
    vec4 tint;
    vec4 params; // x = textured
} pc;
layout(location = 0) out vec2 uv;
layout(location = 1) out vec3 vcolor;
layout(location = 2) out vec3 worldPos;
void main() {
    vec4 wp = pc.model * vec4(inPos, 1.0);
    worldPos = wp.xyz;
    uv = inUV;
    vcolor = inColor;
    gl_Position = ubo.viewProj * wp;
}";

    public const string WorldFrag = @"
#version 450
layout(location = 0) in vec2 uv;
layout(location = 1) in vec3 vcolor;
layout(location = 2) in vec3 worldPos;
layout(set = 0, binding = 0) uniform GlobalUbo {
    mat4 viewProj;
    vec4 camPos;
    vec4 fogColor;
    vec4 fogParams;
} ubo;
layout(set = 0, binding = 1) uniform sampler2D tex;
layout(push_constant) uniform Push {
    mat4 model;
    vec4 tint;
    vec4 params; // x = textured, y = lightmap mode (vcolor = sky/torch bands)
} pc;
layout(location = 0) out vec4 outColor;
void main() {
    vec4 texel = texture(tex, uv);
    if (pc.params.x > 0.5 && texel.a < 0.5) discard; // cutout for vegetation billboards
    vec3 albedo = pc.params.x > 0.5 ? texel.rgb : vec3(1.0);
    float dist = length(worldPos - ubo.camPos.xyz);
    float torch = ubo.fogParams.z / (1.0 + 0.05 * dist * dist);
    // chunk meshes carry sky light (r) and torch light (g) separately;
    // daylight (camPos.w) scales only the sky band, torch light stays warm
    vec3 vc = vcolor;
    if (pc.params.y > 0.5) {
        float cs = vcolor.r * ubo.camPos.w;
        float cb = vcolor.g;
        vc = vec3(max(cs, cb), max(cs, cb * 0.85), max(cs, cb * 0.62));
    }
    vec3 lit = albedo * vc * pc.tint.rgb * (ubo.fogParams.w + torch);
    float fog = clamp((dist - ubo.fogParams.x) / (ubo.fogParams.y - ubo.fogParams.x), 0.0, 1.0);
    outColor = vec4(mix(lit, ubo.fogColor.rgb, fog), pc.tint.a);
}";

    public const string HudVert = @"
#version 450
layout(location = 0) in vec2 inPos;
layout(location = 1) in vec2 inUV;
layout(location = 2) in vec4 inColor;
layout(push_constant) uniform Push { vec4 screen; } pc;
layout(location = 0) out vec2 uv;
layout(location = 1) out vec4 col;
void main() {
    uv = inUV;
    col = inColor;
    vec2 ndc = inPos / pc.screen.xy * 2.0 - 1.0;
    gl_Position = vec4(ndc, 0.0, 1.0);
}";

    public const string HudFrag = @"
#version 450
layout(location = 0) in vec2 uv;
layout(location = 1) in vec4 col;
layout(set = 0, binding = 1) uniform sampler2D tex;
layout(location = 0) out vec4 outColor;
void main() {
    outColor = texture(tex, uv) * col;
}";

    public static byte[] Compile(string source, ShaderKind kind)
    {
        var api = Shaderc.GetApi();
        var compiler = api.CompilerInitialize();
        var options = api.CompileOptionsInitialize();
        byte[] sourceBytes = Encoding.UTF8.GetBytes(source);
        CompilationResult* result;
        fixed (byte* pSource = sourceBytes)
        {
            var name = (byte*)SilkMarshal.StringToPtr("shader");
            var entry = (byte*)SilkMarshal.StringToPtr("main");
            result = api.CompileIntoSpv(compiler, pSource, (nuint)sourceBytes.Length, kind, name, entry, options);
            SilkMarshal.Free((nint)name);
            SilkMarshal.Free((nint)entry);
        }
        if (api.ResultGetCompilationStatus(result) != CompilationStatus.Success)
        {
            string error = Marshal.PtrToStringAnsi((IntPtr)api.ResultGetErrorMessage(result));
            throw new InvalidOperationException($"Shader compilation failed: {error}");
        }
        nuint length = api.ResultGetLength(result);
        var spirv = new byte[(int)length];
        Marshal.Copy((IntPtr)api.ResultGetBytes(result), spirv, 0, (int)length);
        api.ResultRelease(result);
        api.CompileOptionsRelease(options);
        api.CompilerRelease(compiler);
        return spirv;
    }
}
