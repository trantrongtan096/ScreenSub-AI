# Hướng Dẫn Mô Hình AI & Bản Quyền (Model Dependency Guide)

Tài liệu cung cấp thông tin kỹ thuật, giấy phép bản quyền và liên kết tải về cho tất cả các mô hình AI được hỗ trợ trong **ScreenSub AI**.

---

## 1. Bảng Danh Mục Mô Hình AI

| Tên mô hình | Danh mục | Mục đích sử dụng | Kích thước | Giấy phép | Tình trạng mặc định | Nguồn chính thức |
|---|:---:|---|:---:|:---:|:---:|---|
| **PaddleOCR / RapidOCR PP-OCRv4** | OCR | Nhận diện chữ tiếng Trung, Anh, Việt trên màn hình | ~125 MB | Apache-2.0 | Tích hợp qua Python RapidOCR | [RapidOCR GitHub](https://github.com/RapidAI/RapidOCR) |
| **Facebook M2M-100 (418M) INT8** | Dịch thuật | Mô hình dịch đa ngữ Offline 6 chiều (Anh ↔ Trung ↔ Việt) & Fallback mặc định | ~473.8 MB (dir) | **MIT License** | Tải qua Model Manager | [Rohith04/ct2fast_m2m100_418M](https://huggingface.co/Rohith04/ct2fast_m2m100_418M) |
| **VietAI EnViT5 (`envit5_ct2_int8`)** | Dịch thuật (Beta Router) | Chuyên biệt hội thoại Anh → Việt / Việt → Anh (CT2 INT8, beam=1) | ~269.0 MB (dir) | **BigScience OpenRAIL-M** | Router Beta (`EN↔VI`) | [VietAI/envit5-translation](https://huggingface.co/VietAI/envit5-translation) |
| **Opus-MT Zh-Vi (`opus_mt_zh_vi_ct2`)** | Dịch thuật (Experimental) | Chuyên biệt Trung → Việt thử nghiệm (CT2 INT8, beam=2) | ~80.0 MB (dir) | **Apache-2.0** | Tắt trong Beta EN-VI (`EnableExperimentalZhViOpusModel=false`) | [Helsinki-NLP/opus-mt-zh-vi](https://huggingface.co/Helsinki-NLP/opus-mt-zh-vi) |
| **Whisper Base GGML (Multilingual)** | Âm thanh | Nhận diện giọng nói từ âm thanh hệ thống (ASR) | ~141,1 MiB (147,95 MB) | **MIT License** | Tải qua Model Manager | [ggerganov/whisper.cpp](https://huggingface.co/ggerganov/whisper.cpp) |
| **Whisper Small GGML (Multilingual)** | Âm thanh | Nhận diện giọng nói độ chính xác cao cho phim phức tạp | ~466 MB | **MIT License** | Tùy chọn nâng cao | [ggerganov/whisper.cpp](https://huggingface.co/ggerganov/whisper.cpp) |
| **Built-in Lexical Engine** | Dịch thuật | Bộ quy tắc từ điển tốc độ cao (~1 ms) | ~1 MB | Apache-2.0 | Tích hợp sẵn trong App | Bản quyền nội bộ ScreenSub AI |

---

## 2. Bản Quyền Thương Mại & Điều Khoản Phân Phối

1. **Meta NLLB-200:** Được phát hành dưới giấy phép `CC BY-NC 4.0` (cấm thương mại). Do đó ScreenSub AI **không sử dụng NLLB-200**.
2. **Meta M2M-100:** Giấy phép **MIT License**, cho phép sử dụng và phân phối thương mại kèm thông báo bản quyền/giấy phép MIT gốc.
3. **VietAI EnViT5 (`VietAI/envit5-translation`):** Giấy phép **OpenRAIL (BigScience OpenRAIL-M)**.
   - Cho phép phân phối/tích hợp thương mại (bao gồm bản lượng tử hóa `ctranslate2 INT8`), **nhưng bắt buộc**:
     - Kèm theo văn bản giấy phép OpenRAIL và ghi nhận nguồn gốc tác giả (**VietAI / `VietAI/envit5-translation`**) trong tài liệu phân phối / About.
     - Tuân thủ các điều khoản hạn chế sử dụng có trách nhiệm (Use-based Restrictions trong OpenRAIL: không dùng cho mục đích vi phạm pháp luật, gây hại, mạo danh, y tế/tư pháp tự động không kiểm soát...) và truyền đạt lại các điều khoản hạn chế này cho người dùng nhận mô hình phái sinh.
4. **Helsinki-NLP Opus-MT Zh-Vi:** Giấy phép **Apache-2.0**, yêu cầu giữ thông báo bản quyền & NOTICE (nếu có) và ghi rõ nguồn gốc `Helsinki-NLP/opus-mt-zh-vi`.

---

## 3. Cấu Trúc Thư Mục, Nguồn Gốc Chuyển Đổi CT2 & Mã Băm SHA-256

Tất cả các mô hình Offline được lưu trữ ngoài bộ cài tại `%LocalAppData%\ScreenSubAI\Models\translation\` (không tự động tải ngầm khi khởi động ứng dụng; bộ gỡ cài đặt bảo toàn thư mục này).

- **`m2m100_418m_ct2/`** (Nguồn: `Rohith04/ct2fast_m2m100_418M` chuyển đổi từ `facebook/m2m100_418M`):
  - `model.bin` (487,900,865 bytes) — SHA-256: `a68b104a4e4db53165562b6d7d30839b318242e5042081a5407885cc2266a66c`
  - `sentencepiece.bpe.model` (2,423,393 bytes) — SHA-256: `d8f7c76ed2a5e0822be39f0a4f95a55eb19c78f4593ce609e2edbc2aea4d380a`
- **`envit5_ct2_int8/`** (Chuyển đổi `ct2-transformers-converter` định dạng `int8` từ `VietAI/envit5-translation`):
  - `model.bin` (276,586,816 bytes) — SHA-256: `e8a3d9be0fc077668972ed89c351cc4c911a09e9e3c6d8966478c113fc85ff8e`
  - `tokenizer.json` (3,488,920 bytes) — SHA-256: `74d993969a7a9addc0096ba0f574ee13046ebf234b3436e183a7951a9da1ee2b`
  - `spiece.model` (1,102,207 bytes) — SHA-256: `3b4eda923bbac1726e8fda6625448783ecc705be5577149ee8c98074efdb5de5`
- **`opus_mt_zh_vi_ct2/`** (Bản CT2 INT8 thử nghiệm từ `Helsinki-NLP/opus-mt-zh-vi`):
  - `model.bin` (79,567,635 bytes) — SHA-256: `488da6baa02675083a7f3e01e788e58e18b77e94b4cf9d2bfb4bcd4406204542`
  - `source.spm` (750,285 bytes) — SHA-256: `d617117f90da08f471ef72c649401748a26e9ffb22289986379a9b44f7d8810e`
  - `target.spm` (766,197 bytes) — SHA-256: `eb082ee05298800b89d0cfa7101ef9e468b2554459a8e7635cb56cc17d0f3f02`

- **`whisper/ggml-base.bin`** (Nguồn chính thức `ggerganov/whisper.cpp`, thư mục `%LocalAppData%\ScreenSubAI\Models\whisper\`):
  - `ggml-base.bin` (147,951,465 bytes) — SHA-256: `60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe`

ScreenSub AI (`LocalModelManager.VerifyModelIntegrityAsync`) kiểm tra tồn tại file tokenizer bắt buộc và đối chiếu SHA-256 của `model.bin` (và `ggml-base.bin`) trước khi xác nhận mô hình hợp lệ.

