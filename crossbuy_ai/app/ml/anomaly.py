"""Journal-entry anomaly detection (local ML — no LLM, no API key).

Three complementary signals, each producing bilingual reasons:
  1. IsolationForest on (log amount, line count) — global structural outliers.
  2. Per-source-type amount z-score — "unusual for its kind".
  3. Duplicate grouping (amount + source type + description) — possible double-posting.

The model only flags; a human reviews. Designed to degrade gracefully on small
datasets (skips the forest below 10 rows, skips z-score groups below 5 rows).
"""
from __future__ import annotations

import numpy as np
import pandas as pd
from sklearn.ensemble import IsolationForest


def _norm_desc(s: object) -> str:
    return (str(s) if s is not None else "").strip().lower()


def detect_journal_anomalies(entries: list[dict]) -> dict:
    n = len(entries)
    if n == 0:
        return {"scanned": 0, "anomalyCount": 0, "anomalies": [], "summary": {}}

    df = pd.DataFrame(entries)
    df["amount"] = pd.to_numeric(df.get("amount"), errors="coerce").fillna(0.0)
    df["lineCount"] = pd.to_numeric(df.get("lineCount"), errors="coerce").fillna(0).astype(int)
    df["sourceType"] = df.get("sourceType")
    df["sourceType"] = df["sourceType"].where(df["sourceType"].notna(), "Manual")
    df["descN"] = df.get("description").map(_norm_desc) if "description" in df else ""

    reasons: dict[int, list] = {i: [] for i in df.index}
    iso_score = pd.Series(0.0, index=df.index)

    # 1) Global structural outliers
    if n >= 10:
        feats = np.column_stack([
            np.log1p(df["amount"].clip(lower=0).to_numpy()),
            df["lineCount"].to_numpy(dtype=float),
        ])
        iso = IsolationForest(n_estimators=200, contamination=0.06, random_state=42)
        iso.fit(feats)
        iso_score = pd.Series(-iso.score_samples(feats), index=df.index)  # higher = more anomalous
        for i in df.index[iso.predict(feats) == -1]:
            reasons[i].append({"ar": "مبلغ أو بنية قيد غير معتادة",
                               "en": "Unusual amount or entry structure"})

    # 2) Per-source-type amount z-score
    for src, grp in df.groupby("sourceType"):
        if len(grp) >= 5:
            mu = grp["amount"].mean()
            sd = grp["amount"].std(ddof=0)
            if sd and sd > 0:
                for i in grp.index:
                    if abs((df.at[i, "amount"] - mu) / sd) > 3:
                        reasons[i].append({"ar": f"مبلغ شاذ مقارنةً بنوع القيد ({src})",
                                           "en": f"Outlier amount for source type ({src})"})

    # 3) Possible duplicates — same amount + type + description AND same day.
    # The date constraint avoids flagging legitimate recurring entries (monthly
    # depreciation / payroll share amount+description across different months).
    df["amtKey"] = df["amount"].round(2)
    df["dateKey"] = df.get("date").astype(str).str.slice(0, 10) if "date" in df else ""
    dup = df.groupby(["amtKey", "sourceType", "descN", "dateKey"]).filter(lambda g: len(g) > 1)
    for i in dup.index:
        reasons[i].append({"ar": "احتمال ازدواج قيد (نفس المبلغ والوصف والنوع وفي نفس اليوم)",
                           "en": "Possible duplicate (same amount, description, type, same day)"})

    anomalies = []
    for i in df.index:
        if reasons[i]:
            # Severity: a same-day duplicate is a specific, actionable error → "high".
            # Statistical outliers ("unusual amount") are worth a look but often
            # legitimate (a big asset purchase) → "medium".
            is_dup = any("duplicate" in r["en"].lower() for r in reasons[i])
            severity = "high" if is_dup else "medium"
            anomalies.append({
                "id": int(df.at[i, "id"]),
                "entryNo": df.at[i, "entryNo"] if "entryNo" in df else None,
                "date": str(df.at[i, "date"])[:10] if "date" in df else None,
                "sourceType": df.at[i, "sourceType"],
                "amount": round(float(df.at[i, "amount"]), 2),
                "score": round(float(iso_score[i]), 4),
                "severity": severity,
                "reasons": reasons[i],
            })
    sev_rank = {"high": 0, "medium": 1, "low": 2}
    anomalies.sort(key=lambda a: (sev_rank.get(a["severity"], 3), -a["score"]))

    def _has(a, kw):
        return any(kw in r["en"].lower() for r in a["reasons"])

    return {
        "scanned": n,
        "anomalyCount": len(anomalies),
        "anomalies": anomalies,
        "summary": {
            "duplicates": sum(1 for a in anomalies if _has(a, "duplicate")),
            "amountOutliers": sum(1 for a in anomalies if _has(a, "outlier") or _has(a, "unusual")),
        },
    }
