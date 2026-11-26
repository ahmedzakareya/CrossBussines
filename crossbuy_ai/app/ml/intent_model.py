"""The intent model — trained here, owned here, no download and no provider.

WHY NOT A LANGUAGE MODEL. The task is closed: pick one of seven questions, or say none of them.
A general model would spend a billion parameters and several seconds rediscovering a boundary that
a classifier learns exactly, in milliseconds, from data we can generate. The language model earns
its keep where the output is open — writing, reasoning, summarising. Choosing from seven is not
that, and paying LLM latency for it would make the chat box feel broken.

WHAT IT IS. Two feature views over the question, joined:

  * CHARACTER n-grams (2–5), which is where the Arabic lives. A prefix, a suffix and a typo all
    mean "nearly the same string", and n-grams measure that directly — which is why the و- and
    -ه rules written by hand are not needed here.
  * WORD n-grams (1–2), which is where the discriminating phrase lives. "قيمة مخزون" and "مخزون"
    are different questions, and only a word view sees the pair as one thing.

Then multinomial logistic regression, which gives a probability rather than a distance. That
matters: the whole safety property is a floor, and a floor needs a number that means something.

THE EIGHTH CLASS. `none` is a trained label, not a threshold trick. A seven-class model always
picks one of seven; this one can answer "not any of them" with the same machinery it uses to
answer anything else, and it was shown look-alike business questions — best supplier, dormant
items, profit margin — precisely so that refusing them is learned rather than hoped for.

CALIBRATION IS NOT GUESSWORK. The floor is chosen on held-out data by the rule stated in
`choose_floor`, not picked because it looked round.
"""
from __future__ import annotations

import os
import threading
from dataclasses import dataclass

from sklearn.feature_extraction.text import TfidfVectorizer
from sklearn.linear_model import LogisticRegression
from sklearn.pipeline import FeatureUnion, Pipeline

from app.ml.intent_data import INTENT_IDS, OUT_OF_SCOPE
from app.ml.understand import normalise

ARTIFACT = os.path.join(os.path.dirname(os.path.dirname(os.path.dirname(
    os.path.abspath(__file__)))), "models", "intent.joblib")


@dataclass(frozen=True)
class Prediction:
    intent: str | None          # None when the model chose `none`, or fell below the floor
    confidence: float
    ranked: tuple[tuple[str, float], ...]
    engine: str = "intent-model-v1"


#: HOW FAR THE WINNER MUST BE AHEAD.
#:
#: An absolute floor alone is the wrong guard. With eight classes and balanced weights the
#: probabilities spread, so the floor chosen on held-out data came out at 0.20 — which answers
#: a question whose top two intents sit at 0.21 and 0.20. That is a coin toss reported as an
#: answer, and it is exactly the failure the keyword matcher had when two intents tied on one
#: hit each.
#:
#: So the winner must also be clear of the runner-up. A near-tie is not a weak answer; it is a
#: DIFFERENT KIND of result — "it is one of these two, you choose" — and it belongs in the offer
#: band with the near misses rather than being resolved by the third decimal place.
MIN_MARGIN = 0.12


def build() -> Pipeline:
    """The untrained pipeline. Separate from training so the test can build one cheaply."""
    return Pipeline([
        ("features", FeatureUnion([
            ("chars", TfidfVectorizer(analyzer="char_wb", ngram_range=(2, 5),
                                      sublinear_tf=True, min_df=2, max_features=120_000)),
            ("words", TfidfVectorizer(analyzer="word", ngram_range=(1, 2),
                                      sublinear_tf=True, min_df=2)),
        ])),
        # Multinomial by default in this sklearn; C left modest because the corpus is generated
        # and a hard-fitting model would memorise the templates rather than their shape.
        ("clf", LogisticRegression(max_iter=2000, C=4.0, class_weight="balanced")),
    ])


def train(texts: list[str], labels: list[str]) -> Pipeline:
    model = build()
    model.fit([normalise(t) for t in texts], labels)
    return model


def choose_floor(model: Pipeline, texts: list[str], labels: list[str],
                 target_precision: float = 0.98) -> float:
    """The lowest floor at which answering is right `target_precision` of the time.

    STATED AS A RULE, NOT A NUMBER. An assistant that answers the wrong question confidently is
    worse than one that asks you to rephrase, so the floor is set by how often answering is
    CORRECT, not by how often it answers at all. Raising the target makes it quieter and righter;
    lowering it makes it chattier and wronger. That is the only trade here, and it is the owner's
    to make, so it is a parameter rather than a constant buried in the code.
    """
    probs = model.predict_proba([normalise(t) for t in texts])
    classes = list(model.named_steps["clf"].classes_)

    # The margin is applied here too, or the floor is calibrated against a decision rule that is
    # not the one the model will actually use -- and a floor chosen for a different rule is not a
    # calibration, it is a coincidence.
    best = 0.95
    for floor in [i / 100 for i in range(10, 96)]:
        answered = right = 0
        for row, truth in zip(probs, labels):
            order = sorted(range(len(row)), key=lambda k: -row[k])
            top = order[0]
            runner_up = row[order[1]] if len(order) > 1 else 0.0
            if (classes[top] == OUT_OF_SCOPE or row[top] < floor
                    or (row[top] - runner_up) < MIN_MARGIN):
                continue                       # refused: not counted either way
            answered += 1
            if classes[top] == truth:
                right += 1
        if answered and right / answered >= target_precision:
            best = floor
            break
    return best




class IntentModel:
    """Loaded once, asked many times."""

    def __init__(self, pipeline: Pipeline, floor: float, intents: tuple[str, ...],
                 margin: float = MIN_MARGIN):
        self.pipeline = pipeline
        self.floor = floor
        self.margin = margin
        self.intents = intents
        self._classes = list(pipeline.named_steps["clf"].classes_)

    def covers(self, requested: list[str]) -> bool:
        """Only answer for a catalogue this model was actually trained on.

        A model that has never seen an intent cannot choose it, and would quietly route every
        question about it somewhere else. When the caller's catalogue is not covered, the service
        falls back to the untrained nearest-neighbour, which adapts to any catalogue sent at
        runtime. Neither is a guess; they have different competences.
        """
        known = set(self.intents)
        return bool(requested) and all(r in known for r in requested)

    def predict(self, question: str, allowed_intents: list[str] | tuple[str, ...] | None = None) -> Prediction:
        q = normalise(question)
        if not q:
            return Prediction(None, 0.0, ())

        allowed = set(self.intents if allowed_intents is None else allowed_intents)
        row = self.pipeline.predict_proba([q])[0]
        pairs = sorted(zip(self._classes, (float(p) for p in row)),
                       key=lambda kv: (-kv[1], kv[0]))
        # The caller may have sent only its permission-filtered catalogue. Never expose hidden
        # capabilities in the ranking, and never redirect a hidden top prediction to a weaker
        # visible intent: that would turn authorization filtering into a wrong-answer generator.
        ranked = tuple((k, round(v, 4)) for k, v in pairs
                       if k != OUT_OF_SCOPE and k in allowed)

        top_class, top_p = pairs[0]
        runner_up = pairs[1][1] if len(pairs) > 1 else 0.0

        if (top_class == OUT_OF_SCOPE or top_class not in allowed
                or top_p < self.floor or (top_p - runner_up) < self.margin):
            # The confidence reported on a refusal is the best REAL intent's score, because that
            # is the number a caller can act on: it decides whether to offer the near misses.
            return Prediction(None, ranked[0][1] if ranked else 0.0, ranked)
        return Prediction(top_class, round(top_p, 4), ranked)


def save(model: Pipeline, floor: float, path: str = ARTIFACT) -> str:
    """Publish a complete artifact atomically so serving never reads a partial retrain."""
    import joblib
    import tempfile

    target_dir = os.path.dirname(os.path.abspath(path))
    os.makedirs(target_dir, exist_ok=True)
    fd, candidate = tempfile.mkstemp(prefix="intent-", suffix=".joblib.tmp", dir=target_dir)
    os.close(fd)
    try:
        joblib.dump({"pipeline": model, "floor": floor, "intents": INTENT_IDS}, candidate)
        os.replace(candidate, path)
    finally:
        if os.path.exists(candidate):
            os.unlink(candidate)
    return path


_loaded: IntentModel | None = None
_tried = False
_loaded_stamp: tuple[float, int] | None = None
_load_lock = threading.Lock()


def _stamp(path: str) -> tuple[float, int] | None:
    """The artifact's identity: modification time and size. None when there is no artifact."""
    try:
        st = os.stat(path)
        return (st.st_mtime, st.st_size)
    except OSError:
        return None


def load(path: str = ARTIFACT) -> IntentModel | None:
    """None when no model has been trained yet — the caller must cope, not crash.

    The artifact is a build output, not source. A checkout without one is normal, and the service
    must still answer; it falls back to the nearest-neighbour matcher, which needs no training.

    IT RELOADS WHEN THE ARTIFACT CHANGES, AND THAT IS THE POINT OF THE stat.
    ----------------------------------------------------------------------
    This used to load once and hold the result for the life of the process, so a retrain only took
    effect when somebody remembered to restart uvicorn. Forgetting had no error and no symptom
    anybody would connect to it: the new intent is absent from the loaded model, `covers()` goes
    false for the catalogue the .NET side sends, EVERY question falls through to the untrained
    nearest-neighbour matcher, and the whole assistant quietly gets worse. On 2026-11-10 that cost
    hours — "اعلي الاصناف بيعا" returned the CATEGORIES list, and the code was right the whole time.
    `reset_cache()` existed for exactly this and nothing ever called it.

    One stat per call, measured in microseconds against a prediction that takes milliseconds. The
    mtime AND the size are compared because a rewrite within the filesystem's timestamp resolution
    is possible, and the two together make that far less likely than either alone.
    """
    global _loaded, _tried, _loaded_stamp
    stamp = _stamp(path)

    if _loaded is not None and stamp is not None and stamp == _loaded_stamp:
        return _loaded
    if stamp is None:
        # No artifact. Remember it so the service is not stat-ing a missing file in a hot loop,
        # but a stamp appearing later still gets picked up on the next call.
        _tried = True
        return _loaded

    with _load_lock:
        # Re-check inside the lock: two requests can arrive together and only one should load.
        if _loaded is not None and stamp == _loaded_stamp:
            return _loaded
        try:
            import joblib
            blob = joblib.load(path)
            _loaded = IntentModel(blob["pipeline"], float(blob["floor"]), tuple(blob["intents"]))
            _loaded_stamp = stamp
            _tried = True
            return _loaded
        except Exception:
            # A HALF-WRITTEN ARTIFACT IS THE EXPECTED FAILURE HERE, because a retrain writes it
            # while the service is serving. Keep the model already in memory rather than dropping
            # to no model at all; the next call sees the finished file and loads it.
            return _loaded


def reset_cache() -> None:
    """For tests. A retrain no longer needs this — `load` notices the artifact changing."""
    global _loaded, _tried, _loaded_stamp
    _loaded = None
    _tried = False
    _loaded_stamp = None
