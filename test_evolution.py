"""Headless evolution test -- no GUI, no rendering.

Runs the sim for many simulated days and tracks population-wide genome trait
means over time. The point is to empirically verify that natural selection
is actually happening (trait means drift away from the seeded defaults and
population survives) rather than hand-tuning the environment to force a
specific outcome.

Usage:
    python test_evolution.py --days 200
"""
import argparse
import pickle
import sys
import numpy as np

from environment import Biosphere
from life import Life, GENES, GENE_DEFAULT

# Sim-hours per step -- matches roughly what STEP_INTERVAL_MS=50 at speed x64
# produces in the GUI (real_dt=3.2s -> sim_dt=192s=0.0533hr), but we drive it
# directly here instead of through wall-clock frames.
DT_HOURS = 0.05
STEPS_PER_DAY = int(round(24 / DT_HOURS))


def save_checkpoint(path, bio, life, history, extinctions):
    with open(path, "wb") as f:
        pickle.dump({"bio": bio, "life": life, "history": history,
                     "extinctions": extinctions}, f)


def load_checkpoint(path):
    with open(path, "rb") as f:
        d = pickle.load(f)
    return d["bio"], d["life"], d["history"], d["extinctions"]


def run(days, seed, reseed_on_extinction=True, log_every_days=10, verbose=True,
        bio=None, life=None, history=None, extinctions=0,
        checkpoint_path=None, checkpoint_every_days=None):
    if bio is None:
        bio = Biosphere(seed=seed)
    if life is None:
        life = Life(bio, seed=seed)
    if history is None:
        history = []  # (day, population, {trait: mean})

    start_day = history[-1][0] if history else 0

    # bio.step()/life.step() both take "real_dt" seconds and internally scale
    # by SIM_SECONDS_PER_REAL_SECOND (60) to get sim-seconds. Back that out
    # so each call advances the sim by exactly DT_HOURS.
    real_dt = (DT_HOURS * 3600.0) / 60.0

    total_steps = days * STEPS_PER_DAY
    for step in range(total_steps):
        bio.step(real_dt)
        life.step(bio, real_dt)

        if life.population == 0 and reseed_on_extinction:
            extinctions += 1
            life.seed_random(bio, count=25)

        if (step + 1) % STEPS_PER_DAY == 0:
            day = start_day + (step + 1) // STEPS_PER_DAY
            gstats = life.genome_stats()
            means = {g: gstats[g]["mean"] for g in GENES}
            history.append((day, life.population, means))
            if verbose and day % log_every_days == 0:
                mean_str = "  ".join(
                    f"{g}={means[g]:.4f}" if means[g] is not None else f"{g}=NA"
                    for g in GENES
                )
                print(f"day {day:4d}  pop {life.population:5d}  {mean_str}", flush=True)

            if checkpoint_path and checkpoint_every_days and day % checkpoint_every_days == 0:
                save_checkpoint(checkpoint_path, bio, life, history, extinctions)

    if checkpoint_path:
        save_checkpoint(checkpoint_path, bio, life, history, extinctions)

    return bio, life, history, extinctions


def summarize(history):
    valid = [h for h in history if h[1] > 0]
    if not valid:
        print("Population never survived a full day -- can't evaluate selection.")
        return

    first = valid[0][2]
    last = valid[-1][2]
    print("\n--- trait drift: first surviving day -> last day ---")
    for g in GENES:
        f, l = first[g], last[g]
        lo, hi = None, None
        if f is not None and l is not None:
            span = None
            delta = l - f
            print(f"{g:22s} {f:.4f} -> {l:.4f}   (delta {delta:+.4f})")

    pops = [h[1] for h in valid]
    print(f"\npopulation: min {min(pops)}  max {max(pops)}  final {pops[-1]}")


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--days", type=int, default=200, help="additional days to run this invocation")
    ap.add_argument("--seed", type=int, default=42)
    ap.add_argument("--log-every-days", type=int, default=10)
    ap.add_argument("--checkpoint", type=str, default=None)
    ap.add_argument("--checkpoint-every-days", type=int, default=10)
    ap.add_argument("--resume", action="store_true")
    args = ap.parse_args()

    bio = life = history = None
    extinctions = 0
    if args.resume and args.checkpoint:
        bio, life, history, extinctions = load_checkpoint(args.checkpoint)
        print(f"resumed from day {history[-1][0]} (pop {history[-1][1]})", flush=True)

    bio, life, history, extinctions = run(
        args.days, args.seed, log_every_days=args.log_every_days,
        bio=bio, life=life, history=history, extinctions=extinctions,
        checkpoint_path=args.checkpoint, checkpoint_every_days=args.checkpoint_every_days,
    )
    print(f"\nextinction events (auto-reseeded): {extinctions}")
    summarize(history)
