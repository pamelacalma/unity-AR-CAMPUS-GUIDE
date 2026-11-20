#!/usr/bin/env python3
"""
SO5 & Dr. Abad Panel Requirement: Statistical Evaluator
Reads so5_gps_vs_iot_benchmark.csv and usability_trials.csv and computes:
  1. Mean Squared Error (MSE), RMSE, Mean Absolute Error (MAE), Standard Deviation,
     and 95% Confidence Intervals for IoT (BLE + Wi-Fi Wk-NN) vs. Smartphone GPS.
  2. Paired Two-Sample t-test (IoT Error vs. GPS Error) and One-Sample t-test against
     the proposal's <= 3.0 m localization error threshold and <= 2000 ms latency threshold.
  3. Ordinary Least Squares (OLS) Linear Regression (slope, intercept, R^2, correlation r)
     of IoT localization error vs. distance to nearest ESP32 beacon.

Usage:
  python evaluate_so5_stats.py [path/to/so5_gps_vs_iot_benchmark.csv] [path/to/usability_trials.csv]
"""

import csv
import math
import os
import sys


def mean(vals):
    return sum(vals) / len(vals) if vals else 0.0


def variance(vals):
    if len(vals) < 2:
        return 0.0
    m = mean(vals)
    return sum((v - m) ** 2 for v in vals) / (len(vals) - 1)


def std_dev(vals):
    return math.sqrt(variance(vals))


def ci95_halfwidth(vals):
    if len(vals) < 2:
        return 0.0
    # Normal/t approximation (t_0.975 ~= 1.96 for n>=30, ~2.04 for n~30)
    t_crit = 2.042 if len(vals) < 35 else 1.960
    return t_crit * std_dev(vals) / math.sqrt(len(vals))


def one_sample_t(vals, mu0):
    if len(vals) < 2:
        return 0.0
    sd = std_dev(vals)
    if sd < 1e-9:
        return 0.0
    return (mean(vals) - mu0) / (sd / math.sqrt(len(vals)))


def paired_t(xs, ys):
    diffs = [x - y for x, y in zip(xs, ys)]
    return one_sample_t(diffs, 0.0), diffs


def ols_regression(xs, ys):
    if len(xs) < 2:
        return 0.0, 0.0, 0.0
    mx, my = mean(xs), mean(ys)
    sxx = sum((x - mx) ** 2 for x in xs)
    syy = sum((y - my) ** 2 for y in ys)
    sxy = sum((x - mx) * (y - my) for x, y in zip(xs, ys))
    if sxx < 1e-9:
        return 0.0, my, 0.0
    slope = sxy / sxx
    intercept = my - slope * mx
    r2 = (sxy ** 2) / (sxx * syy) if syy > 1e-9 else 0.0
    return slope, intercept, r2


def analyze_benchmark(csv_path):
    iot_errs, gps_errs, latencies, beacon_dists = [], [], [], []
    with open(csv_path, "r", encoding="utf-8", errors="replace") as f:
        for row in csv.DictReader(f):
            try:
                iot_errs.append(float(row["iot_error_m"]))
                gps_errs.append(float(row["gps_error_m"]))
                latencies.append(float(row["iot_latency_ms"]))
                beacon_dists.append(float(row["nearest_beacon_dist_m"]))
            except (KeyError, ValueError):
                continue

    n = len(iot_errs)
    if n == 0:
        print("No valid rows in benchmark CSV.")
        return

    iot_mse = mean([e * e for e in iot_errs])
    gps_mse = mean([e * e for e in gps_errs])
    t_paired, diffs = paired_t(iot_errs, gps_errs)
    t_iot_3m = one_sample_t(iot_errs, 3.0)
    t_lat_2s = one_sample_t(latencies, 2000.0)
    slope, intercept, r2 = ols_regression(beacon_dists, iot_errs)

    print("=" * 72)
    print(f"SO5 LOCALIZATION BENCHMARK REPORT (N = {n} trials)")
    print("=" * 72)
    print(f"IoT (BLE + Wi-Fi Wk-NN) Error:")
    print(f"  Mean (MAE) : {mean(iot_errs):.3f} m +/- {ci95_halfwidth(iot_errs):.3f} m (95% CI)")
    print(f"  SD         : {std_dev(iot_errs):.3f} m")
    print(f"  MSE / RMSE : {iot_mse:.3f} m^2 / {math.sqrt(iot_mse):.3f} m")
    print(f"  1-Sample t vs 3.0 m target: t = {t_iot_3m:.3f} (df = {n - 1})")
    print("-" * 72)
    print(f"Smartphone GPS Error (Dr. Abad Comparison):")
    print(f"  Mean (MAE) : {mean(gps_errs):.3f} m +/- {ci95_halfwidth(gps_errs):.3f} m (95% CI)")
    print(f"  SD         : {std_dev(gps_errs):.3f} m")
    print(f"  MSE / RMSE : {gps_mse:.3f} m^2 / {math.sqrt(gps_mse):.3f} m")
    print(f"  Paired t-test (IoT vs GPS): t = {t_paired:.3f}, mean diff = {mean(diffs):.3f} m")
    print("-" * 72)
    print(f"Compute / Alignment Latency:")
    print(f"  Mean       : {mean(latencies):.2f} ms +/- {ci95_halfwidth(latencies):.2f} ms (95% CI)")
    print(f"  1-Sample t vs 2000 ms target: t = {t_lat_2s:.3f}")
    print("-" * 72)
    print(f"OLS Linear Regression (Error vs. Distance to Nearest Beacon):")
    print(f"  Model      : error_m = {slope:.4f} * dist_m + {intercept:.4f}")
    print(f"  R^2        : {r2:.4f}")
    print("=" * 72)


def analyze_usability(csv_path):
    if not os.path.exists(csv_path):
        return
    durations, devs, arrived = [], [], 0
    total = 0
    with open(csv_path, "r", encoding="utf-8", errors="replace") as f:
        for row in csv.DictReader(f):
            try:
                total += 1
                durations.append(float(row["duration_s"]))
                devs.append(int(row["deviation_count"]))
                if row.get("outcome", "").strip().lower() == "arrived":
                    arrived += 1
            except (KeyError, ValueError):
                continue
    if total == 0:
        return
    print(f"\nUSABILITY TRIAL SUMMARY (N = {total} sessions):")
    print(f"  Completion / Arrival Rate : {100.0 * arrived / total:.1f}% ({arrived}/{total})")
    print(f"  Mean Time-on-Task         : {mean(durations):.2f} s +/- {ci95_halfwidth(durations):.2f} s")
    print(f"  Mean Deviations / Reroutes: {mean(devs):.2f}")


if __name__ == "__main__":
    bench_csv = sys.argv[1] if len(sys.argv) > 1 else "so5_gps_vs_iot_benchmark.csv"
    usab_csv = sys.argv[2] if len(sys.argv) > 2 else "usability_trials.csv"
    if os.path.exists(bench_csv):
        analyze_benchmark(bench_csv)
    else:
        print(f"Benchmark file not found: {bench_csv}")
    analyze_usability(usab_csv)
