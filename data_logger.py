"""Per-cell data logging for the biosphere sim.

Unlike Life.stats() (population-wide averages only), this records a row per
living cell at each sampling interval: its genome traits, energy, durability,
age, and lineage (id / parent id). That makes it possible to answer questions
like "how did harvest_rate drift across generations" or "did lineage X die
out" after the fact, by loading the log into pandas.
"""
import numpy as np

try:
    import pandas as pd
except ImportError:
    pd = None

from life import GENES


class CellLogger:
    def __init__(self, record_every_steps=20, max_cells_per_snapshot=None, max_buffer_records=500_000):
        """record_every_steps: how often (in Life.step calls) to take a
        snapshot of the whole living population.
        max_cells_per_snapshot: if set, randomly subsample living cells when
        the population exceeds this, to bound memory/disk use on long runs.
        max_buffer_records: safety cap on in-memory rows for long-running
        GUI sessions -- oldest rows are dropped once exceeded. Call save()
        periodically to flush to disk instead of relying on this.
        """
        self.record_every_steps = record_every_steps
        self.max_cells_per_snapshot = max_cells_per_snapshot
        self.max_buffer_records = max_buffer_records
        self.records = []
        self._rng = np.random.default_rng()

    def maybe_record(self, life, sim_hours, day_count):
        if life.step_count % self.record_every_steps != 0:
            return
        self.record(life, sim_hours, day_count)

    def record(self, life, sim_hours, day_count):
        ys, xs = np.where(life.alive)
        n = len(ys)
        if n == 0:
            return
        if self.max_cells_per_snapshot and n > self.max_cells_per_snapshot:
            idx = self._rng.choice(n, size=self.max_cells_per_snapshot, replace=False)
            ys, xs = ys[idx], xs[idx]
            n = len(ys)

        step = life.step_count
        for k in range(n):
            y, x = int(ys[k]), int(xs[k])
            row = {
                "step": step,
                "sim_hours": sim_hours,
                "day": day_count,
                "cell_id": int(life.cell_id[y, x]),
                "parent_id": int(life.parent_id[y, x]),
                "y": y,
                "x": x,
                "energy": float(life.energy[y, x]),
                "durability": float(life.durability[y, x]),
                "age_hours": float(life.age[y, x]),
            }
            for g in GENES:
                row[g] = float(life.genome[g][y, x])
            self.records.append(row)

        if self.max_buffer_records and len(self.records) > self.max_buffer_records:
            self.records = self.records[-self.max_buffer_records:]

    def to_dataframe(self):
        if pd is None:
            raise RuntimeError("pandas is required to build a DataFrame from the log")
        return pd.DataFrame.from_records(self.records)

    def save(self, path, append=False):
        import os
        df = self.to_dataframe()
        if path.endswith(".parquet"):
            if append and os.path.exists(path):
                df = pd.concat([pd.read_parquet(path), df], ignore_index=True)
            df.to_parquet(path, index=False)
        else:
            write_header = not (append and os.path.exists(path))
            df.to_csv(path, mode="a" if append else "w", header=write_header, index=False)
        return df

    def clear(self):
        self.records = []

    def __len__(self):
        return len(self.records)
