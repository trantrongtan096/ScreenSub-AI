# ScreenSub AI — Beta Release Candidate Manual QA & Clean Install Checklist (Windows 10/11 x64)

Tài liệu nghiệm thu thực tế trên **Windows sạch (Clean VM)** và **Windows Desktop GUI thật** trước khi nâng từ **Controlled Beta** lên phát hành **Wide Beta**.

> **Ghi chú đã tự động kiểm chứng thực tế trên máy phát triển (`SessionId=1`):**  
> - **Bộ cài Installer thật (`/CURRENTUSER` Install `beta.1` -> Upgrade `beta.2` -> Launch App -> Uninstall):** **PASS trên host** (`exit=0`, giữ nguyên `92->92` file Models). Bản phát hành mới **`ScreenSubAI_v1.0.0-beta.2_Setup_x64.exe`** (`50.18 MB`, SHA-256 `826402D6FB8258F553A2DE4927C718181CABAF5FF4E7BD0E02A9A1B9823EDA5D`) giữ song song bản rollback `v1.0.0-beta.1` (`1F3A6AAAD72CD67CA960095EA0254CCEBB40C77ACD83A2A655FF285A0058528B`). *Còn `PENDING` trên VM sạch hoàn toàn chưa cài Python/VC++.*
> - **Real Edge Player Window (`msedge.exe` EN + ZH) -> `PrintWindow` -> OCR -> EnViT5/M2M-100 -> `SubtitleOverlayWindow` -> Pause/Resume/Stop:** **PASS** (`cap=13–19 ms`, `ov=1 ms`, `P50=344 ms`).  
> - **Real Multi-Speaker Neural Audio (EN `Ava/Andrew/Emma/Brian` & ZH `晓晓/云希/曉臻/雲哲`) -> Loa phần cứng `'Speakers (Realtek(R) Audio)'` -> WASAPI Loopback -> EnergyVAD -> Whisper.net (`ggml-base.bin`) -> EnViT5/M2M-100 -> Overlay & Semantic-Safe Hybrid Mode (EN + ZH, `0% FP / 0% FN`) & Concurrency:** **PASS** (EN `AvgWER = 8,57%`, `Whisper P50 = 671 ms`; ZH `SimpNormCER = 4,68%`, `LangAcc = 100%`, `Whisper P50 = 678 ms`; Concurrency `CacheHitRate = 0%`, `SpeechQueueWait P50/P95 = 0/2 ms`, `TotalNormalizedCpu = 13,2%`).
> - **Overlay Win32 ExStyle & Handle Stability (50 lần cập nhật):** **PASS** (`ExStyle=0x080801A8`, `GDI 18->18`, `USER 29->29`, `Update P50/P95 = 1 ms`).  
> - **CTS Retirement & 25-cycle Stop/Restart Stress:** **PASS** (`RealWorker_RepeatedStopRestartStress_25Cycles` trong `5 s`, `0` crash).  
> - Các hạng mục còn `PENDING` bên dưới (VM Windows sạch không dev tools, đa màn hình DPI `100%/125%/150%/200%`, Borderless game, Hotkey `Ctrl+Alt+Q`, ngắt mạng vật lý, Soak 60 phút) cần môi trường thực tế tương ứng.

---

## A. Nghiệm thu Cài Đặt Windows Sạch (Clean Windows / Non-Admin / Upgrade & Uninstall) — `[ ] PENDING VM SẠCH`

Chạy trên máy ảo hoặc máy Windows 10/11 x64 chưa cài .NET SDK, chưa cài Python dev tool:

1. **Cài đặt với tài khoản chuẩn (Standard Non-Admin User):**
   - Chạy `ScreenSubAI_v1.0.0-beta.2_Setup_x64.exe` (`50,18 MB`, SHA-256: `826402D6FB8258F553A2DE4927C718181CABAF5FF4E7BD0E02A9A1B9823EDA5D`, `PrivilegesRequired=lowest`; bản rollback `v1.0.0-beta.1` SHA-256: `1F3A6AAAD72CD67CA960095EA0254CCEBB40C77ACD83A2A655FF285A0058528B`).
   - Xác nhận hiển thị license bên thứ ba (`THIRD_PARTY_LICENSES.txt`, gồm MIT, Apache-2.0, BigScience OpenRAIL-M).
   - Cài đặt thành công vào `%LocalAppData%\Programs\ScreenSub AI` mà không bắt buộc quyền Administrator.
2. **Kiểm tra `.NET 10 Self-Contained` & `VC++ Runtime`:**
   - Mở `ScreenSub.Desktop.exe` khi máy chưa có .NET 10 Desktop Runtime → giao diện Dashboard khởi chạy sạch.
   - Kiểm tra hệ thống có `C:\Windows\System32\vcruntime140.dll` (Microsoft VC++ 2015–2022 Redistributable x64 — cần cho ONNX Runtime và CTranslate2).
3. **Chuẩn bị Python Runtime (Opt-in có sự đồng ý):**
   - Trường hợp B (Installer nhẹ `50,16 MB`): Cài Python 3.10–3.12 x64 (hoặc đặt portable `python\` cạnh `.exe`), chạy Start Menu → *Verify Python AI Environment* / *Setup Python AI Dependencies*, chỉ đồng ý (`Y`) khi sẵn sàng tải pip (`rapidocr_onnxruntime`, `onnxruntime`, `ctranslate2`, `sentencepiece`, `tokenizers`, `transformers`).
4. **Cấp model & kiểm tra SHA-256 + Strict Offline:**
   - Cấp `m2m100_418m_ct2` và `envit5_ct2_int8` vào `%LocalAppData%\ScreenSubAI\Models\translation\`, kiểm tra khớp SHA-256 trong Model Manager.
   - Nếu test Whisper, tải `ggml-base.bin` qua Model Manager (có đồng ý người dùng) vào `%LocalAppData%\ScreenSubAI\Models\whisper\ggml-base.bin`.
   - Ngắt Internet hoàn toàn, kiểm tra app khởi động và dịch Offline thuần túy không gửi request mạng.
5. **Nâng cấp & Gỡ cài đặt (Upgrade & Uninstall):**
   - Cài đè lại bộ cài cùng/mới hơn → giữ nguyên `%LocalAppData%\ScreenSubAI\Models\` và cấu hình.
   - Gỡ cài đặt qua `Apps -> Installed Apps` / `unins000.exe` → xóa sạch `{app}`, bảo toàn thư mục Models & Settings người dùng.

---

## B. Ma trận 14 Hạng Mục Kiểm Thử Windows Desktop GUI & Audio Thực Tế

Log runtime và telemetry ghi tại `%LocalAppData%\ScreenSubAI\Logs\` (theo dõi độ trễ `Capture -> OCR -> Translation -> Overlay` để thống kê `P50 / P95`).

| # | Kịch bản kiểm thử | Thao tác thực hiện | Tiêu chí PASS | Kết quả |
|---:|---|---|---|---|
| 1 | **YouTube & VLC** | Phát video trên Chrome/Edge YouTube và VLC (1080p), bật `Screen OCR + Offline`. | Nhận diện ổn định trên cả browser và desktop media player; ghi nhận P50/P95. | `[ ] PASS / [ ] FAIL` |
| 2 | **Phụ đề EN / Giản thể / Phồn thể** | Thử cả 3 ngôn ngữ phụ đề thực tế; đối chiếu raw OCR log và bản dịch VI. | EN và Giản thể khớp cao; Phồn thể ghi nhận ký tự OOV nếu gặp (không dùng dịch để che lỗi). | `[ ] PASS / [ ] FAIL` |
| 3 | **Cảnh hội thoại nhanh** | Phát thoại đổi nhanh mỗi 400–600 ms trong 2 phút. | Câu mới thắng, 0 câu cũ ghi đè ngược, queue không nghẽn. | `[ ] PASS / [ ] FAIL` |
| 4 | **Vùng OCR nhỏ & nhiều dòng** | Kéo vùng phụ đề nhỏ (~18–20px) và phụ đề 2–3 dòng dài. | `FrameChangeDetector` nhạy đủ không bỏ sót; giữ đủ nhiều dòng khi dịch. | `[ ] PASS / [ ] FAIL` |
| 5 | **Overlay Topmost & Click-Through** | Bật `Always on Top` + `Click-Through`, click chuột xuyên qua Overlay. | Overlay nổi trên video/game borderless; thao tác chuột đi xuyên xuống nền. | `[ ] PASS / [ ] FAIL` |
| 6 | **DPI 100%, 125%, 150%, 200%** | Kiểm tra lần lượt các mức scale Display Windows. | Khung chọn vùng, vị trí Overlay và kích thước crop OCR chuẩn tọa độ. | `[ ] PASS / [ ] FAIL` |
| 7 | **Đa màn hình** | Đặt video ở Monitor 1 (hoặc 2 khác DPI), kéo vùng và hiển thị Overlay. | Tọa độ virtual screen đa màn hình chính xác, không lệch monitor. | `[ ] PASS / [ ] FAIL` |
| 8 | **Windowed / Borderless Fullscreen** | Thử cửa sổ thường và game/player Borderless Fullscreen (`Follow Window`). | Capture lấy frame sạch, `SetWindowDisplayAffinity` tránh tự capture Overlay. | `[ ] PASS / [ ] FAIL` |
| 9 | **Quick Translate (`Ctrl + Alt + Q`)** | Thu nhỏ cửa sổ, bấm `Ctrl + Alt + Q`, chọn vùng mới hoặc nhấn `ESC`. | Chọn xong auto-start ngay; bấm `ESC` hồi phục trạng thái trước an toàn. | `[ ] PASS / [ ] FAIL` |
| 10 | **System Tray & Close-to-Tray** | Đóng Dashboard (`MinimizeToTrayOnClose`), thao tác menu Tray → Exit. | Thu về khay tiếp tục dịch; Exit tắt sạch cả `.NET` và mọi tiến trình `python.exe`. | `[ ] PASS / [ ] FAIL` |
| 11 | **WASAPI Loopback thật** | Phát âm thanh hệ thống trên loa/tai nghe mặc định ở `AudioRealtime`. | Thanh `AudioPeakLevel` dao động đúng âm lượng, đổi output device không lỗi. | `[x] PASS (Auto Real Hardware)` |
| 12 | **Whisper.net & Hybrid Mode** | Tải `ggml-base.bin` (opt-in), test `AudioRealtime` và `Hybrid (OCR + Audio)`. | Speech dịch mạch lạc; trong Hybrid không ghi đè nhảy chữ khi OCR trùng speech. | `[x] PASS (ggml-base.bin 60ed5bc3...)` |
| 13 | **Stop / Pause / Resume / Restart** | Nhấn Stop, Pause, Resume, Restart liên tục lúc OCR/Translation đang chạy. | 0 crash, xóa phụ đề sạch khi Stop, không rò câu cũ từ phiên trước. | `[x] PASS (Stress 25x + Edge + Audio)` |
| 14 | **Strict Offline ngắt Internet** | Rút LAN / tắt Wi-Fi, bật `Offline`, `AllowCloudFallbackOnOfflineFailure=false`. | 0 kết nối mạng ra ngoài, 0 fallback Azure/Gemini/Mock. | `[ ] PASS / [ ] FAIL` |

---

## C. Soak Test Liên Tục 60 Phút (Desktop GUI Thật)

Ghi nhận thông số định kỳ tại **T+0 (sau warm-up 2 phút)**, **T+15m**, **T+30m**, **T+45m**, **T+60m**:
- `ScreenSub.Desktop.exe`: `Working Set (MB)`, `Private Bytes (MB)`, `Handle Count`, `GDI Objects`, `USER Objects`, `CPU %`, `GPU %`.
- Worker `python.exe` (`ocr_worker.py` & `translation_worker.py`): `RSS / Working Set (MB)`, `Private Bytes (MB)`, số tiến trình `<= 2`, `0 crash`.
- Telemetry `SubtitleCoordinator`: `QueueLength`, `OcrAttemptCount`, `OcrEmptyOrFailureRetryCount`, `FrameSkippedUnchangedCount`, `CacheHitRate`, `DroppedObsoleteCount`, `RejectedStaleCount`, `Stop/Restart cycles`.
- **Tiêu chí PASS:** Không crash trong 60 phút, Handle/GDI/USER objects không tăng tuyến tính liên tục sau warm-up, RAM `.NET` và `Python` ổn định (plateau), không mất/trễ phụ đề.
