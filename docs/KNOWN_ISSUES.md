# Các Vấn Đề Đã Biết & Lưu Ý Kỹ Thuật (Known Issues)

Tài liệu này ghi nhận minh bạch các đặc tính, hạn chế kỹ thuật và giải pháp xử lý trong phiên bản **ScreenSub AI Windows Beta v1.0.0**.

---

## 1. Dịch Thuật & Ngôn Ngữ (Translation Nuances)

### 1.1. Dịch Nghĩa Đen Của Thành Ngữ Dân Gian (Idiom Literalism)
- **Hiện tượng:** Một số thành ngữ tiếng Anh hoặc tiếng Trung cổ có thể bị mô hình dịch theo sát nghĩa đen từng chữ.
  - *Ví dụ:* `"It's raining cats and dogs"` → `"Bây giờ có mưa mèo và chó ở bên ngoài"` thay vì `"Trời mưa như trút nước"`.
  - *Ví dụ:* `"Piece of cake"` trong ngữ cảnh đơn lẻ có thể dịch thành món bánh thay vì `"Dễ như ăn bánh"`.
- **Nguyên nhân:** Mô hình M2M-100 là mô hình Seq2Seq tổng quát dựa trên dữ liệu song ngữ đa quốc gia, ưu tiên bảo toàn cấu trúc từ ngữ khi không có ngữ cảnh văn cảnh rộng lớn trước đó.
- **Kế hoạch tương lai:** Bổ sung bộ lọc tiền xử lý thành ngữ phổ biến (Idiom Normalizer Layer) trước khi đưa vào encoder.

### 1.2. Chuyển Tự Danh Từ Riêng Tiếng Trung Sang Hán-Việt
- **Hiện tượng:** Tên các nhân vật nổi tiếng trong văn học cổ điển Trung Quốc (như *Tôn Ngộ Không, Gia Cát Lượng*) hoặc địa danh Việt Nam viết bằng chữ Hán (*岘港 - Đà Nẵng, 芽庄 - Nha Trang*) có thể bị M2M-100 phiên âm sang Pinyin hoặc dịch nghĩa ký tự thay vì dùng âm Hán-Việt truyền thống.
- **Khuyến nghị:** Đối với phim lịch sử hoặc phim kiếm hiệp, người dùng có thể kích hoạt tùy chọn chuyển đổi chữ Hán giản thể/phồn thể tích hợp trong ScreenSub AI.

### 1.3. Từ Đa Nghĩa Khi Thiếu Ngữ Cảnh (Context Ambiguity)
- **Hiện tượng:** Khi câu phụ đề chỉ có 1–2 từ đơn lẻ (ví dụ: từ `"Bank"`), mô hình sẽ suy luận theo xác suất thống kê cao nhất (ngân hàng tài chính) thay vì bờ sông.
- **Giải pháp:** Khi chọn vùng nhận diện (`Ctrl + Alt + Q`), hãy chọn trọn vẹn cả câu hoặc cụm từ hoàn chỉnh trên video để AI có đủ ngữ cảnh dịch chuẩn xác nhất.

---

## 2. Hệ Thống & Phần Cứng (Hardware & System)

### 2.1. Yêu Cầu Card Đồ Họa NVIDIA Cho CTranslate2 GPU Acceleration
- **Hiện tượng:** Khi chạy suy luận mô hình M2M-100 bằng CTranslate2 trên GPU NVIDIA, máy tính cần cài đặt đầy đủ CUDA Toolkit (phiên bản 12.x) và thư viện cuDNN.
- **Cơ chế an toàn:** Nếu máy tính không có GPU NVIDIA hoặc thiếu thư viện CUDA, ScreenSub AI sẽ tự động kích hoạt **CPU Fallback** với tập lệnh AVX2 và lượng tử hóa INT8. Trải nghiệm dịch thuật vẫn duy trì độ trễ mượt mà (~300–400 ms) mà không phát sinh bất kỳ lỗi crash nào.

### 2.2. Chế Độ Âm Thanh Độc Quyền (WASAPI Exclusive Mode)
- **Hiện tượng:** Thu âm hệ thống (System Audio Loopback) hoạt động ở chế độ chia sẻ (Shared Mode). Nếu một phần mềm nghe nhạc chuyên dụng (như Foobar2000, Tidal) bật chế độ độc quyền (WASAPI Exclusive / ASIO), luồng âm thanh Windows có thể bị chặn thu.
- **Khắc phục:** Chuyển phần mềm phát nhạc sang chế độ DirectSound hoặc WASAPI Shared Mode.

---

## 3. Worker Lifecycle, Tường Lửa & Giới Hạn Bản Controlled Beta (`v1.0.0-beta.2`)

### 3.1. Cảnh Báo Windows Defender / Tường Lửa Lần Đầu Khởi Động
- Khi khởi động lần đầu, Windows có thể hiển thị hộp thoại hỏi quyền mạng cho tiến trình Python worker. Bạn có thể nhấn **Cancel** hoặc **Allow** đều được, vì các worker OCR và Offline Translation hoạt động hoàn toàn nội bộ qua stdin/stdout (không lắng nghe bất kỳ cổng mạng mở nào ra ngoài).

### 3.2. Nhận Diện OCR Chữ Hán Phồn Thể (`zh-Hant`) Nhiều Nét
- Mô hình `PP-OCRv4` mặc định hiện có 51 ký tự Phồn thể ngoài từ điển (OOV) trên bộ ảnh thử nghiệm Phồn thể chuyên sâu. Khi xem video có phụ đề Phồn thể kèm giọng đọc tiếng Trung, khuyến nghị bật **Hybrid Mode (`SourceLanguage = Chinese`)** để Whisper (`ggml-base.bin`) hỗ trợ nhận diện giọng nói khi phụ đề màn hình thiếu nét.

### 3.3. Độ Trễ Whisper Ở Chế Độ Tự Động Nhận Diện Ngôn Ngữ (`Auto`)
- Trên CPU tầm trung (`i5-10300H`), khi cả `SourceLanguage` và `SpeechLanguage` đều để `Auto`, Whisper cần chạy thêm lượt nhận diện ngôn ngữ (`P50 ~ 2,04 s`). Khuyến nghị chọn trực tiếp `SourceLanguage = English` hoặc `Chinese` khi đã biết ngôn ngữ của video để giảm độ trễ Whisper xuống **`~670 ms` (P50)**.

### 3.4. Các Cổng Nghiệm Thu Đang Chờ Trước Khi Phát Hành Wide Beta
- Bản `v1.0.0-beta.2` hiện ở trạng thái **Controlled Beta**. Ba hạng mục nghiệm thu còn ở trạng thái `PENDING`: (1) kiểm thử bộ cài trên máy ảo Windows 10/11 hoàn toàn sạch chưa có Python/VC++, (2) kiểm thử tương tác đa màn hình khác mức DPI và game Borderless Fullscreen, (3) chạy Soak Test liên tục đủ 60 phút (`0/15/30/45/60m`).

