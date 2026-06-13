using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace VibeniteURP
{
    /// <summary>
    /// Unity 6 URP RenderGraph feature (NOT compatibility mode).
    /// Per camera:
    ///   1. ComputePass  — frustum-culls every (instance, cluster) pair and selects
    ///                     the LOD cut by projected screen-space error, appending
    ///                     survivors + building DrawProceduralIndirect args.
    ///   2. RasterPass   — one indirect draw renders all visible clusters: 384
    ///                     vertices per "instance", vertex shader pulls geometry
    ///                     from structured buffers via SV_VertexID / SV_InstanceID.
    /// </summary>
    public class VibeniteRenderFeature : ScriptableRendererFeature
    {
        public ComputeShader cullShader;
        public Shader drawShader;
        [Range(0.25f, 32f)]
        [Tooltip("Allowed screen-space geometric error in pixels. 1 = Vibenite-like quality.")]
        public float errorThresholdPixels = 1.0f;
        public bool debugClusterColors = true;
        public RenderPassEvent injectionPoint = RenderPassEvent.AfterRenderingOpaques;

        Material material;
        VibenitePass pass;

        public override void Create()
        {
            pass = new VibenitePass();
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (cullShader == null || drawShader == null) return;

            var camType = renderingData.cameraData.cameraType;
            if (camType == CameraType.Preview || camType == CameraType.Reflection) return;

            if (material == null)
                material = CoreUtils.CreateEngineMaterial(drawShader);

            pass.renderPassEvent = injectionPoint;
            pass.Setup(cullShader, material, errorThresholdPixels, debugClusterColors);
            renderer.EnqueuePass(pass);
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(material);
        }

        // ------------------------------------------------------------------

        class VibenitePass : ScriptableRenderPass
        {
            ComputeShader cs;
            Material material;
            float thresholdPx;
            bool debugColors;

            int kernelClear = -1, kernelCull = -1;
            readonly Vector4[] planeVecs = new Vector4[6];
            static readonly Plane[] planes = new Plane[6];

            static class ShaderIDs
            {
                public static readonly int Clusters = Shader.PropertyToID("_Clusters");
                public static readonly int Instances = Shader.PropertyToID("_Instances");
                public static readonly int ClusterInstances = Shader.PropertyToID("_ClusterInstances");
                public static readonly int Visible = Shader.PropertyToID("_Visible");
                public static readonly int Args = Shader.PropertyToID("_Args");
                public static readonly int ClusterInstanceCount = Shader.PropertyToID("_ClusterInstanceCount");
                public static readonly int FrustumPlanes = Shader.PropertyToID("_FrustumPlanes");
                public static readonly int CameraPos = Shader.PropertyToID("_CameraPos");
                public static readonly int ErrorScale = Shader.PropertyToID("_ErrorScale");
                public static readonly int Threshold = Shader.PropertyToID("_Threshold");
                public static readonly int Orthographic = Shader.PropertyToID("_Orthographic");

                public static readonly int VibenitePositions = Shader.PropertyToID("_VibenitePositions");
                public static readonly int VibeniteNormals = Shader.PropertyToID("_VibeniteNormals");
                public static readonly int VibeniteIndices = Shader.PropertyToID("_VibeniteIndices");
                public static readonly int VibeniteClusters = Shader.PropertyToID("_VibeniteClusters");
                public static readonly int VibeniteInstances = Shader.PropertyToID("_VibeniteInstances");
                public static readonly int VibeniteVisible = Shader.PropertyToID("_VibeniteVisible");
                public static readonly int VibeniteDebugClusters = Shader.PropertyToID("_VibeniteDebugClusters");
            }

            public void Setup(ComputeShader cull, Material mat, float threshold, bool debug)
            {
                cs = cull;
                material = mat;
                thresholdPx = threshold;
                debugColors = debug;
                if (kernelClear < 0)
                {
                    kernelClear = cs.FindKernel("ClearArgs");
                    kernelCull = cs.FindKernel("Cull");
                }
            }

            class CullPassData
            {
                public ComputeShader cs;
                public int kernelClear, kernelCull;
                public BufferHandle clusters, instances, clusterInstances, visible, args;
                public int clusterInstanceCount;
                public Vector4[] frustum;
                public Vector3 cameraPos;
                public float errorScale;
                public float threshold;
                public int orthographic;
            }

            class DrawPassData
            {
                public Material material;
                public GraphicsBuffer argsBufferRaw;
                public BufferHandle args, visible;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var sys = VibeniteRenderSystem.Instance;
                if (!sys.Prepare()) return;

                var cameraData = frameData.Get<UniversalCameraData>();
                var resourceData = frameData.Get<UniversalResourceData>();
                Camera cam = cameraData.camera;

                // ---- camera-dependent culling constants ----
                GeometryUtility.CalculateFrustumPlanes(cam, planes);
                for (int i = 0; i < 6; i++)
                {
                    var p = planes[i];
                    planeVecs[i] = new Vector4(p.normal.x, p.normal.y, p.normal.z, p.distance);
                }

                float pixelHeight = Mathf.Max(cam.pixelHeight, 1);
                bool ortho = cam.orthographic;
                float errorScale = ortho
                    ? pixelHeight / Mathf.Max(2f * cam.orthographicSize, 1e-4f)                       // px per world unit
                    : 0.5f * pixelHeight / Mathf.Tan(0.5f * cam.fieldOfView * Mathf.Deg2Rad);          // px per (unit/dist)

                // ---- import persistent buffers into the graph ----
                BufferHandle hClusters = renderGraph.ImportBuffer(sys.ClusterBuffer);
                BufferHandle hInstances = renderGraph.ImportBuffer(sys.InstanceBuffer);
                BufferHandle hClusterInstances = renderGraph.ImportBuffer(sys.ClusterInstanceBuffer);
                BufferHandle hVisible = renderGraph.ImportBuffer(sys.VisibleBuffer);
                BufferHandle hArgs = renderGraph.ImportBuffer(sys.ArgsBuffer);

                // ---- pass 1: cull + LOD select ----
                using (var builder = renderGraph.AddComputePass<CullPassData>("Vibenite Cull", out var data))
                {
                    data.cs = cs;
                    data.kernelClear = kernelClear;
                    data.kernelCull = kernelCull;
                    data.clusters = hClusters;
                    data.instances = hInstances;
                    data.clusterInstances = hClusterInstances;
                    data.visible = hVisible;
                    data.args = hArgs;
                    data.clusterInstanceCount = sys.ClusterInstanceCount;
                    data.frustum = planeVecs;
                    data.cameraPos = cam.transform.position;
                    data.errorScale = errorScale;
                    data.threshold = thresholdPx;
                    data.orthographic = ortho ? 1 : 0;

                    builder.UseBuffer(hClusters, AccessFlags.Read);
                    builder.UseBuffer(hInstances, AccessFlags.Read);
                    builder.UseBuffer(hClusterInstances, AccessFlags.Read);
                    builder.UseBuffer(hVisible, AccessFlags.Write);
                    builder.UseBuffer(hArgs, AccessFlags.Write);
                    builder.AllowPassCulling(false);

                    builder.SetRenderFunc((CullPassData d, ComputeGraphContext ctx) =>
                    {
                        var cmd = ctx.cmd;
                        cmd.SetComputeBufferParam(d.cs, d.kernelClear, ShaderIDs.Args, d.args);
                        cmd.DispatchCompute(d.cs, d.kernelClear, 1, 1, 1);

                        cmd.SetComputeBufferParam(d.cs, d.kernelCull, ShaderIDs.Clusters, d.clusters);
                        cmd.SetComputeBufferParam(d.cs, d.kernelCull, ShaderIDs.Instances, d.instances);
                        cmd.SetComputeBufferParam(d.cs, d.kernelCull, ShaderIDs.ClusterInstances, d.clusterInstances);
                        cmd.SetComputeBufferParam(d.cs, d.kernelCull, ShaderIDs.Visible, d.visible);
                        cmd.SetComputeBufferParam(d.cs, d.kernelCull, ShaderIDs.Args, d.args);
                        cmd.SetComputeIntParam(d.cs, ShaderIDs.ClusterInstanceCount, d.clusterInstanceCount);
                        cmd.SetComputeVectorArrayParam(d.cs, ShaderIDs.FrustumPlanes, d.frustum);
                        cmd.SetComputeVectorParam(d.cs, ShaderIDs.CameraPos, d.cameraPos);
                        cmd.SetComputeFloatParam(d.cs, ShaderIDs.ErrorScale, d.errorScale);
                        cmd.SetComputeFloatParam(d.cs, ShaderIDs.Threshold, d.threshold);
                        cmd.SetComputeIntParam(d.cs, ShaderIDs.Orthographic, d.orthographic);

                        int groups = (d.clusterInstanceCount + 63) / 64;
                        cmd.DispatchCompute(d.cs, d.kernelCull, groups, 1, 1);
                    });
                }

                // ---- bind geometry buffers on the material (plain device state, not RG resources) ----
                material.SetBuffer(ShaderIDs.VibenitePositions, sys.PositionBuffer);
                material.SetBuffer(ShaderIDs.VibeniteNormals, sys.NormalBuffer);
                material.SetBuffer(ShaderIDs.VibeniteIndices, sys.IndexBuffer);
                material.SetBuffer(ShaderIDs.VibeniteClusters, sys.ClusterBuffer);
                material.SetBuffer(ShaderIDs.VibeniteInstances, sys.InstanceBuffer);
                material.SetBuffer(ShaderIDs.VibeniteVisible, sys.VisibleBuffer);
                material.SetFloat(ShaderIDs.VibeniteDebugClusters, debugColors ? 1f : 0f);

                // ---- pass 2: single indirect draw of all visible clusters ----
                using (var builder = renderGraph.AddRasterRenderPass<DrawPassData>("Vibenite Draw", out var data))
                {
                    data.material = material;
                    data.argsBufferRaw = sys.ArgsBuffer;
                    data.args = hArgs;
                    data.visible = hVisible;

                    builder.UseBuffer(hArgs, AccessFlags.Read);
                    builder.UseBuffer(hVisible, AccessFlags.Read);
                    builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.Write);
                    builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.ReadWrite);
                    builder.AllowPassCulling(false);

                    builder.SetRenderFunc((DrawPassData d, RasterGraphContext ctx) =>
                    {
                        ctx.cmd.DrawProceduralIndirect(
                            Matrix4x4.identity, d.material, 0,
                            MeshTopology.Triangles, d.argsBufferRaw, 0, null);
                    });
                }
            }
        }
    }
}
