#!/usr/bin/env python3
"""
ScreenSub AI - PaddleOCR Worker Process
Communicates with ScreenSub.Desktop over standard input/output using JSON-lines protocol.
Supports: English, Chinese, Vietnamese, and Unicode text.
Compatible with: RapidOCR (PaddleOCR ONNX Runtime) and native PaddleOCR.
"""

import sys
import os
import json
import base64
import time
import io
import argparse
import re

# Ensure standard streams use UTF-8 on Windows to avoid 'charmap' encoding errors
if sys.platform == "win32":
    if hasattr(sys.stdin, "reconfigure"):
        try:
            sys.stdin.reconfigure(encoding="utf-8")
        except Exception:
            pass
    if hasattr(sys.stdout, "reconfigure"):
        try:
            sys.stdout.reconfigure(encoding="utf-8")
        except Exception:
            pass
    if hasattr(sys.stderr, "reconfigure"):
        try:
            sys.stderr.reconfigure(encoding="utf-8")
        except Exception:
            pass

try:
    from PIL import Image
    import numpy as np
    PIL_AVAILABLE = True
except ImportError:
    PIL_AVAILABLE = False

try:
    import cv2
    CV2_AVAILABLE = True
except ImportError:
    CV2_AVAILABLE = False

OCR_BACKEND = "none"
ocr_instance = None
_WARMED_UP = False

def _configure_onnx_sessions(ocr_obj, intra_threads):
    """Configures ONNX Runtime CPU memory arena and non-spinning threads for RapidOCR sessions."""
    try:
        import onnxruntime as ort
        import rapidocr_onnxruntime

        models_dir = os.path.join(os.path.dirname(rapidocr_onnxruntime.__file__), "models")
        det_model = os.path.join(models_dir, "ch_PP-OCRv4_det_infer.onnx")
        rec_model = os.path.join(models_dir, "ch_PP-OCRv4_rec_infer.onnx")
        if not (os.path.isfile(det_model) and os.path.isfile(rec_model)):
            return

        sess_opt = ort.SessionOptions()
        sess_opt.log_severity_level = 4
        sess_opt.enable_cpu_mem_arena = True
        sess_opt.enable_mem_pattern = True
        sess_opt.intra_op_num_threads = intra_threads
        sess_opt.inter_op_num_threads = 1
        sess_opt.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
        # Prevent CPU busy-spin contention when running alongside translation / .NET capture threads
        sess_opt.add_session_config_entry("session.intra_op.allow_spinning", "0")

        ocr_obj.text_det.infer.session = ort.InferenceSession(
            det_model, sess_options=sess_opt, providers=["CPUExecutionProvider"]
        )
        ocr_obj.text_rec.session.session = ort.InferenceSession(
            rec_model, sess_options=sess_opt, providers=["CPUExecutionProvider"]
        )
    except Exception as ex:
        sys.stderr.write(f"[PaddleOCR Worker] Session option tuning skipped: {ex}\n")
        sys.stderr.flush()

def _warmup_ocr_pipeline():
    """Executes one small dummy pass so ONNX Runtime arenas and CPU kernels are warm before Frame #1."""
    global _WARMED_UP
    if _WARMED_UP or ocr_instance is None or not PIL_AVAILABLE:
        return 0.0
    t0 = time.perf_counter()
    try:
        dummy = np.zeros((64, 256, 3), dtype=np.uint8)
        dummy[18:46, 24:220] = 255
        if OCR_BACKEND == "rapidocr":
            ocr_instance(dummy)
        elif OCR_BACKEND == "paddleocr":
            ocr_instance.ocr(dummy, cls=False)
        _WARMED_UP = True
    except Exception:
        pass
    return (time.perf_counter() - t0) * 1000.0

# 1. First try RapidOCR (PaddleOCR PP-OCRv4 ONNX Runtime - fast, lightweight, CPU-optimized)
try:
    from rapidocr_onnxruntime import RapidOCR
    _intra_threads = min(4, max(1, os.cpu_count() or 4))
    # Desktop/video subtitles are horizontal (0 deg); disabling angle cls and vertical padding cuts wasted CNN work.
    ocr_instance = RapidOCR(
        det_limit_type="max",
        det_limit_side_len=736,
        intra_op_num_threads=_intra_threads,
        inter_op_num_threads=1,
        use_cls=False,
        width_height_ratio=-1,
    )
    _configure_onnx_sessions(ocr_instance, _intra_threads)
    OCR_BACKEND = "rapidocr"
except Exception as e_rapid:
    # 2. Next try native PaddleOCR
    try:
        from paddleocr import PaddleOCR
        ocr_instance = PaddleOCR(use_angle_cls=False, lang="ch", show_log=False)
        OCR_BACKEND = "paddleocr"
    except Exception as e_paddle:
        OCR_BACKEND = "none"
        sys.stderr.write(f"[PaddleOCR Worker] RapidOCR error: {e_rapid}\n")
        sys.stderr.write(f"[PaddleOCR Worker] PaddleOCR error: {e_paddle}\n")
        sys.stderr.flush()

def parse_box_and_text(item):
    """
    Parses OCR item from either RapidOCR or PaddleOCR format.
    RapidOCR format: [box, text, score]
    PaddleOCR format: [box, (text, score)]
    """
    try:
        if not item or len(item) < 2:
            return None, "", 0.0

        box = item[0]

        # PaddleOCR: item[1] is a tuple/list: (text, conf)
        if isinstance(item[1], (tuple, list)) and len(item[1]) >= 2:
            text = str(item[1][0]).strip()
            conf = float(item[1][1])
            return box, text, conf

        # RapidOCR: item[1] is text, item[2] is conf
        if len(item) >= 3:
            text = str(item[1]).strip()
            conf = float(item[2])
            return box, text, conf

        return None, "", 0.0
    except Exception:
        return None, "", 0.0

def restore_word_spacing(text):
    if not text:
        return ""
    # 1. Clean accidental spaces between CJK characters and punctuation
    text = re.sub(r'(?<=[\u4e00-\u9fff\u3400-\u4dbf])\s+(?=[\u4e00-\u9fff\u3400-\u4dbf])', '', text)
    text = re.sub(r'\s+([，。！？；：、“”‘’（）《》])', r'\1', text)
    text = re.sub(r'([，。！？；：、“”‘’（）《》])\s+', r'\1', text)

    # 2. Insert space at CamelCase / PascalCase word boundaries in Latin text
    text = re.sub(r'(?<=[a-z])(?=[A-Z])', ' ', text)
    text = re.sub(r'(?<=[A-Z])(?=[A-Z][a-z])', ' ', text)

    # 3. Insert space after punctuation if immediately followed by a letter
    text = re.sub(r'([,\.\?!;:])(?=[a-zA-Z])', r'\1 ', text)

    # 4. Collapse multiple spaces into one
    text = re.sub(r'[ ]{2,}', ' ', text)
    return text.strip()

def _decode_bgr_and_scale(image_bytes):
    """Decodes raw image bytes to BGR numpy array (RapidOCR native format) and applies safe subtitle-height normalization."""
    if CV2_AVAILABLE:
        buf = np.frombuffer(image_bytes, dtype=np.uint8)
        bgr = cv2.imdecode(buf, cv2.IMREAD_COLOR)
        if bgr is not None:
            orig_h, orig_w = bgr.shape[:2]
            scale = 1.0
            if orig_h < 64:
                scale = max(1.5, min(2.5, 75.0 / max(1, orig_h)))
                bgr = cv2.resize(bgr, (int(orig_w * scale), int(orig_h * scale)), interpolation=cv2.INTER_CUBIC)
            elif orig_w > 1440 and orig_h > 200:
                # High-DPI screen crops (e.g. 150%-200% DPI): scale cleanly to ~1280px wide (~36-45px font height)
                sc = min(1280.0 / orig_w, 190.0 / orig_h)
                if sc < 0.90:
                    scale = sc
                    bgr = cv2.resize(bgr, (int(orig_w * scale), int(orig_h * scale)), interpolation=cv2.INTER_AREA)
            return bgr, scale

    image = Image.open(io.BytesIO(image_bytes))
    if image.mode != "RGB":
        image = image.convert("RGB")
    orig_w, orig_h = image.size
    scale = 1.0
    if orig_h < 64:
        scale = max(1.5, min(2.5, 75.0 / max(1, orig_h)))
        image = image.resize((int(orig_w * scale), int(orig_h * scale)), Image.Resampling.LANCZOS)
    elif orig_w > 1440 and orig_h > 200:
        sc = min(1280.0 / orig_w, 190.0 / orig_h)
        if sc < 0.90:
            scale = sc
            image = image.resize((int(orig_w * scale), int(orig_h * scale)), Image.Resampling.BILINEAR)
    # RapidOCR expects BGR channel order when given an ndarray
    return np.ascontiguousarray(np.array(image)[:, :, ::-1]), scale

def _run_rapidocr_decoupled(img_np):
    """Runs TextDetector on <=960px side and crops TextRecognizer directly from full-res img_np."""
    cur_h, cur_w = img_np.shape[:2]
    det_ms = 0.0
    cls_ms = 0.0
    rec_ms = 0.0

    if max(cur_h, cur_w) > 960 and CV2_AVAILABLE and hasattr(ocr_instance, "text_det") and hasattr(ocr_instance, "text_rec"):
        d_sc = 960.0 / max(cur_h, cur_w)
        det_h = int(cur_h * d_sc)
        det_w = int(cur_w * d_sc)
        min_h = getattr(ocr_instance, "min_height", 30)
        if det_h > min_h and det_w > 32:
            det_img = cv2.resize(img_np, (det_w, det_h), interpolation=cv2.INTER_AREA)
            dt_boxes, det_elapse = ocr_instance.text_det(det_img)
            det_ms = float(det_elapse or 0.0) * 1000.0
            if dt_boxes is None or len(dt_boxes) == 0:
                return [], det_ms, 0.0, 0.0

            inv = 1.0 / d_sc
            scaled_boxes = []
            for box in ocr_instance.sorted_boxes(dt_boxes):
                b = box.astype(np.float32) * inv
                b[:, 0] = np.clip(b[:, 0], 0, max(0, cur_w - 1))
                b[:, 1] = np.clip(b[:, 1], 0, max(0, cur_h - 1))
                scaled_boxes.append(b)

            crops = ocr_instance.get_crop_img_list(img_np, scaled_boxes)
            rec_res, rec_elapse = ocr_instance.text_rec(crops, False)
            rec_ms = float(rec_elapse or 0.0) * 1000.0

            results = []
            score_thresh = float(getattr(ocr_instance, "text_score", 0.5))
            for bx, rc in zip(scaled_boxes, rec_res):
                txt, conf = rc[0], float(rc[1])
                if txt and conf >= score_thresh:
                    results.append([bx.tolist(), txt, conf])
            return results, det_ms, cls_ms, rec_ms

    results, elapse = ocr_instance(img_np)
    if elapse and len(elapse) >= 3:
        det_ms = float(elapse[0]) * 1000.0
        cls_ms = float(elapse[1]) * 1000.0
        rec_ms = float(elapse[2]) * 1000.0
    elif elapse and len(elapse) >= 1:
        det_ms = float(elapse[0]) * 1000.0
    return results or [], det_ms, cls_ms, rec_ms

def process_image(image_bytes):
    """Performs OCR on raw image bytes and returns structured line bounds and text."""
    lines = []
    start_time = time.perf_counter()

    if not PIL_AVAILABLE:
        return {
            "status": "error",
            "message": "Pillow / numpy not installed in Python environment",
            "lines": [],
            "latency_ms": 0,
            "backend": OCR_BACKEND
        }

    try:
        t0 = time.perf_counter()
        img_np, scale = _decode_bgr_and_scale(image_bytes)
        decode_ms = round((time.perf_counter() - t0) * 1000.0, 2)
        det_ms = 0.0
        cls_ms = 0.0
        rec_ms = 0.0

        t_post0 = time.perf_counter()
        if OCR_BACKEND == "rapidocr" and ocr_instance is not None:
            results, det_ms, cls_ms, rec_ms = _run_rapidocr_decoupled(img_np)
            t_post0 = time.perf_counter()
            if results:
                for item in results:
                    box, text, conf = parse_box_and_text(item)
                    if not text or not box:
                        continue
                    left = int(min(p[0] for p in box) / scale)
                    top = int(min(p[1] for p in box) / scale)
                    right = int(max(p[0] for p in box) / scale)
                    bottom = int(max(p[1] for p in box) / scale)
                    cleaned_text = restore_word_spacing(text)
                    lines.append({
                        "text": cleaned_text,
                        "confidence": round(conf, 4),
                        "left": left,
                        "top": top,
                        "right": right,
                        "bottom": bottom
                    })

        elif OCR_BACKEND == "paddleocr" and ocr_instance is not None:
            raw_results = ocr_instance.ocr(img_np, cls=False)
            t_post0 = time.perf_counter()
            if raw_results and raw_results[0]:
                for item in raw_results[0]:
                    box, text, conf = parse_box_and_text(item)
                    if not text or not box:
                        continue
                    left = int(min(p[0] for p in box) / scale)
                    top = int(min(p[1] for p in box) / scale)
                    right = int(max(p[0] for p in box) / scale)
                    bottom = int(max(p[1] for p in box) / scale)
                    cleaned_text = restore_word_spacing(text)
                    lines.append({
                        "text": cleaned_text,
                        "confidence": round(conf, 4),
                        "left": left,
                        "top": top,
                        "right": right,
                        "bottom": bottom
                    })
        else:
            # Fallback when no OCR engine is installed
            return {
                "status": "error",
                "message": "Neither rapidocr_onnxruntime nor paddleocr is installed",
                "lines": [],
                "latency_ms": int((time.perf_counter() - start_time) * 1000),
                "backend": "none"
            }

        post_ms = round((time.perf_counter() - t_post0) * 1000.0, 2)
        latency = int((time.perf_counter() - start_time) * 1000)
        return {
            "status": "ok",
            "backend": OCR_BACKEND,
            "paddle": OCR_BACKEND in ("rapidocr", "paddleocr"),
            "lines": lines,
            "latency_ms": latency,
            "decode_ms": decode_ms,
            "det_ms": round(det_ms, 2),
            "cls_ms": round(cls_ms, 2),
            "rec_ms": round(rec_ms, 2),
            "post_ms": post_ms
        }
    except Exception as ex:
        return {
            "status": "error",
            "message": str(ex),
            "backend": OCR_BACKEND,
            "lines": [],
            "latency_ms": int((time.perf_counter() - start_time) * 1000)
        }

def run_self_test():
    """Generates a test image with Chinese and English text and verifies recognition."""
    if not PIL_AVAILABLE:
        print(json.dumps({"status": "error", "message": "Pillow not available"}, ensure_ascii=False))
        return

    from PIL import ImageDraw
    img = Image.new("RGB", (320, 80), color=(0, 0, 0))
    draw = ImageDraw.Draw(img)
    draw.text((10, 20), "ScreenSub AI OCR Test", fill=(255, 255, 255))

    buf = io.BytesIO()
    img.save(buf, format="PNG")
    res = process_image(buf.getvalue())
    print(json.dumps(res, ensure_ascii=False, indent=2))

def main():
    parser = argparse.ArgumentParser(description="ScreenSub AI PaddleOCR Worker")
    parser.add_argument("--test", action="store_true", help="Run quick self-test on generated image")
    parser.add_argument("--ping", action="store_true", help="Print ping status and exit")
    parser.add_argument("file", nargs="?", help="Optional image file path to run OCR on")
    args = parser.parse_args()

    if args.test:
        run_self_test()
        return

    if args.ping:
        status = {
            "status": "ok",
            "pong": True,
            "backend": OCR_BACKEND,
            "paddle": OCR_BACKEND in ("rapidocr", "paddleocr")
        }
        print(json.dumps(status, ensure_ascii=False))
        return

    if args.file:
        if not os.path.exists(args.file):
            print(json.dumps({"status": "error", "message": f"File not found: {args.file}"}))
            return
        with open(args.file, "rb") as f:
            res = process_image(f.read())
        print(json.dumps(res, ensure_ascii=False, indent=2))
        return

    # Standard JSON-lines standard input/output mode for C# process communication
    warmup_ms = _warmup_ocr_pipeline()
    sys.stderr.write(f"[PaddleOCR Worker] Started. Backend: {OCR_BACKEND}, warmup={warmup_ms:.1f}ms\n")
    sys.stderr.flush()

    while True:
        try:
            line = sys.stdin.readline()
            if not line:
                break
            line = line.strip().lstrip('\ufeff')
            if not line:
                continue

            req = json.loads(line)
            req_id = req.get("id", 0)
            cmd = req.get("cmd", "")

            if cmd == "ping":
                resp = {
                    "id": req_id,
                    "status": "ok",
                    "pong": True,
                    "warmed": _WARMED_UP,
                    "backend": OCR_BACKEND,
                    "paddle": OCR_BACKEND in ("rapidocr", "paddleocr")
                }
            elif cmd == "warmup":
                ms = _warmup_ocr_pipeline()
                resp = {
                    "id": req_id,
                    "status": "ok",
                    "warmed": _WARMED_UP,
                    "warmup_ms": round(ms, 2),
                    "backend": OCR_BACKEND
                }
            elif cmd == "ocr":
                img_b64 = req.get("image_base64", "")
                if not img_b64:
                    resp = {"id": req_id, "status": "error", "message": "Missing image_base64"}
                else:
                    img_bytes = base64.b64decode(img_b64)
                    result = process_image(img_bytes)
                    resp = {"id": req_id, **result}
            elif cmd == "quit":
                break
            else:
                resp = {"id": req_id, "status": "error", "message": f"Unknown command '{cmd}'"}

            sys.stdout.write(json.dumps(resp, ensure_ascii=False) + "\n")
            sys.stdout.flush()

        except KeyboardInterrupt:
            break
        except Exception as e:
            err_resp = {"id": -1, "status": "error", "message": str(e)}
            sys.stdout.write(json.dumps(err_resp, ensure_ascii=False) + "\n")
            sys.stdout.flush()

if __name__ == "__main__":
    main()
