#ifndef GLASS_COMMON_INCLUDED
#define GLASS_COMMON_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

#if defined(_REFRACT_SSR)
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"
#endif

#if defined(_PRECISION_HALF)
    #define gfloat  half
    #define gfloat2 half2
    #define gfloat3 half3
    #define gfloat4 half4
#else
    #define gfloat  float
    #define gfloat2 float2
    #define gfloat3 float3
    #define gfloat4 float4
#endif


CBUFFER_START(UnityPerMaterial)
    float4 _BaseColor;
    float4 _AbsorptionColor;    
    float  _AbsorptionDensity; 
    float  _IOR;
    float  _Dispersion;  
    float  _Roughness;
    float  _ThicknessScale;
    float  _MaxBounces;
    float  _SpectralSamples;
    float  _ThinOffset;       
    float4 _ProxyExtents;   
    float4 _ThicknessMap_ST;
    float  _Cull;      
CBUFFER_END

TEXTURE2D(_ThicknessMap);
SAMPLER(sampler_ThicknessMap);

struct Attributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    float2 uv         : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float4 positionCS : SV_POSITION;
    float3 positionWS : TEXCOORD0;
    float3 normalWS   : TEXCOORD1;
    float2 uv         : TEXCOORD2;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

Varyings GlassVertex(Attributes input)
{
    Varyings output = (Varyings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    VertexPositionInputs pos = GetVertexPositionInputs(input.positionOS.xyz);
    VertexNormalInputs   nrm = GetVertexNormalInputs(input.normalOS);

    output.positionCS = pos.positionCS;
    output.positionWS = pos.positionWS;
    output.normalWS   = nrm.normalWS;
    output.uv         = TRANSFORM_TEX(input.uv, _ThicknessMap);
    return output;
}


gfloat IorToF0(gfloat ior)
{
    gfloat r = (ior - 1.0) / (ior + 1.0);
    return r * r;
}

gfloat FresnelDielectric(gfloat cosThetaI, gfloat eta)
{
    cosThetaI = saturate(cosThetaI);
    gfloat sin2ThetaT = eta * eta * (1.0 - cosThetaI * cosThetaI);
    if (sin2ThetaT >= 1.0) return 1.0;             
    gfloat cosThetaT = sqrt(1.0 - sin2ThetaT);
    gfloat rParl = (eta * cosThetaI - cosThetaT) / max(eta * cosThetaI + cosThetaT, 1e-4);
    gfloat rPerp = (cosThetaI - eta * cosThetaT) / max(cosThetaI + eta * cosThetaT, 1e-4);
    return 0.5 * (rParl * rParl + rPerp * rPerp);
}

gfloat3 BeerLambert(gfloat3 sigmaT, gfloat distance)
{
    return exp(-sigmaT * distance);
}

gfloat3 ExtinctionCoefficient()
{
 
    return -log(clamp(gfloat3(_AbsorptionColor.rgb), 1e-4, 1.0)) * max(_AbsorptionDensity, 0.0);
}

gfloat3 SpectrumWeight(gfloat t)
{
    gfloat3 c;
    c.r = exp(-22.0 * (t - 0.78) * (t - 0.78)) + 0.20 * exp(-45.0 * (t - 0.05) * (t - 0.05));
    c.g = exp(-26.0 * (t - 0.52) * (t - 0.52));
    c.b = exp(-30.0 * (t - 0.22) * (t - 0.22));
    return c;
}

gfloat IorForSample(gfloat t)
{
    return max(_IOR + (0.5 - t) * 2.0 * _Dispersion, 1.0001);
}


static const float3 kPrismN[3] =
{
    float3( 0.0,        0.0,  1.0),
    float3(-0.86602540, 0.0, -0.5),
    float3( 0.86602540, 0.0, -0.5)
};

bool RaySphereOS(float3 ro, float3 rd, float r, out float tEnter, out float tExit)
{
    tEnter = 0; tExit = 0;
    float a = dot(rd, rd);
    float b = dot(ro, rd);
    float c = dot(ro, ro) - r * r;
    float h = b * b - a * c;
    if (h < 0.0 || a < 1e-12) return false;
    h = sqrt(h);
    tEnter = (-b - h) / a;
    tExit  = (-b + h) / a;
    return tExit > 0.0;
}


bool RayPrismOS(float3 ro, float3 rd, float inradius, float halfHeight,
                out float tEnter, out float tExit, out float3 nExit)
{
    tEnter = -1e20;
    tExit  =  1e20;
    nExit  = float3(0, 1, 0);

    UNITY_UNROLL
    for (int i = 0; i < 5; i++)
    {

        float3 n = (i < 3) ? kPrismN[min(i, 2)] : float3(0.0, (i == 3) ? 1.0 : -1.0, 0.0);
        float  d = (i < 3) ? inradius : halfHeight;

        float denom = dot(n, rd);
        float dist  = d - dot(n, ro);

        if (abs(denom) < 1e-7)
        {
            if (dist < 0.0) return false;   
            continue;
        }

        float t = dist / denom;
        if (denom < 0.0) { tEnter = max(tEnter, t); }          
        else if (t < tExit) { tExit = t; nExit = n; }        
    }

    return tExit > max(tEnter, 0.0);
}

bool RayLensOS(float3 ro, float3 rd, float R, float halfThickness,
               out float tEnter, out float tExit, out float3 nExit)
{
    tEnter = 0; tExit = 0; nExit = float3(0, 0, 1);

    float  offset = R - halfThickness;     
    float3 cFront = float3(0, 0, -offset);  
    float3 cBack  = float3(0, 0,  offset); 

    float fE, fX, bE, bX;
    if (!RaySphereOS(ro - cFront, rd, R, fE, fX)) return false;
    if (!RaySphereOS(ro - cBack,  rd, R, bE, bX)) return false;

    tEnter = max(fE, bE);
    tExit  = min(fX, bX);
    if (tExit <= max(tEnter, 0.0)) return false;

    float3 c = (fX < bX) ? cFront : cBack;
    nExit = normalize((ro + rd * tExit) - c);
    return true;
}

bool ProxyIntersect(float3 roOS, float3 rdOS, out float tEnter, out float tExit,
                    out float3 exitNormalOS)
{
#if defined(_PROXY_PRISM)
    return RayPrismOS(roOS, rdOS, _ProxyExtents.x, _ProxyExtents.y,
                      tEnter, tExit, exitNormalOS);
#elif defined(_PROXY_LENS)
    return RayLensOS(roOS, rdOS, _ProxyExtents.x, _ProxyExtents.y,
                     tEnter, tExit, exitNormalOS);
#else
    bool hit = RaySphereOS(roOS, rdOS, _ProxyExtents.x, tEnter, tExit);
    exitNormalOS = normalize(roOS + rdOS * tExit);
    return hit;
#endif
}

float3 ProxySurfaceNormalOS(float3 pOS)
{
#if defined(_PROXY_PRISM)
    float best = -1e20; float3 n = float3(0, 1, 0);
    UNITY_UNROLL
    for (int i = 0; i < 5; i++)
    {
        float3 ni = (i < 3) ? kPrismN[min(i, 2)] : float3(0.0, (i == 3) ? 1.0 : -1.0, 0.0);
        float  di = (i < 3) ? _ProxyExtents.x : _ProxyExtents.y;
        float  f  = dot(ni, pOS) - di;      
        if (f > best) { best = f; n = ni; }
    }
    return n;
#elif defined(_PROXY_LENS)
    float offset = _ProxyExtents.x - _ProxyExtents.y;
    float3 c = float3(0, 0, (pOS.z >= 0.0) ? -offset : offset);
    return normalize(pOS - c);
#else
    return normalize(pOS);
#endif
}

gfloat GlassThickness(float3 positionWS, float3 viewDirWS, float2 uv)
{
#if defined(_THICKNESS_ANALYTIC)
    float3 roOS = TransformWorldToObject(positionWS);
    float3 rdOS = TransformWorldToObjectDir(-viewDirWS, false);
    float tEnter, tExit; float3 nExitUnused;
    if (!ProxyIntersect(roOS - rdOS * 1e-3, rdOS, tEnter, tExit, nExitUnused)) return 0.0;
    return (gfloat)(max(tExit - max(tEnter, 0.0), 0.0) * _ThicknessScale);
#elif defined(_THICKNESS_BAKED)
    return (gfloat)(SAMPLE_TEXTURE2D(_ThicknessMap, sampler_ThicknessMap, uv).r * _ThicknessScale);
#else
    return (gfloat)_ThicknessScale;
#endif
}


gfloat3 SampleReflection(float3 reflectDirWS, float3 positionWS, float2 screenUV)
{
#if defined(_REFLECT_PROBE_ROUGH)
    return (gfloat3)GlossyEnvironmentReflection(reflectDirWS, positionWS,
                                                (half)_Roughness, 1.0h, screenUV);
#elif defined(_REFLECT_PROBE)
    return (gfloat3)GlossyEnvironmentReflection(reflectDirWS, positionWS,
                                                0.0h, 1.0h, screenUV);
#else
    return (gfloat3)0.0;
#endif
}

gfloat3 SampleBackground(float3 positionWS, float3 dirWS)
{
#if defined(_REFRACT_SSR)
    float3 hitWS = positionWS + dirWS * _ThinOffset;
    float4 clip = TransformWorldToHClip(hitWS);
    if (clip.w <= 1e-5)
        return (gfloat3)GlossyEnvironmentReflection(dirWS, positionWS, 0.0h, 1.0h, float2(0, 0));
    float4 screenPos = ComputeScreenPos(clip);
    float2 uv = screenPos.xy / screenPos.w;
    if (any(uv < 0.0) || any(uv > 1.0))
        return (gfloat3)GlossyEnvironmentReflection(dirWS, positionWS, 0.0h, 1.0h, float2(0, 0));
    return (gfloat3)SampleSceneColor(uv);
#elif defined(_REFRACT_CUBE)
    return (gfloat3)GlossyEnvironmentReflection(dirWS, positionWS, 0.0h, 1.0h, float2(0.5, 0.5));
#else
    return (gfloat3)0.0;
#endif
}

#endif // GLASS_COMMON_INCLUDED
