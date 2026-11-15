"""Tests for the reorder point engine.

Run:  python -I tests/test_reorder.py      (from crossbuy_ai/)

Every expected value below is computed BY HAND in the comment above it, so a
failure says which step of the arithmetic moved -- not merely that a number
changed. No fixtures, no database, no network.
"""
from __future__ import annotations

import math
import os
import sys
from datetime import date, timedelta

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from app.ml.reorder import compute_reorder_points, grade_history, z_for_service_level  # noqa: E402

AS_OF = date(2026, 11, 8)
FAILURES: list[str] = []


def check(name: str, cond: bool, detail: str = "") -> None:
    if cond:
        print(f"  PASS  {name}")
    else:
        print(f"  FAIL  {name}  {detail}")
        FAILURES.append(name)


def close(a, b, tol=1e-6) -> bool:
    return a is not None and b is not None and abs(a - b) <= tol


def row(**kw) -> dict:
    base = dict(itemId=1, warehouseId=1, code="T", name="صنف اختبار", nameEn="Test item",
                onHand=0.0, activeDays=0, spanDays=0, totalQty=0.0, sumSqQty=0.0,
                maxDayQty=0.0, firstOut=None, lastOut=None,
                leadTimeDays=None, leadTimeSdDays=0.0,
                currentReorderPoint=None, currentSafetyStock=None)
    base.update(kw)
    return base


def run(rows, service_level=0.95, window=90, default_lt=7.0):
    return compute_reorder_points(AS_OF, window, service_level, default_lt, rows)


# ---------------------------------------------------------------------------
print("\n[1] inverse normal CDF — measured error, not the author's claim")
# ---------------------------------------------------------------------------
KNOWN = {0.50: 0.0, 0.80: 0.8416212336, 0.90: 1.2815515655, 0.95: 1.6448536270,
         0.975: 1.9599639845, 0.99: 2.3263478740, 0.999: 3.0902323062}
worst = max(abs(z_for_service_level(p) - e) for p, e in KNOWN.items())
check("z error below 2e-9 over 0.50..0.999", worst < 2e-9, f"worst={worst:.3e}")
check("z is monotonic in the service level",
      all(z_for_service_level(a) < z_for_service_level(b)
          for a, b in zip(sorted(KNOWN), sorted(KNOWN)[1:])))

# ---------------------------------------------------------------------------
print("\n[2] perfectly steady demand — safety stock must vanish")
# ---------------------------------------------------------------------------
# 10 units every day for 90 days. total=900, activeDays=90, sumSq=90*10^2=9000.
#   mean = 900/90                = 10
#   E[X^2] = 9000/90             = 100
#   var = 100 - 10^2             = 0        -> no demand variability
#   safety = z*sqrt(7*0 + 0)     = 0        -> nothing to protect against
#   ROP = 10*7 + 0               = 70
r = run([row(activeDays=90, spanDays=89, totalQty=900.0, sumSqQty=9000.0, maxDayQty=10.0,
             firstOut=AS_OF - timedelta(days=89), lastOut=AS_OF, onHand=0.0)])["results"][0]
check("grade A on 90 active days", r["grade"] == "A", r["grade"])
check("mean daily demand = 10", close(r["avgDailyDemand"], 10.0, 1e-4))
check("sd = 0 for constant demand", close(r["sdDailyDemand"], 0.0, 1e-4))
check("safety stock = 0", close(r["safetyStock"], 0.0, 1e-4), str(r["safetyStock"]))
check("reorder point = 70", close(r["reorderPoint"], 70.0, 1e-3), str(r["reorderPoint"]))

# ---------------------------------------------------------------------------
print("\n[3] same TOTAL, lumpy shape — the premium must appear")
# ---------------------------------------------------------------------------
# 20 units on every other day: same 900 total, but half the days are zero.
#   mean = 900/90            = 10          (identical to [2])
#   E[X^2] = (45*400)/90     = 200
#   var = 200 - 100          = 100  -> sd = 10
#   safety = 1.6448536*sqrt(7*100) = 1.6448536*26.4575131 = 43.52086
#   ROP = 70 + 43.52086      = 113.52086
sd_expected = 10.0
safety_expected = z_for_service_level(0.95) * math.sqrt(7 * 100.0)
r3 = run([row(activeDays=45, spanDays=89, totalQty=900.0, sumSqQty=18000.0, maxDayQty=20.0,
              firstOut=AS_OF - timedelta(days=89), lastOut=AS_OF, onHand=0.0)])["results"][0]
check("identical mean to the steady case", close(r3["avgDailyDemand"], 10.0, 1e-4))
check("sd = 10", close(r3["sdDailyDemand"], sd_expected, 1e-4), str(r3["sdDailyDemand"]))
check(f"safety stock = {safety_expected:.5f}", close(r3["safetyStock"], safety_expected, 1e-3),
      str(r3["safetyStock"]))
check("lumpy demand costs more stock than steady demand",
      r3["reorderPoint"] > r["reorderPoint"],
      f'{r3["reorderPoint"]} vs {r["reorderPoint"]}')

# ---------------------------------------------------------------------------
print("\n[4] an unreliable supplier must raise the point on its own")
# ---------------------------------------------------------------------------
# Same lumpy demand, but the lead time itself varies with sd = 2 days.
#   leadVarTerm = mean^2 * sd_L^2 = 100 * 4 = 400
#   safety = 1.6448536*sqrt(700 + 400) = 1.6448536*33.1662479 = 54.55566
safety4 = z_for_service_level(0.95) * math.sqrt(700.0 + 400.0)
r4 = run([row(activeDays=45, spanDays=89, totalQty=900.0, sumSqQty=18000.0, maxDayQty=20.0,
              firstOut=AS_OF - timedelta(days=89), lastOut=AS_OF, onHand=0.0,
              leadTimeSdDays=2.0)])["results"][0]
check(f"safety stock = {safety4:.5f}", close(r4["safetyStock"], safety4, 1e-3), str(r4["safetyStock"]))
check("late supplier raises the reorder point", r4["reorderPoint"] > r3["reorderPoint"])
lead_term = next(t for t in r4["terms"] if t["key"] == "leadTimeVariability")
check("lead-time variability is itemised = 400", close(lead_term["value"], 400.0, 1e-3))

# ---------------------------------------------------------------------------
print("\n[5] service level must move the number, and only through z")
# ---------------------------------------------------------------------------
lo = run([row(activeDays=45, spanDays=89, totalQty=900.0, sumSqQty=18000.0, maxDayQty=20.0,
              firstOut=AS_OF - timedelta(days=89), lastOut=AS_OF)], service_level=0.90)["results"][0]
hi = run([row(activeDays=45, spanDays=89, totalQty=900.0, sumSqQty=18000.0, maxDayQty=20.0,
              firstOut=AS_OF - timedelta(days=89), lastOut=AS_OF)], service_level=0.99)["results"][0]
check("99% demands more stock than 90%", hi["safetyStock"] > lo["safetyStock"])
check("ratio of safety stocks equals the ratio of z",
      close(hi["safetyStock"] / lo["safetyStock"],
            z_for_service_level(0.99) / z_for_service_level(0.90), 1e-4))

# ---------------------------------------------------------------------------
print("\n[6] REFUSAL — the behaviour this engine exists for")
# ---------------------------------------------------------------------------
# The real catalogue's worst case: 2,470 units issued on ONE day.
# out90/90 would read this as 27.4 units/day for ever. It must refuse instead.
burst = run([row(activeDays=1, spanDays=0, totalQty=2470.0, sumSqQty=2470.0 ** 2,
                 maxDayQty=2470.0, firstOut=AS_OF - timedelta(days=30),
                 lastOut=AS_OF - timedelta(days=30), onHand=5.0)])["results"][0]
check("single-day burst is graded D", burst["grade"] == "D", burst["grade"])
check("burst refuses to advise", burst["advise"] is False)
check("burst emits NO reorder point", burst["reorderPoint"] is None, str(burst["reorderPoint"]))
check("burst says what is missing", bool(burst.get("needed")))

# Twelve active days over 70 — grade A on counts alone — but one day holds 60%.
# The burst rule must pull it down rather than average the spike away.
hidden = run([row(activeDays=12, spanDays=70, totalQty=1000.0, sumSqQty=600.0 ** 2 + 11 * (400.0 / 11) ** 2,
                  maxDayQty=600.0, firstOut=AS_OF - timedelta(days=70), lastOut=AS_OF)])["results"][0]
check("hidden burst downgrades A -> B", hidden["grade"] == "B", hidden["grade"])
check("downgrade is explained", any("%" in n["ar"] for n in hidden["reasons"]))

thin = run([row(activeDays=2, spanDays=1, totalQty=40.0, sumSqQty=800.0, maxDayQty=20.0,
                firstOut=AS_OF - timedelta(days=10), lastOut=AS_OF - timedelta(days=9))])["results"][0]
check("two active days is graded D", thin["grade"] == "D")
check("two active days emits no number", thin["reorderPoint"] is None)

# ---------------------------------------------------------------------------
print("\n[7] no demand at all is an answer, not a refusal")
# ---------------------------------------------------------------------------
dead = run([row(onHand=120.0)])["results"][0]
check("dormant item is reported as noDemand", dead["status"] == "noDemand", dead["status"])
check("dormant item emits no reorder point", dead["reorderPoint"] is None)

# ---------------------------------------------------------------------------
print("\n[8] a late start must not be diluted by the quiet days before it")
# ---------------------------------------------------------------------------
# 10/day for the last 30 days of a 90-day window. Measuring over 90 would give
# 300/90 = 3.33/day and under-stock the item by a factor of three.
late = run([row(activeDays=30, spanDays=29, totalQty=300.0, sumSqQty=3000.0, maxDayQty=10.0,
                firstOut=AS_OF - timedelta(days=29), lastOut=AS_OF)])["results"][0]
check("effective window is 30 days, not 90", late["effectiveWindowDays"] == 30,
      str(late["effectiveWindowDays"]))
check("rate is 10/day, not 3.33/day", close(late["avgDailyDemand"], 10.0, 1e-4),
      str(late["avgDailyDemand"]))

# ---------------------------------------------------------------------------
print("\n[9] grading boundaries hold exactly where they are documented")
# ---------------------------------------------------------------------------
check("12 days / 60 span -> A", grade_history(12, 60, 0.1)[0] == "A")
check("11 days / 60 span -> B", grade_history(11, 60, 0.1)[0] == "B")
check("12 days / 59 span -> B", grade_history(12, 59, 0.1)[0] == "B")
check("6 days / 30 span  -> B", grade_history(6, 30, 0.1)[0] == "B")
check("5 days / 30 span  -> C", grade_history(5, 30, 0.1)[0] == "C")
check("3 days            -> C", grade_history(3, 5, 0.1)[0] == "C")
check("2 days            -> D", grade_history(2, 5, 0.1)[0] == "D")
check("confidence falls as the grade falls",
      grade_history(12, 60, 0.1)[1] > grade_history(6, 30, 0.1)[1] > grade_history(3, 5, 0.1)[1])

# ---------------------------------------------------------------------------
print("\n[10] the engine never writes, and ranks what needs acting on first")
# ---------------------------------------------------------------------------
mixed = run([
    row(itemId=1, onHand=500.0, activeDays=90, spanDays=89, totalQty=900.0,
        sumSqQty=9000.0, maxDayQty=10.0, firstOut=AS_OF - timedelta(days=89), lastOut=AS_OF),
    row(itemId=2, onHand=5.0, activeDays=90, spanDays=89, totalQty=900.0,
        sumSqQty=9000.0, maxDayQty=10.0, firstOut=AS_OF - timedelta(days=89), lastOut=AS_OF),
    row(itemId=3, onHand=0.0, activeDays=1, spanDays=0, totalQty=50.0,
        sumSqQty=2500.0, maxDayQty=50.0, firstOut=AS_OF - timedelta(days=3), lastOut=AS_OF),
])
ids = [x["itemId"] for x in mixed["results"]]
check("the item needing an order sorts first", ids[0] == 2, str(ids))
check("the refusal sorts last", ids[-1] == 3, str(ids))
check("summary counts the refusal", mixed["summary"]["refusedInsufficientHistory"] == 1)
check("summary counts the two advised", mixed["summary"]["advised"] == 2)
check("every result states its grade", all("grade" in x for x in mixed["results"]))
check("output carries the no-write note", "note" in mixed and "ar" in mixed["note"])

# ---------------------------------------------------------------------------
print("\n[11] demand SHAPE is judged separately from history SUFFICIENCY")
# ---------------------------------------------------------------------------
# Grade answers "is there enough history". This answers "is this the kind of
# demand the formula was built for". A row can have excellent history and
# still be the wrong shape -- found on the real catalogue, where MFGT-R1 has
# eight active days and a standard deviation six times its mean.
def shaped(sd, active=30, span=89, mean=10.0, days=90):
    """Build a row whose mean is `mean` and standard deviation is `sd`."""
    total = mean * days
    sum_sq = (sd * sd + mean * mean) * days      # E[X^2] = var + mean^2
    return row(activeDays=active, spanDays=span, totalQty=total, sumSqQty=sum_sq,
               maxDayQty=total * 0.2, firstOut=AS_OF - timedelta(days=days - 1),
               lastOut=AS_OF, onHand=0.0)


stable = run([shaped(sd=1.0)])["results"][0]
check("sd below the mean -> stable", stable["demandShape"] == "stable", stable["demandShape"])
check("stable cv = 0.1", close(stable["coefficientOfVariation"], 0.1, 1e-3))

lumpy = run([shaped(sd=20.0)])["results"][0]
check("sd = 2x mean -> lumpy", lumpy["demandShape"] == "lumpy", lumpy["demandShape"])
check("lumpy caps confidence at 0.50", lumpy["confidence"] <= 0.50, str(lumpy["confidence"]))
check("lumpy is explained in Arabic and English",
      any("متذبذب" in r["ar"] and "Lumpy" in r["en"] for r in lumpy["reasons"]))

inter = run([shaped(sd=40.0)])["results"][0]
check("sd = 4x mean -> intermittent", inter["demandShape"] == "intermittent", inter["demandShape"])
check("intermittent caps confidence at 0.30", inter["confidence"] <= 0.30, str(inter["confidence"]))
check("intermittent calls the figure an upper bound",
      any("upper bound" in r["en"] for r in inter["reasons"]))
check("shape does NOT silently change the grade — the axes stay separate",
      inter["grade"] == "A", inter["grade"])
check("a dominant safety stock is called out",
      any("safety stock rather than expected" in r["en"] for r in inter["reasons"]))
check("safetyShare is reported", inter["safetyShare"] > 0.6, str(inter["safetyShare"]))
check("the engine still emits the number it warned about", inter["reorderPoint"] is not None)

# ---------------------------------------------------------------------------
print("\n" + "=" * 62)
if FAILURES:
    print(f"FAILED: {len(FAILURES)}")
    for f in FAILURES:
        print(f"   - {f}")
    sys.exit(1)
print("ALL CHECKS PASSED")
