# ScreenSub AI — Windows Realtime Subtitle Translator

[![Version](https://img.shields.io/badge/Release-v1.0.0--beta.2-blue.svg)](https://github.com/trantrongtan096/ScreenSub-AI)
[![Platform](https://img.shields.io/badge/Platform-Windows%20x64-brightgreen.svg)](https://microsoft.com/windows)
[![.NET](https://img.shields.io/badge/.NET-10.0%20WPF-purple.svg)](https://dotnet.microsoft.com/)
[![Tests](https://img.shields.io/badge/Tests-271%2F271%20Passing-success.svg)](docs/BETA_RC_VALIDATION_REPORT.md)
[![License](https://img.shields.io/badge/License-MIT-lightgrey.svg)](LICENSE)

![ScreenSub AI](docs/dashboard-preview.jpg)

**ScreenSub AI** là ứng dụng Windows Desktop (WPF .NET 10 x64) dịch phụ đề thời gian thực (Realtime Subtitle Translator) hỗ trợ **Tiếng Anh (English)**, **Tiếng Trung (中文 — Giản thể & Phồn thể)** và **Tiếng Việt**.

Ứng dụng hỗ trợ **3 chế độ đầu vào linh hoạt**:
1. **Screen OCR Subtitles**: Nhận diện phụ đề trên màn hình (YouTube, Netflix, VLC, media player, livestream) qua RapidOCR / PaddleOCR (`PP-OCRv4`).
2. **Audio Realtime Subtitles**: Thu âm thanh hệ thống trực tiếp qua **WASAPI Loopback** (không cần mic ngoài hay virtual audio cable), phân đoạn bằng **Energy VAD** và nhận diện giọng nói cục bộ bằng **Whisper.net (`ggml-base.bin` multilingual)**.
3. **Hybrid Mode (OCR + Audio)**: Kết hợp đồng thời cả hai luồng với bộ lọc chống trùng lặp an toàn ngữ nghĩa (Semantic-Safe Hybrid Deduplication: bảo vệ độ mới của OCR, kiểm tra cực phủ định, số lượng/thời gian, hành động đối lập `打开/关闭`, tên người/địa điểm và chuẩn hóa chữ Hán Phồn ↔ Giản).

Hệ thống dịch thuật **100% Offline** hỗ trợ **Multi-Model Offline Router**:
- **VietAI EnViT5 (`envit5_ct2_int8`, INT8 CTranslate2)**: Chuyên biệt hội thoại **Anh ↔ Việt** (kích hoạt qua profile Controlled Beta Router, tự động fallback về `M2M-100`).
- **Facebook M2M-100 (`m2m100_418m_ct2`, 418M INT8 CTranslate2)**: Mô hình đa ngữ mặc định 6 chiều (**Anh ↔ Trung ↔ Việt**) và là cơ chế rollback an toàn tức thì.
- Tùy chọn dịch trực tuyến qua **Azure AI Translator** / **Gemini** khi người dùng chủ động bật chế độ Online.

---

## 🚀 Tính năng nổi bật: Quick Translate & Chạy nền (Background Mode)

- **⚡ Dịch tức thì (Quick Translate Mode)**: Đang xem phim/YouTube trên Chrome/Edge hoặc bất kỳ cửa sổ nào, chỉ cần nhấn **`Ctrl + Alt + Q`**, kéo chọn vùng phụ đề. Ứng dụng **tự động lưu vùng, tự động định vị Overlay trên video và tự động kích hoạt nhận diện/dịch ngay lập tức** mà **không cần quay lại Dashboard bấm Start**!
- **🛎 Khay hệ thống (System Tray — Fluent Dark Mode)**: Thu nhỏ Dashboard xuống khay hệ thống khi đóng cửa sổ. Pipeline nhận diện, Overlay và phím tắt toàn cục vẫn tiếp tục chạy mượt mà dưới nền. Menu chuột phải thiết kế Fluent Dark (`#0F172A`, không dải trắng bên trái, icon vector, trạng thái động `Đã dừng / Đang dịch / Tạm dừng`, hỗ trợ DPI 100%–200% & đa màn hình): Mở Dashboard, Chọn vùng dịch mới (`Ctrl+Alt+Q`), Bắt đầu/Tạm dừng/Tiếp tục dịch, Dừng dịch, Cài đặt và Thoát ứng dụng.

---

## 📦 Tải & Cài đặt nhanh (Windows Beta x64)

Người dùng có thể cài đặt trực tiếp qua bộ cài Windows Setup chính thức (`.NET 10 Self-Contained`, hỗ trợ cài đặt tài khoản chuẩn Non-Admin `PrivilegesRequired=lowest`):

- **Bộ cài Beta RC hiện tại (`v1.0.0-beta.2`)**: `dist/ScreenSubAI_v1.0.0-beta.2_Setup_x64.exe` (~50.18 MB, tích hợp `.NET 10 Runtime`)
- **Bộ cài Rollback (`v1.0.0-beta.1`)**: `dist/ScreenSubAI_v1.0.0-beta.1_Setup_x64.exe` (`50.17 MB`, SHA-256 `1F3A6AAAD72CD67CA960095EA0254CCEBB40C77ACD83A2A655FF285A0058528B`, giữ song song để hỗ trợ hoàn nguyên nhanh)
- **Thư mục cài đặt mặc định**: `%LocalAppData%\Programs\ScreenSub AI\` (Non-Admin) hoặc `%ProgramFiles%\ScreenSub AI\`
- **Thư mục AI Models**: `%LocalAppData%\ScreenSubAI\Models\` *(được bảo toàn nguyên vẹn khi nâng cấp hoặc gỡ cài đặt; không tự động tải ngầm khi chưa có sự đồng ý của người dùng)*
- **Thư mục Logs**: `%LocalAppData%\ScreenSubAI\Logs\` *(tự động xoay vòng mỗi ngày, tối đa 10MB/file, lưu 7 ngày)*

Xem hướng dẫn cài đặt & thiết lập Python dependencies (`setup_python_env.ps1`): [docs/INSTALL.md](docs/INSTALL.md)

---

## 📚 Tài liệu chi tiết dự án

- 🧪 **[Báo cáo nghiệm thu Beta Release Candidate (RC Validation Report)](docs/BETA_RC_VALIDATION_REPORT.md)**: Số liệu kiểm thử thực tế trên phần cứng thật, phân rã độ trễ 7 giai đoạn và trạng thái từng Release Gate.
- ✅ **[Checklist QA Desktop & Clean Install](docs/BETA_DESKTOP_QA_CHECKLIST.md)**: Quy trình nghiệm thu 14 kịch bản GUI/Audio, Clean Windows VM và Soak 60 phút.
- 📖 **[Hướng dẫn sử dụng người dùng (User Guide)](docs/USER_GUIDE.md)**: Chi tiết cách dùng OCR Mode, Audio Mode, Hybrid Mode, tùy biến Overlay và phím tắt.
- 🛠️ **[Hướng dẫn cài đặt & Cấu hình môi trường (Install Guide)](docs/INSTALL.md)**: Yêu cầu hệ thống, bước cài đặt và thiết lập Python worker.
- 🧠 **[Cẩm nang mô hình AI Cục bộ (Model Guide)](docs/MODEL_GUIDE.md)**: Thông số kỹ thuật, SHA-256 checksums và bản quyền của `M2M-100`, `EnViT5`, `Whisper Base` và `RapidOCR`.
- ⚠️ **[Lỗi tồn tại & Giới hạn phiên bản Beta (Known Issues)](docs/KNOWN_ISSUES.md)**: Các giới hạn hiện tại của Controlled Beta và hướng xử lý.
- 📝 **[Nhật ký phát hành (Changelog)](docs/CHANGELOG.md)**: Lịch sử phát triển và thay đổi tới `v1.0.0-beta.2`.
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
  - Tối ưu hóa độ trễ: **~61.9 ms P50**, đạt thông lượng ~15 FPS; chuẩn hóa bicubic cho crop phụ đề nhỏ (`4 <= height < 38px -> 48px`) trong `ocr_worker.py` giúp giữ sắc nét khoảng trắng tiếng Anh mà không cần từ điển tách từ bên ngoài, bảo toàn nguyên vẹn thương hiệu CamelCase (`YouTube`, `iPhone`, `ScreenSubAI`, `PostgreSQL`), tên riêng, từ ghép, CJK và tiếng Việt.
  - Phát hiện thay đổi khung hình (Frame Change Detection) để tiết kiệm CPU khi video tĩnh.
  - Bộ lọc ổn định phụ đề ghép dòng và chống giật/nhấp nháy.
- **Audio Realtime Translation & Hybrid Mode**:
  - Thu âm thanh hệ thống qua **WASAPI Loopback Capture** và phân đoạn bằng **Energy Voice Activity Detection (VAD)**.
  - Hàng đợi giới hạn (Bounded Queue Capacity = 2, cơ chế Latest-Wins) và tự động loại bỏ đoạn âm thanh tồn đọng quá 8 giây.
  - Nhận diện giọng nói cục bộ với **Whisper.net (`ggml-base.bin`)**: tối ưu `AudioContextSize = 768` cho đoạn thoại ngắn (`<= 8s`), tự động ánh xạ ngôn ngữ từ `SpeechLanguage` / `SourceLanguage` (`~670 ms P50` khi chọn `English` hoặc `Chinese`; `~2.040 ms P50` ở chế độ tự động nhận diện đa ngôn ngữ `Auto` trên CPU `i5-10300H`).
  - **Semantic-Safe Hybrid Deduplication**: Chỉ lọc trùng khi phụ đề OCR còn mới (`<= 4,5 s`) và tương đương về mặt ngữ nghĩa; tuyệt đối không chặn nhầm khi khác cực phủ định (`不/没/not`), khác số đếm/giờ giấc (`三点/四点`), hành động đối lập (`打开/关闭`, `turn on/off`), hoặc khác tên người/địa điểm.
- **Local AI Translation (100% Offline Multi-Model Router)**:
  - **EnViT5 (`envit5_ct2_int8`)**: Chuyên biệt hội thoại Anh ↔ Việt (~170–240 ms P50 trên CPU), tự động fallback sang `M2M-100` nếu chưa cài đặt.
  - **M2M-100 418M INT8 (`m2m100_418m_ct2`)**: Hỗ trợ dịch hai chiều giữa 3 ngôn ngữ Anh ↔ Trung ↔ Việt (6 cặp ngôn ngữ, ~300–400 ms P50), quản lý bộ nhớ LRU (`MaxLoadedModels = 1` trên máy `< 8 GB RAM`, `2` trên máy `>= 8 GB RAM`).
  - Chống lặp từ với `repetition_penalty=1.2` và `no_repeat_ngram_size=3`.
  - Chuyển đổi chữ Hán Phồn thể ↔ Giản thể tốc độ cao tích hợp Win32 `LCMapStringEx`.
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
│   ├── UnitTests/                # 188 Unit tests (xUnit, FluentAssertions)
│   ├── IntegrationTests/         # 83 Integration & Hardware E2E tests (Explicit HardwareFact skip/verify)
│   ├── benchmark_30_sentences_6_directions.py # 30-sentence benchmark suite
│   ├── quality_evaluation_suite.py            # 8-category translation quality audit
│   └── stability_soak_test.py                 # 100-cycle OCR & 50-cycle Translation soak test
├── docs/                         # Toàn bộ tài liệu kỹ thuật & tài liệu người dùng
├── ScreenSubAI.slnx              # Solution file (.NET 10)
└── README.md
```

---

## Yêu cầu môi trường

- **Hệ điều hành**: Windows 10 (version 1903 / Build 19041 trở lên) hoặc Windows 11 x64.
- **.NET Runtime**: Không bắt buộc cài trước nếu dùng bộ cài Self-Contained (`ScreenSubAI_v1.0.0-beta.2_Setup_x64.exe`); cần .NET 10.0 SDK nếu build từ source.
- **Microsoft VC++ Redistributable**: Visual C++ 2015–2022 Redistributable x64 (`vcruntime140.dll`, cần cho ONNX Runtime, Whisper.net và CTranslate2).
- **Python**: Python 3.10 – 3.12 x64 (hoặc thư mục `python\` portable đặt cạnh `.exe`). Chạy `setup_python_env.ps1` để kiểm tra/cài đặt các gói `rapidocr_onnxruntime`, `onnxruntime`, `ctranslate2`, `sentencepiece`, `tokenizers`, `transformers` khi có sự đồng ý của người dùng.

---

## Giới hạn hiện tại của bản Controlled Beta (`v1.0.0-beta.2`)

1. **OCR Tiếng Trung Phồn thể (`zh-Hant`):** Bộ từ điển `PP-OCRv4` mặc định tập trung cho chữ Giản thể và tiếng Anh; các ký tự Phồn thể nhiều nét ngoài từ điển (OOV) có thể bị nhận diện thiếu nét.
2. **Độ trễ chế độ `SourceLanguage = Auto` trên Audio:** Khi để tự động nhận diện ngôn ngữ nói (`Auto`), Whisper cần chạy thêm lượt nhận diện ngôn ngữ (~2,0 s trên CPU 4 nhân) so với khi chọn trực tiếp `English` hoặc `Chinese` (~0,67 s).
3. **Các hạng mục nghiệm thu còn `PENDING` trước khi mở Wide Beta:** Kiểm thử trên máy ảo Windows 10/11 hoàn toàn sạch (chưa cài sẵn Python/VC++), kiểm thử tương tác đa màn hình khác DPI / game Borderless Fullscreen / rút mạng vật lý, và đánh giá bộ OCR Phồn thể opt-in (`PP-OCRv3 CHT` / `PP-OCRv5`). *(Soak Test liên tục 60 phút trên `ScreenSub.Desktop.exe` đã hoàn tất `PASS`, `0` crash / `0` leak / `0` worker restart).*

---

## Hướng dẫn Build từ Source Code

### 1. Build Solution

```powershell
# Restore và build toàn bộ solution chế độ Release
dotnet build ScreenSubAI.slnx -c Release
```

### 2. Chạy kiểm thử tự động (271/271 Tests Passed)

```powershell
dotnet test ScreenSubAI.slnx -c Release
```

Kết quả trên máy đầy đủ môi trường phần cứng & mô hình cục bộ:
```text
Passed! - UnitTests.dll (188 tests, 0 failed, 0 skipped)
Passed! - IntegrationTests.dll (83 tests, 0 failed, 0 skipped)
Total: 271 passed, 0 failed, 0 skipped.
```
*(Các bài kiểm thử phần cứng/model dùng `[HardwareFact]` sẽ báo `SKIPPED` kèm lý do rõ ràng nếu máy chưa cài mô hình hoặc không có thiết bị âm thanh, không bao giờ báo `PASS` giả).*

### 3. Đóng gói bộ cài đặt (Build Installer)

Yêu cầu đã cài đặt **Inno Setup 6**:
```powershell
powershell -ExecutionPolicy Bypass -File installer/build_installer.ps1 -Version "1.0.0-beta.2"
```
File cài đặt sẽ được tạo tự động tại thư mục `dist/ScreenSubAI_v1.0.0-beta.2_Setup_x64.exe` (không ghi đè bản `v1.0.0-beta.1`).

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


