// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Platform.Media.Scene3D;

/// <summary>
/// HLSL for the model: Filament's standard lit model as gltfio's ubershader uses it — Lambert diffuse
/// lit by the IBL's spherical harmonics, GGX specular from the prefiltered cubemap with a split-sum
/// DFG term, Filament's specular occlusion and roughness-to-LOD mapping, optional directional light,
/// emissive added after exposure, then ACES (legacy) tone mapping and sRGB output with premultiplied alpha.
/// </summary>
internal static class SceneShaders
{
    public const string Source = """
cbuffer Frame : register(b0)
{
    row_major float4x4 World;
    row_major float4x4 ViewProjection;
    row_major float4x4 IblRotation;   // world -> environment
    float4 CameraPosition;
    float4 Sh[9];                     // pre-scaled irradiance SH (Filament cmgen)
    float4 LightDirection;            // xyz: direction the light travels; w: illuminance x exposure
    float4 LightColor;
    float4 Params;                    // x: IBL luminance (intensity x exposure); y: last cubemap LOD; z: has IBL
};

cbuffer Material : register(b1)
{
    float4 BaseColorFactor;
    float4 EmissiveFactor;
    float4 Pbr;                       // metallic, perceptual roughness, normal scale, AO strength
    float4 Maps;                      // has base color, metallic-roughness, normal, occlusion
    float4 Extra;                     // has emissive, reflectance, alpha cutoff (<0: none), unlit backfaces
};

Texture2D BaseColorMap : register(t0);
Texture2D MetallicRoughnessMap : register(t1);
Texture2D NormalMap : register(t2);
Texture2D OcclusionMap : register(t3);
Texture2D EmissiveMap : register(t4);
TextureCube Environment : register(t5);
SamplerState MaterialSampler : register(s0);
SamplerState EnvironmentSampler : register(s1);

struct VertexInput
{
    float3 Position : POSITION;
    float3 Normal : NORMAL;
    float4 Tangent : TANGENT;
    float2 Uv : TEXCOORD0;
};

struct PixelInput
{
    float4 Position : SV_Position;
    float3 World : POSITION1;
    float3 Normal : NORMAL;
    float4 Tangent : TANGENT;
    float2 Uv : TEXCOORD0;
};

PixelInput VS(VertexInput input)
{
    PixelInput output;
    float4 world = mul(float4(input.Position, 1), World);
    output.World = world.xyz;
    output.Position = mul(world, ViewProjection);
    output.Normal = mul(input.Normal, (float3x3)World);
    output.Tangent = float4(mul(input.Tangent.xyz, (float3x3)World), input.Tangent.w);
    output.Uv = input.Uv;
    return output;
}

static const float PI = 3.14159265;
static const float MinPerceptualRoughness = 0.045;

float3 Irradiance(float3 n)
{
    return max(Sh[0].rgb
        + Sh[1].rgb * n.y + Sh[2].rgb * n.z + Sh[3].rgb * n.x
        + Sh[4].rgb * (n.y * n.x) + Sh[5].rgb * (n.y * n.z) + Sh[6].rgb * (3.0 * n.z * n.z - 1.0)
        + Sh[7].rgb * (n.z * n.x) + Sh[8].rgb * (n.x * n.x - n.y * n.y), 0.0);
}

// Split-sum DFG (Karis' analytic fit of the integrated BRDF): returns (scale, bias) for f0.
float2 Dfg(float NoV, float perceptualRoughness)
{
    const float4 c0 = float4(-1, -0.0275, -0.572, 0.022);
    const float4 c1 = float4(1, 0.0425, 1.04, -0.04);
    float4 r = perceptualRoughness * c0 + c1;
    float a004 = min(r.x * r.x, exp2(-9.28 * NoV)) * r.x + r.y;
    return float2(-1.04, 1.04) * a004 + r.zw;
}

float SpecularAO(float NoV, float ao, float roughness)
{
    return saturate(pow(NoV + ao, exp2(-16.0 * roughness - 1.0)) - 1.0 + ao);
}

float D_GGX(float NoH, float roughness)
{
    float a = NoH * roughness;
    float k = roughness / (1.0 - NoH * NoH + a * a);
    return k * k * (1.0 / PI);
}

float V_SmithGGXCorrelated(float NoV, float NoL, float roughness)
{
    float a2 = roughness * roughness;
    float lv = NoL * sqrt(NoV * NoV * (1.0 - a2) + a2);
    float ll = NoV * sqrt(NoL * NoL * (1.0 - a2) + a2);
    return 0.5 / (lv + ll);
}

float3 F_Schlick(float3 f0, float VoH)
{
    float f = pow(1.0 - VoH, 5.0);
    return f + f0 * (1.0 - f);
}

// Filament's ACES_LEGACY: the ACES RRT + ODT (Hill's fit) after a 1/0.6 brightness boost.
float3 ToneMap(float3 x)
{
    const float3x3 input = { 0.59719, 0.35458, 0.04823, 0.07600, 0.90834, 0.01566, 0.02840, 0.13383, 0.83777 };
    const float3x3 output = { 1.60475, -0.53108, -0.07367, -0.10208, 1.10813, -0.00605, -0.00327, -0.07276, 1.07602 };
    float3 v = mul(input, x / 0.6);
    float3 a = v * (v + 0.0245786) - 0.000090537;
    float3 b = v * (0.983729 * v + 0.4329510) + 0.238081;
    return saturate(mul(output, a / b));
}

float3 LinearToSrgb(float3 c)
{
    return c <= 0.0031308 ? c * 12.92 : 1.055 * pow(c, 1.0 / 2.4) - 0.055;
}

float4 PS(PixelInput input, bool front : SV_IsFrontFace) : SV_Target
{
    float4 baseColor = BaseColorFactor;
    if (Maps.x > 0) baseColor *= BaseColorMap.Sample(MaterialSampler, input.Uv);
    if (Extra.z >= 0) clip(baseColor.a - Extra.z);

    float metallic = Pbr.x;
    float perceptualRoughness = Pbr.y;
    if (Maps.y > 0)
    {
        float4 mr = MetallicRoughnessMap.Sample(MaterialSampler, input.Uv);
        perceptualRoughness *= mr.g;
        metallic *= mr.b;
    }
    metallic = saturate(metallic);
    perceptualRoughness = clamp(perceptualRoughness, MinPerceptualRoughness, 1.0);
    float roughness = perceptualRoughness * perceptualRoughness;
    float ao = Maps.w > 0 ? 1.0 + Pbr.w * (OcclusionMap.Sample(MaterialSampler, input.Uv).r - 1.0) : 1.0;

    float3 n = normalize(input.Normal);
    if (!front) n = -n;
    if (Maps.z > 0)
    {
        float3 t = input.Tangent.xyz - n * dot(n, input.Tangent.xyz);
        if (dot(t, t) > 1e-12)
        {
            t = normalize(t);
            float3 b = cross(n, t) * (input.Tangent.w < 0 ? -1.0 : 1.0);
            float3 tn = NormalMap.Sample(MaterialSampler, input.Uv).xyz * 2.0 - 1.0;
            tn.xy *= Pbr.z;
            n = normalize(tn.x * t + tn.y * b + tn.z * n);
        }
    }

    float3 v = normalize(CameraPosition.xyz - input.World);
    float NoV = max(dot(n, v), 1e-4);
    float reflectance = Extra.y;
    float3 diffuseColor = baseColor.rgb * (1.0 - metallic);
    float3 f0 = 0.16 * reflectance * reflectance * (1.0 - metallic) + baseColor.rgb * metallic;

    float3 color = 0;
    if (Params.z > 0)
    {
        float2 dfg = Dfg(NoV, perceptualRoughness);
        float3 E = f0 * dfg.x + dfg.y;
        float3 r = reflect(-v, n);
        r = lerp(r, n, roughness * roughness);
        float3 envR = mul(r, (float3x3)IblRotation);
        float3 envN = mul(n, (float3x3)IblRotation);
        float lod = Params.y * perceptualRoughness * (2.0 - perceptualRoughness);
        float3 radiance = Environment.SampleLevel(EnvironmentSampler, envR, lod).rgb;
        float3 Fr = E * radiance * SpecularAO(NoV, ao, roughness);
        float3 Fd = diffuseColor * Irradiance(envN) * (1.0 - E) * ao;
        color += (Fd + Fr) * Params.x;
    }
    if (LightDirection.w > 0)
    {
        float3 l = normalize(-LightDirection.xyz);
        float NoL = saturate(dot(n, l));
        float3 h = normalize(v + l);
        float NoH = saturate(dot(n, h));
        float VoH = saturate(dot(v, h));
        float3 Fr = D_GGX(NoH, roughness) * V_SmithGGXCorrelated(NoV, NoL, roughness) * F_Schlick(f0, VoH);
        float3 Fd = diffuseColor / PI;
        color += (Fd + Fr) * LightColor.rgb * (LightDirection.w * NoL);
    }
    float3 emissive = EmissiveFactor.rgb;
    if (Extra.x > 0) emissive *= EmissiveMap.Sample(MaterialSampler, input.Uv).rgb;
    color += emissive;

    float alpha = Extra.w > 0 ? baseColor.a : 1.0;
    return float4(LinearToSrgb(ToneMap(color)) * alpha, alpha);
}
""";
}
