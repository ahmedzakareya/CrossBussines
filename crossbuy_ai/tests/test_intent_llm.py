"""Tests for the tail engine — the local language model.

Run:  python -I tests/test_intent_llm.py      (from crossbuy_ai/)

WHAT IS TESTED HERE AND WHAT IS NOT. Generation is not deterministic enough to pin an answer, and
a test that forbids an answer from moving forbids a better prompt. What IS pinned is the machinery
that makes a generative model safe behind a closed catalogue — the grammar, the catalogue bound,
the refusal path — and the one hygiene rule that protects every measurement in this directory.

THE CONTAMINATION GUARD IS THE MOST IMPORTANT CHECK IN THIS FILE. Adding few-shot examples took
the model from 21/27 with a leak to 25/27 with none — and the first version of those examples was
six questions copied out of the benchmark. That would have turned the only honest measurement into
a memory test and reported it as a four-point gain. It was caught by looking; it is caught from
now on by this.
"""
from __future__ import annotations

import os
import re
import sys

sys.stdout.reconfigure(encoding="utf-8")
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))
sys.path.insert(0, HERE)

from app.ml import intent_llm  # noqa: E402
from app.ml.intent_llm import _grammar_for, find_model  # noqa: E402
from real_questions import REAL  # noqa: E402

FAILURES: list[str] = []


def check(name: str, cond: bool, detail: str = "") -> None:
    if cond:
        print(f"  PASS  {name}")
    else:
        print(f"  FAIL  {name}  {detail}")
        FAILURES.append(name)


# ===================================================================================
print("\n[1] NO FEW-SHOT EXAMPLE MAY APPEAR IN THE BENCHMARK")
# ===================================================================================
source = open(os.path.join(os.path.dirname(HERE), "app", "ml", "intent_llm.py"),
              encoding="utf-8").read()
shots = [q.strip() for q in re.findall(r"Question: (.+)\nAnswer:", source)
         if "{question}" not in q]
bench = {q.strip().rstrip("؟?") for q, _ in REAL}
overlap = [q for q in shots if q.rstrip("؟?") in bench]
check(f"the prompt carries few-shot examples at all ({len(shots)})", len(shots) >= 4)
check("none of them is a benchmark question", not overlap, "; ".join(overlap))
check("the refusal answer is demonstrated, not only described",
      any(a.strip() == "none" for a in re.findall(r"Answer: (.+)", source)))


# ===================================================================================
print("\n[2] THE GRAMMAR IS THE CONSTRAINT, NOT THE PROMPT")
# A prompt instruction is a request; a grammar is a constraint. This is what stops a generative
# model returning prose, an invented id, or whatever a malicious question told it to say.
# ===================================================================================
g = _grammar_for(["inventory.item-stock", "accounting.cash-balance"])
check("the grammar admits each id", '"inventory.item-stock"' in g and '"accounting.cash-balance"' in g)
check("the grammar admits none", '"none"' in g)
check("the grammar has a single root rule", g.count("root ::=") == 1, g)
check("an id outside the list cannot be spelled by the grammar", "hr.payroll" not in g)

# Built per request, so an intent added in .NET is admitted immediately. A grammar baked at build
# time silently forbids every new question.
g2 = _grammar_for(["a.b", "c.d", "e.f"])
check("the grammar is rebuilt from whatever ids arrive", g2.count('"') == 8, g2)
check("quotes in an id cannot break out of the grammar",
      '\\"' in _grammar_for(['we"ird']), _grammar_for(['we"ird']))


# ===================================================================================
print("\n[3] NO WEIGHTS IS A NORMAL CHECKOUT, NOT AN ERROR")
# The artifact is multiple gigabytes and is not in source control. The service must answer without
# it, through the classifier and the matcher, rather than failing to start.
# ===================================================================================
model_path = find_model()
print(f"    weights: {os.path.basename(model_path) if model_path else 'none present'}")
intent_llm.reset_cache()
real_dir = intent_llm.MODELS_DIR
try:
    intent_llm.MODELS_DIR = os.path.join(HERE, "__no_such_dir__")
    intent_llm.reset_cache()
    check("no weights yields no engine rather than an exception", intent_llm.engine() is None)
finally:
    intent_llm.MODELS_DIR = real_dir
    intent_llm.reset_cache()


# ===================================================================================
print("\n[4] THE ENGINE, IF THE WEIGHTS ARE HERE")
# One question only. Loading 2.3 GB and generating is slow, and a test suite that takes a minute
# stops being run — which costs more than the coverage it buys.
# ===================================================================================
if not model_path:
    print("    skipped: no weights present")
else:
    eng = intent_llm.engine()
    check("an engine is built when weights are present", eng is not None)
    if eng is not None:
        check("it names the weights it loaded", "Qwen" in eng.name or ".gguf" in eng.name, eng.name)
        catalogue = [
            ("inventory.item-stock", "quantity of an item on hand | e.g. كام رصيد مياه معدنية"),
            ("accounting.cash-balance", "cash and bank balances | e.g. كام رصيد النقدية"),
        ]
        # NOT READY IS AN IMMEDIATE NO. Before the weights are loaded the engine must decline at
        # once rather than queue behind the load — a 190-second request was exactly that queue.
        before = eng.choose("فيه كام علبة عصير", catalogue)
        check("an unwarmed engine declines instantly rather than loading", not before.ran)

        check("warm() loads the weights and proves a completion", eng.warm())

        out = eng.choose("فيه كام علبة عصير", catalogue)
        check("it runs once warm", out.ran, out.raw)
        check("whatever it said is inside the catalogue or none",
              out.intent is None or out.intent in [i for i, _ in catalogue], str(out.intent))

        empty = eng.choose("", catalogue)
        check("an empty question does not reach the model", not empty.ran)
        check("an empty catalogue does not reach the model", not eng.choose("x", []).ran)


print("\n" + "=" * 62)
if FAILURES:
    print(f"FAILED: {len(FAILURES)}")
    for f in FAILURES:
        print(f"   - {f}")
    sys.exit(1)
print("ALL CHECKS PASSED")
