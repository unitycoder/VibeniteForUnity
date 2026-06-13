// Draws all visible Vibenite clusters with one DrawProceduralIndirect call.
// instanceCount = visible cluster count, 384 vertices each (128 tris).
// The vertex shader fetches geometry from structured buffers; clusters with
// fewer than 128 triangles emit NaN positions for the padding triangles,
// which the rasterizer discards.

Shader "Hidden/Vibenite/Draw"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "VibeniteForward"
            ZWrite On
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct GpuCluster
            {
                float3 center;       float radius;
                float3 lodCenter;    float lodRadius;
                float3 parentCenter; float parentRadius;
                float  error;        float parentError;
                uint   indexOffset;  uint  triangleCount;
            };

            struct GpuInstance
            {
                float4x4 localToWorld;
                float    maxScale;
                float3   _pad;
            };

            StructuredBuffer<float3>      _VibenitePositions;
            StructuredBuffer<float3>      _VibeniteNormals;
            StructuredBuffer<uint>        _VibeniteIndices;
            StructuredBuffer<GpuCluster>  _VibeniteClusters;
            StructuredBuffer<GpuInstance> _VibeniteInstances;
            StructuredBuffer<uint2>       _VibeniteVisible;
            float _VibeniteDebugClusters;

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS   : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                nointerpolation uint clusterId : TEXCOORD2;
            };

            Varyings Vert(uint vertexId : SV_VertexID, uint instanceId : SV_InstanceID)
            {
                Varyings o = (Varyings)0;

                uint2 ci = _VibeniteVisible[instanceId];
                GpuCluster cluster = _VibeniteClusters[ci.y];

                uint tri = vertexId / 3u;
                if (tri >= cluster.triangleCount)
                {
                    // padding triangle -> NaN clip position kills it
                    o.positionCS = asfloat(0x7FC00000).xxxx;
                    return o;
                }

                uint index = _VibeniteIndices[cluster.indexOffset + vertexId];
                GpuInstance inst = _VibeniteInstances[ci.x];

                float3 positionWS = mul(inst.localToWorld, float4(_VibenitePositions[index], 1)).xyz;
                float3 normalWS = normalize(mul((float3x3)inst.localToWorld, _VibeniteNormals[index]));

                o.positionCS = TransformWorldToHClip(positionWS);
                o.positionWS = positionWS;
                o.normalWS = normalWS;
                o.clusterId = ci.y;
                return o;
            }

            half3 HashColor(uint n)
            {
                n = n * 747796405u + 2891336453u;
                n = ((n >> ((n >> 28u) + 4u)) ^ n) * 277803737u;
                n = (n >> 22u) ^ n;
                return half3(
                    (n & 0x3FFu) / 1023.0h,
                    ((n >> 10u) & 0x3FFu) / 1023.0h,
                    ((n >> 20u) & 0x3FFu) / 1023.0h) * 0.75h + 0.25h;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float3 n = normalize(i.normalWS);
                Light mainLight = GetMainLight();
                half ndl = saturate(dot(n, mainLight.direction));

                half3 albedo = _VibeniteDebugClusters > 0.5
                    ? HashColor(i.clusterId)
                    : half3(0.8h, 0.8h, 0.8h);

                half3 ambient = SampleSH(n);
                half3 color = albedo * (mainLight.color * ndl + ambient + 0.03h);
                return half4(color, 1);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
