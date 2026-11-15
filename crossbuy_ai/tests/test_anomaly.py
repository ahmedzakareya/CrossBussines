"""Tests for journal anomaly detection.

Run:  python -I tests/test_anomaly.py      (needs the venv — pandas + scikit-learn)

Case [1] is the regression this rewrite exists for, reproduced from the real ledger: thirty
separate stock movements of one item, same value, same day, which the previous rule reported as a
thirty-way double-posting. No fixtures, no database, no network.
"""
from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from app.ml.anomaly import detect_journal_anomalies  # noqa: E402

FAILURES: list[str] = []


def check(name: str, cond: bool, detail: str = "") -> None:
    if cond:
        print(f"  PASS  {name}")
    else:
        print(f"  FAIL  {name}  {detail}")
        FAILURES.append(name)


def e(i, *, amount=100.0, lines=2, src="Inventory", sid=None, desc="حركة مخزون: x - y",
      date="2026-08-04", no=None, sig="SIGDEFAULT000001"):
    """One entry. `sig` is the hash of its LINES — same posting, same sig; a reversal differs."""
    return {"id": i, "entryNo": no or f"JV-{i:06d}", "date": date, "journalType": "JV",
            "sourceType": src, "sourceId": sid, "lineSignature": sig, "description": desc,
            "amount": amount, "lineCount": lines}


def dups(r):
    return [a for a in r["anomalies"] if any("posted" in x["en"] or "Repeated manual" in x["en"]
                                             for x in a["reasons"])]


# ---------------------------------------------------------------------------
print("\n[1] THE REGRESSION — 30 different documents are not one duplicate")
# ---------------------------------------------------------------------------
# Exactly the real shape: identical amount, identical TEMPLATED description, identical day,
# thirty consecutive but DISTINCT SourceIds. The old rule flagged all thirty as 'high'.
thirty = [e(i, amount=80.0, src="Inventory", sid=13608 + i, desc="حركة مخزون: hm-demo-001 - مياه معدنية 600 مل")
          for i in range(30)]
r1 = detect_journal_anomalies(thirty, review_budget=0.0)
check("30 distinct documents produce ZERO duplicates", len(dups(r1)) == 0, str(len(dups(r1))))
check("and zero anomalies overall", r1["anomalyCount"] == 0, str(r1["anomalyCount"]))
check("summary agrees", r1["summary"]["duplicates"] == 0)

# ---------------------------------------------------------------------------
print("\n[2] a document posted TWICE identically is a duplicate")
# ---------------------------------------------------------------------------
pair = [e(1, amount=90.0, lines=2, src="WorkOrder", sid=10, sig="AAAA111122223333"),
        e(2, amount=90.0, lines=2, src="WorkOrder", sid=10, sig="AAAA111122223333")]
r2 = detect_journal_anomalies(pair + thirty, review_budget=0.0)
check("exactly the two postings are flagged", len(dups(r2)) == 2, str(len(dups(r2))))
check("severity is high", all(a["severity"] == "high" for a in dups(r2)))
check("the reason names the document", "WorkOrder" in dups(r2)[0]["reasons"][0]["en"])
check("the reason says how many times", "2 times" in dups(r2)[0]["reasons"][0]["en"])
check("and rules out a reversal in words", "reversal" in dups(r2)[0]["reasons"][0]["en"])
check("the other thirty stay clean", r2["anomalyCount"] == 2, str(r2["anomalyCount"]))

# ---------------------------------------------------------------------------
print("\n[3] one document's several legs are NOT a duplicate")
# ---------------------------------------------------------------------------
# A work order legitimately posts material, labour and overhead against the same SourceId.
# They differ in amount or line count, which is what keeps them apart.
legs = [e(1, amount=90.0, lines=2, src="WorkOrder", sid=77, sig="LEG1"),
        e(2, amount=40.0, lines=2, src="WorkOrder", sid=77, sig="LEG2"),
        e(3, amount=90.0, lines=4, src="WorkOrder", sid=77, sig="LEG3")]
r3 = detect_journal_anomalies(legs, review_budget=0.0)
check("different amounts / line counts on one document are clean", len(dups(r3)) == 0,
      str(len(dups(r3))))

# ---------------------------------------------------------------------------
print()
print("[3b] AN ENTRY AND ITS REVERSAL ARE NOT A DUPLICATE")
# ---------------------------------------------------------------------------
# Straight from the real ledger: work order 3052 posts the material issue and then its
# cancellation. Same document, same 95.00 total, same three lines - mirror images. Keyed on
# amount and line count those look identical, which is how 24 false duplicates survived the
# first fix. Only the line signature separates them.
rev = [e(1, amount=95.0, lines=3, src="WorkOrder", sid=3052, sig="ISSUE00000000001"),
       e(2, amount=95.0, lines=3, src="WorkOrder", sid=3052, sig="REVERSAL00000001")]
check("totals and line counts really are identical",
      rev[0]["amount"] == rev[1]["amount"] and rev[0]["lineCount"] == rev[1]["lineCount"])
r3b = detect_journal_anomalies(rev, review_budget=0.0)
check("an issue and its reversal are NOT duplicates", len(dups(r3b)) == 0, str(len(dups(r3b))))
check("neither is counted in the summary", r3b["summary"]["duplicates"] == 0)
# Re-post each of the two a second time and it IS worth flagging - all four rows.
r3c = detect_journal_anomalies(rev + [dict(rev[0], id=3), dict(rev[1], id=4)], review_budget=0.0)
check("but a genuine re-post of each one is", len(dups(r3c)) == 4, str(len(dups(r3c))))

# ---------------------------------------------------------------------------
print("\n[4] hand-typed entries still use amount + description + day")
# ---------------------------------------------------------------------------
# No document to key on, but the description here is AUTHOR-written, so it discriminates.
manual = [e(1, amount=500.0, src="Manual", sid=None, desc="تسوية عهدة محمد", date="2026-08-04", sig="M1"),
          e(2, amount=500.0, src="Manual", sid=None, desc="تسوية عهدة محمد", date="2026-08-04", sig="M1"),
          e(3, amount=500.0, src="Manual", sid=None, desc="تسوية عهدة سامي", date="2026-08-04", sig="M1"),
          e(4, amount=500.0, src="Manual", sid=None, desc="تسوية عهدة محمد", date="2026-09-04", sig="M1")]
r4 = detect_journal_anomalies(manual, review_budget=0.0)
check("the two identical manual entries are flagged", len(dups(r4)) == 2, str(len(dups(r4))))
check("a different description is not", all(a["id"] != 3 for a in dups(r4)))
check("a different day is not", all(a["id"] != 4 for a in dups(r4)))

# ---------------------------------------------------------------------------
print("\n[5] what cannot be judged is REPORTED, not guessed")
# ---------------------------------------------------------------------------
# System-generated, no document id, templated description: a duplicate and a coincidence are
# indistinguishable here, so the engine claims neither and says how many it skipped.
opening = [e(i, amount=20.0, src="OpeningStock", sid=None, desc="رصيد مخزون افتتاحي")
           for i in range(18)]
r5 = detect_journal_anomalies(opening, review_budget=0.0)
check("18 identical opening balances produce no duplicate claim", len(dups(r5)) == 0,
      str(len(dups(r5))))
check("they are counted as unchecked", r5["summary"]["notCheckedForDuplicates"] == 18,
      str(r5["summary"]["notCheckedForDuplicates"]))
check("an entry WITH a document is not counted unchecked",
      detect_journal_anomalies([e(1, sid=5)], review_budget=0.0)["summary"]["notCheckedForDuplicates"] == 0)

# ---------------------------------------------------------------------------
print("\n[6] amount outliers are measured within their own source type")
# ---------------------------------------------------------------------------
same = [e(i, amount=100.0, src="Payroll", sid=1000 + i) for i in range(20)]
r6a = detect_journal_anomalies(same, review_budget=0.0)
check("uniform amounts produce NO outlier", r6a["summary"]["amountOutliers"] == 0,
      str(r6a["summary"]["amountOutliers"]))
spike = same + [e(99, amount=100000.0, src="Payroll", sid=9999)]
r6b = detect_journal_anomalies(spike, review_budget=0.0)
check("one extreme amount IS an outlier", r6b["summary"]["amountOutliers"] >= 1,
      str(r6b["summary"]["amountOutliers"]))
check("the outlier is the spike", any(a["id"] == 99 for a in r6b["anomalies"]))
check("its severity is medium, not high",
      all(a["severity"] == "medium" for a in r6b["anomalies"] if a["id"] == 99))
# A big petty-cash voucher must not be judged against payroll.
mixed = [e(i, amount=100.0, src="Payroll", sid=1000 + i) for i in range(20)] + \
        [e(100 + i, amount=90000.0 + i, src="AssetPurchase", sid=2000 + i) for i in range(6)]
r6c = detect_journal_anomalies(mixed, review_budget=0.0)
check("a big amount of ANOTHER type is not an outlier", r6c["summary"]["amountOutliers"] == 0,
      str(r6c["summary"]["amountOutliers"]))

# ---------------------------------------------------------------------------
print("\n[7] the structural ranking is a QUEUE, never an anomaly")
# ---------------------------------------------------------------------------
ledger = [e(i, amount=100.0 + i, lines=2 + (i % 3), src="Inventory", sid=5000 + i) for i in range(200)]
r7 = detect_journal_anomalies(ledger, review_budget=0.01)
check("the queue honours its budget", r7["summary"]["reviewQueueSize"] == 2,
      str(r7["summary"]["reviewQueueSize"]))
check("the queue is NOT counted as anomalies", r7["anomalyCount"] == 0, str(r7["anomalyCount"]))
check("queue rows are severity low", all(q["severity"] == "low" for q in r7["reviewQueue"]))
check("queue rows say they are a ranking",
      all("not a finding" in q["reasons"][0]["en"] for q in r7["reviewQueue"]))
r7b = detect_journal_anomalies(ledger, review_budget=0.05)
check("a bigger budget surfaces more", r7b["summary"]["reviewQueueSize"] == 10,
      str(r7b["summary"]["reviewQueueSize"]))
r7c = detect_journal_anomalies(ledger, review_budget=0.0)
check("a zero budget surfaces none", r7c["summary"]["reviewQueueSize"] == 0)
check("the budget is capped so it cannot become the ledger",
      detect_journal_anomalies(ledger, review_budget=9.0)["summary"]["reviewQueueSize"] <= 50)

# ---------------------------------------------------------------------------
print("\n[8] A CLEAN LEDGER REPORTS NOTHING — what the old detector could never do")
# ---------------------------------------------------------------------------
# contamination=0.06 labelled 6% of ANY input, so "nothing is wrong" was unsayable.
clean = [e(i, amount=100.0 + (i % 7), lines=2, src="SalesInvoice", sid=7000 + i) for i in range(500)]
r8 = detect_journal_anomalies(clean, review_budget=0.0)
check("500 ordinary entries produce ZERO anomalies", r8["anomalyCount"] == 0, str(r8["anomalyCount"]))
check("zero duplicates", r8["summary"]["duplicates"] == 0)
check("zero outliers", r8["summary"]["amountOutliers"] == 0)
check("and nothing was skipped silently", r8["summary"]["notCheckedForDuplicates"] == 0)

# ---------------------------------------------------------------------------
print("\n[9] shape and degradation")
# ---------------------------------------------------------------------------
r9 = detect_journal_anomalies([], review_budget=0.01)
check("empty input is handled", r9["scanned"] == 0 and r9["anomalyCount"] == 0)
check("empty input still carries the summary keys",
      all(k in r9["summary"] for k in ("duplicates", "amountOutliers",
                                       "notCheckedForDuplicates", "reviewQueueSize")))
r10 = detect_journal_anomalies([e(1, sid=1), e(2, sid=2)], review_budget=0.5)
check("below the forest minimum there is no queue", r10["summary"]["reviewQueueSize"] == 0,
      str(r10["summary"]["reviewQueueSize"]))
# An older caller that sends no sourceId at all must not crash, and must not invent duplicates.
legacy = [{"id": i, "entryNo": f"JV-{i}", "date": "2026-08-04", "sourceType": "Inventory",
           "description": "حركة مخزون", "amount": 80.0, "lineCount": 2} for i in range(5)]
r11 = detect_journal_anomalies(legacy, review_budget=0.0)
check("a payload with no sourceId does not crash", r11["scanned"] == 5)
check("and claims no duplicates it cannot prove", r11["summary"]["duplicates"] == 0)
check("counting them as unchecked instead", r11["summary"]["notCheckedForDuplicates"] == 5,
      str(r11["summary"]["notCheckedForDuplicates"]))
# A document whose LINES were not sent cannot be compared either, and says so.
nosig = [e(1, src="WorkOrder", sid=9, sig=""), e(2, src="WorkOrder", sid=9, sig="")]
r12 = detect_journal_anomalies(nosig, review_budget=0.0)
check("a document with no line signature is unchecked, not duplicated",
      r12["summary"]["duplicates"] == 0 and r12["summary"]["notCheckedForDuplicates"] == 2,
      f'dups={r12["summary"]["duplicates"]} unchecked={r12["summary"]["notCheckedForDuplicates"]}')
check("output carries the method note", "note" in r11 and "ar" in r11["note"])

print("\n" + "=" * 62)
if FAILURES:
    print(f"FAILED: {len(FAILURES)}")
    for f in FAILURES:
        print(f"   - {f}")
    sys.exit(1)
print("ALL CHECKS PASSED")
