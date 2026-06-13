using System;
using System.Collections.Generic;
using UnityEngine;

namespace VibeniteURP
{
    /// <summary>
    /// Builds Vibenite cluster hierarchies:
    ///
    ///   1. Weld the source mesh and split it into clusters of ≤128 triangles
    ///      (triangles sorted along a Morton curve for spatial locality).
    ///   2. Repeatedly: group ~4 spatially adjacent clusters, LOCK the group's
    ///      boundary vertices, QEM-simplify the merged interior to ~50% triangles,
    ///      and re-split the result into ~2 new (coarser) clusters.
    ///   3. Record monotonically growing errors + group bounds so the GPU can
    ///      select a crack-free cut through the hierarchy in parallel.
    /// </summary>
    public static class VibeniteBuilder
    {
        class BuildCluster
        {
            public List<int> tris = new List<int>();
            public Vector3 center;
            public float radius;
            public Vector3 lodCenter;
            public float lodRadius;
            public Vector3 parentCenter;
            public float parentRadius;
            public float error;
            public float parentError = float.MaxValue;
        }

        public static void Build(Vector3[] srcVerts, int[] srcIndices, VibeniteMeshAsset asset)
        {
            // ---------- 1. weld ----------
            WeldVertices(srcVerts, srcIndices, out Vector3[] pos, out int[] indices);
            Vector3[] normals = ComputeNormals(pos, indices);

            int srcTris = indices.Length / 3;
            var allClusters = new List<BuildCluster>();

            // ---------- 2. LOD 0 ----------
            var indexList = new List<int>(indices);
            var level = Clusterize(pos, indexList);
            foreach (var c in level)
            {
                c.error = 0f;                  // leaf clusters are exact
                c.lodCenter = c.center;
                c.lodRadius = c.radius;
            }
            allClusters.AddRange(level);

            // ---------- 3. build the hierarchy ----------
            int lodLevels = 1;
            int safety = 0;
            while (level.Count > 1 && safety++ < 40)
            {
                SortByMorton(level);
                var next = new List<BuildCluster>();

                for (int i = 0; i < level.Count; i += 4)
                {
                    int count = Mathf.Min(4, level.Count - i);
                    var group = level.GetRange(i, count);

                    // merge triangles of the group
                    var tris = new List<int>();
                    float maxChildError = 0;
                    foreach (var c in group)
                    {
                        tris.AddRange(c.tris);
                        maxChildError = Mathf.Max(maxChildError, c.error);
                    }
                    int triCount = tris.Count / 3;

                    // lock group boundary so neighbouring groups never crack open
                    var locked = FindBoundaryVertices(tris);

                    int target = Mathf.Max(triCount / 2, 1);
                    float simplifyError = MeshSimplifier.Simplify(pos, tris, locked, target);

                    // monotonic error: a parent is always at least as wrong as its children
                    float groupError = Mathf.Max(simplifyError, maxChildError) * 1.0001f + 1e-7f;

                    // group bounds must contain the children's LOD bounds
                    ComputeGroupBounds(group, out Vector3 gCenter, out float gRadius);

                    foreach (var c in group)
                    {
                        c.parentError = groupError;
                        c.parentCenter = gCenter;
                        c.parentRadius = gRadius;
                    }

                    var coarser = Clusterize(pos, tris);
                    foreach (var c in coarser)
                    {
                        c.error = groupError;
                        c.lodCenter = gCenter;
                        c.lodRadius = gRadius;
                    }

                    next.AddRange(coarser);
                    allClusters.AddRange(coarser);
                }

                lodLevels++;
                if (next.Count >= level.Count) { level = next; break; } // simplification stalled
                level = next;
            }

            // ---------- 4. serialize ----------
            var outIndices = new List<int>(allClusters.Count * VibeniteMeshAsset.MaxTrisPerCluster * 3);
            var outClusters = new VibeniteMeshAsset.Cluster[allClusters.Count];
            for (int i = 0; i < allClusters.Count; i++)
            {
                var c = allClusters[i];
                outClusters[i] = new VibeniteMeshAsset.Cluster
                {
                    center = c.center,
                    radius = c.radius,
                    lodCenter = c.lodCenter,
                    lodRadius = c.lodRadius,
                    parentCenter = c.parentCenter,
                    parentRadius = c.parentRadius,
                    error = c.error,
                    parentError = c.parentError,
                    indexOffset = outIndices.Count,
                    triangleCount = c.tris.Count / 3
                };
                outIndices.AddRange(c.tris);
            }

            asset.positions = pos;
            asset.normals = normals;
            asset.indices = outIndices.ToArray();
            asset.clusters = outClusters;
            asset.sourceTriangles = srcTris;
            asset.totalClusters = allClusters.Count;
            asset.lodLevels = lodLevels;
        }

        // ------------------------------------------------------------------ helpers

        static void WeldVertices(Vector3[] verts, int[] indices, out Vector3[] outVerts, out int[] outIndices)
        {
            var map = new Dictionary<Vector3Int, int>(verts.Length);
            var remap = new int[verts.Length];
            var welded = new List<Vector3>(verts.Length);
            const float grid = 1e-5f;

            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 v = verts[i];
                var key = new Vector3Int(
                    Mathf.RoundToInt(v.x / grid),
                    Mathf.RoundToInt(v.y / grid),
                    Mathf.RoundToInt(v.z / grid));
                if (!map.TryGetValue(key, out int idx))
                {
                    idx = welded.Count;
                    welded.Add(v);
                    map[key] = idx;
                }
                remap[i] = idx;
            }

            var outIdx = new List<int>(indices.Length);
            for (int t = 0; t < indices.Length; t += 3)
            {
                int a = remap[indices[t]], b = remap[indices[t + 1]], c = remap[indices[t + 2]];
                if (a == b || b == c || c == a) continue; // drop degenerates
                outIdx.Add(a); outIdx.Add(b); outIdx.Add(c);
            }

            outVerts = welded.ToArray();
            outIndices = outIdx.ToArray();
        }

        static Vector3[] ComputeNormals(Vector3[] pos, int[] indices)
        {
            var normals = new Vector3[pos.Length];
            for (int t = 0; t < indices.Length; t += 3)
            {
                int a = indices[t], b = indices[t + 1], c = indices[t + 2];
                Vector3 n = Vector3.Cross(pos[b] - pos[a], pos[c] - pos[a]); // area weighted
                normals[a] += n; normals[b] += n; normals[c] += n;
            }
            for (int i = 0; i < normals.Length; i++)
                normals[i] = normals[i].sqrMagnitude > 1e-20f ? normals[i].normalized : Vector3.up;
            return normals;
        }

        /// <summary>Split a triangle soup into clusters of ≤ MaxTrisPerCluster, Morton-ordered.</summary>
        static List<BuildCluster> Clusterize(Vector3[] pos, List<int> tris)
        {
            int triCount = tris.Count / 3;
            var result = new List<BuildCluster>();
            if (triCount == 0) return result;

            // bounds for Morton quantization
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            var centroids = new Vector3[triCount];
            for (int t = 0; t < triCount; t++)
            {
                Vector3 c = (pos[tris[t * 3]] + pos[tris[t * 3 + 1]] + pos[tris[t * 3 + 2]]) / 3f;
                centroids[t] = c;
                min = Vector3.Min(min, c);
                max = Vector3.Max(max, c);
            }
            Vector3 inv = InvExtent(min, max);

            var order = new int[triCount];
            var keys = new ulong[triCount];
            for (int t = 0; t < triCount; t++)
            {
                order[t] = t;
                keys[t] = Morton(centroids[t], min, inv);
            }
            Array.Sort(keys, order);

            for (int start = 0; start < triCount; start += VibeniteMeshAsset.MaxTrisPerCluster)
            {
                int n = Mathf.Min(VibeniteMeshAsset.MaxTrisPerCluster, triCount - start);
                var c = new BuildCluster();
                Vector3 bmin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                Vector3 bmax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                for (int k = 0; k < n; k++)
                {
                    int t = order[start + k];
                    for (int j = 0; j < 3; j++)
                    {
                        int idx = tris[t * 3 + j];
                        c.tris.Add(idx);
                        bmin = Vector3.Min(bmin, pos[idx]);
                        bmax = Vector3.Max(bmax, pos[idx]);
                    }
                }
                c.center = (bmin + bmax) * 0.5f;
                float r2 = 0;
                for (int k = 0; k < c.tris.Count; k++)
                    r2 = Mathf.Max(r2, (pos[c.tris[k]] - c.center).sqrMagnitude);
                c.radius = Mathf.Sqrt(r2);
                result.Add(c);
            }
            return result;
        }

        static HashSet<int> FindBoundaryVertices(List<int> tris)
        {
            var edgeCount = new Dictionary<ulong, int>(tris.Count);
            for (int t = 0; t < tris.Count; t += 3)
            {
                CountEdge(edgeCount, tris[t], tris[t + 1]);
                CountEdge(edgeCount, tris[t + 1], tris[t + 2]);
                CountEdge(edgeCount, tris[t + 2], tris[t]);
            }
            var locked = new HashSet<int>();
            foreach (var kv in edgeCount)
            {
                if (kv.Value != 1) continue;
                locked.Add((int)(kv.Key >> 32));
                locked.Add((int)(kv.Key & 0xFFFFFFFF));
            }
            return locked;
        }

        static void CountEdge(Dictionary<ulong, int> map, int a, int b)
        {
            ulong key = a < b ? ((ulong)(uint)a << 32) | (uint)b : ((ulong)(uint)b << 32) | (uint)a;
            map.TryGetValue(key, out int n);
            map[key] = n + 1;
        }

        static void ComputeGroupBounds(List<BuildCluster> group, out Vector3 center, out float radius)
        {
            center = Vector3.zero;
            foreach (var c in group) center += c.lodCenter;
            center /= group.Count;
            radius = 0;
            foreach (var c in group)
                radius = Mathf.Max(radius, Vector3.Distance(center, c.lodCenter) + c.lodRadius);
        }

        static void SortByMorton(List<BuildCluster> clusters)
        {
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            foreach (var c in clusters)
            {
                min = Vector3.Min(min, c.center);
                max = Vector3.Max(max, c.center);
            }
            Vector3 inv = InvExtent(min, max);
            clusters.Sort((a, b) => Morton(a.center, min, inv).CompareTo(Morton(b.center, min, inv)));
        }

        static Vector3 InvExtent(Vector3 min, Vector3 max)
        {
            Vector3 e = max - min;
            return new Vector3(
                e.x > 1e-9f ? 1f / e.x : 0f,
                e.y > 1e-9f ? 1f / e.y : 0f,
                e.z > 1e-9f ? 1f / e.z : 0f);
        }

        static ulong Morton(Vector3 p, Vector3 min, Vector3 inv)
        {
            uint x = (uint)(Mathf.Clamp01((p.x - min.x) * inv.x) * 1023f);
            uint y = (uint)(Mathf.Clamp01((p.y - min.y) * inv.y) * 1023f);
            uint z = (uint)(Mathf.Clamp01((p.z - min.z) * inv.z) * 1023f);
            return (Expand(x) << 2) | (Expand(y) << 1) | Expand(z);
        }

        static ulong Expand(uint v) // 10 bits -> every 3rd bit
        {
            ulong x = v & 0x3FF;
            x = (x | (x << 16)) & 0x30000FF;
            x = (x | (x << 8)) & 0x300F00F;
            x = (x | (x << 4)) & 0x30C30C3;
            x = (x | (x << 2)) & 0x9249249;
            return x;
        }
    }
}
