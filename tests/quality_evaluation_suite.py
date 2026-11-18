import os
import time
import ctranslate2
import transformers

model_dir = os.path.expandvars(r"%LocalAppData%\ScreenSubAI\Models\translation\m2m100_418m_ct2")
print("Loading M2M-100 model for quality evaluation...")
translator = ctranslate2.Translator(model_dir, device="cpu", compute_type="int8", intra_threads=4)
tokenizer = transformers.M2M100Tokenizer.from_pretrained(model_dir)

CATEGORIES = [
    {
        "category": "1. Lời thoại phim (Movie Dialogue)",
        "pairs": [
            ("en", "vi", "May the Force be with you, always."),
            ("en", "vi", "Houston, we have a problem here."),
            ("zh", "vi", "报告长官，敌军已经突破了第一道防线！"),
            ("vi", "en", "Chúng ta không thể bỏ rơi đồng đội ở lại chiến trường được.")
        ]
    },
    {
        "category": "2. Câu hội thoại dài (Long Compound Sentences)",
        "pairs": [
            ("en", "vi", "Even though the journey was exhausting and fraught with unexpected dangers, everyone remained optimistic about reaching the summit before dusk."),
            ("zh", "vi", "尽管在项目研发过程中遇到了许多前所未有的技术难题，但整个团队始终保持着高度的凝聚力与专注。"),
            ("vi", "en", "Mặc dù thời tiết hôm nay không mấy thuận lợi do trời mưa rào tầm tã, buổi hòa nhạc ngoài trời vẫn thu hút hàng ngàn khán giả nhiệt thành.")
        ]
    },
    {
        "category": "3. Danh từ riêng & Tên nhân vật (Proper Nouns & Characters)",
        "pairs": [
            ("en", "vi", "Sherlock Holmes and Dr. John Watson arrived at Baker Street."),
            ("zh", "vi", "孙悟空和诸葛亮是中国古典文学中家喻户晓的著名人物。"),
            ("vi", "en", "Nguyễn Du là đại thi hào của dân tộc Việt Nam với tác phẩm Truyện Kiều.")
        ]
    },
    {
        "category": "4. Thành ngữ & Tiếng lóng (Idioms & Colloquialisms)",
        "pairs": [
            ("en", "vi", "It's raining cats and dogs outside right now."),
            ("en", "vi", "Piece of cake! Don't sweat the small stuff."),
            ("zh", "vi", "塞翁失马，焉知非福，凡事都要往好处看。"),
            ("zh", "vi", "画蛇添足只会把原本简单的事情弄得更加复杂。"),
            ("vi", "zh", "Có công mài sắt, có ngày nên kim."),
            ("vi", "en", "Vạn sự khởi đầu nan, gian nan đừng nản chí.")
        ]
    },
    {
        "category": "5. Câu hỏi hội thoại (Inquiries & Questions)",
        "pairs": [
            ("en", "vi", "Could you possibly tell me where I can find the emergency exit?"),
            ("zh", "vi", "你真的确信这个决定不会给公司带来潜在的法律风险吗？"),
            ("vi", "en", "Bao lâu nữa thì chuyến bay tiếp theo cất cánh?")
        ]
    },
    {
        "category": "6. Phụ đề hai dòng có xuống dòng (Dual-line Subtitles)",
        "pairs": [
            ("en", "vi", "Wait for me at the station.\nI will be there in ten minutes."),
            ("zh", "vi", "请在原地稍等片刻。\n救援队马上就到。"),
            ("vi", "zh", "Hãy giữ bình tĩnh.\nChúng tôi đang trên đường tới.")
        ]
    },
    {
        "category": "7. Tên địa danh Việt Nam (Vietnamese Geographical Names)",
        "pairs": [
            ("vi", "en", "Thành phố Hồ Chí Minh, Hà Nội, Đà Nẵng và Vịnh Hạ Long là những điểm đến nổi tiếng."),
            ("en", "vi", "We traveled from Da Nang to Hue across the Hai Van Pass."),
            ("zh", "vi", "越南的岘港、芽庄和富国岛是极具吸引力的海滨旅游胜地。")
        ]
    },
    {
        "category": "8. Ngữ cảnh đa nghĩa / Thiếu ngữ cảnh (Context Ambiguity)",
        "pairs": [
            ("en", "vi", "Bank"), # River bank vs Financial bank
            ("en", "vi", "I can't bear this anymore."), # Bear (animal) vs Bear (endure)
            ("zh", "vi", "我想静静。"), # Jingjing (person name) vs Jingjing (quiet)
            ("vi", "en", "Đường này ngọt quá.") # Đường (road) vs Đường (sugar)
        ]
    }
]

print("================================================================================")
print("             SCREENSUB AI - PHASE 10D TRANSLATION QUALITY AUDIT")
print("================================================================================")

results_summary = []

for group in CATEGORIES:
    print(f"\n### {group['category']}")
    print("-" * 80)
    for src, tgt, text in group["pairs"]:
        tokenizer.src_lang = src
        # For dual-line subtitles, translate line-by-line or together
        tokens = tokenizer.convert_ids_to_tokens(tokenizer.encode(text.replace("\n", " ")))
        tgt_token = f"__{tgt}__"
        t0 = time.perf_counter()
        results = translator.translate_batch([tokens], target_prefix=[[tgt_token]], beam_size=1)
        lat_ms = (time.perf_counter() - t0) * 1000
        hyp = results[0].hypotheses[0]
        out_tokens = hyp[1:] if hyp and hyp[0] == tgt_token else hyp
        out_ids = tokenizer.convert_tokens_to_ids(out_tokens)
        translated = tokenizer.decode(out_ids, skip_special_tokens=True).strip()
        
        display_src = text.replace('\n', ' [LF] ')
        print(f"[{src.upper()} -> {tgt.upper()}] ({lat_ms:.1f}ms):")
        print(f"  SRC: {display_src}")
        print(f"  TGT: {translated}")
        results_summary.append({
            "category": group["category"],
            "pair": f"{src.upper()}->{tgt.upper()}",
            "src": text,
            "tgt": translated,
            "latency": lat_ms
        })

print("\n================================================================================")
print("Quality evaluation completed successfully.")

