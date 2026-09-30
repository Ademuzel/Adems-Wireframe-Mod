Shader "Custom/GeometryWireframe"
{
    Properties
    {
        _Color ("Wire Color", Color) = (0, 1, 1, 1)
        _WireThickness ("Wire Thickness (px)", Range(0.5, 10)) = 1.5
        _FillAlpha ("Fill Alpha", Range(0, 1)) = 0.0
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "RenderPipeline"="UniversalPipeline" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma geometry geom
            #pragma fragment frag
            #pragma target 4.0
            #pragma multi_compile_instancing
            // Required for single-pass-instanced stereo rendering (this is the same
            // macro set that turned out to be missing from the decompiled stub shader
            // during the emote-wheel one-eye bug - don't skip these here either).
            #pragma multi_compile _ STEREO_INSTANCING_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct appdata
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2g
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            struct g2f
            {
                float4 positionCS : SV_POSITION;
                float3 barycentric : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float _WireThickness;
                float _FillAlpha;
            CBUFFER_END

            v2g vert(appdata v)
            {
                v2g o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                VertexPositionInputs pos = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = pos.positionCS;
                return o;
            }

            // Geometry shaders run per-primitive, so the stereo eye index set up in
            // vert() needs to be explicitly re-applied to each emitted vertex here -
            // UNITY_TRANSFER_INSTANCE_ID alone does not automatically propagate the
            // stereo target eye through the GS stage on all platforms.
            [maxvertexcount(3)]
            void geom(triangle v2g input[3], inout TriangleStream<g2f> stream)
            {
                g2f o0, o1, o2;

                UNITY_SETUP_INSTANCE_ID(input[0]);
                UNITY_TRANSFER_INSTANCE_ID(input[0], o0);
                UNITY_TRANSFER_INSTANCE_ID(input[0], o1);
                UNITY_TRANSFER_INSTANCE_ID(input[0], o2);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o0);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o1);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o2);
                UNITY_TRANSFER_VERTEX_OUTPUT_STEREO(input[0], o0);
                UNITY_TRANSFER_VERTEX_OUTPUT_STEREO(input[0], o1);
                UNITY_TRANSFER_VERTEX_OUTPUT_STEREO(input[0], o2);

                o0.positionCS = input[0].positionCS;
                o1.positionCS = input[1].positionCS;
                o2.positionCS = input[2].positionCS;

                o0.barycentric = float3(1, 0, 0);
                o1.barycentric = float3(0, 1, 0);
                o2.barycentric = float3(0, 0, 1);

                stream.Append(o0);
                stream.Append(o1);
                stream.Append(o2);
            }

            float edgeFactor(float3 bary)
            {
                float3 d = fwidth(bary);
                float3 a3 = smoothstep(float3(0,0,0), d * _WireThickness, bary);
                return min(min(a3.x, a3.y), a3.z);
            }

            half4 frag(g2f i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                float edge = edgeFactor(i.barycentric);
                // edge ~0 right on the wire line, ~1 in the triangle interior.
                float alpha = lerp(1.0, _FillAlpha, edge);

                if (alpha <= 0.001) discard;
                return half4(_Color.rgb, _Color.a * alpha);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
