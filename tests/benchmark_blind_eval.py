#!/usr/bin/env python3
"""
ScreenSub AI — Blind Unseen Subtitle Evaluation (106 sentences across 6 directions, >=60 En->Vi)
Strictly disjoint from benchmark_120_sentences.py and benchmark_holdout_36.py.
Evaluates raw + postprocessed neural output, P50/P95 latency, RSS memory, and startup time.
"""

import os
import sys
import time
import json
import ctypes
import ctypes.wintypes
import statistics

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "workers", "translation"))

import translation_worker as tw

# 66 unseen En->Vi conversational sentences + 16 Zh->Vi + 6 each for Vi->En, Vi->Zh, En->Zh, Zh->En = 106 sentences
BLIND_DATASET = [
    # === 1. Idioms & Phrasal Verbs (En -> Vi, 12) ===
    ("That rings a bell, doesn't it?", "en", "vi", "idiom_phrasal"),
    ("Stop beating around the bush and tell me straight.", "en", "vi", "idiom_phrasal"),
    ("We'll play it by ear after lunch.", "en", "vi", "idiom_phrasal"),
    ("Don't get your hopes up too high just yet.", "en", "vi", "idiom_phrasal"),
    ("You totally blew it this time.", "en", "vi", "idiom_phrasal"),
    ("I'm starting to get the hang of it.", "en", "vi", "idiom_phrasal"),
    ("Hang in there, things will turn around.", "en", "vi", "idiom_phrasal"),
    ("He always backs out at the last minute.", "en", "vi", "idiom_phrasal"),
    ("I ran into an old classmate on the subway.", "en", "vi", "idiom_phrasal"),
    ("Let's wrap this up before five o'clock.", "en", "vi", "idiom_phrasal"),
    ("She spilled the beans right in front of him.", "en", "vi", "idiom_phrasal"),
    ("We need to figure this out on our own.", "en", "vi", "idiom_phrasal"),

    # === 2. Light Verbs & Natural Conversational Dialogue (En -> Vi, 12) ===
    ("Go ahead and grab a chair.", "en", "vi", "dialogue_light_verb"),
    ("I could really use some sleep right now.", "en", "vi", "dialogue_light_verb"),
    ("Did you end up making the flight?", "en", "vi", "dialogue_light_verb"),
    ("Take a wild guess who just called me.", "en", "vi", "dialogue_light_verb"),
    ("You're making way too big a deal out of this.", "en", "vi", "dialogue_light_verb"),
    ("I'll drop you off at the corner.", "en", "vi", "dialogue_light_verb"),
    ("Can you keep an eye on my bag for a minute?", "en", "vi", "dialogue_light_verb"),
    ("Let's grab a coffee and catch up.", "en", "vi", "dialogue_light_verb"),
    ("He gave me a weird look when I walked in.", "en", "vi", "dialogue_light_verb"),
    ("Have a safe trip back home!", "en", "vi", "dialogue_light_verb"),
    ("Make sure you lock the back door tonight.", "en", "vi", "dialogue_light_verb"),
    ("I had such a rough week at work.", "en", "vi", "dialogue_light_verb"),

    # === 3. Short Replies, Slang & Ellipses (En -> Vi, 10) ===
    ("Fair enough.", "en", "vi", "short_slang"),
    ("Count me in.", "en", "vi", "short_slang"),
    ("Not a chance.", "en", "vi", "short_slang"),
    ("Tell me about it.", "en", "vi", "short_slang"),
    ("My bad, I missed your text.", "en", "vi", "short_slang"),
    ("Sounds sketchy to me.", "en", "vi", "short_slang"),
    ("You're kidding, right?", "en", "vi", "short_slang"),
    ("Long story short, we missed the train.", "en", "vi", "short_slang"),
    ("Wait up! You dropped your keys.", "en", "vi", "short_slang"),
    ("Whatever works for you.", "en", "vi", "short_slang"),

    # === 4. Movie / YouTube Dialogue & Emotional Reactions (En -> Vi, 12) ===
    ("Why didn't you say something earlier?!", "en", "vi", "media_emotional"),
    ("You have no idea what I just went through.", "en", "vi", "media_emotional"),
    ("Look, I didn't sign up for this.", "en", "vi", "media_emotional"),
    ("Stay right where you are, don't move a muscle.", "en", "vi", "media_emotional"),
    ("Honestly, I saw that coming from a mile away.", "en", "vi", "media_emotional"),
    ("Wait—did you actually hear what she just said?", "en", "vi", "media_emotional"),
    ("Please, just hear me out for two seconds.", "en", "vi", "media_emotional"),
    ("I swear I left my wallet right on this counter.", "en", "vi", "media_emotional"),
    ("Hey guys, welcome back to the channel!", "en", "vi", "media_emotional"),
    ("Before we jump in, smash that subscribe button.", "en", "vi", "media_emotional"),
    ("That escalated way faster than I expected.", "en", "vi", "media_emotional"),
    ("I'm literally freezing out here.", "en", "vi", "media_emotional"),

    # === 5. Proper Names, Numbers, Tech & Mixed Clauses (En -> Vi, 10) ===
    ("Alex moved to Da Nang back in 2022.", "en", "vi", "proper_tech"),
    ("Check out the settings tab in the top right corner.", "en", "vi", "proper_tech"),
    ("Our next update drops at 8:30 PM tonight.", "en", "vi", "proper_tech"),
    ("Nina asked if we could postpone Monday's review.", "en", "vi", "proper_tech"),
    ("Plug the HDMI cable into the second port.", "en", "vi", "proper_tech"),
    ("Microsoft released a new patch earlier this morning.", "en", "vi", "proper_tech"),
    ("The ticket costs roughly forty-five dollars each.", "en", "vi", "proper_tech"),
    ("Press Ctrl+Shift+S to capture the subtitle region.", "en", "vi", "proper_tech"),
    ("Marcus is flying out of Singapore tomorrow afternoon.", "en", "vi", "proper_tech"),
    ("Battery dropped below 15 percent during the stream.", "en", "vi", "proper_tech"),

    # === 6. Longer Multi-Clause Conversational Subtitles (En -> Vi, 10) ===
    ("If we leave right now, we might still catch the ferry before the storm hits.", "en", "vi", "long_multi"),
    ("She kept smiling through dinner, even though everyone knew something was off.", "en", "vi", "long_multi"),
    ("Every time I try explaining how the budget works, someone changes the subject.", "en", "vi", "long_multi"),
    ("I thought fixing one tiny bug would take ten minutes, not half the night.", "en", "vi", "long_multi"),
    ("Whenever the connection slows down, the video drops to 480p automatically.", "en", "vi", "long_multi"),
    ("He promised to pay me back by Friday, which I honestly doubt will happen.", "en", "vi", "long_multi"),
    ("Once you get past the steep climb, the whole coastline opens up below you.", "en", "vi", "long_multi"),
    ("Neither of us had cash on us, so we ended up splitting the cab fare later.", "en", "vi", "long_multi"),
    ("I'd rather walk in the rain than wait another hour for a delayed bus.", "en", "vi", "long_multi"),
    ("By the time we reached the theater, the opening credits were already rolling.", "en", "vi", "long_multi"),

    # === 7. ZH -> VI (8 Simplified + 8 Traditional = 16) ===
    ("你别开玩笑了，这怎么可能？", "zh", "vi", "zh_simp_conv"),
    ("算了，就当我没说过。", "zh", "vi", "zh_simp_conv"),
    ("我手机快没电了，借我充一下。", "zh", "vi", "zh_simp_conv"),
    ("你今天怎么这么晚才下班？", "zh", "vi", "zh_simp_conv"),
    ("话不能这么说，他其实也挺不容易的。", "zh", "vi", "zh_simp_conv"),
    ("帮我把桌上的那杯冰水递过来好吗？", "zh", "vi", "zh_simp_conv"),
    ("等会下楼去买点夜宵吧，我都饿扁了。", "zh", "vi", "zh_simp_conv"),
    ("只要坚持下去，总会有办法解决的。", "zh", "vi", "zh_simp_conv"),
    ("先別急著下結論，聽他把話說完。", "zh", "vi", "zh_trad_conv"),
    ("這場雨到底要下到什麼時候啊？", "zh", "vi", "zh_trad_conv"),
    ("太誇張了吧，排隊排了兩個小時還沒輪到我們。", "zh", "vi", "zh_trad_conv"),
    ("你確定要把鑰匙留在門衛那邊嗎？", "zh", "vi", "zh_trad_conv"),
    ("難得大家都聚在一起，今晚不醉不歸！", "zh", "vi", "zh_trad_conv"),
    ("抱歉，剛才信號不好，我沒聽清楚。", "zh", "vi", "zh_trad_conv"),
    ("他嘴上說不在意，其實心裡比誰都緊張。", "zh", "vi", "zh_trad_conv"),
    ("如果周末天氣放晴，我們就去海邊露營。", "zh", "vi", "zh_trad_conv"),

    # === 8. VI -> EN (6) ===
    ("Thôi bỏ qua đi, chuyện nhỏ mà.", "vi", "en", "vi_to_en"),
    ("Tối nay tụi mình ra quán cũ ngồi nhé.", "vi", "en", "vi_to_en"),
    ("Mình tìm mãi mà chẳng thấy chìa khóa xe đâu cả.", "vi", "en", "vi_to_en"),
    ("Cậu đừng có nói quá lên như thế chứ.", "vi", "en", "vi_to_en"),
    ("Tranh thủ làm nốt đi rồi còn về sớm.", "vi", "en", "vi_to_en"),
    ("Nghe giọng là biết ngay cậu vừa mới ngủ dậy.", "vi", "en", "vi_to_en"),

    # === 9. VI -> ZH (6) ===
    ("Đợi mình năm phút, mình xuống ngay đây.", "vi", "zh", "vi_to_zh"),
    ("Món này hơi cay một chút nhưng ăn rất cuốn.", "vi", "zh", "vi_to_zh"),
    ("Ngoài trời đang gió lớn lắm, nhớ mặc thêm áo ấm.", "vi", "zh", "vi_to_zh"),
    ("Lâu rồi tụi mình chưa có dịp nói chuyện với nhau.", "vi", "zh", "vi_to_zh"),
    ("Cứ bình tĩnh, mọi việc rồi đâu sẽ vào đấy thôi.", "vi", "zh", "vi_to_zh"),
    ("Chiều nay sếp vừa thông báo hoãn cuộc họp sang thứ Năm.", "vi", "zh", "vi_to_zh"),

    # === 10. EN -> ZH (6) ===
    ("Skip the intro and jump straight to the demo.", "en", "zh", "en_to_zh"),
    ("I barely slept four hours last night.", "en", "zh", "en_to_zh"),
    ("Keep me posted if anything changes before noon.", "en", "zh", "en_to_zh"),
    ("No wonder the street was completely blocked off.", "en", "zh", "en_to_zh"),
    ("Grab a jacket, it's getting chilly outside.", "en", "zh", "en_to_zh"),
    ("Did you seriously eat the whole pizza by yourself?", "en", "zh", "en_to_zh"),

    # === 11. ZH -> EN (6) ===
    ("别紧张，深呼吸，慢慢来。", "zh", "en", "zh_to_en"),
    ("我差点忘了今天要交报告。", "zh", "en", "zh_to_en"),
    ("这家小店的牛肉面特别地道。", "zh", "en", "zh_to_en"),
    ("你把空调温度调高两度好吗？", "zh", "en", "zh_to_en"),
    ("沒想到在這裡也能遇到老朋友。", "zh", "en", "zh_to_en"),
    ("既然决定了，那就放手去做吧。", "zh", "en", "zh_to_en"),
]


def percentile(sorted_vals, pct):
    if not sorted_vals:
        return 0.0
    idx = min(len(sorted_vals) - 1, int(round((pct / 100.0) * (len(sorted_vals) - 1))))
    return sorted_vals[idx]


def get_rss_mb():
    try:
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
        pmc = PROCESS_MEMORY_COUNTERS()
        pmc.cb = ctypes.sizeof(PROCESS_MEMORY_COUNTERS)
        kernel32 = ctypes.windll.kernel32
        kernel32.GetCurrentProcess.restype = ctypes.c_void_p
        h = kernel32.GetCurrentProcess()
        fn = getattr(kernel32, "K32GetProcessMemoryInfo", None)
        if fn is None:
            fn = ctypes.windll.psapi.GetProcessMemoryInfo
        fn.argtypes = [ctypes.c_void_p, ctypes.POINTER(PROCESS_MEMORY_COUNTERS), ctypes.wintypes.DWORD]
        fn.restype = ctypes.wintypes.BOOL
        if fn(h, ctypes.byref(pmc), pmc.cb):
            return round(pmc.WorkingSetSize / (1024 * 1024), 1), round(pmc.PeakWorkingSetSize / (1024 * 1024), 1)
    except Exception:
        pass
    return 0.0, 0.0


def verify_disjoint():
    from benchmark_holdout_36 import HOLDOUT_DATASET
    import ast
    b120_path = os.path.join(ROOT, "tests", "benchmark_120_sentences.py")
    with open(b120_path, "r", encoding="utf-8") as f:
        tree = ast.parse(f.read())
    seen_texts = {x[0].strip().lower() for x in HOLDOUT_DATASET}
    for node in tree.body:
        if isinstance(node, ast.Assign) and any(getattr(t, "id", None) == "BENCHMARK_DATASET" for t in node.targets):
            for elt in node.value.elts:
                seen_texts.add(elt.elts[0].value.strip().lower())
    overlap = [x[0] for x in BLIND_DATASET if x[0].strip().lower() in seen_texts]
    if overlap:
        raise AssertionError(f"Overlap detected with seen/holdout datasets: {overlap}")


def main():
    verify_disjoint()
    rss_before, _ = get_rss_mb()
    load_ms = tw.load_neural_model()
    rss_loaded, _ = get_rss_mb()
    tw.translate_text("Warmup sentence.", "en", "vi")

    rows = []
    latencies = []
    by_pair = {}
    for text, src, tgt, cat in BLIND_DATASET:
        t0 = time.perf_counter()
        res = tw.translate_text(text, src, tgt, diagnostic=True)
        ms = (time.perf_counter() - t0) * 1000.0
        latencies.append(ms)
        pair = f"{src}->{tgt}"
        by_pair.setdefault(pair, []).append(ms)
        rows.append({
            "pair": pair,
            "cat": cat,
            "input": text,
            "raw": res["raw_translated_text"],
            "final": res["translated_text"],
            "ms": round(ms, 1),
            "infer_ms": round(res["infer_ms"], 1),
            "unk_count": res["diagnostic"]["unk_count"],
        })

    rss_end, peak_rss = get_rss_mb()
    s_lat = sorted(latencies)
    pair_stats = {}
    for pair, lats in by_pair.items():
        sl = sorted(lats)
        pair_stats[pair] = {
            "count": len(lats),
            "mean_ms": round(statistics.mean(lats), 1),
            "p50_ms": round(percentile(sl, 50), 1),
            "p95_ms": round(percentile(sl, 95), 1),
        }

    out = {
        "model": "m2m100_418m_ct2",
        "fingerprint": tw.current_model_fingerprint,
        "load_ms": load_ms,
        "rss_before_mb": rss_before,
        "rss_loaded_mb": rss_loaded,
        "rss_end_mb": rss_end,
        "peak_rss_mb": peak_rss,
        "total_sentences": len(rows),
        "overall": {
            "mean_ms": round(statistics.mean(latencies), 1),
            "p50_ms": round(percentile(s_lat, 50), 1),
            "p95_ms": round(percentile(s_lat, 95), 1),
            "max_ms": round(max(latencies), 1),
            "unk_total": sum(r["unk_count"] for r in rows),
        },
        "by_pair": pair_stats,
        "rows": rows,
    }
    print(json.dumps(out, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
