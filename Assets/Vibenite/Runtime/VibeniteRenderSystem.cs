using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace VibeniteURP
{
    /// <summary>
    /// Owns all GPU resources for the Nanite pipeline and tracks registered
    /// NaniteObject instances. Static (per-asset) data is rebuilt only when the
    /// set of objects changes; per-instance transforms are uploaded each frame.
    /// </summary>
    public class VibeniteRenderSystem
    {
        public static VibeniteRenderSystem Instance { get; } = new VibeniteRenderSystem();

        [StructLayout(LayoutKind.Sequential)]
        public struct GpuCluster // must match NaniteCull.compute / NaniteDraw.shader (64 bytes)
        {
            public Vector3 center; public float radius;
            public Vector3 lodCenter; public float lodRadius;
            public Vector3 parentCenter; public float parentRadius;
            public float error; public float parentError;
            public uint indexOffset; public uint triangleCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct GpuInstance // 80 bytes
        {
            public Matrix4x4 localToWorld;
            public float maxScale;
            public Vector3 _pad;
        }

        readonly List<VibeniteObject> objects = new List<VibeniteObject>();
        readonly Dictionary<VibeniteMeshAsset, Vector2Int> assetClusterRange = new Dictionary<VibeniteMeshAsset, Vector2Int>(); // (start, count)
        bool structureDirty;
        int lastInstanceUpdateFrame = -1;

        public GraphicsBuffer PositionBuffer { get; private set; }
        public GraphicsBuffer NormalBuffer { get; private set; }
        public GraphicsBuffer IndexBuffer { get; private set; }
        public GraphicsBuffer ClusterBuffer { get; private set; }
        public GraphicsBuffer ClusterInstanceBuffer { get; private set; } // uint2 (instance, cluster)
        public GraphicsBuffer InstanceBuffer { get; private set; }
        public GraphicsBuffer VisibleBuffer { get; private set; }         // uint2, compute output
        public GraphicsBuffer ArgsBuffer { get; private set; }            // indirect args

        public int ClusterInstanceCount { get; private set; }
        public int TotalClusters { get; private set; }

        GpuInstance[] instanceData;

        public void Register(VibeniteObject obj)
        {
            if (objects.Contains(obj)) return;
            objects.Add(obj);
            structureDirty = true;
        }

        public void Unregister(VibeniteObject obj)
        {
            if (objects.Remove(obj))
                structureDirty = true;
        }

        public void MarkDirty() => structureDirty = true;

        /// <summary>Called once per frame from the render feature. Returns false if there is nothing to draw.</summary>
        public bool Prepare()
        {
            objects.RemoveAll(o => o == null);
            if (objects.Count == 0) return false;

            if (structureDirty)
            {
                RebuildStatic();
                structureDirty = false;
            }
            if (ClusterInstanceCount == 0) return false;

            if (lastInstanceUpdateFrame != Time.frameCount)
            {
                lastInstanceUpdateFrame = Time.frameCount;
                UpdateInstances();
            }
            return true;
        }

        void RebuildStatic()
        {
            ReleaseStatic();
            assetClusterRange.Clear();

            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var indices = new List<uint>();
            var clusters = new List<GpuCluster>();

            // concatenate every distinct asset once, rebasing indices/offsets
            foreach (var obj in objects)
            {
                var asset = obj.Asset;
                if (asset == null || !asset.IsValid) continue;
                if (assetClusterRange.ContainsKey(asset)) continue;

                int vertexBase = positions.Count;
                int indexBase = indices.Count;
                int clusterBase = clusters.Count;

                positions.AddRange(asset.positions);
                normals.AddRange(asset.normals);
                foreach (int i in asset.indices)
                    indices.Add((uint)(i + vertexBase));

                foreach (var c in asset.clusters)
                {
                    clusters.Add(new GpuCluster
                    {
                        center = c.center, radius = c.radius,
                        lodCenter = c.lodCenter, lodRadius = c.lodRadius,
                        parentCenter = c.parentCenter, parentRadius = c.parentRadius,
                        error = c.error, parentError = c.parentError,
                        indexOffset = (uint)(c.indexOffset + indexBase),
                        triangleCount = (uint)c.triangleCount
                    });
                }
                assetClusterRange[asset] = new Vector2Int(clusterBase, asset.clusters.Length);
            }

            // flatten (instance, cluster) pairs for the culling dispatch
            var clusterInstances = new List<Vector2Int>();
            int instanceIndex = 0;
            foreach (var obj in objects)
            {
                var asset = obj.Asset;
                if (asset == null || !assetClusterRange.TryGetValue(asset, out var range)) { obj.InstanceIndex = -1; continue; }
                obj.InstanceIndex = instanceIndex;
                for (int c = 0; c < range.y; c++)
                    clusterInstances.Add(new Vector2Int(instanceIndex, range.x + c));
                instanceIndex++;
            }

            ClusterInstanceCount = clusterInstances.Count;
            TotalClusters = clusters.Count;
            if (ClusterInstanceCount == 0) return;

            PositionBuffer = NewBuffer(GraphicsBuffer.Target.Structured, positions.Count, 12);
            PositionBuffer.SetData(positions);
            NormalBuffer = NewBuffer(GraphicsBuffer.Target.Structured, normals.Count, 12);
            NormalBuffer.SetData(normals);
            IndexBuffer = NewBuffer(GraphicsBuffer.Target.Structured, indices.Count, 4);
            IndexBuffer.SetData(indices);
            ClusterBuffer = NewBuffer(GraphicsBuffer.Target.Structured, clusters.Count, 64);
            ClusterBuffer.SetData(clusters);

            var ciData = new uint[clusterInstances.Count * 2];
            for (int i = 0; i < clusterInstances.Count; i++)
            {
                ciData[i * 2] = (uint)clusterInstances[i].x;
                ciData[i * 2 + 1] = (uint)clusterInstances[i].y;
            }
            ClusterInstanceBuffer = NewBuffer(GraphicsBuffer.Target.Structured, clusterInstances.Count, 8);
            ClusterInstanceBuffer.SetData(ciData);

            InstanceBuffer = NewBuffer(GraphicsBuffer.Target.Structured, Mathf.Max(instanceIndex, 1), 80);
            instanceData = new GpuInstance[Mathf.Max(instanceIndex, 1)];

            VisibleBuffer = NewBuffer(GraphicsBuffer.Target.Structured, ClusterInstanceCount, 8);
            ArgsBuffer = NewBuffer(GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Structured, 4, 4);
            ArgsBuffer.SetData(new uint[] { VibeniteMeshAsset.MaxTrisPerCluster * 3, 0, 0, 0 });
        }

        void UpdateInstances()
        {
            foreach (var obj in objects)
            {
                if (obj == null || obj.InstanceIndex < 0) continue;
                var t = obj.transform;
                Vector3 s = t.lossyScale;
                instanceData[obj.InstanceIndex] = new GpuInstance
                {
                    localToWorld = t.localToWorldMatrix,
                    maxScale = Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z))
                };
            }
            InstanceBuffer.SetData(instanceData);
        }

        static GraphicsBuffer NewBuffer(GraphicsBuffer.Target target, int count, int stride)
            => new GraphicsBuffer(target, count, stride);

        void ReleaseStatic()
        {
            PositionBuffer?.Dispose(); PositionBuffer = null;
            NormalBuffer?.Dispose(); NormalBuffer = null;
            IndexBuffer?.Dispose(); IndexBuffer = null;
            ClusterBuffer?.Dispose(); ClusterBuffer = null;
            ClusterInstanceBuffer?.Dispose(); ClusterInstanceBuffer = null;
            InstanceBuffer?.Dispose(); InstanceBuffer = null;
            VisibleBuffer?.Dispose(); VisibleBuffer = null;
            ArgsBuffer?.Dispose(); ArgsBuffer = null;
            ClusterInstanceCount = 0;
        }
    }
}
