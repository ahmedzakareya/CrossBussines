"""Tests for inventory risk analysis.

Run:  .venv/Scripts/python.exe -I tests/test_inventory.py      (needs pandas)

Cases [1] and [2] are the two regressions this rewrite exists for, both reproduced from the real
catalogue: a daily rate invented from a total, and sixty items called dead capital when it was the
WINDOW that was empty.
"""
from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from app.ml.inventory import analyze_inventory  # noqa: E402

FAILURES: list[str] = []


def check(name: str, cond: bool, detail: str = "") -> None:
    if cond:
        print(f"  PASS  {name}")
    else:
        print(f"  FAIL  {name}  {detail}")
        FAILURES.append(name)


def item(i, *, on_hand=100.0, value=1000.0, out90=0.0, out30=0.0, rp=None,
         active=None, span=0, maxday=0.0, name="صنف"):
    return {"itemId": i, "code": f"IT-{i}", "name": name, "onHand": on_hand, "value": value,
            "out90": out90, "out30": out30, "reorderPoint": rp,
            "activeDays": active, "spanDays": span, "maxDayQty": maxday}


def of_class(r, c):
    return [f for f in r["flagged"] if f["class"] == c]


# ---------------------------------------------------------------------------
print("\n[1] A QUIET WINDOW IS NOT SIXTY DEAD ITEMS")
# ---------------------------------------------------------------------------
# The real shape on 2026-11-08: a 90-day window holding 4 active days and 35 movements, with 62 of
# 80 stocked items untouched. The old rule called every one of them dead capital.
dormant = [item(i, on_hand=50.0, value=3600.0, out90=0.0) for i in range(60)]
quiet = analyze_inventory(None, 90, dormant, window_active_days=4, window_movements=35)
check("no item is called dormant in a near-empty window", len(of_class(quiet, "slow")) == 0,
      str(len(of_class(quiet, "slow"))))
check("dead-stock value is not asserted either", quiet["summary"]["deadStockValue"] == 0.0,
      str(quiet["summary"]["deadStockValue"]))
check("the window is flagged instead", quiet["windowTooQuiet"] is True)
check("and the numbers are named", "4" in quiet["windowNote"]["en"] and "35" in quiet["windowNote"]["en"])

# The same items in a window that WAS busy are a real finding.
busy = analyze_inventory(None, 90, dormant, window_active_days=60, window_movements=900)
check("a busy window still reports dormant stock", len(of_class(busy, "slow")) == 60,
      str(len(of_class(busy, "slow"))))
check("and totals their value", busy["summary"]["deadStockValue"] == 216000.0,
      str(busy["summary"]["deadStockValue"]))
check("busy window is not flagged quiet", busy["windowTooQuiet"] is False)

# The threshold scales with the window rather than being a magic constant.
check("9 active days in 90 is still too quiet",
      analyze_inventory(None, 90, dormant, window_active_days=8)["windowTooQuiet"] is True)
check("10 active days in 90 is enough",
      analyze_inventory(None, 90, dormant, window_active_days=9)["windowTooQuiet"] is False)
check("4 active days in 30 is enough (same share, shorter window)",
      analyze_inventory(None, 30, dormant, window_active_days=5)["windowTooQuiet"] is False)

# ---------------------------------------------------------------------------
print("\n[2] NO DAILY RATE IS INVENTED FROM A TOTAL")
# ---------------------------------------------------------------------------
# 2,470 units through ONE day. out90/90 calls that 27.4/day for ever.
burst = [item(1, on_hand=5.0, out90=2470.0, out30=0.0, active=1, span=0, maxday=2470.0, rp=50.0)]
rb = analyze_inventory(None, 90, burst, window_active_days=60)
f = rb["flagged"][0]
check("the item is still flagged (it is below its reorder point)", f["class"] == "reorder")
check("but NO daily rate is stated", f["avgDailyUsage"] is None, str(f["avgDailyUsage"]))
check("and NO quantity is proposed", f["suggestedReorderQty"] is None, str(f["suggestedReorderQty"]))
check("the omission is explained",
      any("cannot support a daily rate" in x["en"] for x in f["reasons"]))
check("it is counted", rb["rateNotEstimable"] == 1, str(rb["rateNotEstimable"]))

# Demand genuinely spread across the window does get a rate.
spread = [item(2, on_hand=5.0, out90=900.0, out30=300.0, active=45, span=89, maxday=30.0, rp=50.0)]
rs = analyze_inventory(None, 90, spread, window_active_days=60)
g = rs["flagged"][0]
check("spread demand DOES get a rate", g["avgDailyUsage"] == 10.0, str(g["avgDailyUsage"]))
check("and a quantity", g["suggestedReorderQty"] is not None)
check("and carries its grade", g["historyGrade"] == "A", str(g["historyGrade"]))

# A burst hidden inside otherwise good coverage is caught by the same grading.
hidden = [item(3, on_hand=5.0, out90=1000.0, out30=0.0, active=12, span=70, maxday=800.0, rp=50.0)]
rh = analyze_inventory(None, 90, hidden, window_active_days=60)
check("a hidden burst downgrades below A", rh["flagged"][0]["historyGrade"] != "A",
      str(rh["flagged"][0]["historyGrade"]))

# ---------------------------------------------------------------------------
print("\n[3] the two classes that need NO estimated rate still work")
# ---------------------------------------------------------------------------
# Zero stock with recent demand is two recorded facts, not an estimate.
so = analyze_inventory(None, 90, [item(1, on_hand=0.0, out90=50.0, out30=20.0, active=1, maxday=50.0)],
                       window_active_days=60)
check("stockout risk is reported without any rate", len(of_class(so, "stockout")) == 1)
check("its severity is high", of_class(so, "stockout")[0]["severity"] == "high")
# Below a RECORDED reorder point is a comparison of two stored numbers.
bp = analyze_inventory(None, 90, [item(1, on_hand=10.0, out90=5.0, out30=5.0, active=1, maxday=5.0, rp=50.0)],
                       window_active_days=60)
check("below a recorded reorder point is reported without a rate", len(of_class(bp, "reorder")) == 1)
check("but with no invented quantity", of_class(bp, "reorder")[0]["suggestedReorderQty"] is None)

# ---------------------------------------------------------------------------
print()
print("[3b] A REORDER POINT OF ZERO IS NOT A REORDER POINT")
# ---------------------------------------------------------------------------
# Shipped bug: Sum() over an all-NULL column answers 0, not null, so "never set" reached this
# engine as "zero" - and on_hand <= 0 then made every out-of-stock item read as below its
# reorder point. The live screen showed 42 such items directly above a panel saying nothing
# could be concluded from the period. The caller is fixed; this keeps a future one honest.
zero_rp = [item(i, on_hand=0.0, out90=0.0, out30=0.0, rp=0.0) for i in range(42)]
rz = analyze_inventory(None, 90, zero_rp, window_active_days=60)
check("a reorder point of 0 flags nothing", len(of_class(rz, "reorder")) == 0,
      str(len(of_class(rz, "reorder"))))
# out90 > 0 so it does not fall into the slow/dead branch first; the point is the rp test.
real_rp = [item(1, on_hand=3.0, out90=10.0, out30=5.0, rp=50.0, active=10, span=60, maxday=2.0)]
check("a real reorder point still fires",
      len(of_class(analyze_inventory(None, 90, real_rp, window_active_days=60), "reorder")) == 1)
check("null is still treated as unset",
      len(of_class(analyze_inventory(None, 90, [item(1, on_hand=0.0, rp=None)],
                                     window_active_days=60), "reorder")) == 0)

# ---------------------------------------------------------------------------
print("\n[4] an older caller that sends no distribution claims nothing")
# ---------------------------------------------------------------------------
legacy = [{"itemId": 1, "code": "X", "name": "x", "onHand": 10.0, "value": 100.0,
           "out90": 2470.0, "out30": 0.0, "reorderPoint": 50.0}]
rl = analyze_inventory(None, 90, legacy, window_active_days=60)
check("no crash without the distribution fields", rl["itemsAnalyzed"] == 1)
check("no rate is claimed", rl["flagged"][0]["avgDailyUsage"] is None)
check("no quantity is proposed", rl["flagged"][0]["suggestedReorderQty"] is None)
check("the reason names the missing input",
      any("distribution was not supplied" in x["en"] for x in rl["flagged"][0]["reasons"]))

# ---------------------------------------------------------------------------
print("\n[5] shape and degradation")
# ---------------------------------------------------------------------------
e = analyze_inventory(None, 90, [], window_active_days=60)
check("empty input is handled", e["itemsAnalyzed"] == 0 and e["flaggedCount"] == 0)
check("window facts are echoed back", e["windowActiveDays"] == 60)
unknown = analyze_inventory(None, 90, dormant)   # no window facts at all
check("without window facts it does NOT claim the window is quiet", unknown["windowTooQuiet"] is False)
check("so dormancy is still reported, as before", len(of_class(unknown, "slow")) == 60,
      str(len(of_class(unknown, "slow"))))

print("\n" + "=" * 62)
if FAILURES:
    print(f"FAILED: {len(FAILURES)}")
    for f_ in FAILURES:
        print(f"   - {f_}")
    sys.exit(1)
print("ALL CHECKS PASSED")
