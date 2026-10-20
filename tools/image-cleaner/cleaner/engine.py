"""Detect, remove and tidy vendor marks on product photos — fully offline.

Pipeline per image:
  1. OCR every text region (RapidOCR, ONNX models bundled in the package).
  2. Classify each region (rules.py): vendor name / price / phone → remove; dimensions / model codes → keep.
  3. Erase removed regions and rebuild the background (LaMa model if present, else OpenCV inpainting).
  4. Studio shots (product on white): pure-white background, product centred on a square canvas.
     Room / lifestyle shots: gentle exposure + white-balance correction only (never cropped).
"""
from __future__ import annotations

import os
from dataclasses import asdict, dataclass, field

import cv2
import numpy as np
from rapidocr_onnxruntime import RapidOCR

from .rules import classify

_ocr: RapidOCR | None = None
_lama = None
MODELS_DIR = os.path.join(os.path.dirname(os.path.dirname(__file__)), "models")
LAMA_PATH = os.path.join(MODELS_DIR, "lama_fp32.onnx")


def ocr() -> RapidOCR:
    global _ocr
    if _ocr is None:
        _ocr = RapidOCR()
    return _ocr


@dataclass
class TextItem:
    text: str
    confidence: float
    box: list[list[int]]          # 4 corner points
    kind: str
    action: str


@dataclass
class Report:
    texts: list[TextItem] = field(default_factory=list)
    style: str = "unknown"          # studio | lifestyle
    removed: int = 0
    kept: int = 0
    review: int = 0
    inpaint_engine: str = ""
    recentred: bool = False
    uncropped: bool = False         # blurred side panels removed
    enhanced: bool = False
    status: str = "clean"           # clean (nothing to do) | cleaned | needs_review | failed
    message: str = ""

    def to_dict(self):
        d = asdict(self)
        return d


def read(path: str) -> np.ndarray:
    data = np.fromfile(path, dtype=np.uint8)          # works with non-ASCII Windows paths
    img = cv2.imdecode(data, cv2.IMREAD_COLOR)
    if img is None:
        raise ValueError(f"Cannot read image {path}")
    return img


def write(path: str, img: np.ndarray, quality: int = 82) -> None:
    os.makedirs(os.path.dirname(path), exist_ok=True)
    ext = os.path.splitext(path)[1].lower()
    params = [cv2.IMWRITE_WEBP_QUALITY, quality] if ext == ".webp" else [cv2.IMWRITE_JPEG_QUALITY, quality]
    ok, buf = cv2.imencode(ext if ext in (".webp", ".jpg", ".jpeg", ".png") else ".webp", img, params)
    if not ok:
        raise ValueError("Encoding failed")
    buf.tofile(path)


# ---------------------------------------------------------------- detection

def detect(img: np.ndarray) -> list[TextItem]:
    res, _ = ocr()(img)
    items = []
    for box, text, conf in res or []:
        conf = float(conf)
        if conf < 0.45 or len(text.strip()) < 2:
            continue
        kind, action = classify(text, conf)
        items.append(TextItem(text=text, confidence=round(conf, 3), box=[[int(x), int(y)] for x, y in box], kind=kind, action=action))
    _join_split_marks(items)
    return items


def _join_split_marks(items: list[TextItem]) -> None:
    """A watermark is often read as separate words ("Furniture" + "Cafe", "Guju" + "Bazar").
    Unclear words sitting right next to a vendor mark are part of it — remove them too."""
    marks = [i for i in items if i.kind in ("vendor", "website")]
    for it in items:
        if it.action != "review" or it.kind not in ("brand?", "vendor?", "text"):
            continue
        x, y, w, h = cv2.boundingRect(np.array(it.box, np.int32))
        for m in marks:
            mx, my, mw, mh = cv2.boundingRect(np.array(m.box, np.int32))
            gap = max(h, mh) * 1.5
            if x < mx + mw + gap and mx < x + w + gap and y < my + mh + gap and my < y + h + gap:
                it.kind, it.action = "vendor", "remove"
                break


def is_studio(img: np.ndarray) -> bool:
    """Product photographed on a plain white/light background: the image border is almost all near-white."""
    h, w = img.shape[:2]
    b = max(4, int(min(h, w) * 0.04))
    border = np.concatenate([img[:b].reshape(-1, 3), img[-b:].reshape(-1, 3), img[:, :b].reshape(-1, 3), img[:, -b:].reshape(-1, 3)])
    near_white = np.all(border > 225, axis=1).mean()
    return near_white > 0.88


# ---------------------------------------------------------------- framing

def crop_blur_padding(img: np.ndarray) -> tuple[np.ndarray, int]:
    """Some vendor photos are widened with blurred copies of the photo down the sides ("blur fill").
    Those panels meet the real photo at a sharp, full-height vertical edge — find both edges and crop to the real photo."""
    h, w = img.shape[:2]
    gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY).astype(np.float32)
    gx = np.abs(cv2.Sobel(gray, cv2.CV_32F, 1, 0, ksize=3))
    strength = (gx > 25).mean(axis=0)                       # share of rows with an edge in each column
    def seam(cols):
        best = max(cols, key=lambda c: strength[c])
        return best if strength[best] > 0.42 else None
    left = seam(range(int(w * 0.05), int(w * 0.38)))
    right = seam(range(int(w * 0.62), int(w * 0.95)))
    if left is None or right is None:
        return img, -1
    inner = img[:, left + 2:right - 1]
    # The outer panels of a blur fill have far less fine detail than the real photo.
    def sharp(x):
        return cv2.Laplacian(cv2.cvtColor(x, cv2.COLOR_BGR2GRAY), cv2.CV_32F).var()
    si = sharp(inner)
    m = 8  # keep clear of the sharp boundary line itself
    for side in (img[:, :max(1, left - m)], img[:, min(w - 1, right + m):]):
        # Blurred copies: much softer than the real photo, yet not a plain wall (they still carry tones/shapes).
        if sharp(side) > si * 0.35 or float(cv2.cvtColor(side, cv2.COLOR_BGR2GRAY).std()) < 15:
            return img, -1
    if (right - left) < w * 0.35:
        return img, -1
    return inner, left + 2


# ---------------------------------------------------------------- removal

def _mask_for(img: np.ndarray, items: list[TextItem]) -> np.ndarray:
    h, w = img.shape[:2]
    mask = np.zeros((h, w), np.uint8)
    for it in items:
        pts = np.array(it.box, np.int32)
        x, y, bw, bh = cv2.boundingRect(pts)
        # Vendor watermarks usually have a round logo just before the text — extend the box to the left.
        extra_left = int(bh * 2.4) if it.kind in ("vendor", "website") and len(it.text) > 3 else int(bh * 0.35)
        # Short marks such as "gb" sit inside a round logo that is larger than the letters.
        logo = it.kind == "vendor" and len(it.text.strip()) <= 3
        pad = max(3, int(bh * (0.7 if logo else 0.25)))
        extra_right = int(bh * 0.6) if it.kind in ("vendor", "website", "price") else pad
        x0, y0 = max(0, x - extra_left), max(0, y - pad)
        x1, y1 = min(w, x + bw + extra_right), min(h, y + bh + pad)
        # A mark cut off by the photo edge: clear right up to the edge so no sliver of a letter remains.
        if x0 < bh * 1.5: x0 = 0
        if w - x1 < bh * 1.5: x1 = w
        if h - y1 < bh * 0.8: y1 = h
        mask[y0:y1, x0:x1] = 255
    return cv2.dilate(mask, np.ones((5, 5), np.uint8), iterations=1)


def _lama_session():
    global _lama
    if _lama is None and os.path.exists(LAMA_PATH):
        import onnxruntime as ort
        _lama = ort.InferenceSession(LAMA_PATH, providers=["CPUExecutionProvider"])
    return _lama


def _inpaint_lama(img: np.ndarray, mask: np.ndarray) -> np.ndarray:
    """LaMa works on 512×512; inpaint a crop around the mask and paste it back for full resolution."""
    sess = _lama_session()
    ys, xs = np.where(mask > 0)
    h, w = img.shape[:2]
    cx, cy = (xs.min() + xs.max()) // 2, (ys.min() + ys.max()) // 2
    size = int(max(xs.max() - xs.min(), ys.max() - ys.min()) * 2.2)
    size = min(max(size, 256), max(h, w))
    x0, y0 = max(0, min(w - size, cx - size // 2)), max(0, min(h - size, cy - size // 2))
    x1, y1 = min(w, x0 + size), min(h, y0 + size)
    crop, mcrop = img[y0:y1, x0:x1], mask[y0:y1, x0:x1]
    ch, cw = crop.shape[:2]
    inp = cv2.resize(cv2.cvtColor(crop, cv2.COLOR_BGR2RGB), (512, 512), interpolation=cv2.INTER_AREA).astype(np.float32) / 255.0
    m = (cv2.resize(mcrop, (512, 512), interpolation=cv2.INTER_NEAREST) > 0).astype(np.float32)
    out = sess.run(None, {sess.get_inputs()[0].name: inp.transpose(2, 0, 1)[None], sess.get_inputs()[1].name: m[None, None]})[0][0]
    out = out.transpose(1, 2, 0)
    if out.max() <= 1.5:
        out = out * 255.0
    out = cv2.cvtColor(np.clip(out, 0, 255).astype(np.uint8), cv2.COLOR_RGB2BGR)
    out = cv2.resize(out, (cw, ch), interpolation=cv2.INTER_CUBIC)
    result = img.copy()
    region = result[y0:y1, x0:x1]
    soft = cv2.GaussianBlur((mcrop > 0).astype(np.float32), (7, 7), 0)[..., None]
    result[y0:y1, x0:x1] = (out * soft + region * (1 - soft)).astype(np.uint8)
    return result


def remove_regions(img: np.ndarray, items: list[TextItem], studio: bool) -> tuple[np.ndarray, str]:
    targets = [i for i in items if i.action == "remove"]
    if not targets:
        return img, ""
    mask = _mask_for(img, targets)
    if studio:
        # On a white backdrop, paint white where the mark sat on background, inpaint where it overlaps the product.
        bg = np.all(img > 215, axis=2)
        around = cv2.dilate(mask, np.ones((15, 15), np.uint8)) > 0
        mostly_bg = bg[around].mean() if around.any() else 0
        if mostly_bg > 0.8:
            out = img.copy()
            out[mask > 0] = 255
            return out, "white-fill"
    if _lama_session() is not None:
        return _inpaint_lama(img, mask), "lama"
    return cv2.inpaint(img, mask, 5, cv2.INPAINT_TELEA), "opencv"


# ---------------------------------------------------------------- background & position

def recentre_studio(img: np.ndarray, size: int = 1200, fill: float = 0.86) -> tuple[np.ndarray, bool]:
    """Pure-white background; product (and kept text such as dimensions) centred and scaled on a square canvas."""
    gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
    fg = (np.min(img, axis=2) < 232).astype(np.uint8) * 255
    fg = cv2.morphologyEx(fg, cv2.MORPH_OPEN, np.ones((3, 3), np.uint8))
    n, labels, stats, _ = cv2.connectedComponentsWithStats(fg, 8)
    h, w = gray.shape
    keep = [i for i in range(1, n) if stats[i, cv2.CC_STAT_AREA] > h * w * 0.0004]
    if not keep:
        return img, False
    xs0 = min(stats[i, cv2.CC_STAT_LEFT] for i in keep)
    ys0 = min(stats[i, cv2.CC_STAT_TOP] for i in keep)
    xs1 = max(stats[i, cv2.CC_STAT_LEFT] + stats[i, cv2.CC_STAT_WIDTH] for i in keep)
    ys1 = max(stats[i, cv2.CC_STAT_TOP] + stats[i, cv2.CC_STAT_HEIGHT] for i in keep)
    obj = img[ys0:ys1, xs0:xs1].copy()
    # Flatten off-white background pixels inside the crop to pure white (removes grey cast and specks).
    lightness = np.min(obj, axis=2)
    obj[lightness >= 238] = 255
    oh, ow = obj.shape[:2]
    scale = (size * fill) / max(oh, ow)
    obj = cv2.resize(obj, (max(1, int(ow * scale)), max(1, int(oh * scale))), interpolation=cv2.INTER_AREA if scale < 1 else cv2.INTER_CUBIC)
    canvas = np.full((size, size, 3), 255, np.uint8)
    oy, ox = (size - obj.shape[0]) // 2, (size - obj.shape[1]) // 2
    canvas[oy:oy + obj.shape[0], ox:ox + obj.shape[1]] = obj
    return canvas, True


def enhance(img: np.ndarray) -> tuple[np.ndarray, bool]:
    """Gentle auto-levels and white balance — only when the photo is noticeably dull or tinted."""
    lab = cv2.cvtColor(img, cv2.COLOR_BGR2LAB)
    l = lab[..., 0]
    lo, hi = np.percentile(l, 0.5), np.percentile(l, 99.5)
    b, g, r = [float(c.mean()) for c in cv2.split(img)]
    tint = max(b, g, r) - min(b, g, r)
    if hi - lo > 215 and tint < 18:
        return img, False                      # already well exposed and neutral
    out = img.astype(np.float32)
    if hi - lo < 215:
        out = (out - lo) * (235.0 / max(1.0, hi - lo)) + 10
    if tint >= 18:                             # 50 % gray-world correction
        avg = (b + g + r) / 3
        gains = np.array([avg / max(b, 1), avg / max(g, 1), avg / max(r, 1)], np.float32)
        out = out * (1 + (gains - 1) * 0.5)
    return np.clip(out, 0, 255).astype(np.uint8), True


# ---------------------------------------------------------------- full pipeline

def process(src: str, dst: str | None, remove: bool = True, recentre: bool = True, enhance_photo: bool = True,
            max_size: int = 1200, thumb: str | None = None, thumb_size: int = 480) -> Report:
    rep = Report()
    try:
        img = read(src)
        # Read text on the full original (small, faint marks are found more reliably), then crop if needed.
        rep.texts = detect(img)
        if recentre:
            cropped, offset = crop_blur_padding(img)
            if offset >= 0:
                rep.uncropped = True
                width = cropped.shape[1]
                kept = []
                for t in rep.texts:
                    xs = [p[0] - offset for p in t.box]
                    if max(xs) <= 0 or min(xs) >= width:
                        continue          # text only on the blurred side panels — removed with them
                    t.box = [[min(max(p[0] - offset, 0), width - 1), p[1]] for p in t.box]
                    kept.append(t)
                rep.texts = kept
                img = cropped
        rep.removed = sum(1 for t in rep.texts if t.action == "remove")
        rep.kept = sum(1 for t in rep.texts if t.action == "keep")
        rep.review = sum(1 for t in rep.texts if t.action == "review")
        studio = is_studio(img)
        rep.style = "studio" if studio else "lifestyle"

        out = img
        if remove and rep.removed:
            out, rep.inpaint_engine = remove_regions(out, rep.texts, studio)
            # Check pass: read the cleaned photo again and remove anything left (e.g. half a large watermark).
            removed_boxes = [cv2.boundingRect(np.array(t.box, np.int32)) for t in rep.texts if t.action == "remove"]
            for _ in range(2):
                found = detect(out)
                for t in found:
                    # Fragments left exactly where a mark was removed ("rni" of "furniture.in") go too —
                    # never dimensions or model codes.
                    if t.action != "remove" and t.kind not in ("dimension", "model"):
                        x, y, bw, bh = cv2.boundingRect(np.array(t.box, np.int32))
                        for rx, ry, rw, rh in removed_boxes:
                            gap = max(bh, rh) * 1.5
                            if x < rx + rw + gap and rx < x + bw + gap and y < ry + rh + gap and ry < y + bh + gap:
                                t.kind, t.action = "vendor", "remove"
                                break
                left_over = [t for t in found if t.action == "remove"]
                if not left_over:
                    break
                out, _ = remove_regions(out, left_over, studio)
                rep.texts.extend(left_over)
                rep.removed += len(left_over)
        if recentre and studio:
            out, rep.recentred = recentre_studio(out, size=max_size)
        elif enhance_photo:
            out, rep.enhanced = enhance(out)

        changed = rep.removed > 0 or rep.recentred or rep.enhanced or rep.uncropped
        if rep.review:
            rep.status = "needs_review"
        elif rep.removed:
            rep.status = "cleaned"
        else:
            rep.status = "clean"
        if dst and changed:
            h, w = out.shape[:2]
            if max(h, w) > max_size:
                s = max_size / max(h, w)
                out = cv2.resize(out, (int(w * s), int(h * s)), interpolation=cv2.INTER_AREA)
            write(dst, out)
            if thumb:
                s = thumb_size / max(out.shape[:2])
                write(thumb, cv2.resize(out, (int(out.shape[1] * s), int(out.shape[0] * s)), interpolation=cv2.INTER_AREA), 78)
        rep.message = "changed" if changed else "unchanged"
    except Exception as e:  # noqa: BLE001 — report every failure to the caller
        rep.status = "failed"
        rep.message = str(e)
    return rep
