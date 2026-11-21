# BÁO CÁO NGHIỆM THU BETA RELEASE CANDIDATE — SCREENSUB AI

**Ngày cập nhật:** 2026-11-20
**Phiên bản đóng gói:** `ScreenSub AI v1.0.0-beta.2` (giữ song song `v1.0.0-beta.1` để rollback)
**Trạng thái phát hành Beta:** **`CONDITIONAL` (Controlled Beta — Đã nghiệm thu xong Soak Test liên tục 60 phút trên `ScreenSub.Desktop.exe` + Real-World Audio EN & ZH + Hybrid Mode + Concurrency CPU Contention + Upgrade `beta.1 -> beta.2`; còn chờ Clean Windows VM & Multi-Monitor/DPI/Borderless Game)**

---

## 1. Các lỗi thực sự phát hiện và đã khắc phục triệt để

1. **Lỗi P0 Installer crash ngay khi khởi chạy (`ScreenSubAI_Setup.iss`):**
   - Khi chạy thử trực tiếp `ScreenSubAI_v1.0.0-beta.1_Setup_x64.exe`, bộ cài văng ngoại lệ fatal ngay tại `InitializeSetup()` (`Internal error: An attempt was made to expand the "app" constant before it was initialized`) do hàm `HasCompatiblePython()` gọi `{app}\python\python.exe` trước khi trang chọn thư mục chạy.
   - Đã sửa `HasCompatiblePython(CheckAppDir: Boolean)` truyền `False` trong `InitializeSetup()` tại [ScreenSubAI_Setup.iss](../installer/ScreenSubAI_Setup.iss), build lại bộ cài và kiểm chứng cài đặt/chạy app/upgrade/uninstall thật **exit code = 0**.
2. **Sai checksum SHA-256 của `whisper-base-ggml` trong `LocalModelManager.cs`:**
   - Khi tải `ggml-base.bin` trực tiếp từ `https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin` (`147,951,465 bytes`), hash LFS chính thức là `60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe` trong khi registry cũ ghi hash stale (`a048a977...`). Đã cập nhật đúng `SizeBytes = 147_951_465L` và `Sha256Checksum` trong [LocalModelManager.cs](../src/ScreenSub.Infrastructure/LocalModelManager.cs).
3. **Lỗi thoát scope thư mục `ModelStorageDirectory` trong `WhisperModelManager.GetModelPath`:**
   - Sau khi cài đặt `ggml-base.bin` vào `%LocalAppData%\ScreenSubAI\Models\whisper\`, phát hiện nếu người dùng hoặc cấu hình đặt `ModelStorageDirectory` sang một thư mục riêng chưa có file model thì `GetModelPath` vẫn rơi xuống tìm thấy file ở `%LocalAppData%` thay vì khóa phạm vi theo `ModelStorageDirectory`. Đã sửa cô lập scope tại [WhisperModelManager.cs](../src/ScreenSub.Speech/Whisper/WhisperModelManager.cs).
4. **Lệch đường dẫn & shadowing file hỏng trong `WhisperModelManager` / Race & CTS leak trong `SubtitleCoordinator`:**
   - Đồng bộ `%LocalAppData%\ScreenSubAI\Models\whisper\`, kiểm tra `IsValidModelFile` (`> 1 MB`), `JobCts` ownership + `RetireSessionCts` (PASS stress `25` chu kỳ Stop/Restart liên tiếp).

5. **Lỗi P1 Hybrid Deduplication nguy cơ suppress nhầm câu trái nghĩa / khác thời gian / khác thực thể (`SubtitleCoordinator` + `SubtitleTextProcessor`):**
   - Trước đây, `SubtitleCoordinator` so khớp `ocrCanon` và `speechCanon` chỉ bằng `.Contains(...)` hoặc `CalculateSimilarity >= 0.78`, đồng thời dùng chung `_lastTextSeenTime` với luồng Speech. Trên các câu dài có chung khung ngữ cảnh (`"请在离开办公室之前打开所有的电脑电源。"` vs `"请在离开办公室之前关闭所有的电脑电源。"`, similarity `88,2%`), câu trái nghĩa (`打开/关闭`), khác giờ (`三点/四点`), thêm/bớt phủ định (`去/不去`), khác tên riêng/địa danh (`北京/南京`, `Smith/Jones`) hoặc OCR cũ đã hết hạn vẫn bị chặn nhầm (`10/10 = 100%` False-Positive trên bộ 10 cặp đối kháng).
   - Đã tách riêng `_lastOcrSeenTimeUtc` (giới hạn độ mới OCR `<= 4,5 s`) và cài đặt `SubtitleTextProcessor.ShouldSuppressHybridSpeechDuplicate(...)` / `AreSubtitlesSemanticallyEquivalent(...)` kết hợp **Hard Veto** cho số/thời gian/số lượng (`ExtractCanonicalNumbers`), cực tính phủ định (`CountNegationMarkers`), hình vị tương phản CJK/từ trái nghĩa EN và khoảng cách từ khác biệt. Kết quả: **False-Positive giảm từ `100%` xuống `0,0%` (`0/10`)**, **False-Negative giữ `0,0%` (`0/6`)** cho câu đồng nghĩa Giản/Phồn và lỗi đồng âm 1 từ ASR.
6. **Cơ chế kiểm thử phần cứng minh bạch (`HardwareFactAttribute`):**
   - Thay thế toàn bộ `10` chỗ `if (...) return;` âm thầm trong `RealWorldAudioAndConcurrencyTests.cs`, `LiveHardwareE2ETests.cs` và `WhisperAuditBenchmarkTests.cs` bằng `[HardwareFact(...)]` + `[Trait("Category", "HardwareE2E")]`. Khi thiếu Edge, Whisper model, Translation model hoặc thiết bị WASAPI, xUnit báo **`SKIPPED`** kèm lý do rõ ràng thay vì báo `PASS` giả; khi đủ điều kiện, test bắt buộc đi qua các `Should()` assertions.
7. **Lỗi chuyển chế độ động `ScreenOcr -> AudioRealtime / Hybrid` khi đang chạy (`SubtitleCoordinator.Start()`):**
   - Phát hiện trong quá trình kiểm toán chu kỳ luân phiên chế độ cho Soak Test 60 phút: nếu phiên bắt đầu ở `ScreenOcr` (`_speechWorkerTask == null`) rồi người dùng bấm chuyển sang `AudioRealtime` hoặc `Hybrid` qua `SetTranslationMode()`, hàm gọi `Pause()` rồi `Start()`; nhánh `if (State == SystemState.Paused)` chỉ khởi động `_audioCaptureService` và `return` sớm mà chưa khởi tạo `_speechWorkerTask` đọc từ `_speechChannel`.
   - Đã vá trực tiếp trong nhánh resume của `SubtitleCoordinator.Start()` tại [SubtitleCoordinator.cs](../src/ScreenSub.Desktop/Services/SubtitleCoordinator.cs) để tự động khởi tạo `_speechService` và `_speechWorkerTask` nếu chưa chạy, và đã kiểm chứng qua `30` lần chuyển chế độ liên tục trong phiên Soak Test 60 phút (`251` lượt nhận diện Whisper thành công, `0` lỗi).
8. **Tái thiết kế System Tray Context Menu Fluent Dark Mode (`Theme.xaml` & `SystemTrayManager.cs`):**
   - Loại bỏ hoàn toàn dải trắng bên trái của WPF `ContextMenu` bằng custom `ControlTemplate` (`TrayContextMenuStyle`, `TrayMenuItemStyle`, `TraySeparatorStyle`), header tùy chỉnh không dùng `MenuItem` disabled với badge trạng thái động (`Đã dừng / Đang dịch / Tạm dừng`), vector icons 17–18px căn thẳng hàng, phím tắt `Ctrl+Alt+Q` căn phải và tự động định vị chống cắt mép cho DPI `100%–200%` & đa màn hình (kiểm chứng qua test tự động trong [DesktopAndAudioHardwareTests.cs](../tests/IntegrationTests/DesktopAndAudioHardwareTests.cs)).
9. **Lỗi OCR tiếng Anh mất khoảng trắng & Quyết định A/B/C Ablation (`outit maybeIong to a differenttownship.`):**
   - **Root cause:** Với phụ đề cỡ nhỏ (`14–22px`) trong vùng chụp cao `>= 64px`, `ocr_worker.py` không phóng đại crop trước `ch_PP-OCRv4_rec`, khiến nội suy bilinear làm mờ khe khoảng trắng hẹp và CTC nuốt khoảng trắng; đồng thời regex tách CamelCase mù `(?<=[a-z])(?=[A-Z])` cắt hỏng thương hiệu (`YouTube`, `iPhone`, `ScreenSubAI`, `PostgreSQL`).
   - **Thực nghiệm A/B/C trên 80 ảnh độc lập chưa từng dùng để tinh chỉnh (`52 EN + 10 ZHS + 8 ZHT + 10 VI`):**
     - `Config A` (OCR cũ + post cũ): EN `Exact = 21/52 (40,4%)`, `WB F1 = 82,81%`, `MissSp = 112`, `InsSp = 13`, `WER = 38,71%`, `P50 = 94,3 ms`.
     - `Config B` (RapidOCR `_enhance_small_crops_for_rec` `< 38px` bicubic, không dùng `EnglishWordSegmenter`): EN **`Exact = 50/52 (96,2%)`**, **`WB F1 = 99,64%`**, **`MissSp = 2` (`-98,2%`)**, **`InsSp = 1`**, **`WER = 0,65%`**, **`P50 = 87,1 ms`**; cải thiện đồng thời cả ZHS (`Exact 80% -> 90%`, `P50 63,1 -> 49,5 ms`), ZHT (`CER 14,38% -> 12,42%`) và VI (`WB F1 70,54% -> 81,15%`, `WER 87,88% -> 81,06%`).
     - `Config C` (`Config B` + `EnglishWordSegmenter` hardened): **kém hơn `Config B` trên tập chưa tinh chỉnh** (`1` giúp vs `4` làm hỏng: `strait -> str a it`, `conductor -> conduct or`, `toan -> to an`, `soat -> so at`; EN `Exact = 49/52 = 94,2%`, `WER = 1,29%`, `+1,08 MB` heap, `+32 ms` cold init) và phụ thuộc từ điển `english_lexicon.txt` lọc qua Windows `ISpellChecker` chưa đủ bằng chứng cấp phép tái phân phối.
   - **Quyết định phát hành `v1.0.0-beta.2`:** Chọn **`Config B`** — giữ nguyên bản sửa bicubic small-crop trong [ocr_worker.py](../workers/paddle-ocr/ocr_worker.py) và chuẩn hóa dấu câu/CJK an toàn không từ điển trong [SubtitleTextProcessor.cs](../src/ScreenSub.Core/Processing/SubtitleTextProcessor.cs); vô hiệu hóa `EnglishWordSegmenter` và **gỡ bỏ hoàn toàn `english_lexicon.txt` khỏi [ScreenSub.Core.csproj](../src/ScreenSub.Core/ScreenSub.Core.csproj) & bộ cài** (xác minh tự động qua `ScreenSubCoreAssembly_ShouldNotEmbedUnlicensedLexiconResource`).

---

## 2. Danh sách file cập nhật gần nhất

- [SubtitleTextProcessor.cs](../src/ScreenSub.Core/Processing/SubtitleTextProcessor.cs) & [SubtitleCoordinator.cs](../src/ScreenSub.Desktop/Services/SubtitleCoordinator.cs) — thêm `_isOcrCurrentlyPresentOnScreen`, `_lastOcrSeenTimeUtc` (`<= 4,5 s`) và bộ lọc ngữ nghĩa an toàn `ShouldSuppressHybridSpeechDuplicate` / `AreSubtitlesSemanticallyEquivalent` (`0% FP / 0% FN`).
- [HardwareFactAttribute.cs](../tests/IntegrationTests/HardwareFactAttribute.cs) — thuộc tính `[HardwareFact]` kế thừa `FactAttribute` đảm bảo không bao giờ báo `PASS` giả khi thiếu phần cứng/model.
- [ocr_worker.py](../workers/paddle-ocr/ocr_worker.py) & [SubtitleTextProcessor.cs](../src/ScreenSub.Core/Processing/SubtitleTextProcessor.cs) — chuẩn hóa bicubic cho crop `< 38px`, gỡ regex tách CamelCase mù, giữ chuẩn hóa dấu câu/CJK an toàn không cần từ điển ngoài (`0` false splits, không nhúng `english_lexicon.txt`).
- [Theme.xaml](../src/ScreenSub.Desktop/Themes/Theme.xaml), [SystemTrayManager.cs](../src/ScreenSub.Desktop/Services/SystemTrayManager.cs) & [DesktopAndAudioHardwareTests.cs](../tests/IntegrationTests/DesktopAndAudioHardwareTests.cs) — tái thiết kế System Tray Context Menu Fluent Dark Mode không còn dải trắng bên trái.
- [SubtitleTextProcessorTests.cs](../tests/UnitTests/SubtitleTextProcessorTests.cs) & [EndToEndPipelineTests.cs](../tests/IntegrationTests/EndToEndPipelineTests.cs) — bổ sung `27` unit test cases (chuẩn hóa CJK/dấu câu, bộ chống tách sai `therapist/nowhere/notebook/football/database/strait/conductor/YouTube/iPhone/ScreenSubAI/PostgreSQL`, tên riêng/từ ghép/tiếng Việt mất dấu và `ScreenSubCoreAssembly_ShouldNotEmbedUnlicensedLexiconResource`) và `TestK` thực tế trên ảnh phụ đề EN/ZH (tổng `188` Unit Tests + `83` Integration Tests = `271` tests).
- [LocalModelManager.cs](../src/ScreenSub.Infrastructure/LocalModelManager.cs) — cập nhật chính xác `SizeBytes` và `Sha256Checksum` của `whisper-base-ggml` (`ggerganov/whisper.cpp`).
- [WhisperModelManager.cs](../src/ScreenSub.Speech/Whisper/WhisperModelManager.cs) & [WhisperSpeechRecognitionService.cs](../src/ScreenSub.Speech/Whisper/WhisperSpeechRecognitionService.cs) — cô lập `ModelStorageDirectory`, `WithGreedySamplingStrategy(bestOf=1)`, `WithNoContext()`, adaptive `WithAudioContextSize(768)` cho đoạn VAD `<= 8s`.
- [EnergyVoiceActivityDetector.cs](../src/ScreenSub.Audio/VAD/EnergyVoiceActivityDetector.cs) & [SpeechSegment.cs](../src/ScreenSub.Core/Models/SpeechSegment.cs) — sửa rollover `_speechStartTimeMs`/`_lastSpeechTimeMs` khi thoại vượt `MaxSpeechDurationMs`, thêm `CreatedAtUtc`.
- [FrameChangeDetector.cs](../src/ScreenSub.Capture/FrameChangeDetector.cs) — thêm SHA-256 fast-path cho frame giống nhau byte-by-byte để tránh decode WIC thừa gây reset retry bound.
- [LocalChineseConverter.cs](../src/ScreenSub.Translation/LocalChineseConverter.cs) — tích hợp thêm Win32 `LCMapStringEx` (`LCMAP_SIMPLIFIED_CHINESE` / `LCMAP_TRADITIONAL_CHINESE`) để chuyển đổi đầy đủ bộ chữ Hán Phồn ↔ Giản offline.
- [ScreenSubAI_Setup.iss](../installer/ScreenSubAI_Setup.iss), [build_installer.ps1](../installer/build_installer.ps1) & [ScreenSub.Desktop.csproj](../src/ScreenSub.Desktop/ScreenSub.Desktop.csproj) — nâng phiên bản lên `v1.0.0-beta.2` (không ghi đè `beta.1`).
- [LiveHardwareE2ETests.cs](../tests/IntegrationTests/LiveHardwareE2ETests.cs), [WhisperAuditBenchmarkTests.cs](../tests/IntegrationTests/WhisperAuditBenchmarkTests.cs), [RealWorldAudioAndConcurrencyTests.cs](../tests/IntegrationTests/RealWorldAudioAndConcurrencyTests.cs) & [FrameChangeAndRetrySafetyTests.cs](../tests/IntegrationTests/FrameChangeAndRetrySafetyTests.cs).
- [README.md](../README.md), [INSTALL.md](INSTALL.md), [CHANGELOG.md](CHANGELOG.md), [KNOWN_ISSUES.md](KNOWN_ISSUES.md), [MODEL_GUIDE.md](MODEL_GUIDE.md), [BETA_DESKTOP_QA_CHECKLIST.md](BETA_DESKTOP_QA_CHECKLIST.md) & [BETA_RC_VALIDATION_REPORT.md](BETA_RC_VALIDATION_REPORT.md).
- Bộ cài phát hành song song trong `dist/`:
  - **Bản mới (`v1.0.0-beta.2`, license-clean, không chứa `EnglishWordSegmenter` hay `english_lexicon.txt`, kèm bản vá Maximize/DPI):** `dist/ScreenSubAI_v1.0.0-beta.2_Setup_x64.exe` (`50.18 MB` / `52.622.089 bytes`, SHA-256 `9EEF3FE1179733AF8CFBB1005E7EA8B29C9D472F7026709CCB031834B2BB846B`).
  - **Bản rollback (`v1.0.0-beta.1`):** `dist/ScreenSubAI_v1.0.0-beta.1_Setup_x64.exe` (`50.17 MB` / `52.607.228 bytes`, SHA-256 `1F3A6AAAD72CD67CA960095EA0254CCEBB40C77ACD83A2A655FF285A0058528B`).

---

## 3. Kết quả kiểm thử thực tế tự thực hiện trên máy

### A. Build & Toàn bộ Regression Suite (`Release`, chạy sau quyết định Config B `2026-11-20`)
| Bộ kiểm thử | Kết quả | Thời gian |
|---|---|---:|
| **Release Build (`ScreenSubAI.slnx`)** | **PASS (`0 Warning, 0 Error`)** | `21,01 s` |
| **UnitTests** *(gồm False Correction Safety + kiểm tra không nhúng `english_lexicon.txt`)* | **`188/188 PASSED`** (`0 Failed, 0 Skipped`) | `20 s` |
| **IntegrationTests** (Real Workers + Live Edge + Real EN/ZH Neural Audio + OCR Word Spacing `TestK` + Fluent Dark System Tray + Hybrid Semantic Safety & OcrFreshnessLifecycle + Concurrency + Stress 25x) | **`83/83 PASSED`** (`0 Failed, 0 Skipped`) | `15 m 44 s` |

### B. Thực nghiệm Whisper.net (`ggml-base.bin`) -> EnViT5 -> Overlay & Hybrid Mode (`LiveHardwareE2ETests`)
- **Nguồn model:** `C:\Users\Tan\AppData\Local\ScreenSubAI\Models\whisper\ggml-base.bin` (`147,951,465 bytes`, SHA-256 `60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe`). `LocalModelManager.VerifyModelIntegrityAsync("whisper-base-ggml")` → **PASS (`Installed`)**.
- **Thời gian nạp model:** `LoadMs = 178 ms`.
- **Mức tiêu thụ RAM (`.NET` test host process):**
  - Trước khi nạp Whisper: `WS = 112 MB`, `Private = 47 MB`.
  - Sau `InitializeAsync()`: `WS = 267 MB` (**`+155 MB`**), `Private = 197 MB` (**`+150 MB`**).
  - Đỉnh suy luận đồng thời Whisper + EnViT5: `Peak WS = 297 MB`, `Peak Private = 213 MB`.
- **Pipeline AudioRealtime (`6` câu thoại tiếng Anh qua loa phần cứng -> WASAPI -> EnergyVAD -> Whisper (`auto` & `en`) -> EnViT5 -> Overlay + `1` câu Stop/Restart):**
  - *Trước tối ưu (`auto`):* `Whisper P50 = 2.868 ms / P95 = 2.930 ms`, `VadToOverlay P50 = 3.019 ms / P95 = 3.204 ms`, `AvgWER = 4,17%`.
  - *Sau tối ưu (`auto`, đa ngôn ngữ):* **`Whisper P50 = 2.049 ms / P95 = 2.120 ms`** (`-28,6%`), **`VadToOverlay P50 = 2.242 ms / P95 = 2.289 ms`**, **`AvgWER = 0,00%`**.
  - *Audit 7 giai đoạn (`Speech Onset -> Overlay`, cùng 6 câu):*
    - **`AUTO`:** `AccumP50 = 3.188 ms`, Hangover `519 ms`, Whisper `2.138 / 2.276 ms`, EnViT5 `169 / 224 ms`, `VadToOverlay P50/P95 = 2.341 / 2.423 ms`, `PostSpeechToOverlay = 2.856 / 2.950 ms`, **`Speech Onset -> Overlay P50/P95 = 6.120 / 6.716 ms`**, `CpuTime = 85,84 s`, `WS = 316 MB`.
    - **`EN_EXPLICIT`:** `AccumP50 = 3.248 ms`, Hangover `538 ms`, **Whisper `644 / 688 ms` (`4,45x` nhanh hơn baseline)**, EnViT5 `1 / 164 ms` (`159–165 ms` khi miss cache), **`VadToOverlay P50/P95 = 683 / 819 ms` (`-77,4%` P50)**, **`PostSpeechToOverlay = 1.221 / 1.344 ms`**, **`Speech Onset -> Overlay P50/P95 = 4.469 / 5.330 ms`**, `CpuTime = 28,19 s` (`3x` nhẹ CPU hơn), `WS = 315 MB`.
- **Benchmark chất lượng 52 câu đa điều kiện (Sạch đa giọng `David`/`Zira`, nhanh/chậm, ngắn/dài, tên riêng/kỹ thuật vs Nhiễu 18 dB & 12 dB SNR):**
  - *Baseline (`T7 auto Ctx1500`):* `CleanWER = 5,94%`, `NoisyWER = 2,36%`, `AllWER = 5,11%`, `AllCER = 1,53%`, `LangAcc = 100,0%`, `P50 = 3.086 ms / P95 = 3.258 ms`.
  - *Sau tối ưu (`PROD52_AUTO`):* `CleanWER = 5,48%`, `NoisyWER = 2,36%`, **`AllWER = 4,76%`**, `AllCER = 1,53%`, `LangAcc = 100,0%`, **`P50 = 2.222 ms / P95 = 2.701 ms`**.
  - *Sau tối ưu (`PROD52_EN`):* `CleanWER = 5,48%`, `NoisyWER = 2,36%`, **`AllWER = 4,76%`**, `AllCER = 1,53%`, `LangAcc = 100,0%`, **`P50 = 726 ms / P95 = 1.240 ms`**.
- **Hội thoại Neural thực tế đa người nói EN (`Ava`, `Andrew`, `Emma`, `Brian` + nhạc nền 14 dB SNR + 6 câu ngữ cảnh danh từ riêng — `RealWorldAudioAndConcurrencyTests`):**
  - `SourceLanguage = Auto`: `12` câu, `Missing = 0`, `Duplicates = 0`, `AvgWER = 8,57%`, `AvgCER = 1,74%`, `Whisper P50/P95 = 2.001 / 2.091 ms`, `EnViT5 P50/P95 = 234 / 284 ms`, `TotalAfterVad P50/P95 = 2.234 / 2.360 ms`, `CpuSec = 162,73 s`, `WS = 285 MB`.
  - `SourceLanguage = English`: `12` câu, `Missing = 0`, `Duplicates = 0`, `AvgWER = 8,57%`, `AvgCER = 1,74%`, **`Whisper P50/P95 = 671 / 720 ms`**, `EnViT5 P50/P95 = 238 / 280 ms`, **`TotalAfterVad P50/P95 = 906 / 987 ms`**, **`CpuSec = 56,69 s` (`2,87x` nhẹ CPU hơn)**, `WS = 291 MB`.
  - *Kiểm tra riêng `WithNoContext()` vs `CarryContext`:* Đạt cùng độ chính xác trên hội thoại 6 lượt có ngữ cảnh (`All12_WER = 8,57%`, `Context6_WER = 17,14%`, `Context6_CER = 3,47%`, `ProperNounRecall = 12/15 = 80,0%`, `P50 = 664 ms` vs `667 ms`), xác nhận `WithNoContext()` không làm giảm độ chính xác tên riêng trong khi ngăn ngừa lặp hallucination khi có nhạc nền.
- **Hội thoại Neural tiếng Trung thực tế Giản thể (`zh-CN`: `晓晓`, `云希`) & Phồn thể (`zh-TW`: `曉臻`, `雲哲`) + `M2M-100` + Chinese Hybrid Mode (`RealWorldAudioAndConcurrencyTests`):**
  - `SourceLanguage = Auto`: `8/8` câu nhận diện đúng `DetLang = zh` (**`LangAcc = 100,0%`**), **`SimpNormCER = 4,68%`** (`ZHS = 5,60%`, `ZHT = 3,75%`; `RawCER = 24,65%` do Whisper xuất chữ Phồn thể cho câu đọc Giản thể), `Whisper P50/P95 = 2.040 / 2.085 ms`, `M2M-100 P50/P95 = 385 / 483 ms`.
  - `SourceLanguage = Chinese`: `8/8` câu `LangAcc = 100,0%`, **`SimpNormCER = 4,68%`** (`ZHS = 5,60%`, `ZHT = 3,75%`), **`Whisper P50/P95 = 678 / 688 ms` (`3,0x` nhanh hơn `Auto`)**, `M2M-100 P50/P95 = 389 / 456 ms`.
  - *Chinese Hybrid Mode (`OCR + Audio`):* Đã phát hiện và vá lỗi so khớp chữ Hán khác hệ chữ (`欢迎` vs `歡迎`), dấu câu (`，` vs khoảng trắng) và từ đồng âm ASR (`措施` vs `测试`, tương đồng `88,2% >= 78%`). Sau khi vá: câu trùng `"你好，欢迎大家参加今天的软件测试会议。"` được chặn ghi đè (`Suppressed = True`, `DupCountDelta = 1`); câu thoại mới `"請在離開辦公室之前關閉所有的電腦電源。"` được dịch chính xác sang `"Hãy tắt tất cả các nguồn điện của máy tính trước khi rời khỏi văn phòng."`.
- **Đo đồng thời `OCR + Whisper + Uncached Translation` trên CPU (`Phase4_RealtimeConcurrency`):**
  - Chạy song song `4` lượt đổi phụ đề màn hình (`RapidOCR`) + `6` câu thoại (`Whisper.net`) + `10` lượt dịch `EnViT5` mới hoàn toàn (**`CacheHitRate = 0,0%`**) trong `44,6 s`:
  - `OCR P50/P95 = 171 / 334 ms`, `Whisper P50/P95 = 676 / 1.355 ms`, `Uncached Translation P50/P95 = 211 / 566 ms`, **`SpeechQueueWait P50/P95 = 0 / 2 ms`**, `SpeechEnd -> Overlay P50/P95 = 1.405 / 2.237 ms`, `SpeechOnset -> Overlay P50/P95 = 5.522 / 6.638 ms`, `HostCpu = 31,14 s`, `PyCpu = 15,84 s`, **`TotalNormalizedCpu = 13,2%`**, `HostWS = 306 MB`, `PyWS = 544 MB`.
  - **Stop / Restart (`AudioRealtime`):** `Stop()` xóa phụ đề sạch (`HasContent = false`), `Start()` nhận diện & dịch câu `"Restart test succeeded cleanly."` → `"Khởi chạy lại tiến trình kiểm thử thành công một cách sạch sẽ."` (`WER = 0,0%`, `Speech = 2.000 ms`, `Trans = 149 ms`, `VadToOverlay = 2.149 ms`).
- **Hybrid Mode (`RealWhisperHybridMode_OcrAndSpeechConcurrent_...`):**
  - *Lọc trùng OCR + Speech:* Khi Edge OCR đang hiển thị `"Welcome to the station."` và phát audio câu khớp, Whisper nhận dạng đúng nhưng `AudioPublishesSuppressed = True` — không ghi đè/nhấp nháy Overlay.
  - *Bổ sung audio khác nội dung màn hình:* Khi phát `"Please open the window before leaving."`, Whisper nhận dạng (`2.959 ms`, `en`) và EnViT5 dịch `"Vui lòng mở cửa sổ trước khi rời đi."` lên `SubtitleOverlayWindow`.

### C. Thực nghiệm Live Edge Player + EnViT5 Beta + Real Overlay (`LiveHardwareE2ETests`)
- Chụp trực tiếp `msedge.exe` (`PrintWindow` + OCR + EnViT5 Beta + `SubtitleOverlayWindow`):
  - `Cues = 3`, `P50 = 344 ms`, `Crashes = 0` (`cap = 13–19 ms`, `ocr = 115–163 ms`, `tr = 104–179 ms`, `ov = 1 ms`).
  - `Overlay`: `ExStyle = 0x080801A8` (`Topmost + Transparent + Tool + NoAct`), `GDI 18->18 (delta=0)`, `USER 29->29 (delta=0)`, `Update P50/P95 = 1 ms`.

### D. Thực nghiệm Cài Đặt `beta.1` -> Nâng cấp tại chỗ `beta.2` -> Khởi chạy App -> Uninstall thật (`2026-11-20`)
- Đã kiểm chứng trực tiếp `/CURRENTUSER /VERYSILENT`: cài `v1.0.0-beta.1` (`exit=0`) -> cài đè nâng cấp lên `v1.0.0-beta.2` (`exit=0`, `ProductVersion = 1.0.0-beta.2+4c08dcd...`, `ocr_worker.py` có `_enhance_small_crops_for_rec = True`, `ScreenSub.Core.dll = 85.504 bytes` sạch hoàn toàn không có `EnglishWordSegmenter` hay `english_lexicon.txt`, `vcruntime140.dll = True`), khởi chạy `ScreenSub.Desktop.exe` (`PID=8520`, `WS = 159,4 MB`, `Priv = 81,5 MB`), gỡ cài đặt `unins000.exe` (`exit=0`, xóa sạch `.exe`, `OrphanPy = 0`), bảo toàn nguyên vẹn **`92 -> 92`** file trong `%LocalAppData%\ScreenSubAI\Models` (gồm cả `ggml-base.bin`).

### E. Độ trễ OCR & Soak Test Liên Tục 60 Phút trên `ScreenSub.Desktop.exe` Thật (`2026-11-20`, **`PASS`**)
- `OCR 22 ảnh`: `P50 = 60,7 ms / P95 = 115,4 ms`, `MeanCER = 1,91%` (`EN = 0,00%`, `ZHS = 0,00%`).
- **Soak Test 60 phút liên tục trên `ScreenSub.Desktop.exe` (`PID = 17052`, mốc đồng hồ tường `18:59:39 -> 20:00:22` gồm `~41,3 s` khởi tạo/warmup model `18:59:39 -> 19:00:21` + `60,01` phút / `3.600,3 s` đo soak liên tục theo monotonic clock `19:00:21 -> 20:00:21` + `~1 s` tắt sạch, `exit code = 0`, 0 orphan process sau khi tắt):**
  - Chạy luân phiên `30` lần chuyển chế độ (`ScreenOcr ↔ AudioRealtime ↔ Hybrid`) cùng `RapidOCR` thật (`ocr_worker.py`, `PID = 12892`), `CTranslate2` thật (`translation_worker.py`, `PID = 5144`, nạp song song `EnViT5` + `M2M-100`), `Whisper Base` thật (`ggml-base.bin` qua WASAPI Loopback), `msedge.exe` thay đổi phụ đề EN/ZHS/ZHT mỗi `3,8 s`, `10` chu kỳ `Pause/Resume` và `10` chu kỳ `Stop/Restart`.
  - Bằng chứng log đầy đủ: [soak_60min_live.log](soak_60min_live.log) và [soak_60min_results.json](soak_60min_results.json).

| Mốc | `.NET Host` WS / Priv / Heap | Host CPU (Cửa sổ) | Handles / GDI / USER | `RapidOCR` (`PID 12892`) WS / Priv / CPU / H | `CTranslate2` (`PID 5144`) WS / Priv / CPU / H | OCR P50/P95 (`n`) | Whisper P50/P95 (`n`) | Trans P50/P95 (`n`) | Pub / Drop / Stale / Dup | Crash / Restart / Timeout |
|---:|---|---:|---|---|---|---:|---:|---:|---:|---:|
| **0m** | `352,5 / 258,6 / 9,2 MB` | `11,41%` | `638 / 41 / 43` | `176,1 / 405,2 MB / 2,71% / 402` | `906,6 / 4.674,9 MB / 12,85% / 251` | `188 / 270 ms (4)` | `835 / 835 ms (1)` | `590 / 794 ms (3)` | `3 / 0 / 0 / 2` | `0 / 0 / 0` |
| **15m** | `353,9 / 254,2 / 14,1 MB` | `4,01%` | `648 / 41 / 39` | `176,1 / 406,8 MB / 0,91% / 394` | `892,4 / 4.677,0 MB / 6,20% / 241` | `123 / 221 ms (167)` | `666 / 905 ms (57)` | `497 / 1.149 ms (218)` | `219 / 0 / 0 / 4` | `0 / 0 / 0` |
| **30m** | `382,1 / 282,8 / 9,7 MB` | `4,85%` | `627 / 41 / 40` | `176,1 / 406,8 MB / 0,99% / 394` | `892,3 / 4.677,4 MB / 6,55% / 241` | `125 / 228 ms (330)` | `668 / 895 ms (126)` | `498 / 1.175 ms (439)` | `444 / 0 / 3 / 7` | `0 / 0 / 0` |
| **45m** | `350,9 / 271,3 / 12,3 MB` | `3,95%` | `649 / 41 / 40` | `152,0 / 406,8 MB / 0,99% / 394` | `791,3 / 4.676,3 MB / 5,41% / 241` | `129 / 240 ms (492)` | `661 / 895 ms (182)` | `482 / 1.149 ms (646)` | `654 / 0 / 7 / 9` | `0 / 0 / 0` |
| **60m** | `362,3 / 281,6 / 11,4 MB` | `4,84%` | `633 / 41 / 42` | `152,4 / 407,1 MB / 0,93% / 394` | `791,3 / 4.676,4 MB / 6,62% / 241` | `129 / 236 ms (653)` | `660 / 905 ms (251)` | `488 / 1.177 ms (870)` | `881 / 0 / 9 / 9` | `0 / 0 / 0` |

- **Phân tích xu hướng tài nguyên (`0m -> 60m`):**
  - **Không rò rỉ RAM (Private Bytes phẳng theo plateau):** `.NET Host Private` ổn định quanh `254–283 MB` (`30m -> 60m`: `-1,2 MB`), `Managed Heap` dao động `9,2–14,1 MB`; `RapidOCR Private` phẳng ở `406,8 -> 407,1 MB` (`+0,3 MB` trong 45 phút); `CTranslate2 Private` phẳng ở `4.674,9 -> 4.676,4 MB` (`+1,5 MB` trong 60 phút, `-0,6 MB` từ `15m -> 60m`). (Lưu ý: các đỉnh tức thời `~915 MB` ở 1–2 heartbeat phút lẻ trong `AudioRealtime` là bộ đệm cấp phát cục bộ khi Whisper giải mã và tự giải phóng ngay về `~260–280 MB`).
  - **Không rò rỉ Handles / GDI / USER:** Host `Handles` `638 -> 633` (`-5`), **`GDI Objects` cố định tuyệt đối `41 -> 41`** (`0` delta sau `881` lượt cập nhật Overlay và `9.479` frame capture), **`USER Objects` `43 -> 42`** (`-1`). Cả hai worker Python giữ nguyên `Handles` (`394` và `241`) và **giữ nguyên PID** xuyên suốt 60 phút (`0` crash, `0` worker restart, `0` timeout).

---

## 4. Trạng thái Release Gates & Các mục còn `PENDING`

| # | Release Gate | Trạng thái | Chi tiết |
|---:|---|:---:|---|
| 1 | Installer `v1.0.0-beta.2` + Upgrade từ `beta.1` + Uninstall trên host | **PASS** | `beta.2` (`50.18 MB` / `52.622.089 bytes`, `9EEF3FE1179733AF8CFBB1005E7EA8B29C9D472F7026709CCB031834B2BB846B`, sạch hoàn toàn không có `EnglishWordSegmenter` hay `english_lexicon.txt`, gồm bản vá Maximize/DPI) giữ song song `beta.1` (`1F3A6AAAD72CD67CA960095EA0254CCEBB40C77ACD83A2A655FF285A0058528B`); Upgrade & Uninstall `exit=0`, giữ `92->92` file Models, `OrphanPy=0` |
| 2 | Release Build + `188 Unit` + `84 Integration` (`272` tổng) | **PASS** | `0 Warning, 0 Error`; `188/188 UnitTests`, `84/84 IntegrationTests` (gồm test `MainWindow_MaximizeAndResponsiveLayout`, `0 Failed, 0 Skipped`) |
| 3 | Live Edge Window -> OCR -> EnViT5 -> Overlay E2E | **PASS** | Chạy thực tế trên `msedge.exe` + `SubtitleOverlayWindow` |
| 4 | Hardware WASAPI Loopback + VAD | **PASS** | Thu loopback sạch từ `Speakers (Realtek(R) Audio)` (`MaxPeak = 0,1407`) |
| 5 | Whisper (`ggml-base.bin`) EN + ZH (`zh-CN`/`zh-TW`) + Semantic-Safe Hybrid + Concurrency | **PASS** | EN `Whisper P50 = 671 ms` (`AvgWER = 8,57%`), ZH `Whisper P50 = 678 ms` (`SimpNormCER = 4,68%`, `LangAcc = 100%`), Concurrency `CacheHit = 0%`, Hybrid Semantic Safety `0% FP / 0% FN` |
| 6 | Clean Windows VM hoàn toàn sạch (không có Python/VC++) | **PENDING** | Máy dùng `Windows 11 Home` (không có Hyper-V/Sandbox/VM sẵn) |
| 7 | Interactive Multi-Monitor / Đa DPI / Borderless Game / Hotkey `Ctrl+Alt+Q` / Strict Offline vật lý | **PENDING** | Cần test tương tác trực tiếp đa màn hình, scale `125%–200%`, game fullscreen & rút mạng vật lý |
| 8 | Soak Test liên tục `60` phút (`0/15/30/45/60m`) trên `ScreenSub.Desktop.exe` | **PASS** | Đạt đủ `60,01` phút liên tục (`18:59 -> 20:00`), `2` Python workers giữ nguyên PID, `GDI 41->41`, `USER 43->42`, `Handles 638->633`, `0` crash / `0` restart / `0` timeout (`10` Pause/Resume, `10` Stop/Restart, `30` mode switches, `881` phụ đề) |
| 9 | Traditional Chinese OCR Opt-in Benchmark (`PP-OCRv3 CHT` / `PP-OCRv5`) | **PENDING** | Giữ nguyên PP-OCRv4 (chưa tải thêm trọng số mới theo đúng yêu cầu) |
| 10 | Independent Real-World Video OCR Holdout (50–100 khung hình phim/YouTube thật) | **PENDING** | Đã hoàn tất Untouched 80-Crop Synthetic Ablation (Config B: EN `Exact = 50/52 = 96,2%`, `WB F1 = 99,64%`, `WER = 0,65%`, `0` false splits), nhưng chưa có 50–100 khung hình cắt từ video thật có ground truth độc lập |
| 11 | Lexicon License Safety (không ship từ điển chưa rõ bản quyền) | **PASS** | Đã chọn Config B, vô hiệu hóa `EnglishWordSegmenter` và gỡ hoàn toàn `english_lexicon.txt` khỏi `ScreenSub.Core.csproj` & bộ cài (xác minh bởi `ScreenSubCoreAssembly_ShouldNotEmbedUnlicensedLexiconResource`) |

---

## 5. Kết luận: **`CONDITIONAL` (Controlled Beta `v1.0.0-beta.2` Ready)**

- Toàn bộ pipeline **Screen OCR**, **Audio Realtime (EN & ZH Giản thể/Phồn thể)**, **Hybrid Mode (với bộ lọc trùng ngữ nghĩa an toàn `0% FP / 0% FN`)** và **Soak Test 60 phút liên tục trên `ScreenSub.Desktop.exe` thật** đã đạt **`PASS` (`0` crash, `0` leak, `0` worker restart)**.
- Dự án **chưa tuyên bố Wide Beta Ready** vì vẫn còn 2 cổng nghiệm thu môi trường thực tế đang ở trạng thái **`PENDING`**: **Clean Windows 10/11 VM hoàn toàn sạch (Non-Admin, chưa có Python/VC++)** và **Multi-Monitor / Mixed DPI (`125%–200%`) / Borderless Fullscreen Game / Strict Offline vật lý**.
