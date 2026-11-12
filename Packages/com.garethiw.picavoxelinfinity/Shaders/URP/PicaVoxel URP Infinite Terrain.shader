Shader "PicaVoxel/PicaVoxel URP Infinite Terrain"
{
    Properties
    {
        _TileSheet("TileSheet", 2D) = "white" {}
        _NumTiles("Num Tiles", Vector) = (0,0,0,0)
        _TilePadding("Tile Padding", Float) = 0
        _Tint("Tint", Color) = (1,1,1,0)
        _DrawDistance("Draw Distance", Float) = 0
        _DistanceFalloff("Distance Falloff", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha, SrcAlpha OneMinusSrcAlpha
            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 texcoord : TEXCOORD0;
                float4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 uv : TEXCOORD0;
                float4 color : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
                float3 normalWS : TEXCOORD3;
                float fogFactor : TEXCOORD4;
                half3 vertexSH : TEXCOORD5;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_TileSheet);
            SAMPLER(sampler_TileSheet);

            CBUFFER_START(UnityPerMaterial)
                float4 _TileSheet_ST;
                float4 _TileSheet_TexelSize;
                float2 _NumTiles;
                float _TilePadding;
                float4 _Tint;
                float _DrawDistance;
                float _DistanceFalloff;
            CBUFFER_END

            float2 ComputeTiledUV(float4 uv)
            {
                float tileW = 1.0 / _NumTiles.x;
                float tileH = 1.0 / _NumTiles.y;

                float2 padding = float2(_TileSheet_TexelSize.x, _TileSheet_TexelSize.y) * _TilePadding;
                float2 tileScale = float2(tileW, tileH) - padding * 2.0;
                float2 tileOffset = float2(tileW * uv.z, tileH * uv.w) + padding;

                float2 tiledUV = uv.xy * tileScale + tileOffset;
                return uv.w < 0.0 ? uv.xy : tiledUV;
            }

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS);

                output.positionCS = vertexInput.positionCS;
                output.positionWS = vertexInput.positionWS;
                output.normalWS = normalInput.normalWS;
                output.uv = input.texcoord;
                output.color = input.color;
                output.fogFactor = ComputeFogFactor(vertexInput.positionCS.z);
                output.vertexSH = SampleSH(output.normalWS);

                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 finalUV = ComputeTiledUV(input.uv);
                half4 texColor = SAMPLE_TEXTURE2D(_TileSheet, sampler_TileSheet, finalUV);

                half3 albedo = texColor.rgb * input.color.rgb * _Tint.rgb;

                // Distance fade
                float dist = distance(GetCameraPositionWS(), input.positionWS);
                float distFade = saturate(pow(dist / _DrawDistance, _DistanceFalloff));
                half alpha = texColor.a * (1.0 - distFade);

                // Lighting
                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.normalWS = normalize(input.normalWS);
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                inputData.fogCoord = InitializeInputDataFog(float4(input.positionWS, 1.0), input.fogFactor);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.bakedGI = input.vertexSH;

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = albedo;
                surfaceData.metallic = 0;
                surfaceData.smoothness = 0;
                surfaceData.alpha = alpha;
                surfaceData.occlusion = 1;

                half4 color = UniversalFragmentPBR(inputData, surfaceData);
                color.rgb = MixFog(color.rgb, input.fogFactor);
                color.a = alpha;

                return color;
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct ShadowAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct ShadowVaryings
            {
                float4 positionCS : SV_POSITION;
                float4 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_TileSheet);
            SAMPLER(sampler_TileSheet);

            CBUFFER_START(UnityPerMaterial)
                float4 _TileSheet_ST;
                float4 _TileSheet_TexelSize;
                float2 _NumTiles;
                float _TilePadding;
                float4 _Tint;
                float _DrawDistance;
                float _DistanceFalloff;
            CBUFFER_END

            float3 _LightDirection;

            ShadowVaryings ShadowVert(ShadowAttributes input)
            {
                ShadowVaryings output = (ShadowVaryings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);

                output.positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, _LightDirection));

                #if UNITY_REVERSED_Z
                    output.positionCS.z = min(output.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    output.positionCS.z = max(output.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif

                output.uv = input.texcoord;

                return output;
            }

            half4 ShadowFrag(ShadowVaryings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float tileW = 1.0 / _NumTiles.x;
                float tileH = 1.0 / _NumTiles.y;
                float2 padding = float2(_TileSheet_TexelSize.x, _TileSheet_TexelSize.y) * _TilePadding;
                float2 tileScale = float2(tileW, tileH) - padding * 2.0;
                float2 tileOffset = float2(tileW * input.uv.z, tileH * input.uv.w) + padding;
                float2 tiledUV = input.uv.xy * tileScale + tileOffset;
                float2 finalUV = input.uv.w < 0.0 ? input.uv.xy : tiledUV;

                half4 texColor = SAMPLE_TEXTURE2D(_TileSheet, sampler_TileSheet, finalUV);
                clip(texColor.a - 0.01);

                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R
            Cull Back

            HLSLPROGRAM
            #pragma vertex DepthVert
            #pragma fragment DepthFrag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct DepthAttributes
            {
                float4 positionOS : POSITION;
                float4 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct DepthVaryings
            {
                float4 positionCS : SV_POSITION;
                float4 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_TileSheet);
            SAMPLER(sampler_TileSheet);

            CBUFFER_START(UnityPerMaterial)
                float4 _TileSheet_ST;
                float4 _TileSheet_TexelSize;
                float2 _NumTiles;
                float _TilePadding;
                float4 _Tint;
                float _DrawDistance;
                float _DistanceFalloff;
            CBUFFER_END

            DepthVaryings DepthVert(DepthAttributes input)
            {
                DepthVaryings output = (DepthVaryings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.texcoord;

                return output;
            }

            half4 DepthFrag(DepthVaryings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float tileW = 1.0 / _NumTiles.x;
                float tileH = 1.0 / _NumTiles.y;
                float2 padding = float2(_TileSheet_TexelSize.x, _TileSheet_TexelSize.y) * _TilePadding;
                float2 tileScale = float2(tileW, tileH) - padding * 2.0;
                float2 tileOffset = float2(tileW * input.uv.z, tileH * input.uv.w) + padding;
                float2 tiledUV = input.uv.xy * tileScale + tileOffset;
                float2 finalUV = input.uv.w < 0.0 ? input.uv.xy : tiledUV;

                half4 texColor = SAMPLE_TEXTURE2D(_TileSheet, sampler_TileSheet, finalUV);
                clip(texColor.a - 0.01);

                return 0;
            }
            ENDHLSL
        }
    }
    Fallback "Universal Render Pipeline/Lit"
}
