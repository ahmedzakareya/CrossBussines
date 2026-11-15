"""Score the tail engine on the same hand-written questions as the classifier.

Run:  python -I tools/eval_intent_llm.py      (from crossbuy_ai/)

ONE BENCHMARK FOR EVERY ENGINE, or the comparison is a story rather than a measurement. This is
the same tests/real_questions.py set the trained model is scored on, read the same way, with the
same expensive mistake counted first.

The number that decides whether this engine ships is LEAKED — questions we have no handler for,
answered anyway. A generative model is far more willing than a classifier to find SOMETHING
plausible, and "مين أحسن مورد عندنا" is exactly the kind of question it will happily route to
item-stock. If that number is not zero or close to it, the engine is a liability at any accuracy,
because a confident answer to an unimplemented question is indistinguishable from a real one.
"""
from __future__ import annotations

import json
import os
import sys
import time

sys.stdout.reconfigure(encoding="utf-8")
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, ROOT)
sys.path.insert(0, os.path.join(ROOT, "tests"))

from app.ml.intent_data import OUT_OF_SCOPE  # noqa: E402
from app.ml.intent_llm import engine  # noqa: E402
from real_questions import REAL  # noqa: E402

with open(os.path.join(ROOT, "tests", "catalogue.json"), encoding="utf-8") as fh:
    CATALOGUE_JSON = json.load(fh)

# The description AND the Arabic example. Telling a 4B model what a category means in English
# while the questions arrive in Egyptian colloquial leaves it to bridge the two by itself; showing
# it one real Arabic phrasing per intent is the cheapest help there is.
CATALOGUE = [
    (
        intent,
        (body["desc"][1] if len(body["desc"]) > 1 else intent)
        + ((" | e.g. " + body["example"][0]) if body.get("example") else ""),
    )
    for intent, body in CATALOGUE_JSON.items()
]

if __name__ == "__main__":
    eng = engine()
    if eng is None:
        print("no local model found under crossbuy_ai/models/ — nothing to score")
        raise SystemExit(0)

    print(f"engine: {eng.name}")
    print(f"threads: {eng.threads}")
    print("loading weights (first call only) ...")
    t0 = time.time()
    eng.choose("كام رصيد مياه معدنية", CATALOGUE)
    print(f"  loaded + first answer in {time.time() - t0:.1f}s\n")

    answered = correct = wrong = leaked = refused_rightly = refused_wrongly = 0
    notes: list[str] = []
    times: list[float] = []

    for text, truth in REAL:
        t = time.time()
        choice = eng.choose(text, CATALOGUE)
        times.append(time.time() - t)
        got = choice.intent or OUT_OF_SCOPE

        if got == OUT_OF_SCOPE:
            if truth == OUT_OF_SCOPE:
                refused_rightly += 1
            else:
                refused_wrongly += 1
                notes.append(f"refused: {text}  (wanted {truth.split('.')[-1]})")
            continue
        answered += 1
        if truth == OUT_OF_SCOPE:
            leaked += 1
            notes.append(f"LEAKED:  {text}  -> {got.split('.')[-1]}")
        elif got == truth:
            correct += 1
        else:
            wrong += 1
            notes.append(f"wrong:   {text}  -> {got.split('.')[-1]} (wanted {truth.split('.')[-1]})")

    in_scope = sum(1 for _, t in REAL if t != OUT_OF_SCOPE)
    out_scope = len(REAL) - in_scope

    print(f"  answered correctly ....... {correct}/{in_scope}")
    print(f"  answered the WRONG one ... {wrong}")
    print(f"  answered one we cannot ... {leaked}/{out_scope}      <- the expensive mistake")
    print(f"  refused, rightly ......... {refused_rightly}/{out_scope}")
    print(f"  refused, wrongly ......... {refused_wrongly}")
    if answered:
        print(f"  precision when answering . {correct / answered:.3f}")
    times.sort()
    print(f"\n  latency: median {times[len(times) // 2]:.2f}s, "
          f"slowest {times[-1]:.2f}s over {len(times)} questions")
    for n in notes:
        print(f"    - {n}")
