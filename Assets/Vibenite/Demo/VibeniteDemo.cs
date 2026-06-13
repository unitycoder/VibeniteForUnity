using UnityEngine;
using UnityEngine.Rendering;

namespace VibeniteURP
{
    /// <summary>
    /// Drop on an empty GameObject in an empty URP scene (with the
    /// VibeniteRenderFeature added to your URP Renderer) and press Play.
    ///
    /// Generates a high-poly displaced icosphere (no external assets needed),
    /// bakes it into a Vibenite cluster hierarchy once, and spawns a grid of
    /// instances sharing that data. Walk away from the grid and watch the
    /// cluster colors coarsen — that's the GPU LOD cut working.
    /// </summary>
    public class VibeniteDemo : MonoBehaviour
    {
        [Header("Source mesh")]
        [Range(4, 7)]
        [Tooltip("6 = ~82k tris, 7 = ~328k tris per object. Bake time grows with this.")]
        public int icosphereSubdivisions = 6;
        public float noiseAmplitude = 0.25f;
        public float noiseFrequency = 2.5f;

        [Header("Scene")]
        public int gridSize = 6;
        public float spacing = 3.5f;
        public bool attachFreeCamera = true;

        VibeniteMeshAsset asset;
        uint[] argsReadback = new uint[4];
        int visibleClusters;
        float readbackTimer;

        void Start()
        {
            // --- build the dense source mesh ---
            var sw = System.Diagnostics.Stopwatch.StartNew();
            GenerateIcosphere(icosphereSubdivisions, out Vector3[] verts, out int[] tris);
            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 v = verts[i];
                float n = Fbm(v * noiseFrequency);
                verts[i] = v * (1f + n * noiseAmplitude);
            }
            sw.Stop();
            Debug.Log($"[VibeniteDemo] Generated source mesh: {tris.Length / 3} tris in {sw.ElapsedMilliseconds} ms");

            // --- bake the cluster hierarchy ---
            asset = ScriptableObject.CreateInstance<VibeniteMeshAsset>();
            asset.name = "DemoRock (Vibenite)";
            sw.Restart();
            asset.BuildFrom(verts, tris);
            sw.Stop();
            Debug.Log($"[VibeniteDemo] Baked {asset.totalClusters} clusters / {asset.lodLevels} LOD levels in {sw.ElapsedMilliseconds} ms");

            // --- spawn the grid ---
            float half = (gridSize - 1) * spacing * 0.5f;
            for (int x = 0; x < gridSize; x++)
            for (int z = 0; z < gridSize; z++)
            {
                var go = new GameObject($"Vibenite_{x}_{z}");
                go.transform.SetParent(transform, false);
                go.transform.localPosition = new Vector3(x * spacing - half, 0, z * spacing - half);
                go.transform.localRotation = Quaternion.Euler(0, (x * 73 + z * 31) % 360, 0);
                go.AddComponent<VibeniteObject>().asset = asset;
            }

            // --- camera + light convenience ---
            var cam = Camera.main;
            if (cam == null)
            {
                var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
                cam = camGo.AddComponent<Camera>();
            }
            cam.transform.position = new Vector3(0, gridSize * 1.2f, -gridSize * spacing * 0.8f);
            cam.transform.LookAt(Vector3.zero);
            if (attachFreeCamera && cam.GetComponent<VibeniteFreeCamera>() == null)
                cam.gameObject.AddComponent<VibeniteFreeCamera>();

            if (FindAnyObjectByType<Light>() == null)
            {
                var lightGo = new GameObject("Directional Light");
                var l = lightGo.AddComponent<Light>();
                l.type = LightType.Directional;
                lightGo.transform.rotation = Quaternion.Euler(50, -30, 0);
            }
        }

        void Update()
        {
            // periodically read back how many clusters survived culling (debug only)
            readbackTimer += Time.unscaledDeltaTime;
            var args = VibeniteRenderSystem.Instance.ArgsBuffer;
            if (readbackTimer > 0.25f && args != null)
            {
                readbackTimer = 0;
                AsyncGPUReadback.Request(args, req =>
                {
                    if (!req.hasError)
                        visibleClusters = (int)req.GetData<uint>()[1];
                });
            }
        }

        void OnGUI()
        {
            if (asset == null) return;
            int instances = gridSize * gridSize;
            long fullSceneTris = (long)asset.sourceTriangles * instances;
            GUI.Label(new Rect(10, 10, 700, 120),
                $"Vibenite prototype — {instances} objects × {asset.sourceTriangles:N0} tris " +
                $"(scene total {fullSceneTris:N0} tris at full detail)\n" +
                $"Clusters in hierarchy: {asset.totalClusters * instances:N0}  |  " +
                $"Visible after GPU cull/LOD: {visibleClusters:N0}  (~{visibleClusters * 128L:N0} tris drawn)\n" +
                "WASD + RMB-drag to fly. Move far away to watch clusters coarsen.");
        }

        // ------------------------------------------------------------------ mesh gen

        static float Fbm(Vector3 p)
        {
            float sum = 0, amp = 0.5f, freq = 1f;
            for (int i = 0; i < 4; i++)
            {
                sum += amp * (Mathf.PerlinNoise(p.x * freq + p.z * 0.7f * freq, p.y * freq - p.z * 0.3f * freq) - 0.5f) * 2f;
                amp *= 0.5f;
                freq *= 2.1f;
            }
            return sum;
        }

        static void GenerateIcosphere(int subdivisions, out Vector3[] outVerts, out int[] outTris)
        {
            float t = (1f + Mathf.Sqrt(5f)) / 2f;
            var verts = new System.Collections.Generic.List<Vector3>
            {
                new Vector3(-1,  t,  0), new Vector3( 1,  t,  0), new Vector3(-1, -t,  0), new Vector3( 1, -t,  0),
                new Vector3( 0, -1,  t), new Vector3( 0,  1,  t), new Vector3( 0, -1, -t), new Vector3( 0,  1, -t),
                new Vector3( t,  0, -1), new Vector3( t,  0,  1), new Vector3(-t,  0, -1), new Vector3(-t,  0,  1)
            };
            for (int i = 0; i < verts.Count; i++) verts[i] = verts[i].normalized;

            int[] tris =
            {
                0,11,5, 0,5,1, 0,1,7, 0,7,10, 0,10,11,
                1,5,9, 5,11,4, 11,10,2, 10,7,6, 7,1,8,
                3,9,4, 3,4,2, 3,2,6, 3,6,8, 3,8,9,
                4,9,5, 2,4,11, 6,2,10, 8,6,7, 9,8,1
            };

            var midpointCache = new System.Collections.Generic.Dictionary<ulong, int>();
            int GetMidpoint(int a, int b)
            {
                ulong key = a < b ? ((ulong)(uint)a << 32) | (uint)b : ((ulong)(uint)b << 32) | (uint)a;
                if (midpointCache.TryGetValue(key, out int idx)) return idx;
                idx = verts.Count;
                verts.Add(((verts[a] + verts[b]) * 0.5f).normalized);
                midpointCache[key] = idx;
                return idx;
            }

            for (int s = 0; s < subdivisions; s++)
            {
                var next = new int[tris.Length * 4];
                int w = 0;
                for (int i = 0; i < tris.Length; i += 3)
                {
                    int a = tris[i], b = tris[i + 1], c = tris[i + 2];
                    int ab = GetMidpoint(a, b), bc = GetMidpoint(b, c), ca = GetMidpoint(c, a);
                    next[w++] = a;  next[w++] = ab; next[w++] = ca;
                    next[w++] = b;  next[w++] = bc; next[w++] = ab;
                    next[w++] = c;  next[w++] = ca; next[w++] = bc;
                    next[w++] = ab; next[w++] = bc; next[w++] = ca;
                }
                tris = next;
            }

            outVerts = verts.ToArray();
            outTris = tris;
        }
    }

    /// <summary>Minimal fly camera for the demo.</summary>
    public class VibeniteFreeCamera : MonoBehaviour
    {
        public float moveSpeed = 8f;
        public float lookSpeed = 3f;
        float yaw, pitch;

        void Start()
        {
            Vector3 e = transform.eulerAngles;
            yaw = e.y;
            pitch = e.x;
        }

        void Update()
        {
            if (Input.GetMouseButton(1))
            {
                yaw += Input.GetAxis("Mouse X") * lookSpeed;
                pitch -= Input.GetAxis("Mouse Y") * lookSpeed;
                pitch = Mathf.Clamp(pitch, -89, 89);
                transform.rotation = Quaternion.Euler(pitch, yaw, 0);
            }

            float speed = moveSpeed * (Input.GetKey(KeyCode.LeftShift) ? 5f : 1f);
            Vector3 move = Vector3.zero;
            if (Input.GetKey(KeyCode.W)) move += transform.forward;
            if (Input.GetKey(KeyCode.S)) move -= transform.forward;
            if (Input.GetKey(KeyCode.A)) move -= transform.right;
            if (Input.GetKey(KeyCode.D)) move += transform.right;
            if (Input.GetKey(KeyCode.E)) move += Vector3.up;
            if (Input.GetKey(KeyCode.Q)) move -= Vector3.up;
            transform.position += move * (speed * Time.deltaTime);
        }
    }
}
