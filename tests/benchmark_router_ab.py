#!/usr/bin/env python3
"""
ScreenSub AI — Real A/B Quality & Performance Benchmark (Baseline M2M-100 vs Multi-Model Router)
Evaluates BOTH benchmark_blind_eval (106 sentences) and benchmark_holdout_36 (36 sentences) = 142 unseen sentences
using the actual translation_worker.py pipeline on local CTranslate2 INT8 models.
"""

import os
import sys
import time
import json
import ctypes
import ctypes.wintypes
import statistics
from collections import defaultdict

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "workers", "translation"))
sys.path.insert(0, os.path.join(ROOT, "tests"))

import translation_worker as tw
from benchmark_blind_eval import BLIND_DATASET
from benchmark_holdout_36 import HOLDOUT_DATASET


class PROCESS_MEMORY_COUNTERS(ctypes.Structure):
    _fields_ = [
        ("cb", ctypes.wintypes.DWORD),
        ("PageFaultCount", ctypes.wintypes.DWORD),
        ("PeakWorkingSetSize", ctypes.c_size_t),
        ("WorkingSetSize", ctypes.c_size_t),
        ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
        ("QuotaPagedPoolUsage", ctypes.c_size_t),
        ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
        ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
        ("PagefileUsage", ctypes.c_size_t),
        ("PeakPagefileUsage", ctypes.c_size_t),
    ]


def get_rss_mb():
    pmc = PROCESS_MEMORY_COUNTERS()
    pmc.cb = ctypes.sizeof(PROCESS_MEMORY_COUNTERS)
    hproc = ctypes.windll.kernel32.GetCurrentProcess()
    if ctypes.windll.psapi.GetProcessMemoryInfo(hproc, ctypes.byref(pmc), pmc.cb):
        return round(pmc.WorkingSetSize / (1024 * 1024), 1), round(pmc.PeakWorkingSetSize / (1024 * 1024), 1)
    return 0.0, 0.0


def percentile(vals, pct):
    if not vals:
        return 0.0
    s = sorted(vals)
    idx = min(len(s) - 1, int(round((pct / 100.0) * (len(s) - 1))))
    return round(s[idx], 1)


def summarize_group(rows):
    warm_lats = [r["latency_ms"] for r in rows if r["model_load_ms"] == 0]
    all_lats = [r["latency_ms"] for r in rows]
    infers = [r["infer_ms"] for r in rows]
    used_vals = warm_lats if warm_lats else all_lats
    return {
        "count": len(rows),
        "warm_count": len(warm_lats),
        "models_used": sorted(list({r["model_used"] for r in rows})),
        "p50_ms": percentile(used_vals, 50),
        "p95_ms": percentile(used_vals, 95),
        "mean_ms": round(statistics.mean(used_vals), 1) if used_vals else 0.0,
        "infer_p50_ms": percentile(infers, 50),
        "infer_p95_ms": percentile(infers, 95),
    }


def run_mode(mode_name, dataset, routing_enabled, experimental_zh_vi=True, max_loaded_models=1):
    tw.unload_neural_model()
    tw.router_config["enabled"] = routing_enabled
    tw.router_config["experimental_zh_vi"] = experimental_zh_vi
    tw.router_config["max_loaded_models"] = max_loaded_models

    # Group by direction first when measuring warm inference quality/latency
    by_dir = defaultdict(list)
    for idx, (text, src, tgt, cat) in enumerate(dataset):
        by_dir[f"{src}->{tgt}"].append((idx, text, src, tgt, cat))

    rows = [None] * len(dataset)
    cold_loads = {}

    rss_start, _ = get_rss_mb()
    for pair_key, items in by_dir.items():
        # Run 1 warmup on the first item of each direction (or track load_ms carefully)
        for j, (idx, text, src, tgt, cat) in enumerate(items):
            res = tw.translate_text(
                text,
                src,
                tgt,
                routing_enabled=routing_enabled,
                experimental_zh_vi=experimental_zh_vi,
                max_loaded_models=max_loaded_models,
            )
            if res["model_load_ms"] > 0:
                cold_loads.setdefault(res["model_used"], []).append(res["model_load_ms"])
            rows[idx] = {
                "idx": idx,
                "pair": pair_key,
                "cat": cat,
                "src_text": text,
                "raw": res["raw_translated_text"],
                "final": res["translated_text"],
                "model_used": res["model_used"],
                "decoding_config": res["decoding_config"],
                "model_load_ms": res["model_load_ms"],
                "latency_ms": float(res["latency_ms"]),
                "tokenize_ms": float(res["tokenize_ms"]),
                "infer_ms": float(res["infer_ms"]),
                "postproc_ms": float(res["postproc_ms"]),
            }

    rss_end, rss_peak = get_rss_mb()
    per_dir_stats = {}
    for pair_key in sorted(by_dir.keys()):
        pair_rows = [r for r in rows if r["pair"] == pair_key]
        per_dir_stats[pair_key] = summarize_group(pair_rows)

    return {
        "mode": mode_name,
        "routing_enabled": routing_enabled,
        "max_loaded_models": max_loaded_models,
        "cold_loads_ms": cold_loads,
        "rss_start_mb": rss_start,
        "rss_end_mb": rss_end,
        "rss_peak_mb": rss_peak,
        "overall": summarize_group(rows),
        "per_direction": per_dir_stats,
        "rows": rows,
    }


def measure_language_switching(max_loaded_models):
    tw.unload_neural_model()
    tw.router_config["enabled"] = True
    tw.router_config["experimental_zh_vi"] = True
    tw.router_config["max_loaded_models"] = max_loaded_models

    # Continuous alternating transitions: EN->VI, ZH->VI, VI->ZH, EN->VI, ZH->VI, EN->ZH
    sequence = [
        ("Let's grab a coffee and catch up.", "en", "vi"),
        ("太誇張了吧，排隊排了兩個小時還沒輪到我們。", "zh", "vi"),
        ("Hôm nay bạn có rảnh không?", "vi", "zh"),
        ("Wait up! You dropped your keys.", "en", "vi"),
        ("先別急著下結論，聽他把話說完。", "zh", "vi"),
        ("What are you doing tonight?", "en", "zh"),
        ("That rings a bell, doesn't it?", "en", "vi"),
    ]
    events = []
    rss_before, _ = get_rss_mb()
    t0 = time.perf_counter()
    for step, (txt, src, tgt) in enumerate(sequence):
        res = tw.translate_text(txt, src, tgt, routing_enabled=True, max_loaded_models=max_loaded_models)
        rss_now, rss_peak = get_rss_mb()
        events.append({
            "step": step,
            "pair": f"{src}->{tgt}",
            "model_used": res["model_used"],
            "model_load_ms": res["model_load_ms"],
            "total_ms": res["latency_ms"],
            "infer_ms": res["infer_ms"],
            "loaded_models": res["loaded_models"],
            "rss_mb": rss_now,
        })
    total_wall_ms = round((time.perf_counter() - t0) * 1000.0, 1)
    rss_after, rss_peak = get_rss_mb()
    return {
        "max_loaded_models": max_loaded_models,
        "total_wall_ms": total_wall_ms,
        "rss_before_mb": rss_before,
        "rss_after_mb": rss_after,
        "rss_peak_mb": rss_peak,
        "events": events,
    }


def main():
    combined = [("holdout_" + c, t, s, tg) for (t, s, tg, c) in HOLDOUT_DATASET] + [
        ("blind_" + c, t, s, tg) for (t, s, tg, c) in BLIND_DATASET
    ]
    dataset = [(t, s, tg, cat) for (cat, t, s, tg) in combined]
    print(f"Total unseen evaluation sentences: {len(dataset)} (36 holdout + 106 blind)")

    baseline = run_mode("A_M2M100_Baseline", dataset, routing_enabled=False, max_loaded_models=1)
    router = run_mode("B_MultiModel_Router", dataset, routing_enabled=True, experimental_zh_vi=True, max_loaded_models=1)

    switch_cap1 = measure_language_switching(max_loaded_models=1)
    switch_cap2 = measure_language_switching(max_loaded_models=2)

    out_path = os.path.join(
        r"C:\Users\Tan\.gemini\antigravity\brain\c8c3f2d9-4eaf-469b-bdd7-67467536b1d1\scratch",
        "router_ab_results.json",
    )
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    report = {
        "dataset_count": len(dataset),
        "baseline": baseline,
        "router": router,
        "switching_cap1": switch_cap1,
        "switching_cap2": switch_cap2,
    }
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(report, f, ensure_ascii=False, indent=2)

    print("=== BENCHMARK SUMMARY ===")
    for label, section in [("Baseline (M2M-100)", baseline), ("Multi-Model Router", router)]:
        print(f"\n[{label}] overall={section['overall']} rss_end={section['rss_end_mb']}MB rss_peak={section['rss_peak_mb']}MB cold={section['cold_loads_ms']}")
        for d, st in section["per_direction"].items():
            print(f"  {d:8s}: n={st['count']:3d}, P50={st['p50_ms']:6.1f}ms, P95={st['p95_ms']:6.1f}ms, models={st['models_used']}")

    print(f"\nSwitch Cap=1: wall={switch_cap1['total_wall_ms']}ms, rss_after={switch_cap1['rss_after_mb']}MB")
    print(f"Switch Cap=2: wall={switch_cap2['total_wall_ms']}ms, rss_after={switch_cap2['rss_after_mb']}MB")
    print(f"Saved full JSON to: {out_path}")


if __name__ == "__main__":
    main()
