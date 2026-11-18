import os
import time
import ctranslate2
import transformers

model_dir = os.path.expandvars(r"%LocalAppData%\ScreenSubAI\Models\translation\m2m100_418m_ct2")
print("Loading model and tokenizer...")
translator = ctranslate2.Translator(model_dir, device="cpu", compute_type="int8", intra_threads=4)
tokenizer = transformers.M2M100Tokenizer.from_pretrained(model_dir)

# 30 unseen sentences per direction (movie dialogue, questions, short/long, idioms, daily life)
TEST_SETS = {
    ("en", "vi"): [
        "What time does the last train leave tonight?",
        "I didn't expect to see you here after all these years.",
        "Could you please lower your voice? The baby is sleeping.",
        "We need to find shelter before the storm hits the coast.",
        "Don't worry about the mistake; everyone makes them sometimes.",
        "She has been practicing the piano for more than three hours.",
        "Are you going to attend the annual conference in New York?",
        "The food at this restaurant is surprisingly delicious.",
        "I have never seen such a breathtaking view from the mountaintop.",
        "Why didn't you answer my phone call yesterday afternoon?",
        "Please remember to lock the front door before going to bed.",
        "He promised that he would help us finish the project on time.",
        "How much does this vintage leather jacket cost?",
        "The detective carefully examined every clue left at the scene.",
        "I really appreciate everything you have done for our family.",
        "Look at those dark storm clouds moving across the sky.",
        "Can you recommend a good Vietnamese restaurant nearby?",
        "If we hurry up, we might still catch the opening ceremony.",
        "She smiled warmly and handed him a cup of hot green tea.",
        "This movie is much more interesting than the previous one.",
        "Where did you buy this beautiful handmade silk scarf?",
        "It is always important to stay calm during an emergency.",
        "The doctor advised him to get plenty of rest and drink water.",
        "I'm afraid we won't be able to finish before the deadline.",
        "Listen to me carefully, because I will only explain this once.",
        "Technology has completely changed the way people communicate.",
        "Do you mind if I open the window for some fresh air?",
        "He decided to study computer science at the state university.",
        "Nothing can stop us from reaching our goals if we stay united.",
        "I hope you have a safe and wonderful journey back home."
    ],
    ("vi", "en"): [
        "Hôm nay thời tiết ở Hà Nội thật sự rất đẹp.",
        "Bạn có thể chỉ đường cho tôi đến viện bảo tàng gần nhất không?",
        "Tôi nghĩ chúng ta nên thảo luận vấn đề này vào sáng mai.",
        "Đừng lo lắng, mọi chuyện rồi sẽ ổn thỏa cả thôi.",
        "Bộ phim này đã để lại cho tôi rất nhiều cảm xúc sâu sắc.",
        "Anh ấy đã làm việc tại công ty này được hơn năm năm.",
        "Xin vui lòng cho tôi xin một ly nước lọc có đá.",
        "Chúng tôi đang lên kế hoạch cho chuyến du lịch mùa hè tới Đà Nẵng.",
        "Bạn đã hoàn thành bản báo cáo tài chính của quý này chưa?",
        "Cảm ơn bạn rất nhiều vì đã luôn ủng hộ tôi suốt thời gian qua.",
        "Trời sắp mưa rồi, bạn nhớ mang theo áo mưa khi ra đường nhé.",
        "Món ăn Việt Nam nổi tiếng khắp thế giới nhờ hương vị thanh đạm.",
        "Nếu bạn có bất kỳ câu hỏi nào, xin đừng ngần ngại liên hệ với tôi.",
        "Gia đình là điểm tựa vững chắc nhất của mỗi con người.",
        "Cô ấy vừa mới tốt nghiệp chuyên ngành kỹ thuật phần mềm.",
        "Cuộc họp sẽ bắt đầu lúc chín giờ sáng tại phòng hội thảo lớn.",
        "Tôi cảm thấy rất tự hào về những thành tựu mà bạn đã đạt được.",
        "Hãy giữ bình tĩnh và tìm kiếm giải pháp tốt nhất cho tình huống này.",
        "Chúng tôi hy vọng sẽ có cơ hội hợp tác lâu dài với quý đối tác.",
        "Bạn thích uống cà phê sữa đá hay trà đào cam sả hơn?",
        "Con đường phía trước còn nhiều thử thách nhưng chúng tôi không bỏ cuộc.",
        "Cửa hàng tiện lợi mở cửa hai mươi tư trên bảy ngay góc phố.",
        "Tôi đã đọc cuốn sách này ba lần và vẫn thấy rất bổ ích.",
        "Sự chân thành là yếu tố quan trọng nhất trong tình bạn.",
        "Bạn có thể giải thích lại phần nội dung này được không?",
        "Buổi hòa nhạc tối qua thực sự rất bùng nổ và tràn đầy năng lượng.",
        "Trẻ em luôn cần được yêu thương và giáo dục trong môi trường an toàn.",
        "Tôi sẽ gửi tài liệu qua thư điện tử ngay sau khi kiểm tra xong.",
        "Thời gian trôi qua thật nhanh, mới đó mà đã hết một năm.",
        "Chúc bạn một ngày làm việc hiệu quả và tràn đầy niềm vui."
    ],
    ("zh", "vi"): [
        "请问去最近的地铁站应该怎么走？",
        "今天晚上的天气看起来很适合散步。",
        "如果你遇到困难，随时都可以来找我帮忙。",
        "这部纪录片深入探讨了环境保护的重要意义。",
        "他经过多年的努力，终于实现了自己的创业梦想。",
        "请在表格上填写您的姓名和联系电话。",
        "我们要学会珍惜身边每一个关心自己的人。",
        "这家餐厅的北京烤鸭味道非常正宗地道。",
        "会议已经推迟到下周一上午十点举行。",
        "听到这个令人振奋的好消息，大家都高兴极了。",
        "旅行不仅能开阔眼界，还能让人结交新朋友。",
        "请不要在公共场所大声喧哗，以免影响他人。",
        "医生建议他在康复期间保持清淡的饮食习惯。",
        "虽然面临很多挑战，但我们依然对未来充满信心。",
        "你认为人工智能在未来会取代人类的工作吗？",
        "这本书的内容非常精彩，让人忍不住一口气读完。",
        "请务必在截止日期之前提交完整的申请材料。",
        "父母对孩子的关爱是世界上最无私的情感。",
        "我们计划今年冬天去哈尔滨观赏冰雕艺术展。",
        "坚持每天阅读半小时可以有效积累词汇量。",
        "这台笔记本电脑的续航能力比旧款提升了很多。",
        "友谊需要双方共同用心去经营和维护。",
        "下周我们将迎来一年一度的传统中秋佳节。",
        "无论发生什么事情，请一定要保持积极乐观的态度。",
        "老师耐心地为学生们解答了关于数学题的疑问。",
        "城市的发展离不开每一位普通劳动者的辛勤付出。",
        "他在演讲比赛中表现优异，赢得了观众热烈的掌声。",
        "请在离开房间前确认所有电源和灯光已经关闭。",
        "健康的生活方式包括合理的饮食和充足的睡眠。",
        "祝您和您的家人度过一个温馨快乐的周末假期。"
    ],
    ("vi", "zh"): [
        "Xin chào, rất vui được gặp bạn trong hội nghị ngày hôm nay.",
        "Bạn có thể giới thiệu cho tôi vài món ăn đặc sản địa phương không?",
        "Tôi đang học tiếng Trung để chuẩn bị cho chuyến công tác sắp tới.",
        "Chúng ta nên bắt đầu chuẩn bị bài thuyết trình từ bây giờ.",
        "Thời tiết mùa thu ở Hà Nội rất mát mẻ và dễ chịu.",
        "Dự án này đòi hỏi sự phối hợp chặt chẽ giữa các phòng ban.",
        "Cảm ơn sự hỗ trợ nhiệt tình của bạn trong suốt dự án.",
        "Tôi muốn đặt vé máy bay khứ hồi đi Bắc Kinh vào tuần tới.",
        "Trẻ em rất thích tham gia các hoạt động ngoại khóa vào cuối tuần.",
        "Sức khỏe luôn là tài sản quý giá nhất của mỗi con người.",
        "Anh ấy là một kỹ sư rất có trách nhiệm và giàu kinh nghiệm.",
        "Vui lòng gửi cho tôi bản hợp đồng đã ký qua email trong ngày hôm nay.",
        "Chúng tôi cam kết cung cấp dịch vụ tốt nhất cho khách hàng.",
        "Bạn có muốn cùng tôi đi dạo quanh hồ sau giờ làm việc không?",
        "Tập thể dục thường xuyên giúp tăng cường hệ miễn dịch.",
        "Cuốn tiểu thuyết này đã được dịch ra hơn hai mươi thứ tiếng.",
        "Tôi rất ấn tượng với sự phát triển nhanh chóng của công nghệ thông tin.",
        "Đừng ngần ngại nói lên ý kiến của bạn trong cuộc họp.",
        "Chúng ta cần tìm hiểu kỹ thị trường trước khi đưa ra quyết định.",
        "Mỗi chuyến đi đều mang lại cho tôi những trải nghiệm vô cùng quý giá.",
        "Gia đình luôn là nơi bình yên nhất để trở về sau ngày dài mệt mỏi.",
        "Tôi hy vọng tình hình kinh tế sẽ khởi sắc hơn trong năm tới.",
        "Bạn đã thử món phở truyền thống của Việt Nam chưa?",
        "Hãy luôn giữ vững niềm tin vào bản thân dù có chuyện gì xảy ra.",
        "Buổi lễ tốt nghiệp sẽ diễn ra vào sáng thứ Bảy tuần này.",
        "Chúng tôi đang tuyển dụng thêm nhiều lập trình viên tài năng.",
        "Bạn có thể giúp tôi kiểm tra lại bản dịch này được không?",
        "Thói quen đọc sách mỗi ngày giúp mở rộng vốn hiểu biết.",
        "Tình bạn chân thành không thể đong đếm bằng vật chất.",
        "Chúc bạn một chuyến đi thượng lộ bình an và nhiều may mắn."
    ],
    ("en", "zh"): [
        "Could you please explain this concept one more time?",
        "The newly opened shopping mall is crowded with visitors today.",
        "We are looking forward to establishing a partnership with your company.",
        "Don't forget to back up your important files regularly.",
        "She showed extraordinary courage in the face of immense danger.",
        "The flight from London to Tokyo was delayed by two hours.",
        "Environmental protection is a shared responsibility for all mankind.",
        "He has a talent for explaining complex algorithms in simple terms.",
        "I was deeply touched by the kindness of the local people.",
        "Can you help me carry these heavy boxes up to the second floor?",
        "The scientific breakthrough will benefit millions of patients worldwide.",
        "Please ensure all safety regulations are strictly followed in the laboratory.",
        "Learning a foreign language opens up new opportunities and perspectives.",
        "The museum displays artifacts dating back thousands of years.",
        "We should always respect different cultures and traditions.",
        "How do you usually spend your free time on the weekends?",
        "The company announced a significant increase in its quarterly revenue.",
        "It takes patience and persistence to master any musical instrument.",
        "Please let me know if you require any additional assistance.",
        "The city skyline looks magnificent when viewed from the harbor bridge.",
        "She graduated with honors from the department of electrical engineering.",
        "Taking regular breaks can significantly improve your working productivity.",
        "The international film festival attracted directors from around the world.",
        "I am confident that our team can overcome this temporary difficulty.",
        "Why did the manager decide to reschedule the team meeting?",
        "Artificial intelligence is transforming industries across the global economy.",
        "Thank you for sharing your valuable insights during the workshop.",
        "He enjoys hiking in the national park whenever the weather is sunny.",
        "Clear communication is essential for effective teamwork and leadership.",
        "We wish you continued success and happiness in all your future endeavors."
    ],
    ("zh", "en"): [
        "请问您需要预订无烟楼层的房间吗？",
        "随着科技的发展，人们的生活方式正在发生巨大的改变。",
        "面对困难时，我们应该积极寻找解决问题的方法。",
        "这幅中国传统水墨画展现了高超的艺术水准。",
        "他由于工作出色，最近被提拔为部门主管。",
        "我们期待在不久的将来能够再次与您相聚。",
        "每天保持适量的运动对身心健康大有裨益。",
        "请在登机前准备好您的有效护照和登机牌。",
        "阅读优秀的书籍就像是与智者进行跨越时空的对话。",
        "这部手机的拍照效果在弱光环境下依然表现出色。",
        "为了保护生态环境，我们应当尽量减少塑料制品的使用。",
        "他在团队中始终扮演着沟通桥梁的关键角色。",
        "老师鼓励学生们在课堂上积极发表自己的独立见解。",
        "这道传统菜肴的制作工艺非常讲究火候和调料。",
        "无论遇到多大的挫折，我们都不应该轻易放弃希望。",
        "请确认您的收货地址和电话信息无误后再付款。",
        "经过全体员工的共同拼搏，公司超额完成了年度目标。",
        "文化交流有助于增进不同国家人民之间的理解与友谊。",
        "雨后的空气格外清新，公园里盛开着各种鲜艳的花朵。",
        "合理的理财规划能够帮助家庭实现长期的财务稳健。",
        "现代医学的发展极大地提高了人类的平均预期寿命。",
        "您对我们今天提供的客户服务感到满意吗？",
        "时间是最公平的资源，每个人每天都拥有二十四小时。",
        "他在科研领域取得了令人瞩目的突破性研究成果。",
        "请大家遵守公共交通秩序，主动给老弱妇孺让座。",
        "学习新技能需要长期的积累和持续不断的练习。",
        "这个古老的故事代代相传，蕴含着深刻的人生哲理。",
        "我们在做重要决定之前应当充分听取各方的意见。",
        "互联网为远程办公和在线教育提供了极大的便利。",
        "祝愿两国的文化与经贸合作在未来取得更加丰硕的成果。"
    ]
}

print(f"Starting benchmark across 6 directions (30 sentences each = 180 total)...")

benchmark_results = {}

for (src, tgt), sentences in TEST_SETS.items():
    direction_key = f"{src.upper()} -> {tgt.upper()}"
    print(f"\n==========================================")
    print(f"  Testing Direction: {direction_key} (30 unseen sentences)")
    print(f"==========================================")
    
    m2m_src = src
    m2m_tgt = f"__{tgt}__"
    tokenizer.src_lang = m2m_src
    
    latencies = []
    sample_outputs = []
    
    for idx, text in enumerate(sentences):
        tokens = tokenizer.convert_ids_to_tokens(tokenizer.encode(text))
        t0 = time.perf_counter()
        results = translator.translate_batch([tokens], target_prefix=[[m2m_tgt]], beam_size=1)
        t1 = time.perf_counter()
        lat_ms = (t1 - t0) * 1000
        latencies.append(lat_ms)
        
        hyp = results[0].hypotheses[0]
        out_tokens = hyp[1:] if hyp and hyp[0] == m2m_tgt else hyp
        out_ids = tokenizer.convert_tokens_to_ids(out_tokens)
        translated = tokenizer.decode(out_ids, skip_special_tokens=True).strip()
        
        if idx < 5:  # Collect first 5 for reporting
            sample_outputs.append((text, translated, lat_ms))
            print(f"  [{idx+1}/30] ({lat_ms:.1f} ms) {text[:40]}... -> {translated[:40]}...")

    latencies.sort()
    p50 = latencies[len(latencies) // 2]
    p90 = latencies[int(len(latencies) * 0.9)]
    p95 = latencies[int(len(latencies) * 0.95)]
    mean_lat = sum(latencies) / len(latencies)
    
    benchmark_results[direction_key] = {
        "count": len(sentences),
        "mean_ms": mean_lat,
        "p50_ms": p50,
        "p90_ms": p90,
        "p95_ms": p95,
        "samples": sample_outputs
    }
    
    print(f"  Summary {direction_key}: Mean={mean_lat:.1f}ms, P50={p50:.1f}ms, P90={p90:.1f}ms, P95={p95:.1f}ms")

print("\n\n==========================================")
print("       ALL 6 DIRECTIONS BENCHMARK TABLE")
print("==========================================")
print(f"{'Direction':<12} | {'Sentences':<9} | {'Mean (ms)':<10} | {'P50 (ms)':<9} | {'P90 (ms)':<9} | {'P95 (ms)':<9}")
print("-" * 68)
for k, v in benchmark_results.items():
    print(f"{k:<12} | {v['count']:<9} | {v['mean_ms']:<10.1f} | {v['p50_ms']:<9.1f} | {v['p90_ms']:<9.1f} | {v['p95_ms']:<9.1f}")
print("==========================================")
