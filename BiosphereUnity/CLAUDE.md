# Briefing for the Claude agent working in this Unity project

You are working on **Biosphere**, a WorldBox-style 2D pixel sandbox built on top
of a working artificial-life simulation. Read this before touching anything.

If you have Unity MCP tools available (`Unity_ReadConsole`, scene/GameObject
inspection, script editing), **use them instead of asking the user to paste
things.** Reading the console yourself and re-reading it after a fix is the whole
point of the connection.

---

## 1. Current status — read this first

**The project compiles, the simulation is verified, and Play mode renders.**
As of 2026-08-10 it is a working prototype — no longer the never-compiled
skeleton it started as.

| | |
|---|---|
| Editor | Unity 6.5 (6000.5.7f1), DX12 |
| Render pipeline | **Built-In** (deliberate — see §3) |
| Packages | Burst, Collections, Mathematics, uGUI, AI Assistant, AI Inference |
| Compile | **clean.** `EditorUtility.scriptCompilationFailed == false`, zero `CS####` |
| Headless sim test | **passed** — see below |
| Scene | **built** — `Assets/Scenes/Biosphere.unity`, 5 roots |
| Play mode | **works** — terrain + live cell sprites render, no Biosphere errors |
| World size in scene | 256×256 (`WorldConfig` default) |

**Headless evolution test, seed 2086318005**, 64×48, 60 sim-days (86,400 steps)
in ~29.8 s (~2,896 steps/sec). Population 8 → 1412, births 1608, deaths 204,
saturated every habitable tile on day 44 then froze (expected — §4).

| trait | change |
|---|---|
| `harvest_rate` | **+118.5%** (0.04993 → 0.10912) |
| `metabolism_rate` | **−40.7%** (0.01266 → 0.00751) |
| `repro_threshold` | −14.8% |
| `mutation_rate` | +78.0% |
| `durability_loss` | +3.3% |

Pass condition met (harvest up, metabolism down); signs match the Python oracle.
**The C# port is verified against the reference implementation.**

Console errors from Unity's own **AI Assistant package** (backend refresh
failures, a `NullReferenceException` in its search popup) are unrelated to this
project and safe to ignore. Don't chase them.

**Next unstarted work:** the placeholder atlas is still placeholder art; the
saturation freeze (§4) is still unaddressed; camera framing has been verified
once but tools/HUD interaction have not been exercised.

### Errors already found and fixed — expect more of the same kind

Three of the first four errors were **missing `using` directives**, because the
code was written blind. When you see a "does not contain a definition for X" or
"X is not an attribute class", suspect a missing namespace before suspecting a
real bug.

| Error | Cause |
|---|---|
| `CS0616: 'Range' is not an attribute class` | `CellLogger.cs` imports `System` but not `UnityEngine`, so `[Range]` bound to the C# 8 `System.Range` struct |
| `CS1061: 'BuildCellInstancesJob' has no definition for 'Schedule'` | `Schedule()` is an extension method in `Unity.Jobs.IJobParallelForExtensions`; `GameBootstrap.cs` didn't import `Unity.Jobs` |
| `UnityEngine.EventSystems` unavailable | Lives in the `UnityEngine.UI` assembly; this project uses an asmdef, which needs explicit references. Removed the dependency rather than adding the reference |
| `QualitySettings.softParticles` etc. | Deprecated/removed in Unity 6 |

Six files deliberately do **not** import `UnityEngine` — they are pure
simulation and must stay engine-agnostic: `CellLogger`, `WorldGrid`, `Genome`,
`LifeGrid`, `LifeJobs`, `FieldMath`. Don't "fix" them by adding
`using UnityEngine;`. Fix the specific symbol instead.

### Two runtime bugs found after the first clean compile — learn from both

Compiling cleanly proved nothing. Both of these passed compilation, and the
second even survived a "no console errors" check.

**1. One MonoBehaviour per file, named after the file. Non-negotiable.**
`SpriteLayerRenderer` was originally declared inside
`InstancedSpriteBatch.cs`. Unity only generates a GUID-backed `MonoScript`
asset for the class matching the *filename*, so `SpriteLayerRenderer` had no
GUID anywhere in the project. It worked via `AddComponent`/`typeof` in code but
**could never be serialized into a scene** — the scene YAML held a bare local
`fileID` with no guid, and the component was silently dropped on reload, giving
a `NullReferenceException` every frame in `BuildDecorLayer()`.
Symptom to watch for: *a field looks assigned in the inspector/YAML but is null
at runtime.* Fixed by splitting it into its own file.

**2. `AddComponent<T>()` runs `Awake()` synchronously, in Edit mode too.**
In `BiosphereSetup.BuildScene()`, `AddComponent<PixelCameraController>()` fired
`Awake()`, which dereferenced `cfg` before the *next line* assigned it → NRE →
Unity disabled the component → that disabled state was baked into the saved
scene. The camera never framed the world. Fix: `SetActive(false)` before
`AddComponent`, wire fields, then `SetActive(true)`.

**2b. `EditorSceneManager.NewScene(..., Single)` can invalidate references held
across it.** A `WorldConfig` loaded via `AssetDatabase` *before* the scene
switch was destroyed by it (`MissingReferenceException` on touch), so the first
wiring call after `NewScene` silently serialized null. Re-fetch any asset
reference immediately *after* `NewScene`, don't trust one passed in.

**The meta-lesson: "no console errors" is not "it works".** Bug 2 was missed on
the first pass because verification used a synthetic top-down capture instead of
the real camera. Verify through the actual path the player uses.

---

## 2. What the project is

Cells live on a procedurally generated island, harvest sunlight for energy,
reproduce, die, and **evolve via heritable genomes**. Five traits per cell:
`harvest_rate`, `metabolism_rate`, `repro_threshold`, `mutation_rate`,
`durability_loss`. Children inherit the parent genome plus Gaussian mutation
scaled by the parent's *own* `mutation_rate` gene — mutation rate is itself
under selection.

**Selection pressure is never scripted.** It emerges from sunlight, cloud
shadow, weather, terrain and the day/night cycle. This is the core research
premise of the project.

There is a **Python prototype in the parent folder** (`../*.py`) that is
working and empirically validated. It is the reference oracle. If the C#
evolution behaves oddly, diff the logic against `../life.py` and
`../environment.py`.

---

## 3. Invariants — do not "improve" these

These look like odd choices and are all load-bearing. Read
`ARCHITECTURE.md` before changing any of them.

**Built-In Render Pipeline, not URP.** The three shaders are built-in CG
(`UnityCG.cginc`, `UnityObjectToClipPos`, no SRP tag). Under URP/HDRP everything
renders **solid magenta**. `Biosphere → Check Render Pipeline` asserts this.
Unity 6.5 deprecates Built-In (supported through 6.7 LTS) — that's a known,
accepted future cost, not a bug to fix today.

**Terrain is ONE point-filtered `Texture2D` on one quad — not a Tilemap.**
At 576×576 that's 331,776 tiles; a Tilemap chunk-remeshes on every edit. Tile
edits here are texel writes with an O(dirty-rect) upload. Do not migrate this to
`Tilemap`/`TilemapRenderer`.

**All sprites go through GPU instancing, not `SpriteRenderer`.** One
`GraphicsBuffer` + one `Graphics.RenderPrimitives` per layer. No GameObjects, no
Transforms, no per-entity components. ~5 draw calls for the whole world
regardless of entity count. Do not add `SpriteRenderer` components for entities.

**Sorting is the depth buffer, not a CPU sort.** Per-instance depth
(`layerBase + gridH − worldY`) written into clip Z, with **cutout** alpha
(`clip()`, not blending) — cutout is what makes depth sorting valid. Do not add
`SortingGroup` or `sortingOrder`.

**Particles are a fixed pre-allocated Burst pool.** Untextured squares, 32 bytes
each, one draw call, zero runtime allocation; over capacity it recycles oldest
rather than growing. Do not replace with Unity's `ParticleSystem`.

**Entity indices are NOT stable.** `LifeGrid` uses an occupancy grid plus packed
struct-of-arrays with **swap-remove** on death. Anything holding a reference to a
cell must hold its `CellId` and re-resolve, never an index. The inspector panel
does this correctly — copy that pattern.

**Pixel-perfect camera rules** (`PixelCameraController`): orthographic size is
*derived* (`Screen.height / (2 × PPU × zoom)`), zoom snaps to a step list, and
camera position snaps to the texel grid. Hand-setting ortho size or allowing
arbitrary zoom produces shimmering pixels.

---

## 4. Known behaviour that is NOT a bug

**Population saturation freeze.** The population fills every habitable tile in
roughly 35–45 simulated days, after which births stop (no space) and deaths stop
(nothing drains energy without reproduction), so population and all trait means
freeze. Verified bit-for-bit identical stats from day 40 to day 60 in the Python
version.

This is a property of the mechanics, not the implementation, and it is
**unfixed by design so far**. A bigger map delays it (~145× more tiles at
576² vs 64×48) but doesn't prevent it. The real fix is an ongoing turnover
mechanism — age-based mortality, periodic disasters, or crowding pressure — and
it belongs in `LifeGrid.Step`. **Do not fix it by hand-tuning constants to
produce a nicer-looking curve.** That would destroy the research value.

**Warnings that are safe to ignore:** Input Manager deprecation (we use legacy
`Input`; migration is a real future task, not a blocker) and Dynamic Batching
deprecation (irrelevant — everything is already GPU-instanced).

---

## 5. Layout

```
BiosphereUnity/
├── ARCHITECTURE.md      full rationale, settings tables, perf model
├── SETUP.md             click-by-click setup
├── CLAUDE.md            this file
└── Assets/
    ├── Editor/          menu items: project settings, scene builder, headless tests
    ├── Scripts/
    │   ├── Core/        WorldConfig (ScriptableObject), GameBootstrap (frame loop)
    │   ├── Sim/         Environment/WorldGrid, Life/{Genome,LifeGrid,LifeJobs}, Data/CellLogger
    │   ├── Render/      TerrainRenderer, InstancedSpriteBatch
    │   ├── Particles/   PixelParticleSystem
    │   ├── CameraRig/   PixelCameraController
    │   ├── UI/          DashboardHud (IMGUI)
    │   └── Util/        FieldMath (gaussian blur, rank normalise)
    └── Shaders/         TerrainUnlit, PixelSpriteInstanced, PixelParticle
```

World size, pixels-per-unit and every sim constant live on the **`WorldConfig`**
ScriptableObject (`Assets/Settings/WorldConfig.asset`, created by the setup menu
item). Scaling 256×256 → 576×576 is one inspector field. Don't hardcode sizes.

### Menu items

| Menu | Does |
|---|---|
| `Biosphere → 0. Apply Pixel-Art Project Settings` | linear colour, MSAA/aniso/shadows off, across all quality levels |
| `Biosphere → Check Render Pipeline` | asserts Built-In |
| `Biosphere → 1. Setup Project Scene` | creates WorldConfig, generates placeholder atlas, builds + wires the scene |
| `Biosphere → 2. Run Headless Evolution Test` | 64×48, 60 sim-days, no scene, no rendering |
| `Biosphere → 3. Run Headless Perf Probe` | 256×256, 20 days, reports steps/sec |
| `Biosphere → Regenerate Placeholder Atlas` | rebuilds the stand-in sprite atlas |

The scene is **built by code**, not stored as a hand-authored `.unity` file —
hand-writing that format is a GUID minefield. If the scene is broken, re-run the
setup item rather than repairing YAML.

---

## 6. Verification standard

The user's stated preference, carried over from the Python work: **test as you
go, don't write everything then run it once.**

- Compile errors: fix, then **read the console again** to confirm. Don't batch
  guesses.
- Simulation changes: run the headless evolution test. Pass condition is
  **`harvest_rate` up, `metabolism_rate` down** — cells evolving to gather more
  and burn less. Reference range from Python: harvest +55–113%, metabolism
  −35–40% over ~45 days at 64×48. Exact numbers won't match (different RNG and
  reproduction draw order); the **signs** are the check.
- Rendering changes: actually look. Enter Play mode, or take and inspect a
  screenshot. Don't declare a visual fix working without seeing it.
- Perf claims: measure with the perf probe. Don't assert.

Say plainly when something is unverified. Most of this codebase currently is.

---

## 7. Repo hygiene

- Public repo: **https://github.com/abiramr44/biosphere**, branch `main`.
- The Unity project root is `BiosphereUnity/`; the **repo root is one level up**
  and also holds the Python prototype. Don't move either.
- `ProjectSettings/` **and** `.meta` files are committed — that's deliberate.
  `.meta` files carry the GUIDs that keep scene and asset references from
  breaking; losing them silently breaks the project for everyone else.
- `.gitignore` excludes `Library/`, `Temp/`, `Obj/`, `Build/`, `Logs/`,
  `UserSettings/`, generated IDE files, `venv/`, `__pycache__/`, `*.csv`.
- **Never commit `Library/`.** It's multi-GB and regenerated.
- `PROJECT_STATUS.md` (repo root) is the running handoff document. The user has a
  standing rule: **update it after each task**, not just at session end.
