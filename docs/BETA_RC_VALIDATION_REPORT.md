# BÁO CÁO NGHIỆM THU BETA RELEASE CANDIDATE — SCREENSUB AI

**Ngày cập nhật:** 2026-11-18  
**Phiên bản đóng gói:** `ScreenSub AI v1.0.0-beta.2` (giữ song song `v1.0.0-beta.1` để rollback)  
**Trạng thái phát hành Beta:** **`CONDITIONAL` (Controlled Beta — Đã nghiệm thu xong Real-World Audio EN & ZH Giản/Phồn + Hybrid Mode + Concurrency CPU Contention + Upgrade `beta.1 -> beta.2`; còn chờ VM sạch / Multi-monitor & DPI / Soak 60 phút)**

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

---

## 2. Danh sách file cập nhật gần nhất

- [SubtitleTextProcessor.cs](../src/ScreenSub.Core/Processing/SubtitleTextProcessor.cs) & [SubtitleCoordinator.cs](../src/ScreenSub.Desktop/Services/SubtitleCoordinator.cs) — thêm `_isOcrCurrentlyPresentOnScreen`, `_lastOcrSeenTimeUtc` (`<= 4,5 s`) và bộ lọc ngữ nghĩa an toàn `ShouldSuppressHybridSpeechDuplicate` / `AreSubtitlesSemanticallyEquivalent` (`0% FP / 0% FN`).
- [HardwareFactAttribute.cs](../tests/IntegrationTests/HardwareFactAttribute.cs) — thuộc tính `[HardwareFact]` kế thừa `FactAttribute` đảm bảo không bao giờ báo `PASS` giả khi thiếu phần cứng/model.
- [SubtitleTextProcessorTests.cs](../tests/UnitTests/SubtitleTextProcessorTests.cs) — bổ sung `77` unit test cases cho toàn bộ nhóm kiểm thử P1 Hybrid Deduplication Safety (tổng `161` Unit Tests).
- [LocalModelManager.cs](../src/ScreenSub.Infrastructure/LocalModelManager.cs) — cập nhật chính xác `SizeBytes` và `Sha256Checksum` của `whisper-base-ggml` (`ggerganov/whisper.cpp`).
- [WhisperModelManager.cs](../src/ScreenSub.Speech/Whisper/WhisperModelManager.cs) & [WhisperSpeechRecognitionService.cs](../src/ScreenSub.Speech/Whisper/WhisperSpeechRecognitionService.cs) — cô lập `ModelStorageDirectory`, `WithGreedySamplingStrategy(bestOf=1)`, `WithNoContext()`, adaptive `WithAudioContextSize(768)` cho đoạn VAD `<= 8s`.
- [EnergyVoiceActivityDetector.cs](../src/ScreenSub.Audio/VAD/EnergyVoiceActivityDetector.cs) & [SpeechSegment.cs](../src/ScreenSub.Core/Models/SpeechSegment.cs) — sửa rollover `_speechStartTimeMs`/`_lastSpeechTimeMs` khi thoại vượt `MaxSpeechDurationMs`, thêm `CreatedAtUtc`.
- [FrameChangeDetector.cs](../src/ScreenSub.Capture/FrameChangeDetector.cs) — thêm SHA-256 fast-path cho frame giống nhau byte-by-byte để tránh decode WIC thừa gây reset retry bound.
- [LocalChineseConverter.cs](../src/ScreenSub.Translation/LocalChineseConverter.cs) — tích hợp thêm Win32 `LCMapStringEx` (`LCMAP_SIMPLIFIED_CHINESE` / `LCMAP_TRADITIONAL_CHINESE`) để chuyển đổi đầy đủ bộ chữ Hán Phồn ↔ Giản offline.
- [ScreenSubAI_Setup.iss](../installer/ScreenSubAI_Setup.iss), [build_installer.ps1](../installer/build_installer.ps1) & [ScreenSub.Desktop.csproj](../src/ScreenSub.Desktop/ScreenSub.Desktop.csproj) — nâng phiên bản lên `v1.0.0-beta.2` (không ghi đè `beta.1`).
- [LiveHardwareE2ETests.cs](../tests/IntegrationTests/LiveHardwareE2ETests.cs), [WhisperAuditBenchmarkTests.cs](../tests/IntegrationTests/WhisperAuditBenchmarkTests.cs), [RealWorldAudioAndConcurrencyTests.cs](../tests/IntegrationTests/RealWorldAudioAndConcurrencyTests.cs) & [FrameChangeAndRetrySafetyTests.cs](../tests/IntegrationTests/FrameChangeAndRetrySafetyTests.cs).
- [README.md](../README.md), [INSTALL.md](INSTALL.md), [CHANGELOG.md](CHANGELOG.md), [KNOWN_ISSUES.md](KNOWN_ISSUES.md), [MODEL_GUIDE.md](MODEL_GUIDE.md), [BETA_DESKTOP_QA_CHECKLIST.md](BETA_DESKTOP_QA_CHECKLIST.md) & [BETA_RC_VALIDATION_REPORT.md](BETA_RC_VALIDATION_REPORT.md).
- Bộ cài phát hành song song trong `dist/`:
  - **Bản mới (`v1.0.0-beta.2`):** `dist/ScreenSubAI_v1.0.0-beta.2_Setup_x64.exe` (`50.18 MB` / `52.619.098 bytes`, SHA-256 `826402D6FB8258F553A2DE4927C718181CABAF5FF4E7BD0E02A9A1B9823EDA5D`).
  - **Bản rollback (`v1.0.0-beta.1`):** `dist/ScreenSubAI_v1.0.0-beta.1_Setup_x64.exe` (`50.17 MB` / `52.607.228 bytes`, SHA-256 `1F3A6AAAD72CD67CA960095EA0254CCEBB40C77ACD83A2A655FF285A0058528B`).

---

## 3. Kết quả kiểm thử thực tế tự thực hiện trên máy

### A. Build & Toàn bộ Regression Suite (`Release`, chạy sau lần sửa cuối)
| Bộ kiểm thử | Kết quả | Thời gian |
|---|---|---:|
| **Release Build (`ScreenSubAI.slnx`)** | **PASS (`0 Warning, 0 Error`)** | `4,20 s` |
| **UnitTests** | **`161/161 PASSED`** (`0 Failed, 0 Skipped`) | `19 s` |
| **IntegrationTests** (Real Workers + Live Edge + Real EN/ZH Neural Audio + Hybrid Semantic Safety & OcrFreshnessLifecycle + Concurrency + Stress 25x) | **`81/81 PASSED`** (`0 Failed, 0 Skipped`) | `15 m 20 s` |

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

### D. Thực nghiệm Cài Đặt `beta.1` -> Nâng cấp tại chỗ `beta.2` -> Khởi chạy App -> Uninstall thật (`task-7381`)
- Đã kiểm chứng trực tiếp `/CURRENTUSER /VERYSILENT`: cài `v1.0.0-beta.1` (`exit=0`) -> cài đè nâng cấp lên `v1.0.0-beta.2` (`exit=0`, `ProductVersion = 1.0.0-beta.2`, `vcruntime140.dll = True`), khởi chạy `ScreenSub.Desktop.exe` (`WS = 128,1 MB`, `Priv = 58,3 MB`), gỡ cài đặt `unins000.exe` (`exit=0`, xóa sạch `.exe`), bảo toàn nguyên vẹn **`92 -> 92`** file trong `%LocalAppData%\ScreenSubAI\Models` (gồm cả `ggml-base.bin`).

### E. Độ trễ OCR & Soak Worker thật (`[PHASE3_OCR]`, `[PHASE4_SOAK]`)
- `OCR 22 ảnh`: `P50 = 60,7 ms / P95 = 115,4 ms`, `MeanCER = 1,91%` (`EN = 0,00%`, `ZHS = 0,00%`).
- `Soak 75,3 s`: `Cycles=138, StopRestarts=5, Published=152, Crashes=0, HostCpu=2,14 s, HostPriv 53,6 -> 51,0 MB, PyRss 1.011,1 -> 1.079,3 MB, PyPriv Mid->End +0,0 MB`.

---

## 4. Trạng thái Release Gates & Các mục còn `PENDING`

| # | Release Gate | Trạng thái | Chi tiết |
|---:|---|:---:|---|
| 1 | Installer `v1.0.0-beta.2` + Upgrade từ `beta.1` + Uninstall trên host | **PASS** | `beta.2` (`50.18 MB`, `826402D6FB8258F553A2DE4927C718181CABAF5FF4E7BD0E02A9A1B9823EDA5D`) giữ song song `beta.1` (`1F3A6AAAD72CD67CA960095EA0254CCEBB40C77ACD83A2A655FF285A0058528B`); Upgrade & Uninstall `exit=0`, giữ `92->92` file Models |
| 2 | Release Build + `161 Unit` + `81 Integration` (`242` tổng) | **PASS** | `0 Warning, 0 Error`; `161/161 UnitTests`, `81/81 IntegrationTests` (`0 Skipped` trên phần cứng thật) |
| 3 | Live Edge Window -> OCR -> EnViT5 -> Overlay E2E | **PASS** | Chạy thực tế trên `msedge.exe` + `SubtitleOverlayWindow` |
| 4 | Hardware WASAPI Loopback + VAD | **PASS** | Thu loopback sạch từ `Speakers (Realtek(R) Audio)` (`MaxPeak = 0,1407`) |
| 5 | Whisper (`ggml-base.bin`) EN + ZH (`zh-CN`/`zh-TW`) + Semantic-Safe Hybrid + Concurrency | **PASS** | EN `Whisper P50 = 671 ms` (`AvgWER = 8,57%`), ZH `Whisper P50 = 678 ms` (`SimpNormCER = 4,68%`, `LangAcc = 100%`), Concurrency `CacheHit = 0%`, Hybrid Semantic Safety `0% FP / 0% FN` |
| 6 | Clean Windows VM hoàn toàn sạch (không có Python/VC++) | **PENDING** | Máy dùng `Windows 11 Home` (không có Hyper-V/Sandbox/VM sẵn) |
| 7 | Interactive Multi-Monitor / Đa DPI / Borderless Game / Hotkey `Ctrl+Alt+Q` / Strict Offline vật lý | **PENDING** | Cần test tương tác trực tiếp đa màn hình, scale `125%–200%`, game fullscreen & rút mạng vật lý |
| 8 | Soak Test liên tục `60` phút (`0/15/30/45/60m`) | **PENDING** | Mới chạy soak ngắn (`75,3 s`); chưa chạy đủ 60 phút nên không đánh dấu PASS |
| 9 | Traditional Chinese OCR Opt-in Benchmark (`PP-OCRv3 CHT` / `PP-OCRv5`) | **PENDING** | Giữ nguyên PP-OCRv4 (chưa tải thêm trọng số mới theo đúng yêu cầu) |

---

## 5. Kết luận: **`CONDITIONAL` (Controlled Beta `v1.0.0-beta.2` Ready)**

- Toàn bộ pipeline **Screen OCR**, **Audio Realtime (EN & ZH Giản thể/Phồn thể)** và **Hybrid Mode (cùng chạy đồng thời OCR + Whisper + Translation trên CPU với bộ lọc trùng ngữ nghĩa an toàn `0% FP / 0% FN`)** đã được kiểm chứng end-to-end trên phần cứng thật (`161/161 Unit`, `81/81 Integration`, `0` crash) và đóng gói thành **`ScreenSubAI_v1.0.0-beta.2_Setup_x64.exe`** (bảo toàn bản `beta.1` để rollback).
- Trước khi mở rộng lên **Wide Beta**, cần hoàn tất các cổng `PENDING` phụ thuộc môi trường/thời lượng: **Clean Windows VM**, **Multi-Monitor / DPI / Borderless Fullscreen / Hotkey tương tác vật lý**, và **Soak Test đủ 60 phút (`0/15/30/45/60m`)**.
