"""Tests for the question-understanding layer.

Run:  python -I tests/test_understand.py      (from crossbuy_ai/)

THE FIXTURE IS THE REAL CATALOGUE. tests/catalogue.json is extracted from the .NET handlers by
tools/extract_catalogue.py. Testing against an invented catalogue would prove the algorithm works
on questions I made up, which is the one thing that was never in doubt -- every wording that
defeated the old matcher was one a person chose and I had not.

Keep the fixture current: re-run the extractor after changing any handler's Keywords, Example or
Description. A stale fixture turns these from a test of the product into a test of its history.
"""
from __future__ import annotations

import json
import os
import sys
from collections import defaultdict

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from app.ml.understand import (  # noqa: E402
    ANSWER_FLOOR,
    OFFER_FLOOR,
    Intent,
    Understander,
    fingerprint,
    normalise,
    understander_for,
)
from app.ml.intent_data import NEGATIVES, OUT_OF_SCOPE  # noqa: E402
from app.ml.fallback_refusals import FALLBACK_REFUSALS  # noqa: E402

# Arabic test names go to a Windows console that defaults to cp1252, where printing one
# raises and the run dies at the first PASS. The test output must survive its own alphabet.
sys.stdout.reconfigure(encoding="utf-8")

FAILURES: list[str] = []


def check(name: str, cond: bool, detail: str = "") -> None:
    if cond:
        print(f"  PASS  {name}")
    else:
        print(f"  FAIL  {name}  {detail}")
        FAILURES.append(name)


HERE = os.path.dirname(os.path.abspath(__file__))
with open(os.path.join(HERE, "catalogue.json"), encoding="utf-8") as fh:
    CATALOGUE = json.load(fh)

INTENTS = [
    Intent(id=k, phrasings=tuple(v["keywords"] + v["example"] + v["desc"]))
    for k, v in CATALOGUE.items()
]
known = {normalise(p) for intent in INTENTS for p in intent.phrasings}
INTENTS.append(Intent(id=OUT_OF_SCOPE, phrasings=tuple(
    p for p in (*NEGATIVES, *FALLBACK_REFUSALS) if normalise(p) and normalise(p) not in known
)))
U = understander_for(INTENTS)

STOCK = "inventory.item-stock"
VALUE = "inventory.item-value"
MOVES = "inventory.item-movements"
WHS = "inventory.my-warehouses"
CASH = "accounting.cash-balance"
RECV = "accounting.open-receivables"
ANOM = "accounting.journal-anomalies"


# ===================================================================================
print("\n[1] THE FOUR WORDINGS THAT DEFEATED THE KEYWORD MATCHER")
# Each of these cost a hand-written rule in AskService. Character n-grams see all four as the
# same fact -- the word is nearly a word we know -- so none of them needs a rule.
# ===================================================================================
for q, want, why in [
    ("كام رصيد مياه معدنية", STOCK, "the plain form, as a control"),
    ("والتكلفة؟", VALUE, "a conjunction glued to the front"),
    ("اخر حركاته", MOVES, "a pronoun glued to the back"),
    ("اعرض التكلفة ليهم", VALUE, "refused outright by the keyword matcher"),
]:
    r = U.understand(q)
    check(f"{q}  ->  {want.split('.')[-1]}   [{why}]", r.intent == want,
          f"got {r.intent} at {r.confidence}")


# ===================================================================================
print("\n[2] REFUSING IS AN ANSWER")
# A matcher that always picks something is worse than one that admits it does not know: the
# figure it returns looks exactly like a correct one. "مين أحسن مورد" DID match item-stock
# before, because "عندنا" sat in that intent's keyword list.
# ===================================================================================
for q in ["ما هو الطقس اليوم", "مين أحسن مورد عندنا؟", "اكتب لي قصيدة"]:
    r = U.understand(q)
    check(f"out of catalogue is refused: {q}", r.intent == OUT_OF_SCOPE,
          f"got {r.intent} at {r.confidence}")

check("an empty question is refused", U.understand("").intent is None)
check("punctuation alone is refused", U.understand("؟؟؟ ...").intent is None)


# ===================================================================================
print("\n[3] THE SEVEN INTENTS ARE REACHABLE, AND DISTINGUISHABLE")
# A catalogue entry nobody can reach is a dead feature. Each intent must be selected by a
# wording that is NOT simply its own example copied back.
# ===================================================================================
for q, want in [
    ("ايه المخازن اللي اقدر اشوفها", WHS),
    ("المستحق على العملاء", RECV),
    ("فيه قيود شاذة؟", ANOM),
    ("show me the cash", CASH),
    ("قيمة المخزون", VALUE),
    ("كام رصيد لابتوب", STOCK),
    ("اخر حركات مياه معدنية", MOVES),
]:
    r = U.understand(q)
    check(f"{want.split('.')[-1]:<16} reachable by: {q}", r.intent == want,
          f"got {r.intent} at {r.confidence}")


# ===================================================================================
print("\n[4] THE CATALOGUE ITSELF MUST NOT BE AMBIGUOUS")
# Two intents claiming the SAME phrasing can never be told apart: they tie, and whichever
# sorts first wins silently. This check found a real defect -- in my own extractor, which gave
# two handlers that share a .cs file the same keyword list, producing impossible 0.919 ties
# between exactly those pairs. The guard belongs here either way: a copy-pasted keyword list in
# a new handler would do the same thing to the product.
#
# stock / مخزون are allowed: "stock" and "stock value" are genuinely both about stock, and the
# longer phrase wins on length. Anything else sharing a phrasing is a mistake.
# ===================================================================================
owner: dict[str, set[str]] = defaultdict(set)
for intent in INTENTS:
    for p in intent.phrasings:
        n = normalise(p)
        if n:
            owner[n].add(intent.id)
shared = {n: sorted(o) for n, o in owner.items() if len(o) > 1}
ALLOWED_SHARED = {"stock", "مخزون", "صرفنا كام"}
check("no unexpected phrasing is claimed by two intents",
      set(shared) <= ALLOWED_SHARED, str(sorted(set(shared) - ALLOWED_SHARED)))

# And no question may end in a tie at the top, whatever the catalogue says.
ties = []
for q in ["كام رصيد مياه معدنية", "اخر حركاته", "المخازن بتاعتي", "المستحق على العملاء",
          "فيه قيود شاذة؟", "قيمة المخزون", "show me the cash"]:
    r = U.understand(q)
    if len(r.ranked) > 1 and r.ranked[0].score == r.ranked[1].score and r.ranked[0].score > 0:
        ties.append(f"{q} -> {r.ranked[0].intent}={r.ranked[1].intent}={r.ranked[0].score}")
check("no question is decided by a coin toss", not ties, "; ".join(ties))


# ===================================================================================
print("\n[5] CONFIDENCE MEANS SOMETHING")
# The floors are the whole safety property: above ANSWER, acting is honest; between the two,
# the caller offers and does not act; below OFFER, there is nothing to say.
# ===================================================================================
check("the floors are ordered and inside [0,1]", 0 < OFFER_FLOOR < ANSWER_FLOOR < 1)

strong = U.understand("قيمة المخزون")
check(f"a wording the catalogue states scores high ({strong.confidence})",
      strong.confidence >= 0.9, str(strong.confidence))

nothing = U.understand("ما هو الطقس اليوم")
check("an unrelated question is captured by the refusal examples",
      nothing.intent == OUT_OF_SCOPE, f"{nothing.intent} at {nothing.confidence}")

check("a refused question still ranks the catalogue, so a caller can offer",
      len(nothing.ranked) == len(INTENTS))
check("offerable is the slice above the offer floor",
      all(m.score >= OFFER_FLOOR for m in nothing.offerable))

# An answered question never also reports itself as unsure.
answered = U.understand("اخر حركات مياه معدنية")
check("an answered question carries its own score as confidence",
      answered.intent is not None and answered.confidence == answered.ranked[0].score)


# ===================================================================================
print("\n[6] FOLDING — both sides, or the comparison is about the spelling")
# ===================================================================================
check("teh marbuta folds", normalise("معدنية") == normalise("معدنيه"))
check("alef forms fold", normalise("أحمد") == normalise("إحمد") == normalise("احمد"))
check("alef maqsura folds", normalise("على") == normalise("علي"))
check("tashkeel is dropped", normalise("مِيَاه") == normalise("مياه"))
check("tatweel is dropped", normalise("مـــياه") == normalise("مياه"))
check("Arabic-Indic digits read as Latin", normalise("٦٠٠") == "600")
check("case folds", normalise("Dell") == normalise("dell"))
check("punctuation separates rather than joins", normalise("معدنية؟") == normalise("معدنية ?"))
check("folding is idempotent", normalise(normalise("مياه معدنية ٦٠٠")) == normalise("مياه معدنية ٦٠٠"))
check("a spelling variant reaches the same intent",
      U.understand("كام رصيد ميه معدنيه").intent == U.understand("كام رصيد مياه معدنية").intent)


# ===================================================================================
print("\n[7] THE CACHE IS KEYED BY CONTENT, NOT BY IDENTITY")
# Keying on intent ids alone would serve a stale model for ever after a phrasing changed --
# the failure being that the new wording silently does not work, which is the exact bug this
# layer exists to remove.
# ===================================================================================
a = [Intent(id="x", phrasings=("alpha",))]
b = [Intent(id="x", phrasings=("alpha", "beta"))]
check("same content, same fingerprint", fingerprint(a) == fingerprint(list(a)))
check("one more phrasing, different fingerprint", fingerprint(a) != fingerprint(b))
check("phrasing order does not change the fingerprint",
      fingerprint([Intent(id="x", phrasings=("a", "b"))])
      == fingerprint([Intent(id="x", phrasings=("b", "a"))]))
check("the cache returns the same object for the same catalogue",
      understander_for(a) is understander_for(list(a)))
check("and a different one when the catalogue changed",
      understander_for(a) is not understander_for(b))


# ===================================================================================
print("\n[8] DEGRADATION — an empty or broken catalogue must not throw")
# ===================================================================================
empty = Understander([])
check("no intents at all answers nothing", empty.understand("أي سؤال").intent is None)
check("and ranks nothing", empty.understand("أي سؤال").ranked == ())
blank = Understander([Intent(id="x", phrasings=("", "   "))])
check("an intent with no usable phrasing answers nothing",
      blank.understand("أي سؤال").intent is None)
check("a question of only spaces is refused", U.understand("     ").intent is None)


print("\n" + "=" * 62)
if FAILURES:
    print(f"FAILED: {len(FAILURES)}")
    for f in FAILURES:
        print(f"   - {f}")
    sys.exit(1)
print("ALL CHECKS PASSED")
