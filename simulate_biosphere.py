import numpy as np
import matplotlib.pyplot as plt
import matplotlib.animation as animation
from matplotlib.widgets import Button

from environment import Biosphere, GRID_W, GRID_H

STEP_INTERVAL_MS = 50   # ~20 fps; real_dt passed to Biosphere.step() matches this
PIXEL_SCALE = 4           # upscale factor for a chunky pixel-art look


def upscale(img, scale):
    return np.kron(img, np.ones((scale, scale, 1)))


class BiosphereViewer:
    def __init__(self, seed=None):
        self.bio = Biosphere(seed=seed)
        self.paused = False
        self._build_ui()

    def _build_ui(self):
        plt.style.use("dark_background")
        self.fig, self.ax = plt.subplots(
            figsize=(GRID_W * PIXEL_SCALE / 100, GRID_H * PIXEL_SCALE / 100 + 1.4)
        )
        self.fig.canvas.manager.set_window_title("Biosphere — Living World")
        self.fig.patch.set_facecolor("#0a0a12")

        self.ax.set_position([0.03, 0.13, 0.94, 0.80])
        self.ax.axis("off")

        img = upscale(self.bio.render(), PIXEL_SCALE)
        self.im = self.ax.imshow(img, interpolation="nearest")

        self.ax.set_title(
            "8-bit Biosphere — sunlight, water, weather, day/night cycle",
            fontsize=11, color="#e8e8f0", pad=10,
        )

        self.status_text = self.fig.text(
            0.03, 0.065, "", fontsize=10, color="#8fd3ff", family="monospace"
        )

        ax_pause = self.fig.add_axes([0.03, 0.015, 0.20, 0.04])
        self.btn_pause = Button(ax_pause, "Pause", color="#2a2a3a", hovercolor="#3a3a52")
        self.btn_pause.label.set_color("#e8e8f0")
        self.btn_pause.on_clicked(self._toggle_pause)

        ax_new = self.fig.add_axes([0.26, 0.015, 0.20, 0.04])
        self.btn_new = Button(ax_new, "New world", color="#2a2a3a", hovercolor="#3a3a52")
        self.btn_new.label.set_color("#e8e8f0")
        self.btn_new.on_clicked(self._new_world)

        ax_fast = self.fig.add_axes([0.49, 0.015, 0.20, 0.04])
        self.speed_levels = [1, 2, 4, 8, 16, 32, 64]
        self.speed_idx = 0
        self.btn_fast = Button(ax_fast, "Speed x1", color="#2a2a3a", hovercolor="#3a3a52")
        self.btn_fast.label.set_color("#e8e8f0")
        self.btn_fast.on_clicked(self._cycle_speed)

        self.fig.canvas.mpl_connect("close_event", self._on_close)

    def _on_close(self, event):
        # Ensures the animation timer fully stops and the process exits
        # cleanly when the window is closed, instead of occasionally
        # leaving a hung background process.
        if hasattr(self, "anim"):
            self.anim.event_source.stop()

    @property
    def speed_mult(self):
        return self.speed_levels[self.speed_idx]

    def _toggle_pause(self, event):
        self.paused = not self.paused
        self.btn_pause.label.set_text("Resume" if self.paused else "Pause")

    def _new_world(self, event):
        self.bio = Biosphere(seed=None)

    def _cycle_speed(self, event):
        self.speed_idx = (self.speed_idx + 1) % len(self.speed_levels)
        self.btn_fast.label.set_text(f"Speed x{self.speed_mult}")

    def step(self, frame):
        if not self.paused:
            real_dt = (STEP_INTERVAL_MS / 1000.0) * self.speed_mult
            self.bio.step(real_dt)

        self.im.set_data(upscale(self.bio.render(), PIXEL_SCALE))
        self.status_text.set_text(
            self.bio.status_string() + ("   |   PAUSED" if self.paused else "")
        )
        return (self.im,)

    def run(self):
        self.anim = animation.FuncAnimation(
            self.fig, self.step, interval=STEP_INTERVAL_MS,
            blit=False, cache_frame_data=False,
        )
        plt.show()


if __name__ == "__main__":
    viewer = BiosphereViewer(seed=None)
    viewer.run()