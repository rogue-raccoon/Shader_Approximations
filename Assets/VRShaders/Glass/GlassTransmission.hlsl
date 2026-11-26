#ifndef GLASS_TRANSMISSION_INCLUDED
#define GLASS_TRANSMISSION_INCLUDED

#include "GlassCommon.hlsl"

gfloat3 TraceWavelength(float3 positionWS, float3 normalWS, float3 viewDirWS,
                        gfloat ior, gfloat3 sigmaT, int maxBounces)
{
    gfloat eta = 1.0 / ior; 
    float3 rd = refract(-viewDirWS, normalWS, (float)eta);
    if (dot(rd, rd) < 1e-6) return (gfloat3)0.0; 

    float3  p          = positionWS;
    gfloat3 throughput = (gfloat3)1.0;
    gfloat3 accum      = (gfloat3)0.0;

    UNITY_LOOP
    for (int i = 0; i < maxBounces; i++)
    {
        float3 roOS = TransformWorldToObject(p);
        float3 rdOS = TransformWorldToObjectDir(rd, false);

        float tEnter, tExit; float3 exitNormalOS;
        float3 traceOriginOS = roOS + rdOS * 1e-4;
        if (!ProxyIntersect(traceOriginOS, rdOS, tEnter, tExit, exitNormalOS)) break;

        float3 exitOS = traceOriginOS + rdOS * tExit;
        float3 exitWS = TransformObjectToWorld(exitOS);
        float3 exitNormalWS = normalize(TransformObjectToWorldNormal(exitNormalOS));

#if defined(_ABSORB_BEER)
        throughput *= BeerLambert(sigmaT, (gfloat)(length(exitWS - p) * max(_ThicknessScale, 0.0)));
#endif

        gfloat cosI = (gfloat)saturate(dot(rd, exitNormalWS));
        gfloat F    = FresnelDielectric(cosI, ior); 
        float3 outDir = refract(rd, -exitNormalWS, (float)ior);
        if (dot(outDir, outDir) < 1e-6) F = 1.0;      

        if (F < 1.0)
            accum += throughput * (1.0 - F) * SampleBackground(exitWS, outDir);

        throughput *= F;
        rd = reflect(rd, exitNormalWS);
        p  = exitWS;

        if (Max3(throughput.r, throughput.g, throughput.b) < 0.01) break;
    }

#if defined(_ABSORB_TINT)
    accum *= (gfloat3)_AbsorptionColor.rgb;
#endif
    return accum;
}

gfloat3 TransmitThin(float3 positionWS, float3 normalWS, float3 viewDirWS,
                     gfloat ior, gfloat3 sigmaT, gfloat thickness)
{
    float3 rd = refract(-viewDirWS, normalWS, (float)(1.0 / ior));
    if (dot(rd, rd) < 1e-6) rd = -viewDirWS;

    gfloat3 bg = SampleBackground(positionWS, rd);

#if defined(_ABSORB_BEER)
    bg *= BeerLambert(sigmaT, thickness);
#elif defined(_ABSORB_TINT)
    bg *= (gfloat3)_AbsorptionColor.rgb;
#endif
    return bg;
}

gfloat3 Transmission(float3 positionWS, float3 normalWS, float3 viewDirWS,
                     gfloat thickness, int maxBounces)
{
    gfloat3 sigmaT = ExtinctionCoefficient();

#if defined(_INTERNAL_SINGLE) || defined(_INTERNAL_MULTI)
    #define GLASS_TRANSMIT(ior) TraceWavelength(positionWS, normalWS, viewDirWS, ior, sigmaT, maxBounces)
#else
    #define GLASS_TRANSMIT(ior) TransmitThin(positionWS, normalWS, viewDirWS, ior, sigmaT, thickness)
#endif

#if defined(_DISPERSION_SPECTRAL)

    int   n     = clamp((int)_SpectralSamples, 2, 24);
    gfloat3 sum = (gfloat3)0.0;
    gfloat3 wsum = (gfloat3)1e-4;

    UNITY_LOOP
    for (int s = 0; s < n; s++)
    {
        gfloat  t = ((gfloat)s + 0.5) / (gfloat)n;
        gfloat3 w = SpectrumWeight(t);
        sum  += w * GLASS_TRANSMIT(IorForSample(t));
        wsum += w;
    }
    return sum / wsum;

#elif defined(_DISPERSION_RGB)

    gfloat3 r = GLASS_TRANSMIT(IorForSample(0.85));
    gfloat3 g = GLASS_TRANSMIT(IorForSample(0.50));
    gfloat3 b = GLASS_TRANSMIT(IorForSample(0.15));
    return gfloat3(r.r, g.g, b.b);

#else

    return GLASS_TRANSMIT((gfloat)_IOR);

#endif

#undef GLASS_TRANSMIT
}

#endif // GLASS_TRANSMISSION_INCLUDED
