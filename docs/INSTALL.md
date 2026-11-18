# Hướng Dẫn Cài Đặt ScreenSub AI (Windows Beta v1.0.0)

Tài liệu hướng dẫn cài đặt và thiết lập môi trường cho ứng dụng **ScreenSub AI — Windows Realtime Subtitle Translator**.

---

## 1. Yêu Cầu Hệ Thống

| Thành phần | Yêu cầu tối thiểu | Khuyến nghị cho trải nghiệm tốt nhất |
|---|---|---|
| **Hệ điều hành** | Windows 10 x64 (Build 19041+) / Windows 11 | Windows 11 x64 |
| **Bộ xử lý (CPU)** | Intel Core i3 / AMD Ryzen 3 (hỗ trợ AVX2) | Intel Core i5 / AMD Ryzen 5 trở lên |
| **Bộ nhớ RAM** | 8 GB RAM | 16 GB RAM DDR4/DDR5 |
| **Dung lượng đĩa trống** | 2 GB đĩa trống | 5 GB đĩa SSD trống (kèm các mô hình AI) |
| **Card đồ họa (GPU)** | GPU tích hợp (Intel UHD / AMD Radeon) | NVIDIA GeForce GTX 1650 trở lên |

---

## 2. Các Bước Cài Đặt Trên Máy Windows Sạch (Clean Windows)

### Phương án đóng gói (.NET Self-Contained + Tùy chọn Python Portable / Consented Setup)
Bộ cài chuẩn `ScreenSubAI_v1.0.0-beta.1_Setup_x64.exe` được build ở chế độ **`.NET 10 Self-Contained (win-x64)`** nên **không bắt buộc người dùng cài trước .NET 10 Desktop Runtime**. (Có thể build bản nhẹ framework-dependent bằng `build_installer.ps1 -SelfContained $false`).

### Bước 1: Chạy File Cài Đặt
1. Chạy `ScreenSubAI_v1.0.0-beta.1_Setup_x64.exe` (tạo từ `installer\build_installer.ps1`).
2. Đọc và xác nhận thông báo bản quyền bên thứ ba (`docs\THIRD_PARTY_LICENSES.txt`, gồm **MIT**, **Apache-2.0** và **OpenRAIL-M** cho `VietAI/envit5-translation`).
3. Chọn thư mục cài đặt và nhấn **Install**.

### Bước 2: Môi Trường Python Cho RapidOCR & Dịch Neural Cục Bộ
ScreenSub AI tự động ưu tiên tìm kiếm Python runtime cục bộ đặt cạnh ứng dụng trước (`{app}\python\python.exe` hoặc `{app}\runtime\python\python.exe`), rồi mới tới Python hệ thống (`3.10`, `3.11`, `3.12`):
- **Trường hợp A (Portable Python đóng gói sẵn):** Nếu gói phát hành kèm thư mục `python\` đặt cạnh `ScreenSub.Desktop.exe`, ứng dụng chạy ngay **không cần thao tác thêm**.
- **Trường hợp B (Hệ thống chưa có Python):**
  1. Cài đặt **Python 3.10 – 3.12 (x64)** từ [python.org](https://www.python.org/downloads/) (tích *"Add python.exe to PATH"*).
  2. Chạy script hỗ trợ có xác nhận đồng ý trong thư mục cài đặt:
     ```powershell
     powershell -ExecutionPolicy Bypass -File "setup_python_env.ps1"
     ```
     Script sẽ kiểm tra gói đang có và **chỉ tải/cài qua `pip` khi bạn nhấn `Y` xác nhận** (nội dung từ `workers\paddle-ocr\requirements.txt` và `workers\translation\requirements.txt`).

### Bước 3: Mô Hình AI Cục Bộ & Bảo Toàn Dữ Liệu
ScreenSub AI **không tự động tải các mô hình dung lượng lớn khi chưa được người dùng cho phép**:
- Thư mục mặc định: `%LocalAppData%\ScreenSubAI\Models\translation\`.
- Xem chi tiết checksum SHA-256, danh sách file tokenizer bắt buộc và bản quyền tại [MODEL_GUIDE.md](MODEL_GUIDE.md) và [THIRD_PARTY_LICENSES.txt](THIRD_PARTY_LICENSES.txt).
- **Kích hoạt / Rollback Controlled Beta Router (`EN <-> VI`):**
  - Mặc định Production dùng `M2M-100` (`EnableOfflineMultiModelRouter = false`).
  - Kích hoạt thử nghiệm Beta `EnViT5` (`EN <-> VI`) qua biến môi trường opt-in `SCREENSUB_BETA_ROUTER=1`, và hoàn nguyên tức thì bằng cách gỡ biến môi trường hoặc chuyển profile rollback mà không cần xóa file model.

---

## 3. Chạy Bản Portable / Bản Build Sẵn

Nếu không muốn dùng trình cài đặt, bạn có thể chạy trực tiếp từ thư mục publish:
1. Mở thư mục `publish/ScreenSubAI/`.
2. Nhấp đúp vào `ScreenSub.Desktop.exe`.
3. Ứng dụng sẽ tự động khởi động và lưu cấu hình vào `%LocalAppData%\ScreenSubAI`.

---

## 4. Gỡ Bỏ Ứng Dụng (Uninstall)

- Vào **Windows Settings** → **Apps** → **Installed Apps** → Tìm **ScreenSub AI** → Chọn **Uninstall**.
- Hoặc chạy file `unins000.exe` trong thư mục cài đặt.
- **Bảo toàn dữ liệu:** Bộ gỡ cài đặt được thiết kế để **không bao giờ xóa** các mô hình AI đã tải trong `%LocalAppData%\ScreenSubAI\Models` và cài đặt cá nhân của bạn, giúp bạn nâng cấp phiên bản mới mà không phải tải lại mô hình.

