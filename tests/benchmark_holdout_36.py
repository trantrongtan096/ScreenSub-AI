#!/usr/bin/env python3
"""
ScreenSub AI — Unseen Holdout Translation Evaluation (36 sentences, 6 directions)
Verifies generalizable M2M-100 418M INT8 translation quality & latency through translation_worker.py
with zero hardcoded test-phrase substitutions.
"""

import os
import sys
import time
import json
import statistics

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "workers", "translation"))
import translation_worker as tw

HOLDOUT_DATASET = [
    # Everyday conversational & idioms (EN -> VI)
    ("I couldn't care less.", "en", "vi", "idiom_conversational"),
    ("Let's call it a day.", "en", "vi", "idiom_conversational"),
    ("You nailed it!", "en", "vi", "short_emotional"),
    ("Beats me.", "en", "vi", "missing_subject_slang"),
    ("We\u2019re running out of time.", "en", "vi", "curly_quote_contraction"),
    ("Don't jump to conclusions.", "en", "vi", "idiom_conversational"),
    ("It takes two to tango.", "en", "vi", "word_for_word_trap"),
    ("She's been under the weather lately.", "en", "vi", "idiom_conversational"),
    ("Wait here, I'll check with David in Tokyo.", "en", "vi", "proper_names_multi"),
    ("Seriously? You actually believed that?", "en", "vi", "multi_sentence_emotional"),
    ("Just say the word and I'll be there.", "en", "vi", "conversational"),
    ("As soon as the rain stopped, we packed up and headed down the valley before dark.", "en", "vi", "long_sentence"),

    # ZH -> VI (Simp + Trad, emotional, conversational, short, long)
    ("这下可麻烦大了。", "zh", "vi", "zh_emotional"),
    ("别客气，随便坐。", "zh", "vi", "zh_dialogue"),
    ("沒關係，我們再試一次吧。", "zh", "vi", "zh_trad_dialogue"),
    ("你這樣說我真的有點難過。", "zh", "vi", "zh_trad_emotional"),
    ("到底發生什麼事了？快告訴我！", "zh", "vi", "zh_trad_multi"),
    ("李明下个月要去上海出差三天。", "zh", "vi", "zh_proper_name"),

    # VI -> EN
    ("Mình mệt rã rời rồi, nghỉ chút đi.", "vi", "en", "vi_conversational"),
    ("Cậu làm ơn nói nhỏ lại một chút được không?", "vi", "en", "vi_polite_request"),
    ("Chuyện này để sau rồi tính tiếp nhé.", "vi", "en", "vi_idiomatic"),
    ("Mai Lan sẽ bay ra Hà Nội vào sáng thứ Hai.", "vi", "en", "vi_proper_name"),
    ("Không sao đâu, lần sau cố gắng hơn là được.", "vi", "en", "vi_encouragement"),
    ("Trời sắp mưa to rồi đấy, nhớ mang theo áo mưa nhé.", "vi", "en", "vi_multi_clause"),

    # VI -> ZH
    ("Hôm nay đường tắc quá, chắc mình đến muộn mất.", "vi", "zh", "vi_to_zh"),
    ("Bạn ăn tối chưa? Đi ăn phở với tụi mình không?", "vi", "zh", "vi_to_zh"),
    ("Để mình kiểm tra lại lịch họp chiều nay nhé.", "vi", "zh", "vi_to_zh"),
    ("Nghe nói bộ phim mới này hay lắm đấy.", "vi", "zh", "vi_to_zh"),

    # EN -> ZH
    ("Could you speak a little slower, please?", "en", "zh", "en_to_zh"),
    ("I forgot to bring my umbrella again.", "en", "zh", "en_to_zh"),
    ("That sounds like a solid plan.", "en", "zh", "en_to_zh"),
    ("Tell Sarah I'll call her back after lunch.", "en", "zh", "en_to_zh"),

    # ZH -> EN
    ("真不好意思，让你久等了。", "zh", "en", "zh_to_en"),
    ("這家店的咖啡味道真不錯。", "zh", "en", "zh_trad_to_en"),
    ("先别急，把事情看清楚再决定。", "zh", "en", "zh_to_en"),
    ("如果明天不下雨，我们就去爬山。", "zh", "en", "zh_to_en"),
]


def percentile(sorted_vals, pct):
    if not sorted_vals:
        return 0.0
    idx = min(len(sorted_vals) - 1, int(round((pct / 100.0) * (len(sorted_vals) - 1))))
    return sorted_vals[idx]


def main():
    tw.load_neural_model()
    tw.translate_text("Warmup.", "en", "vi")

    latencies = []
    results = []
    for text, src, tgt, cat in HOLDOUT_DATASET:
        t0 = time.perf_counter()
        res = tw.translate_text(text, src, tgt, diagnostic=True)
        ms = (time.perf_counter() - t0) * 1000.0
        latencies.append(ms)
        results.append({
            "src": src,
            "tgt": tgt,
            "cat": cat,
            "input": text,
            "normalized": res["diagnostic"]["normalized_input"],
            "raw": res["raw_translated_text"],
            "final": res["translated_text"],
            "unk_count": res["diagnostic"]["unk_count"],
            "ms": round(ms, 1),
        })

    s_lat = sorted(latencies)
    summary = {
        "count": len(results),
        "model_fingerprint": tw.current_model_fingerprint,
        "mean_ms": round(statistics.mean(latencies), 1),
        "p50_ms": round(percentile(s_lat, 50), 1),
        "p95_ms": round(percentile(s_lat, 95), 1),
        "unk_total": sum(r["unk_count"] for r in results),
    }
    print(json.dumps(summary, indent=2, ensure_ascii=False))
    for r in results:
        print(f"[{r['src']}->{r['tgt']} | {r['cat']}] {r['input']} => {r['final']} ({r['ms']}ms)")


if __name__ == "__main__":
    main()
