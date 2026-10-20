"""Local image-cleaning service used by the WoodsyCart API (http://127.0.0.1:5200).

Run:  .venv\\Scripts\\python server.py            (the API can also start it automatically — see README)
"""
import os
import sys
import threading

sys.path.insert(0, os.path.dirname(__file__))

import uvicorn  # noqa: E402
from fastapi import FastAPI, HTTPException  # noqa: E402
from pydantic import BaseModel  # noqa: E402

from cleaner import engine  # noqa: E402

app = FastAPI(title="WoodsyCart image cleaner", version="1.0")
# One photo at a time: OCR and LaMa each use every CPU core, so parallel requests only slow each other down.
_busy = threading.Lock()


class ProcessRequest(BaseModel):
    src: str                       # absolute path of the original image
    dst: str                       # where to write the cleaned image
    thumb: str | None = None       # where to write the cleaned thumbnail
    remove: bool = True            # remove vendor names, prices, phone numbers
    recentre: bool = True          # studio shots: white background + centred product
    enhance: bool = True           # room shots: gentle exposure / white balance
    maxSize: int = 1200


@app.get("/health")
def health():
    return {"ok": True, "inpaint": "lama" if os.path.exists(engine.LAMA_PATH) else "opencv"}


@app.post("/process")
def process(req: ProcessRequest):
    if not os.path.isfile(req.src):
        raise HTTPException(404, f"Source not found: {req.src}")
    with _busy:
        rep = engine.process(req.src, req.dst, remove=req.remove, recentre=req.recentre, enhance_photo=req.enhance,
                             max_size=req.maxSize, thumb=req.thumb)
    return rep.to_dict()


if __name__ == "__main__":
    port = int(os.environ.get("IMAGE_CLEANER_PORT", "5200"))
    engine.ocr()  # load models once at start-up
    engine._lama_session()
    uvicorn.run(app, host="127.0.0.1", port=port, log_level="warning")
