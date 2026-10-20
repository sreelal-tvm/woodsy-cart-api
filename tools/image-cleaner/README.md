# WoodsyCart image cleaner

A local, offline service that tidies vendor product photos for WoodsyCart:

- **Removes** the vendor's name and logo (GB Furniture, gbfurniture.in, Gujju Bazar), websites, prices printed on photos (e.g. "Rs 34500") and phone numbers.
- **Keeps** dimensions (72" × 60", 6 ft, 180 cm, H: 36), sizes and model codes (F120B).
- **Studio photos** (product on white): removes the blurred side copies the vendor adds to widen photos, makes the background pure white, and centres the product on a 1200 × 1200 canvas.
- **Room photos:** gentle light and colour correction only. They are never cropped.

Originals are always kept next to the cleaned files (`{id}.webp` is the original, `{id}_c.webp` the cleaned photo, `{id}_ct.webp` its thumbnail). Every result can be approved or switched back to the original in **Admin → Catalogue → Image clean-up**.

## How it runs

The WoodsyCart API starts this service automatically (`ImageCleaner:AutoStart` in `appsettings.json`) and sends it:

- new photos downloaded by the **vendor sync**, and
- photos **uploaded manually** on a product page.

You can also run it on all existing photos from the Image clean-up page.

## Install (once)

```bash
cd tools/image-cleaner
python -m venv .venv
.venv\Scripts\python -m pip install -r requirements.txt
```

To run it by hand: `.venv\Scripts\python server.py`. It listens on `http://127.0.0.1:5200` and has a `/health` check.

### Better background repair (optional)

With `models/lama_fp32.onnx` present, removed areas on rugs, wood and fabric are rebuilt by the LaMa AI model instead of OpenCV's standard repair, which can look blurry on textured backgrounds.

The model comes from <https://huggingface.co/Carve/LaMa-ONNX>: 208 MB, Apache-2.0 licence. Put it in the `models` folder and restart the API.

## Scan only (no changes)

```bash
.venv\Scripts\python scan.py --workers 3
```

This lists which photos carry vendor names, prices or dimension text, and writes a report to `reports/`.

## Rules

The rules are in `cleaner/rules.py`. Add new vendor names to `VENDOR_NAMES` / `VENDOR_FRAGMENTS`. Text the tool isn't sure about (for example a bare "34500") is never removed automatically; the photo is marked **To review** instead.
