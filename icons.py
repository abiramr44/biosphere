"""Small procedural pixel-art icons for the dashboard toolbar (RGBA numpy
arrays, rendered with nearest-neighbor interpolation for a crisp blocky
look). Built from geometry instead of unicode glyphs/emoji because most
icon-like emoji glyphs aren't available in matplotlib's default font."""
import numpy as np

SIZE = 16

WHITE = np.array([0.92, 0.94, 0.98])
MAGENTA = np.array([1.0, 0.45, 0.85])
RED = np.array([1.0, 0.35, 0.35])
GREEN = np.array([0.35, 0.95, 0.55])
BLUE = np.array([0.45, 0.75, 1.0])
YELLOW = np.array([1.0, 0.85, 0.4])
GRAY = np.array([0.75, 0.77, 0.82])


def _canvas():
    return np.zeros((SIZE, SIZE, 4), dtype=np.float32)


def _paint(img, mask, color, alpha=1.0):
    img[mask, :3] = color
    img[mask, 3] = alpha


def _grid():
    yy, xx = np.mgrid[0:SIZE, 0:SIZE]
    return yy.astype(np.float32), xx.astype(np.float32)


def _segment_mask(y0, x0, y1, x1, width=1.3):
    """Boolean mask of pixels within `width` of the line segment (y0,x0)-(y1,x1)
    -- a robust way to draw strokes (bolts, rays) without hand-tuned boolean geometry."""
    yy, xx = _grid()
    dx, dy = x1 - x0, y1 - y0
    length2 = dx * dx + dy * dy
    if length2 == 0:
        dist = np.sqrt((xx - x0) ** 2 + (yy - y0) ** 2)
    else:
        t = np.clip(((xx - x0) * dx + (yy - y0) * dy) / length2, 0, 1)
        proj_x = x0 + t * dx
        proj_y = y0 + t * dy
        dist = np.sqrt((xx - proj_x) ** 2 + (yy - proj_y) ** 2)
    return dist <= width


def icon_play():
    img = _canvas()
    yy, xx = _grid()
    cy = SIZE / 2
    x0, x1 = SIZE * 0.28, SIZE * 0.78
    half_h = SIZE * 0.34
    frac = np.clip((x1 - xx) / (x1 - x0), 0, 1)
    tri = (xx >= x0) & (xx <= x1) & (np.abs(yy - cy) <= half_h * frac)
    _paint(img, tri, WHITE)
    return img


def icon_pause():
    img = _canvas()
    yy, xx = _grid()
    top, bot = SIZE * 0.22, SIZE * 0.78
    bar1 = (xx >= SIZE * 0.28) & (xx <= SIZE * 0.44) & (yy >= top) & (yy <= bot)
    bar2 = (xx >= SIZE * 0.56) & (xx <= SIZE * 0.72) & (yy >= top) & (yy <= bot)
    _paint(img, bar1 | bar2, WHITE)
    return img


def icon_new_world():
    """A little globe: circle outline + an equator band."""
    img = _canvas()
    yy, xx = _grid()
    cy = cx = SIZE / 2
    r = SIZE * 0.36
    dist = np.sqrt((xx - cx) ** 2 + (yy - cy) ** 2)
    ring = (dist <= r) & (dist >= r - 1.6)
    equator = (dist <= r) & (np.abs(yy - cy) <= 0.8)
    meridian = (dist <= r) & (np.abs(xx - cx) <= 0.8)
    _paint(img, ring, GREEN)
    _paint(img, equator | meridian, GREEN, alpha=0.7)
    return img


def icon_scatter_seed():
    """Several small dots scattered -- the bulk '+N cells' random-seed action."""
    img = _canvas()
    yy, xx = _grid()
    pts = [(4, 4), (11, 5), (6, 10), (12, 12), (3, 12), (9, 8)]
    for py, px in pts:
        dist = np.sqrt((xx - px) ** 2 + (yy - py) ** 2)
        _paint(img, dist <= 1.3, MAGENTA)
    return img


def icon_speed():
    """Two right-pointing chevrons, like a fast-forward symbol."""
    img = _canvas()
    yy, xx = _grid()
    cy = SIZE / 2
    half_h = SIZE * 0.32

    def chevron(x0, x1):
        frac = np.clip((x1 - xx) / (x1 - x0), 0, 1)
        return (xx >= x0) & (xx <= x1) & (np.abs(yy - cy) <= half_h * frac)

    tri1 = chevron(SIZE * 0.14, SIZE * 0.5)
    tri2 = chevron(SIZE * 0.5, SIZE * 0.86)
    _paint(img, tri1 | tri2, YELLOW)
    return img


def icon_palette():
    """2x2 swatch grid -- the overlay color-mode cycle button."""
    img = _canvas()
    yy, xx = _grid()
    mid = SIZE / 2
    pad = SIZE * 0.14
    q1 = (xx >= pad) & (xx < mid - 1) & (yy >= pad) & (yy < mid - 1)
    q2 = (xx >= mid + 1) & (xx < SIZE - pad) & (yy >= pad) & (yy < mid - 1)
    q3 = (xx >= pad) & (xx < mid - 1) & (yy >= mid + 1) & (yy < SIZE - pad)
    q4 = (xx >= mid + 1) & (xx < SIZE - pad) & (yy >= mid + 1) & (yy < SIZE - pad)
    _paint(img, q1, MAGENTA)
    _paint(img, q2, BLUE)
    _paint(img, q3, GREEN)
    _paint(img, q4, YELLOW)
    return img


def icon_save():
    """Simple floppy-disk glyph."""
    img = _canvas()
    yy, xx = _grid()
    body = (xx >= 3) & (xx <= 13) & (yy >= 2) & (yy <= 13)
    notch = (xx >= 9) & (xx <= 12) & (yy >= 2) & (yy <= 5)
    slot = (xx >= 5) & (xx <= 11) & (yy >= 8) & (yy <= 12)
    _paint(img, body, GRAY)
    img[notch] = [0.1, 0.1, 0.16, 1.0]
    img[slot] = [0.1, 0.1, 0.16, 1.0]
    return img


def icon_inspect():
    """Magnifying glass: ring + diagonal handle."""
    img = _canvas()
    yy, xx = _grid()
    cy, cx = SIZE * 0.42, SIZE * 0.42
    r = SIZE * 0.26
    dist = np.sqrt((xx - cx) ** 2 + (yy - cy) ** 2)
    ring = (dist <= r) & (dist >= r - 1.6)
    _paint(img, ring, WHITE)
    handle = (xx - cx >= r * 0.55) & (yy - cy >= r * 0.55) & \
             (np.abs((xx - cx) - (yy - cy)) <= 1.1) & (xx <= SIZE - 1) & (yy <= SIZE - 1)
    _paint(img, handle, WHITE)
    return img


def icon_seed_tool():
    """Circle with a '+' inside -- place a single cell where you click."""
    img = _canvas()
    yy, xx = _grid()
    cy = cx = SIZE / 2
    r = SIZE * 0.36
    dist = np.sqrt((xx - cx) ** 2 + (yy - cy) ** 2)
    ring = (dist <= r) & (dist >= r - 1.4)
    plus = ((np.abs(xx - cx) <= 1.0) & (np.abs(yy - cy) <= r * 0.6)) | \
           ((np.abs(yy - cy) <= 1.0) & (np.abs(xx - cx) <= r * 0.6))
    _paint(img, ring, MAGENTA)
    _paint(img, plus, MAGENTA)
    return img


def icon_kill_tool():
    """A red X -- remove the cell you click on."""
    img = _canvas()
    yy, xx = _grid()
    d1 = np.abs((xx - yy))
    d2 = np.abs((xx + yy) - (SIZE - 1))
    margin = (xx >= 2) & (xx <= SIZE - 3) & (yy >= 2) & (yy <= SIZE - 3)
    cross = ((d1 <= 1.3) | (d2 <= 1.3)) & margin
    _paint(img, cross, RED)
    return img


def icon_storm_tool():
    """Lightning bolt -- the 'storm strike' area-disaster click-tool."""
    img = _canvas()
    bolt = _segment_mask(2, 9.5, 8, 6, width=1.2) | \
           _segment_mask(8, 6, 7, 9, width=1.2) | \
           _segment_mask(7, 9, 13, 5.5, width=1.2)
    _paint(img, bolt, YELLOW)
    return img


def icon_clear_sky():
    """Sun -- manual 'clear skies' weather trigger."""
    img = _canvas()
    yy, xx = _grid()
    cy = cx = SIZE / 2
    r = SIZE * 0.22
    dist = np.sqrt((xx - cx) ** 2 + (yy - cy) ** 2)
    _paint(img, dist <= r, YELLOW)
    rays = np.zeros((SIZE, SIZE), dtype=bool)
    for k in range(8):
        a = k * np.pi / 4
        y0, x0 = cy + np.sin(a) * (r + 1.2), cx + np.cos(a) * (r + 1.2)
        y1, x1 = cy + np.sin(a) * (r + 3.4), cx + np.cos(a) * (r + 3.4)
        rays |= _segment_mask(y0, x0, y1, x1, width=0.8)
    _paint(img, rays, YELLOW)
    return img


def icon_rain():
    """Cloud with raindrops -- manual 'trigger rain' weather effect."""
    img = _canvas()
    yy, xx = _grid()
    cy, cx = SIZE * 0.36, SIZE * 0.5
    rx, ry = SIZE * 0.34, SIZE * 0.21
    cloud = (((xx - cx) / rx) ** 2 + ((yy - cy) / ry) ** 2) <= 1.0
    _paint(img, cloud, GRAY)
    drops = np.zeros((SIZE, SIZE), dtype=bool)
    for dx0 in (SIZE * 0.32, SIZE * 0.5, SIZE * 0.68):
        drops |= _segment_mask(SIZE * 0.62, dx0, SIZE * 0.92, dx0 - SIZE * 0.10, width=0.8)
    _paint(img, drops, BLUE)
    return img


def icon_reset_view():
    """Four corner brackets -- reset the camera to the full-world view."""
    img = _canvas()
    m, L = 2, 4
    img[m:m + L + 1, m, :] = [*WHITE, 1.0]
    img[m, m:m + L + 1, :] = [*WHITE, 1.0]
    img[m:m + L + 1, SIZE - 1 - m, :] = [*WHITE, 1.0]
    img[m, SIZE - 1 - m - L:SIZE - m, :] = [*WHITE, 1.0]
    img[SIZE - 1 - m - L:SIZE - m, m, :] = [*WHITE, 1.0]
    img[SIZE - 1 - m, m:m + L + 1, :] = [*WHITE, 1.0]
    img[SIZE - 1 - m - L:SIZE - m, SIZE - 1 - m, :] = [*WHITE, 1.0]
    img[SIZE - 1 - m, SIZE - 1 - m - L:SIZE - m, :] = [*WHITE, 1.0]
    return img
