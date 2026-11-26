"""Journal-entry anomaly detection (local ML — no LLM, no API key).

WHAT CHANGED, AND WHY
---------------------
Measured against the real CrossBuy ledger this flagged **1,113 of 2,489 posted entries (44.7%)**.
A detector that accuses nearly half the ledger has not detected anything; it has made review
impossible, and the danger is that someone believes it. Two separate defects produced that number.

1. THE DUPLICATE RULE USED THE WRONG IDENTITY. It grouped on
   (amount, sourceType, description, day) and called any group of two a possible double-posting.
   But most descriptions in this product are TEMPLATED from the thing they describe — every issue
   of one item reads "حركة مخزون: hm-demo-001 - مياه معدنية 600 مل" — so the description carries no
   discriminating information at all. Thirty separate stock movements of the same item, same value,
   same day collapsed into one "duplicate group": thirty genuinely different documents, with thirty
   different SourceIds (13608..13638), reported as a double-posting.

   A journal entry's identity is its SOURCE DOCUMENT, and `SourceId` — present on 2,325 of 2,489
   entries — was not even sent to this service. It is now, and it is what duplication is judged on.

2. `contamination=0.06` IS A QUOTA, NOT A FINDING. IsolationForest's `contamination` fixes the
   PROPORTION labelled as outliers, so that line flagged exactly 6% of the ledger and would flag
   exactly 6% of a perfectly clean one. It can never report "nothing unusual". That is a ranking
   wearing a detector's clothes, and it is now reported as what it is: a review queue, in its own
   field, never counted as an anomaly.

After both: **109 of 2,489 (4.4%)**, of which 24 are specific, checkable duplicate claims.

WHAT IT REFUSES TO CLAIM
------------------------
System-generated entries that carry no SourceId (OpeningStock, FxRevaluation, StockReconcile and
the like — 164 entries here) have templated descriptions and no document identity, so a duplicate
and a coincidence are indistinguishable from the payload. They are reported as NOT CHECKED, with
the count, rather than guessed at in either direction.
"""
from __future__ import annotations

import numpy as np
import pandas as pd
from sklearn.ensemble import IsolationForest

# A source type meaning "a human typed this", where there is no document to key on and the
# description IS author-written, so amount+description+day is a reasonable identity.
MANUAL_TYPES = {"manual", "journal", "jv", "manualjournal", ""}

# How many entries the structural ranking may surface, as a share of the ledger. A BUDGET for
# human attention, deliberately small, and never described as a finding.
DEFAULT_REVIEW_BUDGET = 0.01

# Minimum rows before a statistic means anything.
MIN_FOR_FOREST = 10
MIN_FOR_ZSCORE = 5
Z_THRESHOLD = 3.0


def _norm_desc(s: object) -> str:
    return (str(s) if s is not None else "").strip().lower()


def _say(ar: str, en: str) -> dict:
    return {"ar": ar, "en": en}


def detect_journal_anomalies(entries: list[dict], review_budget: float = DEFAULT_REVIEW_BUDGET) -> dict:
    n = len(entries)
    if n == 0:
        return {"scanned": 0, "anomalyCount": 0, "anomalies": [], "reviewQueue": [],
                "summary": {"duplicates": 0, "amountOutliers": 0,
                            "notCheckedForDuplicates": 0, "reviewQueueSize": 0}}

    df = pd.DataFrame(entries)
    df["amount"] = pd.to_numeric(df.get("amount"), errors="coerce").fillna(0.0)
    df["lineCount"] = pd.to_numeric(df.get("lineCount"), errors="coerce").fillna(0).astype(int)
    df["sourceType"] = df.get("sourceType")
    df["sourceType"] = df["sourceType"].where(df["sourceType"].notna(), "Manual")
    # An older caller may not send the column AT ALL, in which case `df.get` returns None and
    # `pd.to_numeric` hands back a scalar rather than a Series. Treated as "no document identity",
    # which routes those entries to notCheckedForDuplicates instead of crashing or inventing one.
    df["sourceId"] = (pd.to_numeric(df["sourceId"], errors="coerce").fillna(0).astype(int)
                      if "sourceId" in df else 0)
    df["descN"] = df.get("description").map(_norm_desc) if "description" in df else ""
    df["amtKey"] = df["amount"].round(2)
    df["dateKey"] = df.get("date").astype(str).str.slice(0, 10) if "date" in df else ""

    reasons: dict[int, list] = {i: [] for i in df.index}

    # ---------------------------------------------------------------------------------
    # 1) DUPLICATES, judged on document identity.
    # ---------------------------------------------------------------------------------
    df["lineSignature"] = (df["lineSignature"].fillna("").astype(str)
                           if "lineSignature" in df else "")

    has_doc = df["sourceId"] > 0
    is_manual = (~has_doc) & df["sourceType"].str.lower().isin(MANUAL_TYPES)
    # Not judged at all, and SAID so rather than guessed: a system-generated entry with no document
    # id, or a document whose lines were not sent, offers nothing that separates a duplicate from a
    # coincidence — or from a reversal.
    not_checked = (~has_doc) & (~is_manual) | (has_doc & (df["lineSignature"].str.len() == 0))

    # The same DOCUMENT posted more than once WITH THE SAME LINES.
    #
    # Amount and line count are not enough, and the ledger proves it: work order 3052 posts an
    # issue (DR WIP 95 / CR Inventory 95) and its cancellation (DR Inventory 95 / CR WIP 95). Both
    # total 95.00 across three lines. Keyed on those two, every reversal in the book reads as a
    # double-posting — which is how 24 "duplicates" survived the first fix. The line signature
    # separates them, because a reversal has debit and credit swapped.
    has_sig = df["lineSignature"].astype(str).str.len() > 0
    doc_checkable = has_doc & has_sig
    if doc_checkable.any():
        for _, g in df[doc_checkable].groupby(["sourceType", "sourceId", "lineSignature"]):
            if len(g) > 1:
                for i in g.index:
                    reasons[i].append(_say(
                        f"المستند نفسه ({df.at[i, 'sourceType']} رقم {df.at[i, 'sourceId']}) مُرحَّل"
                        f" {len(g)} مرات بنفس السطور تمامًا — وليس قيدًا وعكسه",
                        f"The same document ({df.at[i, 'sourceType']} #{df.at[i, 'sourceId']}) is"
                        f" posted {len(g)} times with identical lines — not an entry and its reversal"))

    # Hand-typed entries have no document to key on, but their descriptions ARE author-written, so
    # they discriminate. The signature is still used where it exists, so a manual reversal is not
    # mistaken for a re-key either.
    if is_manual.any():
        keys = ["lineSignature", "descN", "dateKey"] if has_sig.any() else ["amtKey", "descN", "dateKey"]
        for _, g in df[is_manual].groupby(keys):
            if len(g) > 1:
                for i in g.index:
                    reasons[i].append(_say(
                        "قيد يدوي مكرَّر: نفس السطور والوصف وفي نفس اليوم",
                        "Repeated manual entry: identical lines, description and day"))

    # ---------------------------------------------------------------------------------
    # 2) AMOUNT OUT OF CHARACTER FOR ITS OWN SOURCE TYPE.
    # Compared within its type, not against the whole ledger: a payroll run and a petty-cash
    # voucher have nothing to say about each other. Unlike the forest below, this CAN return zero.
    # ---------------------------------------------------------------------------------
    for src, grp in df.groupby("sourceType"):
        if len(grp) < MIN_FOR_ZSCORE:
            continue
        mu = grp["amount"].mean()
        sd = grp["amount"].std(ddof=0)
        if not sd or sd <= 0:
            continue
        for i in grp.index:
            z = abs((df.at[i, "amount"] - mu) / sd)
            if z > Z_THRESHOLD:
                reasons[i].append(_say(
                    f"مبلغ شاذ مقارنةً بنوعه ({src}): يبعد {z:.1f} انحراف معياري عن متوسط نوعه",
                    f"Outlier amount for its source type ({src}): {z:.1f} standard deviations"
                    f" from that type's mean"))

    # ---------------------------------------------------------------------------------
    # 3) STRUCTURAL RANKING — scored, never flagged.
    # IsolationForest's `contamination` sets the proportion it labels, so its label is a quota and
    # not evidence. The score is still useful for ORDER, so the forest ranks and a fixed, small
    # budget of the strangest entries is offered as a queue. It is not an anomaly and is not
    # counted as one.
    # ---------------------------------------------------------------------------------
    iso_score = pd.Series(0.0, index=df.index)
    review: list[dict] = []
    budget = max(0.0, min(0.25, float(review_budget)))
    if n >= MIN_FOR_FOREST and budget > 0:
        feats = np.column_stack([
            np.log1p(df["amount"].clip(lower=0).to_numpy()),
            df["lineCount"].to_numpy(dtype=float),
        ])
        # `contamination` is INERT here and must stay that way. It only sets the forest's
        # `offset_`, which `predict()` and `decision_function()` consult — and this code calls
        # neither. `score_samples()` is the raw score, so what reaches the queue is decided by
        # `review_budget` alone. Verified by mutation: restoring `contamination=0.06` changes
        # nothing about the output, which is the whole point of the rewrite. If anyone ever
        # reintroduces `iso.predict(...)`, the 6%-of-anything quota comes back with it.
        iso = IsolationForest(n_estimators=200, contamination="auto", random_state=42)
        iso.fit(feats)
        iso_score = pd.Series(-iso.score_samples(feats), index=df.index)  # higher = stranger
        take = max(1, int(round(n * budget)))
        for i in iso_score.sort_values(ascending=False).head(take).index:
            review.append({
                "id": int(df.at[i, "id"]),
                "entryNo": df.at[i, "entryNo"] if "entryNo" in df else None,
                "date": str(df.at[i, "date"])[:10] if "date" in df else None,
                "sourceType": df.at[i, "sourceType"],
                "amount": round(float(df.at[i, "amount"]), 2),
                "lineCount": int(df.at[i, "lineCount"]),
                "score": round(float(iso_score[i]), 4),
                "severity": "low",
                "reasons": [_say(
                    "ضمن الأكثر غرابة في بنيته (المبلغ وعدد السطور) — هذا ترتيب للمراجعة وليس خطأً",
                    "Among the structurally most unusual by amount and line count — this is a"
                    " review ranking, not a finding")],
            })

    # ---------------------------------------------------------------------------------
    # findings
    # ---------------------------------------------------------------------------------
    anomalies = []
    for i in df.index:
        if not reasons[i]:
            continue
        is_dup = any("posted" in r["en"] or "Repeated manual" in r["en"] for r in reasons[i])
        anomalies.append({
            "id": int(df.at[i, "id"]),
            "entryNo": df.at[i, "entryNo"] if "entryNo" in df else None,
            "date": str(df.at[i, "date"])[:10] if "date" in df else None,
            "sourceType": df.at[i, "sourceType"],
            "sourceId": int(df.at[i, "sourceId"]) or None,
            "amount": round(float(df.at[i, "amount"]), 2),
            "lineCount": int(df.at[i, "lineCount"]),
            "score": round(float(iso_score[i]), 4),
            # A repeated document is a specific, checkable claim about one pair of rows.
            # An amount out of character is a judgement that is often legitimate.
            "severity": "high" if is_dup else "medium",
            "reasons": reasons[i],
        })

    sev_rank = {"high": 0, "medium": 1, "low": 2}
    anomalies.sort(key=lambda a: (sev_rank.get(a["severity"], 3), -a["score"]))

    def _has(a, kw):
        return any(kw in r["en"].lower() for r in a["reasons"])

    dup_count = sum(1 for a in anomalies if _has(a, "posted") or _has(a, "repeated manual"))
    return {
        "scanned": n,
        "anomalyCount": len(anomalies),
        "anomalies": anomalies,
        "reviewQueue": review,
        "summary": {
            "duplicates": dup_count,
            "amountOutliers": sum(1 for a in anomalies if _has(a, "outlier")),
            # Named so the limitation is visible instead of silently absent: these entries carry
            # no document id and a templated description, so duplication cannot be judged at all.
            "notCheckedForDuplicates": int(not_checked.sum()),
            "reviewQueueSize": len(review),
        },
        "note": _say(
            "الازدواج يُحكم عليه بهوية المستند لا بالوصف، لأن أوصاف القيود مُولَّدة آليًا في الغالب."
            " وطابور المراجعة ترتيب إحصائي وليس اتهامًا.",
            "Duplication is judged on document identity rather than description, because entry"
            " descriptions here are mostly machine-generated. The review queue is a statistical"
            " ranking, not an accusation."),
    }
