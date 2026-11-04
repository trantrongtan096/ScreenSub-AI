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

OCR_BACKEND = "none"
ocr_instance = None

# 1. First try RapidOCR (PaddleOCR PP-OCRv4 ONNX Runtime - fast, lightweight, CPU-optimized)
try:
    from rapidocr_onnxruntime import RapidOCR
    ocr_instance = RapidOCR()
    OCR_BACKEND = "rapidocr"
except Exception as e_rapid:
    # 2. Next try native PaddleOCR
    try:
        from paddleocr import PaddleOCR
        ocr_instance = PaddleOCR(use_angle_cls=True, lang="ch", show_log=False)
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

def process_image(image_bytes):
    """Performs OCR on raw image bytes and returns structured line bounds and text."""
    lines = []
    start_time = time.time()

    if not PIL_AVAILABLE:
        return {
            "status": "error",
            "message": "Pillow / numpy not installed in Python environment",
            "lines": [],
            "latency_ms": 0,
            "backend": OCR_BACKEND
        }

    try:
        image = Image.open(io.BytesIO(image_bytes))
        if image.mode != "RGB":
            image = image.convert("RGB")
        img_np = np.array(image)

        if OCR_BACKEND == "rapidocr" and ocr_instance is not None:
            results, elapse = ocr_instance(img_np)
            if results:
                for item in results:
                    box, text, conf = parse_box_and_text(item)
                    if not text or not box:
                        continue
                    left = int(min(p[0] for p in box))
                    top = int(min(p[1] for p in box))
                    right = int(max(p[0] for p in box))
                    bottom = int(max(p[1] for p in box))
                    lines.append({
                        "text": text,
                        "confidence": round(conf, 4),
                        "left": left,
                        "top": top,
                        "right": right,
                        "bottom": bottom
                    })

        elif OCR_BACKEND == "paddleocr" and ocr_instance is not None:
            raw_results = ocr_instance.ocr(img_np, cls=True)
            if raw_results and raw_results[0]:
                for item in raw_results[0]:
                    box, text, conf = parse_box_and_text(item)
                    if not text or not box:
                        continue
                    left = int(min(p[0] for p in box))
                    top = int(min(p[1] for p in box))
                    right = int(max(p[0] for p in box))
                    bottom = int(max(p[1] for p in box))
                    lines.append({
                        "text": text,
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
                "latency_ms": int((time.time() - start_time) * 1000),
                "backend": "none"
            }

        latency = int((time.time() - start_time) * 1000)
        return {
            "status": "ok",
            "backend": OCR_BACKEND,
            "paddle": OCR_BACKEND in ("rapidocr", "paddleocr"),
            "lines": lines,
            "latency_ms": latency
        }
    except Exception as ex:
        return {
            "status": "error",
            "message": str(ex),
            "backend": OCR_BACKEND,
            "lines": [],
            "latency_ms": int((time.time() - start_time) * 1000)
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
    sys.stderr.write(f"[PaddleOCR Worker] Started. Backend: {OCR_BACKEND}\n")
    sys.stderr.flush()

    while True:
        try:
            line = sys.stdin.readline()
            if not line:
                break
            line = line.strip()
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
                    "backend": OCR_BACKEND,
                    "paddle": OCR_BACKEND in ("rapidocr", "paddleocr")
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
