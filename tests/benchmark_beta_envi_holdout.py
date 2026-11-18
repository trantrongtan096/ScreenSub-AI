#!/usr/bin/env python3
"""
Phase 3 Unseen Holdout Regression Benchmark for ScreenSub AI Beta EN-VI Router.
- 108 brand-new EN -> VI sentences across 9 challenging categories (12 each),
  verified programmatically to be 100% disjoint from all 4 prior benchmark scripts.
- 12 ZH -> VI sanity sentences (confirming M2M-100 active when EnableExperimentalZhViOpusModel=False).
- 6 VI -> EN sanity sentences (confirming EnViT5 active on VI -> EN).
Runs BOTH Baseline (routing_enabled=False -> M2M-100) and Beta EN-VI Router
(routing_enabled=True, experimental_zh_vi=False, allow_fallback=True, max_loaded_models=2)
over the real JSON-lines IPC worker process (`workers/translation/translation_worker.py`).
"""

import json
import os
import pathlib
import random
import re
import statistics
import subprocess
import sys
import time

REPO_ROOT = pathlib.Path(__file__).resolve().parent.parent
WORKER_PATH = REPO_ROOT / "workers" / "translation" / "translation_worker.py"
PRIOR_BENCHMARKS = [
    REPO_ROOT / "tests" / "benchmark_120_sentences.py",
    REPO_ROOT / "tests" / "benchmark_30_sentences_6_directions.py",
    REPO_ROOT / "tests" / "benchmark_holdout_36.py",
    REPO_ROOT / "tests" / "benchmark_blind_eval.py",
]

# 108 unseen EN -> VI items (9 categories x 12 items)
EN_VI_HOLDOUT = [
    # 1. Negation & double negation (12)
    ("neg_01", "negation", "I wouldn't say she isn't qualified, but she lacks fieldwork experience."),
    ("neg_02", "negation", "Don't tell me you forgot to lock the server room again."),
    ("neg_03", "negation", "Hardly anyone showed up before the rain let up."),
    ("neg_04", "negation", "It's not that I don't trust him; I just can't afford another slip-up."),
    ("neg_05", "negation", "Never have I seen a patch break this many builds without warning."),
    ("neg_06", "negation", "You can't not reply when the client copies your director."),
    ("neg_07", "negation", "Neither the backup node nor the mirror responded before midnight."),
    ("neg_08", "negation", "I don't think nobody noticed—Marcus stayed completely quiet."),
    ("neg_09", "negation", "Seldom do they ship on Friday unless an audit is looming."),
    ("neg_10", "negation", "Please don't leave the prototype unplugged overnight."),
    ("neg_11", "negation", "No sooner had we rolled back than alerts fired across three regions."),
    ("neg_12", "negation", "She didn't reject the offer outright, nor did she agree to those terms."),

    # 2. Multi-clause dialogue (12)
    ("mc_01", "multi_clause", "As soon as Mia lands in Da Nang, grab the badges so we don't miss check-in."),
    ("mc_02", "multi_clause", "Even though the projector flickered twice, the keynote went smoothly until Q&A."),
    ("mc_03", "multi_clause", "If you finish reviewing chapter four tonight, let me know before I export the PDF."),
    ("mc_04", "multi_clause", "While everyone argued over pricing tiers, nobody checked whether the billing hook worked."),
    ("mc_05", "multi_clause", "Since traffic spiked right after noon, we throttled guest uploads instead of shutting down search."),
    ("mc_06", "multi_clause", "Tell Kevin that once the courier arrives, he should sign for both crates and photograph the seal."),
    ("mc_07", "multi_clause", "Although I promised to stay neutral, seeing them blame the intern made my blood boil."),
    ("mc_08", "multi_clause", "Whenever the AC kicks on in this studio, the condenser mic picks up a faint hum."),
    ("mc_09", "multi_clause", "Before you merge that branch, run the migration locally so staging doesn't wipe test data."),
    ("mc_10", "multi_clause", "Unless the ferry resumes before dusk, we'll have to crash at a motel near the pier."),
    ("mc_11", "multi_clause", "Because the subtitles drifted by two frames every minute, viewers thought the dub was broken."),
    ("mc_12", "multi_clause", "Grab the spare tripod from the trunk, or else we'll be shooting handheld all afternoon."),

    # 3. Consecutive questions (12)
    ("cq_01", "consecutive_questions", "Wait, are we seriously doing this now? Didn't we postpone it till Monday?"),
    ("cq_02", "consecutive_questions", "Who unplugged the router? Was it maintenance or did the breaker trip?"),
    ("cq_03", "consecutive_questions", "Where did you put the receipt? You didn't toss it with the wrappers, did you?"),
    ("cq_04", "consecutive_questions", "How long have you been standing outside? Why didn't you buzz my apartment?"),
    ("cq_05", "consecutive_questions", "Is that thunder I hear? Should we pack up the easel before it pours?"),
    ("cq_06", "consecutive_questions", "Can you hear me okay on your end, or is my headset clipping again?"),
    ("cq_07", "consecutive_questions", "Wait—whose jacket is draped over my chair? Did Hannah stop by earlier?"),
    ("cq_08", "consecutive_questions", "Why is the lobby locked at three? Did they reschedule the rehearsal?"),
    ("cq_09", "consecutive_questions", "Are you ordering iced coffee? Mind grabbing an extra croissant for me?"),
    ("cq_10", "consecutive_questions", "Did the train already depart? How many minutes till the next express?"),
    ("cq_11", "consecutive_questions", "Which cable goes to the capture card? Is it the braided USB-C one?"),
    ("cq_12", "consecutive_questions", "You actually read the whole contract? And you still signed on line four?"),

    # 4. Idioms, slang & phrasal verbs (12)
    ("id_01", "idioms_slang_phrasal", "Let's call it a day and pick this up first thing tomorrow morning."),
    ("id_02", "idioms_slang_phrasal", "He spilled the beans right before the surprise party kicked off."),
    ("id_03", "idioms_slang_phrasal", "Stop beating around the bush and tell me what broke in production."),
    ("id_04", "idioms_slang_phrasal", "I'm totally wiped out after pulling back-to-back shifts all week."),
    ("id_05", "idioms_slang_phrasal", "That sketchy workaround blew up in our faces within ten minutes."),
    ("id_06", "idioms_slang_phrasal", "Don't sweat it—we'll figure it out as we go along."),
    ("id_07", "idioms_slang_phrasal", "She pulled off an insane comeback when everyone had written her off."),
    ("id_08", "idioms_slang_phrasal", "Heads up, the landlord might drop by later to check the plumbing."),
    ("id_09", "idioms_slang_phrasal", "Keep your chin up; one bombed demo doesn't sink the whole quarter."),
    ("id_10", "idioms_slang_phrasal", "They bent over backwards to wrap up the footage before midnight."),
    ("id_11", "idioms_slang_phrasal", "Cut me some slack, I literally only slept three hours last night."),
    ("id_12", "idioms_slang_phrasal", "Hold your horses—let's double-check the checksums before hitting deploy."),

    # 5. Dropped subjects (12)
    ("ds_01", "dropped_subject", "Sounds fishy to me, better verify the invoice first."),
    ("ds_02", "dropped_subject", "Running five minutes behind, grab a booth by the window."),
    ("ds_03", "dropped_subject", "Looks like the battery swelled up after leaving it in the car."),
    ("ds_04", "dropped_subject", "Got stuck at customs for two hours, barely made the connection."),
    ("ds_05", "dropped_subject", "Forgot my charger upstairs; mind lending me yours till lunch?"),
    ("ds_06", "dropped_subject", "Makes sense when you put it that way, honestly."),
    ("ds_07", "dropped_subject", "Tried rebooting twice, still stuck on the spinning wheel."),
    ("ds_08", "dropped_subject", "Heading downstairs now—want anything from the bakery?"),
    ("ds_09", "dropped_subject", "Feels warmer inside than it looked from the street."),
    ("ds_10", "dropped_subject", "Could've sworn I muted notifications before walking into court."),
    ("ds_11", "dropped_subject", "Waiting on legal to sign off; might take till Thursday."),
    ("ds_12", "dropped_subject", "Never rains like this in November unless a typhoon brushes the coast."),

    # 6. Proper nouns & places (12)
    ("pn_01", "proper_nouns_places", "Meet Dr. Nguyen outside Ben Thanh Market at noon sharp."),
    ("pn_02", "proper_nouns_places", "Lucas flew from Zurich to Haneda using an expired boarding pass on his phone."),
    ("pn_03", "proper_nouns_places", "Microsoft Research published the whitepaper right after Kyoto Summit."),
    ("pn_04", "proper_nouns_places", "Ask Claire or Minh to forward the Figma link from Notion."),
    ("pn_05", "proper_nouns_places", "Traffic along Nguyen Hue Boulevard crawls whenever Tet fireworks start."),
    ("pn_06", "proper_nouns_places", "Elena's bakery near Golden Gate Park sells out of sourdough by nine."),
    ("pn_07", "proper_nouns_places", "Check whether OBS Studio recognizes the Elgato HD60 X on Windows 11."),
    ("pn_08", "proper_nouns_places", "Take the Yamanote Line from Shinjuku to Shibuya and exit Gate B."),
    ("pn_09", "proper_nouns_places", "Rachel left her Canon lens inside Starbucks on Le Loi Street."),
    ("pn_10", "proper_nouns_places", "Even Nvidia engineers struggled with DirectX 12 frame pacing back then."),
    ("pn_11", "proper_nouns_places", "Send the package via DHL Express to Hanoi instead of Hai Phong."),
    ("pn_12", "proper_nouns_places", "Remember to tag Victor and Linh on GitHub PR #482 before noon."),

    # 7. Numbers, time, units & keyboard shortcuts (12)
    ("nt_01", "numbers_time_units_shortcuts", "Press Ctrl+Shift+S to snap the current subtitle region immediately."),
    ("nt_02", "numbers_time_units_shortcuts", "The patch dropped idle RAM from 1,450 MB down to 680 MB in 4.2 seconds."),
    ("nt_03", "numbers_time_units_shortcuts", "Gate 14B closes at 7:45 AM sharp, so be through security by 7:15."),
    ("nt_04", "numbers_time_units_shortcuts", "Use Alt+F4 only if the modal freezes for over 30 seconds."),
    ("nt_05", "numbers_time_units_shortcuts", "Torque each bolt to 42 Nm and bake the coating at 180°C for 15 minutes."),
    ("nt_06", "numbers_time_units_shortcuts", "Export at 60 fps in 1080p, keeping audio bitrate above 192 kbps."),
    ("nt_07", "numbers_time_units_shortcuts", "My flight's delayed 2 hours and 35 minutes due to 90 km/h crosswinds."),
    ("nt_08", "numbers_time_units_shortcuts", "Hold Ctrl+Alt+T to toggle the floating overlay between top and bottom."),
    ("nt_09", "numbers_time_units_shortcuts", "Only 3.5% of packets timed out across 12,000 concurrent requests."),
    ("nt_10", "numbers_time_units_shortcuts", "Room 309 costs $125 per night, plus a 10% service charge."),
    ("nt_11", "numbers_time_units_shortcuts", "Tap F11 to enter fullscreen, then hit Esc if tooltips block the bottom bar."),
    ("nt_12", "numbers_time_units_shortcuts", "Keep latency under 250 ms at P95 on an 8 GB DDR4 laptop."),

    # 8. Long & line-broken OCR subtitles (12)
    ("lm_01", "long_and_multiline", "Look, I know you spent half the semester tuning that shader,\nbut the frame drops on integrated GPUs make it unplayable."),
    ("lm_02", "long_and_multiline", "Wait until the crowd clears out of the west tunnel,\nthen signal the crew to roll the camera dolly onto platform three."),
    ("lm_03", "long_and_multiline", "She handed me an unlabeled flash drive at the station\nand whispered that nobody on the board could be trusted anymore."),
    ("lm_04", "long_and_multiline", "If you zoom in past 200%, you can still spot faint compression halos\nalong every diagonal beam behind the anchor's shoulder."),
    ("lm_05", "long_and_multiline", "By the time the paramedics reached the ridge trail,\nthree hikers had already pitched emergency tarps out of old ponchos."),
    ("lm_06", "long_and_multiline", "I wasn't trying to undermine your presentation in front of investors;\nI only flagged the cash-burn slide so you wouldn't get blindsided."),
    ("lm_07", "long_and_multiline", "First we verify the SHA-256 digest of every weight file on disk,\nand only then do we mark the neural engine ready for inference."),
    ("lm_08", "long_and_multiline", "Even after swapping two thermal pads and cleaning the heatsink fins,\nthe laptop still throttles whenever ambient room temp passes 32°C."),
    ("lm_09", "long_and_multiline", "Do not turn off your computer while firmware flashes sector zero,\nbecause a sudden power loss can brick the motherboard permanently."),
    ("lm_10", "long_and_multiline", "Whenever the bass guitar drops out right before the chorus,\nyou can clearly hear the tape hiss from that 1978 studio session."),
    ("lm_11", "long_and_multiline", "Rather than rebuilding the entire desktop window from scratch,\nwe kept the layout intact and swapped only the background queue worker."),
    ("lm_12", "long_and_multiline", "They promised overnight shipping across three islands,\nyet my parcel sat in customs clearance until late Tuesday evening."),

    # 9. Low-context fragments (12)
    ("lc_01", "low_context_fragments", "Not on my watch."),
    ("lc_02", "low_context_fragments", "Easy for you to say."),
    ("lc_03", "low_context_fragments", "Wait—seriously?"),
    ("lc_04", "low_context_fragments", "Works like a charm."),
    ("lc_05", "low_context_fragments", "No clue whatsoever."),
    ("lc_06", "low_context_fragments", "About bloody time."),
    ("lc_07", "low_context_fragments", "Long story short..."),
    ("lc_08", "low_context_fragments", "Fair enough, go ahead."),
    ("lc_09", "low_context_fragments", "Wrong floor, buddy."),
    ("lc_10", "low_context_fragments", "Right back at you."),
    ("lc_11", "low_context_fragments", "Speak of the devil."),
    ("lc_12", "low_context_fragments", "Now or never."),
]

ZH_VI_SANITY = [
    ("zh_01", "zh_vi_sanity", "先别急着下结论，等检测报告出来再说。"),
    ("zh_02", "zh_vi_sanity", "楼下保安说今晚十点以后就锁正门了。"),
    ("zh_03", "zh_vi_sanity", "你把快捷键改回默认吧，不然我老按错。"),
    ("zh_04", "zh_vi_sanity", "外面风这么大，把阳台上的衣服收进来。"),
    ("zh_05", "zh_vi_sanity", "刚才不是说好在地铁B口汇合吗？"),
    ("zh_06", "zh_vi_sanity", "这批货的质检还没过，暂时不能发往港口。"),
    ("zh_07", "zh_vi_sanity", "别光顾着看手机，锅里的汤快溢出来了。"),
    ("zh_08", "zh_vi_sanity", "虽然延迟只有五十毫秒，但连续对话还是能听出来。"),
    ("zh_09", "zh_vi_sanity", "我刚才重启了一下路由器，现在网速稳多了。"),
    ("zh_10", "zh_vi_sanity", "如果明天早上还下雨，户外拍摄就改到棚里。"),
    ("zh_11", "zh_vi_sanity", "这件事千万别在群里乱传，等官方通知。"),
    ("zh_12", "zh_vi_sanity", "这台旧笔记本内存只有8G，同时开太多模型会卡。"),
]

VI_EN_SANITY = [
    ("vi_01", "vi_en_sanity", "Khoan đã, đừng rút dây nguồn khi máy đang cập nhật."),
    ("vi_02", "vi_en_sanity", "Nhờ bạn kiểm tra lại giúp mình xem file cấu hình đã lưu chưa."),
    ("vi_03", "vi_en_sanity", "Trời sắp đổ mưa rồi, tụi mình ghé quán kia trú tạm đi."),
    ("vi_04", "vi_en_sanity", "Bấm Ctrl+Shift+S để chọn lại vùng phụ đề mới."),
    ("vi_05", "vi_en_sanity", "Hẹn gặp mọi người lúc 8 giờ tối tại cổng số 3 nhé."),
    ("vi_06", "vi_en_sanity", "Nghe hơi vô lý nhưng cứ thử chạy lại một lần nữa xem sao."),
]


def _normalize_for_overlap(s: str) -> str:
    return re.sub(r"\s+", " ", s.replace("\n", " ").strip().lower())


def verify_disjoint_from_prior_benchmarks():
    prior_norms = set()
    for p in PRIOR_BENCHMARKS:
        txt = p.read_text(encoding="utf-8")
        for m in re.findall(r'"([^"\\]*(?:\\.[^"\\]*)*)"|\'([^\'\\]*(?:\\.[^\'\\]*)*)\'', txt):
            candidate = m[0] or m[1]
            if len(candidate) >= 6:
                prior_norms.add(_normalize_for_overlap(candidate))

    overlaps = []
    for item_id, cat, src in EN_VI_HOLDOUT + ZH_VI_SANITY + VI_EN_SANITY:
        norm = _normalize_for_overlap(src)
        if norm in prior_norms:
            overlaps.append((item_id, src))
    assert not overlaps, f"Holdout overlaps with prior benchmarks: {overlaps}"


def ipc_call(proc, req_obj: dict) -> dict:
    proc.stdin.write(json.dumps(req_obj, ensure_ascii=False) + "\n")
    proc.stdin.flush()
    while True:
        line = proc.stdout.readline()
        if not line:
            stderr_rem = proc.stderr.read()
            raise RuntimeError(f"Worker EOF! stderr={stderr_rem}")
        line = line.strip()
        if not line:
            continue
        return json.loads(line)


def run_benchmark(out_json_path: pathlib.Path, blind_json_path: pathlib.Path):
    verify_disjoint_from_prior_benchmarks()
    print(f"[DISJOINT_CHECK] Verified 0 overlaps across {len(EN_VI_HOLDOUT)} EN->VI + {len(ZH_VI_SANITY)} ZH->VI + {len(VI_EN_SANITY)} VI->EN items.")

    proc = subprocess.Popen(
        [sys.executable, "-u", str(WORKER_PATH), "--threads", "4", "--lazy-load"],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        bufsize=1,
    )

    try:
        req_id = 1
        init_resp = ipc_call(proc, {"id": req_id, "cmd": "ping"})
        assert init_resp.get("status") == "ok", init_resp

        # Helper to simulate real multi-line OCR collapse (same as SubtitleTextProcessor join) or pass raw
        def run_suite(mode_name: str, routing_enabled: bool):
            nonlocal req_id
            items = []
            # Warmup 2 calls (not counted in steady-state latency)
            for warm_src, warm_s, warm_t in [
                ("Hello there.", "en", "vi"),
                ("你好。", "zh", "vi"),
                ("Xin chào.", "vi", "en"),
            ]:
                req_id += 1
                ipc_call(proc, {
                    "id": req_id,
                    "cmd": "translate",
                    "text": warm_src,
                    "source_lang": warm_s,
                    "target_lang": warm_t,
                    "routing_enabled": routing_enabled,
                    "experimental_zh_vi": False,
                    "allow_fallback": True,
                    "max_loaded_models": 2,
                })

            all_tasks = (
                [(i, c, t, "en", "vi") for i, c, t in EN_VI_HOLDOUT]
                + [(i, c, t, "zh", "vi") for i, c, t in ZH_VI_SANITY]
                + [(i, c, t, "vi", "en") for i, c, t in VI_EN_SANITY]
            )

            for item_id, cat, src_text, src_lang, tgt_lang in all_tasks:
                req_id += 1
                t0 = time.perf_counter()
                r = ipc_call(proc, {
                    "id": req_id,
                    "cmd": "translate",
                    "text": src_text,
                    "source_lang": src_lang,
                    "target_lang": tgt_lang,
                    "routing_enabled": routing_enabled,
                    "experimental_zh_vi": False,
                    "allow_fallback": True,
                    "max_loaded_models": 2,
                })
                wall_ms = (time.perf_counter() - t0) * 1000.0
                items.append({
                    "id": item_id,
                    "category": cat,
                    "source_lang": src_lang,
                    "target_lang": tgt_lang,
                    "source_text": src_text,
                    "raw_translated_text": r.get("raw_translated_text", ""),
                    "translated_text": r.get("translated_text", ""),
                    "model_used": r.get("model_used", ""),
                    "model_fingerprint": r.get("model_fingerprint", ""),
                    "fallback_used": r.get("fallback_used", False),
                    "decoding_config": r.get("decoding_config", ""),
                    "model_load_ms": r.get("model_load_ms", 0.0),
                    "tokenize_ms": r.get("tokenize_ms", 0.0),
                    "infer_ms": r.get("infer_ms", 0.0),
                    "postproc_ms": r.get("postproc_ms", 0.0),
                    "worker_latency_ms": r.get("latency_ms", 0.0),
                    "ipc_wall_ms": round(wall_ms, 2),
                })
            return items

        baseline_items = run_suite("baseline_m2m100", routing_enabled=False)
        beta_items = run_suite("beta_envi_router", routing_enabled=True)

        proc.stdin.write(json.dumps({"id": req_id + 1, "cmd": "quit"}) + "\n")
        proc.stdin.flush()
        proc.wait(timeout=5)

        def stats_for(items, src_l, tgt_l):
            lats = sorted(x["ipc_wall_ms"] for x in items if x["source_lang"] == src_l and x["target_lang"] == tgt_l)
            infers = sorted(x["infer_ms"] for x in items if x["source_lang"] == src_l and x["target_lang"] == tgt_l)
            p50 = lats[len(lats) // 2]
            p95 = lats[min(len(lats) - 1, int(round(0.95 * (len(lats) - 1))))]
            ip50 = infers[len(infers) // 2]
            ip95 = infers[min(len(infers) - 1, int(round(0.95 * (len(infers) - 1))))]
            models = sorted(set(x["model_used"] for x in items if x["source_lang"] == src_l and x["target_lang"] == tgt_l))
            return {"count": len(lats), "models": models, "wall_p50_ms": p50, "wall_p95_ms": p95, "infer_p50_ms": ip50, "infer_p95_ms": ip95}

        summary = {
            "baseline_en_vi": stats_for(baseline_items, "en", "vi"),
            "beta_en_vi": stats_for(beta_items, "en", "vi"),
            "baseline_zh_vi": stats_for(baseline_items, "zh", "vi"),
            "beta_zh_vi": stats_for(beta_items, "zh", "vi"),
            "baseline_vi_en": stats_for(baseline_items, "vi", "en"),
            "beta_vi_en": stats_for(beta_items, "vi", "en"),
        }

        out_json_path.parent.mkdir(parents=True, exist_ok=True)
        out_json_path.write_text(json.dumps({
            "summary": summary,
            "baseline": baseline_items,
            "beta": beta_items,
        }, ensure_ascii=False, indent=2), encoding="utf-8")

        # Produce deterministic blinded A/B file so evaluation isn't biased by model labels
        rng = random.Random(20261113)
        blind_pairs = []
        for b_item, r_item in zip(baseline_items, beta_items):
            swap = rng.choice([True, False])
            blind_pairs.append({
                "id": b_item["id"],
                "category": b_item["category"],
                "dir": f"{b_item['source_lang']}->{b_item['target_lang']}",
                "source": b_item["source_text"],
                "candidate_1": r_item["translated_text"] if swap else b_item["translated_text"],
                "candidate_2": b_item["translated_text"] if swap else r_item["translated_text"],
                "swap_1_is_beta": swap,
            })
        blind_json_path.write_text(json.dumps(blind_pairs, ensure_ascii=False, indent=2), encoding="utf-8")

        print(json.dumps(summary, ensure_ascii=False, indent=2))
    finally:
        if proc.poll() is None:
            proc.kill()
        proc.wait(timeout=5)


if __name__ == "__main__":
    artifact_scratch = pathlib.Path(r"C:\Users\Tan\.gemini\antigravity\brain\c8c3f2d9-4eaf-469b-bdd7-67467536b1d1\scratch")
    out_p = artifact_scratch / "beta_envi_holdout_results.json"
    blind_p = artifact_scratch / "beta_envi_blind_pairs.json"
    run_benchmark(out_p, blind_p)
