"""Tests for the trained intent model.

Run:  python -I tests/test_intent_model.py      (from crossbuy_ai/)
      (train first:  python -I tools/train_intent_model.py)

WHAT A TEST CAN AND CANNOT PIN ABOUT A MODEL. It cannot pin a prediction: retrain on one more
template and any single answer may move, and a test that forbids that forbids improvement. What
it CAN pin is the behaviour the model is relied on for, and the numbers it must not fall below.

So this asserts a FLOOR ON QUALITY, not a snapshot of outputs:
  * it must answer the real questions at least this well,
  * it must NEVER answer one we have no handler for — that number is zero, not "low",
  * it must refuse rather than resolve a near-tie,
  * it must survive nonsense without raising.

The expensive mistake is a confident wrong answer, not a refusal, so the two are counted apart and
never averaged into an "accuracy".
"""
from __future__ import annotations

import os
import sys

sys.stdout.reconfigure(encoding="utf-8")
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))
sys.path.insert(0, HERE)

from app.ml.intent_data import OUT_OF_SCOPE, TASKS  # noqa: E402
from app.ml.intent_model import ARTIFACT, MIN_MARGIN, load  # noqa: E402
from real_questions import REAL  # noqa: E402

FAILURES: list[str] = []


def check(name: str, cond: bool, detail: str = "") -> None:
    if cond:
        print(f"  PASS  {name}")
    else:
        print(f"  FAIL  {name}  {detail}")
        FAILURES.append(name)


MODEL = load()
if MODEL is None:
    print(f"\n  NO TRAINED MODEL at {ARTIFACT}")
    print("  The artifact is a build output, not source. Train it with:")
    print("      python -I tools/train_intent_model.py")
    # Not a failure: a checkout without an artifact is normal, and the service still answers
    # through the nearest-neighbour matcher. Saying so beats a red run nobody can act on.
    sys.exit(0)


# ===================================================================================
print("\n[1] THE REAL QUESTIONS — hand-written, outside the training corpus")
# ===================================================================================
answered = correct = wrong = leaked = refused_wrongly = refused_rightly = 0
detail: list[str] = []
for text, truth in REAL:
    p = MODEL.predict(text)
    got = p.intent or OUT_OF_SCOPE
    if got == OUT_OF_SCOPE:
        if truth == OUT_OF_SCOPE:
            refused_rightly += 1
        else:
            refused_wrongly += 1
    else:
        answered += 1
        if truth == OUT_OF_SCOPE:
            leaked += 1
            detail.append(f"LEAKED {text} -> {got}")
        elif got == truth:
            correct += 1
        else:
            wrong += 1
            detail.append(f"wrong {text} -> {got} (wanted {truth})")

in_scope = sum(1 for _, t in REAL if t != OUT_OF_SCOPE)
out_scope = len(REAL) - in_scope
print(f"    {correct}/{in_scope} answerable answered correctly, "
      f"{refused_rightly}/{out_scope} refusals right, {wrong} wrong, {leaked} leaked")

# ZERO, NOT "FEW". Answering a question we have no handler for means inventing a figure for
# something nobody implemented, and it reads exactly like a real answer.
check("it NEVER answers a question we have no handler for", leaked == 0, "; ".join(detail))
check("it never answers the wrong one of ours", wrong == 0, "; ".join(detail))
check("it answers at least 20 of the 27 answerable", correct >= 20, f"{correct}/{in_scope}")
check("it refuses at least 8 of the 10 it cannot answer", refused_rightly >= 8,
      f"{refused_rightly}/{out_scope}")
check("precision when it answers is 1.0", answered > 0 and (correct / answered) == 1.0,
      f"{correct}/{answered}")


# ===================================================================================
# A few overlaps are deliberate - the wordings that actually failed on the owner's screen - but
# the set must not quietly become a memory test as templates are added. The cap is the contract.
from app.ml.intent_data import NEGATIVES, TEMPLATES  # noqa: E402
from app.ml.understand import normalise  # noqa: E402

trained = {normalise(t) for v in TEMPLATES.values() for t in v} | {normalise(n) for n in NEGATIVES}
overlap = [q for q, _ in REAL if normalise(q) in trained]
check(f"at most 6 benchmark questions are verbatim training data ({len(overlap)})",
      len(overlap) <= 6, "; ".join(overlap))
check("the benchmark is still mostly unseen",
      len(overlap) / max(1, len(REAL)) < 0.2, f"{len(overlap)}/{len(REAL)}")

print("\n[2] THE FOUR WORDINGS THAT DEFEATED THE KEYWORD MATCHER")
# Each cost a hand-written rule in AskService. The model was never given those rules.
# ===================================================================================
for q, want in [
    ("كام رصيد مياه معدنية", "inventory.item-stock"),
    ("والتكلفة؟", "inventory.item-value"),
    ("اخر حركاته", "inventory.item-movements"),
    ("اعرض التكلفة ليهم", "inventory.item-value"),
]:
    got = MODEL.predict(q).intent
    check(f"{q}  ->  {want.split('.')[-1]}", got == want, f"got {got}")

# Tasks are a new governed surface: keep conversational Arabic and English phrasings reachable
# after retraining, while the real-question benchmark continues to guard wrong routes and leaks.
print("\n[2b] TASKS ARE REACHABLE IN ARABIC AND ENGLISH")
for q in ["وريني المهام المفتوحة", "ما المهام المتأخرة", "عدد المهام حسب الحالة",
          "show overdue tasks", "list my tasks", "which tasks are due soon"]:
    got = MODEL.predict(q).intent
    check(f"{q}  ->  tasks.query", got == TASKS, f"got {got}")


# ===================================================================================
print("\n[3] A NEAR-TIE IS OFFERED, NOT RESOLVED")
# An absolute floor alone answered a question whose top two intents sat at 0.21 and 0.20 — a
# coin toss presented as a fact. Every answer must be clear of the runner-up.
# ===================================================================================
check("the margin is a real constraint, not zero", MIN_MARGIN > 0.05, str(MIN_MARGIN))
tie_violations = []
for text, _ in REAL:
    p = MODEL.predict(text)
    if p.intent is None or len(p.ranked) < 2:
        continue
    if p.ranked[0][1] - p.ranked[1][1] < MIN_MARGIN:
        tie_violations.append(f"{text}: {p.ranked[0]} vs {p.ranked[1]}")
check("no answer is within the margin of its runner-up", not tie_violations,
      "; ".join(tie_violations[:3]))


# ===================================================================================
print("\n[4] THE CATALOGUE IT KNOWS IS THE CATALOGUE IT ANSWERS FOR")
# A model cannot choose an intent it has never seen, and would route every question about one
# somewhere else. When the caller's catalogue is not covered the service uses the untrained
# matcher instead — so `covers` is the switch, and it must be strict.
# ===================================================================================
check("it covers its own intents", MODEL.covers(list(MODEL.intents)))
check("it covers a subset", MODEL.covers([MODEL.intents[0]]))
check("it does NOT cover an unknown intent",
      not MODEL.covers([MODEL.intents[0], "hr.payroll-run"]))
check("it does not cover an empty catalogue", not MODEL.covers([]))
check("every id it knows is a real .NET intent id",
      all("." in i for i in MODEL.intents), str(MODEL.intents))

# A permission-filtered request may carry only a subset of the full model catalogue. The model
# must neither emit an unavailable intent nor reveal it in its ranked suggestions.
scoped = MODEL.predict("كام رصيد مياه معدنية", ["accounting.cash-balance"])
check("a filtered catalogue never receives a hidden intent",
      scoped.intent is None or scoped.intent == "accounting.cash-balance", str(scoped.intent))
check("hidden intents are absent from scoped rankings",
      all(intent == "accounting.cash-balance" for intent, _ in scoped.ranked), str(scoped.ranked))


# ===================================================================================
print("\n[5] DEGRADATION — nonsense must not raise, and must not be answered")
# ===================================================================================
for junk in ["", "   ", "؟؟؟", "...", "asdfghjkl", "1234567890", "ـــ", "?!:;"]:
    try:
        p = MODEL.predict(junk)
        check(f"junk is refused rather than answered: {junk!r}", p.intent is None,
              f"got {p.intent}")
    except Exception as exc:                                   # noqa: BLE001
        check(f"junk does not raise: {junk!r}", False, repr(exc))

long_q = "رصيد " * 300
try:
    MODEL.predict(long_q)
    check("a very long question is survivable", True)
except Exception as exc:                                       # noqa: BLE001
    check("a very long question is survivable", False, repr(exc))

check("a refusal still ranks the real intents, so a caller can offer",
      len(MODEL.predict("ما هو الطقس اليوم").ranked) == len(MODEL.intents))
check("the refusal class never appears in the ranking",
      all(k != OUT_OF_SCOPE for k, _ in MODEL.predict("ما هو الطقس اليوم").ranked))


print("\n" + "=" * 62)
if FAILURES:
    print(f"FAILED: {len(FAILURES)}")
    for f in FAILURES:
        print(f"   - {f}")
    sys.exit(1)
print("ALL CHECKS PASSED")
