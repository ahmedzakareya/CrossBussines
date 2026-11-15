"""Tests for the cash-flow projection.

Run:  python -I tests/test_forecast.py      (from crossbuy_ai/)

Every expected figure below is worked out BY HAND in the comment above it, so a failure says which
step of the arithmetic moved rather than merely that a number changed. No fixtures, no database,
no network.

THIS MODULE HAD NO TESTS, WHICH IS WHY ITS THREE LEAKS SURVIVED. All three were silent: an undated
flow fell into no week, a flow past the horizon fell into no week, and an empty input produced a
confident flat line. None of them raised, none of them logged, and the totals still looked like
totals. A number that is wrong by omission cannot be caught by reading the code — only by handing
it the degenerate input and insisting it say so.
"""
from __future__ import annotations

import os
import sys

sys.stdout.reconfigure(encoding="utf-8")
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from app.ml.forecast import project_cashflow  # noqa: E402

FAILURES: list[str] = []


def check(name: str, cond: bool, detail: str = "") -> None:
    if cond:
        print(f"  PASS  {name}")
    else:
        print(f"  FAIL  {name}  {detail}")
        FAILURES.append(name)


AS_OF = "2026-11-08"
# horizon 14 -> n_weeks = (14 + 6) // 7 = 2; weeks are 11-08..11-15 and 11-15..11-22,
# so the horizon ends 2026-11-22 and anything due on or after that date is outside it.
HORIZON = 14


# ===================================================================================
print("\n[1] THE ARITHMETIC, ON FLOWS THAT ALL FIT")
# opening 1000
# week 0 (11-08..11-15): in 500            -> net +500 -> balance 1500
# week 1 (11-15..11-22): out 200           -> net -200 -> balance 1300
# totals: in 500, out 200, end 1300
# the running minimum never drops below the opening 1000, so minProjectedBalance is 1000
# ===================================================================================
r = project_cashflow(1000, AS_OF, HORIZON,
                     [{"date": "2026-11-10", "amount": 500}],
                     [{"date": "2026-11-17", "amount": 200}])
check("two weekly periods", len(r["periods"]) == 2, str(len(r["periods"])))
check("week 0 balance is 1500", r["periods"][0]["projectedBalance"] == 1500.0,
      str(r["periods"][0]["projectedBalance"]))
check("week 1 balance is 1300", r["periods"][1]["projectedBalance"] == 1300.0,
      str(r["periods"][1]["projectedBalance"]))
check("total inflow is 500", r["totalExpectedInflow"] == 500.0, str(r["totalExpectedInflow"]))
check("total outflow is 200", r["totalExpectedOutflow"] == 200.0, str(r["totalExpectedOutflow"]))
check("end balance is 1300", r["projectedEndBalance"] == 1300.0, str(r["projectedEndBalance"]))
check("minimum is the opening 1000", r["minProjectedBalance"] == 1000.0,
      str(r["minProjectedBalance"]))
check("nothing was dropped, so the projection is complete", r["complete"] is True)
check("and only then is negativeRisk a claim", r["negativeRisk"] is False)
check("no reasons when nothing was excluded", r["reasons"] == [], str(r["reasons"]))


# ===================================================================================
print("\n[2] OVERDUE IS NOT MISSING — it is due now")
# A receivable dated 2026-10-01, a month before as_of, belongs in week 0 rather than in no week.
# week 0: in 300 -> balance 1300
# ===================================================================================
r = project_cashflow(1000, AS_OF, HORIZON, [{"date": "2026-10-01", "amount": 300}], [])
check("an overdue inflow lands in the first week", r["periods"][0]["inflow"] == 300.0,
      str(r["periods"][0]["inflow"]))
check("and is not reported as excluded", r["coverage"]["flowsPlaced"] == 1)


# ===================================================================================
print("\n[3] THE THREE SILENT LEAKS")
# Each of these used to vanish: the amount left the projection, the totals still presented
# themselves as totals, and nothing anywhere said a figure was missing.
# ===================================================================================
# -- undated: NaT compares False against every week boundary, so it fell into no week --
r = project_cashflow(1000, AS_OF, HORIZON,
                     [{"date": None, "amount": 999}, {"date": "2026-11-10", "amount": 100}], [])
check("an undated flow is counted as excluded", r["coverage"]["flowsWithoutDueDate"] == 1,
      str(r["coverage"]["flowsWithoutDueDate"]))
check("and its AMOUNT is reported, not just its existence",
      r["coverage"]["amountWithoutDueDate"] == 999.0, str(r["coverage"]["amountWithoutDueDate"]))
check("it does not reach the totals", r["totalExpectedInflow"] == 100.0,
      str(r["totalExpectedInflow"]))
check("the projection is no longer complete", r["complete"] is False)
check("so negativeRisk refuses to be a boolean", r["negativeRisk"] is None, str(r["negativeRisk"]))
check("and a reason names it", any("due date" in x["en"] for x in r["reasons"]), str(r["reasons"]))

# -- beyond the horizon: 2026-12-01 is past the 2026-11-22 end --
r = project_cashflow(1000, AS_OF, HORIZON, [{"date": "2026-12-01", "amount": 750}], [])
check("a flow past the horizon is counted", r["coverage"]["flowsBeyondHorizon"] == 1)
check("with its amount", r["coverage"]["amountBeyondHorizon"] == 750.0,
      str(r["coverage"]["amountBeyondHorizon"]))
check("it is outside the totals", r["totalExpectedInflow"] == 0.0, str(r["totalExpectedInflow"]))
check("and the result says it is not complete", r["complete"] is False)

# -- an unusable amount must NOT become zero --
r = project_cashflow(1000, AS_OF, HORIZON,
                     [{"date": "2026-11-10", "amount": "not a number"}], [])
check("an unusable amount is counted, not coerced", r["coverage"]["flowsUnusable"] == 1)
check("it is not silently a zero inflow", r["totalExpectedInflow"] == 0.0 and not r["complete"])


# ===================================================================================
print("\n[4] NOTHING IN IS NOT A FORECAST")
# The original returned openingCash, negativeRisk False and two tidy zero weeks — a flat line
# that reads as reassurance. 'nothing is due' and 'nothing was sent to me' produced identical
# output, and only one of those is good news.
# ===================================================================================
r = project_cashflow(1000, AS_OF, HORIZON, [], [])
check("the opening balance is still reported", r["openingCash"] == 1000.0)
check("an empty projection is not complete", r["complete"] is False)
check("negativeRisk is withheld rather than False", r["negativeRisk"] is None, str(r["negativeRisk"]))
check("and the reason says the inputs were empty",
      any("No receivables or payables" in x["en"] for x in r["reasons"]), str(r["reasons"]))
check("flowsOffered is zero and says so", r["coverage"]["flowsOffered"] == 0)


# ===================================================================================
print("\n[5] DEGRADATION — odd input must not throw")
# ===================================================================================
check("None lists are survivable", project_cashflow(0, AS_OF, 7, None, None)["complete"] is False)
check("a zero horizon still yields one week",
      len(project_cashflow(0, AS_OF, 0, [], [])["periods"]) == 1)
check("a negative horizon does not produce negative weeks",
      len(project_cashflow(0, AS_OF, -5, [], [])["periods"]) == 1)
# FOUND BY THIS TEST: pandas RAISES on a string it cannot parse, so one mistyped due date
# returned a 500 from a live endpoint instead of a projection with a row it could not place.
check("an unparseable as_of falls back to today rather than throwing",
      project_cashflow(0, "not a date", 7, [], [])["asOf"] is not None)
check("an unparseable FLOW date is treated as undated, not fatal",
      project_cashflow(0, AS_OF, 7, [{"date": "32/13/2026", "amount": 50}], [])
      ["coverage"]["flowsWithoutDueDate"] == 1)
check("and its amount is still reported",
      project_cashflow(0, AS_OF, 7, [{"date": "32/13/2026", "amount": 50}], [])
      ["coverage"]["amountWithoutDueDate"] == 50.0)
check("opening cash of None reads as zero, and is stated",
      project_cashflow(None, AS_OF, 7, [], [])["openingCash"] == 0.0)
check("a flow with no amount key is counted as unusable",
      project_cashflow(0, AS_OF, 7, [{"date": "2026-11-10"}], [])["coverage"]["flowsUnusable"] == 1)


print("\n" + "=" * 62)
if FAILURES:
    print(f"FAILED: {len(FAILURES)}")
    for f in FAILURES:
        print(f"   - {f}")
    sys.exit(1)
print("ALL CHECKS PASSED")
