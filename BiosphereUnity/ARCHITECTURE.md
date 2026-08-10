# Biosphere — Unity 2D graphics pipeline architecture

WorldBox-style top-down pixel sandbox, with the existing Python biosphere
simulation (genomes, evolution, weather, day/night) ported to the sim layer.

> **Status: written, not compiled.** There is no Unity install in this session,
> so every file here has been syntax-checked with a C# parser but never built.
> Expect to fix a handful of API-version mismatches on first import — the likely
> candidates are listed under [First-build checklist](#first-build-checklist).

---

## 1. The central decision

Everything below follows from one choice: **the GPU does almost nothing.**

| Concern | Conventional Unity 2D | What this project does | Why |
|---|---|---|---|
| Terrain | `Tilemap` + `TilemapRenderer` | One `Texture2D`, one quad, point filter | 576×576 = 331,776 tiles. A Tilemap chunk-remeshes on every edit. A texture edit is a texel write with an O(dirty-rect) upload. |
| Creatures | `GameObject` + `SpriteRenderer` each | One `GraphicsBuffer` + `Graphics.RenderPrimitives` | 20,000 SpriteRenderers = 20,000 Transforms, culling entries and sorting evaluations. Instanced = 1 draw call, 0 Transforms. |
| Sorting | `SortingGroup` / `sortingOrder` | Per-instance depth written to clip Z, cutout alpha | CPU sort of 20k sprites per frame is pure waste. The depth buffer does it free. |
| Particles | `ParticleSystem` per effect | One Burst-simulated pool, one draw call | 200 simultaneous fires would be 200 ParticleSystem components. Here they're 200 emit calls into one pool. |
| Day/night | Re-tint the tile colours | Three shader uniforms | A sunset should cost zero CPU and zero uploads. |

The result is that adding entities costs **CPU simulation time and buffer
upload bytes**, and nothing else. That's the requested "CPU-bound over
GPU-bound" tradeoff, made structural rather than aspirational.

---

## 2. Folder structure

```
BiosphereUnity/
├── Packages/manifest.json          Burst, Collections, Mathematics, URP
└── Assets/
    ├── Scripts/
    │   ├── Biosphere.asmdef        Own assembly -> fast incremental compiles
    │   ├── Core/
    │   │   ├── WorldConfig.cs      ScriptableObject: grid size, PPU, all sim constants
    │   │   └── GameBootstrap.cs    Composition root; the frame loop
    │   ├── Sim/
    │   │   ├── Environment/
    │   │   │   └── WorldGrid.cs    Terrain, decor, moisture, clouds, clock, weather
    │   │   ├── Life/
    │   │   │   ├── Genome.cs       5-trait heritable struct + GeneTable metadata
    │   │   │   ├── LifeGrid.cs     Population: occupancy grid + packed entity SoA
    │   │   │   └── LifeJobs.cs     Burst jobs: metabolism, instance-buffer build
    │   │   └── Data/
    │   │       └── CellLogger.cs   Per-cell + aggregate longitudinal logging -> CSV
    │   ├── Render/
    │   │   ├── TerrainRenderer.cs  Texture terrain, dirty-rect repaint, Burst paint job
    │   │   └── InstancedSpriteBatch.cs  Per-layer GPU instancing
    │   ├── Particles/
    │   │   └── PixelParticleSystem.cs   Pooled Burst particles, 1 draw call
    │   ├── CameraRig/
    │   │   └── PixelCameraController.cs Ortho, derived size, zoom snap, texel snap
    │   ├── UI/
    │   │   └── DashboardHud.cs     Toolbar, cell inspector, live trait charts
    │   └── Util/
    │       └── FieldMath.cs        Gaussian blur, rank-normalise (numpy ports)
    ├── Shaders/
    │   ├── TerrainUnlit.shader           Terrain + day/night + cloud + rain
    │   ├── PixelSpriteInstanced.shader   All sprites; depth-based Y sorting
    │   └── PixelParticle.shader          Untextured additive pixel squares
    ├── Art/Sprites/                Put the 24×28 atlas here
    └── Settings/                   URP asset, WorldConfig asset
```

---

## 3. Rendering configuration

### 3.1 Sprite import settings (Art/Sprites)

Wrong values here cause 90% of "why is my pixel art blurry" problems.

| Setting | Value | Consequence if wrong |
|---|---|---|
| Texture Type | Sprite (2D and UI) | — |
| Sprite Mode | Multiple (atlas) | One atlas = one draw call |
| **Pixels Per Unit** | **16** (must equal `WorldConfig.PixelsPerUnit`) | Mismatch = sprites the wrong size relative to tiles |
| Mesh Type | Full Rect | Tight meshes break instanced quad UVs |
| **Filter Mode** | **Point (no filter)** | Bilinear = blurry at every zoom |
| **Compression** | **None** | DXT/ETC introduces colour banding on flat pixel art |
| **Generate Mip Maps** | **Off** | Mips = mush when zoomed out |
| Max Size | ≥ atlas dimension | Silent downscale = destroyed art |
| sRGB | On | — |
| Alpha Is Transparency | On | — |
| Wrap Mode | Clamp | Bleeding between atlas cells |
| Extrude Edges | 0 | Atlas UVs are computed, not from sprite rects |

### 3.2 Project Settings

All of these are applied in code by **`Biosphere → 0. Apply Pixel-Art Project
Settings`**. The table is here so you know what it did and why.

| Location | Setting | Value | Why |
|---|---|---|---|
| Player → Other | Color Space | **Linear** | Consistent tint maths in shaders |
| Player → Other | Auto Graphics API | Off; DX11/Vulkan/Metal | `target 4.5` needs SM4.5+ for `StructuredBuffer` in vertex |
| Quality (all levels) | Anti Aliasing | **Disabled** | MSAA destroys hard pixel edges |
| Quality (all levels) | Anisotropic Textures | **Disabled** | — |
| Quality (all levels) | Texture Quality | Full Res | — |
| Quality (all levels) | Shadows | Disabled | Top-down 2D |
| Quality (all levels) | V Sync Count | Every V Blank | Prevents tearing during pans |
| Graphics | Scriptable Render Pipeline | **None (empty)** | See §3.3 |
| Time | Fixed Timestep | 0.02 | Sim runs on `Update`, not `FixedUpdate` — see §6 |

### 3.3 Render pipeline: Built-in, NOT URP

**Use the "2D (Built-In Render Pipeline)" template.**

The three shaders here are built-in-pipeline CG — they use `UnityCG.cginc`,
`UnityObjectToClipPos` and `UNITY_MATRIX_VP`, and carry no
`"RenderPipeline"="UniversalPipeline"` tag. Under URP or HDRP they render
**magenta**. `Biosphere → Check Render Pipeline` verifies this and errors loudly
if an SRP is assigned.

This isn't a compromise. URP's value in 2D is the 2D lighting system and the
SRP Batcher, and this project uses neither: lighting is two shader uniforms, and
batching is irrelevant when the whole world is ~5 draw calls. Built-in is fewer
moving parts for identical output.

If you later want URP anyway (for 2D lights or post-processing), the port is
mechanical: swap `CGPROGRAM`/`ENDCG` for `HLSLPROGRAM`/`ENDHLSL`, replace
`UnityCG.cginc` with `Core.hlsl`, `UnityObjectToClipPos` with
`TransformObjectToHClip`, and add the pipeline tag.

### 3.4 Camera

Set entirely from code by `PixelCameraController` — do not hand-edit these in
the inspector, they get overwritten:

| Property | Value |
|---|---|
| Projection | Orthographic |
| Rotation | Identity (strict bird's-eye) |
| Orthographic Size | `Screen.height / (2 × PixelsPerUnit × zoom)` — **derived, never typed** |
| Near / Far | −100 / +100 (so instance depth offsets have room) |
| Allow MSAA | Off |
| Allow HDR | Off |
| Occlusion Culling | Off |

Three rules keep pixels crisp, all enforced in `PixelCameraController`:

1. **Ortho size is derived**, never typed. Hand-set sizes put a fractional
   number of screen pixels on each texel.
2. **Zoom snaps to a step list** (`0.25, 0.5, 1, 2, 3, 4, 6, 8, 12, 16`). A
   1.37× zoom gives some texels 1 screen pixel and their neighbours 2 — the
   classic "wobbly pixels".
3. **Position snaps to the texel grid** (`1/PixelsPerUnit`) for rendering while
   the true float position is kept separately, so panning is still smooth.

---

## 4. Grid & tile architecture

`WorldGrid` holds ~9 flat `NativeArray`s of length `GridW × GridH`, indexed
`y * GridW + x`. Struct-of-arrays, Burst-addressable, no GameObjects.

Memory at each target size (all persistent fields combined, ≈ 21 bytes/tile):

| Map | Tiles | Field memory | Terrain texture (RGBA32) |
|---|---|---|---|
| 256×256 | 65,536 | ~1.4 MB | 256 KB |
| 384×384 | 147,456 | ~3.1 MB | 576 KB |
| 512×512 | 262,144 | ~5.5 MB | 1.0 MB |
| 576×576 | 331,776 | ~7.0 MB | 1.3 MB |

Changing map size is one field on the `WorldConfig` asset. Nothing else.

### Instant tile updates

`WorldGrid.ApplyElevationBrush(cx, cy, radius, delta)` is the model for every
terrain-mutating tool:

1. Write the affected `Elevation` texels.
2. `ClassifyRegion(rect)` — recompute `Terrain` and `Habitable` for that rect only.
3. Return the rect; the caller passes it to `TerrainRenderer.MarkDirty(rect)`.
4. `LateUpdate` unions all dirty rects, runs `PaintTerrainJob` over just those
   rows, and uploads via `Graphics.CopyTexture` on a rect-sized scratch texture.

A 9×9 brush therefore touches 81 texels, not 331,776. Full-map repaints
(worldgen, biome sweeps) automatically switch to `Texture2D.Apply()` when the
dirty rect exceeds a quarter of the map, because at that point the blit
bookkeeping costs more than a straight reupload.

**Biome spreading** slots into the same pattern: run a spread pass over a
region, accumulate a dirty rect, mark it. The renderer never needs to know what
changed, only where.

---

## 5. Micro-scale sprites & sorting

**Canvas size:** 24×24 to 28×28 px. With `PixelsPerUnit = 16`, a 24 px sprite is
1.5 tiles tall — it reads as a small figure standing on a tile rather than
filling it. Author on a 24×28 canvas with the character's feet at the bottom
edge; `GameBootstrap` offsets the quad up by `size * 0.25` so the visual base
sits on the tile centre.

**Atlas:** a single 16×16-cell grid atlas. `SpriteInstance.AtlasDepth.x` is the
cell index; the vertex shader converts index → (col,row) → UV. Adding an art
asset is adding a cell and a constant, not a prefab.

### Sorting

Sorting is resolved by the **depth buffer**, not by any CPU sort:

```
depth = layerBase + (gridHeight - worldY)
clipZ += depth * _DepthScale * w
```

with `ZWrite On`, `ZTest LEqual`, and **cutout** alpha (`clip()`, not blending).
Cutout is what makes depth sorting valid — blended sprites would need
back-to-front order.

Layer bands are spaced 1000 apart so a 576-tall map's Y range can never bleed
across a band:

| Band | Base | Contents |
|---|---|---|
| TerrainBase | 0 | the terrain quad |
| TerrainDecal | 1000 | roads, farmland, scorch marks, borders |
| GroundDecor | 2000 | rocks, bushes, dropped items — Y-sorted |
| Structures | 3000 | buildings, walls, **bridges** — Y-sorted |
| Actors | 4000 | creatures/cells — Y-sorted |
| Airborne | 5000 | birds, projectiles |
| Particles | 6000 | fire, explosions, magic |
| WeatherFx | 7000 | rain streaks, overlays |
| UiWorldSpace | 8000 | selection rings, world-anchored tooltips |

So a creature always draws over a building in the band below it, and *within*
the Actors band, a creature standing lower on screen draws in front of one
standing higher — which is the correct top-down read.

**Bridges** are the classic edge case: put the bridge deck in `Structures` and
let actors in `Actors` always win. If you want actors to walk *under* an arch,
split the sprite into a deck piece (Structures) and an arch piece (Airborne).

**Genuinely translucent sprites** (ghosts, glass) can't use depth sorting. Draw
them in a second pass with `ZWrite Off` in the Transparent queue, after this one.

---

## 6. Performance model

Frame budget at 60 fps is 16.6 ms. Rough allocation at 576×576 / ~8,000 actors:

| Stage | Cost | Notes |
|---|---|---|
| `WorldGrid.Step` | ~1–2 ms | Moisture is the only full-grid pass; parallelise it if it shows up |
| `LifeGrid.Step` phase 1 (metabolism) | ~0.2 ms | Burst, parallel, per-entity |
| `LifeGrid.Step` phase 2 (deaths) | ~0.1 ms | Serial swap-remove |
| `LifeGrid.Step` phase 3 (reproduction) | ~0.5 ms | Serial — mutates shared occupancy |
| Instance buffer build | ~0.1 ms | Burst, parallel, writes straight to upload array |
| Buffer upload | ~0.3 MB | 8,000 × 40 bytes |
| Particle sim | ~0.3 ms | 30k particles, Burst |
| **Draw calls** | **~5 total** | terrain + decor + actors + particles + UI |

Known scaling limits and their fixes:

- **Reproduction is serial.** It mutates the occupancy grid, so it can't be
  parallelised naively. If it becomes the bottleneck, partition the map into
  tiles with a one-cell halo and run non-adjacent tiles in parallel.
- **`Compact()` in the particle system is serial** over the live set. Fine to
  ~50k; beyond that, switch to a parallel stream-compaction.
- **Moisture updates the whole grid every step.** At 576² that's 331k float ops
  per tick. Convert to an `IJobParallelFor` when it shows up in a profile.
- **The sim runs in `Update`**, so a speed multiplier scales `deltaTime` rather
  than running more ticks. That keeps frame cost flat at x64 but means x64 is
  coarser, not just faster. If you need step-accurate fast-forward, run N
  fixed-size sub-steps per frame with a wall-clock budget guard.

### Particle system

Three properties give it its cost profile:

- **No per-particle GameObjects.** 200 fires are 200 emit calls into one pool.
- **No runtime allocation.** The pool is `WorldConfig.MaxParticles` (32,768 by
  default) and never grows; emitting past capacity round-robins over the oldest
  particles rather than allocating. Frame cost stays bounded regardless of how
  much chaos the player causes.
- **No texture bandwidth.** Particles are untextured solid squares. VRAM is the
  instance buffer alone: **32 bytes each, so 32 KB per 1,000 particles.** 32k
  particles is 1 MB total.

Presets: `Fire`, `Explosion`, `Magic`, `Smoke`, `Splash`, `Spark` — all in
`PixelParticleSystem.Emit`, all sharing the pool.

---

## 7. How to run it

### 7.1 Install a Unity Editor

Unity Hub on its own can't open anything — it's a launcher. You need an Editor.

1. Hub → **Installs** → **Install Editor** → pick **2022.3 LTS** or **Unity 6
   (6000.x LTS)**. Either works.
2. Modules: you only need the default **Windows Build Support (IL2CPP)**.
   Skip Android/iOS/WebGL — they're multi-GB and unused here.
3. Wait for the download (~5–8 GB).

### 7.2 Create the project and drop these files in

There's no `ProjectSettings/` folder here — that's machine- and version-specific,
so Unity generates it. This repo is the portable subset: `Assets/` and
`Packages/`.

1. Hub → **Projects** → **New project**.
2. Template: **2D (Built-In Render Pipeline)**. *Not* 2D (URP) — see §3.3.
3. Name it `BiosphereUnity`, pick any location, **Create project**.
4. When it finishes opening, **quit Unity** (the Hub can stay open).
5. In Explorer, open the new project folder. Copy from *this* folder:
   - `Assets/` → over the new project's `Assets/` (merge/overwrite)
   - `Packages/manifest.json` → over the new project's `Packages/manifest.json`
6. Reopen the project from the Hub.

On reopen, Unity downloads Burst, Collections and Mathematics from the package
registry (needs internet, takes a minute), then compiles. Watch the Console: it
should end with no errors.

> **If you'd rather not overwrite `manifest.json`:** skip step 5's second half
> and instead use **Window → Package Manager → + → Add package by name** three
> times: `com.unity.burst`, `com.unity.collections`, `com.unity.mathematics`.
> Same result.

### 7.3 Configure and build the scene

Three menu items, in order:

1. **`Biosphere → 0. Apply Pixel-Art Project Settings`**
   Sets linear colour space, disables MSAA/aniso/shadows/mip-limiting across all
   quality levels. Unity will ask to reimport assets for the colour space change
   — say yes, it takes seconds on a project this size.

2. **`Biosphere → Check Render Pipeline`**
   Should log "Built-in Render Pipeline detected. Correct." If it errors, you
   picked the URP template — easiest fix is to recreate the project with the
   Built-in one.

3. **`Biosphere → 1. Setup Project Scene`**
   Creates `Assets/Settings/WorldConfig.asset` (256×256, PPU 16), generates a
   placeholder sprite atlas with correct import settings, builds and wires every
   GameObject, saves `Assets/Scenes/Biosphere.unity`, and opens it.

Then press **Play**.

Controls: `Space` pause · `+`/`-` speed · `N` new world · `C` cycle colour
overlay · scroll to zoom · middle-drag to pan · WASD/arrows to pan · left-click
applies the active tool. Tools, speed, weather and log export are on the HUD.

> Why the scene is built by code: a hand-written `.unity` file is a GUID
> minefield and would almost certainly import broken. `BiosphereSetup.cs` spawns
> and wires the objects through the real API instead.

### 7.4 Verify the sim before trusting the graphics

**`Biosphere → 2. Run Headless Evolution Test (64x48, 60 days)`**

Runs 60 sim-days with no scene, no rendering and no Play mode, then logs trait
drift to the Console. This isolates "is the port correct?" from "is the render
pipeline wired?" — if the screen is black but this passes, the bug is in
rendering, and vice versa.

**What a pass looks like:** `harvest_rate` up, `metabolism_rate` down. Those two
signs are the load-bearing check. Exact magnitudes will differ from Python
(different RNG and reproduction draw order); the reference range in
`PROJECT_STATUS.md` is harvest +55–113%, metabolism −35–40% over ~45 days at
this world size.

Expect it to also report saturation around day 35–45 and a frozen population
after that. **That is the known behaviour, not a regression** — see §8.

**`Biosphere → 3. Run Headless Perf Probe (256x256, 20 days)`** reports
steps/sec at the real target size, so you can see the CPU cost before adding
rendering on top.

### 7.5 Replacing the placeholder art

`Assets/Art/Sprites/placeholder_atlas.png` is generated, not authored — a
shaded blob, a tree and a rock in cells 0/1/2 of a 16×16 grid. Overwrite it with
real art at the same cell layout and nothing else changes; the atlas index
constants on `GameBootstrap` are the only contract. Keep the import settings in
§3.1 (`Biosphere → Regenerate Placeholder Atlas` re-applies them if you need a
reference).

---

## 8. What carried over from the Python simulation

| Python | C# | Notes |
|---|---|---|
| `environment.py: _gaussian_blur` | `FieldMath.GaussianBlur` | Same separable zero-padded kernel |
| `environment.py: rank-normalise` | `FieldMath.RankNormalize` | Same trick: `WaterLevel = 0.34` ⇒ ~34% water |
| `Biosphere.render()` | `PaintTerrainJob` | Same colour ramp; day/night moved to the shader |
| `sunlight_intensity`, `_sky_tint` | `WorldGrid.SunlightIntensity/SkyTint` | Identical curves |
| weather Markov + cloud scroll | `WorldGrid.UpdateWeather` | Identical transition weights |
| `GENE_DEFAULT/BOUNDS/INIT_STD` | `GeneTable` | Identical values |
| `_mutate_genome` | `GeneTable.Mutate` | Identical: step = parentMutationRate × traitSpan |
| dense `(H,W)` trait arrays | occupancy grid + packed entity SoA | **The one real change** — see below |
| `data_logger.py: CellLogger` | `Sim/Data/CellLogger.cs` | Adds subsampling + local-sunlight column |

**The one architectural change:** Python stored every trait as a dense `(H, W)`
array and used boolean masks. That's fine at 64×48 but wasteful at 576×576 —
331k slots iterated per step to touch maybe 4k live cells, × 5 genes. The C#
version keeps a dense `int` occupancy grid for O(1) spatial queries plus tightly
packed entity arrays so the step loop only touches live entities. Death is a
swap-remove.

Consequence worth knowing: **entity indices are not stable.** Anything holding a
reference to a cell must hold its `CellId`, not its index. The inspector panel
does this and re-resolves every frame, which is also how it correctly reports
"DEAD" instead of silently following whichever cell got swapped into the slot.

### The saturation problem still exists

`PROJECT_STATUS.md` records that at 64×48 the population saturates every
habitable tile in ~35–45 sim-days, after which births and deaths both stop and
all trait means freeze. **Nothing in this port fixes that** — it's a property of
the mechanics, not the implementation. A 576×576 map buys roughly 145× more
habitable tiles, which delays it but doesn't prevent it. The real fix is an
ongoing turnover mechanism (age-based mortality, periodic disasters, crowding
pressure), and it belongs in `LifeGrid.Step`, not the renderer.

---

## 9. First-build checklist

Things most likely to need a touch-up on first import:

- [ ] **Everything renders magenta** → you're on URP/HDRP. Run
      `Biosphere → Check Render Pipeline`. See §3.3.
- [ ] **`The type or namespace name 'Burst' does not exist`** → packages didn't
      install. Window → Package Manager → In Project; add `com.unity.burst`,
      `com.unity.collections`, `com.unity.mathematics` by name.
- [ ] **No `Biosphere` menu appears** → there are compile errors. Check the
      Console; the menu only registers once `Assets/Editor` compiles.

- [x] **`Graphics.RenderPrimitives` requires Unity 2022.2+.** Handled — both
      call sites are behind `#if UNITY_2022_2_OR_NEWER` with a
      `Graphics.DrawProcedural` fallback.
- [ ] **`GetRawTextureData<Color32>()` in a job** needs the texture created with
      `mipChain: false`. Already set, but confirm no import setting re-enables mips.
- [ ] **Burst may warn on `NativeList<T>.AsArray()`** passed to a job while the
      list is also mutated on the main thread in the same frame. All such jobs
      `.Complete()` immediately, so it's safe — add
      `[NativeDisableContainerSafetyRestriction]` if the safety system disagrees.
- [ ] **Shader `#pragma target 4.5`** — `StructuredBuffer` reads in the vertex
      stage need SM4.5. Fails on GLES3 and old mobile; gate or provide a fallback.
- [ ] **`_DepthScale = 1e-5`** may need tuning. Too small and adjacent Y rows
      z-fight; too large and depth exceeds the near/far range. Test at max zoom-out
      on a 576-tall map.
- [ ] **Sprite atlas must exist** before pressing Play — `SpriteLayerRenderer`
      renders nothing without one.
- [ ] The Python prototype stays in the parent folder and still runs. Use
      `test_evolution.py` as the reference oracle if the C# evolution drifts
      differently than expected.
