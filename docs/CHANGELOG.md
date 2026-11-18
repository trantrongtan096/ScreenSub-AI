# Nhật Ký Thay Đổi (Changelog) - ScreenSub AI

Tất cả các thay đổi đáng chú ý của dự án **ScreenSub AI** qua từng phiên bản phát triển.

---

## [1.0.0-beta.2] - 2026-11-10

### Tính Năng Mới & Đột Phá Kỹ Thuật
- **Quick Translate Mode (Dịch tức thì trên Video):**
  - Người dùng bấm `Ctrl + Alt + Q` từ Chrome, Edge, YouTube, VLC... và quét chọn vùng phụ đề:
    - Ứng dụng **tự động lưu vùng, tự động căn vị trí Overlay ngay cạnh video và tự động bắt đầu dịch ngay lập tức**!
    - **Không cần mở lại Dashboard hoặc nhấn Start lần thứ hai**.
  - Xử lý mượt mà khi hủy chọn vùng (`ESC`): tiếp tục phiên dịch hiện tại mà không làm ngắt quãng trải nghiệm.
- **Windows Background Operation & System Tray:**
  - Hỗ trợ ứng dụng chạy nền hoàn toàn dưới khay hệ thống (System Tray).
  - Thu nhỏ xuống khay hệ thống khi đóng cửa sổ Dashboard (`X`). Phím tắt toàn cục và Overlay tiếp tục hoạt động.
  - Menu chuột phải System Tray chuyên nghiệp:
    - 🖥 Mở Dashboard
    - ✂ Chọn vùng dịch mới (`Ctrl + Alt + Q`)
    - ▶ / ⏸ Bắt đầu / Tạm dừng dịch
    - ⏹ Dừng dịch
    - ⚙ Cài đặt
    - 🚪 Thoát ứng dụng (giải phóng toàn bộ tiến trình con)
- **State Machine & Lifecycle Quản lý Phiên Dịch:**
  - Triển khai máy trạng thái hoàn chỉnh: `Idle → SelectingRegion → Starting → Translating → Paused → Stopped`.
  - Cơ chế **Session ID** và **Anti-Stale Results**: Tự động vô hiệu hóa và loại bỏ các bản dịch trễ của phiên trước hoặc khi đổi vùng.
  - Tự động căn chỉnh vị trí Overlay cạnh vùng phụ đề (`PositionNearRegion`), hỗ trợ màn hình độ phân giải cao và đa màn hình.
  - Xử lý thông báo trực quan khi video bị DRM hoặc ứng dụng bảo vệ chặn capture.

### Thử Nghiệm & Chất Lượng
- Đạt **55 / 55 bài kiểm thử tự động vượt qua (100% PASS)**: 28 Unit Tests + 27 Integration Tests.
- Đóng gói bản cài đặt chính thức: `dist/ScreenSubAI_v1.0.0-beta.1_Setup_x64.exe` (8.80 MB, SHA-256: `18F683E28A68775C32AE6C321B5DF4FBF1B8447FE769BCA15C6805F46A462C65`).

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

