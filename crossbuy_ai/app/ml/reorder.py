"""Reorder point engine (local, no LLM / no API key, no third-party math).

Computes, per item x warehouse, the three fields that `ItemWarehouseSettings`
has always carried and nothing has ever filled: ReorderPoint, SafetyStock and
the demand rate behind them.

WHY THIS EXISTS SEPARATELY FROM `inventory.py`
----------------------------------------------
`analyze_inventory` receives only *totals* (out90, out30) and derives a daily
rate as `out90 / 90`. That is correct only when demand is spread across the
window. Measured against the real CrossBuy catalogue it is not: 816 of 882
outbound movements fall in two months, and August's 432 movements happened on
five days. Under `out90 / 90`, a single-day issue of 2,470 units reads as a
steady 27 units/day for ever.

So this engine never accepts a total on its own. Every row must carry how many
distinct DAYS the demand touched, over what SPAN, and the sum of squared daily
quantities. Those three turn a total into a distribution -- and a distribution
is the only thing a reorder point can honestly be computed from.

THE MODEL
---------
Classical inventory control, written out rather than imported:

    reorder point = (mean daily demand x lead time) + safety stock
    safety stock  = z x sqrt(lead time x var(demand) + mean(demand)^2 x var(lead time))

The second term is what protects against a late supplier as well as a busy
week; dropping it (as most hand-typed reorder points do) silently assumes the
supplier is never late. `z` comes from the requested service level through an
inverse normal CDF implemented below, so the service level is a free parameter
the business sets -- not a constant baked into the code.

REFUSAL IS A RESULT
-------------------
When the history cannot support a number, this engine returns `advise: False`
and says what is missing. It does not emit a plausible-looking figure. A
reorder point is a standing instruction to spend money; one computed from two
days of history is worse than no reorder point, because it looks considered.
"""
from __future__ import annotations

import math
from datetime import date, datetime

# ---------------------------------------------------------------------------
# Grading thresholds. Named, in one place, so a business can argue with them.
# ---------------------------------------------------------------------------
GRADE_A_DAYS, GRADE_A_SPAN = 12, 60     # enough points, spread far enough apart
GRADE_B_DAYS, GRADE_B_SPAN = 6, 30
GRADE_C_DAYS = 3                        # a shape, but a weak one
BURST_SHARE = 0.50                      # one day holding >=50% of demand is a burst
MIN_SERVICE, MAX_SERVICE = 0.50, 0.999

# Demand SHAPE, which is a different question from history SUFFICIENCY.
# The safety-stock formula assumes demand is roughly normal around its mean.
# Once the standard deviation outgrows the mean by this much, demand is
# intermittent rather than noisy, the normal assumption stops holding, and the
# variability term swells until it dominates the reorder point. The number is
# still the best this model can do -- but it must not be handed over silently.
CV_WARN = 1.5                           # sd >= 1.5x mean: say so
CV_SEVERE = 3.0                         # sd >= 3x mean: the model is the wrong shape
SAFETY_DOMINANT = 0.60                  # safety stock >= 60% of the point


# ---------------------------------------------------------------------------
# Inverse normal CDF -- Peter Acklam's rational approximation.
# Written out so the service level stays a parameter and the service keeps no
# dependency on SciPy. MEASURED absolute error across the service levels in use
# (0.50 .. 0.999) is below 2e-9 -- see tests/test_reorder.py, which asserts it
# rather than quoting the author's figure. That is some orders of magnitude
# finer than any demand history justifies.
# ---------------------------------------------------------------------------
_A = (-3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02,
      1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00)
_B = (-5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02,
      6.680131188771972e+01, -1.328068155288572e+01)
_C = (-7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00,
      -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00)
_D = (7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00,
      3.754408661907416e+00)
_P_LOW, _P_HIGH = 0.02425, 1 - 0.02425


def z_for_service_level(p: float) -> float:
    """The z such that P(Z <= z) = p, for a standard normal Z."""
    if not (0.0 < p < 1.0):
        raise ValueError("service level must be strictly between 0 and 1")
    if p < _P_LOW:
        q = math.sqrt(-2 * math.log(p))
        return (((((_C[0] * q + _C[1]) * q + _C[2]) * q + _C[3]) * q + _C[4]) * q + _C[5]) / \
               ((((_D[0] * q + _D[1]) * q + _D[2]) * q + _D[3]) * q + 1)
    if p > _P_HIGH:
        q = math.sqrt(-2 * math.log(1 - p))
        return -(((((_C[0] * q + _C[1]) * q + _C[2]) * q + _C[3]) * q + _C[4]) * q + _C[5]) / \
                ((((_D[0] * q + _D[1]) * q + _D[2]) * q + _D[3]) * q + 1)
    q = p - 0.5
    r = q * q
    return (((((_A[0] * r + _A[1]) * r + _A[2]) * r + _A[3]) * r + _A[4]) * r + _A[5]) * q / \
           (((((_B[0] * r + _B[1]) * r + _B[2]) * r + _B[3]) * r + _B[4]) * r + 1)


# ---------------------------------------------------------------------------
# helpers
# ---------------------------------------------------------------------------
def _as_date(v):
    if v is None or v == "":
        return None
    if isinstance(v, datetime):
        return v.date()
    if isinstance(v, date):
        return v
    s = str(v).strip().replace("Z", "")
    for fmt in ("%Y-%m-%dT%H:%M:%S.%f", "%Y-%m-%dT%H:%M:%S", "%Y-%m-%d %H:%M:%S", "%Y-%m-%d"):
        try:
            return datetime.strptime(s[:26], fmt).date()
        except ValueError:
            continue
    try:
        return datetime.fromisoformat(s).date()
    except ValueError:
        return None


def _f(v, default=0.0):
    try:
        return float(v)
    except (TypeError, ValueError):
        return default


def _say(ar: str, en: str) -> dict:
    return {"ar": ar, "en": en}


# ---------------------------------------------------------------------------
# grading
# ---------------------------------------------------------------------------
def grade_history(active_days: int, span_days: int, max_day_share: float) -> tuple[str, float, list]:
    """Grade what the history can support, before any number is computed.

    Returns (grade, confidence 0..1, notes). Grade D means: do not advise.
    """
    notes = []
    if active_days >= GRADE_A_DAYS and span_days >= GRADE_A_SPAN:
        grade, conf = "A", 0.90
    elif active_days >= GRADE_B_DAYS and span_days >= GRADE_B_SPAN:
        grade, conf = "B", 0.70
        notes.append(_say(
            f"تاريخ محدود: {active_days} يوم حركة على مدى {span_days} يوم",
            f"Limited history: {active_days} active days across {span_days} days"))
    elif active_days >= GRADE_C_DAYS:
        grade, conf = "C", 0.45
        notes.append(_say(
            f"تاريخ ضعيف: {active_days} أيام حركة فقط" +
            (f" ومتجمّعة في {span_days} يوم" if span_days < GRADE_B_SPAN else ""),
            f"Weak history: only {active_days} active days" +
            (f", clustered in {span_days} days" if span_days < GRADE_B_SPAN else "")))
    else:
        grade, conf = "D", 0.0
        notes.append(_say(
            f"لا يكفي للحساب: الصرف حصل في {active_days} يوم فقط",
            f"Not enough to compute: demand occurred on only {active_days} day(s)"))

    # A burst can sit inside any grade: twelve active days still fail if one of
    # them carries the whole year. Downgrade rather than silently average it.
    if max_day_share >= BURST_SHARE and grade != "D":
        notes.append(_say(
            f"يوم واحد يحمل {round(max_day_share * 100)}% من إجمالي الصرف — توزيع غير مستقر",
            f"A single day carries {round(max_day_share * 100)}% of all demand — unstable distribution"))
        grade = {"A": "B", "B": "C", "C": "D"}[grade]
        conf = {"B": 0.55, "C": 0.35, "D": 0.0}[grade]
        if grade == "D":
            notes.append(_say(
                "بعد خصم الانفجار لم يتبقَّ تاريخ كافٍ",
                "After discounting the burst, no usable history remains"))
    return grade, conf, notes


# ---------------------------------------------------------------------------
# the engine
# ---------------------------------------------------------------------------
def compute_reorder_points(as_of, window_days: int, service_level: float,
                           default_lead_time_days: float, rows: list[dict]) -> dict:
    """Compute a reorder point per item x warehouse, or refuse and say why.

    Each row carries the *distribution* of demand, not just its total:
        activeDays   distinct days with an outbound movement in the window
        spanDays     first outbound day -> last outbound day
        totalQty     sum of outbound quantity
        sumSqQty     sum of (daily quantity)^2  -- gives the variance exactly
        maxDayQty    largest single day         -- detects a burst
    """
    as_of_d = _as_date(as_of) or date.today()
    window_days = max(1, int(window_days))
    service_level = min(MAX_SERVICE, max(MIN_SERVICE, _f(service_level, 0.95)))
    z = z_for_service_level(service_level)
    default_lt = max(0.0, _f(default_lead_time_days, 7.0))

    results, advised, refused, no_demand = [], 0, 0, 0
    grades = {"A": 0, "B": 0, "C": 0, "D": 0}

    for r in rows:
        active_days = int(_f(r.get("activeDays"), 0))
        span_days = int(_f(r.get("spanDays"), 0))
        total_qty = _f(r.get("totalQty"))
        sum_sq = _f(r.get("sumSqQty"))
        max_day = _f(r.get("maxDayQty"))
        on_hand = _f(r.get("onHand"))
        first_out = _as_date(r.get("firstOut"))
        last_out = _as_date(r.get("lastOut"))

        lead_time = _f(r.get("leadTimeDays"), 0.0) or default_lt
        lead_sd = max(0.0, _f(r.get("leadTimeSdDays"), 0.0))
        lt_source = "setting" if _f(r.get("leadTimeDays"), 0.0) > 0 else "default"

        base = {
            "itemId": r.get("itemId"),
            "warehouseId": r.get("warehouseId"),
            "code": r.get("code"),
            "name": r.get("name"),
            "nameEn": r.get("nameEn"),
            "warehouseName": r.get("warehouseName"),
            "onHand": round(on_hand, 3),
            "activeDays": active_days,
            "spanDays": span_days,
            "totalQty": round(total_qty, 3),
            "currentReorderPoint": (round(_f(r["currentReorderPoint"]), 3)
                                    if r.get("currentReorderPoint") is not None else None),
            "currentSafetyStock": (round(_f(r["currentSafetyStock"]), 3)
                                   if r.get("currentSafetyStock") is not None else None),
            "leadTimeDays": round(lead_time, 2),
            "leadTimeSource": lt_source,
        }

        # ---- no demand at all: a real answer, not a refusal ----------------
        if total_qty <= 0 or active_days <= 0:
            no_demand += 1
            grades["D"] += 1
            results.append({**base, "grade": "D", "confidence": 0.0, "advise": False,
                            "status": "noDemand",
                            "avgDailyDemand": 0.0, "sdDailyDemand": 0.0,
                            "reorderPoint": None, "safetyStock": None,
                            "daysOfCover": None, "suggestedOrderQty": None,
                            "terms": [],
                            "reasons": [_say(
                                f"لا يوجد أي صرف خلال {window_days} يوم — الصنف راكد في هذا المخزن",
                                f"No issues at all in {window_days} days — dormant in this warehouse")]})
            continue

        max_share = (max_day / total_qty) if total_qty > 0 else 0.0
        grade, confidence, notes = grade_history(active_days, span_days, max_share)
        grades[grade] += 1

        # ---- the effective window -----------------------------------------
        # Measure from the first issue, not from the window edge: an item that
        # only started moving six weeks ago must not have its rate diluted by
        # the fifty quiet days before it existed here.
        eff_days = (as_of_d - first_out).days + 1 if first_out else window_days
        eff_days = max(1, min(window_days, eff_days))

        # ---- demand distribution ------------------------------------------
        # Mean and variance over EVERY day in the effective window, zeros
        # included -- the quiet days are part of the demand pattern, not gaps
        # in it. Zero days contribute nothing to sum_sq, so E[X^2] is exact.
        mean_d = total_qty / eff_days
        var_d = max(0.0, (sum_sq / eff_days) - (mean_d * mean_d))
        sd_d = math.sqrt(var_d)

        # ---- refusal ------------------------------------------------------
        if grade == "D":
            refused += 1
            results.append({**base, "grade": "D", "confidence": 0.0, "advise": False,
                            "status": "insufficientHistory",
                            "avgDailyDemand": round(mean_d, 4),
                            "sdDailyDemand": round(sd_d, 4),
                            "reorderPoint": None, "safetyStock": None,
                            "daysOfCover": round(on_hand / mean_d, 1) if mean_d > 0 else None,
                            "suggestedOrderQty": None,
                            "effectiveWindowDays": eff_days,
                            "terms": [],
                            "reasons": notes + [_say(
                                "لن يُقترح رقم. نقطة إعادة طلب مبنية على تاريخ ناقص أسوأ من لا شيء،"
                                " لأنها تبدو مدروسة.",
                                "No number will be proposed. A reorder point built on thin history is"
                                " worse than none, because it looks considered.")],
                            "needed": _say(
                                f"يلزم صرف على {GRADE_C_DAYS} أيام مختلفة على الأقل"
                                f" (الأفضل {GRADE_B_DAYS} أيام عبر {GRADE_B_SPAN} يوم)",
                                f"Needs issues on at least {GRADE_C_DAYS} distinct days"
                                f" (ideally {GRADE_B_DAYS} across {GRADE_B_SPAN} days)")})
            continue

        # ---- the two protected terms --------------------------------------
        demand_var_term = lead_time * var_d              # a busy lead time
        lead_var_term = (mean_d ** 2) * (lead_sd ** 2)   # a late supplier
        safety = z * math.sqrt(max(0.0, demand_var_term + lead_var_term))
        cycle = mean_d * lead_time
        rop = cycle + safety

        days_cover = (on_hand / mean_d) if mean_d > 0 else None
        shortfall = rop - on_hand

        terms = [
            {"key": "cycleDemand", "value": round(cycle, 3),
             "label": _say(f"الطلب خلال مهلة التوريد ({round(lead_time, 1)} يوم)",
                           f"Demand during lead time ({round(lead_time, 1)} days)"),
             "detail": _say(f"{round(mean_d, 3)} وحدة/يوم × {round(lead_time, 1)} يوم",
                            f"{round(mean_d, 3)} units/day x {round(lead_time, 1)} days")},
            {"key": "safetyStock", "value": round(safety, 3),
             "label": _say(f"مخزون أمان عند مستوى خدمة {round(service_level * 100, 1)}%",
                           f"Safety stock at {round(service_level * 100, 1)}% service level"),
             "detail": _say(f"z={round(z, 3)} × الجذر التربيعي لـ(تذبذب الطلب + تذبذب المهلة)",
                            f"z={round(z, 3)} x sqrt(demand variability + lead-time variability)")},
            {"key": "demandVariability", "value": round(demand_var_term, 3),
             "label": _say("تذبذب الطلب خلال المهلة", "Demand variability over the lead time"),
             "detail": _say(f"الانحراف المعياري {round(sd_d, 3)} وحدة/يوم",
                            f"standard deviation {round(sd_d, 3)} units/day")},
            {"key": "leadTimeVariability", "value": round(lead_var_term, 3),
             "label": _say("تذبذب مهلة التوريد", "Lead-time variability"),
             "detail": _say(
                 f"انحراف المهلة {round(lead_sd, 2)} يوم" if lead_sd > 0
                 else "غير مقيس — يُفترض مورّد منضبط، وهذا افتراض متفائل",
                 f"lead-time sd {round(lead_sd, 2)} days" if lead_sd > 0
                 else "not measured — assumes a punctual supplier, which is optimistic")},
        ]

        # ---- is the classical formula the right shape for this demand? ----
        # Reported on its own axis: `grade` answers "is there enough history",
        # this answers "is this demand the kind the formula was built for".
        # Conflating them would hide one behind the other.
        cv = (sd_d / mean_d) if mean_d > 0 else 0.0
        safety_share = (safety / rop) if rop > 0 else 0.0

        reasons = list(notes)
        if cv >= CV_SEVERE:
            confidence = min(confidence, 0.30)
            reasons.append(_say(
                f"طلب متقطّع بشدّة: الانحراف المعياري ({round(sd_d, 2)}) يفوق المتوسط"
                f" ({round(mean_d, 2)}) بـ{round(cv, 1)} مرة. صيغة مخزون الأمان مبنية على طلب"
                " شبه-طبيعي، وهذا ليس كذلك — عامل الرقم كحدّ أعلى لا كتوصية.",
                f"Severely intermittent demand: the standard deviation ({round(sd_d, 2)}) is"
                f" {round(cv, 1)}x the mean ({round(mean_d, 2)}). The safety-stock formula assumes"
                " near-normal demand and this is not — treat the figure as an upper bound,"
                " not a recommendation."))
        elif cv >= CV_WARN:
            confidence = min(confidence, 0.50)
            reasons.append(_say(
                f"طلب متذبذب: الانحراف المعياري {round(cv, 1)} مرة قدر المتوسط،"
                " فمخزون الأمان مرتفع بطبيعته",
                f"Lumpy demand: the standard deviation is {round(cv, 1)}x the mean,"
                " so safety stock is inherently high"))
        if safety_share >= SAFETY_DOMINANT:
            reasons.append(_say(
                f"{round(safety_share * 100)}% من نقطة الطلب مخزون أمان وليس طلبًا متوقّعًا"
                " — راجِع مهلة التوريد أولًا قبل اعتماد الرقم",
                f"{round(safety_share * 100)}% of the point is safety stock rather than expected"
                " demand — check the lead time before accepting it"))

        if lt_source == "default":
            reasons.append(_say(
                f"مهلة التوريد غير مسجّلة لهذا الصنف — استُخدم الافتراضي {round(default_lt, 1)} يوم",
                f"No lead time recorded for this item — used the {round(default_lt, 1)}-day default"))
        if lead_sd <= 0:
            reasons.append(_say(
                "تذبذب المهلة غير مقيس، فمخزون الأمان يحمي من ازدحام الطلب فقط لا من تأخّر المورّد",
                "Lead-time variability is unmeasured, so safety stock covers busy demand only,"
                " not a late supplier"))
        if last_out and (as_of_d - last_out).days > 45:
            reasons.append(_say(
                f"آخر صرف من {(as_of_d - last_out).days} يوم — قد يكون النمط توقّف",
                f"Last issue was {(as_of_d - last_out).days} days ago — the pattern may have stopped"))

        if shortfall > 0:
            action = "order"
            action_text = _say(
                f"الرصيد {round(on_hand, 2)} تحت نقطة الطلب المحسوبة {round(rop, 2)} — يلزم طلب"
                f" {round(shortfall, 2)} على الأقل",
                f"On hand {round(on_hand, 2)} is below the computed point {round(rop, 2)} —"
                f" order at least {round(shortfall, 2)}")
        else:
            action = "hold"
            action_text = _say(
                f"الرصيد {round(on_hand, 2)} فوق نقطة الطلب {round(rop, 2)} — لا إجراء الآن",
                f"On hand {round(on_hand, 2)} is above the point {round(rop, 2)} — no action now")

        advised += 1
        results.append({**base, "grade": grade, "confidence": round(confidence, 2),
                        "advise": True, "status": "computed",
                        "avgDailyDemand": round(mean_d, 4),
                        "sdDailyDemand": round(sd_d, 4),
                        "coefficientOfVariation": round(cv, 3),
                        "demandShape": ("intermittent" if cv >= CV_SEVERE
                                        else "lumpy" if cv >= CV_WARN else "stable"),
                        "safetyShare": round(safety_share, 3),
                        "effectiveWindowDays": eff_days,
                        "reorderPoint": round(rop, 3),
                        "safetyStock": round(safety, 3),
                        "daysOfCover": round(days_cover, 1) if days_cover is not None else None,
                        "suggestedOrderQty": round(shortfall, 3) if shortfall > 0 else 0.0,
                        "action": action, "actionText": action_text,
                        "terms": terms, "reasons": reasons})

    # Order so the ones a buyer must act on today sit at the top, then the best
    # evidence, then the largest exposure.
    rank = {"order": 0, "hold": 1}
    g_rank = {"A": 0, "B": 1, "C": 2, "D": 3}
    results.sort(key=lambda x: (0 if x["advise"] else 1,
                                rank.get(x.get("action"), 2),
                                g_rank.get(x["grade"], 4),
                                -(x.get("suggestedOrderQty") or 0)))

    return {
        "asOf": as_of_d.isoformat(),
        "windowDays": window_days,
        "serviceLevel": round(service_level, 4),
        "z": round(z, 4),
        "defaultLeadTimeDays": round(default_lt, 2),
        "pairsAnalyzed": len(rows),
        "summary": {
            "advised": advised,
            "refusedInsufficientHistory": refused,
            "noDemand": no_demand,
            "toOrderNow": sum(1 for x in results if x.get("action") == "order"),
            "grades": grades,
        },
        "note": _say(
            "الأرقام المقترحة لا تُكتب في النظام. تُعرض على المشتري، وهو من يعتمدها.",
            "Proposed numbers are not written to the system. They are shown to the buyer, who decides."),
        "results": results,
    }
