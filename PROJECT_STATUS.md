# Biosphere — Project Status Handoff

Artificial life simulation: cells live on a procedurally generated island,
harvest sunlight for energy, reproduce, die, and evolve via heritable
genomes. Built incrementally in a prior Cowork session — this file is the
handoff summary for continuing in a new session.

## File map

| File | Role |
|---|---|
| `environment.py` | World sim: terrain generation, day/night, weather, clouds, moisture, static tree/rock decoration (`decor_kind`). No torch dependency (removed — see Decisions). |
| `life.py` | Cell sim: per-cell heritable genome, reproduction/mutation, death, lineage tracking, rendering overlay. |
| `data_logger.py` | `CellLogger` — per-cell (not just aggregate) stats logging to CSV/parquet, queryable via pandas. |
| `render_utils.py` | Display-time upscaling + tree/rock icon stamping + tile-grid lines. |
| `icons.py` | Procedural pixel-art toolbar icons (numpy RGBA arrays) — built from scratch because most icon-like emoji aren't in matplotlib's default font. |
| `simulate_life.py` | Original minimal viewer (Pause/New World/Speed/Seed only). Still works, unchanged in spirit. |
| `simulate_life_dashboard.py` | Main GUI — full research dashboard (see below). This is the file actively being developed. |
| `test_evolution.py` | Headless (no GUI) long-run evolution test, with checkpoint/resume support for splitting long runs across multiple invocations. |
| `simulate_biosphere.py` | Pre-existing environment-only viewer (not touched this session). |

## What's implemented

**1. Genomes + evolution** (`life.py`). Five heritable traits per cell, each a
`(GRID_H, GRID_W)` numpy array (not shared constants): `harvest_rate`,
`metabolism_rate`, `repro_threshold`, `mutation_rate`, `durability_loss`.
Bounded per-trait (`GENE_BOUNDS`) to prevent degenerate mutation. Initial
population sampled from `GENE_DEFAULT ± GENE_INIT_STD` (variance from gen
zero). On reproduction, child = parent genome + Gaussian mutation scaled by
the *parent's own* `mutation_rate` gene (mutation rate is itself heritable —
evolvable evolvability). Nothing hand-tuned to force an outcome; selection
pressure emerges from existing world mechanics only.

**2. Per-cell data tracking** (`data_logger.py`, plus `cell_id`/`parent_id`/
`age`/`birth_step` arrays in `life.py`). `CellLogger.record()` snapshots every
living cell's genome + energy + durability + age + lineage at a configurable
interval; exports to a pandas DataFrame or CSV/parquet.

**3. WorldBox-style GUI** (`simulate_life_dashboard.py`):
- Click-to-inspect side panel (genome, energy, durability, age, lineage), live-updating, handles cell death gracefully.
- Camera: scroll wheel to zoom (centered on cursor), middle-mouse-drag to pan, both clamped to world bounds; "Reset view" button.
- Three-row icon toolbar (procedural pixel-art icons, not emoji):
  - **click tool:** Inspect / Seed here / Kill here / Storm strike (only one active at a time, highlighted)
  - **nature effects:** Trigger rain / Clear skies (temporary weather override, ~3 sim-hour hold) / Reset view
  - **actions:** Pause, New world, +8 cells (bulk reseed), Speed (x1..x64), Color overlay cycle (energy or any genome trait), Save log
- World-color overlay modes: energy (default) or any of the 5 genome traits, to see spatial strategy patterns.
- Trait-means-over-time chart + trait-distribution bar chart (bounds-normalized), updated periodically.
- Visual effects: low-durability cells blend toward warning-red; newly-born cells get a brief bright ring flash; storm strikes show a fading flash circle.
- Native matplotlib pan/zoom/save toolbar disabled (`rcParams["toolbar"]="None"`) since it would've hijacked world clicks.

## Key config (current values)

- World size: `GRID_W, GRID_H = 64, 48` (shrunk from 192×128 — intentionally small sandbox; user plans to expand later once verified).
- Starting population: `DEFAULT_SEED_COUNT = 8` in `life.py`, placed with minimum spacing (~7 tiles apart) via greedy rejection sampling in `seed_random`/`_pick_spaced` — not pure random.
- `PIXEL_SCALE = 10` in the dashboard (display pixels per world tile).
- `STORM_RADIUS = 3`, `STORM_KILL_PROB = 0.7` — storm-strike disaster tool.
- Gene bounds/defaults live in `life.py` (`GENE_DEFAULT`, `GENE_BOUNDS`, `GENE_INIT_STD`).

## Verified findings (empirical, not assumed)

- **Evolution works at both scales tested.** Original 192×128/25-cell scale: over 45 days, harvest_rate +73%, metabolism_rate -45%, repro_threshold -10%, mutation_rate +33% — consistent directional drift. New 64×48/8-cell scale: confirmed again on 2 independent seeds (harvest_rate +55–113%, metabolism_rate -35–40%, etc.).
- **Important limitation at the small scale:** population reliably saturates *every* habitable tile (confirmed exactly, e.g. 1412/1412 tiles) within ~35–45 simulated days. Once full, births stop (no space) and deaths stop (energy/durability stay positive with no reproduction cost draining them) — population and all trait means freeze in a static equilibrium (verified bit-for-bit identical stats from day 40 to day 60 in one run). No extinction risk observed at this scale — the opposite problem (overcrowding/stagnation). If longer-horizon evolution research is needed at this scale, will need either a bigger world (already the stated plan) or some ongoing turnover mechanism (aging-based death, periodic disasters — storm-strike tool already exists as a manual option, or crowding/resource pressure).

## Decisions worth knowing

- **Torch dependency removed.** `environment.py` used torch only for one small Gaussian blur (terrain generation). Replaced with an equivalent vectorized pure-numpy implementation (`sliding_window_view`-based separable convolution) — installing torch in a sandbox pulled a multi-GB CUDA stack for no real benefit. Output is unchanged.
- **Toolbar icons are hand-built, not emoji.** Checked empirically (rendered a glyph test grid) — most icon-like emoji (🔍🌱💀🎨💾🌍👁 etc.) aren't in matplotlib's default DejaVu Sans font and render as tofu boxes. `icons.py` builds small numpy RGBA pixel-art icons from geometry instead (circles, line segments via a `_segment_mask` helper), rendered with nearest-neighbor interpolation.
- **Terrain decoration density was tuned by eye.** First pass (trees on >55% fertility) looked like a solid carpet texture, not individual trees; raised to >76% (and rocks >68%) after visual review — screenshots were rendered to PNG and inspected before/after each tuning pass.

## Pending / not yet done

- **World expansion**: user's stated plan is to test the small sandbox first, then ask to expand `GRID_W`/`GRID_H` back up (or further) once satisfied. Also the fix for population-saturation freeze (see Verified findings above) is still unaddressed — needs either a bigger world or an ongoing-turnover mechanism.

## Recently completed (this session)

- **Unity 2D port scaffolded — `BiosphereUnity/`.** Full graphics-pipeline
  boilerplate for a WorldBox-style version, with the Python sim ported to
  Burst/NativeArray C#. See `BiosphereUnity/ARCHITECTURE.md` for the settings
  tables, scene setup, and perf model. **Written but never compiled** — no Unity
  in the session; all 13 C# files pass a C# parser syntax check only, and
  `ARCHITECTURE.md §9` lists the API-version items likely to need fixing on
  first import.
  - Rendering strategy: terrain = ONE point-filtered `Texture2D` on one quad with
    dirty-rect repaint (not a Unity Tilemap); all sprites = GPU-instanced via
    `GraphicsBuffer` + `Graphics.RenderPrimitives` (no GameObjects/Transforms);
    sorting = per-instance depth written to clip Z with cutout alpha, so the depth
    buffer sorts instead of the CPU; particles = one Burst-simulated fixed pool,
    untextured, one draw call. ~5 draw calls total regardless of entity count.
  - Sim port change worth knowing: Python's dense `(H,W)` per-trait arrays became
    an `int` occupancy grid + packed entity struct-of-arrays with swap-remove
    death. Genome values, mutation rule, weather Markov, sky tint and terrain
    colour ramp are all identical to the Python. **Entity indices are no longer
    stable** — hold `CellId`, not an index (the inspector panel does).
  - Scales to 256×256 → 576×576 by editing one `WorldConfig` ScriptableObject
    field; 576² is ~7 MB of field memory + a 1.3 MB terrain texture.
  - **The saturation freeze is NOT fixed by this port** — it's a mechanics
    property, not an implementation one. A bigger map delays it ~145×; the real
    fix (age mortality / disasters / crowding) still belongs in `LifeGrid.Step`.
  - **Render pipeline: Built-in, NOT URP.** The three shaders are built-in CG
    (`UnityCG.cginc`, `UnityObjectToClipPos`, no SRP tag) and render magenta
    under URP/HDRP. Create the project from the **2D (Built-In Render Pipeline)**
    template. URP was briefly specified by mistake and corrected;
    `Biosphere → Check Render Pipeline` asserts it. URP buys nothing here — its
    2D value is lighting + SRP Batcher, and this uses neither (lighting is two
    uniforms, the world is ~5 draw calls).
  - **Running it:** no `ProjectSettings/` is committed (Unity generates it, and
    it's version-specific), so the flow is: install a Unity Editor (2022.3 LTS or
    Unity 6) via Hub → new project from the 2D Built-in template → copy `Assets/`
    and `Packages/manifest.json` over it → reopen. Then three menu items in
    order: `Biosphere → 0. Apply Pixel-Art Project Settings` (linear colour,
    MSAA/aniso/shadows off across all quality levels — done in code so it can't
    be mis-clicked), `→ Check Render Pipeline`, `→ 1. Setup Project Scene`
    (builds and wires the whole scene from code, since hand-writing a `.unity`
    file is a GUID minefield, and generates a placeholder atlas).
    `Biosphere → 2. Run Headless Evolution Test` runs 60 sim-days with no scene
    or rendering at all, which separates "is the sim port correct?" from "is the
    render pipeline wired?". Pass condition: harvest_rate up, metabolism_rate
    down. Full instructions in `BiosphereUnity/ARCHITECTURE.md §7`.
- **Slime sprites.** `render_utils.py` gained `_slime_template`/`stamp_cells`: a shaded pseudo-3D rounded blob (soft alpha edge, upper-left highlight, lower-right shadow rim), stamped per living cell at display resolution — same vectorized gather-scatter pattern as the tree/rock decor stamping, not a Python loop. `life.py` gained `cell_color_data()` (per-cell energy/genome + durability-warning color, without the old display-only dilation) so the dashboard can tint the sprite template per cell. `render_overlay` (the old dilate-based flat-dot renderer) was left intact/untouched for `simulate_life.py`'s minimal viewer. Perf-checked at full-saturation population (1412 cells on the 64×48 map): ~35ms per stamp call, well inside the 50ms frame budget.
- **World-panel dead space fixed.** `simulate_life_dashboard.py`'s `_build_ui` now computes figure width and the world column's `width_ratio` from `GRID_W/GRID_H` (previously a fixed ratio tuned for the old 192×128 world, which left the 64×48 image letterboxed with empty space below it). Recomputes automatically if the world size changes later.

## How to run

```
cd biosphere
venv\Scripts\activate        # existing Windows venv
python simulate_life_dashboard.py     # main GUI
python simulate_life.py               # minimal original viewer
python test_evolution.py --days 200   # headless evolution verification (supports --checkpoint/--resume for long runs)
```

Dependencies: numpy, matplotlib, pandas (for `data_logger.py`). No torch needed.

## Housekeeping note

There's a `biosphere_cell_log.csv` in the project folder — this accumulated
from testing (Save Log button + headless test runs) during development, not
from real research use. Safe to delete before starting real data collection
with it, so old test rows don't mix with real ones.
