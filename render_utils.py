"""Display-time rendering helpers shared by the simulate_* scripts: upscaling
the low-res simulation grid to screen pixels, stamping small tree/rock decor
icons on top (from Biosphere.decor_kind), and drawing subtle tile-grid lines
so the world reads as tiled rather than a smooth gradient blob."""
import numpy as np

from environment import DECOR_TREE, DECOR_ROCK

_TEMPLATE_CACHE = {}


def _tree_template(scale):
    yy, xx = np.mgrid[0:scale, 0:scale].astype(np.float32)
    cy = cx = (scale - 1) / 2.0
    dist = np.sqrt((yy - cy) ** 2 + (xx - cx) ** 2)
    canopy = dist < scale * 0.42
    color = np.tile(np.array([0.10, 0.50, 0.14], dtype=np.float32), (scale, scale, 1))
    rim = canopy & (dist >= scale * 0.28)
    color[rim] = np.array([0.06, 0.38, 0.10], dtype=np.float32)
    alpha = canopy.astype(np.float32)
    trunk_row = scale - 1
    trunk_col = int(round(cx))
    color[trunk_row, trunk_col] = np.array([0.38, 0.24, 0.11], dtype=np.float32)
    alpha[trunk_row, trunk_col] = 1.0
    return color, alpha


def _rock_template(scale):
    yy, xx = np.mgrid[0:scale, 0:scale].astype(np.float32)
    cy = (scale - 1) / 2.0
    cx = (scale - 1) / 2.0
    dist = np.sqrt(((yy - cy) / 1.15) ** 2 + (xx - cx) ** 2)
    body = dist < scale * 0.40
    color = np.tile(np.array([0.55, 0.53, 0.56], dtype=np.float32), (scale, scale, 1))
    highlight = body & (yy < cy)
    color[highlight] = np.array([0.68, 0.66, 0.70], dtype=np.float32)
    alpha = body.astype(np.float32)
    return color, alpha


def _templates(scale):
    if scale not in _TEMPLATE_CACHE:
        _TEMPLATE_CACHE[scale] = {
            DECOR_TREE: _tree_template(scale),
            DECOR_ROCK: _rock_template(scale),
        }
    return _TEMPLATE_CACHE[scale]


_SLIME_CACHE = {}


def _slime_template(scale):
    """Shaded pseudo-3D 'slime blob' sprite template: a soft-edged rounded
    body with a bright specular highlight (upper-left) and a darker shadow
    rim (lower-right), like the tree/rock decor templates above but with a
    softer (non-boolean) alpha edge for a glossy blob look instead of hard
    pixel-art geometry. Returns (shade, alpha), both (scale, scale) float32:
    shade is a brightness multiplier to tint the per-cell base color (from
    Life.cell_color_data), alpha is the blend/opacity mask."""
    yy, xx = np.mgrid[0:scale, 0:scale].astype(np.float32)
    cy = cx = (scale - 1) / 2.0
    r = scale * 0.40

    dist = np.sqrt((yy - cy) ** 2 + (xx - cx) ** 2)
    alpha = np.clip((r - dist) / 1.4 + 0.5, 0.0, 1.0)

    # Specular highlight toward the upper-left, shadow rim toward the
    # lower-right -- gives the flat blob a rounded, lit-from-one-side look.
    hl_y, hl_x = cy - r * 0.55, cx - r * 0.55
    hl_dist = np.sqrt((yy - hl_y) ** 2 + (xx - hl_x) ** 2)
    highlight = np.clip(1.0 - hl_dist / (r * 0.95), 0.0, 1.0)

    rim_y, rim_x = cy + r * 0.55, cx + r * 0.55
    rim_dist = np.sqrt((yy - rim_y) ** 2 + (xx - rim_x) ** 2)
    rim = np.clip(1.0 - rim_dist / (r * 1.15), 0.0, 1.0)

    shade = 0.72 + 0.60 * highlight - 0.24 * rim
    shade = np.clip(shade, 0.5, 1.35).astype(np.float32)
    return shade, alpha.astype(np.float32)


def _slime_template_cached(scale):
    if scale not in _SLIME_CACHE:
        _SLIME_CACHE[scale] = _slime_template(scale)
    return _SLIME_CACHE[scale]


def stamp_cells(base, alive_mask, colors, scale):
    """Stamp a shaded pseudo-3D slime-blob sprite for every living cell onto
    an already-upscaled image, tinted per-cell by `colors` (an (H,W,3) array,
    e.g. from Life.cell_color_data -- energy/genome gradient + low-durability
    warning blend). Same vectorized gather-scatter pattern as the tree/rock
    stamping in upscale_with_decor: build one template, index every instance
    at once instead of looping in Python, so this stays fast at high sim
    speed even with hundreds of living cells."""
    ys, xs = np.where(alive_mask)
    if len(ys) == 0:
        return base
    shade, alpha = _slime_template_cached(scale)
    idx = np.arange(scale)
    by = ys[:, None, None] * scale + idx[None, :, None]
    bx = xs[:, None, None] * scale + idx[None, None, :]
    by = np.broadcast_to(by, (len(ys), scale, scale))
    bx = np.broadcast_to(bx, (len(ys), scale, scale))

    block = base[by, bx]
    cell_col = colors[ys, xs]  # (N, 3)
    tinted = np.clip(cell_col[:, None, None, :] * shade[None, :, :, None], 0.0, 1.0)
    a = alpha[None, :, :, None]
    block = block * (1 - a) + tinted * a
    base[by, bx] = block
    return base


def upscale_with_decor(img, bio, scale, alive_mask=None, grid_lines=True, grid_darken=0.80):
    """Nearest-neighbor upscale `img` (H,W,3) by `scale`, then stamp
    tree/rock icons from bio.decor_kind (skipping tiles with a living cell,
    if alive_mask is given, so creatures stay visible) and darken tile
    borders slightly for a chunkier, more 'tiled' look."""
    base = np.kron(img, np.ones((scale, scale, 1), dtype=img.dtype))

    decor_kind = getattr(bio, "decor_kind", None)
    if decor_kind is not None:
        templates = _templates(scale)
        mask_free = ~alive_mask if alive_mask is not None else np.ones_like(decor_kind, dtype=bool)
        idx = np.arange(scale)
        for kind, (color, alpha) in templates.items():
            ys, xs = np.where((decor_kind == kind) & mask_free)
            if len(ys) == 0:
                continue
            by = ys[:, None, None] * scale + idx[None, :, None]
            bx = xs[:, None, None] * scale + idx[None, None, :]
            by = np.broadcast_to(by, (len(ys), scale, scale))
            bx = np.broadcast_to(bx, (len(ys), scale, scale))
            block = base[by, bx]
            a = alpha[None, :, :, None]
            block = block * (1 - a) + color[None, :, :, :] * a
            base[by, bx] = block

    if grid_lines and scale > 1:
        base[::scale, :, :] *= grid_darken
        base[:, ::scale, :] *= grid_darken

    return np.clip(base, 0, 1)
