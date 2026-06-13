### DISCLAIMER

This is one shot coded with Fable 5.

# Vibenite Virtual Geometry for Unity 6 URP (RenderGraph)

A complete working prototype of UE5 Vibenite's core idea — cluster-based virtual
geometry with GPU-driven culling and continuous, crack-aware LOD — built on the
**Unity 6 URP RenderGraph API** (`RecordRenderGraph`, *not* compatibility mode).

Inspired by the Chinese-language "Unity 实现 Virtual Geometry" / "Unity
Vibenite for Mobile" article series on Zhihu, which take the same route: meshlets
of 128 triangles, a simplification DAG, compute-shader cluster culling, and one
`DrawProceduralIndirect` to draw everything.

## Requirements

- Unity **6000.0+** with **URP 17+**
- RenderGraph enabled (default in Unity 6 — make sure *Project Settings →
  Graphics → Render Graph → Compatibility Mode* is **OFF**; this code only
  implements `RecordRenderGraph`)
- A platform with compute shaders + `SV_InstanceID` in procedural draws
  (DX11/DX12, Vulkan, Metal)

## Setup (5 minutes)

1. Copy `Assets/Vibenite` into your URP project.
2. Select your **URP Renderer asset** (e.g. `PC_Renderer`), click
   **Add Renderer Feature → Vibenite Render Feature**.
3. Assign on the feature:
   - **Cull Shader** → `Assets/Vibenite/Shaders/VibeniteCull.compute`
   - **Draw Shader** → `Hidden/Vibenite/Draw` (`VibeniteDraw.shader`)
4. Run the demo: create an empty GameObject in an empty scene, add the
   **VibeniteDemo** component, press Play. It procedurally generates a ~82k-tri
   displaced sphere (subdiv 6; set 7 for ~328k tris), bakes the cluster
   hierarchy, and spawns a 6×6 grid of instances. Fly with WASD + RMB.

To use your own meshes:

- **Editor**: select a Mesh asset → right-click → *Vibenite → Bake Cluster
  Hierarchy From Mesh* → assign the produced `VibeniteMeshAsset` to a
  **VibeniteObject** component, or
- **Runtime**: add a **VibeniteObject**, assign `sourceMesh`; it bakes on first
  enable (cached per mesh). Note: the mesh must be Read/Write enabled.

Tuning: `Error Threshold Pixels` on the render feature is the Vibenite quality
dial — 1 px means "no triangle should ever be more than ~1 pixel wrong".
Raise it to draw far fewer clusters. `Debug Cluster Colors` shows the cut.

## How it works

### Bake (VibeniteBuilder + MeshSimplifier)

1. **Weld** the source mesh and recompute smooth normals.
2. **Clusterize** triangles into meshlets of ≤128 tris, ordered along a Morton
   curve for spatial locality (the simple stand-in for METIS partitioning).
3. **Build the LOD hierarchy**, level by level:
   - group ~4 spatially adjacent clusters,
   - **lock the group's boundary vertices** (edges used by only one triangle
     inside the group), so neighbouring groups always meet exactly,
   - QEM-simplify the merged interior to ~50% triangles using *half-edge
     collapses* (vertices only ever collapse onto existing vertices, so every
     LOD level indexes the same vertex pool),
   - re-split the result into ~2 coarser clusters,
   - record a **monotonic error** (`max(childError, simplifyError)·1.0001`)
     plus the **group bounds**, stored on both the children (as
     `parentError`/`parentBounds`) and the new clusters (as their own
     `error`/`lodBounds`).
4. Serialize everything into a flat `VibeniteMeshAsset`: all clusters of all
   levels side by side, no tree structure needed at runtime.

### Runtime (VibeniteRenderFeature, per camera, via RenderGraph)

1. **Compute pass** (`VibeniteCull.compute`) — one thread per
   (instance, cluster) pair across *all* LOD levels:
   - project `error` and `parentError` to screen pixels using their stored
     group bounds,
   - keep the cluster iff `selfError ≤ threshold < parentError`. Because
     parent and child evaluate the *same* group error with the *same* group
     bounds, exactly one level of every region survives — a crack-free cut
     selected fully in parallel, no tree traversal;
   - frustum-cull the survivors, append to `_Visible`, bump the indirect
     instance count with `InterlockedAdd`.
2. **Raster pass** — a single
   `DrawProceduralIndirect(MeshTopology.Triangles, args)`:
   384 vertices per instance, where `SV_InstanceID` selects the visible
   cluster and `SV_VertexID` pulls indices/positions/normals from structured
   buffers. Padding triangles of partial clusters output NaN positions and
   are discarded by the rasterizer. Depth writes into the camera depth target
   so Vibenite geometry composes correctly with regular URP objects.

## What's intentionally NOT here (prototype scope)

- **Occlusion culling** (Vibenite's two-pass Hi-Z) — only frustum culling.
- **Software rasterization** of pixel-sized triangles and the visibility
  buffer / deferred material pass — this draws via the hardware rasterizer
  with a simple lit shader.
- **Streaming** — all clusters of all LODs are resident in GPU memory.
- **METIS-quality graph partitioning** — Morton-order clustering is cruder, so
  cluster shapes and the simplification DAG are less optimal than Vibenite's.
- Shadow casting, motion vectors, lightmaps, SRP batcher materials.

Each of these is an additive extension on top of the same data layout, which
is why this layout (flat cluster list + monotonic errors + indirect draws)
is the right skeleton to grow from.

## File map

```
Assets/Vibenite/
├── Runtime/
│   ├── VibeniteMeshAsset.cs      # baked data: clusters + vertex/index pools
│   ├── VibeniteBuilder.cs        # weld → clusterize → group/simplify DAG
│   ├── MeshSimplifier.cs       # QEM half-edge-collapse w/ boundary locking
│   ├── VibeniteRenderSystem.cs   # GPU buffers + object registry (singleton)
│   ├── VibeniteObject.cs         # per-object component
│   └── VibeniteRenderFeature.cs  # URP RenderGraph compute + raster passes
├── Shaders/
│   ├── VibeniteCull.compute      # frustum cull + screen-error LOD cut
│   └── VibeniteDraw.shader       # vertex-pulling indirect draw, lit + debug
├── Editor/
│   └── VibeniteBakeMenu.cs       # Assets → Vibenite → Bake From Mesh
└── Demo/
    └── VibeniteDemo.cs           # procedural high-poly demo scene + fly cam
```
