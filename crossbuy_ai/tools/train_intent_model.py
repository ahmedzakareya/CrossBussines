"""Train the intent model and report honestly on what it learned.

Run:  python -I tools/train_intent_model.py      (from crossbuy_ai/)

TWO NUMBERS, BECAUSE NEITHER ALONE IS THE TRUTH.

  * The HELD-OUT SPLIT is generated data the model never saw. It measures whether the model
    learned the templates' SHAPE rather than their letters. It will look good, and a good number
    here on its own means very little — the test set was made by the same machine as the training
    set, so it shares every blind spot.

  * The REAL set is hand-written (tests/real_questions.py) in wordings the templates do not
    contain. It is small and therefore noisy, but it is the only measurement that can be
    surprised. This is the number to read.

AND A THIRD THING THAT MATTERS MORE THAN EITHER: how often the model answers the WRONG question.
A refusal costs a rephrase. A confident wrong answer costs trust, and is indistinguishable from a
right one to the person reading it. They are reported separately and never averaged together.
"""
from __future__ import annotations

import os
import sys
import time

sys.stdout.reconfigure(encoding="utf-8")
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from sklearn.model_selection import train_test_split  # noqa: E402

from app.ml.intent_data import NEGATIVES, OUT_OF_SCOPE, TEMPLATES, generate  # noqa: E402
from app.ml.intent_model import IntentModel, choose_floor, save, train  # noqa: E402
from app.ml.intent_data import INTENT_IDS  # noqa: E402

sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "tests"))
from real_questions import REAL  # noqa: E402


def report(model: IntentModel, cases: list[tuple[str, str]], title: str) -> dict:
    answered = correct = wrong = refused_wrongly = refused_rightly = leaked = 0
    misses: list[str] = []
    for text, truth in cases:
        p = model.predict(text)
        got = p.intent or OUT_OF_SCOPE
        if got == OUT_OF_SCOPE:
            if truth == OUT_OF_SCOPE:
                refused_rightly += 1
            else:
                refused_wrongly += 1
                misses.append(f"refused: {text}  (wanted {truth.split('.')[-1]})")
            continue
        answered += 1
        if truth == OUT_OF_SCOPE:
            # The expensive mistake: a question we cannot answer, answered anyway.
            leaked += 1
            misses.append(f"LEAKED:   {text}  -> {got.split('.')[-1]} @{p.confidence}")
        elif got == truth:
            correct += 1
        else:
            wrong += 1
            misses.append(f"wrong:    {text}  -> {got.split('.')[-1]} (wanted {truth.split('.')[-1]})")

    total = len(cases)
    precision = (correct / answered) if answered else 0.0
    print(f"\n  {title}  ({total} questions)")
    print(f"    answered correctly ....... {correct}")
    print(f"    answered the WRONG one ... {wrong}")
    print(f"    answered one we cannot ... {leaked}      <- the expensive mistake")
    print(f"    refused, rightly ......... {refused_rightly}")
    print(f"    refused, wrongly ......... {refused_wrongly}")
    print(f"    precision when answering . {precision:.3f}")
    for m in misses[:14]:
        print(f"      - {m}")
    return {"correct": correct, "wrong": wrong, "leaked": leaked,
            "refusedWrongly": refused_wrongly, "precision": precision, "answered": answered}


if __name__ == "__main__":
    print("generating corpus ...")
    texts, labels = generate()
    print(f"  {len(texts)} examples across {len(set(labels))} classes "
          f"({len(INTENT_IDS)} intents + the refusal class)")

    x_train, x_test, y_train, y_test = train_test_split(
        texts, labels, test_size=0.2, random_state=20261108, stratify=labels)

    print("training ...")
    t0 = time.time()
    pipeline = train(x_train, y_train)
    print(f"  fitted in {time.time() - t0:.1f}s")

    # The floor is chosen on held-out data by a stated rule, never by eye.
    floor = choose_floor(pipeline, x_test, y_test, target_precision=0.98)
    print(f"  answer floor chosen on held-out data: {floor:.2f}")

    model = IntentModel(pipeline, floor, INTENT_IDS)
    generated = report(model, list(zip(x_test, y_test)), "HELD-OUT GENERATED (same machine, shares its blind spots)")
    real = report(model, REAL, "HAND-WRITTEN REAL QUESTIONS (the number to read)")

    # AND THE SAME SET WITH THE OVERLAP REMOVED.
    #
    # A few benchmark questions are verbatim in the training corpus, deliberately: "والتكلفة؟" and
    # "اخر حركاته" are the wordings that actually failed on the owner's screen, and dropping them
    # from the benchmark would stop measuring the regression they exist to catch. Keeping them
    # inflates the score, because the model has seen them. Both numbers are therefore printed, and
    # the second one is the conservative read.
    from app.ml.understand import normalise
    trained = {normalise(t) for v in TEMPLATES.values() for t in v} | {normalise(n) for n in NEGATIVES}
    clean = [(q, t) for q, t in REAL if normalise(q) not in trained]
    overlap = len(REAL) - len(clean)
    if overlap:
        report(model, clean, f"SAME SET, MINUS THE {overlap} IT WAS TRAINED ON (the conservative read)")

    t0 = time.time()
    for text, _ in REAL:
        model.predict(text)
    per = (time.time() - t0) / max(1, len(REAL)) * 1000
    print(f"\n  latency: {per:.1f} ms per question")

    path = save(pipeline, floor)
    print(f"\n  saved -> {path}")

    # A model that leaks questions it cannot answer is not shippable, whatever its accuracy.
    if real["leaked"] > 2:
        print(f"\n  REFUSING TO BLESS THIS MODEL: it answered {real['leaked']} questions it has no "
              f"handler for. A confident wrong answer is worse than a refusal.")
        sys.exit(1)
