# Biosphere

An artificial-life simulation where cells live on a procedurally generated
island, harvest sunlight for energy, reproduce, die, and **evolve via heritable
genomes**. Selection pressure is not scripted — it emerges from the world's own
mechanics (sunlight, cloud shadow, weather, terrain, day/night).

The repo holds two implementations:

| | `*.py` (repo root) | `BiosphereUnity/` |
|---|---|---|
| **Role** | Research prototype, and the reference oracle | WorldBox-style sandbox game |
| **Stack** | numpy + matplotlib | Unity 2D, Burst/NativeArray, custom shaders |
| **Scale** | 64×48 tiles, ~1.4k cells | 256×256 → 576×576 tiles, thousands of entities |
| **Status** | Working, evolution empirically verified | Written, **not yet compiled** |

`PROJECT_STATUS.md` is the detailed handoff document — read it first.

---

## The Python prototype

```bash
cd biosphere
venv\Scripts\activate                 # Windows
python simulate_life_dashboard.py     # main research GUI
python simulate_life.py               # minimal viewer
python test_evolution.py --days 200   # headless evolution verification
```

Dependencies: numpy, matplotlib, pandas.

**What's verified:** over 45 sim-days, harvest_rate rises 55–113% and
metabolism_rate falls 35–40%, consistently, across independent seeds. Cells in
sunnier regions evolve differently from those in shaded ones.

**Known limitation:** the population saturates every habitable tile in ~35–45
days, after which births and deaths both stop and all trait means freeze. This
is a property of the mechanics, not a bug, and it is still unfixed. See
`PROJECT_STATUS.md`.

## The Unity port

Full architecture, render settings and setup instructions:
**[`BiosphereUnity/ARCHITECTURE.md`](BiosphereUnity/ARCHITECTURE.md)**

Quick version — it rests on one decision, *the GPU does almost nothing*:

- Terrain is **one point-filtered `Texture2D` on one quad**, not a Unity
  Tilemap. Tile edits are texel writes with an O(dirty-rect) upload.
- Every sprite draws through **one GPU-instanced batch per layer**. No
  GameObjects, no Transforms, no SpriteRenderers. ~5 draw calls for the whole
  world regardless of entity count.
- **Sorting is the depth buffer**, not a CPU sort — per-instance depth written
  to clip Z with cutout alpha.
- Particles are a **fixed Burst-simulated pool** of untextured squares, 32 bytes
  each, one draw call, zero runtime allocation.

Adding 10,000 more creatures costs CPU simulation time and buffer upload bytes.
It costs zero draw calls.

### Running it

**Click-by-click setup: [`BiosphereUnity/SETUP.md`](BiosphereUnity/SETUP.md)**

In brief: Unity **2022.3 LTS or newer**, project created from the **2D
(Built-In Render Pipeline)** template — *not* URP, the shaders are built-in CG
and render magenta under an SRP.

`ProjectSettings/` is deliberately not committed (Unity generates it, and it's
version-specific). Create a project from the template, copy `Assets/` and
`Packages/manifest.json` into it, reopen, then run three menu items in order:

1. `Biosphere → 0. Apply Pixel-Art Project Settings`
2. `Biosphere → Check Render Pipeline`
3. `Biosphere → 1. Setup Project Scene`

Then press Play.

**Verify the sim before trusting anything visual:**
`Biosphere → 2. Run Headless Evolution Test` runs 60 sim-days with no scene and
no rendering, so it separates "is the port correct?" from "is the renderer
wired?". Pass condition: harvest_rate up, metabolism_rate down.

---

## Status and honesty about it

The Unity code has been syntax-checked with a C# parser but **has never been
compiled or run** — there was no Unity install available when it was written.
`ARCHITECTURE.md §9` lists the things most likely to need fixing on first
import. Treat the first build as a debugging session, not a launch.
