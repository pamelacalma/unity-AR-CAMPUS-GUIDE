#!/usr/bin/env python3
"""
SO4 Deliverable: Spatial Traffic & Bottleneck Heatmap Generator
Reads nav_heatmap_log.csv (pulled from Application.persistentDataPath via adb pull)
and generates:
  1. campus_navigation_heatmap.png (2D kernel/grid spatial density plot + deviation bottleneck markers)
  2. heatmap_zone_summary.csv (sample counts, dwell seconds, and deviation counts per 2m x 2m grid cell)

Usage:
  python generate_heatmap.py [path/to/nav_heatmap_log.csv]
"""

import csv
import math
import os
import sys
from collections import Counter, defaultdict


def load_samples(csv_path):
    samples = []
    deviations = []
    with open(csv_path, "r", encoding="utf-8", errors="replace") as f:
        reader = csv.DictReader(f)
        for row in reader:
            try:
                x = float(row["x"])
                z = float(row["z"])
                bldg = row.get("building_code", "M").strip() or "M"
                floor = row.get("floor", "1F").strip() or "1F"
                pwd = row.get("pwd_mode", "0").strip() == "1"
                tag = row.get("event_tag", "NAV").strip()
                samples.append((x, z, bldg, floor, pwd, tag))
                if tag == "DEVIATION_REROUTE":
                    deviations.append((x, z, bldg, floor))
            except (KeyError, ValueError):
                continue
    return samples, deviations


def write_grid_summary(samples, out_csv, cell_size=2.0):
    cell_counts = Counter()
    cell_devs = Counter()
    for x, z, bldg, floor, pwd, tag in samples:
        gx = round(math.floor(x / cell_size) * cell_size, 1)
        gz = round(math.floor(z / cell_size) * cell_size, 1)
        key = (bldg, floor, gx, gz)
        cell_counts[key] += 1
        if tag == "DEVIATION_REROUTE":
            cell_devs[key] += 1

    with open(out_csv, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["building_code", "floor", "grid_x_m", "grid_z_m", "dwell_samples", "deviation_count"])
        for (bldg, floor, gx, gz), count in cell_counts.most_common():
            w.writerow([bldg, floor, gx, gz, count, cell_devs[(bldg, floor, gx, gz)]])


def try_plot_heatmap(samples, deviations, out_png):
    try:
        import matplotlib.pyplot as plt
        import numpy as np
    except ImportError:
        print("[generate_heatmap] matplotlib/numpy not installed; skipping PNG plot (CSV summary generated).")
        return

    xs = np.array([s[0] for s in samples])
    zs = np.array([s[1] for s in samples])

    fig, ax = plt.subplots(figsize=(10, 6), dpi=180)
    h = ax.hist2d(xs, zs, bins=[36, 24], cmap="YlOrRd", cmin=1)
    plt.colorbar(h[3], ax=ax, label="Navigation Dwell Density (samples)")

    if deviations:
        dx = [d[0] for d in deviations]
        dz = [d[1] for d in deviations]
        ax.scatter(dx, dz, c="cyan", edgecolors="black", s=65, marker="^", label="Reroute / Deviation Point")
        ax.legend(loc="upper right")

    ax.set_title("DLSU AR Campus Guide — Indoor Navigation Spatial Traffic Heatmap (SO4)")
    ax.set_xlabel("CAD World X (meters)")
    ax.set_ylabel("CAD World Z (meters)")
    ax.grid(True, linestyle="--", alpha=0.3)
    plt.tight_layout()
    plt.savefig(out_png)
    plt.close(fig)
    print(f"[generate_heatmap] Saved heatmap figure to: {out_png}")


def main():
    csv_path = sys.argv[1] if len(sys.argv) > 1 else "nav_heatmap_log.csv"
    if not os.path.exists(csv_path):
        print(f"File not found: {csv_path}")
        sys.exit(1)

    samples, deviations = load_samples(csv_path)
    if not samples:
        print("No valid trajectory samples found in CSV.")
        sys.exit(1)

    out_dir = os.path.dirname(os.path.abspath(csv_path))
    out_csv = os.path.join(out_dir, "heatmap_zone_summary.csv")
    out_png = os.path.join(out_dir, "campus_navigation_heatmap.png")

    write_grid_summary(samples, out_csv)
    print(f"[generate_heatmap] Loaded {len(samples)} samples ({len(deviations)} deviations). Summary -> {out_csv}")
    try_plot_heatmap(samples, deviations, out_png)


if __name__ == "__main__":
    main()
