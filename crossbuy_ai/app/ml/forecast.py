"""Cash-flow projection (local, no LLM / no API key).

Projects the running cash balance over a horizon from outstanding AR/AP due dates. Overdue items
are expected in the first week. This is a deterministic treasury projection; a statistical model
can be layered on later once enough history accrues — the data path stays the same.

WHAT WAS WRONG WITH IT, AND WHY IT IS THE SAME FAULT AS THE OTHER THREE
----------------------------------------------------------------------
Every defect found in this service has been one disease: **a missing value becoming a confident
number**. 1,113 journal entries called suspicious because `contamination` is a quota. 60 dormant
items in a window that held four active days. A reorder point of 0 read as a threshold. Open
receivables of 106,543 against a ledger that said 827,682.

This module had three more, all shipping, all on a live screen:

1. **No flows at all produced a confident forecast.** With empty lists it returned
   `projectedEndBalance == openingCash` and `negativeRisk: false` — a flat line that reads as "you
   are fine". Nothing distinguished *no receivables are outstanding* from *nobody sent me the
   receivables*, and the second is a data fault wearing the first's clothes.

2. **A flow with no due date vanished.** `pd.to_datetime(None)` is NaT; `NaT < as_of` is False, so
   it was never pulled forward, and every weekly comparison against NaT is False, so it fell into
   no week at all. The money left the projection and no line said so.

3. **A flow beyond the horizon vanished the same way**, while the result called its subtotal
   `totalExpectedInflow` — the total of what was expected, except for the parts that were not
   counted.

The repair is the one the other three got: nothing is dropped silently, a total that is partial
says how much it is missing, and a judgement is only offered when the inputs could support it.
"""
from __future__ import annotations

from typing import Any

import pandas as pd


def _parse(d) -> pd.Timestamp:
    """NaT for anything unreadable — never an exception.

    pandas RAISES on a string it cannot parse, so one malformed date in one row took the whole
    endpoint down with a 500. A treasury projection that dies because a single due date is
    mistyped is less useful than one that reports the row it could not place, and a caller cannot
    repair what it never receives.

    .NET may serialize some dates tz-aware (UTC "Z") and others naive; the timezone is dropped so
    all comparisons are consistent at day granularity.
    """
    if d is None:
        return pd.NaT
    try:
        ts = pd.to_datetime(d)
    except Exception:
        return pd.NaT
    if ts is pd.NaT or pd.isna(ts):
        return pd.NaT
    if ts.tzinfo is not None:
        ts = ts.tz_localize(None)
    return ts.normalize()


def _amount(value) -> float | None:
    """None rather than 0.0 for anything unusable.

    Coercing a bad amount to zero is how a payment disappears while the total still looks like a
    total. The caller is told how many did this and what they were worth.
    """
    try:
        f = float(value)
    except (TypeError, ValueError):
        return None
    if f != f or f in (float("inf"), float("-inf")):   # NaN / infinity
        return None
    return f


def project_cashflow(opening_cash: float, as_of, horizon_days: int,
                     inflows: list[dict], outflows: list[dict]) -> dict:
    as_of = _parse(as_of)
    if as_of is pd.NaT:
        as_of = pd.Timestamp.today().normalize()

    horizon_days = max(0, int(horizon_days or 0))
    n_weeks = max(1, (horizon_days + 6) // 7)
    horizon_end = as_of + pd.Timedelta(days=7 * n_weeks)

    # ---- what we could NOT place, counted as we go ------------------------------------------
    excluded_no_date = {"count": 0, "amount": 0.0}
    excluded_beyond = {"count": 0, "amount": 0.0}
    excluded_unusable = {"count": 0}

    def prep(flows, sign):
        placed = []
        for f in flows or []:
            amount = _amount(f.get("amount"))
            if amount is None:
                excluded_unusable["count"] += 1
                continue

            d = _parse(f.get("date"))
            if d is pd.NaT:
                # UNDATED IS NOT UNDUE. It is money we know about and cannot schedule, so it is
                # reported rather than quietly left out of every week.
                excluded_no_date["count"] += 1
                excluded_no_date["amount"] += amount
                continue

            if d < as_of:            # overdue -> expected immediately (first week)
                d = as_of
            if d >= horizon_end:
                excluded_beyond["count"] += 1
                excluded_beyond["amount"] += amount
                continue

            placed.append({"date": d, "amount": amount * sign})
        return placed

    rows = prep(inflows, 1) + prep(outflows, -1)
    df = pd.DataFrame(rows) if rows else pd.DataFrame(columns=["date", "amount"])

    bal = float(opening_cash or 0.0)
    min_bal, min_week = bal, as_of.date().isoformat()
    total_in = total_out = 0.0
    periods = []
    for w in range(n_weeks):
        start = as_of + pd.Timedelta(days=7 * w)
        end = start + pd.Timedelta(days=7)
        if not df.empty:
            seg = df[(df["date"] >= start) & (df["date"] < end)]
            inflow = float(seg[seg["amount"] > 0]["amount"].sum())
            outflow = float(-seg[seg["amount"] < 0]["amount"].sum())
        else:
            inflow = outflow = 0.0
        net = inflow - outflow
        bal += net
        total_in += inflow
        total_out += outflow
        if bal < min_bal:
            min_bal, min_week = bal, start.date().isoformat()
        periods.append({
            "weekStart": start.date().isoformat(),
            "inflow": round(inflow, 2),
            "outflow": round(outflow, 2),
            "net": round(net, 2),
            "projectedBalance": round(bal, 2),
        })

    # ---- what this projection is, and is not, entitled to claim ------------------------------
    offered = len(inflows or []) + len(outflows or [])
    dropped = (excluded_no_date["count"] + excluded_beyond["count"] + excluded_unusable["count"])
    placed_count = offered - dropped
    complete = offered > 0 and dropped == 0

    reasons: list[dict[str, str]] = []
    if offered == 0:
        reasons.append({
            "ar": "لم تُقدَّم أي مستحقات أو التزامات، فهذا ليس توقعًا بل رصيدٌ افتتاحي ممتد. "
                  "«لا توجد مدفوعات» و«لم تصلني المدفوعات» يبدوان متطابقين هنا.",
            "en": "No receivables or payables were supplied, so this is not a forecast — it is the "
                  "opening balance drawn forward. 'nothing is due' and 'nothing was sent to me' "
                  "look identical here.",
        })
    if excluded_no_date["count"]:
        reasons.append({
            "ar": f"استُبعد {excluded_no_date['count']} بند بلا تاريخ استحقاق "
                  f"بقيمة {round(excluded_no_date['amount'], 2)}؛ مبلغ معروف لا يمكن جدولته.",
            "en": f"{excluded_no_date['count']} item(s) worth "
                  f"{round(excluded_no_date['amount'], 2)} have no due date and could not be "
                  f"scheduled. Known money, unplaceable.",
        })
    if excluded_beyond["count"]:
        reasons.append({
            "ar": f"{excluded_beyond['count']} بند بقيمة {round(excluded_beyond['amount'], 2)} "
                  f"يستحق بعد نهاية الأفق، فهو خارج الإجماليات أدناه.",
            "en": f"{excluded_beyond['count']} item(s) worth "
                  f"{round(excluded_beyond['amount'], 2)} fall due after the horizon and are "
                  f"outside the totals below.",
        })
    if excluded_unusable["count"]:
        reasons.append({
            "ar": f"{excluded_unusable['count']} بند بمبلغ غير صالح، لم يُعامَل كصفر.",
            "en": f"{excluded_unusable['count']} item(s) had an unusable amount and were NOT "
                  f"treated as zero.",
        })

    return {
        "asOf": as_of.date().isoformat(),
        "horizonDays": horizon_days,
        "openingCash": round(float(opening_cash or 0.0), 2),
        # Named for what they are: the totals OF WHAT WAS PLACED, not of everything offered.
        "totalExpectedInflow": round(total_in, 2),
        "totalExpectedOutflow": round(total_out, 2),
        "projectedEndBalance": round(bal, 2),
        "minProjectedBalance": round(min_bal, 2),
        "minBalanceWeek": min_week,

        # A JUDGEMENT ONLY WHERE THE INPUTS SUPPORT ONE. `negativeRisk` was a bare boolean, and a
        # bare boolean from an empty projection is a reassurance nobody earned. It is None when
        # the projection is not complete, so a caller must decide what to show rather than read
        # False as "safe".
        "negativeRisk": (min_bal < 0) if complete else None,
        "complete": complete,

        "coverage": {
            "flowsOffered": offered,
            "flowsPlaced": placed_count,
            "flowsWithoutDueDate": excluded_no_date["count"],
            "amountWithoutDueDate": round(excluded_no_date["amount"], 2),
            "flowsBeyondHorizon": excluded_beyond["count"],
            "amountBeyondHorizon": round(excluded_beyond["amount"], 2),
            "flowsUnusable": excluded_unusable["count"],
        },
        "reasons": reasons,
        "periods": periods,
    }


def describe() -> dict[str, Any]:
    """What this model needs in order to mean anything. Used by the readiness check."""
    return {
        "model": "cashflow-projection",
        "requires": ["opening cash as a posted figure", "AR/AP with due dates", "one currency"],
        "refusesWhen": ["no flows offered", "a flow has no due date", "an amount is unusable"],
    }
