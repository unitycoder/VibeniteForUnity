using System;
using System.Collections.Generic;
using UnityEngine;

namespace VibeniteURP
{
    /// <summary>
    /// Compact quadric-error-metric simplifier using *half-edge collapses*
    /// (a vertex is always collapsed onto an existing vertex, never onto a new
    /// position). This means all LOD levels reference the original vertex pool,
    /// which keeps the GPU data layout trivial.
    ///
    /// Vertices in 'locked' are never removed — the Vibenite builder locks the
    /// boundary vertices of each cluster group so neighbouring groups stay
    /// crack-free at the same LOD level.
    /// </summary>
    public static class MeshSimplifier
    {
        struct Quadric
        {
            public double xx, xy, xz, xw, yy, yz, yw, zz, zw, ww;

            public static Quadric FromPlane(double a, double b, double c, double d, double w)
            {
                Quadric q;
                q.xx = a * a * w; q.xy = a * b * w; q.xz = a * c * w; q.xw = a * d * w;
                q.yy = b * b * w; q.yz = b * c * w; q.yw = b * d * w;
                q.zz = c * c * w; q.zw = c * d * w;
                q.ww = d * d * w;
                return q;
            }

            public void Add(in Quadric o)
            {
                xx += o.xx; xy += o.xy; xz += o.xz; xw += o.xw;
                yy += o.yy; yz += o.yz; yw += o.yw;
                zz += o.zz; zw += o.zw; ww += o.ww;
            }

            public double Eval(Vector3 v)
            {
                double x = v.x, y = v.y, z = v.z;
                return xx * x * x + 2 * xy * x * y + 2 * xz * x * z + 2 * xw * x
                     + yy * y * y + 2 * yz * y * z + 2 * yw * y
                     + zz * z * z + 2 * zw * z
                     + ww;
            }
        }

        struct Candidate
        {
            public float cost;
            public int from; // removed
            public int to;   // kept
        }

        /// <summary>
        /// Simplifies 'tris' (triples of indices into 'positions') in place down to
        /// 'targetTris' triangles if possible. Returns the object-space error
        /// (max distance-ish metric, sqrt of max quadric cost) introduced.
        /// </summary>
        public static float Simplify(Vector3[] positions, List<int> tris, HashSet<int> locked, int targetTris)
        {
            double maxError = 0;
            var quadrics = new Dictionary<int, Quadric>(tris.Count);

            // --- initial per-vertex quadrics (area-weighted plane quadrics) ---
            for (int t = 0; t < tris.Count; t += 3)
            {
                int i0 = tris[t], i1 = tris[t + 1], i2 = tris[t + 2];
                Vector3 p0 = positions[i0], p1 = positions[i1], p2 = positions[i2];
                Vector3 n = Vector3.Cross(p1 - p0, p2 - p0);
                float area2 = n.magnitude;
                if (area2 < 1e-12f) continue;
                Vector3 un = n / area2;
                double d = -Vector3.Dot(un, p0);
                var q = Quadric.FromPlane(un.x, un.y, un.z, d, area2 * 0.5f);
                Accumulate(quadrics, i0, q);
                Accumulate(quadrics, i1, q);
                Accumulate(quadrics, i2, q);
            }

            var candidates = new List<Candidate>(tris.Count);
            var touched = new HashSet<int>();
            var adjacency = new Dictionary<int, List<int>>(); // vertex -> triangle start offsets

            int guard = 0;
            while (TriCount(tris) > targetTris && guard++ < 64)
            {
                // --- rebuild adjacency ---
                adjacency.Clear();
                for (int t = 0; t < tris.Count; t += 3)
                {
                    if (tris[t] < 0) continue;
                    AddAdj(adjacency, tris[t], t);
                    AddAdj(adjacency, tris[t + 1], t);
                    AddAdj(adjacency, tris[t + 2], t);
                }

                // --- gather collapse candidates ---
                candidates.Clear();
                for (int t = 0; t < tris.Count; t += 3)
                {
                    if (tris[t] < 0) continue;
                    AddEdgeCandidates(candidates, quadrics, positions, locked, tris[t], tris[t + 1]);
                    AddEdgeCandidates(candidates, quadrics, positions, locked, tris[t + 1], tris[t + 2]);
                    AddEdgeCandidates(candidates, quadrics, positions, locked, tris[t + 2], tris[t]);
                }
                candidates.Sort((a, b) => a.cost.CompareTo(b.cost));

                // --- greedy non-conflicting collapses ---
                touched.Clear();
                int collapsed = 0;
                int remaining = TriCount(tris);

                foreach (var c in candidates)
                {
                    if (remaining <= targetTris) break;
                    if (touched.Contains(c.from) || touched.Contains(c.to)) continue;
                    if (!adjacency.TryGetValue(c.from, out var fromTris)) continue;

                    // normal-flip rejection
                    if (WouldFlip(positions, tris, fromTris, c.from, c.to)) continue;

                    // perform collapse: rewrite triangles around 'from'
                    int removedTris = 0;
                    foreach (int t in fromTris)
                    {
                        if (tris[t] < 0) continue;
                        int a = tris[t], b = tris[t + 1], d = tris[t + 2];
                        if (a == c.from) a = c.to;
                        if (b == c.from) b = c.to;
                        if (d == c.from) d = c.to;
                        if (a == b || b == d || d == a)
                        {
                            tris[t] = -1; // degenerate -> delete
                            removedTris++;
                        }
                        else
                        {
                            tris[t] = a; tris[t + 1] = b; tris[t + 2] = d;
                        }
                    }

                    if (removedTris == 0) continue; // nothing actually merged

                    var qf = quadrics.TryGetValue(c.from, out var qa) ? qa : default;
                    if (quadrics.TryGetValue(c.to, out var qb)) { qb.Add(qf); quadrics[c.to] = qb; }
                    else quadrics[c.to] = qf;

                    touched.Add(c.from);
                    touched.Add(c.to);
                    maxError = Math.Max(maxError, c.cost);
                    remaining -= removedTris;
                    collapsed++;
                }

                Compact(tris);
                if (collapsed == 0) break; // fully locked / no safe collapses left
            }

            return Mathf.Sqrt((float)Math.Max(maxError, 0));
        }

        static void AddEdgeCandidates(List<Candidate> list, Dictionary<int, Quadric> quadrics,
                                      Vector3[] positions, HashSet<int> locked, int a, int b)
        {
            if (a == b) return;
            TryAdd(list, quadrics, positions, locked, a, b);
            TryAdd(list, quadrics, positions, locked, b, a);
        }

        static void TryAdd(List<Candidate> list, Dictionary<int, Quadric> quadrics,
                           Vector3[] positions, HashSet<int> locked, int from, int to)
        {
            if (locked.Contains(from)) return; // locked vertices may never be removed
            quadrics.TryGetValue(from, out var qa);
            quadrics.TryGetValue(to, out var qb);
            qa.Add(qb);
            float cost = (float)Math.Max(qa.Eval(positions[to]), 0);
            list.Add(new Candidate { cost = cost, from = from, to = to });
        }

        static bool WouldFlip(Vector3[] positions, List<int> tris, List<int> fromTris, int from, int to)
        {
            Vector3 newPos = positions[to];
            foreach (int t in fromTris)
            {
                if (tris[t] < 0) continue;
                int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                if (a == to || b == to || c == to) continue; // will be deleted, ignore

                Vector3 p0 = positions[a], p1 = positions[b], p2 = positions[c];
                Vector3 before = Vector3.Cross(p1 - p0, p2 - p0);

                Vector3 q0 = a == from ? newPos : p0;
                Vector3 q1 = b == from ? newPos : p1;
                Vector3 q2 = c == from ? newPos : p2;
                Vector3 after = Vector3.Cross(q1 - q0, q2 - q0);

                if (Vector3.Dot(before, after) <= 1e-12f) return true;
            }
            return false;
        }

        static void Accumulate(Dictionary<int, Quadric> map, int v, in Quadric q)
        {
            if (map.TryGetValue(v, out var existing)) { existing.Add(q); map[v] = existing; }
            else map[v] = q;
        }

        static void AddAdj(Dictionary<int, List<int>> adj, int v, int tri)
        {
            if (!adj.TryGetValue(v, out var list)) { list = new List<int>(8); adj[v] = list; }
            list.Add(tri);
        }

        static int TriCount(List<int> tris)
        {
            int n = 0;
            for (int t = 0; t < tris.Count; t += 3)
                if (tris[t] >= 0) n++;
            return n;
        }

        static void Compact(List<int> tris)
        {
            int w = 0;
            for (int t = 0; t < tris.Count; t += 3)
            {
                if (tris[t] < 0) continue;
                tris[w] = tris[t]; tris[w + 1] = tris[t + 1]; tris[w + 2] = tris[t + 2];
                w += 3;
            }
            tris.RemoveRange(w, tris.Count - w);
        }
    }
}
