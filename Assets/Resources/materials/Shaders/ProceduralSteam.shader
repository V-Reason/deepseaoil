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

                // 随时间微弱滚动的热气流噪波
                float timeOffset = _Time.y * _NoiseSpeed;
                float noise = fbm(uv * _NoiseScale + float2(timeOffset * 0.2, timeOffset * 0.4));

                // 核心：用噪波扰动打破死板正圆边界，使圆盘边缘撕裂成云絮状
                float distortedDist = dist + (noise - 0.5) * _EdgeSoftness;
                float radialMask = smoothstep(1.0, 1.0 - _EdgeSoftness, distortedDist);

                // 核心：在粒子寿命后半段，根据透明度衰减逐层侵蚀瓦解成缕缕青烟，拒绝整块暴毙
                float lifeAlpha = input.color.a;
                float erode = smoothstep(1.0 - lifeAlpha * (1.0 + _ErosionStrength), 1.0, noise + lifeAlpha);

                float finalAlpha = radialMask * erode * saturate(lifeAlpha * 1.3);

                return half4(input.color.rgb, finalAlpha);
            }
            ENDHLSL
        }
    }
}