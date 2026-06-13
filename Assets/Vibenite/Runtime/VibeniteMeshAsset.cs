using System;
using UnityEngine;

namespace VibeniteURP
{
    /// <summary>
    /// Baked virtual-geometry data: a flat list of clusters covering ALL LOD levels
    /// of one source mesh, plus the shared vertex/index pools they reference.
    ///
    /// The runtime never picks "a LOD level" — the GPU evaluates every cluster in
    /// parallel and keeps exactly the ones whose own error is acceptable while
    /// their parent's error is not (the "Nanite cut").
    /// </summary>
    public class VibeniteMeshAsset : ScriptableObject
    {
        public const int MaxTrisPerCluster = 128;

        [Serializable]
        public struct Cluster
        {
            // Tight culling bounds of this cluster's own triangles.
            public Vector3 center;
            public float radius;

            // Bounds of the *group* this cluster was simplified from.
            // Self-error must be projected with these so that parent/child
            // tests are evaluated identically and the LOD cut has no gaps.
            public Vector3 lodCenter;
            public float lodRadius;

            // Bounds + error of the group one level coarser (our parent group).
            public Vector3 parentCenter;
            public float parentRadius;

            public float error;        // object-space error of this cluster's geometry
            public float parentError;  // object-space error of the parent group (float.MaxValue at roots)

            public int indexOffset;    // first index (not triangle) into 'indices'
            public int triangleCount;  // <= MaxTrisPerCluster
        }

        internal Vector3[] positions;
        internal Vector3[] normals;
        internal int[] indices;          // global indices into positions/normals, concatenated per cluster
        internal Cluster[] clusters;

        [Header("Bake stats")]
        public int sourceTriangles;
        public int totalClusters;
        public int lodLevels;

        public bool IsValid => clusters != null && clusters.Length > 0 && positions != null && indices != null;

        public void BuildFrom(Mesh mesh)
        {
            var verts = mesh.vertices;
            var tris = mesh.triangles;
            VibeniteBuilder.Build(verts, tris, this);
        }

        public void BuildFrom(Vector3[] verts, int[] tris)
        {
            VibeniteBuilder.Build(verts, tris, this);
        }
    }
}
