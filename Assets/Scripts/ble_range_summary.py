#!/usr/bin/env python3
"""Summarize ble_range_test.csv (fixed version).

Changes vs. the original:
  * "Usable range" is CONTIGUOUS: the farthest distance D such that every measured
    distance up to D passes. The original took the farthest passing distance even if a
    nearer one failed (a lucky multipath hit at 7 m reported "up to 7 m" after 5 m failed).
  * Results are grouped by device and phone height, so different phones are never averaged.
  * Also prints the snap-zone edge: the first distance where mean RSSI drops below the
    threshold, and the first distance where (mean + 1 std) does. Between those two is the
    zone where snapping is unreliable (people at the same spot snap sometimes, not always).

Usage: python ble_range_summary.py ble_range_test.csv --threshold -65
"""
import argparse
import pandas as pd

ap = argparse.ArgumentParser()
ap.add_argument("csv")
ap.add_argument("--threshold", type=float, default=-65.0, help="snap RSSI threshold in dBm")
ap.add_argument("--detect", type=float, default=90.0, help="min %% of seconds with a packet")
a = ap.parse_args()

df = pd.read_csv(a.csv)
for col, default in (("device", ""), ("phone_height_m", float("nan"))):
    if col not in df.columns:
        df[col] = default

keys = ["device", "phone_height_m", "beacon_name", "condition", "distance_m"]
g = (df.groupby(keys, dropna=False)
       .agg(trials=("test_id", "nunique"), rssi_mean=("rssi_mean", "mean"),
            rssi_std=("rssi_std", "mean"), detect_pct=("detect_secs_pct", "mean"),
            rx_rate_hz=("adv_rate_hz", "mean")).round(1).reset_index())
print(g.to_string(index=False))

print(f"\nContiguous usable range (mean RSSI >= {a.threshold:g} dBm AND detected >= {a.detect:g}% of seconds):")
for k, grp in g.groupby(["device", "phone_height_m", "beacon_name", "condition"], dropna=False):
    grp = grp.sort_values("distance_m")
    mean_edge, upper_edge, best = None, None, None
    for _, r in grp.iterrows():
        if r.rssi_mean >= a.threshold and r.detect_pct >= a.detect:
            best = r.distance_m
        else:
            break
    for _, r in grp.iterrows():
        if mean_edge is None and not (r.rssi_mean >= a.threshold):
            mean_edge = r.distance_m
        if upper_edge is None and not (r.rssi_mean + r.rssi_std >= a.threshold):
            upper_edge = r.distance_m
    dev, h, b, c = k
    print(f"  {b} [{c}] {dev} h={h}: up to {best if best is not None else 'none'} m"
          f" | mean drops below threshold at {mean_edge} m | mean+1std drops below at {upper_edge} m")
