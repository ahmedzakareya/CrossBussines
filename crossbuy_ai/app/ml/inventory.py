"""Inventory analysis (local, no LLM / no API key).

Classifies each item from on-hand quantity, value, and outbound demand:
  - slow / dead stock   : on hand but no issues over the window
  - reorder             : below reorder point, or low days-of-cover
  - stockout risk       : zero stock with recent demand
Computes average daily usage and days-of-cover, and a suggested reorder qty.
Deterministic rules + simple stats — the model surfaces, the buyer decides.
"""
from __future__ import annotations

import pandas as pd


def _as_of(d) -> pd.Timestamp:
    ts = pd.to_datetime(d) if d else pd.Timestamp.today()
    if ts.tzinfo is not None:
        ts = ts.tz_localize(None)
    return ts.normalize()


def analyze_inventory(as_of, slow_days: int, items: list[dict]) -> dict:
    as_of_ts = _as_of(as_of)
    slow_days = max(1, int(slow_days))

    flagged = []
    slow = reorder = stockout = 0
    dead_value = 0.0

    for it in items:
        on_hand = float(it.get("onHand") or 0)
        value = float(it.get("value") or 0)
        out90 = float(it.get("out90") or 0)
        out30 = float(it.get("out30") or 0)
        rp = it.get("reorderPoint")
        rp = float(rp) if rp is not None else None

        avg_daily = out90 / slow_days
        days_cover = (on_hand / avg_daily) if avg_daily > 0 else None

        cls = None
        suggest = None
        reasons = []
        if on_hand > 0 and out90 == 0:
            cls = "slow"; slow += 1; dead_value += value
            reasons.append({"ar": f"راكد/بطيء الحركة (لا صرف خلال {slow_days} يوم)",
                           "en": f"Slow / dead stock (no issues in {slow_days} days)"})
        elif on_hand <= 0 and out30 > 0:
            cls = "stockout"; stockout += 1
            reasons.append({"ar": "نفاد مخزون مع طلب حديث", "en": "Out of stock with recent demand"})
        elif rp is not None and on_hand <= rp:
            cls = "reorder"; reorder += 1
            suggest = round(max(avg_daily * 30 - on_hand, rp - on_hand), 2)
            reasons.append({"ar": "تحت نقطة إعادة الطلب", "en": "Below reorder point"})
        elif days_cover is not None and days_cover < 14:
            cls = "reorder"; reorder += 1
            suggest = round(avg_daily * 30, 2)
            reasons.append({"ar": f"تغطية منخفضة (~{round(days_cover)} يوم)",
                           "en": f"Low days-of-cover (~{round(days_cover)} days)"})

        if cls:
            flagged.append({
                "itemId": it.get("itemId"),
                "code": it.get("code"),
                "name": it.get("name"),
                "onHand": round(on_hand, 2),
                "value": round(value, 2),
                "avgDailyUsage": round(avg_daily, 3),
                "daysCover": round(days_cover, 1) if days_cover is not None else None,
                "class": cls,
                "suggestedReorderQty": suggest,
                "reasons": reasons,
            })

    # Severity: stockout/reorder need action now → high. Slow-moving is graded by
    # its share of total dead-stock value (no magic constant) so the few big
    # capital-tied items surface as high rather than drowning in the long tail.
    for f in flagged:
        if f["class"] in ("stockout", "reorder"):
            f["severity"] = "high"
        else:
            share = (f["value"] / dead_value) if dead_value > 0 else 0
            f["severity"] = "high" if share >= 0.10 else ("medium" if share >= 0.02 else "low")

    sev_rank = {"high": 0, "medium": 1, "low": 2}
    flagged.sort(key=lambda f: (sev_rank.get(f["severity"], 3), -f["value"]))

    return {
        "asOf": as_of_ts.date().isoformat(),
        "slowDays": slow_days,
        "itemsAnalyzed": len(items),
        "flaggedCount": len(flagged),
        "summary": {
            "slowMoving": slow,
            "reorder": reorder,
            "stockoutRisk": stockout,
            "deadStockValue": round(dead_value, 2),
        },
        "flagged": flagged,
    }
