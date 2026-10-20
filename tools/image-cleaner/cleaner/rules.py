"""Decide what each piece of text found on a product photo is, and whether to remove it."""
import re
from difflib import SequenceMatcher

# The vendor's own marks (OCR often misreads faint watermarks, so matching is fuzzy).
VENDOR_FUZZY = ["gujjubazar", "gujubazar", "gujjubazaar", "gbfurniture", "gbfurniturein", "gujubazarcom"]
VENDOR_FRAGMENTS = ["gujj", "gujub", "gujjub", "gbfurn", "gbfur", "furniture.in", "furniturein", "jubazar", "ubazar", "w.guju", "azar.co"]
# Other shops whose watermarks appear on photos the vendor re-uses.
OTHER_BRANDS = ["furniturecafe", "furnituremall", "thefurnituremall", "furniturewala", "qualityfurniture", "urniturewala",
                "niturecafe", "nituremall", "turecafe", "turemall", "furniturem", "furnituresampl", "samplehouse"]
EXACT_MARKS = {"gb", "gbf", "g.b", "gujju", "guju", "bazar", "bazaar", "gbfurniture"}

PRICE = re.compile(r"(₹|rs\.?|inr|mrp|price|offer|only)\s*[:\-]?\s*\d|\d[\d,]*\s*(/-|rs\b|₹)|^\D?\d{3,6}\s*/-?$", re.I)
DIMENSION = re.compile(
    r"(\d+(\.\d+)?\s*(\"|''|”|inch(es)?|in\b|ft\b|feet|foot|cm\b|mm\b|m\b|')"   # 72" / 6 ft / 180 cm
    r"|\d+(\.\d+)?\s*[x×*]\s*\d"                                                 # 72 x 60
    r"|\b(l|w|h|d|length|width|height|depth|dia|size)\s*[:\-]?\s*\d"             # H: 36
    r"|\d(\.\d+)?ft)",                                                           # 6FTTALL1.5FTWIDE
    re.I)
MODEL_CODE = re.compile(r"^[A-Za-z]{1,4}[-\s]?\d{2,5}[A-Za-z]?$")
PHONE = re.compile(r"(\+?91[\s-]?)?[6-9]\d{9}\b")


def _norm(text: str) -> str:
    return re.sub(r"[^a-z0-9.]", "", text.lower())


def vendor_score(text: str) -> float:
    """0..1 — how much the text looks like the vendor's name / website."""
    n = _norm(text)
    if not n:
        return 0.0
    flat = n.replace(".", "")
    if any(f in n for f in VENDOR_FRAGMENTS) or any(b in flat for b in OTHER_BRANDS):
        return 1.0
    # "gbfurniture" must start with gb — the bare word "furniture" is not a brand.
    candidates = [v for v in VENDOR_FUZZY if not v.startswith("gbfurn") or flat.startswith("gb")]
    return max((SequenceMatcher(None, flat, v).ratio() for v in candidates), default=0.0)


def classify(text: str, confidence: float) -> tuple[str, str]:
    """Returns (kind, action). action: remove | keep | review."""
    t = text.strip()
    n = _norm(t)
    if not n:
        return "noise", "keep"
    flat = n.replace(".", "")

    if flat in EXACT_MARKS:
        return "vendor", "remove"
    v = vendor_score(t)
    if v >= 0.72:
        return "vendor", "remove"
    # Faint "gujjubazar" watermarks are often misread ("gbyjubuzur", "gujubuzar"): similar shape + the z-a-r ending.
    if v >= 0.55 and len(n) >= 7 and ("z" in n[-5:] or "bazr" in n):
        return "vendor", "remove"
    if PRICE.search(t) or re.fullmatch(r"rs\d[\d,]*", n) or re.fullmatch(r"\d{4,6}[1/]?\s*-", t):
        return "price", "remove"
    if PHONE.search(t.replace(" ", "")):
        return "phone", "remove"
    if any(n.endswith(x) for x in (".in", ".com", ".co")) or n.startswith("www") or ".com" in n:
        return "website", "remove"
    if DIMENSION.search(t):
        return "dimension", "keep"
    if MODEL_CODE.match(t):
        return "model", "keep"
    if "furnit" in flat or "urniture" in flat:
        return "brand?", "review"      # e.g. a cut-off "…Furniture Mall"; a plain "Furniture" label is checked, not removed
    if v >= 0.55:
        return "vendor?", "review"
    # A bare 4–6 digit number is often a price ("34500") but could be a code — let a human decide.
    if re.fullmatch(r"\d{4,6}", n):
        return "number", "review"
    return "text", "keep"
