using System.Collections.Generic;
using UnityEngine;

namespace VibeniteURP
{
    /// <summary>
    /// Attach to a GameObject to render it through the Nanite pipeline.
    /// Either assign a baked NaniteMeshAsset, or assign a sourceMesh and it
    /// will be clusterized on first enable (cached, so many objects can share
    /// one mesh without rebuilding).
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("Rendering/Nanite Object")]
    public class VibeniteObject : MonoBehaviour
    {
        public VibeniteMeshAsset asset;
        [Tooltip("Optional: built into a NaniteMeshAsset at runtime if no asset is assigned.")]
        public Mesh sourceMesh;

        [System.NonSerialized] public int InstanceIndex = -1;

        static readonly Dictionary<Mesh, VibeniteMeshAsset> runtimeCache = new Dictionary<Mesh, VibeniteMeshAsset>();

        public VibeniteMeshAsset Asset => asset;

        void OnEnable()
        {
            if (asset == null && sourceMesh != null)
                asset = GetOrBuild(sourceMesh);
            VibeniteRenderSystem.Instance.Register(this);
        }

        void OnDisable()
        {
            VibeniteRenderSystem.Instance.Unregister(this);
        }

        public static VibeniteMeshAsset GetOrBuild(Mesh mesh)
        {
            if (runtimeCache.TryGetValue(mesh, out var cached) && cached != null)
                return cached;

            var built = ScriptableObject.CreateInstance<VibeniteMeshAsset>();
            built.name = mesh.name + " (Nanite)";
            var sw = System.Diagnostics.Stopwatch.StartNew();
            built.BuildFrom(mesh);
            sw.Stop();
            Debug.Log($"[Nanite] Built '{mesh.name}': {built.sourceTriangles} tris -> " +
                      $"{built.totalClusters} clusters, {built.lodLevels} LOD levels in {sw.ElapsedMilliseconds} ms");
            runtimeCache[mesh] = built;
            return built;
        }

        [ContextMenu("Rebuild From Source Mesh")]
        void RebuildFromSource()
        {
            if (sourceMesh == null) { Debug.LogWarning("[Nanite] No source mesh assigned."); return; }
            runtimeCache.Remove(sourceMesh);
            asset = GetOrBuild(sourceMesh);
            VibeniteRenderSystem.Instance.MarkDirty();
        }
    }
}
