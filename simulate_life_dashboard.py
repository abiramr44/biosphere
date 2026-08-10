import numpy as np
import matplotlib
matplotlib.rcParams["toolbar"] = "None"  # hide the default pan/zoom/save toolbar --
                                          # it's not part of this UI and its pan/zoom
                                          # tools would hijack world clicks
import matplotlib.pyplot as plt
import matplotlib.animation as animation
from matplotlib.widgets import Button
from matplotlib.patches import Circle
from collections import deque

from environment import Biosphere, GRID_W, GRID_H
from life import Life, GENES, GENE_BOUNDS
from data_logger import CellLogger
from render_utils import upscale_with_decor, stamp_cells
import icons

STEP_INTERVAL_MS = 50
PIXEL_SCALE = 10        # bigger than before (was 4) -- smaller world, so zoom in more by default
HISTORY_LEN = 2000       # capped history length so memory doesn't grow unbounded
STAT_EVERY = 4              # record aggregate stats every N simulation steps
LOG_EVERY = 20               # per-cell log snapshot every N simulation steps -- coarser
                              # than STAT_EVERY since it's O(population) not O(1)
BULK_SEED_COUNT = 8
STORM_RADIUS = 3
STORM_KILL_PROB = 0.7
BIRTH_FLASH_FRAMES = 5
MIN_VIEW_TILES = 6        # can't zoom in past this many tiles wide/tall

TRAIT_COLORS = {
    "harvest_rate": "#8fd3ff",
    "metabolism_rate": "#ff8f8f",
    "repro_threshold": "#ffd08f",
    "mutation_rate": "#c08fff",
    "durability_loss": "#8fffb0",
}
OVERLAY_MODES = ["energy"] + GENES

BTN_COLOR = "#2a2a3a"
BTN_HOVER = "#3a3a52"
BTN_ACTIVE = "#4f4f7a"


class LifeDashboard:
    def __init__(self, seed=None):
        self.bio = Biosphere(seed=seed)
        self.life = Life(self.bio, seed=seed)
        self.paused = False
        self.frame_count = 0
        self.sim_hours_elapsed = 0.0

        self.hist_t = deque(maxlen=HISTORY_LEN)
        self.hist_pop = deque(maxlen=HISTORY_LEN)
        self.hist_energy = deque(maxlen=HISTORY_LEN)
        self.hist_births = deque(maxlen=HISTORY_LEN)
        self.hist_deaths = deque(maxlen=HISTORY_LEN)
        self.hist_traits = {g: deque(maxlen=HISTORY_LEN) for g in GENES}

        self.logger = CellLogger(record_every_steps=LOG_EVERY, max_cells_per_snapshot=300)
        self.overlay_idx = 0
        self.selected = None       # (y, x) of the cell the user clicked on
        self.active_tool = "inspect"  # "inspect" | "seed" | "kill" | "storm"
        self._tool_axes = {}

        # camera pan/zoom state (view is tracked as normal-order data coords:
        # x0<x1, y0<y1 with y0=top row, y1=bottom row -- flipped only when
        # actually applied to the inverted-origin imshow axes)
        self._pan_active = False
        self._pan_start_px = None
        self._pan_start_view = None

        # transient visual effects
        self._birth_flashes = []   # list of [y, x, ttl]
        self._strike_flash = None  # dict(y=,x=,radius=,ttl=,max_ttl=) or None

        self._build_ui()

    # ---------------- UI construction ----------------
    def _build_ui(self):
        plt.style.use("dark_background")

        # ---- Figure sizing: pick the world column's width so its box
        # aspect ratio matches the world image's aspect ratio (GRID_W /
        # GRID_H) exactly, given the vertical space the 3-row gridspec gets.
        # Previously the world column was a fixed width_ratio tuned for the
        # old 192x128 world; at 64x48 that box was nearly square while the
        # image is 4:3, so imshow (aspect='equal') letterboxed it and left
        # empty space below the map. Computing it from GRID_W/GRID_H keeps
        # this correct if the world size changes again later.
        grid_left, grid_right = 0.03, 0.98
        grid_top, grid_bottom = 0.94, 0.32
        chart_col_in = 5.0          # desired width (inches) of each chart column
        fig_height_in = 11.0

        rows_height_in = fig_height_in * (grid_top - grid_bottom)
        world_col_in = rows_height_in * (GRID_W / GRID_H)
        fig_width_in = (world_col_in + 2 * chart_col_in) / (grid_right - grid_left)

        self.fig = plt.figure(figsize=(fig_width_in, fig_height_in))
        self.fig.patch.set_facecolor("#0a0a12")
        self.fig.canvas.manager.set_window_title("Biosphere — Evolution Research Dashboard")

        gs = self.fig.add_gridspec(
            3, 3, width_ratios=[world_col_in / chart_col_in, 1, 1], height_ratios=[1.5, 1, 0.85],
            left=grid_left, right=grid_right, top=grid_top, bottom=grid_bottom,
            wspace=0.24, hspace=0.5,
        )

        # ---- World view (click behavior depends on the active tool; scroll to
        # zoom, middle-drag to pan) ----
        self.ax_world = self.fig.add_subplot(gs[:, 0])
        self.ax_world.axis("off")
        self.ax_world.set_title("World  (scroll = zoom, middle-drag = pan)", fontsize=10.5, color="#e8e8f0")
        img = self._render_world()
        self.im = self.ax_world.imshow(img, interpolation="nearest")
        self._full_w = GRID_W * PIXEL_SCALE
        self._full_h = GRID_H * PIXEL_SCALE
        (self.selection_marker,) = self.ax_world.plot(
            [], [], marker="o", markersize=11, markerfacecolor="none",
            markeredgecolor="#ffffff", markeredgewidth=1.6, linestyle="none", zorder=5,
        )
        (self.birth_flash_marker,) = self.ax_world.plot(
            [], [], marker="o", markersize=14, markerfacecolor="none",
            markeredgecolor="#fff27a", markeredgewidth=1.8, linestyle="none", zorder=5,
        )
        self.strike_circle = Circle((0, 0), 0, facecolor="#ffe27a", edgecolor="#fff7cc",
                                     alpha=0.0, linewidth=1.5, zorder=6)
        self.ax_world.add_patch(self.strike_circle)

        self.fig.canvas.mpl_connect("button_press_event", self._on_press)
        self.fig.canvas.mpl_connect("button_release_event", self._on_release)
        self.fig.canvas.mpl_connect("motion_notify_event", self._on_motion)
        self.fig.canvas.mpl_connect("scroll_event", self._on_scroll)

        # ---- Population / energy over time ----
        self.ax_pop = self.fig.add_subplot(gs[0, 1])
        self.ax_pop.set_title("Population", fontsize=10, color="#e8e8f0")
        self.ax_pop.set_facecolor("#12121a")
        (self.line_pop,) = self.ax_pop.plot([], [], color="#ff5fc0")
        self.ax_pop.set_xlabel("sim-hours", fontsize=8, color="#aaaaaa")
        self.ax_pop.tick_params(colors="#aaaaaa", labelsize=7)

        self.ax_energy = self.fig.add_subplot(gs[1, 1])
        self.ax_energy.set_title("Avg energy per cell", fontsize=10, color="#e8e8f0")
        self.ax_energy.set_facecolor("#12121a")
        (self.line_energy,) = self.ax_energy.plot([], [], color="#8fd3ff")
        self.ax_energy.set_ylim(0, 1.05)
        self.ax_energy.set_xlabel("sim-hours", fontsize=8, color="#aaaaaa")
        self.ax_energy.tick_params(colors="#aaaaaa", labelsize=7)

        # ---- Trait means over time -- this is the "watch evolution happen" chart ----
        self.ax_trait_t = self.fig.add_subplot(gs[2, 1])
        self.ax_trait_t.set_title("Genome trait means over time (normalized to bounds)",
                                   fontsize=9.5, color="#e8e8f0")
        self.ax_trait_t.set_facecolor("#12121a")
        self.trait_lines = {}
        for g in GENES:
            (line,) = self.ax_trait_t.plot([], [], color=TRAIT_COLORS[g], label=g, linewidth=1.3)
            self.trait_lines[g] = line
        self.ax_trait_t.set_ylim(0, 1.05)
        self.ax_trait_t.set_xlabel("sim-hours", fontsize=8, color="#aaaaaa")
        self.ax_trait_t.tick_params(colors="#aaaaaa", labelsize=7)
        self.ax_trait_t.legend(fontsize=6.5, loc="upper left", ncol=2, facecolor="#12121a",
                                labelcolor="#e8e8f0", framealpha=0.6)

        # ---- Cell inspector panel ----
        self.ax_inspect = self.fig.add_subplot(gs[0, 2])
        self.ax_inspect.axis("off")
        self.ax_inspect.set_title("Cell inspector", fontsize=10, color="#e8e8f0")
        self.inspect_text = self.ax_inspect.text(
            0.0, 1.0, "Click a living cell in the world view.",
            fontsize=7.6, color="#c8f0ff", family="monospace",
            va="top", ha="left", transform=self.ax_inspect.transAxes,
            linespacing=1.35,
        )

        # ---- Trait distribution snapshot (current population, bounds-normalized) ----
        self.ax_trait_dist = self.fig.add_subplot(gs[1, 2])
        self.ax_trait_dist.set_title("Trait distribution (population now)", fontsize=9.5, color="#e8e8f0")
        self.ax_trait_dist.set_facecolor("#12121a")
        self.ax_trait_dist.set_xlim(0, 1.05)
        self.ax_trait_dist.tick_params(colors="#aaaaaa", labelsize=7)

        # ---- Overlay / logging status panel ----
        self.ax_info = self.fig.add_subplot(gs[2, 2])
        self.ax_info.axis("off")
        self.info_text = self.ax_info.text(
            0.0, 1.0, "", fontsize=8.7, color="#ffd08f", family="monospace",
            va="top", ha="left", transform=self.ax_info.transAxes,
        )

        self.status_text = self.fig.text(
            0.03, 0.29, "", fontsize=10, color="#8fd3ff", family="monospace"
        )
        self.stats_text = self.fig.text(
            0.03, 0.265, "", fontsize=9.5, color="#ffd08f", family="monospace"
        )

        self._build_toolbar()
        self.fig.canvas.mpl_connect("close_event", self._on_close)

    # ---------------- Icon toolbar ----------------
    def _icon_button(self, rect, icon_img, caption, on_click, active=False):
        ax = self.fig.add_axes(rect)
        btn = Button(ax, "", color=(BTN_ACTIVE if active else BTN_COLOR), hovercolor=BTN_HOVER)
        ax.set_xlim(0, 1)
        ax.set_ylim(0, 1)
        ax.imshow(icon_img, extent=[0.20, 0.80, 0.28, 0.95], zorder=2,
                  interpolation="nearest", aspect="auto")
        ax.text(0.5, 0.11, caption, fontsize=6.6, color="#e8e8f0", ha="center",
                va="center", family="monospace", zorder=3)
        btn.on_clicked(on_click)
        return btn, ax

    def _build_toolbar(self):
        btn_w, gap, x0, h = 0.105, 0.013, 0.03, 0.058

        def slot(i):
            return x0 + i * (btn_w + gap)

        # ---- Row 1 (top): click-tools -- what a left-click on the world does ----
        y_tools = 0.185
        self.btn_tool_inspect, ax = self._icon_button(
            [slot(0), y_tools, btn_w, h], icons.icon_inspect(), "Inspect",
            lambda e: self._set_tool("inspect"), active=True)
        self._tool_axes["inspect"] = ax

        self.btn_tool_seed, ax = self._icon_button(
            [slot(1), y_tools, btn_w, h], icons.icon_seed_tool(), "Seed here",
            lambda e: self._set_tool("seed"))
        self._tool_axes["seed"] = ax

        self.btn_tool_kill, ax = self._icon_button(
            [slot(2), y_tools, btn_w, h], icons.icon_kill_tool(), "Kill here",
            lambda e: self._set_tool("kill"))
        self._tool_axes["kill"] = ax

        self.btn_tool_storm, ax = self._icon_button(
            [slot(3), y_tools, btn_w, h], icons.icon_storm_tool(), "Storm strike",
            lambda e: self._set_tool("storm"))
        self._tool_axes["storm"] = ax

        # ---- Row 2 (middle): weather / nature effects ----
        y_weather = 0.10
        self.btn_rain, _ = self._icon_button(
            [slot(0), y_weather, btn_w, h], icons.icon_rain(), "Trigger rain", self._trigger_rain)
        self.btn_clear, _ = self._icon_button(
            [slot(1), y_weather, btn_w, h], icons.icon_clear_sky(), "Clear skies", self._trigger_clear)
        self.btn_reset_view, _ = self._icon_button(
            [slot(2), y_weather, btn_w, h], icons.icon_reset_view(), "Reset view", self._reset_view)

        # ---- Row 3 (bottom): global actions ----
        y_actions = 0.015
        self.btn_pause, _ = self._icon_button(
            [slot(0), y_actions, btn_w, h], icons.icon_pause(), "Pause", self._toggle_pause)
        self.btn_new, _ = self._icon_button(
            [slot(1), y_actions, btn_w, h], icons.icon_new_world(), "New world", self._new_world)
        self.btn_seed_bulk, _ = self._icon_button(
            [slot(2), y_actions, btn_w, h], icons.icon_scatter_seed(), f"+{BULK_SEED_COUNT} cells", self._reseed)
        self.btn_speed, ax = self._icon_button(
            [slot(3), y_actions, btn_w, h], icons.icon_speed(), "Speed x1", self._cycle_speed)
        self._speed_ax = ax
        self.btn_overlay, ax = self._icon_button(
            [slot(4), y_actions, btn_w, h], icons.icon_palette(), "Color: energy", self._cycle_overlay)
        self._overlay_ax = ax
        self.btn_save, _ = self._icon_button(
            [slot(5), y_actions, btn_w, h], icons.icon_save(), "Save log", self._save_log)

        self.fig.text(x0, y_tools + h + 0.008, "click tool:", fontsize=7.5, color="#8fa0aa")
        self.fig.text(x0, y_weather + h + 0.008, "nature effects:", fontsize=7.5, color="#8fa0aa")
        self.fig.text(x0, y_actions + h + 0.008, "actions:", fontsize=7.5, color="#8fa0aa")

    def _set_active_caption(self, ax, text):
        # the caption Text is the 2nd text artist added to the axes (after the
        # image); simplest robust way to find it is by its position (y~0.11)
        for t in ax.texts:
            if abs(t.get_position()[1] - 0.11) < 1e-6:
                t.set_text(text)
                return

    def _set_tool(self, tool):
        self.active_tool = tool
        for name, ax in self._tool_axes.items():
            ax.set_facecolor(BTN_ACTIVE if name == tool else BTN_COLOR)

    @property
    def speed_mult(self):
        return self.speed_levels[self.speed_idx]

    speed_levels = [1, 2, 4, 8, 16, 32, 64]
    speed_idx = 0

    @property
    def overlay_mode(self):
        return OVERLAY_MODES[self.overlay_idx]

    def _render_world(self):
        img = upscale_with_decor(self.bio.render(), self.bio, PIXEL_SCALE, alive_mask=self.life.alive)
        alive_mask, colors = self.life.cell_color_data(color_by=self.overlay_mode)
        return stamp_cells(img, alive_mask, colors, PIXEL_SCALE)

    # ---------------- Camera (pan/zoom) ----------------
    def _get_view(self):
        x0, x1 = self.ax_world.get_xlim()
        y_lo, y_hi = sorted(self.ax_world.get_ylim())
        return x0, x1, y_lo, y_hi

    def _set_view(self, x0, x1, y0, y1):
        self.ax_world.set_xlim(x0, x1)
        self.ax_world.set_ylim(y1, y0)  # inverted for imshow's default origin

    def _clamp_view(self, x0, x1, y0, y1):
        min_span_x = min(MIN_VIEW_TILES * PIXEL_SCALE, self._full_w)
        min_span_y = min(MIN_VIEW_TILES * PIXEL_SCALE, self._full_h)
        w = float(np.clip(x1 - x0, min_span_x, self._full_w))
        h = float(np.clip(y1 - y0, min_span_y, self._full_h))
        cx = float(np.clip((x0 + x1) / 2, w / 2, self._full_w - w / 2))
        cy = float(np.clip((y0 + y1) / 2, h / 2, self._full_h - h / 2))
        return cx - w / 2, cx + w / 2, cy - h / 2, cy + h / 2

    def _reset_view(self, event):
        self._set_view(0, self._full_w, 0, self._full_h)

    def _on_scroll(self, event):
        if event.inaxes is not self.ax_world or event.xdata is None:
            return
        factor = 0.85 if event.button == "up" else (1 / 0.85)
        x0, x1, y0, y1 = self._get_view()
        fx = (event.xdata - x0) / (x1 - x0)
        fy = (event.ydata - y0) / (y1 - y0)
        w, h = (x1 - x0) * factor, (y1 - y0) * factor
        nx0 = event.xdata - fx * w
        ny0 = event.ydata - fy * h
        nx0, nx1, ny0, ny1 = self._clamp_view(nx0, nx0 + w, ny0, ny0 + h)
        self._set_view(nx0, nx1, ny0, ny1)

    def _on_press(self, event):
        if event.button == 2 and event.inaxes is self.ax_world:  # middle-click drag = pan
            self._pan_active = True
            self._pan_start_px = (event.x, event.y)
            self._pan_start_view = self._get_view()
            return
        if event.button == 1:
            self._on_world_click(event)

    def _on_motion(self, event):
        if not self._pan_active or self._pan_start_px is None:
            return
        bbox = self.ax_world.get_window_extent()
        if bbox.width == 0 or bbox.height == 0:
            return
        dx_px = event.x - self._pan_start_px[0]
        dy_px = event.y - self._pan_start_px[1]
        x0, x1, y0, y1 = self._pan_start_view
        shift_x = -dx_px / bbox.width * (x1 - x0)
        shift_y = dy_px / bbox.height * (y1 - y0)
        nx0, nx1, ny0, ny1 = self._clamp_view(x0 + shift_x, x1 + shift_x, y0 + shift_y, y1 + shift_y)
        self._set_view(nx0, nx1, ny0, ny1)

    def _on_release(self, event):
        if event.button == 2:
            self._pan_active = False
            self._pan_start_px = None
            self._pan_start_view = None

    # ---------------- Controls ----------------
    def _toggle_pause(self, event):
        self.paused = not self.paused
        self._set_active_caption(self.btn_pause.ax, "Resume" if self.paused else "Pause")

    def _new_world(self, event):
        self.bio = Biosphere(seed=None)
        self.life = Life(self.bio, seed=None)
        self.sim_hours_elapsed = 0.0
        self.selected = None
        self._birth_flashes = []
        self._strike_flash = None
        self.hist_t.clear(); self.hist_pop.clear(); self.hist_energy.clear()
        self.hist_births.clear(); self.hist_deaths.clear()
        for g in GENES:
            self.hist_traits[g].clear()
        self.logger = CellLogger(record_every_steps=LOG_EVERY, max_cells_per_snapshot=300)
        self._reset_view(None)

    def _reseed(self, event):
        self.life.seed_random(self.bio, count=BULK_SEED_COUNT)

    def _cycle_speed(self, event):
        self.speed_idx = (self.speed_idx + 1) % len(self.speed_levels)
        self._set_active_caption(self._speed_ax, f"Speed x{self.speed_mult}")

    def _cycle_overlay(self, event):
        self.overlay_idx = (self.overlay_idx + 1) % len(OVERLAY_MODES)
        self._set_active_caption(self._overlay_ax, f"Color: {self.overlay_mode}")

    def _save_log(self, event):
        if len(self.logger) == 0:
            self.info_text.set_text("Nothing to save yet.")
            return
        path = "biosphere_cell_log.csv"
        n = len(self.logger)
        self.logger.save(path, append=True)
        self.logger.clear()
        self.info_text.set_text(f"Saved {n} rows -> {path}\n(appended, buffer cleared)")

    def _trigger_rain(self, event):
        self.bio.set_weather("RAINY", hold_hours=3.0)
        self.info_text.set_text("Triggered: RAIN (holds ~3 sim-hours)")

    def _trigger_clear(self, event):
        self.bio.set_weather("CLEAR", hold_hours=3.0)
        self.info_text.set_text("Triggered: CLEAR SKIES (holds ~3 sim-hours)")

    def _on_world_click(self, event):
        if event.inaxes is not self.ax_world or event.xdata is None:
            return
        x = int(event.xdata // PIXEL_SCALE)
        y = int(event.ydata // PIXEL_SCALE)
        if not (0 <= y < GRID_H and 0 <= x < GRID_W):
            return

        if self.active_tool == "seed":
            self.life.seed_at(self.bio, y, x)
            return
        if self.active_tool == "kill":
            self.life.kill_at(y, x)
            if self.selected == (y, x):
                self.selected = None
                self.selection_marker.set_data([], [])
                self.inspect_text.set_text("(cell removed)")
            return
        if self.active_tool == "storm":
            n_hit = self.life.strike_area(y, x, radius=STORM_RADIUS, kill_prob=STORM_KILL_PROB)
            self._strike_flash = {"y": y, "x": x, "radius": STORM_RADIUS, "ttl": 8, "max_ttl": 8}
            self.info_text.set_text(f"Storm strike at (y={y}, x={x}) -- hit {n_hit} cell(s)")
            return

        # default: inspect
        info = self.life.cell_info(y, x)
        if info is None:
            self.selected = None
            self.inspect_text.set_text(f"No living cell at (y={y}, x={x}).")
            self.selection_marker.set_data([], [])
        else:
            self.selected = (y, x)
            self._render_inspector(info)

    def _render_inspector(self, info):
        g = info["genome"]
        lines = [
            f"cell id      {info['id']}",
            f"parent id    {info['parent_id'] or '(seeded, no parent)'}",
            f"position     y={info['position'][0]}, x={info['position'][1]}",
            f"energy       {info['energy']:.3f}",
            f"durability   {info['durability']:.3f}",
            f"age          {info['age_hours']:.1f} sim-hours",
            "genome:",
            f"  harvest_rate      {g['harvest_rate']:.4f}",
            f"  metabolism_rate   {g['metabolism_rate']:.4f}",
            f"  repro_threshold   {g['repro_threshold']:.4f}",
            f"  mutation_rate     {g['mutation_rate']:.4f}",
            f"  durability_loss   {g['durability_loss']:.4f}",
        ]
        self.inspect_text.set_text("\n".join(lines))
        y, x = info["position"]
        self.selection_marker.set_data([x * PIXEL_SCALE + PIXEL_SCALE / 2],
                                        [y * PIXEL_SCALE + PIXEL_SCALE / 2])

    def _on_close(self, event):
        if hasattr(self, "anim"):
            self.anim.event_source.stop()

    # ---------------- Trait distribution chart ----------------
    def _update_trait_dist(self):
        self.ax_trait_dist.cla()
        self.ax_trait_dist.set_title("Trait distribution (population now)", fontsize=9.5, color="#e8e8f0")
        self.ax_trait_dist.set_facecolor("#12121a")
        gstats = self.life.genome_stats()
        ys = np.arange(len(GENES))
        means_norm, stds_norm, labels = [], [], []
        for g in GENES:
            lo, hi = GENE_BOUNDS[g]
            m, s = gstats[g]["mean"], gstats[g]["std"]
            if m is None:
                means_norm.append(0); stds_norm.append(0)
            else:
                means_norm.append((m - lo) / (hi - lo))
                stds_norm.append((s or 0) / (hi - lo))
            labels.append(g)
        colors = [TRAIT_COLORS[g] for g in GENES]
        self.ax_trait_dist.barh(ys, means_norm, xerr=stds_norm, color=colors,
                                 error_kw={"ecolor": "#e8e8f0", "elinewidth": 1})
        self.ax_trait_dist.set_yticks(ys)
        self.ax_trait_dist.set_yticklabels(labels, fontsize=7, color="#e8e8f0")
        self.ax_trait_dist.set_xlim(0, 1.05)
        self.ax_trait_dist.tick_params(colors="#aaaaaa", labelsize=7)

    # ---------------- Transient visual effects ----------------
    def _update_effects(self):
        for coords in self.life.newborn_coords:
            self._birth_flashes.append([coords[0], coords[1], BIRTH_FLASH_FRAMES])

        alive_flashes = []
        for f in self._birth_flashes:
            f[2] -= 1
            if f[2] > 0:
                alive_flashes.append(f)
        self._birth_flashes = alive_flashes

        if self._birth_flashes:
            xs = [f[1] * PIXEL_SCALE + PIXEL_SCALE / 2 for f in self._birth_flashes]
            ys = [f[0] * PIXEL_SCALE + PIXEL_SCALE / 2 for f in self._birth_flashes]
            self.birth_flash_marker.set_data(xs, ys)
        else:
            self.birth_flash_marker.set_data([], [])

        if self._strike_flash is not None:
            sf = self._strike_flash
            sf["ttl"] -= 1
            frac = max(sf["ttl"], 0) / sf["max_ttl"]
            self.strike_circle.set_center((sf["x"] * PIXEL_SCALE + PIXEL_SCALE / 2,
                                            sf["y"] * PIXEL_SCALE + PIXEL_SCALE / 2))
            self.strike_circle.set_radius(sf["radius"] * PIXEL_SCALE * (1.3 - frac * 0.3))
            self.strike_circle.set_alpha(frac * 0.55)
            if sf["ttl"] <= 0:
                self._strike_flash = None
                self.strike_circle.set_alpha(0.0)

    # ---------------- Animation step ----------------
    def step(self, frame):
        if not self.paused:
            real_dt = (STEP_INTERVAL_MS / 1000.0) * self.speed_mult
            self.bio.step(real_dt)
            self.life.step(self.bio, real_dt)
            self.sim_hours_elapsed += (real_dt * 60.0) / 3600.0
            self.frame_count += 1

            self.logger.maybe_record(self.life, self.sim_hours_elapsed, self.bio.day_count)
            self._update_effects()

            if self.frame_count % STAT_EVERY == 0:
                s = self.life.stats()
                self.hist_t.append(self.sim_hours_elapsed)
                self.hist_pop.append(s["population"])
                self.hist_energy.append(s["avg_energy"])
                self.hist_births.append(s["births"])
                self.hist_deaths.append(s["deaths"])
                for g in GENES:
                    lo, hi = GENE_BOUNDS[g]
                    v = s[f"avg_{g}"]
                    self.hist_traits[g].append(None if v is None else (v - lo) / (hi - lo))
                self._update_trait_dist()

            # keep the inspector panel live if the previously-selected cell is still alive
            if self.selected is not None:
                info = self.life.cell_info(*self.selected)
                if info is None:
                    self.inspect_text.set_text(
                        self.inspect_text.get_text().split("\n")[0] + "\n\n(this cell has died)"
                    )
                    self.selected = None
                else:
                    self._render_inspector(info)
        else:
            self._update_effects()  # let flashes finish decaying even while paused

        self.im.set_data(self._render_world())

        if len(self.hist_t) > 1:
            self.line_pop.set_data(self.hist_t, self.hist_pop)
            self.ax_pop.relim(); self.ax_pop.autoscale_view()
            self.line_energy.set_data(self.hist_t, self.hist_energy)
            self.ax_energy.set_xlim(self.hist_t[0], self.hist_t[-1])
            for g in GENES:
                self.trait_lines[g].set_data(self.hist_t, self.hist_traits[g])
            self.ax_trait_t.set_xlim(self.hist_t[0], self.hist_t[-1])

        s = self.life.stats()
        self.status_text.set_text(
            self.bio.status_string() + ("   |   PAUSED" if self.paused else "")
        )
        self.stats_text.set_text(
            f"pop {s['population']:4d}  |  born {s['births']:5d}  |  died {s['deaths']:5d}  |  "
            f"seeded {s['seeded']:4d}  |  culled {s['culled']:4d}  |  avg energy {s['avg_energy']:.2f}  |  "
            f"avg durability {s['avg_durability']:.2f}"
        )
        if self.frame_count % STAT_EVERY == 0 and self._strike_flash is None:
            self.info_text.set_text(
                f"overlay: {self.overlay_mode}\nper-cell log buffer: {len(self.logger)} rows\n"
                f"(unsaved -- click 'Save log' to flush to CSV)"
            )
        return (self.im,)

    def run(self):
        self.anim = animation.FuncAnimation(
            self.fig, self.step, interval=STEP_INTERVAL_MS,
            blit=False, cache_frame_data=False,
        )
        plt.show()


if __name__ == "__main__":
    dashboard = LifeDashboard(seed=None)
    dashboard.run()
