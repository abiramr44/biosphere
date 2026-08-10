import numpy as np
from numpy.lib.stride_tricks import sliding_window_view

# ---- World config ----
# Smaller sandbox to start with (was 192x128) -- easier to see individual
# beings spaced out on screen; expand later once the smaller scale is proven out.
GRID_W, GRID_H = 64, 48
WATER_LEVEL = 0.34
MOUNTAIN_LEVEL = 0.80

# ---- Static terrain decoration (trees / rocks) ----
# Purely cosmetic per-tile dressing, precomputed once per world so it doesn't
# flicker as weather/moisture change. Consumed by the dashboard's renderer
# to stamp small pixel-art icons on top of the terrain.
DECOR_NONE = 0
DECOR_TREE = 1
DECOR_ROCK = 2

# 1 real second = 60 simulated seconds -> 24 sim-hours pass in 24 real minutes
SIM_SECONDS_PER_REAL_SECOND = 60.0
SECONDS_PER_DAY = 24 * 3600


def _gaussian_blur(arr, sigma=4.0, iterations=3):
    """Smooth a 2D numpy array using a small separable Gaussian kernel
    so procedural noise turns into natural-looking terrain instead of static.
    Implemented as two vectorized 1D passes (zero-padded, same as the
    previous torch conv2d version) via sliding_window_view -- no torch
    dependency needed for a blur this small."""
    t = arr.astype(np.float32)
    size = int(sigma * 3) | 1  # force odd
    ax = np.arange(size, dtype=np.float32) - size // 2
    k1d = np.exp(-(ax ** 2) / (2 * sigma ** 2))
    k1d /= k1d.sum()
    pad = size // 2
    for _ in range(iterations):
        # horizontal pass
        padded = np.pad(t, ((0, 0), (pad, pad)), mode="constant")
        windows = sliding_window_view(padded, size, axis=1)
        t = np.tensordot(windows, k1d, axes=([2], [0]))
        # vertical pass -- sliding_window_view always appends the new
        # window axis at the end, regardless of which axis we slide on
        padded = np.pad(t, ((pad, pad), (0, 0)), mode="constant")
        windows = sliding_window_view(padded, size, axis=0)
        t = np.tensordot(windows, k1d, axes=([2], [0]))
    return t


def _lerp(a, b, t):
    return a + (b - a) * t


# ---- Cached color palette (module-level so render() doesn't rebuild these
# numpy arrays from scratch on every single frame) ----
_WATER_SHALLOW = np.array([0.20, 0.85, 0.80])
_WATER_MID = np.array([0.10, 0.48, 0.78])
_WATER_DEEP = np.array([0.03, 0.14, 0.42])
_DRY = np.array([0.78, 0.68, 0.22])
_LUSH = np.array([0.12, 0.72, 0.20])
_HIGHLAND = np.array([0.30, 0.55, 0.22])
_BEACH_COLOR = np.array([0.96, 0.85, 0.50])
_ROCK_LOW = np.array([0.42, 0.38, 0.40])
_ROCK_HIGH = np.array([0.62, 0.60, 0.62])
_SNOW_COLOR = np.array([0.96, 0.97, 1.0])
_CLOUD_COLOR = np.array([0.88, 0.88, 0.92])


class Biosphere:
    def __init__(self, seed=None):
        self.rng = np.random.default_rng(seed)
        self.sim_seconds = 6 * 3600  # start at 6:00 AM
        self.day_count = 1

        self.weather = "CLEAR"  # CLEAR, CLOUDY, RAINY
        self._weather_timer = 0.0
        self._weather_change_at = self.rng.uniform(3, 7) * 3600  # sim-seconds

        self._generate_terrain()
        self._generate_decor()
        self._init_clouds()
        self.moisture = np.where(self.water_mask, 1.0, 0.15).astype(np.float32)
        self._cloud_density_mix = 0.05
        self.current_clouds = self.cloud_field[:, :GRID_W] * self._cloud_density_mix
        # Fixed per-pixel dither noise (centered at 0) for retro pixel-grain texture
        self._dither = self.rng.uniform(-1, 1, size=(GRID_H, GRID_W)).astype(np.float32)
        # A second, smoother noise field for broader organic mottling (patches
        # of slightly different shade within a terrain type, not just per-pixel grain)
        raw_detail = self.rng.uniform(-1, 1, size=(GRID_H, GRID_W)).astype(np.float32)
        self._terrain_detail = _gaussian_blur(raw_detail, sigma=1.4, iterations=2)
        self._terrain_detail /= (np.abs(self._terrain_detail).max() + 1e-8)

    # ---------------- Terrain generation ----------------
    def _generate_terrain(self):
        raw = self.rng.random((GRID_H, GRID_W))
        smoothed = _gaussian_blur(raw, sigma=5.0, iterations=4)
        # Blurred noise is naturally skewed (not uniformly distributed), so a
        # min/max or percentile rescale doesn't give predictable land/water
        # ratios. Rank-normalizing (histogram equalization) fixes that: it's a
        # monotonic transform, so the smooth spatial shape is preserved, but
        # the output values are uniformly spread over [0,1] -- meaning
        # WATER_LEVEL=0.34 reliably gives ~34% water, etc.
        flat = smoothed.ravel()
        ranks = np.argsort(np.argsort(flat))
        elevation = (ranks / (len(flat) - 1)).reshape(smoothed.shape)
        self.elevation = elevation

        self.water_mask = elevation < WATER_LEVEL
        self.beach_mask = (elevation >= WATER_LEVEL) & (elevation < WATER_LEVEL + 0.05)
        self.mountain_mask = elevation > MOUNTAIN_LEVEL
        self.land_mask = ~self.water_mask & ~self.beach_mask & ~self.mountain_mask

    def _generate_decor(self):
        """Scatter trees on lush-ish land and rocks on mountains. Based on
        dedicated static noise fields (not the dynamic moisture array, which
        shifts with weather) so tile decoration doesn't flicker over time."""
        self.decor_kind = np.zeros((GRID_H, GRID_W), dtype=np.uint8)

        # Thresholds tuned by eye (rendered + inspected) so decoration reads
        # as scattered individual trees/rocks, not a solid carpet texture.
        raw_fert = self.rng.random((GRID_H, GRID_W))
        fertility = _gaussian_blur(raw_fert, sigma=2.0, iterations=2)
        fertility = (fertility - fertility.min()) / (fertility.max() - fertility.min() + 1e-8)
        tree_candidate = self.land_mask & (fertility > 0.76) & \
            (self.elevation < MOUNTAIN_LEVEL - 0.05)
        self.decor_kind[tree_candidate] = DECOR_TREE

        raw_rock = self.rng.random((GRID_H, GRID_W))
        rockiness = _gaussian_blur(raw_rock, sigma=1.5, iterations=2)
        rockiness = (rockiness - rockiness.min()) / (rockiness.max() - rockiness.min() + 1e-8)
        rock_candidate = self.mountain_mask & (rockiness > 0.68)
        self.decor_kind[rock_candidate] = DECOR_ROCK

    def _init_clouds(self):
        pad = 20
        raw = self.rng.random((GRID_H, GRID_W + pad))
        self.cloud_field = _gaussian_blur(raw, sigma=3.5, iterations=3)
        self.cloud_field = np.clip((self.cloud_field - 0.4) * 2.0, 0, 1)
        self._cloud_offset = 0.0

    # ---------------- Time / astronomy ----------------
    @property
    def sim_hour(self):
        return (self.sim_seconds % SECONDS_PER_DAY) / 3600.0

    def sunlight_intensity(self):
        h = self.sim_hour
        if 6 <= h <= 18:
            return float(np.clip(np.sin(np.pi * (h - 6) / 12), 0, 1))
        return 0.0

    def _sky_tint(self):
        """Returns (brightness, tint_rgb) for the current hour."""
        h = self.sim_hour
        night = np.array([0.08, 0.10, 0.22])
        dawn = np.array([0.95, 0.55, 0.35])
        day = np.array([1.0, 1.0, 0.98])
        dusk = np.array([0.85, 0.40, 0.35])

        if h < 5 or h >= 20:
            return 0.18, night
        if 5 <= h < 7:
            t = (h - 5) / 2
            return _lerp(0.18, 1.0, t), _lerp(night, dawn, t)
        if 7 <= h < 9:
            t = (h - 7) / 2
            return _lerp(1.0, 1.0, t), _lerp(dawn, day, t)
        if 9 <= h < 17:
            return 1.0, day
        if 17 <= h < 19:
            t = (h - 17) / 2
            return _lerp(1.0, 0.9, t), _lerp(day, dusk, t)
        t = (h - 19) / 1
        return _lerp(0.9, 0.18, t), _lerp(dusk, night, t)

    # ---------------- Weather ----------------
    def _update_weather(self, sim_dt):
        self._weather_timer += sim_dt
        if self._weather_timer >= self._weather_change_at:
            self._weather_timer = 0.0
            self._weather_change_at = self.rng.uniform(3, 7) * 3600
            weights = {"CLEAR": [0.5, 0.35, 0.15],
                       "CLOUDY": [0.35, 0.4, 0.25],
                       "RAINY": [0.3, 0.4, 0.3]}[self.weather]
            self.weather = self.rng.choice(["CLEAR", "CLOUDY", "RAINY"], p=weights)

        target_density = {"CLEAR": 0.05, "CLOUDY": 0.55, "RAINY": 0.85}[self.weather]
        self._cloud_density_mix = getattr(self, "_cloud_density_mix", 0.05)
        self._cloud_density_mix += (target_density - self._cloud_density_mix) * 0.02

        # scroll clouds across the sky
        self._cloud_offset += sim_dt * 0.0015
        shift = int(self._cloud_offset) % self.cloud_field.shape[1]
        scrolled = np.roll(self.cloud_field, -shift, axis=1)
        self.current_clouds = scrolled[:, :GRID_W] * self._cloud_density_mix

    def set_weather(self, weather, hold_hours=3.0):
        """Manually force weather (GUI 'nature effect' trigger). Holds for
        hold_hours of sim-time before the normal random weather cycle can
        take over again -- a temporary override, not a permanent switch."""
        assert weather in ("CLEAR", "CLOUDY", "RAINY")
        self.weather = weather
        self._weather_timer = 0.0
        self._weather_change_at = hold_hours * 3600

    # ---------------- Moisture ----------------
    def _update_moisture(self, sim_dt):
        sun = self.sunlight_intensity()
        evap = 0.000006 * sun * sim_dt
        self.moisture -= evap
        if self.weather == "RAINY":
            self.moisture += 0.00004 * sim_dt
        self.moisture = np.clip(self.moisture, 0.0, 1.0)
        self.moisture[self.water_mask] = 1.0

    # ---------------- Step ----------------
    def step(self, real_dt):
        sim_dt = real_dt * SIM_SECONDS_PER_REAL_SECOND
        self.sim_seconds += sim_dt
        if self.sim_seconds >= SECONDS_PER_DAY:
            self.sim_seconds -= SECONDS_PER_DAY
            self.day_count += 1

        self._update_weather(sim_dt)
        self._update_moisture(sim_dt)

    # ---------------- Rendering ----------------
    def render(self):
        h, w = GRID_H, GRID_W
        img = np.zeros((h, w, 3), dtype=np.float32)

        # ---- Water: 3-stop gradient (shallow turquoise -> mid blue -> deep navy) ----
        depth = np.clip((WATER_LEVEL - self.elevation) / WATER_LEVEL, 0, 1) ** 0.65
        near = depth < 0.5
        t_near = np.clip(depth / 0.5, 0, 1)[..., None]
        t_far = np.clip((depth - 0.5) / 0.5, 0, 1)[..., None]
        water_color = np.where(
            near[..., None],
            _lerp(_WATER_SHALLOW, _WATER_MID, t_near),
            _lerp(_WATER_MID, _WATER_DEEP, t_far),
        )

        # ---- Grass: moisture (dry<->lush) blended with elevation (lowland<->highland) ----
        moisture_color = _lerp(_DRY, _LUSH, self.moisture[..., None])
        land_elev_t = np.clip((self.elevation - WATER_LEVEL) / (MOUNTAIN_LEVEL - WATER_LEVEL), 0, 1)
        grass_color = _lerp(moisture_color, _HIGHLAND, (land_elev_t ** 1.5)[..., None])

        # ---- Mountain: smooth rock -> snow gradient instead of a hard cutoff ----
        mtn_t = np.clip((self.elevation - MOUNTAIN_LEVEL) / (1.0 - MOUNTAIN_LEVEL), 0, 1)
        rock_color = _lerp(_ROCK_LOW, _ROCK_HIGH, mtn_t[..., None])
        snow_t = np.clip((self.elevation - 0.90) / 0.10, 0, 1)[..., None]
        mountain_color = _lerp(rock_color, _SNOW_COLOR, snow_t)

        img[self.water_mask] = water_color[self.water_mask]
        img[self.beach_mask] = _BEACH_COLOR
        img[self.land_mask] = grass_color[self.land_mask]
        img[self.mountain_mask] = mountain_color[self.mountain_mask]

        # Organic mottling (broad soft patches) + fine pixel-grain dither, layered
        img = np.clip(img + self._terrain_detail[..., None] * 0.05
                      + self._dither[..., None] * 0.045, 0, 1)

        brightness, tint = self._sky_tint()
        cloud_shadow = 1.0 - self.current_clouds * 0.6
        light = brightness * cloud_shadow[..., None]

        img = img * light + tint[None, None, :] * (1 - brightness) * 0.5
        img = np.clip(img, 0, 1)

        cloud_color = _CLOUD_COLOR
        img = img * (1 - self.current_clouds[..., None] * 0.5) + \
              cloud_color[None, None, :] * (self.current_clouds[..., None] * 0.5)

        if self.weather == "RAINY":
            sparkle = self.rng.random((h, w)) > 0.985
            img[sparkle] = np.clip(img[sparkle] + 0.3, 0, 1)

        return np.clip(img, 0, 1)

    def local_sunlight(self):
        """Per-cell effective sunlight in [0,1], factoring in time of day
        and cloud shadow at each grid position. Used by organisms to
        compute energy income."""
        base = self.sunlight_intensity()
        cloud_shadow = 1.0 - self.current_clouds * 0.6
        return base * cloud_shadow

    def status_string(self):
        hh = int(self.sim_hour)
        mm = int((self.sim_hour - hh) * 60)
        return (f"Day {self.day_count}  |  {hh:02d}:{mm:02d}  |  "
                f"{self.weather}  |  sun {self.sunlight_intensity():.2f}")