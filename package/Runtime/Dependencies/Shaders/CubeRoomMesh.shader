Shader "HoloSpace/CubeRoomMesh"
{
    Properties
    {
        _Color ("Tint", Color) = (1,1,1,1)
        _Angle ("Effect Angle (deg)", Range (0, 90)) = 60
        [Enum(Off,0,On,1)] _SceneMeshZWrite("Self Occlude (BiRP only)", Float) = 0
        _FloorIntensity ("Floor Intensity", Range(0,1)) = 0.25
    }

    /////////////////////////////////////////////////////////////
    // ======================  URP  ============================
    /////////////////////////////////////////////////////////////
    SubShader
    {
        PackageRequirements {"com.unity.render-pipelines.universal"}
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }

        Pass
        {
            Name "UniversalForward"
            Tags { "LightMode"="UniversalForward" "RenderQueue"="3001" }
            LOD 100
            Cull Off
            ZWrite Off
            ZTest LEqual
            Blend One One

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
                half3  normal : NORMAL;
                float4 color  : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float2 uv      : TEXCOORD0;
                float4 color   : TEXCOORD1;
                half3  normal  : NORMAL;
                float4 vertex  : SV_POSITION;
                float3 worldN  : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float4 _Color;
            float  _Angle;
            float  _FloorIntensity;

            Varyings vert (Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                o.vertex = TransformObjectToHClip(input.vertex.xyz);
                o.worldN = TransformObjectToWorldNormal(input.normal);
                o.normal = o.worldN;
                o.uv     = input.uv;
                o.color  = input.color;
                return o;
            }

            half GetCoordFrom01(half v)
            {
                half c = saturate(frac(abs(v)));
                return abs(c * 2 - 1);
            }

            half4 frag (Varyings i, float facing : VFACE) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float backFace = facing > 0 ? 1 : 0.1;

                // Edge/Stroke
                float edgeGradient = max(GetCoordFrom01(i.uv.x), GetCoordFrom01(i.uv.y));
                float stroke = step(0.99, edgeGradient);
                float glow   = saturate((edgeGradient - 0.75) * 4);
                half4 edgeEffect = _Color * (stroke + pow(glow, 4) + 0.1);

                // Boden: sanfte Emission
                half4 floorEffect = _Color * _FloorIntensity;

                float groundMask = step( abs(dot(normalize(i.normal), float3(0,1,0))), cos(radians(_Angle)) );

                half4 finalEffect = lerp(floorEffect, edgeEffect, groundMask) * backFace;
                return finalEffect;
            }
            ENDHLSL
        }

        // ---------- SRPDefaultUnlit (Scene View Stabilizer) ----------
        Pass
        {
            Name "SRPDefaultUnlit"
            Tags { "LightMode"="SRPDefaultUnlit" }
            LOD 100
            Cull Off
            ZWrite Off
            ZTest LEqual
            Blend One One

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
                half3  normal : NORMAL;
                float4 color  : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float2 uv : TEXCOORD0;
                half3  normal : NORMAL;
                float4 vertex : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float4 _Color;
            float  _Angle;
            float  _FloorIntensity;

            Varyings vert (Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.vertex = TransformObjectToHClip(v.vertex.xyz);
                o.uv     = v.uv;
                o.normal = TransformObjectToWorldNormal(v.normal);
                return o;
            }

            half GetCoordFrom01(half x)
            {
                half c = saturate(frac(abs(x)));
                return abs(c * 2 - 1);
            }

            half4 frag (Varyings i, float facing : VFACE) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float backFace = facing > 0 ? 1 : 0.1;

                float edgeGradient = max(GetCoordFrom01(i.uv.x), GetCoordFrom01(i.uv.y));
                float stroke = step(0.99, edgeGradient);
                float glow   = saturate((edgeGradient - 0.75) * 4);
                half4 edgeEffect = _Color * (stroke + pow(glow, 4) + 0.1);

                half4 floorEffect = _Color * _FloorIntensity;

                float groundMask = step( abs(dot(normalize(i.normal), float3(0,1,0))), cos(radians(_Angle)) );

                return lerp(floorEffect, edgeEffect, groundMask) * backFace;
            }
            ENDHLSL
        }

        // ---------- Editor Picking/Selection ----------
        Pass
        {
            Name "ScenePickingPass"
            Tags{ "LightMode" = "ScenePickingPass" }
            Cull Off
            ZWrite Off
            ZTest LEqual
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            struct Attributes { float4 vertex:POSITION; };
            struct Varyings { float4 vertex:SV_POSITION; };
            Varyings vert(Attributes v){ Varyings o; o.vertex = TransformObjectToHClip(v.vertex.xyz); return o; }
            half4 frag(Varyings i) : SV_Target { return half4(0,0,0,0); }
            ENDHLSL
        }

        Pass
        {
            Name "SceneSelectionPass"
            Tags{ "LightMode" = "SceneSelectionPass" }
            Cull Off
            ZWrite Off
            ZTest LEqual
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            struct Attributes { float4 vertex:POSITION; };
            struct Varyings { float4 vertex:SV_POSITION; };
            Varyings vert(Attributes v){ Varyings o; o.vertex = TransformObjectToHClip(v.vertex.xyz); return o; }
            half4 frag(Varyings i) : SV_Target { return half4(0,0,0,0); }
            ENDHLSL
        }
    }

    /////////////////////////////////////////////////////////////
    // ======================  BiRP  ============================
    /////////////////////////////////////////////////////////////
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }

        Pass
        {
            Name "DepthOnly"
            Cull Off
            ZWrite [_SceneMeshZWrite]
            ZTest LEqual
            ColorMask 0
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct appdata { float4 vertex:POSITION; };
            struct v2f { float4 vertex:SV_POSITION; };
            v2f vert(appdata v){ v2f o; o.vertex = UnityObjectToClipPos(v.vertex); return o; }
            fixed4 frag(v2f i) : SV_Target { return 0; }
            ENDCG
        }

        Pass
        {
            Name "AdditiveColor"
            Cull Off
            ZWrite Off
            ZTest LEqual
            Blend One One
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
                half3  normal : NORMAL;
                float4 color  : COLOR;
            };
            struct v2f
            {
                float2 uv      : TEXCOORD0;
                half3  normal  : NORMAL;
                float4 vertex  : SV_POSITION;
            };

            float4 _Color;
            float  _Angle;
            float  _FloorIntensity;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.normal = UnityObjectToWorldNormal(v.normal);
                o.uv     = v.uv;
                return o;
            }

            fixed GetCoordFrom01(fixed x)
            {
                fixed c = saturate(frac(abs(x)));
                return abs(c * 2 - 1);
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float edgeGradient = max(GetCoordFrom01(i.uv.x), GetCoordFrom01(i.uv.y));
                float stroke = step(0.99, edgeGradient);
                float glow   = saturate((edgeGradient - 0.75) * 4);
                fixed4 edgeEffect = _Color * (stroke + pow(glow,4) + 0.1);

                fixed4 floorEffect = _Color * _FloorIntensity;

                float groundMask = step( abs(dot(normalize(i.normal), float3(0,1,0))), cos(radians(_Angle)) );

                return lerp(floorEffect, edgeEffect, groundMask);
            }
            ENDCG
        }
    }
}
