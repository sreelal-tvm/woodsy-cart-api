"""Read-only scan: which product photos carry vendor branding, prices or dimension text.

Usage:  .venv\\Scripts\\python scan.py [media_products_folder] [--workers 4]
Writes reports/scan-<date>.json and prints a summary. No image is modified.
"""
import argparse
import json
import os
import sys
import time
from collections import Counter
from concurrent.futures import ProcessPoolExecutor, as_completed
from datetime import datetime

DEFAULT_MEDIA = os.path.normpath(os.path.join(os.path.dirname(__file__), "..", "..", "src", "WoodsyCart.Api", "wwwroot", "media", "products"))


def _scan_one(path: str):
    os.environ.setdefault("OMP_NUM_THREADS", "2")
    from cleaner.engine import detect, is_studio, read
    try:
        img = read(path)
        items = detect(img)
        return {"path": path, "studio": bool(is_studio(img)), "w": img.shape[1], "h": img.shape[0],
                "texts": [{"text": i.text, "kind": i.kind, "action": i.action, "conf": i.confidence} for i in items]}
    except Exception as e:  # noqa: BLE001
        return {"path": path, "error": str(e), "texts": []}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("media", nargs="?", default=DEFAULT_MEDIA)
    ap.add_argument("--workers", type=int, default=4)
    args = ap.parse_args()

    files = []
    for d in sorted(os.listdir(args.media), key=lambda x: int(x) if x.isdigit() else 0):
        folder = os.path.join(args.media, d)
        if not os.path.isdir(folder):
            continue
        for f in sorted(os.listdir(folder)):
            stem, ext = os.path.splitext(f)
            if stem.endswith("_t") or stem.endswith("_orig") or ext.lower() not in (".webp", ".jpg", ".jpeg", ".png"):
                continue
            files.append(os.path.join(folder, f))

    print(f"Scanning {len(files)} product photos with {args.workers} workers…", flush=True)
    t0 = time.time()
    results = []
    with ProcessPoolExecutor(max_workers=args.workers) as pool:
        futures = [pool.submit(_scan_one, f) for f in files]
        for n, fut in enumerate(as_completed(futures), 1):
            results.append(fut.result())
            if n % 100 == 0 or n == len(files):
                print(f"  {n}/{len(files)}  ({time.time() - t0:.0f}s)", flush=True)

    kinds = Counter()
    per_image = Counter()
    for r in results:
        found = {t["kind"] for t in r["texts"]}
        for k in found:
            kinds[k] += 1
        acts = {t["action"] for t in r["texts"]}
        if "remove" in acts:
            per_image["needs_cleaning"] += 1
        if "review" in acts:
            per_image["needs_review"] += 1
        if "dimension" in found:
            per_image["has_dimensions"] += 1
        if not r["texts"]:
            per_image["no_text"] += 1
        per_image["studio" if r.get("studio") else "lifestyle"] += 1
        if r.get("error"):
            per_image["errors"] += 1

    products = {os.path.basename(os.path.dirname(r["path"])) for r in results if any(t["action"] == "remove" for t in r["texts"])}
    summary = {"photos": len(results), "products_with_vendor_marks": len(products), "seconds": round(time.time() - t0),
               "by_image": dict(per_image), "images_by_text_kind": dict(kinds)}

    os.makedirs(os.path.join(os.path.dirname(__file__), "reports"), exist_ok=True)
    out = os.path.join(os.path.dirname(__file__), "reports", f"scan-{datetime.now():%Y%m%d-%H%M}.json")
    with open(out, "w", encoding="utf8") as fh:
        json.dump({"summary": summary, "results": sorted(results, key=lambda r: r["path"])}, fh, ensure_ascii=False, indent=1)
    print(json.dumps(summary, indent=2))
    print(f"Report: {out}")


if __name__ == "__main__":
    sys.path.insert(0, os.path.dirname(__file__))
    main()
