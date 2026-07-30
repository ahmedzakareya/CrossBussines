"""Cash-flow projection (local, no LLM / no API key).

Projects the running cash balance over a horizon from outstanding AR/AP due
dates. Overdue items are expected in the first week. This is a deterministic
treasury projection (the useful core); a statistical model can be layered on
later once enough history accrues — the data path stays the same.
"""
from __future__ import annotations

import pandas as pd


def _parse(d) -> pd.Timestamp:
    # .NET may serialize some dates tz-aware (UTC "Z") and others naive; drop tz
    # so all comparisons are consistent at day granularity.
    ts = pd.to_datetime(d)
    if ts.tzinfo is not None:
        ts = ts.tz_localize(None)
    return ts.normalize()


def project_cashflow(opening_cash: float, as_of, horizon_days: int,
                     inflows: list[dict], outflows: list[dict]) -> dict:
    as_of = _parse(as_of) if as_of else pd.Timestamp.today().normalize()

    def prep(flows, sign):
        out = []
        for f in flows:
            d = _parse(f["date"])
            if d < as_of:        # overdue → expected immediately (first week)
                d = as_of
            out.append({"date": d, "amount": float(f["amount"]) * sign})
        return out

    rows = prep(inflows, 1) + prep(outflows, -1)
    df = pd.DataFrame(rows) if rows else pd.DataFrame(columns=["date", "amount"])

    n_weeks = max(1, (horizon_days + 6) // 7)
    bal = float(opening_cash)
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

    return {
        "asOf": as_of.date().isoformat(),
        "horizonDays": horizon_days,
        "openingCash": round(float(opening_cash), 2),
        "totalExpectedInflow": round(total_in, 2),
        "totalExpectedOutflow": round(total_out, 2),
        "projectedEndBalance": round(bal, 2),
        "minProjectedBalance": round(min_bal, 2),
        "minBalanceWeek": min_week,
        "negativeRisk": min_bal < 0,
        "periods": periods,
    }
