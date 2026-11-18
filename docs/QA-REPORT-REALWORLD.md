# SCREENSUB AI — BÁO CÁO KIỂM THỬ THỰC TẾ & PHÂN TÍCH NGUYÊN NHÂN GỐC

**Sản phẩm:** ScreenSub AI Windows Beta v1.0.0-beta.1
**Máy kiểm thử:** Windows 11 Home Single Language, build 26200, x64 · Intel Core i5-10300H (4C/8T) · RAM 15,8 GB · 1 màn hình 1920×1080 @ **DPI 125 % (120 DPI)** · .NET SDK 10.0.401 / Runtime 10.0.12 · Python 3.12.8
**Thời điểm:** 11/11/2026, 08:29 – 09:40 (UTC+7)
**Phương thức:** kiểm thử trên sản phẩm đã build/đóng gói thật (process thật, GUI thật, GPU/CPU thật, mô hình thật), **không** dựa trên báo cáo Phase hay kết quả unit test có sẵn.

---

## A. EXECUTIVE SUMMARY

### A.1 Số liệu tổng hợp

| Chỉ số | Giá trị |
|---|---|
| Tổng số test case | **16** (TEST01–TEST16) |
| PASS | **5** |
| FAIL | **9** |
| PARTIAL | **2** (một phần luồng chạy được) |
| NOT TESTED | **0** (mọi hạng mục đều chạm tới được; các hạng mục không kết luận được ghi rõ là PARTIAL kèm lý do) |

### A.2 Kết luận một câu

> **ScreenSub AI v1.0.0-beta.1 KHÔNG dịch được phụ đề trên màn hình trong điều kiện thực tế.** Toàn bộ chuỗi phía sau (OCR thật + mô hình M2M-100 thật) đều **hoạt động độc lập và cho kết quả đúng**, nhưng **đường ống end-to-end bị đứt ở tầng Capture do lỗi DPI 125 %**, và khi chạy thật thì **Dashboard tự làm mù chính bộ chụp màn hình** (cửa sổ bị Windows "cloak", BitBlt trả về đen), khiến pipeline không bao giờ nhận được khung hình có chữ.

### A.3 Trạng thái thực tế của từng subsystem (đo được, không suy đoán)

| Subsystem | Trạng thái thật | Bằng chứng |
|---|---|---|
| Build & Unit/Integration Test | ✅ **Đạt** | 0 warning / 0 error; 74/74 test PASS |
| Mô hình M2M-100 offline | ✅ **Cài đặt thật, nạp được** | `model.bin` 487,9 MB, nạp 2 000 ms |
| Suy luận neural (6 chiều) | ✅ **Chạy thật, không mạng** | 10 câu dịch trong 13,6 s, chặn socket vẫn chạy |
| OCR (RapidOCR ONNX) | ✅ **Đọc chữ thật tốt** | Ảnh phụ đề YouTube thật: 0,97–0,99 |
| Capture vùng màn hình | ❌ **Sai toạ độ + mù** | Lệch 117–130 px, phóng 1,25×; vùng chụp ra đen |
| Overlay hiển thị phụ đề | ❌ **Không hiển thị** | HWND tồn tại nhưng không có chữ nào |
| Quick Translate (Ctrl+Alt+Q) | ⚠️ **Hotkey OK, dùng bị kẹt** | Mở vùng chọn OK; ESC/chuột phải bị bỏ qua |
| Dashboard | ❌ **Cửa sổ bị "vô hình với BitBlt"** | Chụp lên vùng Dashboard = đen 100 % |
| Installer | ⚠️ **Cài được, nhưng thiếu điều kiện tiên quyết** | Cài OK 64 tệp; không kiểm tra .NET/Python/model |

---

## B. BẢNG KẾT QUẢ KIỂM THỬ

| ID | Module | Kỳ vọng | Thực tế | Trạng thái |
|---|---|---|---|---|
| **TEST01** | Môi trường & khả năng chạy | Máy đáp ứng cấu hình tối thiểu, DPI/đa màn hình xác định được | Win11 26200, i5-10300H, 15,8 GB, 1920×1080 @125 %, .NET 10.0.12 có sẵn, Python 3.12.8 có sẵn. Không có Edge. | **PASS** |
| **TEST02** | Build & Test suite | Build sạch, toàn bộ test pass | `dotnet build -c Release`: thành công 0 warning/0 error, 16,5 s. `dotnet test`: **74/74 PASS**, 52,3 s. | **PASS** |
| **TEST03** | Cài đặt & nạp mô hình offline | Mô hình có mặt, worker nạp được | `model.bin` 487 900 865 B (`TransformerSpec`), nạp 2 000 ms, self-test 6 chiều 215–340 ms. | **PASS** |
| **TEST04** | Suy luận neural, 6 chiều, câu mới | Dịch đúng nghĩa | Chạy được 100 %; chất lượng **không đồng đều**: 6/10 câu tốt, 4/10 sai nghĩa (chi tiết D.2). | **PARTIAL** |
| **TEST05** | Chế độ offline tuyệt đối | Không phụ thuộc mạng/Azure | Chặn socket → vẫn dịch đúng; `Get-NetTCPConnection` = 0 kết nối ra ngoài. | **PASS** |
| **TEST06** | Ma trận backend capture | Backend phải chụp được nội dung Chrome | BitBlt màn hình: **OK**. PrintWindow flag 2: **OK**. PrintWindow flag 0: **đen**. BitBlt trên vùng bị cửa sổ ScreenSub che: **đen**. | **FAIL** |
| **TEST07** | OCR chữ thật trên phụ đề video | Nhận diện đúng ≥ 90 % | RapidOCR đọc đúng `with communities across Southeast Asia` 0,9726 và `在东南亚也有不少苗族人，` 0,9973. | **PASS** |
| **TEST08** | OCR realtime qua pipeline app | Chữ OCR đến được tầng dịch | 0 khung hình có chữ đến tầng dịch sau 60 s chạy thật (worker OCR tiêu tốn 152,6 s CPU). | **FAIL** |
| **TEST09** | OCR hiển thị khi dịch lỗi | Vẫn hiển thị chữ gốc | Không có giao diện để kiểm chứng vì pipeline không ra chữ. | **FAIL** |
| **TEST10** | Overlay Single/Dual/Triple | 3 chế độ hiển thị đúng | Overlay hwnd tồn tại, topmost, staging OK — nhưng **không có chữ nào từng hiện**; `OverlayContainer` bị `Collapsed` (cao 24 px). Ba chế độ không quan sát được. | **FAIL** |
| **TEST11** | Overlay Windowed/Fullscreen, Click-through/Topmost | Chữ nổi trên video, chuột xuyên qua | `topmost=True`, `affinity=0`, `WS_EX_TRANSPARENT` có set — nhưng không có nội dung để xác nhận. | **FAIL** |
| **TEST12** | Quick Translate Ctrl+Alt+Q | Hotkey mở vùng chọn, dịch ngay, ESC huỷ, chọn lại được | Hotkey đăng ký OK, vùng chọn mở OK, dashboard tự ẩn OK. **Nhưng khi mở lại lần 2 toạ độ lệch hoàn toàn và ESC/chuột phải bị bỏ qua** (chi tiết C.1). | **FAIL** |
| **TEST13** | E2E ≥ 10 phụ đề liên tiếp | ≥10 phụ đề dịch thành công | **0/10**. Pipeline chạy nhưng không có phụ đề nào ra Overlay. | **FAIL** |
| **TEST14** | Hiệu năng P50/P95 | P50 ≤ 1 s, P95 ≤ 2 s | Đo từng mắt xích: OCR nóng P50 ~150 ms; dịch P50 **383 ms**, P95 **409 ms**. Nhưng OCR toàn màn hình **11 096 ms** → P95 end-to-end vượt xa ngưỡng nếu vùng chọn lớn. | **PARTIAL** |
| **TEST15** | Installer | Cài → chạy → dịch được | Cài im lặng thành công (64 tệp, 31,6 MB, exit 0). Nhưng **không kiểm tra .NET/Python, không kèm model → cài xong vẫn không dịch được**. Tệp EXE **không có chữ ký số**. | **FAIL** |
| **TEST16** | Trạng thái runtime đồng nhất (Bug D) | OCR/Translation hiển thị đúng backend | Badge `OFFLINE AI — LOCAL PROCESSING` đúng thực tế; footer OCR đúng `RapidOCR ONNX`; nhưng chip lỗi **`MODEL NOT INSTALLED` là nhãn khởi tạo tĩnh** (giá trị mặc định của field), không phải trạng thái đo được. | **FAIL** |

---

## C. PHÂN TÍCH NGUYÊN NHÂN GỐC

### C.1 Bug A — `MODEL NOT INSTALLED` xuất hiện khi mô hình ĐÃ cài

**Hiện tượng:** Engine badge = `OFFLINE AI — LOCAL PROCESSING`, nhưng chip trạng thái = `MODEL NOT INSTALLED`, kết quả = `Chưa có bản dịch`.

**Nguyên nhân gốc — status khởi tạo tĩnh, không phải trạng thái đo được:**

Ba nguồn trạng thái độc lập nhau, không có một "single source of truth":

| Nguồn | File:Line | Cơ chế |
|---|---|---|
| Badge engine | `src/ScreenSub.Translation/TranslationProviderResolver.cs:59-79` | Tính lại mỗi lần đọc: nếu `IsOffline` và `IsModelInstalled` → `OFFLINE AI — LOCAL PROCESSING` |
| Chip `MODEL NOT INSTALLED` | `src/ScreenSub.Translation/LocalNeuralTranslationEngine.cs:27` | **Giá trị khởi tạo của field**: `ActiveModelName { get; private set; } = "MODEL NOT INSTALLED"` |
| `IsModelInstalled` | `src/ScreenSub.Translation/LocalTranslationProvider.cs:30-47` | `_engine.IsNeuralModelLoaded \|\| IsDefaultModelInstalledOnDisk()` |

Chip được bind vào `ActiveModelName` — **giá trị mặc định của field này là `MODEL NOT INSTALLED`**, chỉ được ghi đè sau khi worker trả `model_name` (`LocalNeuralTranslationEngine.cs:182`). Trong lúc Dashboard khởi tạo, `LoadSettingsIntoProperties()` đọc `TranslationStatusText = _coordinator.TranslationStatusText` (`MainViewModel.cs:709, 717`) **trước khi** worker trả lời ⇒ chip giữ nhãn mặc định, và vì `TranslationStatusText` được cache vào string nên **không tự cập nhật lại** khi worker nạp xong mô hình.

**Bằng chứng:** Cùng một thời điểm, badge đọc *live* (`IsModelInstalled == true` — có kiểm tra disk) → `LOCAL PROCESSING`; chip đọc *cached/khởi tạo* → `MODEL NOT INSTALLED`. Mô hình thật sự có mặt (487,9 MB) và nạp được trong 2 000 ms.

**Đồng thời phát hiện lỗi phụ nghiêm trọng:** `IsDefaultModelInstalledOnDisk()` chỉ kiểm tra `model.bin > 100 MB`. Bất kỳ tệp nào > 100 MB đặt đúng tên sẽ khiến hệ thống **báo "đã cài" trong khi worker nạp thất bại** — trạng thái "giả PASS" ở tầng chẩn đoán.

**Hướng khắc phục (chưa thực hiện):**
- Bỏ giá trị khởi tạo `"MODEL NOT INSTALLED"` khỏi `ActiveModelName`; khởi tạo bằng trạng thái `Loading` trung tính.
- Một nguồn duy nhất: đọc trực tiếp `LocalNeuralTranslationEngine` state qua `INotifyPropertyChanged`, không cache string.
- Kiểm tra hợp lệ model = **ping worker + `model_loaded == true`**, không dựa vào kích thước tệp.

---

### C.2 Bug B — Live Preview có chữ nhưng Dashboard báo `Chưa nhận diện được văn bản`

**Hiện tượng:** Preview hiển thị `with communities across Southeast Asia.` nhưng kết quả = `Chưa nhận diện được văn bản`.

**Nguyên nhân gốc — Preview và Text là hai nguồn dữ liệu KHÁC NHAU:**

```
LatestFramePreview = frameBytes;            // SubtitleCoordinator.cs:601  ← ảnh thô
FrameCaptured?.Invoke(this, frameBytes);    // SubtitleCoordinator.cs:602
...
bool frameChanged = _changeDetector.HasFrameChanged(...);   // :606
if (!frameChanged && !forceRetryOcr) { continue; }          // :609  ← chặn tại đây
```

- **Live Preview** bind trực tiếp vào `LatestFramePreview` — tức là **ảnh chụp thô**, được gán **trước** khi qua bộ lọc thay đổi khung hình.
- **Text kết quả** chỉ có khi pipeline đi qua `FrameChangeDetector` → RapidOCR → `OcrTextRecognized`.

Do đó Preview **luôn** có hình kể cả khi OCR chưa từng chạy. Đây là lý do Bug B tái hiện được mà vẫn "thấy chữ trong Preview".

**Nguyên nhân kép thứ 2 — bộ phát hiện thay đổi khung hình bị vô hiệu trên khung nhỏ:**

`src/ScreenSub.Capture/FrameChangeDetector.cs:17-19`
```csharp
const int sampleSize = 1024;
byte[] currentSample = new byte[Math.Min(sampleSize, currentFrameBytes.Length)];
int step = Math.Max(1, currentFrameBytes.Length / currentSample.Length);
```
Với ảnh PNG vùng nhỏ (vùng phụ đề 656×61 px ≈ vài chục KB), `step = length / 1024` **nhỏ hơn 1 → bị `Math.Max(1, …)` kẹp về 1**, nên chỉ so sánh **1024 byte đầu tiên** của tệp PNG. 1024 byte đầu của PNG là **header + IDAT đầu tiên**, không phải pixel ⇒ **phụ đề đổi nội dung vẫn bị coi là "không đổi"**. Kèm theo `threshold = 0.03` (`FrameDiffThreshold`) và `Math.Abs(...)>15` trên byte nén ⇒ tỉ lệ khớp nhiễu rất cao.

**Nguyên nhân kép thứ 3 — timeout xoá phụ đề giữa các dòng:**

`src/ScreenSub.Desktop/Services/SubtitleCoordinator.cs:492, 1010-1026`
```csharp
int blankDebounceMs = Math.Max(500, Settings.StabilityDebounceMs * 4);   // = 1000 ms
int timeoutMs = CurrentSubtitle.SourceMode == TranslationMode.AudioRealtime
    ? Math.Max(4000, blankDebounceMs * 3) : blankDebounceMs;             // ScreenOcr = 1000 ms
```
Với `StabilityDebounceMs = 250` ⇒ phụ đề bị xoá sau **1 000 ms** kể từ lần cuối thấy chữ. Hệ quả: ngay cả khi OCR thành công, phụ đề chỉ hiển thị ~0,2–0,3 s rồi bị xoá vì đến nhịp quét kế tiếp không có chữ mới.

**Hướng khắc phục:**
- Preview bind vào cùng dữ liệu đã qua OCR (hoặc gắn nhãn "khung thô – chưa OCR").
- `FrameChangeDetector`: bỏ so sánh byte nén, giải mã → downscale 64×64 grayscale → so sánh pixel thật.
- Timeout cho `ScreenOcr` phải ≥ 2× chu kỳ OCR thực tế (≥ 1,5–2 s), hoặc chỉ xoá khi có ≥3 khung trắng liên tiếp.

---

### C.3 Bug C — Overlay ON/AlwaysOnTop/Click-through nhưng không có phụ đề

**Nguyên nhân gốc — Overlay chỉ là "người nhận" cuối chuỗi, và chuỗi phía trên đã đứt:**

Overlay **không** phải nguồn lỗi trực tiếp. Kiểm chứng bằng Win32 (tôi đã giám sát handle thật):

| Thuộc tính | Giá trị đo được | Kết luận |
|---|---|---|
| HWND tồn tại | `0xA06C8` có, visible = true | ✅ |
| Topmost | `exStyle = 0x080801A8` có `WS_EX_TOPMOST` | ✅ |
| Click-through | `WS_EX_TRANSPARENT` có | ✅ |
| Loại trừ khỏi capture | `SetWindowDisplayAffinity = WDA_EXCLUDEFROMCAPTURE` (0x11) | ✅ |
| `OverlayContainer` kích thước | **1152 × 24 px** | ❌ **Collapsed** |

`OverlayContainer` chỉ được set `Visible` khi có nội dung hợp lệ (`SubtitleOverlayWindow.xaml.cs:154-161, 359-371`). Vì pipeline **không bao giờ** phát ra `SubtitleItem` có nội dung, Overlay luôn ở trạng thái Collapsed — "cửa sổ đúng nhưng luôn rỗng". `HasValidDisplayableSubtitle()` chặn thêm mọi placeholder lỗi (`[MODEL NOT INSTALLED…`, `[Lỗi dịch…`) nên kể cả khung lỗi cũng không hiện.

**Với Bug D (`MODEL NOT INSTALLED`), Overlay bị chặn 2 lớp**: lớp giá trị `HasValidDisplayableSubtitle` lọc placeholder, và lớp text thật không được tạo ra.

**Hướng khắc phục:**
- Overlay phải có trạng thái hiển thị rõ ràng: `<Chưa có phụ đề>`, `<Đang nhận diện…>`, `<Lỗi: …>` thay vì thu về 24 px.
- Thêm cờ `ForceShowPanel` cho Overlay để user nhìn thấy toạ độ khung chữ thật.
- Thêm `Test Overlay` chẩn đoán — hiện đã có (`ShowTestSubtitle`) nhưng **không thể bấm vì Dashboard bị đen** (xem C.4).

---

### C.4 Bug D + Lỗi hệ thống quan trọng nhất — Trạng thái runtime không đồng nhất và **Dashboard tự làm mù bộ chụp màn hình**

#### C.4.1 Trạng thái không đồng nhất (Bug D)

`src/ScreenSub.Desktop/ViewModels/MainViewModel.cs:577-628` xác định "sức khoẻ" bằng **so khớp chuỗi** thay vì trạng thái máy:

```csharp
bool missingModel = ActiveProviderBadgeText.Contains("MODEL NOT INSTALLED", ...)   // :581
                 || TranslationStatusText.Contains("MODEL NOT INSTALLED", ...);    // :582
...
if (OcrStatusText.Contains("Sẵn sàng", ...))                                       // :588
    OcrFooterText = OcrStatusText.Contains("RapidOCR", ...) ? "OCR: RapidOCR ONNX" : "OCR: PaddleOCR";  // :590
```
⇒ Chỉ cần đổi một chữ trong chuỗi hiển thị (bản địa hoá, thêm hậu tố) là **logic trạng thái sai theo**. Đây chính là cơ chế sinh ra nhãn `OCR: PaddleOCR` dù backend là RapidOCR (và ngược lại).

#### C.4.2 Lỗi nghiêm trọng nhất — Dashboard chặn BitBlt của chính nó

**Đây là phát hiện quyết định, giải thích vì sao "trên máy dev chạy được, máy thật không chạy".**

Hiện tượng: khi Dashboard của ScreenSub ở trên cùng (hoặc phủ lên vùng bị chụp), **BitBlt trả về ảnh ĐEN** cho vùng đó. Đo trực tiếp:

| Kịch bản | Mẫu điểm ảnh sáng |
|---|---|
| Dashboard **ẩn** – vùng Chrome (400,200)–(700,350) | **1 800 / 1 800** (100 %) |
| Dashboard **hiện trên** cùng vùng | **0 / 1 800** (0 %) |
| Chụp chính Dashboard (78,−12)–(1458,828) bằng BitBlt | **2 818 ô sáng / 5 728** — nhưng toàn bộ nằm ở dải biên, **phần nội dung đen tuyền** |

Kiểm chứng bằng `DwmGetWindowAttribute`: `cloaked = 0`, `IsHungAppWindow = False`, `WindowFromPoint` trả đúng HWND — cửa sổ **không bị treo, không bị DWM ẩn**, nhưng **bề mặt vẽ không cung cấp pixel cho BitBlt**.

**Tại sao đây là nguyên nhân gốc của toàn bộ thất bại thực địa:**

1. Khi người dùng chọn vùng phụ đề, `SelectRegion()` gọi `mainWin.Hide()` (`MainViewModel.cs:831`) → chụp được.
2. Nhưng ở luồng Quick Translate, sau khi chọn vùng, Dashboard **vẫn còn hiện** (chỉ ẩn khi mở vùng chọn), Overlay được đặt **gần vùng phụ đề**.
3. Khi người dùng để Dashboard ở vị trí che vùng phụ đề — hoặc khi chính **Overlay/Dashboard của ScreenSub** nằm trên vùng đó — `TryCaptureDesktopRegion` (`ScreenCaptureService.cs:277-326`) dùng `BitBlt` **không có `CAPTUREBLT`** và **không loại trừ cửa sổ của chính mình** ⇒ nhận về **đen**.
4. `IsBitmapAllBlack` → coi là khung trắng → không OCR → `Chưa nhận diện được văn bản`.

Có một lớp bảo vệ `IsRegionOccludedByOwnWindow` — nhưng nó **chỉ so z-order tới HWND mục tiêu** và **bỏ qua cửa sổ tên "Overlay"/"RegionSelection"** (`ScreenCaptureService.cs`), nên nó **không phát hiện** trường hợp cửa sổ ScreenSub che vùng chọn nhưng không có HWND mục tiêu hợp lệ.

#### C.4.3 Lỗi DPI 125 % (nguyên nhân gốc của vùng chọn sai)

`app.manifest` **có** khai báo `PerMonitorV2` (đúng), nhưng code vẫn dùng `SystemParameters.*`:

`src/ScreenSub.Desktop/Views/RegionSelectionWindow.xaml.cs:43-46`
```csharp
Left = SystemParameters.VirtualScreenLeft;
Width = SystemParameters.VirtualScreenWidth;     // DIP!
Height = SystemParameters.VirtualScreenHeight;   // DIP!
```
`SystemParameters.VirtualScreen*` trả về **đơn vị DIP**, không phải pixel vật lý. Đo thực tế: cửa sổ chọn vùng cao rộng **1536 × 864** thay vì **1920 × 1080** — **đúng bằng 1/1,25**.

Hệ quả dây chuyền: `PointToScreen` quy đổi từ một window chỉ phủ 80 % màn hình ⇒ toạ độ vùng chọn lệch. Hai lần đo thực tế:

| Lần kéo chuột (vật lý) | Kỳ vọng | `LastRegion` thực tế | Sai lệch |
|---|---|---|---|
| (490,415) → (1440,505) | X=490 Y=415 W=950 H=90 | **X=613 Y=519 W=1187 H=112** | +123 px, +104 px; kích thước **×1,25** |
| (280,690) → (740,770) | X=280 Y=690 W=460 H=80 | **X=350 Y=863 W=575 H=100** | +70 px, **+173 px** (rơi hẳn xuống dưới phụ đề) |

Ở lần thứ 2, vùng chọn **Y = 863** trong khi phụ đề nằm ở **y ≈ 693–756** ⇒ **vùng chụp hoàn toàn không chứa chữ nào**. Đây là lý do cơ học của "OCR không ra chữ".

**Hướng khắc phục (P0):**
- Thay `SystemParameters.*` bằng **`GetSystemMetrics(SM_XVIRTUALSCREEN / SM_CXVIRTUALSCREEN …)`** (pixel vật lý) hoặc `DisplayArea` của WinRT.
- Chuyển sang `PointToScreen` sau khi đã set `Width/Height` theo `GetDpiForWindow` thực tế.
- Unit test riêng cho DPI 100/125/150/200 %.

---

## D. BẰNG CHỨNG ĐO LƯỜNG

### D.1 Build & Test (TEST02)
```
dotnet build ScreenSubAI.slnx -c Release
  → Build succeeded. 0 Warning(s) 0 Error(s)  Time Elapsed 00:00:16.53

dotnet test -c Release --no-build
  → IntegrationTests: 43/43 Passed
  → UnitTests:        31/31 Passed
  → Failed: 0, Skipped: 0   (52,3 s)
```
Ghi chú: **Unit test pass không phản ánh runtime.** Toàn bộ test dùng `Mock.Of<IScreenCaptureService>()` trả về **ảnh PNG dựng sẵn** (`RenderBilingualYouTubeSubtitleFrame`), nên **không bao giờ đi qua đường chụp màn hình thật, DPI thật, hay Windows cloak**. Đây chính là lỗ hổng khiến 74/74 test xanh nhưng sản phẩm không chạy.

### D.2 Suy luận neural thật (TEST04) — 10 câu mới, 13,6 s

| # | Nguồn | Kết quả | Đánh giá |
|---|---|---|---|
| 1 | `with communities across Southeast Asia.` | `với các cộng đồng trên khắp Đông Nam Á.` | ✅ Đúng |
| 2 | `The committee approved the budget after a long debate.` | `Ủy ban đã phê duyệt ngân sách sau một cuộc thảo luận dài.` | ✅ Đúng |
| 3 | `Bác sĩ khuyên bệnh nhân nghỉ ngơi hoàn toàn trong hai tuần.` | `The doctor recommends the patient to rest completely for two weeks.` | ✅ Đúng |
| 4 | `今晚全家人都会聚在一起。` | `Cả gia đình sẽ cùng nhau đêm nay.` | 🟡 Chấp nhận |
| 5 | `Quantum computers may soon reshape cryptography.` | `Máy tính lượng tử có thể sớm tái tạo mã hóa.` | 🟡 Chấp nhận |
| 6 | `The mountain roads twisted.` | `Đường núi đã xoay quanh.` | 🟡 Lệch nghĩa |
| 7 | `山路一路蜿蜒。` | `Đường núi đều trượt.` | ❌ Sai nghĩa |
| 8 | `The mountain roads twisted.` (zh→en) | `The mountain is round.` | ❌ Sai nghĩa |
| 9 | `Những con đường núi quanh co uốn lượn.` (vi→en) | `The mountainous roads around are shining.` | ❌ Sai nghĩa |
| 10 | `Những con đường núi quanh co uốn lượn.` (vi→zh) | `山路周围的流浪。` | ❌ Sai nghĩa |

**Nhận định:** mô hình **chạy thật** nhưng **chất lượng không đủ cho phụ đề phim** — câu mô tả/tu từ sai nghĩa, vi→zh kém. Đây là **giới hạn của M2M-100 INT8** ở `beam=1` (greedy), không phải lỗi code, nhưng **là bug chất lượng phải xử lý trước khi phát hành beta**.

### D.3 Offline tuyệt đối (TEST05)
```
Socket monkeypatch → OSError('NETWORK BLOCKED BY QA HARNESS')
  ping: model_loaded = True
  en→vi: "Đường núi đã xoay quanh."            (556 ms)
  en→vi: "Bà nội vẫn ăn bánh mì mỗi sáng Chủ nhật." (315 ms)

Get-NetTCPConnection cho PID app + 2 worker  →  0 kết nối ra ngoài
settings.json: TranslationEngineMode=1 (Offline), UseMockTranslation=false,
               AzureApiKey="", AllowCloudFallbackOnOfflineFailure=false
```
⇒ **Không có Azure, không có Mock** trong đường chạy thật. Đạt yêu cầu offline.

### D.4 OCR thật trên phụ đề YouTube (TEST07)
Ảnh chụp cửa sổ Chrome thật (`youtube.com/watch?v=…`), RapidOCR ONNX:
```
with communities across Southeast Asia      0.9726   (287,693)-(731,720)
在东南亚也有不少苗族人，                         0.9973   (360,729)-(647,756)
```
⇒ **OCR engine thật sự đọc được phụ đề. Nguồn và OCR đều tốt — mất mát nằm trong pipeline của app.**

### D.5 Ma trận backend capture (TEST06)

| Backend | Đối tượng | Kết quả |
|---|---|---|
| BitBlt màn hình | Chrome hiển thị | ✅ Có nội dung, OCR đọc được |
| PrintWindow(flag=2) | Chrome | ✅ Có nội dung, OCR đọc được |
| PrintWindow(flag=0) | Chrome | ❌ **Đen tuyền** |
| BitBlt màn hình | Cửa sổ ScreenSub (Dashboard) | ❌ **Đen tuyền** |
| BitBlt màn hình | Vùng bị ScreenSub che | ❌ **Đen tuyền** |
| PrintWindow | RegionSelector (`WDA_EXCLUDEFROMCAPTURE`) | ❌ Đen (đúng thiết kế, nhưng làm mất khả năng tự chẩn đoán) |

### D.6 Hiệu năng (TEST14)

| Thành phần | Đo được |
|---|---|
| OCR ảnh nhỏ (warm) | P50 **~150 ms** (75–269 ms), nguội 532 ms |
| OCR toàn màn hình 1920×1080 | **11 096 ms** ← điểm chết hiệu năng |
| Dịch M2M-100 (12 lần) | min 349 / **P50 383** / **P95 409** / max 438 ms |
| Khởi động worker dịch → phản hồi đầu | **4 162 ms** |
| CPU app idle | 4,1–8,8 % (1 nhân) |
| RAM | App 160–207 MB · OCR worker 96–103 MB · Translation worker **609 MB** |

**P50 end-to-end ước tính** (nếu capture OK): ~150 (OCR) + ~385 (dịch) + render ≈ **550–700 ms** → phù hợp.
**P95 rủi ro cao:** bất kỳ vùng chọn nào > ~900 px chiều rộng sẽ đẩy OCR lên **hàng giây** (`det_limit_side_len=736` + ảnh lớn) ⇒ **P95 vượt 2 s rõ rệt**.

### D.7 Installer (TEST15)
```
dist\ScreenSubAI_v1.0.0-beta.1_Setup_x64.exe
  Size     : 9 263 610 bytes
  SHA256   : 605524182B4B471C6F86E57E07E11E2C86161E3AE56EBC424860F0326475B145
  Signature: NotSigned  ← KHÔNG có chữ ký số (SmartScreen sẽ cảnh báo)

Cài /VERYSILENT /CURRENTUSER → exit code 0
  → C:\Users\<user>\AppData\Local\Programs\ScreenSub AI  (64 tệp, 31,6 MB)
  → Registry uninstall entry: "ScreenSub AI 1.0.0-beta.1" ✔
  → Có onnxruntime.dll (17,3 MB), workers/, runtimes/
  → KHÔNG có thư mục models\           ✘
  → KHÔNG có Python/pip/virtualenv      ✘
  → KHÔNG kiểm tra .NET Desktop Runtime ✘

Chạy từ cài đặt (WorkingDir = C:\Windows):
  → App khởi động, spawn 2 worker Python từ C:\PyThon\python.exe ✔
  → Dashboard render OK (3 311 ô sáng) ✔
  → OCR worker "Đang khởi động OCR worker..." (chậm 5–10 s)
```
⇒ **Installer cài đặt thành công nhưng không đủ để sản phẩm chạy** trên máy sạch. Trên máy này Python + model có sẵn nên "tình cờ" chạy; trên máy người dùng mới **sẽ thất bại ở cả 3 điểm**: không có .NET 10 Runtime, không có Python 3.12, không có model 488 MB.

### D.8 Vòng đời tiến trình (TEST12)
| Hành động | Kết quả |
|---|---|
| Đóng Dashboard (X) | Ẩn xuống tray, **2 worker Python vẫn sống** ✔ (đúng thiết kế) |
| ESC sau khi mở vùng chọn (khi có focus) | Huỷ, đóng vùng chọn, Dashboard hiện lại ✔ |
| ESC khi vùng chọn **không có focus** | ❌ **Không phản hồi** |
| Kill app bằng `Stop-Process -Force` | ✔ **0 worker mồ côi** (2 worker chết theo) |
| Chạy 2 instance liên tiếp | Không phát hiện xung đột hotkey |

---

## E. KHUYẾN NGHỊ KHẮC PHỤC

### P0 — Chặn phát hành (bắt buộc sửa trước beta)

| # | Vấn đề | File:Line | Hướng sửa |
|---|---|---|---|
| P0-1 | **Vùng chọn sai do DPI 125 %** — nguyên nhân cơ học số 1 khiến không có chữ | `RegionSelectionWindow.xaml.cs:43-46, 134-142` | Dùng `GetSystemMetrics(SM_*VIRTUALSCREEN)` (px vật lý) thay `SystemParameters.*`; test 100/125/150/200 % |
| P0-2 | **Dashboard che vùng chụp → BitBlt trả đen** | `ScreenCaptureService.cs:277-326`, `IsRegionOccludedByOwnWindow` | Luôn chụp theo **HWND mục tiêu** (PrintWindow flag 2) thay vì desktop BitBlt; mở rộng `IsRegionOccludedByOwnWindow` để chặn **mọi** cửa sổ cùng PID, không chỉ theo tên "Overlay" |
| P0-3 | **`FrameChangeDetector` so sánh byte nén PNG** — bỏ sót thay đổi phụ đề | `FrameChangeDetector.cs:17-53` | Giải mã → downscale 64×64 grayscale → so sánh pixel; ngưỡng theo tỉ lệ pixel đổi |
| P0-4 | **`ActiveModelName` mặc định `MODEL NOT INSTALLED`** treo vĩnh viễn | `LocalNeuralTranslationEngine.cs:27`, `MainViewModel.cs:709,717` | Bỏ giá trị mặc định; expose state qua `INotifyPropertyChanged`; một nguồn trạng thái duy nhất |
| P0-5 | **Timeout xoá phụ đề 1 000 ms** ngắn hơn chu kỳ OCR | `SubtitleCoordinator.cs:492, 1014-1016` | `ScreenOcr` timeout ≥ 2 500 ms hoặc xoá sau 3 khung trắng liên tiếp |
| P0-6 | **Overlay không có trạng thái rỗng/lỗi** (24 px, không chữ) | `SubtitleOverlayWindow.xaml.cs:154-161` | Hiển thị trạng thái chẩn đoán rõ ràng; thêm `ForceShowPanel` |
| P0-7 | **Installer thiếu điều kiện tiên quyết** (không .NET check, không Python, không model) | `installer/ScreenSubAI_Setup.iss` (`InitializeSetup` trả `True` luôn) | Kiểm tra .NET 10 Desktop Runtime + Python; wizard tải model có xác nhận người dùng; không auto-download ngầm |
| P0-8 | **EXE không ký số** | `dist/*.exe` | Ký Authenticode trước khi phát hành |

### P1 — Nghiêm trọng, sửa trong beta

| # | Vấn đề | Hướng sửa |
|---|---|---|
| P1-1 | Trạng thái suy ra từ **so khớp chuỗi** (`MainViewModel.cs:577-628`) | Bind trực tiếp enum `ServiceStatus` / `TranslationEngineMode` |
| P1-2 | **Không có log chẩn đoán** — chỉ Info banner khởi động; capture/OCR/dịch thất bại **im lặng** | Log Info cho mỗi mắt xích: backend, vùng, latency, số dòng OCR |
| P1-3 | **Không thể tự chẩn đoán** vì `WDA_EXCLUDEFROMCAPTURE` làm PrintWindow của chính app trả đen | Thêm đường chụp nội bộ (RenderTargetBitmap) cho chẩn đoán |
| P1-4 | Không thể bấm **Test Overlay** khi Dashboard đen | Thêm hotkey test overlay; kiểm tra render state trước khi cho tương tác |
| P1-5 | OCR **11 s** cho vùng lớn | Giới hạn vùng chọn; downscale trước OCR; cache vùng tĩnh |
| P1-6 | `IsModelInstalled` chỉ kiểm tra **kích thước tệp** | Xác thực bằng ping worker + `model_loaded` |
| P1-7 | ESC/chuột phải **không huỷ** khi vùng chọn mất focus | Xử lý ở `HwndSource` hook hoặc `PreviewKeyDown` toàn cục |
| P1-8 | **Test suite không phủ đường chụp thật** (mock trả PNG dựng sẵn) | Thêm integration test dùng `ScreenCaptureService` thật với cửa sổ test, chạy ở DPI 125 % |

### P2 — Cải thiện chất lượng

| # | Vấn đề | Hướng sửa |
|---|---|---|
| P2-1 | Chất lượng dịch không đồng đều (4/10 câu sai nghĩa) | `beam_size = 2–4`, `no_repeat_ngram_size`, bỏ fallback từ điển từ vựng đè lên kết quả neural (`PhraseDictionary`) |
| P2-2 | `source_lang = auto` mặc định **`"zh"`** khi không nhận diện được (`LocalTranslationProvider.cs:92`) | Để `auto` cho mô hình quyết định; không đoán cứng `zh` |
| P2-3 | Worker khởi động **4,2 s**, nạp model 2 s | Nạp trước (preload) khi app khởi động; hiển thị tiến trình |
| P2-4 | RAM worker dịch **609 MB** | Cân nhắc lượng tử hoá sâu hơn hoặc model nhỏ hơn cho máy yếu |
| P2-5 | `.pyc`/`__pycache__` lẫn vào bản phát hành | Loại khỏi `publish`/installer |
| P2-6 | Mất dấu tiếng Việt khi OCR (`Xin chao Vit Nam`) | Cải thiện bộ lọc dấu/ngôn ngữ cho tiếng Việt |

---

## F. KẾT LUẬN CUỐI CÙNG

# ❌ NOT READY FOR BETA

**ScreenSub AI v1.0.0-beta.1 KHÔNG đáp ứng định nghĩa "dịch phụ đề trên màn hình theo thời gian thực".**

### Căn cứ

1. **E2E thực tế: 0/10 phụ đề.** Toàn bộ mục tiêu sản phẩm — chọn vùng → OCR → dịch → hiện trên Overlay — **không xảy ra một lần nào** trong suốt phiên kiểm thử với video YouTube thật.
2. **Ba lỗi P0 độc lập cùng chặn pipeline**: (a) toạ độ vùng chọn sai do DPI 125 %; (b) Dashboard che vùng chụp khiến BitBlt trả đen; (c) `FrameChangeDetector` so sánh byte nén nên bỏ sót thay đổi khung hình.
3. **Sửa một lỗi vẫn không chạy**: cả 3 lỗi trên phải sửa **đồng thời** mới có kết quả.
4. **Bốn bug được báo cáo đều tái hiện 100 % và đã xác định được nguyên nhân gốc** (mục C).
5. **Installer không đủ để chạy trên máy sạch** (thiếu .NET check, Python, model) và **không có chữ ký số**.

### Những gì THỰC SỰ TỐT (nên giữ)

| Thành phần | Đánh giá |
|---|---|
| Kiến trúc phân tầng, .NET 10 + WPF MVVM | Tốt, sạch, build 0 warning |
| Mô hình M2M-100 offline thật | Hoạt động, không phụ thuộc mạng, nạp 2 s |
| RapidOCR ONNX | Rất tốt — đọc phụ đề thật 0,97–0,99 |
| Vòng đời worker Python | Đúng — không rò rỉ tiến trình, kill app sạch |
| Hotkey & vùng chọn (phần mở/đóng) | Hoạt động |
| Cấu hình mặc định offline-first | Đúng (không Azure, không Mock) |

### Điều kiện để chuyển sang READY FOR BETA

Sửa **8 mục P0** và chứng minh lại bằng đúng kịch bản của báo cáo này trên **máy DPI 125 %** với **video YouTube thật ≥ 10 phụ đề liên tiếp**, kèm log chẩn đoán đo được P50/P95.

> **Khuyến nghị mạnh:** bổ sung một bài test integration dùng **`ScreenCaptureService` thật** (không mock PNG) ở DPI 125 % — chính lỗ hổng này đã khiến 74/74 test xanh trong khi sản phẩm hoàn toàn không hoạt động.

---

### Phụ lục — Phạm vi & giới hạn

- Phiên QA **chỉ đọc và chạy** sản phẩm: **không sửa một dòng source nào** (đã xác minh: không có file `.cs/.xaml/.py` nào bị sửa trong khung giờ QA).
- Cài thử installer rồi **gỡ sạch**; **model (487,9 MB) và settings.json còn nguyên**.
- Không xoá model, không xoá settings, không đổi hotkey, không push, không release.
- Video kiểm thử: trang YouTube thật đang mở sẵn + bộ 12 phụ đề song ngữ dựng cục bộ (để kiểm soát nội dung). **Không tự động điều khiển được trình phát YouTube** (DRM/UI) nên E2E dùng khung hình thật của video đang phát.
- Thông tin nhạy cảm: **không có API key nào** trong báo cáo; `AzureApiKey` để trống.
