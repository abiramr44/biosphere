import numpy as np

from environment import GRID_W, GRID_H, SIM_SECONDS_PER_REAL_SECOND

MAX_ENERGY = 1.0

REPRO_ENERGY_COST = 0.45
CHILD_START_ENERGY = 0.25
INIT_DURABILITY = 1.0

DEFAULT_SEED_COUNT = 8   # small + spaced-out starting population (was 25, dense/random)

_NEIGHBOR_OFFSETS = [(-1, -1), (-1, 0), (-1, 1),
                     (0, -1), (0, 1),
                     (1, -1), (1, 0), (1, 1)]

# ---------------- Genome ----------------
# Each trait is a per-cell heritable gene. "default"/"init_std" control the
# starting population (sampled per seeded cell, not identical clones), and
# "bounds" keep mutation from producing degenerate/runaway values (e.g. a
# harvest_rate of 0 or a repro_threshold above MAX_ENERGY).
GENES = ["harvest_rate", "metabolism_rate", "repro_threshold",
          "mutation_rate", "durability_loss"]

GENE_DEFAULT = {
    "harvest_rate": 0.06,
    "metabolism_rate": 0.012,
    "repro_threshold": 0.75,
    "mutation_rate": 0.02,
    "durability_loss": 0.14,
}
GENE_BOUNDS = {
    "harvest_rate": (0.01, 0.20),
    "metabolism_rate": (0.002, 0.06),
    "repro_threshold": (0.30, 0.95),
    "mutation_rate": (0.001, 0.15),
    "durability_loss": (0.02, 0.50),
}
# Spread (std dev) used when sampling the *initial* population, so there is
# variance for selection to act on from generation zero.
GENE_INIT_STD = {
    "harvest_rate": 0.015,
    "metabolism_rate": 0.003,
    "repro_threshold": 0.08,
    "mutation_rate": 0.01,
    "durability_loss": 0.05,
}


def _dilate_bool(mask):
    """Grow a boolean mask by 1 cell in each direction -- for DISPLAY ONLY,
    so a single living cell is visible as more than one screen pixel."""
    d = mask.copy()
    d |= np.roll(mask, 1, axis=0)
    d |= np.roll(mask, -1, axis=0)
    d |= np.roll(mask, 1, axis=1)
    d |= np.roll(mask, -1, axis=1)
    return d


def _dilate_value(mask, values):
    """Spread each true cell's value into its empty neighbors -- for DISPLAY
    ONLY, so the dilated blob is colored by its source cell's energy."""
    base = np.where(mask, values, 0.0)
    d = base.copy()
    for dy, dx in [(1, 0), (-1, 0), (0, 1), (0, -1)]:
        d = np.maximum(d, np.roll(base, dy, axis=0) if dx == 0 else np.roll(base, dx, axis=1))
    return d


class Life:
    def __init__(self, bio, seed=None):
        self.rng = np.random.default_rng(seed)
        self.habitable_mask = bio.land_mask | bio.beach_mask

        self.alive = np.zeros((GRID_H, GRID_W), dtype=bool)
        self.energy = np.zeros((GRID_H, GRID_W), dtype=np.float32)
        self.durability = np.zeros((GRID_H, GRID_W), dtype=np.float32)

        # ---- Genome: one array per trait, per-cell ----
        self.genome = {g: np.full((GRID_H, GRID_W), GENE_DEFAULT[g], dtype=np.float32)
                       for g in GENES}

        # ---- Lineage / identity tracking ----
        self.cell_id = np.zeros((GRID_H, GRID_W), dtype=np.int64)      # 0 = no cell
        self.parent_id = np.zeros((GRID_H, GRID_W), dtype=np.int64)    # 0 = no parent (seeded)
        self.age = np.zeros((GRID_H, GRID_W), dtype=np.float32)        # sim-hours alive
        self.birth_step = np.zeros((GRID_H, GRID_W), dtype=np.int64)
        self._next_id = 1
        self.step_count = 0

        self.births = 0     # cells created through reproduction
        self.deaths = 0
        self.seeded = 0       # cells created by manually seeding (not reproduction)
        self.culled = 0        # cells manually removed via the GUI kill-tool

        self.seed_random(bio, count=DEFAULT_SEED_COUNT)

    # ---------------- Genome helpers ----------------
    def _sample_init_genome(self, n):
        """Sample n genomes (one per newly-seeded cell) from the population
        defaults with some spread, so the initial population already has
        trait variance for natural selection to act on."""
        out = {}
        for g in GENES:
            lo, hi = GENE_BOUNDS[g]
            vals = self.rng.normal(GENE_DEFAULT[g], GENE_INIT_STD[g], size=n)
            out[g] = np.clip(vals, lo, hi).astype(np.float32)
        return out

    def _mutate_genome(self, parent_vals, parent_mutation_rate):
        """Given a dict of scalar parent trait values (+ the parent's own
        heritable mutation_rate), return a mutated child genome dict.
        Mutation step size for each trait is proportional to that trait's
        overall range, scaled by the parent's mutation_rate gene -- so
        mutation_rate itself is under selection (evolvable evolvability)."""
        child = {}
        for g in GENES:
            lo, hi = GENE_BOUNDS[g]
            span = hi - lo
            step_std = parent_mutation_rate * span
            val = parent_vals[g] + self.rng.normal(0.0, step_std)
            child[g] = float(np.clip(val, lo, hi))
        return child

    # ---------------- Seeding ----------------
    def _pick_spaced(self, ys, xs, count, min_spacing):
        """Greedily pick up to `count` indices into (ys, xs) such that no two
        picks are closer than min_spacing apart -- so a small starting
        population reads as individually placed beings instead of a random
        clump. Falls back to whatever it could fit if the habitable area is
        too small/crowded for full spacing."""
        order = self.rng.permutation(len(ys))
        chosen = []
        chosen_pts = np.empty((0, 2))
        for i in order:
            if len(chosen) >= count:
                break
            pt = np.array([[ys[i], xs[i]]])
            if len(chosen_pts) > 0:
                d = np.sqrt(((chosen_pts - pt) ** 2).sum(axis=1)).min()
                if d < min_spacing:
                    continue
            chosen.append(i)
            chosen_pts = np.vstack([chosen_pts, pt])
        return np.array(chosen, dtype=int)

    def seed_random(self, bio, count=DEFAULT_SEED_COUNT, min_spacing=4):
        ys, xs = np.where(self.habitable_mask & ~self.alive)
        if len(ys) == 0:
            return
        idx = self._pick_spaced(ys, xs, count, min_spacing)
        if len(idx) == 0:
            return
        n = len(idx)
        genomes = self._sample_init_genome(n)
        for k, i in enumerate(idx):
            y, x = ys[i], xs[i]
            self.alive[y, x] = True
            self.energy[y, x] = CHILD_START_ENERGY
            self.durability[y, x] = INIT_DURABILITY
            for g in GENES:
                self.genome[g][y, x] = genomes[g][k]
            self.cell_id[y, x] = self._next_id
            self.parent_id[y, x] = 0
            self.age[y, x] = 0.0
            self.birth_step[y, x] = self.step_count
            self._next_id += 1
        self.seeded += n

    def seed_at(self, bio, y, x):
        """Place a single new cell at a specific grid cell (used by the GUI's
        'seed here' click-tool). Returns True if it placed one."""
        if not (0 <= y < GRID_H and 0 <= x < GRID_W):
            return False
        if not self.habitable_mask[y, x] or self.alive[y, x]:
            return False
        genome = self._sample_init_genome(1)
        self.alive[y, x] = True
        self.energy[y, x] = CHILD_START_ENERGY
        self.durability[y, x] = INIT_DURABILITY
        for g in GENES:
            self.genome[g][y, x] = genome[g][0]
        self.cell_id[y, x] = self._next_id
        self.parent_id[y, x] = 0
        self.age[y, x] = 0.0
        self.birth_step[y, x] = self.step_count
        self._next_id += 1
        self.seeded += 1
        return True

    def kill_at(self, y, x):
        """Remove the living cell at a specific grid cell (used by the GUI's
        'kill here' click-tool). Tracked separately from natural deaths."""
        if not (0 <= y < GRID_H and 0 <= x < GRID_W) or not self.alive[y, x]:
            return False
        self.alive[y, x] = False
        self.energy[y, x] = 0.0
        self.durability[y, x] = 0.0
        self.cell_id[y, x] = 0
        self.parent_id[y, x] = 0
        self.age[y, x] = 0.0
        self.culled = getattr(self, "culled", 0) + 1
        return True

    def strike_area(self, y, x, radius=3, kill_prob=0.7):
        """Localized disaster (GUI 'storm strike' tool): each living cell
        within `radius` of (y, x) has `kill_prob` chance of being killed
        outright. Tracked under `culled` (environment/user-caused removal,
        not natural death). Returns the number of cells hit."""
        yy, xx = np.mgrid[0:GRID_H, 0:GRID_W]
        dist = np.sqrt((yy - y) ** 2 + (xx - x) ** 2)
        candidates = self.alive & (dist <= radius)
        roll = self.rng.random((GRID_H, GRID_W)) < kill_prob
        hit = candidates & roll
        n_hit = int(hit.sum())
        if n_hit:
            self.alive[hit] = False
            self.energy[hit] = 0.0
            self.durability[hit] = 0.0
            self.cell_id[hit] = 0
            self.parent_id[hit] = 0
            self.age[hit] = 0.0
            self.culled += n_hit
        return n_hit

    # ---------------- Simulation step ----------------
    def step(self, bio, real_dt):
        dt_hours = (real_dt * SIM_SECONDS_PER_REAL_SECOND) / 3600.0
        self.step_count += 1
        self.newborn_coords = []  # (y, x) of cells born this step -- for GUI birth-flash effect

        sun = bio.sunlight_intensity()
        local_light = sun * (1.0 - bio.current_clouds)

        gain = self.genome["harvest_rate"] * dt_hours * local_light * self.alive * self.habitable_mask
        cost = self.genome["metabolism_rate"] * dt_hours * self.alive
        self.total_harvested_this_step = float(gain.sum())
        self.energy = np.clip(self.energy + gain - cost, 0.0, MAX_ENERGY)
        self.age += self.alive * dt_hours

        # ---- Death ----
        dying = self.alive & ((self.energy <= 0) | (self.durability <= 0))
        n_dying = int(dying.sum())
        if n_dying:
            self.alive[dying] = False
            self.energy[dying] = 0.0
            self.durability[dying] = 0.0
            self.cell_id[dying] = 0
            self.parent_id[dying] = 0
            self.age[dying] = 0.0
            self.deaths += n_dying

        # ---- Reproduction ----
        ready = self.alive & (self.energy >= self.genome["repro_threshold"]) & \
            (self.durability > self.genome["durability_loss"])
        ys, xs = np.where(ready)
        if len(ys):
            order = self.rng.permutation(len(ys))
            for i in order:
                y, x = int(ys[i]), int(xs[i])
                offsets = _NEIGHBOR_OFFSETS.copy()
                self.rng.shuffle(offsets)
                for dy, dx in offsets:
                    ny, nx = y + dy, x + dx
                    if 0 <= ny < GRID_H and 0 <= nx < GRID_W and \
                            self.habitable_mask[ny, nx] and not self.alive[ny, nx]:
                        parent_vals = {g: float(self.genome[g][y, x]) for g in GENES}
                        child_genome = self._mutate_genome(parent_vals, parent_vals["mutation_rate"])

                        self.alive[ny, nx] = True
                        self.energy[ny, nx] = CHILD_START_ENERGY
                        self.durability[ny, nx] = INIT_DURABILITY
                        for g in GENES:
                            self.genome[g][ny, nx] = child_genome[g]
                        self.cell_id[ny, nx] = self._next_id
                        self.parent_id[ny, nx] = self.cell_id[y, x]
                        self.age[ny, nx] = 0.0
                        self.birth_step[ny, nx] = self.step_count
                        self._next_id += 1

                        self.energy[y, x] -= REPRO_ENERGY_COST
                        self.durability[y, x] -= self.genome["durability_loss"][y, x]
                        self.births += 1
                        self.newborn_coords.append((ny, nx))
                        break

    # ---------------- Rendering ----------------
    def cell_color_data(self, color_by="energy"):
        """Per-cell display color (energy/genome gradient blended with a
        low-durability warning tint) at grid resolution, WITHOUT the
        display-only neighbor dilation used by render_overlay. For renderers
        that stamp an actual multi-pixel sprite per cell (see
        render_utils.stamp_cells) instead of relying on dilation to make a
        single grid cell visible. Returns (alive_mask, colors) where colors
        is (GRID_H, GRID_W, 3), valid wherever alive_mask is True."""
        color_low = np.array([0.75, 0.05, 0.55])
        color_high = np.array([1.0, 0.55, 0.90])

        if color_by == "energy" or color_by not in GENES:
            val, vmin, vmax = self.energy, 0.0, MAX_ENERGY
        else:
            val = self.genome[color_by]
            vmin, vmax = GENE_BOUNDS[color_by]

        t = np.clip((val - vmin) / (vmax - vmin + 1e-8), 0, 1)[..., None]
        cell_color = color_low + (color_high - color_low) * t

        durability_low = np.clip((0.35 - self.durability) / 0.35, 0, 1)[..., None]
        warn_color = np.array([1.0, 0.15, 0.05])
        cell_color = cell_color * (1 - durability_low * 0.65) + warn_color * (durability_low * 0.65)
        return self.alive, cell_color

    def render_overlay(self, base_img, color_by="energy"):
        """color_by: 'energy' (default, original magenta->pink by energy
        level) or any name in GENES, to color each living cell by its
        genome trait value instead -- lets the GUI show spatial patterns in
        trait distribution (e.g. sun-heavy vs shaded regions favoring
        different strategies)."""
        img = base_img.copy()
        if self.alive.any():
            # Bright magenta->hot-pink, chosen specifically because nothing in
            # the terrain palette (greens/blues/grays/tan) is anywhere near
            # this hue -- cells should never blend into the background.
            color_low = np.array([0.75, 0.05, 0.55])
            color_high = np.array([1.0, 0.55, 0.90])

            if color_by == "energy" or color_by not in GENES:
                val, vmin, vmax = self.energy, 0.0, MAX_ENERGY
            else:
                val = self.genome[color_by]
                vmin, vmax = GENE_BOUNDS[color_by]

            disp_mask = _dilate_bool(self.alive)
            disp_val = _dilate_value(self.alive, val)
            t = np.clip((disp_val - vmin) / (vmax - vmin + 1e-8), 0, 1)[..., None]
            cell_color = color_low + (color_high - color_low) * t

            # Health warning: cells running low on durability (close to
            # dying from wear, not energy) blend toward a warning red so
            # it's visible at a glance without clicking each cell.
            durability_low = np.clip((0.35 - self.durability) / 0.35, 0, 1)
            disp_warn = _dilate_value(self.alive, durability_low)[..., None]
            warn_color = np.array([1.0, 0.15, 0.05])
            cell_color = cell_color * (1 - disp_warn * 0.65) + warn_color * (disp_warn * 0.65)

            img[disp_mask] = cell_color[disp_mask]
        return img

    @property
    def population(self):
        return int(self.alive.sum())

    def cell_info(self, y, x):
        """Full inspection payload for a single grid cell -- used by the GUI
        click-to-inspect panel. Returns None if the cell isn't alive."""
        if not self.alive[y, x]:
            return None
        return {
            "id": int(self.cell_id[y, x]),
            "parent_id": int(self.parent_id[y, x]),
            "position": (int(y), int(x)),
            "energy": float(self.energy[y, x]),
            "durability": float(self.durability[y, x]),
            "age_hours": float(self.age[y, x]),
            "genome": {g: float(self.genome[g][y, x]) for g in GENES},
        }

    def genome_stats(self):
        """Mean/std of each trait across the living population, for
        trait-distribution charts and drift tracking."""
        if self.population == 0:
            return {g: {"mean": None, "std": None} for g in GENES}
        out = {}
        for g in GENES:
            vals = self.genome[g][self.alive]
            out[g] = {"mean": float(vals.mean()), "std": float(vals.std())}
        return out

    def stats(self):
        """Snapshot of current population-level metrics, for logging/plotting."""
        if self.population == 0:
            avg_energy = 0.0
            avg_durability = 0.0
        else:
            avg_energy = float(self.energy[self.alive].mean())
            avg_durability = float(self.durability[self.alive].mean())
        s = {
            "population": self.population,
            "avg_energy": avg_energy,
            "avg_durability": avg_durability,
            "births": self.births,
            "deaths": self.deaths,
            "seeded": self.seeded,
            "culled": self.culled,
            "harvested_this_step": getattr(self, "total_harvested_this_step", 0.0),
        }
        gstats = self.genome_stats()
        for g in GENES:
            s[f"avg_{g}"] = gstats[g]["mean"]
        return s
