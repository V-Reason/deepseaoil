Shader "DeepseaOil/Particles/ProceduralSteam"
{
    Properties
    {
        _NoiseScale ("Noise Scale (噪波密度)", Float) = 3.5
        _NoiseSpeed ("Noise Speed (翻滚速率)", Float) = 0.8
        _EdgeSoftness ("Edge Softness (边缘揉化度)", Range(0.1, 1.0)) = 0.65
        _ErosionStrength ("Erosion Strength (消散撕裂感)", Range(0.0, 1.0)) = 0.6
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            Name "Unlit"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
            };

            CBUFFER_START(UnityPerMaterial)
                float _NoiseScale;
                float _NoiseSpeed;
                float _EdgeSoftness;
                float _ErosionStrength;
            CBUFFER_END

            float hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float valueNoise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);

                float a = hash21(i);
                float b = hash21(i + float2(1.0, 0.0));
                float c = hash21(i + float2(0.0, 1.0));
                float d = hash21(i + float2(1.0, 1.0));

                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            // 两层轻量 FBM 湍流数学，生成云雾絮状感
            float fbm(float2 p)
            {
                float v = 0.0;
                v += 0.65 * valueNoise(p);
                p = p * 2.05 + float2(11.5, 13.7);
                v += 0.35 * valueNoise(p);
                return v;
            }

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.color = input.color;
                output.uv = input.uv;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float2 uv = input.uv;
                float2 centered = (uv - 0.5) * 2.0;
                float dist = length(centered);

                // 各向同性微弱热对流翻滚
                float time = _Time.y * _NoiseSpeed;
                float2 noiseUV = uv * _NoiseScale + float2(sin(time * 0.6) * 0.08, cos(time * 0.5) * 0.08);
                float noise = fbm(noiseUV);

                // 中心核心区（dist < 0.35）强制封实，噪波仅作用于外圈，彻底消除低频下的空心环
                float coreProtection = smoothstep(0.0, 0.45, dist);
                float distortedDist = dist + (noise - 0.5) * _EdgeSoftness * coreProtection;

                // 柔和羽化衰减
                float radialFalloff = saturate(1.0 - distortedDist);
                radialFalloff = smoothstep(0.0, 0.75, radialFalloff);

                // 连续消散侵蚀
                float lifeAlpha = input.color.a;
                // 中心基底密度保底为 0.75，向外自然受噪波调制
                float densityMod = lerp(0.85, 0.5 + noise * 0.7, coreProtection);
                float density = radialFalloff * densityMod;
                float finalAlpha = saturate(density * lifeAlpha * 1.6);

                // 假体积光感
                // 浓厚雾核略微增亮 + 边缘薄雾略微透光，赋予 2D 贴图以 3D 厚度感
                half3 volumetricColor = lerp(input.color.rgb * 0.94, input.color.rgb * 1.15, radialFalloff * radialFalloff);

                return half4(volumetricColor, finalAlpha);
            }
            ENDHLSL
        }
    }
}