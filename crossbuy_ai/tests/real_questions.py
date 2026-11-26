"""Hand-written questions, kept OUT of the training corpus.

A model scored on data shaped like its training data tells you it memorised the templates. These
are written as a person would type them: different verbs, different word order, colloquial forms
the templates do not contain, and no slot machinery.

HONESTY NOTE. A few of these resemble a template — "اعرض التكلفة ليهم" is close to
"اعرض التكلفة ل{i}" — and they are kept deliberately, because they are the questions that
actually failed on the screen and the point of the model is to answer them. The generated
held-out split is reported separately for the same reason: neither number alone is the truth.

The negatives are the sharp end. Several are real inventory and finance questions with no
handler; a model that routes those confidently is worse than one that refuses everything.
"""
from __future__ import annotations

from app.ml.intent_data import (
    ANOMALIES,
    HRQ as HR_WORKFORCE,
    CASH,
    MOVES,
    OUT_OF_SCOPE,
    RECEIVABLES,
    SALES_QUERY,
    CATEGORIES,
    DORMANT,
    COLLECTION,
    STOCK,
    VALUE,
    WAREHOUSES,
    TASKS,
)

REAL: list[tuple[str, str]] = [
    # --- stock -------------------------------------------------------------------------------
    ("عندي كام قطعة من مياه معدنية", STOCK),
    ("هو لسه عندنا برجر لحم ولا خلص", STOCK),
    ("اطلعلي الموجود من لابتوب", STOCK),
    ("ميه معدنيه فيه كام", STOCK),
    ("how many mineral water are left", STOCK),
    ("كام رصيد مياه معدنية؟", STOCK),
    # --- value -------------------------------------------------------------------------------
    ("بكام اشترينا لابتوب Dell", VALUE),
    ("اعرض التكلفة ليهم", VALUE),
    ("قيمة اللي في المخزن من سكر", VALUE),
    ("والتكلفة؟", VALUE),
    ("what is a chicken burger worth", VALUE),
    # --- movements ---------------------------------------------------------------------------
    ("عايز كشف حركة مياه معدنية", MOVES),
    ("اخر حركاته", MOVES),
    ("اتصرف كام من برجر دجاج", MOVES),
    ("show the last transactions for mineral water", MOVES),
    # --- warehouses --------------------------------------------------------------------------
    ("انا شايف انهي مخازن", WAREHOUSES),
    ("اقدر اوصل لانهي مخزن", WAREHOUSES),
    ("which stores am I allowed to see", WAREHOUSES),
    # --- cash --------------------------------------------------------------------------------
    ("احنا معانا كام دلوقتي", CASH),
    ("الفلوس اللي في البنك", CASH),
    ("how much money is in the treasury", CASH),
    # --- receivables -------------------------------------------------------------------------
    ("العملاء عليهم كام", RECEIVABLES),
    ("مين لسه مدفعش من العملاء", RECEIVABLES),
    ("مين ما سددش حسابه؟", RECEIVABLES),
    ("العملاء لسه عليهم فلوس قد ايه؟", RECEIVABLES),
    ("total outstanding from customers", RECEIVABLES),
    # Collected cash is a separate capability; do not substitute sales or current receivables.
    ("قبضنا كام من العملاء؟", OUT_OF_SCOPE),
    # --- anomalies ---------------------------------------------------------------------------
    ("فيه قيد غلط في اليومية", ANOMALIES),
    ("راجعلي اليومية فيه حاجة غريبة", ANOMALIES),
    ("are there any odd entries in the ledger", ANOMALIES),
    # --- top selling -------------------------------------------------------------------------
    ("قوللي الاصناف الاعلى في المبيعات", SALES_QUERY),
    ("ايه البضاعة اللي بتتباع اكتر من غيرها", SALES_QUERY),
    ("which goods brought in the most money", SALES_QUERY),

    # --- customers, dormancy, collection, margin ---------------------------------------------
    ("مين اكبر زباين عندنا", SALES_QUERY),
    ("العملاء اللي بيشتروا اكتر حاجة", SALES_QUERY),
    ("which clients spend the most with us", SALES_QUERY),
    ("ايه البضاعة اللي واقفة من زمان", DORMANT),
    ("فيه بضاعة ساكنة من شهور", DORMANT),
    ("which stock has not moved in months", DORMANT),
    ("العملاء بيسددوا بعد كام يوم", COLLECTION),
    ("التحصيل عندنا بياخد قد ايه", COLLECTION),
    ("how many days until we actually get paid", COLLECTION),
    ("كام هامش الربح على برجر دجاج", SALES_QUERY),
    ("بنكسب كام في المية على المبيعات", SALES_QUERY),
    ("which products make us the most profit", SALES_QUERY),

    ("وريني المبيعات شهر بشهر", SALES_QUERY),
    ("كام قطعه راحت من كل صنف", SALES_QUERY),

    ("طيب ممكن تجيب اخر فاتورة بيع؟", SALES_QUERY),
    ("وريني اخر ثلاث فواتير", SALES_QUERY),
    ("what was the most recent sale", SALES_QUERY),
    ("وريني المهام المفتوحة", TASKS),
    ("عندي شغل متأخر لازم أخلصه؟", TASKS),
    ("في مهام قربت تستحق؟", TASKS),
    ("اعرض المهام اللي لسه مخلصتش", TASKS),
    ("What work is still waiting on me?", TASKS),
    ("What should I finish before Friday?", TASKS),
    ("ما المهام المتأخرة", TASKS),
    ("عدد المهام حسب الحالة", TASKS),
    ("show overdue tasks", TASKS),
    ("list my tasks", TASKS),
    ("which tasks are due soon", TASKS),
    ("create a task for me", OUT_OF_SCOPE),

    ("طيب قول المخزن ال متسجله عندنا", WAREHOUSES),
    ("عندنا كام مخزن بالظبط", WAREHOUSES),

    ("قول علي فئات الاصناف ال عندنا", CATEGORIES),
    ("الفئات", CATEGORIES),
    ("المبيعات لكل تصنيف", SALES_QUERY),

    # --- NOT OURS: the sharp end -------------------------------------------------------------
    ("مين أحسن مورد عندنا؟", OUT_OF_SCOPE),
    ("امتى المفروض اطلب من المورد", OUT_OF_SCOPE),
    ("ما هو الطقس اليوم", OUT_OF_SCOPE),
    # MOVED FROM OUT_OF_SCOPE ON 2026-11-10, because the capability changed, not the test.
    # hr.workforce now answers it from the Employee roster. Leaving it here as unanswerable
    # would have trained and measured the model to refuse something the product does.
    ("كام موظف في الشركة", HR_WORKFORCE),
    ("اعملي تقرير مبيعات سنوي", OUT_OF_SCOPE),
    ("what do we owe to suppliers", OUT_OF_SCOPE),
    ("ايه نسبة تلبية الطلبات عندنا", OUT_OF_SCOPE),
    ("الموازنة مقابل الفعلي للشهر ده", OUT_OF_SCOPE),
    ("ايه الاصناف اللي هتخلص الاسبوع الجاي", OUT_OF_SCOPE),
    ("which supplier raised prices the most", OUT_OF_SCOPE),
    ("write me a poem about inventory", OUT_OF_SCOPE),
    ("ازيك عامل ايه", OUT_OF_SCOPE),
]
