#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace VibeniteURP.Editor
{
    public static class VibeniteBakeMenu
    {
        [MenuItem("Assets/Vibenite/Bake Cluster Hierarchy From Mesh", true)]
        static bool Validate() => Selection.activeObject is Mesh;

        [MenuItem("Assets/Vibenite/Bake Cluster Hierarchy From Mesh")]
        static void Bake()
        {
            var mesh = (Mesh)Selection.activeObject;
            var asset = ScriptableObject.CreateInstance<VibeniteMeshAsset>();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            asset.BuildFrom(mesh);
            sw.Stop();

            string srcPath = AssetDatabase.GetAssetPath(mesh);
            string dir = string.IsNullOrEmpty(srcPath) ? "Assets" : System.IO.Path.GetDirectoryName(srcPath);
            string path = AssetDatabase.GenerateUniqueAssetPath($"{dir}/{mesh.name}_Vibenite.asset");
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();

            Debug.Log($"[Vibenite] Baked '{mesh.name}': {asset.sourceTriangles:N0} tris -> " +
                      $"{asset.totalClusters:N0} clusters, {asset.lodLevels} LOD levels " +
                      $"in {sw.ElapsedMilliseconds} ms -> {path}");
            Selection.activeObject = asset;
        }
    }
}
#endif
