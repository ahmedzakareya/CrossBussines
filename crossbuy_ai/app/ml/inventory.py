"""Inventory analysis (local, no LLM / no API key).

Classifies each item from on-hand quantity, value and outbound demand:
  - slow / dead stock   : holding stock with no issues across the window
  - reorder             : below its reorder point, or low days-of-cover
  - stockout risk       : zero stock with recent demand

TWO THINGS THIS REFUSES TO DO, BOTH FOUND BY MEASURING IT AGAINST THE REAL CATALOGUE
------------------------------------------------------------------------------------
1. IT WILL NOT DERIVE A DAILY RATE FROM A TOTAL. `out90 / slow_days` is right only when demand is
   spread across the window. In this catalogue it is not: 816 of 882 outbound movements fall in two
   months, and one of them put 2,470 units through a single day — which that formula reads as a
   steady 27 units/day for ever. A rate is now stated only when the DISTRIBUTION behind it
   (`activeDays`, `spanDays`, `maxDayQty`) can carry one, graded by the same rules the reorder
   engine uses. Where it cannot, no rate and no suggested quantity are emitted, and the item says
   why.

2. IT WILL NOT CALL AN ITEM DEAD WHEN THE WHOLE WINDOW IS QUIET. "No issues in 90 days" means
   dormant stock only if the business was issuing stock at all. Measured on 2026-11-08 the 90-day
   window held 4 active days, 35 movements, and 18 of 80 stocked items moving — so 60 items were
   being reported as 218,498.16 of dead capital when the finding was really about the WINDOW. When
   the window itself is near-empty the engine says so once, with the numbers, instead of accusing
   every item on the shelf.

Deterministic rules plus simple statistics. The model surfaces; the buyer decides.
"""
from __future__ import annotations

import pandas as pd

from app.ml.reorder import grade_history

# A window is too quiet to call anything dormant below this much activity. Expressed against the
# window length so a 30-day and a 365-day scan are judged on the same footing, with an absolute
# floor because a handful of days can never establish a pattern whatever the window.
QUIET_MIN_DAYS = 5
QUIET_MIN_SHARE = 0.10


def _as_of(d) -> pd.Timestamp:
    ts = pd.to_datetime(d) if d else pd.Timestamp.today()
    if ts.tzinfo is not None:
        ts = ts.tz_localize(None)
    return ts.normalize()


def _say(ar: str, en: str) -> dict:
    return {"ar": ar, "en": en}


def analyze_inventory(as_of, slow_days: int, items: list[dict],
                      window_active_days: int | None = None,
                      window_movements: int | None = None) -> dict:
    as_of_ts = _as_of(as_of)
    slow_days = max(1, int(slow_days))

    # ---- is the window itself usable? -------------------------------------------------
    quiet = False
    quiet_note = None
    if window_active_days is not None:
        need = max(QUIET_MIN_DAYS, int(slow_days * QUIET_MIN_SHARE))
        if int(window_active_days) < need:
            quiet = True
            quiet_note = _say(
                f"النافذة نفسها شبه فارغة: {window_active_days} يوم حركة فقط خلال {slow_days} يوم"
                f"{f' ({window_movements} حركة)' if window_movements is not None else ''}."
                " لذلك لا يُصنَّف أي صنف كراكد — السكون هنا وصف للفترة لا للأصناف.",
                f"The window itself is nearly empty: only {window_active_days} active day(s) in"
                f" {slow_days}"
                f"{f' ({window_movements} movements)' if window_movements is not None else ''}."
                " No item is therefore classed as dormant — the quiet describes the period, not the"
                " items.")

    flagged = []
    slow = reorder = stockout = 0
    dead_value = 0.0
    ungraded = 0

    for it in items:
        on_hand = float(it.get("onHand") or 0)
        value = float(it.get("value") or 0)
        out90 = float(it.get("out90") or 0)
        out30 = float(it.get("out30") or 0)
        # DEFENCE IN DEPTH against the null-became-zero bug this screen shipped with. A reorder
        # point of exactly 0 means "reorder when you hit nothing", which is not a trigger anyone
        # sets on purpose, and it makes `on_hand <= rp` true for EVERY out-of-stock item. The
        # caller has been fixed to send null; treating 0 as unset here means a future caller that
        # regresses cannot make the screen accuse 42 items again.
        rp = it.get("reorderPoint")
        rp = float(rp) if rp is not None else None
        if rp is not None and rp <= 0:
            rp = None

        # ---- can a rate be stated at all? ---------------------------------------------
        active_days = it.get("activeDays")
        has_dist = active_days is not None
        avg_daily = None
        days_cover = None
        grade = None
        rate_notes: list = []

        if has_dist:
            active_days = int(active_days or 0)
            span_days = int(it.get("spanDays") or 0)
            max_day = float(it.get("maxDayQty") or 0)
            share = (max_day / out90) if out90 > 0 else 0.0
            if out90 > 0 and active_days > 0:
                grade, _conf, rate_notes = grade_history(active_days, span_days, share)
                if grade != "D":
                    avg_daily = out90 / slow_days
                    days_cover = (on_hand / avg_daily) if avg_daily > 0 else None
                else:
                    ungraded += 1
        else:
            # No distribution was sent, so the shape of the demand is unknown. Saying nothing is
            # the honest option; the previous code said out90/slow_days and meant nothing by it.
            ungraded += 1
            rate_notes = [_say(
                "لم تُرسَل توزيعة الطلب، فلا يمكن تقدير معدل يومي",
                "Demand distribution was not supplied, so no daily rate can be estimated")]

        cls = None
        suggest = None
        reasons = []

        if on_hand > 0 and out90 == 0:
            # Dormant — but only sayable when the window had business in it at all.
            if quiet:
                continue
            cls = "slow"; slow += 1; dead_value += value
            reasons.append(_say(f"راكد/بطيء الحركة (لا صرف خلال {slow_days} يوم)",
                                f"Slow / dead stock (no issues in {slow_days} days)"))
        elif on_hand <= 0 and out30 > 0:
            # Needs no rate: stock is zero and something was issued recently. Always sayable.
            cls = "stockout"; stockout += 1
            reasons.append(_say("نفاد مخزون مع طلب حديث", "Out of stock with recent demand"))
        elif rp is not None and on_hand <= rp:
            # Compares two recorded numbers, so this too needs no estimated rate.
            cls = "reorder"; reorder += 1
            if avg_daily is not None:
                suggest = round(max(avg_daily * 30 - on_hand, rp - on_hand), 2)
            reasons.append(_say("تحت نقطة إعادة الطلب", "Below reorder point"))
        elif days_cover is not None and days_cover < 14:
            cls = "reorder"; reorder += 1
            suggest = round(avg_daily * 30, 2)
            reasons.append(_say(f"تغطية منخفضة (~{round(days_cover)} يوم)",
                                f"Low days-of-cover (~{round(days_cover)} days)"))

        if not cls:
            continue

        # Why a number is missing is part of the answer, not an omission.
        if suggest is None and cls == "reorder":
            reasons.append(_say(
                "لم تُقترح كمية: تاريخ الصرف لا يسند معدلًا يوميًا",
                "No quantity proposed: the issue history cannot support a daily rate"))
        reasons.extend(rate_notes)

        flagged.append({
            "itemId": it.get("itemId"),
            "code": it.get("code"),
            "name": it.get("name"),
            "onHand": round(on_hand, 2),
            "value": round(value, 2),
            "avgDailyUsage": round(avg_daily, 3) if avg_daily is not None else None,
            "daysCover": round(days_cover, 1) if days_cover is not None else None,
            "historyGrade": grade,
            "class": cls,
            "suggestedReorderQty": suggest,
            "reasons": reasons,
        })

    # Severity: stockout/reorder need action now. Slow-moving is graded by its share of total
    # dead-stock value, so the few big capital-tied items surface rather than drowning in the tail.
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
        "windowActiveDays": window_active_days,
        "windowMovements": window_movements,
        "windowTooQuiet": quiet,
        "windowNote": quiet_note,
        "rateNotEstimable": ungraded,
        "summary": {
            "slowMoving": slow,
            "reorder": reorder,
            "stockoutRisk": stockout,
            "deadStockValue": round(dead_value, 2),
        },
        "flagged": flagged,
    }
