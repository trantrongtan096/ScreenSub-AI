# Nhật Ký Thay Đổi (Changelog) - ScreenSub AI

Tất cả các thay đổi đáng chú ý của dự án **ScreenSub AI** qua từng phiên bản phát triển.

---

## [1.0.0-beta.2] - 2026-11-18

### Tính Năng Mới & Củng Cố Độ Tin Cậy (Release Hardening)
- **Multi-Model Offline Translation Router & Rollback An Toàn:**
  - Hỗ trợ định tuyến **VietAI EnViT5 (`envit5_ct2_int8`)** cho cặp hội thoại `EN ↔ VI` và **Facebook M2M-100 (`m2m100_418m_ct2`)** cho `ZH ↔ VI`, `EN ↔ ZH` cùng cơ chế fallback tự động khi thiếu model chuyên biệt.
- **Tối Ưu Hóa Whisper.net (`ggml-base.bin`) & Audio Realtime:**
  - Đồng bộ đường dẫn `%LocalAppData%\ScreenSubAI\Models\whisper\ggml-base.bin` và SHA-256 chính thức (`60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe`).
  - Áp dụng `WithGreedySamplingStrategy(bestOf=1)`, `WithNoContext()`, `WithAudioContextSize(768)` cho phân đoạn VAD `<= 8s` và `ResolveSpeechLanguage()` giúp giảm độ trễ Whisper từ `~2.868 ms` xuống **`~671–678 ms` (P50)** khi chọn `English`/`Chinese` và `~2.040 ms` ở chế độ `Auto`.
  - Sửa lỗi rollover `_speechStartTimeMs` trong `EnergyVoiceActivityDetector` khi thoại liên tục vượt `MaxSpeechDurationMs`.
- **Semantic-Safe Hybrid Deduplication (P1 Safety Fix):**
  - Bổ sung kiểm tra độ mới của OCR (`_lastOcrSeenTimeUtc <= 4.500 ms`), kiểm tra cực phủ định (`不/没/not/never/don't...`), khớp số đếm/thời gian (`三点` vs `四点`, `8:00` vs `9:00`), từ trái nghĩa hành động (`打开` vs `关闭`, `turn on` vs `turn off`, `lock` vs `unlock`), bảo vệ tên người/địa danh (`北京` vs `南京`, `小王` vs `小张`, `Smith` vs `Jones`), và chuẩn hóa chữ Hán Phồn ↔ Giản qua Win32 `LCMapStringEx`.
- **Windows Installer (`v1.0.0-beta.2`) & Sửa Lỗi P0 `InitializeSetup()`:**
  - Khắc phục lỗi mở rộng hằng `{app}` sớm trong `ScreenSubAI_Setup.iss`.
  - Tách phiên bản bộ cài mới `dist/ScreenSubAI_v1.0.0-beta.2_Setup_x64.exe` (~50,18 MB, `.NET 10 Self-Contained`), bảo toàn bản `v1.0.0-beta.1` (`50,17 MB`, SHA-256: `1F3A6AAAD72CD67CA960095EA0254CCEBB40C77ACD83A2A655FF285A0058528B`) để rollback.

### Thử Nghiệm & Chất Lượng
- Đạt **242 / 242 bài kiểm thử tự động vượt qua (100% PASS, 0 Skipped trên máy đầy đủ phần cứng/model)**: **161 Unit Tests + 81 Integration Tests**.
- Toàn bộ bài kiểm thử phần cứng dùng `[HardwareFact]` (báo `SKIPPED` kèm lý do rõ ràng nếu thiếu thiết bị/model, không bao giờ báo `PASS` giả).

---

## [1.0.0-beta.1] - 2026-11-08

### Tính Năng Mới & Đột Phá Kỹ Thuật
- **Phase 10: Windows Beta Release & Production Hardening:**
  - Hoàn thiện trình đóng gói và cài đặt tự động **Inno Setup 6** x64 (`ScreenSubAI_v1.0.0-beta.1_Setup_x64.exe`, kích thước ~8.8 MB).
  - Tích hợp **Serilog** với cơ chế ghi log xoay vòng hàng ngày (Daily Log Rotation), giới hạn 10 MB/file, lưu trữ tối đa 7 ngày tại `%LocalAppData%\ScreenSubAI\Logs`.
  - Cơ chế **Orphan Process Prevention**: Đăng ký sự kiện `AppDomain.CurrentDomain.ProcessExit` và bẫy ngoại lệ unhandled để bảo đảm 100% không để sót tiến trình worker chạy ngầm sau khi đóng ứng dụng.
  - Cơ chế **Queue Backlog Protection**: Giới hạn hàng đợi âm thanh tối đa 5 phân đoạn để loại bỏ hiện tượng tràn bộ nhớ hoặc trễ tích lũy khi video phát nhanh.

- **Phase 9G: Real Offline Neural Translation Engine:**
  - Tích hợp mô hình dịch thần kinh thực tế **Facebook M2M-100 (418M) INT8** qua CTranslate2 4.8.5.
  - Hỗ trợ dịch tự nhiên toàn diện cả **6 chiều ngôn ngữ** (Anh ↔ Trung ↔ Việt) trên câu mới hoàn toàn ngoài từ điển.
  - Bổ sung tham số `repetition_penalty=1.2` và `no_repeat_ngram_size=3` loại bỏ triệt để hiện tượng lặp từ vô tận trên các thành ngữ cổ.
  - Phân tách dứt khoát trạng thái `REAL NEURAL TRANSLATION` và `LEXICAL — LIMITED VOCABULARY`. Báo rõ `MODEL NOT INSTALLED` nếu thiếu file trọng số.

- **Phase 9G: OCR Latency Optimization (36x Speedup):**
  - Tối ưu tham số phát hiện chữ `det_limit_type="max"` và `det_limit_side_len=736` trong RapidOCR.
  - Giảm độ trễ OCR từ **2.451 ms xuống 67.6 ms** (P50), đạt tốc độ xử lý 15 FPS liên tục.

- **Phase 8: Realtime Audio Translation & Speech-to-Subtitle:**
  - Tích hợp WASAPI Loopback thu âm hệ thống trực tiếp từ Windows.
  - Tích hợp Energy Voice Activity Detection (VAD) và thư viện Whisper.net (GGML Base model).

- **Phase 7 & Trước:**
  - Hệ thống Dashboard hiện đại Dark Mode (`#0B1020`, Fluent Dark).
  - Phím tắt toàn cục **`Ctrl + Alt + Q`** cho phép vẽ chọn vùng OCR trên mọi ứng dụng.
  - Cửa sổ Subtitle Overlay trong suốt, hỗ trợ xuyên chuột (click-through), kéo thả tự do và ghim trên cùng (Always on Top).
  - Bộ nhớ đệm SQLite + SHA-256 Translation Cache (truy xuất bản dịch cũ trong 0 ms).

### Thử Nghiệm & Chất Lượng
- Đạt **50 / 50 bài kiểm thử tự động vượt qua (100% PASS)**: 28 Unit Tests + 22 Integration Tests.
- Đã thực hiện kiểm thử độ bền (Soak Test) 100 chu kỳ OCR và 50 chu kỳ Neural Translation liên tục: Mức rò rỉ bộ nhớ = 0.00 MB.

