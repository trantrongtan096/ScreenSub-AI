# ScreenSub AI — Windows Realtime Subtitle Translator

[![Version](https://img.shields.io/badge/Release-v1.0.0--beta.1-blue.svg)](https://github.com)
[![Platform](https://img.shields.io/badge/Platform-Windows%20x64-brightgreen.svg)](https://microsoft.com/windows)
[![.NET](https://img.shields.io/badge/.NET-10.0%20WPF-purple.svg)](https://dotnet.microsoft.com/)
[![Tests](https://img.shields.io/badge/Tests-55%2F55%20Passing-success.svg)](docs/TEST_REPORT.md)
[![License](https://img.shields.io/badge/License-MIT-lightgrey.svg)](LICENSE)

![ScreenSub AI](docs/dashboard-preview.jpg)

**ScreenSub AI** là ứng dụng Windows Desktop (WPF .NET 10 x64) dịch phụ đề thời gian thực (Realtime Subtitle Translator) hỗ trợ **Tiếng Anh (English)**, **Tiếng Trung (中文)** và **Tiếng Việt**. 

Ứng dụng hỗ trợ cả **2 nguồn đầu vào độc lập**:
1. **OCR Subtitles**: Nhận diện phụ đề trên màn hình (YouTube, Netflix, VLC, games, livestream) qua RapidOCR / PaddleOCR.
2. **Audio Subtitles**: Thu âm thanh hệ thống trực tiếp qua **WASAPI Loopback** (không cần mic ngoài hay virtual audio cable) và nhận diện giọng nói bằng **Whisper AI** cục bộ.

Tất cả được dịch thuật bằng mô hình **Local Neural AI (M2M-100 418M INT8 qua CTranslate2)** hoạt động **100% Offline**, 0 rò rỉ dữ liệu lên Cloud, hoặc tùy chọn qua **Azure AI Translator**, hiển thị trực tiếp lên cửa sổ phụ đề nổi (Transparent Click-through Overlay).

---

## 🚀 Tính năng nổi bật: Quick Translate & Chạy nền (Background Mode)

- **⚡ Dịch tức thì (Quick Translate Mode)**: Đang xem phim/YouTube trên Chrome hoặc bất kỳ cửa sổ nào, chỉ cần nhấn **`Ctrl + Alt + Q`**, kéo chọn vùng phụ đề. Ứng dụng **tự động lưu vùng, tự động định vị Overlay trên video và tự động kích hoạt nhận diện/dịch ngay lập tức** mà **không cần quay lại Dashboard bấm Start**!
- **🛎 Khay hệ thống (System Tray)**: Thu nhỏ Dashboard xuống khay hệ thống khi đóng cửa sổ. Pipeline nhận diện, Overlay và phím tắt toàn cục vẫn tiếp tục chạy mượt mà dưới nền. Menu chuột phải đầy đủ: Mở Dashboard, Chọn vùng mới, Bắt đầu/Tạm dừng, Dừng, Cài đặt và Thoát ứng dụng.

---

## 📦 Tải & Cài đặt nhanh (Windows Beta x64)

Người dùng có thể cài đặt trực tiếp qua bộ cài Windows Setup chính thức:

- **Bộ cài Setup**: [`dist/ScreenSubAI_v1.0.0-beta.1_Setup_x64.exe`](dist/ScreenSubAI_v1.0.0-beta.1_Setup_x64.exe)
- **Kích thước file**: **8.80 MB** (9,226,106 bytes)
- **Mã băm SHA-256**: `18F683E28A68775C32AE6C321B5DF4FBF1B8447FE769BCA15C6805F46A462C65`
- **Thư mục cài đặt**: `%ProgramFiles%\ScreenSub AI\`
- **Thư mục AI Models**: `%LocalAppData%\ScreenSubAI\Models\` *(được bảo toàn nguyên vẹn khi gỡ cài đặt)*
- **Thư mục Logs**: `%LocalAppData%\ScreenSubAI\Logs\` *(tự động xoay vòng mỗi ngày, tối đa 10MB/file, lưu 7 ngày)*

Xem hướng dẫn chi tiết từng bước: [docs/INSTALL.md](docs/INSTALL.md)

---

## 📚 Tài liệu chi tiết dự án

- 📖 **[Hướng dẫn sử dụng người dùng (User Guide)](docs/USER_GUIDE.md)**: Chi tiết cách dùng OCR Mode, Audio Mode, tùy biến Overlay và phím tắt.
- 🛠️ **[Hướng dẫn cài đặt & Cấu hình môi trường (Install Guide)](docs/INSTALL.md)**: Yêu cầu hệ thống, bước cài đặt và thiết lập Python worker.
- 🧠 **[Cẩm nang mô hình AI Cục bộ (Model Guide)](docs/MODEL_GUIDE.md)**: Thông số kỹ thuật, cấu hình M2M-100 418M INT8, Whisper và RapidOCR.
- 🧪 **[Báo cáo kiểm thử & Đánh giá chất lượng (Test Report)](docs/TEST_REPORT.md)**: Chi tiết 50 automated tests, bài kiểm tra tải 100 chu kỳ OCR, 50 chu kỳ Translation và 8 danh mục ngữ nghĩa.
- ⚠️ **[Lỗi tồn tại & Giải pháp tạm thời (Known Issues)](docs/KNOWN_ISSUES.md)**: Các trường hợp ngoại lệ, giới hạn ngữ cảnh của mô hình và cách xử lý.
- 📝 **[Nhật ký phát hành (Changelog)](docs/CHANGELOG.md)**: Lịch sử phát triển và thay đổi từ Phase 1 đến Phase 10 Beta.
- 🏗️ **[Tài liệu kiến trúc hệ thống (Architecture)](docs/ARCHITECTURE.md)**: Sơ đồ kiến trúc tầng, Clean Architecture MVVM và xử lý đa luồng.

---

## Tính năng chính

- **Giao diện Dashboard Windows Hiện đại**: Thiết kế Fluent Dark Mode chuyên nghiệp (`#0B1020`), hiển thị trực quan thông số OCR, Whisper ASR, công cụ dịch và độ trễ thực tế (ms).
- **Chọn vùng màn hình (Snipping Tool Style)**:
  - Phím tắt toàn cục **`Ctrl + Alt + Q`** (mặc định, tùy chỉnh được trong Cài đặt).
  - Lớp phủ mờ toàn màn hình, con trỏ crosshair, kéo thả hình chữ nhật viền neon cyan với kích thước `W × H px` tức thời.
  - Nhấn `ESC` để hủy.
- **2 Chế độ theo dõi vùng dịch**:
  - **Vùng cố định (Fixed Region)**: Cố định tại tọa độ màn hình.
  - **Theo cửa sổ (Follow Window)**: Tự động bám theo cửa sổ ứng dụng khi di chuyển/đổi kích thước; tự dừng nếu thu nhỏ.
- **Hỗ trợ đa màn hình & DPI Scaling**:
  - DPI Awareness `PerMonitorV2` (100%, 125%, 150%) chuẩn xác theo từng pixel vật lý.
  - Hoạt động mượt mà trên màn hình phụ có tọa độ âm.
- **Realtime OCR Pipeline (RapidOCR / PaddleOCR)**:
  - Tối ưu hóa độ trễ: **~61.9 ms P50**, đạt thông lượng ~15 FPS.
  - Phát hiện thay đổi khung hình (Frame Change Detection) để tiết kiệm CPU khi video tĩnh.
  - Bộ lọc ổn định phụ đề ghép dòng và chống giật/nhấp nháy.
- **Audio Realtime Translation & Speech-to-Subtitle**:
  - Thu âm thanh hệ thống qua **WASAPI Loopback Capture**.
  - Phân đoạn giọng nói thời gian thực bằng **Voice Activity Detection (VAD)**.
  - Hàng đợi bảo vệ chống trễ (Queue Backlog Protection): Tối đa 5 phân đoạn, cơ chế Latest-Wins.
  - Nhận diện giọng nói cục bộ với **Whisper.net**.
- **Local AI Translation (100% Offline)**:
  - Mô hình Seq2Seq **M2M-100 418M INT8** chạy trên CTranslate2 engine.
  - Hỗ trợ dịch hai chiều giữa 3 ngôn ngữ: Anh ↔ Trung ↔ Việt (6 cặp ngôn ngữ).
  - Độ trễ dịch thuật cục bộ cực nhanh: **~291 ms P50**.
  - Chống lặp từ vô hạn với `repetition_penalty=1.2` và `no_repeat_ngram_size=3`.
- **Cửa sổ phụ đề nổi (Subtitle Overlay Window)**:
  - Trong suốt, borderless, luôn hiển thị trên cùng (Always on Top).
  - **Click-through (Xuyên chuột)**: Bấm chuột xuyên qua phụ đề xuống video bên dưới.
  - **Chống nhận diện ngược (Capture Exclusion)**: Sử dụng `WDA_EXCLUDEFROMCAPTURE` ngăn ngừa triệt để vòng lặp OCR vô hạn.
  - 3 Chế độ hiển thị: **Đơn ngữ (Single)**, **Song ngữ (Dual)**, **Tam ngữ (Triple)**.
- **Độ tin cậy & An toàn vận hành**:
  - Khóa API được mã hóa an toàn bằng **Windows DPAPI** (`ProtectedData`).
  - Dọn dẹp tiến trình con tự động: Không bao giờ để lại tiến trình Python worker mồ côi (`ProcessExit` hook).
  - Hệ thống ghi log Serilog chuyên nghiệp với giới hạn dung lượng và xoay vòng theo ngày.

---

## Cấu trúc thư mục dự án

```text
ScreenSubAI/
├── src/
│   ├── ScreenSub.Core/           # Domain models, enums, interfaces, Hardware & Model metadata
│   ├── ScreenSub.Capture/        # Screen capture, BitBlt, DPI scaling, window tracking
│   ├── ScreenSub.Audio/          # WASAPI loopback capture, PCM buffer, VAD integration
│   ├── ScreenSub.Speech/         # Whisper.net speech recognition engine
│   ├── ScreenSub.Ocr/            # RapidOCR/PaddleOCR worker client, stability filter
│   ├── ScreenSub.Translation/    # Local M2M-100 CTranslate2, Azure Translator, Resolver, Cache
│   ├── ScreenSub.Overlay/        # Transparent overlay window, click-through, capture exclusion
│   ├── ScreenSub.Infrastructure/ # Hardware detection, LocalModelManager, SQLite, DPAPI, Serilog
│   └── ScreenSub.Desktop/        # WPF MVVM Presentation layer, Dashboard UI, Global Hotkeys
├── workers/
│   ├── paddle-ocr/               # Python worker script (ocr_worker.py) & requirements.txt
│   └── translation/              # Local Neural M2M-100 worker (translation_worker.py)
├── installer/                    # Inno Setup 6 script (ScreenSubAI_Setup.iss) & build script
├── dist/                         # Setup executable distribution binaries
├── publish/                      # Self-contained Release binaries for packaging
├── test_assets/                  # Sample audio WAV & test images for automated testing
├── tests/
│   ├── UnitTests/                # 28 Unit tests (xUnit, FluentAssertions)
│   ├── IntegrationTests/         # 22 Integration tests (End-to-end, Audio, Offline, DPAPI)
│   ├── benchmark_30_sentences_6_directions.py # 30-sentence benchmark suite
│   ├── quality_evaluation_suite.py            # 8-category translation quality audit
│   └── stability_soak_test.py                 # 100-cycle OCR & 50-cycle Translation soak test
├── docs/                         # Toàn bộ tài liệu kỹ thuật & tài liệu người dùng
├── ScreenSubAI.slnx              # Solution file (.NET 10)
└── README.md
```

---

## Yêu cầu môi trường

- **Hệ điều hành**: Windows 10 (version 1903 trở lên) hoặc Windows 11 x64.
- **.NET Runtime**: .NET 10.0 Desktop Runtime x64 (được tự động kiểm tra khi cài đặt).
- **Python**: Python 3.10+ x64 (để chạy worker OCR và mô hình dịch cục bộ).

---

## Hướng dẫn Build từ Source Code

### 1. Build Solution

```powershell
# Restore và build toàn bộ solution chế độ Release
dotnet build ScreenSubAI.slnx -c Release
```

### 2. Chạy kiểm thử tự động (50/50 Tests Passed)

```powershell
dotnet test ScreenSubAI.slnx
```

Kết quả:
```text
Passed! - UnitTests.dll (28 tests)
Passed! - IntegrationTests.dll (22 tests)
Total: 50 passed, 0 failed (100% Passing).
```

### 3. Đóng gói bộ cài đặt (Build Installer)

Yêu cầu đã cài đặt **Inno Setup 6**:
```powershell
powershell -ExecutionPolicy Bypass -File installer/build_installer.ps1
```
File cài đặt sẽ được tạo tự động tại thư mục `dist/ScreenSubAI_v1.0.0-beta.1_Setup_x64.exe`.

### 4. Khởi chạy ứng dụng trực tiếp

```powershell
dotnet run --project src/ScreenSub.Desktop/ScreenSub.Desktop.csproj
```

---

## Phím tắt mặc định & Thao tác nhanh

| Thao tác | Phím tắt / Hành động | Mô tả |
|---|---|---|
| **Chọn vùng màn hình** | `Ctrl + Alt + Q` *(tùy chỉnh được)* | Kích hoạt công cụ chọn vùng kiểu Snipping Tool |
| **Hủy chọn vùng** | `ESC` | Thoát chế độ chọn vùng màn hình |
| **Bắt đầu dịch** | Nút `▶ Bắt đầu dịch` | Khởi động pipeline capture, OCR/Audio và dịch thuật |
| **Tạm dừng / Dừng** | Nút `⏸` / `⏹` | Tạm dừng hoặc dừng toàn bộ pipeline |
| **Chỉnh vị trí Overlay** | Nút `✏ Chỉnh sửa vị trí Overlay` | Tắt click-through, cho phép kéo thả/đổi kích thước cửa sổ phụ đề |

---

## Giấy phép
Dự án được phân phối dưới giấy phép [MIT](LICENSE). Mọi mô hình AI đi kèm (M2M-100, Whisper, RapidOCR) đều tuân thủ giấy phép nguồn mở tương ứng.


