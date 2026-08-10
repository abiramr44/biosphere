# Setup — from zero to a running scene

Ten minutes, most of it downloading. Detailed reasoning for every setting is in
`ARCHITECTURE.md`; this page is just the clicks.

---

## Step 1 — Install a Unity Editor

Unity Hub is only a launcher; it can't open anything on its own.

1. Open Unity Hub → **Installs** tab → **Install Editor**.
2. Pick **2022.3 LTS** or **Unity 6 (6000.x LTS)**. Either is fine.
3. Modules: leave the defaults (Windows Build Support). **Skip Android, iOS and
   WebGL** — several GB each and unused here.
4. Install. It's a 5–8 GB download.

## Step 2 — Create an empty project

1. Hub → **Projects** → **New project**.
2. Editor version: the one you just installed.
3. Template: **2D (Built-In Render Pipeline)**.

   > ⚠️ **Not 2D (URP).** The shaders in this repo are built-in-pipeline CG.
   > Under URP everything renders **solid magenta**. If the template list only
   > shows "2D (URP)", scroll — the built-in one is usually just called **2D**
   > or **2D (Built-In Render Pipeline)**.

4. Project name: `BiosphereUnity`. Location: anywhere (Documents is fine).
5. **Create project**, wait for it to open, then **close Unity**.

## Step 3 — Copy this repo's files in

You now have two folders. Call them:

- **REPO** — the cloned repo's `BiosphereUnity/` folder (contains `Assets/`,
  `Packages/`, `ARCHITECTURE.md`)
- **PROJECT** — the folder Unity just created

Copy, overwriting when asked:

| From | To |
|---|---|
| `REPO\Assets\` | `PROJECT\Assets\` |
| `REPO\Packages\manifest.json` | `PROJECT\Packages\manifest.json` |

Nothing else. `ProjectSettings/` stays as Unity generated it — that folder is
version- and machine-specific, which is why the repo doesn't ship one.

> **Prefer not to overwrite `manifest.json`?** Skip it, and instead add three
> packages after opening: **Window → Package Manager → + → Add package by
> name**, entering `com.unity.burst`, then `com.unity.collections`, then
> `com.unity.mathematics`. Identical result.

## Step 4 — Reopen and let it compile

Open PROJECT from the Hub. On first open Unity will:

- download Burst, Collections and Mathematics (needs internet, ~1 minute)
- import the scripts and shaders
- generate `.meta` files for everything

Watch the **Console**. It should settle with **no red errors**. A yellow warning
or two is fine.

## Step 5 — Three menu items, in order

A **`Biosphere`** menu appears in the menu bar once the editor scripts compile.

1. **`Biosphere → 0. Apply Pixel-Art Project Settings`**
   Sets linear colour space; disables MSAA, anisotropic filtering, shadows and
   mip limiting across every quality level. Unity will ask to reimport assets
   for the colour space change — **say yes**, it takes seconds.

2. **`Biosphere → Check Render Pipeline`**
   Should log *"Built-in Render Pipeline detected. Correct."*
   If it logs an error, you're on URP — go back to Step 2.

3. **`Biosphere → 1. Setup Project Scene`**
   Creates the `WorldConfig` asset (256×256 tiles, 16 px/unit), generates a
   placeholder sprite atlas with the right import settings, builds and wires
   every GameObject, saves `Assets/Scenes/Biosphere.unity`, and opens it.

## Step 6 — Press Play

| Input | Action |
|---|---|
| `Space` | pause / resume |
| `+` / `-` | speed, x1 up to x64 |
| `N` | new world |
| `C` | cycle colour overlay (energy, or any genome trait) |
| scroll | zoom (snaps to clean steps, centred on cursor) |
| middle-drag | pan |
| WASD / arrows | pan |
| left-click | apply the active tool |

Tools (Inspect, Seed, Kill, Storm strike, Raise/Lower land), speed, weather
triggers and log export are all on the HUD panel on the right.

---

## Step 7 — Check the simulation, not just the picture

**`Biosphere → 2. Run Headless Evolution Test (64x48, 60 days)`**

Runs 60 simulated days with **no scene, no rendering and no Play mode**, then
prints trait drift to the Console. This is worth running first, because it
separates two very different failures: if it passes but the screen is black, the
bug is in the render wiring, not the simulation.

**A pass looks like:** `harvest_rate` **up**, `metabolism_rate` **down**. Those
two signs are the load-bearing check — cells evolving to gather more and burn
less. Exact magnitudes won't match the Python prototype (different RNG and
reproduction draw order); the reference range is harvest +55–113% and metabolism
−35–40% over ~45 days.

It will probably also report the population saturating around day 35–45 and then
freezing. **That's expected** — a known property of the mechanics carried over
from the Python version, not a regression. See `ARCHITECTURE.md §8`.

**`Biosphere → 3. Run Headless Perf Probe (256x256, 20 days)`** reports
steps/sec at the real target world size.

---

## If something breaks

| Symptom | Cause | Fix |
|---|---|---|
| Everything is **magenta** | Project is on URP/HDRP | Recreate from the 2D Built-In template (Step 2) |
| `The type or namespace name 'Burst' does not exist` | Packages didn't install | Package Manager → + → Add package by name (Step 3 note) |
| **No `Biosphere` menu** | Compile errors | Read the Console; the menu only registers once `Assets/Editor` compiles |
| Black screen, no terrain | Scene not built | Run `Biosphere → 1. Setup Project Scene` |
| Sprites invisible | Atlas missing | `Biosphere → Regenerate Placeholder Atlas` |
| Blurry / shimmering pixels | Import settings | `Biosphere → 0. Apply...`, and check `ARCHITECTURE.md §3.1` |

Expect the first build to be a debugging session. **This code has never been
compiled** — it was written without a Unity install available and is
syntax-checked only. `ARCHITECTURE.md §9` lists what's most likely to need a
touch-up.

---

## Replacing the placeholder art

`Assets/Art/Sprites/placeholder_atlas.png` is generated, not drawn — a shaded
blob, a tree and a rock in cells 0, 1 and 2 of a 16×16 grid (32 px cells).

Overwrite it with real art at the same layout and nothing else changes; the
atlas index constants on the `GameBootstrap` component are the only contract.
Keep Filter Mode **Point**, Compression **None**, Mip Maps **off**.
