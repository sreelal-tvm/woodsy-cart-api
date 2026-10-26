"""Replace a product photo's background with a white studio background — fully offline.

The product is cut out with a background-removal model, placed on a white square canvas with a soft contact shadow
under its base, and centred the same way as the cleaner's studio shots. The product's own pixels are never changed.

Models (ONNX, in models/, not in git — see README):
  fast      models/isnet/isnet-general-use.onnx           IS-Net, ~6 s per photo on a laptop CPU
  detailed  models/birefnet/birefnet-general-lite.onnx    BiRefNet lite, 1-2 min; better on busy backgrounds
"""
from __future__ import annotations

import gc
import os
import time
from dataclasses import asdict, dataclass

import cv2
import numpy as np
import onnxruntime as ort

from .engine import MODELS_DIR, read, write

MODELS = {
    "fast": os.path.join(MODELS_DIR, "isnet", "isnet-general-use.onnx"),
    "detailed": os.path.join(MODELS_DIR, "birefnet", "birefnet-general-lite.onnx"),
}
_fast: ort.InferenceSession | None = None


def available() -> dict[str, bool]:
    return {name: os.path.exists(path) for name, path in MODELS.items()}


def _session(model: str) -> ort.InferenceSession:
    """The fast model stays loaded; the detailed one (about 1 GB of memory) is loaded per photo and released after."""
    global _fast
    if model == "fast":
        if _fast is None:
            _fast = ort.InferenceSession(MODELS["fast"], providers=["CPUExecutionProvider"])
        return _fast
    return ort.InferenceSession(MODELS[model], providers=["CPUExecutionProvider"])


@dataclass
class BackgroundReport:
    status: str = "done"            # done | failed
    model: str = "fast"
    seconds: float = 0.0
    coverage: float = 0.0           # share of the photo the product covers (0-1)
    message: str = ""

    def to_dict(self) -> dict:
        return asdict(self)


def _matte(sess: ort.InferenceSession, model: str, rgb: np.ndarray) -> np.ndarray:
    h, w = rgb.shape[:2]
    x = cv2.resize(rgb, (1024, 1024), interpolation=cv2.INTER_LANCZOS4).astype(np.float32) / 255.0
    if model == "detailed":
        x = (x - np.array([0.485, 0.456, 0.406], np.float32)) / np.array([0.229, 0.224, 0.225], np.float32)
    else:
        x = x - 0.5
    out = sess.run(None, {sess.get_inputs()[0].name: x.transpose(2, 0, 1)[None].astype(np.float32)})[0][0, 0]
    if model == "detailed":
        out = 1 / (1 + np.exp(-out))
    out = (out - out.min()) / (out.max() - out.min() + 1e-8)
    return cv2.resize(out, (w, h), interpolation=cv2.INTER_LINEAR)


def _solid(a: np.ndarray) -> np.ndarray:
    """Keep the largest object (the product) and make furniture solid: no see-through patches, only a soft edge."""
    h, w = a.shape
    n, lab, stats, _ = cv2.connectedComponentsWithStats((a > 0.5).astype(np.uint8), 8)
    if n > 1:
        keep = 1 + int(np.argmax(stats[1:, cv2.CC_STAT_AREA]))
        a = a * cv2.dilate((lab == keep).astype(np.uint8), np.ones((15, 15), np.uint8))
    a = np.clip((a - 0.25) / 0.35, 0, 1)
    n, lab, stats, _ = cv2.connectedComponentsWithStats((a <= 0.5).astype(np.uint8), 4)
    for i in range(1, n):
        x, y, bw, bh, area = stats[i]
        enclosed = x > 0 and y > 0 and x + bw < w and y + bh < h
        if enclosed and area < 0.01 * h * w:
            a[lab == i] = 1.0
    return a


def _compose(bgr: np.ndarray, a: np.ndarray, size: int, fill: float = 0.86) -> np.ndarray:
    ys, xs = np.where(a > 0.05)
    y0, y1, x0, x1 = ys.min(), ys.max() + 1, xs.min(), xs.max() + 1
    obj, al = bgr[y0:y1, x0:x1].astype(np.float32), a[y0:y1, x0:x1]
    s = fill * size / max(obj.shape[:2])
    nw, nh = max(1, int(obj.shape[1] * s)), max(1, int(obj.shape[0] * s))
    obj = cv2.resize(obj, (nw, nh), interpolation=cv2.INTER_AREA)
    al = cv2.resize(al, (nw, nh), interpolation=cv2.INTER_AREA)
    ox, oy = (size - nw) // 2, (size - nh) // 2

    # Contact shadow: darker where the lowest parts (legs, base) meet the floor, plus a faint wide shade.
    full = np.zeros((size, size), np.float32)
    full[oy:oy + nh, ox:ox + nw] = al
    base = np.zeros_like(full)
    band = max(2, int(nh * 0.08))
    base[oy + nh - band:oy + nh] = full[oy + nh - band:oy + nh]
    base = cv2.dilate(base, np.ones((5, 31), np.uint8))
    shadow = cv2.GaussianBlur(base, (0, 0), 9) * 0.45
    shadow += cv2.GaussianBlur(np.roll(full, int(nh * 0.03), axis=0), (0, 0), 30) * 0.10
    canvas = np.full((size, size, 3), 255.0, np.float32) * (1 - np.clip(shadow, 0, 0.55)[..., None])

    region = canvas[oy:oy + nh, ox:ox + nw]
    canvas[oy:oy + nh, ox:ox + nw] = region * (1 - al[..., None]) + obj * al[..., None]
    return np.clip(canvas, 0, 255).astype(np.uint8)


def replace(src: str, dst: str, thumb: str | None = None, model: str = "fast", size: int = 1200,
            thumb_size: int = 480) -> BackgroundReport:
    rep = BackgroundReport(model=model)
    start = time.time()
    if model not in MODELS:
        rep.status, rep.message = "failed", f"unknown model '{model}'"
        return rep
    if not os.path.exists(MODELS[model]):
        rep.status, rep.message = "failed", f"model not installed: {os.path.relpath(MODELS[model], MODELS_DIR)}"
        return rep
    sess = None
    try:
        bgr = read(src)
        sess = _session(model)
        a = _solid(_matte(sess, model, cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB)))
        rep.coverage = round(float((a > 0.5).mean()), 3)
        if rep.coverage < 0.02:
            rep.status, rep.message = "failed", "no product found in the photo"
            return rep
        out = _compose(bgr, a, size)
        write(dst, out, 85)
        if thumb:
            write(thumb, cv2.resize(out, (thumb_size, thumb_size), interpolation=cv2.INTER_AREA), 78)
    except Exception as ex:  # noqa: BLE001 — reported back to Admin instead of crashing the service
        rep.status, rep.message = "failed", str(ex)[:300]
    finally:
        if model != "fast":
            del sess
            gc.collect()
        rep.seconds = round(time.time() - start, 1)
    return rep
