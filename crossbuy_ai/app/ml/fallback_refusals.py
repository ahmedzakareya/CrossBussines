"""Extra local refusal examples for the dynamic matcher, separate from training holdouts.

These are alternate phrasings of benchmark topics, not the benchmark questions themselves, so
they teach a refusal boundary without leaking the independent evaluation set into model training.
"""

FALLBACK_REFUSALS = (
    "اخبار الطقس اليوم", "درجات الحرارة اليوم", "هل الدنيا هتمطر النهارده",
    "forecast weather today", "is it raining outside",
    "مين المورد الافضل للتعامل", "ترتيب الموردين حسب الجودة", "اي الموردين افضل سعرا",
    "which vendor is our best supplier", "rank suppliers by quality",
    "what is the supplier rating", "compare vendor performance",
)
