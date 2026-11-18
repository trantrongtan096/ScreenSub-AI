#!/usr/bin/env python3
"""
ScreenSub AI - Neural Translation Worker Process
Communicates with ScreenSub.Translation over standard input/output using JSON-lines protocol.
Supports single-model M2M-100 baseline/rollback AND bounded multi-model routing:
  - en -> vi, vi -> en : VietAI EnViT5 (CT2 INT8, beam=1)
  - zh -> vi           : Opus-MT Zh-Vi (CT2 INT8, beam=2, experimental)
  - vi -> zh, en -> zh, zh -> en (+ fallback) : Facebook M2M-100 418M (CT2 INT8, beam=2)
"""

import sys
import os
import json
import time
import argparse
import gc
import hashlib
import re
import unicodedata
from collections import OrderedDict

# Ensure standard streams use UTF-8 on Windows
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

CTRANSLATE2_AVAILABLE = False
TRANSFORMERS_AVAILABLE = False
SENTENCEPIECE_AVAILABLE = False

try:
    import ctranslate2
    CTRANSLATE2_AVAILABLE = True
except ImportError:
    pass

try:
    import transformers
    TRANSFORMERS_AVAILABLE = True
except ImportError:
    pass

try:
    import sentencepiece as spm
    SENTENCEPIECE_AVAILABLE = True
except ImportError:
    pass

MODEL_ID_M2M100 = "m2m100_418m_ct2"
MODEL_ID_ENVIT5 = "envit5_ct2_int8"
MODEL_ID_OPUS_ZH_VI = "opus_mt_zh_vi_ct2"

MODEL_REGISTRY = {
    MODEL_ID_M2M100: {
        "model_id": MODEL_ID_M2M100,
        "display_name": "M2M-100 418M (INT8 Neural)",
        "family": "m2m100",
        "folder_name": "m2m100_418m_ct2",
        "required_files": ("model.bin", "sentencepiece.bpe.model"),
        "license": "MIT",
        "default_beam": 2,
        "default_lp": 1.0,
        "default_rp": 1.05,
        "default_ng": 3,
        "default_max_len": 256,
        "supported_pairs": frozenset({
            ("en", "vi"), ("vi", "en"),
            ("zh", "vi"), ("vi", "zh"),
            ("en", "zh"), ("zh", "en"),
        }),
    },
    MODEL_ID_ENVIT5: {
        "model_id": MODEL_ID_ENVIT5,
        "display_name": "VietAI EnViT5 (CT2 INT8)",
        "family": "envit5",
        "folder_name": "envit5_ct2_int8",
        "required_files": ("model.bin", "tokenizer.json"),
        "license": "OpenRAIL",
        "default_beam": 1,
        "default_lp": 1.0,
        "default_rp": 1.05,
        "default_ng": 3,
        "default_max_len": 192,
        "supported_pairs": frozenset({("en", "vi"), ("vi", "en")}),
    },
    MODEL_ID_OPUS_ZH_VI: {
        "model_id": MODEL_ID_OPUS_ZH_VI,
        "display_name": "Opus-MT Zh-Vi (CT2 INT8)",
        "family": "opus_zh_vi",
        "folder_name": "opus_mt_zh_vi_ct2",
        "required_files": ("model.bin", "source.spm", "target.spm"),
        "license": "Apache-2.0",
        "default_beam": 2,
        "default_lp": 1.0,
        "default_rp": 1.05,
        "default_ng": 3,
        "default_max_len": 192,
        "supported_pairs": frozenset({("zh", "vi")}),
    },
}

# Legacy globals kept synchronized with most recently active model for backwards compatibility
translator = None
tokenizer = None
current_model_id = ""
current_model_dir = ""
current_device = "cpu"
current_compute_type = "int8"
current_model_fingerprint = ""

# Bounded LRU cache of active CTranslate2 models in memory
_loaded_models = OrderedDict()
_active_inference_count = 0

router_config = {
    "enabled": False,                # Default False = M2M-100 rollback baseline until controlled activation
    "experimental_zh_vi": True,
    "allow_fallback": True,          # Strictly offline fallback (M2M-100 only); never cloud/mock
    "max_loaded_models": 1,          # RAM guard: evict LRU model before loading next
    "models_root": "",
    "device": "cpu",
    "threads": None,
}

DEFAULT_BEAM_SIZE = 2
DEFAULT_LENGTH_PENALTY = 1.0
DEFAULT_REPETITION_PENALTY = 1.05
DEFAULT_NO_REPEAT_NGRAM_SIZE = 3
DEFAULT_MAX_DECODING_LENGTH = 256
PREPROC_VERSION = "v2-general"
POSTPROC_VERSION = "v2-general"

# Standard ISO to M2M-100 token mapping
LANG_CODE_MAP = {
    "en": "__en__",
    "zh": "__zh__",
    "zh-cn": "__zh__",
    "zh-hans": "__zh__",
    "zh-tw": "__zh__",
    "zh-hant": "__zh__",
    "vi": "__vi__",
    "vn": "__vi__",
    "auto": "__en__"
}


def compute_optimal_intra_threads(requested=None):
    """Uses physical-core heuristic capped at 4 for CPU INT8 latency stability."""
    if requested is not None and int(requested) > 0:
        return int(requested)
    cpu_cnt = os.cpu_count() or 4
    return min(4, max(1, cpu_cnt // 2 if cpu_cnt >= 4 else cpu_cnt))


def normalize_iso_lang(code):
    c = (code or "auto").strip().lower()
    if c in ("zh", "zh-cn", "zh-hans", "zh-tw", "zh-hant"):
        return "zh"
    if c in ("vi", "vn"):
        return "vi"
    if c == "en":
        return "en"
    return "auto"


def detect_language_hint(text):
    t = text or ""
    if re.search(r"[\u4e00-\u9fff\u3400-\u4dbf]", t):
        return "zh"
    if re.search(r"[àáạảãâầấậẩẫăằắặẳẵèéẹẻẽêềếệểễìíịỉĩòóọỏõôồốộổỗơờớợởỡùúụủũưừứựửữỳýỵỷỹđ]", t, re.IGNORECASE):
        return "vi"
    return "en"


def infer_model_id_from_dir(model_dir):
    base = os.path.basename(os.path.normpath(model_dir or "")).lower()
    if "envit5" in base:
        return MODEL_ID_ENVIT5
    if "opus" in base and "zh" in base and "vi" in base:
        return MODEL_ID_OPUS_ZH_VI
    return MODEL_ID_M2M100


def compute_model_fingerprint(model_dir, model_id=None):
    """Produces a compact deterministic fingerprint from model.bin size and tokenizer signature."""
    try:
        mid = model_id or infer_model_id_from_dir(model_dir)
        model_bin = os.path.join(model_dir, "model.bin")
        size_bin = os.path.getsize(model_bin) if os.path.exists(model_bin) else 0

        if mid == MODEL_ID_M2M100:
            sp_model = os.path.join(model_dir, "sentencepiece.bpe.model")
            size_tok = os.path.getsize(sp_model) if os.path.exists(sp_model) else 0
        elif mid == MODEL_ID_ENVIT5:
            tj = os.path.join(model_dir, "tokenizer.json")
            size_tok = os.path.getsize(tj) if os.path.exists(tj) else 0
        else:
            sp_s = os.path.join(model_dir, "source.spm")
            sp_t = os.path.join(model_dir, "target.spm")
            size_tok = (os.path.getsize(sp_s) if os.path.exists(sp_s) else 0) + (os.path.getsize(sp_t) if os.path.exists(sp_t) else 0)

        raw = f"{os.path.basename(model_dir)}:{size_bin}:{size_tok}"
        return hashlib.sha256(raw.encode("utf-8")).hexdigest()[:12]
    except Exception:
        return "unknown"


def _candidate_model_dirs(model_id, custom_models_root=None):
    spec = MODEL_REGISTRY.get(model_id)
    if not spec:
        return []
    folder = spec["folder_name"]
    candidates = []

    explicit_root = custom_models_root if custom_models_root is not None else router_config.get("models_root")
    if explicit_root:
        candidates.append(os.path.join(explicit_root, "translation", folder))
        candidates.append(os.path.join(explicit_root, folder))
    else:
        env_dir = os.environ.get("SCREENSUB_TRANSLATION_MODELS_DIR", "")
        if env_dir:
            candidates.append(os.path.join(env_dir, "translation", folder))
            candidates.append(os.path.join(env_dir, folder))

        local_app_data = os.environ.get("LOCALAPPDATA", "")
        if local_app_data:
            candidates.append(os.path.join(local_app_data, "ScreenSubAI", "Models", "translation", folder))
            candidates.append(os.path.join(local_app_data, "ScreenSubAI", "Models", "translation_eval", folder))

        script_root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
        candidates.append(os.path.join(script_root, "models", "translation", folder))
        candidates.append(os.path.join(os.getcwd(), "models", "translation", folder))

    seen = set()
    unique = []
    for c in candidates:
        nc = os.path.normpath(c)
        if nc not in seen:
            seen.add(nc)
            unique.append(nc)
    return unique


def is_valid_model_dir(model_dir, model_id):
    if not model_dir or not os.path.isdir(model_dir):
        return False
    spec = MODEL_REGISTRY.get(model_id)
    if not spec:
        return False
    for req_file in spec["required_files"]:
        fp = os.path.join(model_dir, req_file)
        if not os.path.isfile(fp) or os.path.getsize(fp) <= 0:
            return False
    return True


def resolve_model_dir(model_id, explicit_dir=None, custom_models_root=None):
    """Finds an already-installed model directory on disk without auto-downloading."""
    if explicit_dir and is_valid_model_dir(explicit_dir, model_id):
        return os.path.normpath(explicit_dir)
    for c in _candidate_model_dirs(model_id, custom_models_root=custom_models_root):
        if is_valid_model_dir(c, model_id):
            return c
    return ""


def resolve_default_model_dir():
    """Returns the default LocalAppData path for the M2M100 model."""
    return resolve_model_dir(MODEL_ID_M2M100)


def get_registry_status(custom_models_root=None):
    out = {}
    for mid, spec in MODEL_REGISTRY.items():
        mdir = resolve_model_dir(mid, custom_models_root=custom_models_root)
        out[mid] = {
            "model_id": mid,
            "display_name": spec["display_name"],
            "family": spec["family"],
            "license": spec["license"],
            "installed": bool(mdir),
            "loaded": mid in _loaded_models,
            "model_dir": mdir,
            "model_fingerprint": compute_model_fingerprint(mdir, mid) if mdir else "",
            "default_beam": spec["default_beam"],
        }
    return out


def select_route(
    source_lang,
    target_lang,
    routing_enabled=None,
    experimental_zh_vi=None,
    allow_fallback=None,
    model_override=None,
    text_hint="",
    custom_models_root=None,
):
    """
    Deterministically chooses the target model for (source_lang, target_lang).
    Never triggers auto-downloads or online fallback.
    """
    use_router = router_config["enabled"] if routing_enabled is None else bool(routing_enabled)
    use_exp_zh_vi = router_config["experimental_zh_vi"] if experimental_zh_vi is None else bool(experimental_zh_vi)
    can_fallback = router_config["allow_fallback"] if allow_fallback is None else bool(allow_fallback)

    src = normalize_iso_lang(source_lang)
    if src == "auto":
        src = detect_language_hint(text_hint)
    tgt = normalize_iso_lang(target_lang)
    if tgt == "auto":
        tgt = "vi"

    if model_override:
        preferred = model_override
    elif not use_router:
        preferred = MODEL_ID_M2M100
    elif (src, tgt) in (("en", "vi"), ("vi", "en")):
        preferred = MODEL_ID_ENVIT5
    elif (src, tgt) == ("zh", "vi") and use_exp_zh_vi:
        preferred = MODEL_ID_OPUS_ZH_VI
    else:
        preferred = MODEL_ID_M2M100

    preferred_dir = resolve_model_dir(preferred, custom_models_root=custom_models_root)
    if preferred_dir:
        return {
            "model_id": preferred,
            "requested_model": preferred,
            "fallback_used": False,
            "model_dir": preferred_dir,
            "src": src,
            "tgt": tgt,
        }

    if can_fallback and preferred != MODEL_ID_M2M100:
        m2m_dir = resolve_model_dir(MODEL_ID_M2M100, custom_models_root=custom_models_root)
        if m2m_dir:
            return {
                "model_id": MODEL_ID_M2M100,
                "requested_model": preferred,
                "fallback_used": True,
                "model_dir": m2m_dir,
                "src": src,
                "tgt": tgt,
            }

    raise FileNotFoundError(f"MODEL NOT INSTALLED: {preferred}")


def _sync_legacy_globals(entry):
    global translator, tokenizer, current_model_id, current_model_dir, current_device, current_compute_type, current_model_fingerprint
    if entry is None:
        translator = None
        tokenizer = None
        current_model_id = ""
        current_model_dir = ""
        current_model_fingerprint = ""
        return
    translator = entry["translator"]
    tokenizer = entry.get("tokenizer") or entry.get("sp_source")
    current_model_id = entry["model_id"]
    current_model_dir = entry["model_dir"]
    current_device = entry["device"]
    current_compute_type = entry["compute_type"]
    current_model_fingerprint = entry["fingerprint"]


def _evict_if_needed(target_model_id, max_loaded=None):
    cap = max(1, int(max_loaded if max_loaded is not None else router_config["max_loaded_models"]))
    while len(_loaded_models) >= cap:
        evict_id = next(iter(_loaded_models))
        if evict_id == target_model_id and len(_loaded_models) == 1:
            break
        old = _loaded_models.pop(evict_id)
        sys.stderr.write(f"[Translation Worker] Evicting model '{evict_id}' from memory (cap={cap}).\n")
        sys.stderr.flush()
        old.clear()
        gc.collect()


def acquire_model(model_id, model_dir=None, device="cpu", intra_threads=None, max_loaded_models=None):
    """
    Lazy-loads the requested model into the bounded LRU cache.
    Refuses to swap while an active inference is running.
    Returns (entry, load_time_ms).
    """
    global _active_inference_count

    if not CTRANSLATE2_AVAILABLE:
        raise RuntimeError("ctranslate2 is not installed in the Python environment")

    spec = MODEL_REGISTRY.get(model_id)
    if not spec:
        raise ValueError(f"Unknown translation model id: {model_id}")

    resolved_dir = resolve_model_dir(model_id, explicit_dir=model_dir)
    if not resolved_dir:
        raise FileNotFoundError(f"MODEL NOT INSTALLED: {model_id}")

    if model_id in _loaded_models:
        existing = _loaded_models[model_id]
        if os.path.normpath(existing["model_dir"]) == os.path.normpath(resolved_dir):
            _loaded_models.move_to_end(model_id)
            _sync_legacy_globals(existing)
            return existing, 0
        else:
            if _active_inference_count > 0:
                raise RuntimeError("Cannot replace model directory while inference is running")
            _loaded_models.pop(model_id).clear()
            gc.collect()

    if _active_inference_count > 0:
        raise RuntimeError("Cannot load/evict translation models while inference is running")

    _evict_if_needed(model_id, max_loaded=max_loaded_models)

    start_time = time.perf_counter()
    compute_type = "int8"
    eff_device = device
    if eff_device == "cuda":
        if ctranslate2.get_cuda_device_count() > 0:
            compute_type = "int8_float16"
        else:
            eff_device = "cpu"
            compute_type = "int8"

    effective_threads = compute_optimal_intra_threads(intra_threads if intra_threads is not None else router_config.get("threads"))
    sys.stderr.write(f"[Translation Worker] Loading '{model_id}' from {resolved_dir} on {eff_device} ({compute_type}, threads={effective_threads})...\n")
    sys.stderr.flush()

    ct_translator = ctranslate2.Translator(
        resolved_dir,
        device=eff_device,
        compute_type=compute_type,
        intra_threads=effective_threads,
    )

    family = spec["family"]
    entry = {
        "model_id": model_id,
        "display_name": spec["display_name"],
        "family": family,
        "model_dir": resolved_dir,
        "device": eff_device,
        "compute_type": compute_type,
        "translator": ct_translator,
        "fingerprint": compute_model_fingerprint(resolved_dir, model_id),
    }

    if family == "m2m100":
        if not TRANSFORMERS_AVAILABLE:
            raise RuntimeError("transformers is not installed in the Python environment")
        entry["tokenizer"] = transformers.M2M100Tokenizer.from_pretrained(resolved_dir)
    elif family == "envit5":
        if not TRANSFORMERS_AVAILABLE:
            raise RuntimeError("transformers is not installed in the Python environment")
        tok_path = os.path.join(resolved_dir, "tokenizer.json")
        entry["tokenizer"] = transformers.PreTrainedTokenizerFast(
            tokenizer_file=tok_path,
            eos_token="</s>",
            pad_token="<pad>",
            unk_token="<unk>",
        )
    elif family == "opus_zh_vi":
        if not SENTENCEPIECE_AVAILABLE:
            raise RuntimeError("sentencepiece is not installed in the Python environment")
        entry["sp_source"] = spm.SentencePieceProcessor(model_file=os.path.join(resolved_dir, "source.spm"))
        entry["sp_target"] = spm.SentencePieceProcessor(model_file=os.path.join(resolved_dir, "target.spm"))
    else:
        raise ValueError(f"Unsupported model family: {family}")

    _loaded_models[model_id] = entry
    _sync_legacy_globals(entry)

    load_time_ms = int((time.perf_counter() - start_time) * 1000)
    sys.stderr.write(f"[Translation Worker] Model '{model_id}' loaded in {load_time_ms} ms (fp={entry['fingerprint']}).\n")
    sys.stderr.flush()
    return entry, load_time_ms


def load_neural_model(model_dir=None, device="cpu", intra_threads=None, model_id=None):
    """Loads CTranslate2 Translator and Tokenizer (backwards-compatible entry point)."""
    if not model_dir:
        mid = model_id or MODEL_ID_M2M100
        model_dir = resolve_model_dir(mid)
    else:
        mid = model_id or infer_model_id_from_dir(model_dir)

    if not model_dir or not os.path.exists(model_dir):
        raise FileNotFoundError(f"Model directory not found: {model_dir}")

    _, load_time_ms = acquire_model(mid, model_dir=model_dir, device=device, intra_threads=intra_threads)
    return load_time_ms


def unload_neural_model(model_id=None):
    """Unloads a specific model (or all loaded models) to free RAM/VRAM."""
    if _active_inference_count > 0:
        raise RuntimeError("Cannot unload model while inference is running")

    if model_id:
        removed = _loaded_models.pop(model_id, None)
        if removed:
            removed.clear()
        if _loaded_models:
            _, latest = next(reversed(_loaded_models.items()))
            _sync_legacy_globals(latest)
        else:
            _sync_legacy_globals(None)
    else:
        for entry in _loaded_models.values():
            entry.clear()
        _loaded_models.clear()
        _sync_legacy_globals(None)

    gc.collect()
    sys.stderr.write("[Translation Worker] Model unloaded.\n")
    sys.stderr.flush()

# General Traditional -> Simplified Chinese orthographic character normalization
# Pure script-level normalization (non-sentence-specific) so M2M-100 SentencePiece vocabulary
# achieves consistent tokenization across Simplified and Traditional Chinese subtitles.
ZH_TRAD_TO_SIMP = str.maketrans({
    "嗎": "吗", "體": "体", "點": "点", "經": "经", "國": "国",
    "說": "说", "時": "时", "間": "间", "們": "们", "這": "这",
    "樣": "样", "會": "会", "個": "个", "為": "为", "對": "对",
    "現": "现", "發": "发", "學": "学", "開": "开", "關": "关",
    "機": "机", "動": "动", "電": "电", "話": "话", "頭": "头",
    "買": "买", "賣": "卖", "東": "东", "車": "车", "書": "书",
    "報": "报", "紙": "纸", "筆": "笔", "畫": "画", "寫": "写",
    "問": "问", "題": "题", "見": "见", "親": "亲", "愛": "爱",
    "錢": "钱", "門": "门", "長": "长", "張": "张", "歡": "欢",
    "樂": "乐", "過": "过", "進": "进", "遠": "远", "邊": "边",
    "還": "还", "從": "从", "來": "来", "聽": "听", "認": "认",
    "識": "识", "謝": "谢", "請": "请", "誰": "谁", "讓": "让",
    "幾": "几", "後": "后", "裏": "里", "麵": "面", "簡": "简",
    "華": "华", "語": "语", "該": "该", "沒": "没", "麼": "么",
    "實": "实", "氣": "气", "難": "难", "飛": "飞", "腦": "脑",
    "影": "影", "視": "视", "覺": "觉", "看": "看", "見": "见",
    "真": "真", "種": "种", "類": "类", "結": "结", "果": "果",
    "應": "应", "當": "当", "然": "然", "雖": "虽", "與": "与",
    "給": "给", "總": "总", "統": "统", "變": "变", "化": "化",
    "條": "条", "件": "件", "業": "业", "務": "务", "場": "场",
    "處": "处", "理": "理", "計": "计", "算": "算", "數": "数",
    "據": "据", "網": "网", "絡": "络", "連": "连", "接": "接",
    "錯": "错", "誤": "误", "幫": "帮", "助": "助", "離": "离",
    "開": "开", "始": "始", "終": "终", "於": "于", "無": "无",
    "法": "法", "萬": "万", "億": "亿", "兩": "两", "雙": "双",
    "隻": "只", "許": "许", "多": "多", "少": "少", "很": "很",
    "準": "准", "備": "备", "完": "完", "畢": "毕",
    "導": "导", "致": "致", "運": "运", "行": "行", "轉": "转",
    "換": "换", "選": "选", "擇": "择", "確": "确", "定": "定",
    "設": "设", "置": "置", "項": "项", "目": "目",
    "記": "记", "憶": "忆", "歷": "历", "史": "史", "傳": "传",
    "遞": "递", "收": "收", "集": "集", "資": "资", "訊": "讯",
    "息": "息", "內": "内", "容": "容", "標": "标", "題": "题",
    "圖": "图", "像": "像", "聲": "声", "音": "音", "頻": "频",
    "率": "率", "顯": "显", "示": "示", "隱": "隐", "藏": "藏",
    "窗": "窗", "口": "口", "幕": "幕", "譯": "译", "文": "文",
    "句": "句", "詞": "词", "彙": "汇", "義": "义", "釋": "释",
    "讀": "读", "寫": "写", "講": "讲", "談": "谈", "論": "论",
    "議": "议", "決": "决", "策": "策", "辦": "办", "公": "公",
    "室": "室", "家": "家", "庭": "庭", "友": "友", "情": "情",
    "感": "感", "興": "兴", "趣": "趣", "高": "高", "低": "低",
    "快": "快", "慢": "慢", "早": "早", "晚": "晚", "今": "今",
    "天": "天", "明": "明", "年": "年", "月": "月", "日": "日",
    "星": "星", "期": "期", "號": "号", "碼": "码", "樓": "楼",
    "層": "层", "房": "房", "間": "间", "街": "街", "道": "道",
    "路": "路", "橋": "桥", "站": "站", "飛": "飞", "機": "机",
    "票": "票", "價": "价", "格": "格", "貴": "贵", "便": "便",
    "宜": "宜", "熱": "热", "冷": "冷", "風": "风", "雨": "雨",
    "雪": "雪", "雲": "云", "山": "山", "水": "水", "海": "海",
    "河": "河", "樹": "树", "花": "花", "草": "草", "鳥": "鸟",
    "魚": "鱼", "貓": "猫", "狗": "狗", "吃": "吃", "喝": "喝",
    "飯": "饭", "菜": "菜", "茶": "茶", "酒": "酒", "水": "水",
    "甜": "甜", "苦": "苦", "辣": "辣", "酸": "酸", "鹹": "咸",
    "壯": "壮", "觀": "观", "麗": "丽", "漂": "漂", "亮": "亮",
    "乾": "干", "淨": "净", "髒": "脏", "舊": "旧", "新": "新",
    "好": "好", "壞": "坏", "對": "对", "錯": "错", "是": "是",
    "別": "别", "只": "只", "將": "将", "已": "已", "經": "经",
    "正": "正", "在": "在", "著": "着", "了": "了", "呢": "呢",
    "吧": "吧", "啊": "啊", "呀": "呀", "嗎": "吗", "哪": "哪",
    "裡": "里", "為": "为", "什": "什", "麼": "么", "怎": "怎",
    "樣": "样", "雖": "虽", "但": "但", "因": "因", "所": "所",
    "以": "以", "如": "如", "果": "果", "就": "就", "才": "才",
    "都": "都", "也": "也", "卻": "却", "並": "并", "且": "且",
    "或": "或", "者": "者", "還": "还", "更": "更", "最": "最",
    "極": "极", "太": "太", "挺": "挺", "蠻": "蛮", "較": "较",
    "稍": "稍", "微": "微", "片": "片", "刻": "刻", "馬": "马",
    "上": "上", "立": "立", "即": "即", "剛": "刚", "曾": "曾",
    "將": "将", "會": "会", "能": "能", "夠": "够", "可": "可",
    "願": "愿", "意": "意", "想": "想", "要": "要", "須": "须",
    "必": "必", "需": "需", "應": "应", "該": "该", "敢": "敢",
    "肯": "肯", "值": "值", "得": "得", "像": "像", "似": "似",
    "乎": "乎", "好": "好", "像": "像", "真": "真", "假": "假",
    "裝": "装", "演": "演", "唱": "唱", "跳": "跳", "笑": "笑",
    "哭": "哭", "鬧": "闹", "吵": "吵", "靜": "静", "安": "安",
    "全": "全", "危": "危", "險": "险", "緊": "紧", "張": "张",
    "擔": "担", "心": "心", "放": "放", "鬆": "松", "累": "累",
    "忙": "忙", "閒": "闲", "空": "空", "餓": "饿", "渴": "渴",
    "睏": "困", "睡": "睡", "醒": "醒", "病": "病", "痛": "痛",
    "醫": "医", "院": "院", "藥": "药", "買": "买", "賣": "卖",
    "送": "送", "收": "收", "借": "借", "還": "还", "付": "付",
    "找": "找", "尋": "寻", "見": "见", "遇": "遇", "碰": "碰",
    "等": "等", "待": "待", "留": "留", "走": "走", "跑": "跑",
    "飛": "飞", "趕": "赶", "追": "追", "逃": "逃", "躲": "躲",
    "藏": "藏", "站": "站", "坐": "坐", "躺": "躺", "睡": "睡",
    "拿": "拿", "抓": "抓", "放": "放", "擺": "摆", "丟": "丢",
    "扔": "扔", "掉": "掉", "落": "落", "撿": "捡", "接": "接",
    "推": "推", "拉": "拉", "抬": "抬", "扛": "扛", "背": "背",
    "抱": "抱", "親": "亲", "打": "打", "敲": "敲", "踢": "踢",
    "踩": "踩", "跳": "跳", "爬": "爬", "游": "游", "洗": "洗",
    "刷": "刷", "掃": "扫", "擦": "擦", "穿": "穿", "戴": "戴",
    "脫": "脱", "換": "换", "試": "试", "用": "用", "做": "做",
    "幹": "干", "弄": "弄", "搞": "搞", "修": "修", "建": "建",
    "造": "造", "毀": "毁", "壞": "坏", "破": "破", "碎": "碎",
    "斷": "断", "裂": "裂", "開": "开", "關": "关", "鎖": "锁",
    "鑰": "钥", "匙": "匙", "燈": "灯", "火": "火", "燒": "烧",
    "烤": "烤", "煮": "煮", "炒": "炒", "燉": "炖", "蒸": "蒸"
})
# Keep only single-character entries for str.maketrans
ZH_TRAD_TO_SIMP = {k: v for k, v in ZH_TRAD_TO_SIMP.items() if isinstance(k, int)}


def preprocess_source_for_nmt(text, m2m_src, normalize_zh_trad_to_simp=True):
    """
    General, non-sentence-specific source text normalization before subword encoding:
    1. Unicode NFKC normalization + zero-width / NBSP whitespace normalization.
    2. Typographic apostrophe/quote/dash canonicalization for consistent subword tokenization.
    3. For Chinese subtitles: trailing dangling comma trim and optional Trad->Simp normalization
       (enabled for M2M-100, disabled for Opus-MT Zh-Vi whose native SPM handles Traditional intact).
    """
    if not text:
        return ""

    # 1. Unicode compatibility normalization & control/zero-width strip
    cleaned = unicodedata.normalize("NFKC", text)
    cleaned = re.sub(r"[\u200b\u200c\u200d\ufeff]", "", cleaned)
    cleaned = cleaned.replace("\u00a0", " ")
    # Canonicalize curly quotes/apostrophes and typographic dashes so contractions & punctuation match training vocab
    cleaned = cleaned.replace("\u2019", "'").replace("\u2018", "'")
    cleaned = cleaned.replace("\u201c", '"').replace("\u201d", '"')
    cleaned = cleaned.replace("\u2014", " - ").replace("\u2013", "-")
    cleaned = re.sub(r"\s+", " ", cleaned).strip()
    if not cleaned:
        return ""

    if m2m_src == "zh":
        # Strip trailing dangling comma at the end of an isolated subtitle line
        if cleaned.endswith("，") or cleaned.endswith(","):
            cleaned = cleaned[:-1].strip()
        if normalize_zh_trad_to_simp:
            cleaned = cleaned.translate(ZH_TRAD_TO_SIMP)

    return cleaned


def refine_target_translation(text, target_lang, source_text="", model_family="m2m100"):
    """
    General structural post-processing (non-sentence-specific):
    1. Strip leaked literal '<unk>' markers without fabricating replacement words.
    2. For EnViT5: strip task-prefix echoes ('vi: ' / 'en: ') from decoded output.
    3. Remove spurious leading dialogue dashes ('- ') when source had no leading dash.
    4. Deduplicate accidental consecutive identical words.
    5. Remove accidental internal whitespace between CJK characters & around CJK punctuation.
    6. Collapse stutter punctuation (e.g. '..' -> '.', ',,' -> ',') while preserving '...'.
    """
    t = (text or "").strip()
    if not t:
        return ""

    # Strip EnViT5 task prefix emitted at output start
    if model_family == "envit5":
        t = re.sub(r"^(?:vi|en):\s*", "", t, flags=re.IGNORECASE).strip()

    # 1. Remove raw <unk> marker if emitted
    if "<unk>" in t:
        t = t.replace("<unk>", " ")
        t = re.sub(r"\s+", " ", t).strip()

    # 2. Spurious leading dialogue dash if source did not begin with a dash
    src_stripped = (source_text or "").lstrip()
    src_has_dash = bool(src_stripped and src_stripped[0] in ("-", "–", "—"))
    if not src_has_dash:
        t = re.sub(r"^[-–—]\s+", "", t)

    # 3. Stutter punctuation (preserve triple-dot ellipsis '...')
    t = re.sub(r"\.{4,}", "...", t)
    t = re.sub(r"(?<!\.)\.\.(?!\.)", ".", t)
    t = re.sub(r"([,\?!;:])\1+", r"\1", t)

    if target_lang in ("zh", "zh-cn", "zh-hans", "zh-tw", "zh-hant"):
        t = re.sub(r"(?<=[\u4e00-\u9fff\u3400-\u4dbf])\s+(?=[\u4e00-\u9fff\u3400-\u4dbf])", "", t)
        t = re.sub(r"\s+([，。！？；：、“”‘’（）《》])", r"\1", t)
        t = re.sub(r"([，。！？；：、“”‘’（）《》])\s+", r"\1", t)
    else:
        # Adjacent identical word deduplication
        t = re.sub(r"\b(\w+)\s+\1\b", r"\1", t, flags=re.IGNORECASE | re.UNICODE)
        # Collapse SentencePiece detokenizer space inside numeric clock timestamps ("7: 45" -> "7:45")
        t = re.sub(r"(\b\d{1,2}):\s+(\d{2}\b)", r"\1:\2", t)
        t = re.sub(r"\s+", " ", t)

    return t.strip()


_ABBREV_DOT_RE = re.compile(r"\b(Dr|Mr|Mrs|Ms|Prof|Sr|Jr|St|No|vs|Fig|etc|e\.g|i\.e|U\.S)\.", re.IGNORECASE)


def _split_envit5_sentences(text):
    """
    Splits multi-sentence dialogue (e.g., consecutive questions ending in '? ' or '! ')
    before single-pair T5 encoding so EnViT5 does not emit premature EOS after sentence 1.
    Protects common abbreviations (Dr., Mr., e.g., U.S.) and avoids splitting long text into >3 chunks.
    """
    if not text or len(text) < 12:
        return [text]

    placeholder = "\u0007"
    protected = _ABBREV_DOT_RE.sub(lambda m: m.group(1) + placeholder, text)
    parts = re.split(r"(?<=[.!?])\s+(?=[A-ZÀ-Ỵ\"'(\[])", protected)
    restored = [p.replace(placeholder, ".").strip() for p in parts if p.strip()]
    if 1 < len(restored) <= 3:
        return restored
    return [text]


def _format_decoding_config(beam_size, length_penalty, repetition_penalty, no_repeat_ngram_size, model_family):
    return (
        f"model={model_family},beam={beam_size},lp={float(length_penalty):g},rp={float(repetition_penalty):g},"
        f"ng={no_repeat_ngram_size},{PREPROC_VERSION},{POSTPROC_VERSION}"
    )


def translate_text(
    text,
    source_lang="en",
    target_lang="vi",
    beam_size=None,
    length_penalty=None,
    repetition_penalty=None,
    no_repeat_ngram_size=None,
    max_decoding_length=None,
    diagnostic=False,
    routing_enabled=None,
    experimental_zh_vi=None,
    allow_fallback=None,
    model_override=None,
    custom_models_root=None,
    max_loaded_models=None,
):
    """
    Translates text from source_lang to target_lang using the active offline neural router
    (or legacy single loaded M2M-100 model) and returns translation + telemetry metadata.
    """
    global _active_inference_count

    text = (text or "").strip()
    src_norm = normalize_iso_lang(source_lang)
    tgt_norm = normalize_iso_lang(target_lang)
    if tgt_norm == "auto":
        tgt_norm = "vi"

    if not text:
        return {
            "translated_text": "",
            "raw_translated_text": "",
            "model_used": current_model_id or MODEL_ID_M2M100,
            "model_name": MODEL_REGISTRY.get(current_model_id or MODEL_ID_M2M100, {}).get("display_name", "M2M-100 418M (INT8 Neural)"),
            "model_fingerprint": current_model_fingerprint,
            "requested_model": current_model_id or MODEL_ID_M2M100,
            "fallback_used": False,
            "source_language": src_norm,
            "target_language": tgt_norm,
            "decoding_config": "",
            "model_load_ms": 0,
            "latency_ms": 0,
            "tokenize_ms": 0.0,
            "infer_ms": 0.0,
            "postproc_ms": 0.0,
        }

    t_total0 = time.perf_counter()

    use_router = router_config["enabled"] if routing_enabled is None else bool(routing_enabled)
    eff_root = custom_models_root if custom_models_root is not None else (router_config.get("models_root") or None)

    # Preserve backwards compatibility when a custom M2M-100 directory was loaded via load_model/--model-dir
    # while routing is disabled and no override/custom root was requested.
    if not use_router and not model_override and MODEL_ID_M2M100 in _loaded_models:
        loaded_m2m = _loaded_models[MODEL_ID_M2M100]
        eff_src = src_norm if src_norm != "auto" else detect_language_hint(text)
        route = {
            "model_id": MODEL_ID_M2M100,
            "requested_model": MODEL_ID_M2M100,
            "fallback_used": False,
            "model_dir": loaded_m2m["model_dir"],
            "src": eff_src,
            "tgt": tgt_norm,
        }
    else:
        route = select_route(
            source_lang,
            target_lang,
            routing_enabled=routing_enabled,
            experimental_zh_vi=experimental_zh_vi,
            allow_fallback=allow_fallback,
            model_override=model_override,
            text_hint=text,
            custom_models_root=eff_root,
        )

    model_id = route["model_id"]
    src = route["src"]
    tgt = route["tgt"]
    spec = MODEL_REGISTRY[model_id]

    entry, model_load_ms = acquire_model(
        model_id,
        model_dir=route["model_dir"],
        device=router_config.get("device", "cpu"),
        intra_threads=router_config.get("threads"),
        max_loaded_models=max_loaded_models,
    )

    family = entry["family"]
    default_beam = spec["default_beam"]
    if family == "envit5" and src == "vi":
        default_beam = 2
    eff_beam = max(1, int(default_beam if beam_size is None else beam_size))
    eff_lp = float(spec["default_lp"] if length_penalty is None else length_penalty)
    eff_rp = float(spec["default_rp"] if repetition_penalty is None else repetition_penalty)
    eff_ng = int(spec["default_ng"] if no_repeat_ngram_size is None else no_repeat_ngram_size)
    eff_max_len = int(spec["default_max_len"] if max_decoding_length is None else max_decoding_length)

    working_text = preprocess_source_for_nmt(
        text,
        src,
        normalize_zh_trad_to_simp=(family == "m2m100"),
    )

    _active_inference_count += 1
    try:
        t_tok0 = time.perf_counter()
        diag_extra = {}

        if family == "m2m100":
            tok = entry["tokenizer"]
            m2m_tgt = LANG_CODE_MAP.get(tgt, "__vi__")
            tok.src_lang = src
            tokens = tok.convert_ids_to_tokens(tok.encode(working_text))
            tokenize_ms = (time.perf_counter() - t_tok0) * 1000.0

            t_inf0 = time.perf_counter()
            results = entry["translator"].translate_batch(
                [tokens],
                target_prefix=[[m2m_tgt]],
                beam_size=eff_beam,
                length_penalty=eff_lp,
                max_decoding_length=eff_max_len,
                repetition_penalty=eff_rp,
                no_repeat_ngram_size=eff_ng,
            )
            infer_ms = (time.perf_counter() - t_inf0) * 1000.0

            t_post0 = time.perf_counter()
            hyp = results[0].hypotheses[0]
            out_tokens = hyp[1:] if hyp and hyp[0] == m2m_tgt else hyp
            out_ids = tok.convert_tokens_to_ids(out_tokens)
            raw_translated_text = tok.decode(out_ids, skip_special_tokens=True)
            diag_extra = {
                "src_token": tok.lang_code_to_token.get(src, f"__{src}__"),
                "tgt_token": m2m_tgt,
            }

        elif family == "envit5":
            tok = entry["tokenizer"]
            chunks = _split_envit5_sentences(working_text)
            batch_tokens = []
            for seg in chunks:
                tok_ids = tok.encode(f"{src}: {seg}")
                seg_tokens = tok.convert_ids_to_tokens(tok_ids)
                if not seg_tokens or seg_tokens[-1] != "</s>":
                    seg_tokens.append("</s>")
                batch_tokens.append(seg_tokens)
            tokenize_ms = (time.perf_counter() - t_tok0) * 1000.0

            t_inf0 = time.perf_counter()
            results = entry["translator"].translate_batch(
                batch_tokens,
                beam_size=eff_beam,
                length_penalty=eff_lp,
                max_decoding_length=eff_max_len,
                repetition_penalty=eff_rp,
                no_repeat_ngram_size=eff_ng,
            )
            infer_ms = (time.perf_counter() - t_inf0) * 1000.0

            t_post0 = time.perf_counter()
            decoded_parts = []
            for r_item in results:
                hyp = r_item.hypotheses[0]
                out_ids = tok.convert_tokens_to_ids(hyp)
                piece = tok.decode(out_ids, skip_special_tokens=True)
                piece = re.sub(r"^(?:vi|en):\s*", "", (piece or "").strip(), flags=re.IGNORECASE).strip()
                if piece:
                    decoded_parts.append(piece)
            raw_translated_text = " ".join(decoded_parts)
            diag_extra = {
                "task_prefix": f"{src}:",
                "sentence_chunks": len(chunks),
            }

        elif family == "opus_zh_vi":
            sp_src = entry["sp_source"]
            sp_tgt = entry["sp_target"]
            tokens = sp_src.encode(working_text, out_type=str)
            if not tokens or tokens[-1] != "</s>":
                tokens.append("</s>")
            tokenize_ms = (time.perf_counter() - t_tok0) * 1000.0

            t_inf0 = time.perf_counter()
            results = entry["translator"].translate_batch(
                [tokens],
                beam_size=eff_beam,
                length_penalty=eff_lp,
                max_decoding_length=eff_max_len,
                repetition_penalty=eff_rp,
                no_repeat_ngram_size=eff_ng,
            )
            infer_ms = (time.perf_counter() - t_inf0) * 1000.0

            t_post0 = time.perf_counter()
            hyp = results[0].hypotheses[0]
            clean_hyp = [tok for tok in hyp if tok not in ("</s>", "<pad>")]
            raw_translated_text = sp_tgt.decode(clean_hyp)
            diag_extra = {
                "trad_to_simp_normalized": False,
            }
        else:
            raise ValueError(f"Unsupported model family: {family}")

        final_text = refine_target_translation(raw_translated_text, tgt, source_text=text, model_family=family)
        postproc_ms = (time.perf_counter() - t_post0) * 1000.0
    finally:
        _active_inference_count -= 1

    dec_cfg = _format_decoding_config(eff_beam, eff_lp, eff_rp, eff_ng, family)
    latency_ms = int((time.perf_counter() - t_total0) * 1000.0)

    out = {
        "translated_text": final_text,
        "raw_translated_text": raw_translated_text,
        "model_used": model_id,
        "model_name": entry["display_name"],
        "model_fingerprint": entry["fingerprint"],
        "requested_model": route["requested_model"],
        "fallback_used": route["fallback_used"],
        "source_language": src,
        "target_language": tgt,
        "decoding_config": dec_cfg,
        "model_load_ms": model_load_ms,
        "latency_ms": latency_ms,
        "tokenize_ms": round(tokenize_ms, 2),
        "infer_ms": round(infer_ms, 2),
        "postproc_ms": round(postproc_ms, 2),
        "loaded_models": list(_loaded_models.keys()),
    }
    if diagnostic:
        out["diagnostic"] = {
            "source_text": text,
            "normalized_input": working_text,
            "encoded_tokens": tokens,
            "raw_output_tokens": hyp,
            "unk_count": tokens.count("<unk>"),
            **diag_extra,
        }
    return out


def run_self_test(model_dir=None, use_router=False):
    """Runs a quick self-test across all 6 directions."""
    print(f"--- ScreenSub AI Translation Worker Self-Test (router={use_router}) ---")
    try:
        if not use_router or model_dir:
            load_ms = load_neural_model(model_dir)
            print(f"Initial model loaded in {load_ms} ms.")

        test_pairs = [
            ("We're just having a drink.", "en", "vi"),
            ("Xin chào, rất vui được gặp bạn!", "vi", "en"),
            ("你今天有时间吗？", "zh", "vi"),
            ("Hôm nay bạn có rảnh không?", "vi", "zh"),
            ("What are you doing tonight?", "en", "zh"),
            ("今天晚上你在做什么？", "zh", "en"),
        ]

        for text, src, tgt in test_pairs:
            res = translate_text(text, src, tgt, routing_enabled=use_router)
            print(
                f"[{src} -> {tgt}] [{res['model_used']}] (load={res['model_load_ms']}ms, "
                f"total={res['latency_ms']}ms): '{text}' -> '{res['translated_text']}'"
            )
        print("Self-test PASSED!")
    except Exception as e:
        print(f"Self-test FAILED: {e}")


def main():
    parser = argparse.ArgumentParser(description="ScreenSub AI Neural Translation Worker")
    parser.add_argument("--test", action="store_true", help="Run self-test across 6 directions")
    parser.add_argument("--ping", action="store_true", help="Print ping status and exit")
    parser.add_argument("--model-dir", type=str, default="", help="Path to M2M-100 model directory")
    parser.add_argument("--models-root", type=str, default="", help="Custom models root directory")
    parser.add_argument("--router", action="store_true", help="Enable offline multi-model router")
    parser.add_argument("--no-experimental-zh-vi", action="store_true", help="Disable Opus-MT Zh-Vi experimental route")
    parser.add_argument("--max-loaded-models", type=int, default=1, help="Max simultaneously loaded CT2 models")
    parser.add_argument("--threads", type=int, default=0, help="Intra-op CPU threads (0=auto)")
    parser.add_argument("--lazy-load", action="store_true", help="Skip eager startup model load")
    args = parser.parse_args()

    router_config["enabled"] = bool(args.router)
    router_config["experimental_zh_vi"] = not bool(args.no_experimental_zh_vi)
    router_config["max_loaded_models"] = max(1, int(args.max_loaded_models))
    router_config["models_root"] = args.models_root or ""
    if args.threads > 0:
        router_config["threads"] = int(args.threads)

    if args.ping:
        eff_root = router_config["models_root"] or None
        status = {
            "status": "ok",
            "pong": True,
            "ctranslate2": CTRANSLATE2_AVAILABLE,
            "transformers": TRANSFORMERS_AVAILABLE,
            "sentencepiece": SENTENCEPIECE_AVAILABLE,
            "default_model_dir": resolve_default_model_dir(),
            "router_enabled": router_config["enabled"],
            "registry": get_registry_status(eff_root),
        }
        print(json.dumps(status, ensure_ascii=False))
        return

    if args.test:
        run_self_test(args.model_dir or None, use_router=router_config["enabled"])
        return

    # Auto-load default M2M-100 at startup unless deferred via --lazy-load or explicit router mode
    if not args.lazy_load and (not router_config["enabled"] or args.model_dir):
        default_dir = args.model_dir or resolve_default_model_dir()
        if default_dir and os.path.exists(default_dir):
            try:
                load_neural_model(default_dir)
            except Exception as e:
                sys.stderr.write(f"[Translation Worker] Auto-load error: {e}\n")
                sys.stderr.flush()

    sys.stderr.write("[Translation Worker] Started. Ready for JSON-lines requests.\n")
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
                eff_root = router_config.get("models_root") or None
                active_spec = MODEL_REGISTRY.get(current_model_id or MODEL_ID_M2M100, MODEL_REGISTRY[MODEL_ID_M2M100])
                resp = {
                    "id": req_id,
                    "status": "ok",
                    "pong": True,
                    "model_loaded": len(_loaded_models) > 0,
                    "model_id": current_model_id,
                    "model_dir": current_model_dir,
                    "model_name": active_spec["display_name"] if len(_loaded_models) > 0 else "MODEL NOT INSTALLED",
                    "model_fingerprint": current_model_fingerprint,
                    "decoding_config": _format_decoding_config(
                        active_spec["default_beam"],
                        active_spec["default_lp"],
                        active_spec["default_rp"],
                        active_spec["default_ng"],
                        active_spec["family"],
                    ),
                    "device": current_device,
                    "compute_type": current_compute_type,
                    "ctranslate2": CTRANSLATE2_AVAILABLE,
                    "router_enabled": router_config["enabled"],
                    "experimental_zh_vi": router_config["experimental_zh_vi"],
                    "allow_fallback": router_config["allow_fallback"],
                    "max_loaded_models": router_config["max_loaded_models"],
                    "loaded_models": list(_loaded_models.keys()),
                    "registry": get_registry_status(eff_root),
                }

            elif cmd == "configure_router":
                if "enabled" in req:
                    router_config["enabled"] = bool(req["enabled"])
                if "experimental_zh_vi" in req:
                    router_config["experimental_zh_vi"] = bool(req["experimental_zh_vi"])
                if "allow_fallback" in req:
                    router_config["allow_fallback"] = bool(req["allow_fallback"])
                if "max_loaded_models" in req:
                    router_config["max_loaded_models"] = max(1, int(req["max_loaded_models"]))
                if "models_root" in req:
                    router_config["models_root"] = req["models_root"] or ""
                if "device" in req and req["device"]:
                    router_config["device"] = str(req["device"])
                if "threads" in req:
                    router_config["threads"] = int(req["threads"]) if req["threads"] else None

                # Trim LRU cache if max_loaded_models was lowered and no inference is active
                cap = router_config["max_loaded_models"]
                if _active_inference_count == 0:
                    while len(_loaded_models) > cap:
                        evict_id = next(iter(_loaded_models))
                        _loaded_models.pop(evict_id).clear()
                        gc.collect()
                    if _loaded_models:
                        _sync_legacy_globals(next(reversed(_loaded_models.items()))[1])
                    else:
                        _sync_legacy_globals(None)

                eff_root = router_config.get("models_root") or None
                resp = {
                    "id": req_id,
                    "status": "ok",
                    "router_enabled": router_config["enabled"],
                    "experimental_zh_vi": router_config["experimental_zh_vi"],
                    "allow_fallback": router_config["allow_fallback"],
                    "max_loaded_models": router_config["max_loaded_models"],
                    "loaded_models": list(_loaded_models.keys()),
                    "registry": get_registry_status(eff_root),
                }

            elif cmd == "get_registry":
                eff_root = req.get("models_root") or router_config.get("models_root") or None
                resp = {
                    "id": req_id,
                    "status": "ok",
                    "router_enabled": router_config["enabled"],
                    "experimental_zh_vi": router_config["experimental_zh_vi"],
                    "allow_fallback": router_config["allow_fallback"],
                    "max_loaded_models": router_config["max_loaded_models"],
                    "loaded_models": list(_loaded_models.keys()),
                    "registry": get_registry_status(eff_root),
                }

            elif cmd == "load_model":
                req_mid = req.get("model_id") or None
                mdir = req.get("model_dir", "") or ""
                if not mdir and not req_mid:
                    mdir = resolve_default_model_dir()
                dev = req.get("device", router_config.get("device", "cpu"))
                threads_raw = req.get("threads", None)
                threads = int(threads_raw) if threads_raw is not None else None
                eff_mid = req_mid or (infer_model_id_from_dir(mdir) if mdir else MODEL_ID_M2M100)
                entry, load_ms = acquire_model(
                    eff_mid,
                    model_dir=mdir or None,
                    device=dev,
                    intra_threads=threads,
                )
                active_spec = MODEL_REGISTRY[entry["model_id"]]
                resp = {
                    "id": req_id,
                    "status": "ok",
                    "model_used": entry["model_id"],
                    "model_dir": entry["model_dir"],
                    "model_name": entry["display_name"],
                    "model_fingerprint": entry["fingerprint"],
                    "decoding_config": _format_decoding_config(
                        active_spec["default_beam"],
                        active_spec["default_lp"],
                        active_spec["default_rp"],
                        active_spec["default_ng"],
                        active_spec["family"],
                    ),
                    "device": entry["device"],
                    "load_time_ms": load_ms,
                    "loaded_models": list(_loaded_models.keys()),
                }

            elif cmd == "translate":
                text = req.get("text", "")
                src = req.get("source_lang", "auto")
                tgt = req.get("target_lang", "vi")
                beam_raw = req.get("beam_size", None)
                lp_raw = req.get("length_penalty", None)
                rp_raw = req.get("repetition_penalty", None)
                ng_raw = req.get("no_repeat_ngram_size", None)
                max_len_raw = req.get("max_decoding_length", None)
                diag = bool(req.get("diagnostic", False))

                res = translate_text(
                    text,
                    src,
                    tgt,
                    beam_size=int(beam_raw) if beam_raw is not None else None,
                    length_penalty=float(lp_raw) if lp_raw is not None else None,
                    repetition_penalty=float(rp_raw) if rp_raw is not None else None,
                    no_repeat_ngram_size=int(ng_raw) if ng_raw is not None else None,
                    max_decoding_length=int(max_len_raw) if max_len_raw is not None else None,
                    diagnostic=diag,
                    routing_enabled=req.get("routing_enabled", None),
                    experimental_zh_vi=req.get("experimental_zh_vi", None),
                    allow_fallback=req.get("allow_fallback", None),
                    model_override=req.get("model_id", None),
                    custom_models_root=req.get("models_root", None),
                    max_loaded_models=req.get("max_loaded_models", None),
                )
                resp = {
                    "id": req_id,
                    "status": "ok",
                    "translated_text": res["translated_text"],
                    "raw_translated_text": res["raw_translated_text"],
                    "model_used": res["model_used"],
                    "model_name": res["model_name"],
                    "model_fingerprint": res["model_fingerprint"],
                    "requested_model": res["requested_model"],
                    "fallback_used": res["fallback_used"],
                    "source_lang": src,
                    "target_lang": tgt,
                    "source_language": res["source_language"],
                    "target_language": res["target_language"],
                    "decoding_config": res["decoding_config"],
                    "model_load_ms": res["model_load_ms"],
                    "latency_ms": res["latency_ms"],
                    "tokenize_ms": res["tokenize_ms"],
                    "infer_ms": res["infer_ms"],
                    "postproc_ms": res["postproc_ms"],
                    "loaded_models": res["loaded_models"],
                }
                if "diagnostic" in res:
                    resp["diagnostic"] = res["diagnostic"]

            elif cmd == "unload":
                unload_neural_model(req.get("model_id", None))
                resp = {
                    "id": req_id,
                    "status": "ok",
                    "message": "Model unloaded",
                    "loaded_models": list(_loaded_models.keys()),
                }

            elif cmd == "quit":
                break

            else:
                resp = {"id": req_id, "status": "error", "message": f"Unknown command '{cmd}'"}

            sys.stdout.write(json.dumps(resp, ensure_ascii=False) + "\n")
            sys.stdout.flush()

        except KeyboardInterrupt:
            break
        except Exception as ex:
            err_resp = {"id": req.get("id", -1) if 'req' in locals() else -1, "status": "error", "message": str(ex)}
            sys.stdout.write(json.dumps(err_resp, ensure_ascii=False) + "\n")
            sys.stdout.flush()

if __name__ == "__main__":
    main()

