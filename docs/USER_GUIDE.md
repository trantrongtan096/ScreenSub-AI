# Hướng Dẫn Sử Dụng ScreenSub AI (Windows Beta)

Chào mừng bạn đến với **ScreenSub AI** — Ứng dụng dịch phụ đề màn hình và âm thanh thời gian thực chuyên nghiệp cho Windows.

---

## 1. Tổng Quan Giao Diện Dashboard

Giao diện Dashboard gồm 3 khu vực chính:
1. **Thanh Điều Khiển Bên Trái (Quick Controls):**
   - **Bắt đầu dịch / Tạm dừng / Dừng:** Điều khiển luồng xử lý thời gian thực.
   - **Nguồn chụp:** Chọn toàn màn hình hoặc một cửa sổ ứng dụng cụ thể (YouTube, VLC, Trình duyệt).
   - **Chế độ vùng dịch:**
     - *Vùng cố định (Fixed Region):* Dịch vùng chữ nhật bạn đã chọn trên màn hình.
     - *Theo cửa sổ (Follow Window):* Vùng phụ đề tự động di chuyển theo cửa sổ khi bạn phóng to/thu nhỏ hoặc kéo cửa sổ.
   - **Chọn vùng nhận diện:** Bấm nút hoặc nhấn phím tắt toàn cục **`Ctrl + Alt + Q`**.
   - **Ngôn ngữ gốc & Ngôn ngữ đích:** Chọn ngôn ngữ nguồn (Anh, Trung, Việt hoặc Tự động nhận diện) và ngôn ngữ cần hiển thị phụ đề.

2. **Khu Vực Xem Trước (Live Preview & Realtime Result) Ở Giữa:**
   - **Xem trước vùng dịch:** Hiển thị trực tiếp khung hình phụ đề đang được chụp.
   - **Kết quả realtime:** Hiển thị 3 dòng phụ đề (Văn bản gốc, Bản dịch tiếng Anh, Bản dịch tiếng Việt) cập nhật trực tiếp theo mili-giây.
   - **Huy hiệu trạng thái:** Báo rõ nguồn dịch đang chạy (`OFFLINE AI — LOCAL PROCESSING` hoặc `ONLINE TRANSLATOR`).

3. **Cài Đặt Overlay (Phụ Đề Nổi) Bên Phải:**
   - **Bật/Tắt hiển thị Overlay.**
   - **Kiểu hiển thị:** Song ngữ (2 dòng), Tam ngữ (3 dòng) hoặc Đơn ngữ (1 dòng).
   - **Tùy chỉnh font chữ:** Tăng/giảm cỡ chữ, chọn màu chữ (#FFFFFF, vàng, cyan...), thanh trượt độ đậm nền phụ đề.
   - **Chế độ tương tác:**
     - *Khóa vị trí Overlay:* Cố định khung phụ đề, tránh vô tình di chuyển.
     - *Click-through (Xuyên chuột):* Cho phép click xuyên qua phụ đề để tương tác với video bên dưới.
     - *Luôn hiển thị trên cùng (Always on Top):* Giữ phụ đề luôn nổi phía trên các cửa sổ khác.
     - *Chỉnh sửa vị trí Overlay:* Mở chế độ kéo thả trực quan để đặt phụ đề vào vị trí mong muốn.

---

## 2. Hướng Dẫn Các Tình Huống Sử Dụng Phổ Biến

### Tình huống 1: Dịch tức thì trực tiếp trên Video (Quick Translate Mode)
1. Mở video trên trình duyệt (Chrome, Edge) hoặc bất kỳ trình phát video nào (YouTube, Netflix, Bilibili, VLC...).
2. Để ScreenSub AI chạy nền (Dashboard có thể đóng hoặc thu nhỏ xuống System Tray).
3. Nhấn tổ hợp phím **`Ctrl + Alt + Q`** trực tiếp khi đang xem video.
4. Màn hình tối nhẹ, kéo chuột quét chọn vùng phụ đề của video rồi thả chuột.
5. **ScreenSub AI tự động làm tất cả**:
   - Lưu vùng phụ đề đã chọn.
   - Căn chỉnh cửa sổ Overlay xuất hiện ngay dưới vùng vừa chọn.
   - Tự động kích hoạt pipeline OCR & Dịch thuật ngoại tuyến (hoặc online).
   - Phụ đề dịch sẽ nổi trực tiếp trên video và tự động cập nhật khi video chuyển cảnh!
   - **Hoàn toàn không cần quay lại Dashboard bấm Start lần thứ hai!**
6. Nếu muốn hủy việc chọn vùng, chỉ cần nhấn **`ESC`**. Phiên dịch hiện tại (nếu đang chạy) sẽ tiếp tục bình thường mà không bị gián đoạn.

### Tình huống 2: Chạy Nền Ẩn Dưới Khay Hệ Thống (System Tray)
1. Khi nhấn nút đóng (**X**) trên cửa sổ Dashboard, ứng dụng sẽ tự động thu nhỏ xuống khay hệ thống (System Tray) thay vì tắt hẳn.
2. Dưới khay hệ thống có biểu tượng ScreenSub AI với menu chuột phải tiện lợi:
   - **🖥 Mở Dashboard**: Mở lại cửa sổ điều khiển chính.
   - **✂ Chọn vùng dịch mới (Ctrl + Alt + Q)**: Kích hoạt công cụ chọn vùng.
   - **▶ / ⏸ Bắt đầu / Tạm dừng dịch**: Điều khiển luồng dịch nhanh.
   - **⏹ Dừng dịch**: Dừng phiên dịch hiện tại và xóa phụ đề cũ.
   - **⚙ Cài đặt**: Mở thẳng trang Cài đặt.
   - **🚪 Thoát ứng dụng**: Tắt hoàn toàn ứng dụng và giải phóng toàn bộ worker.

### Tình huống 3: Dịch Video Không Có Phụ Đề Bằng Âm Thanh (Audio Realtime Mode)
1. Trên menu bên trái, chọn chế độ **Nhận diện âm thanh (Audio Realtime)**.
2. Chọn thiết bị phát âm thanh trong danh sách (Default Speakers / Headphones).
3. Bật video nói tiếng Anh hoặc tiếng Trung.
4. Nhấn **Bắt đầu dịch**: Hệ thống WASAPI Loopback sẽ bắt luồng âm thanh trực tiếp từ Windows, nhận diện giọng nói qua Whisper và hiển thị phụ đề dịch trên Overlay.

### Tình huống 4: Xem Phim Khi Mất Mạng Hoặc Đi Máy Bay (Offline Translation)
1. Vào mục **Cài đặt** → Chọn **Chế độ dịch: Offline AI (Cục bộ)**.
2. Bảo đảm bạn đã tải mô hình **Facebook M2M-100 (INT8)** trong **Quản lý mô hình AI**.
3. Toàn bộ quá trình OCR và dịch thuật diễn ra 100% trong RAM và CPU máy tính, không gửi bất kỳ byte dữ liệu nào lên Internet.

---

## 3. Hệ Thống Phím Tắt Tiện Lợi

- **`Ctrl + Alt + Q`**: Kích hoạt công cụ vẽ chọn vùng phụ đề màn hình (hoạt động từ mọi ứng dụng).
- **`ESC`**: Hủy bỏ việc chọn vùng màn hình.
- Có thể đổi phím tắt sang tổ hợp khác trong tab **Cài đặt**.
- **`Esc`**: Hủy bỏ vùng vẽ đang chọn.
- **`Alt + Drag` (trên Overlay)**: Kéo di chuyển vị trí thanh phụ đề nổi đến bất kỳ đâu trên màn hình.

