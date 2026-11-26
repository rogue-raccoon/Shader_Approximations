Shader "Thesis/GlassTiered"
{
    Properties
    {
        [Header(Base)]
        _BaseColor          ("Base Color (A = minimal tier opacity)", Color) = (1,1,1,0.1)
        _IOR                ("Index of Refraction", Range(1.0, 2.5)) = 1.52
        _Roughness          ("Roughness", Range(0,1)) = 0.05

        [Header(Absorption)]
        _AbsorptionColor    ("Transmittance @ unit distance", Color) = (0.85, 0.92, 0.88, 1)
        _AbsorptionDensity  ("Absorption Density", Range(0, 10)) = 1.0

        [Header(Dispersion)]
        _Dispersion         ("IOR Spread", Range(0, 0.2)) = 0.02
        _SpectralSamples    ("Spectral Samples", Range(2, 24)) = 8

        [Header(Volume)]
        _MaxBounces         ("Max Internal Bounces", Range(1, 8)) = 4
        _ProxyExtents       ("Proxy Extents", Vector) = (0.5, 0.5, 0.5, 0)
        _ThicknessScale     ("Thickness Scale", Range(0, 5)) = 1.0
        _ThinOffset         ("Thin Refraction Offset", Range(0, 2)) = 0.25
        _ThicknessMap       ("Thickness Map (R)", 2D) = "white" {}

        [Header(Blending)]
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType"     = "Transparent"
            "Queue"          = "Transparent"
        }

        Pass
        {
            Name "GlassForward"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex   GlassVertex
            #pragma fragment GlassFragment

            #pragma multi_compile_instancing
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION

            #pragma shader_feature_local_fragment _ _REFRACT_CUBE _REFRACT_SSR
            #pragma shader_feature_local_fragment _ _DISPERSION_RGB _DISPERSION_SPECTRAL
            #pragma shader_feature_local_fragment _ _ABSORB_TINT _ABSORB_BEER
            #pragma shader_feature_local_fragment _ _INTERNAL_SINGLE _INTERNAL_MULTI
            #pragma shader_feature_local_fragment _ _REFLECT_PROBE _REFLECT_PROBE_ROUGH
            #pragma shader_feature_local_fragment _ _THICKNESS_BAKED _THICKNESS_ANALYTIC
            #pragma shader_feature_local_fragment _ _PRECISION_HALF


            #pragma shader_feature_local_fragment _PROXY_SPHERE _PROXY_PRISM _PROXY_LENS


            #pragma shader_feature_local_fragment _ _ANALYTIC_ENTRY_NORMAL

            #include "GlassTransmission.hlsl"

            half4 GlassFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float3 normalWS  = normalize(input.normalWS);
                float3 viewDirWS = normalize(GetWorldSpaceViewDir(input.positionWS));

#if defined(_ANALYTIC_ENTRY_NORMAL)
                float3 pOS = TransformWorldToObject(input.positionWS);
                normalWS = normalize(TransformObjectToWorldNormal(ProxySurfaceNormalOS(pOS)));
#endif
                float2 screenUV  = GetNormalizedScreenSpaceUV(input.positionCS);

                gfloat cosTheta = (gfloat)saturate(dot(normalWS, viewDirWS));
                gfloat F = FresnelDielectric(cosTheta, (gfloat)(1.0 / _IOR));

                gfloat3 reflection = SampleReflection(reflect(-viewDirWS, normalWS),
                                                      input.positionWS, screenUV);

                gfloat thickness = GlassThickness(input.positionWS, viewDirWS, input.uv);

#if defined(_INTERNAL_MULTI)
                int maxBounces = clamp((int)_MaxBounces, 1, 8);
#else
                int maxBounces = 1;
#endif

                gfloat3 transmission = Transmission(input.positionWS, normalWS, viewDirWS,
                                                    thickness, maxBounces);

                gfloat3 color = lerp(transmission, reflection, F) * (gfloat3)_BaseColor.rgb;

#if defined(_REFRACT_SSR) || defined(_REFRACT_CUBE)
                gfloat alpha = 1.0;
#else
                gfloat alpha = saturate(F + (1.0 - F) * _BaseColor.a);
                color = reflection * (gfloat3)_BaseColor.rgb;
    #if defined(_ABSORB_TINT) || defined(_ABSORB_BEER)
                color += (gfloat3)_AbsorptionColor.rgb * (1.0 - F);
    #endif
#endif
                return half4((half3)color, (half)alpha);
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Unlit"
}
