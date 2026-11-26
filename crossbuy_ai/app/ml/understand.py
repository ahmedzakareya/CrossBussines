"""Which question is this? — the understanding layer.

WHY THIS EXISTS AT ALL
----------------------
The assistant chose its intent with a keyword matcher: a question matched an intent when it
literally contained one of that intent's phrases. Four separate wordings defeated it in a single
afternoon, each fixed by hand afterwards:

    "مياه معدنية"      the stored name folded differently from the query
    "برجر داج"         one keystroke from "برجر دجاج"
    "والتكلفة"         a conjunction glued to the front of a keyword
    "اخر حركاته"       a pronoun glued to the back of one

Every one of those is the same fact — *the word is nearly, but not exactly, a string in the
list* — and a matcher built on `contains` cannot see "nearly". Each fix was a hand-written rule
for one shape of nearly, and the list of shapes is not finite: Arabic also glues ال، ب، ك، ل، ف
on the front and ـين ـات ـية on the back, and people misspell everything.

WHAT REPLACES IT
----------------
Character n-grams. A word with a prefix, a suffix, or a typo shares nearly all of its 2–4
character windows with the word it came from, so similarity measured over those windows sees
"nearly" directly, with no rule per shape. The clitic rules written by hand are a special case of
this, which is why they are only needed while the matcher compares whole tokens.

WHY NEAREST-NEIGHBOUR AND NOT A TRAINED CLASSIFIER
--------------------------------------------------
There are seven intents and a handful of phrasings each. A fitted classifier on that much data
learns the phrasings, not the language, and — worse — reports a confident probability while doing
it. Cosine similarity to the nearest known phrasing is a number that means something: "this is how
close you came to something I was actually told about". A confidence that can be read is worth
more here than one that is merely higher.

WHAT THIS DOES NOT DO
---------------------
It does not understand language and it is not a language model. It cannot answer a question that
is not in the catalogue, cannot combine two intents, and cannot work out that "ايه اللي بيخلص
بسرعة" means reorder. It picks the nearest of a closed list, says how near, and refuses below a
floor. Everything downstream — the company, the authority, the rows — is decided in .NET and is
not affected by what this returns, because all it can return is an id that was sent to it.
"""
from __future__ import annotations

import hashlib
from dataclasses import dataclass
from typing import Iterable, Sequence

from sklearn.feature_extraction.text import TfidfVectorizer
from sklearn.metrics.pairwise import cosine_similarity
from app.ml.text_normalization import normalise

# ---------------------------------------------------------------------------------------------
# Confidence bands.
#
# ANSWER:  near enough to a known phrasing that acting on it is honest.
# OFFER:   recognisably about something, but not enough to act — the caller offers it instead.
# below:   nothing. Refusing is a correct answer, and is always better than a confident wrong one.
#
# The numbers are cosine similarities over character n-grams, so they are comparable between
# questions of different lengths. They were set by running the phrasings that actually defeated
# the keyword matcher and the ones that should NOT match (see tests/test_understand.py), and the
# gap between those two groups is wide — around 0.6 against around 0.15 — so the exact cut is not
# delicate.
# ---------------------------------------------------------------------------------------------
ANSWER_FLOOR = 0.47
OFFER_FLOOR = 0.18

#: THE BAR RISES WITH THE CATALOGUE, and that is not a tuning detail.
#:
#: 0.42 was enough at seven intents and stopped being enough at twelve. This matcher has no "none"
#: class — it can only pick the nearest thing it was told about — so the floor is its only defence,
#: and every intent added is another chance for something unrelated to drift above it. The moment a
#: customer-ranking intent existed, "مين أحسن مورد عندنا؟" scored 0.4386 against it, because both
#: are a "مين" question about a trading partner.
#:
#: A MARGIN OVER THE RUNNER-UP WAS TRIED HERE AND WAS WRONG. Unlike the trained model, this scores
#: similarity rather than probability, and two intents that legitimately share vocabulary — stock
#: and stock value — sit close together while one of them is still the right answer. Requiring a
#: gap refused "كام رصيد لابتوب" and let the supplier question through, which is both failures at
#: once. Raising the bar is the lever that matches what is actually happening.

@dataclass(frozen=True)
class Intent:
    """One answerable question, as the .NET catalogue describes it."""

    id: str
    phrasings: tuple[str, ...]

    def examples(self) -> list[str]:
        return [p for p in (normalise(p) for p in self.phrasings) if p]


@dataclass(frozen=True)
class Match:
    intent: str
    score: float


@dataclass(frozen=True)
class Understanding:
    """What was understood, and how sure.

    `intent` is None whenever nothing cleared ANSWER_FLOOR. `ranked` is always populated, so a
    caller can offer the near misses rather than dead-end; that is the whole difference between
    "I did not understand" and "did you mean one of these".
    """

    intent: str | None
    confidence: float
    ranked: tuple[Match, ...]
    engine: str = "tfidf-char-3-5"

    @property
    def offerable(self) -> tuple[Match, ...]:
        return tuple(m for m in self.ranked if m.score >= OFFER_FLOOR)


def fingerprint(intents: Sequence[Intent]) -> str:
    """Identify a catalogue by its CONTENT, so a changed phrasing invalidates the cached model.

    Keying the cache on the intent ids alone would serve a stale model for ever after someone
    adds a phrasing — the failure being that the new wording silently does not work, which is
    exactly the bug this layer exists to remove.
    """
    h = hashlib.sha256()
    for i in sorted(intents, key=lambda x: x.id):
        h.update(i.id.encode("utf-8"))
        for p in sorted(i.phrasings):
            h.update(b"\x1f")
            h.update(p.encode("utf-8"))
        h.update(b"\x1e")
    return h.hexdigest()[:16]


class Understander:
    """Fitted over one catalogue. Cheap to build (milliseconds) and cached by fingerprint."""

    def __init__(self, intents: Sequence[Intent]) -> None:
        self.intents = tuple(intents)
        self.fingerprint = fingerprint(intents)

        self._owner: list[str] = []
        docs: list[str] = []
        for intent in self.intents:
            for example in intent.examples():
                docs.append(example)
                self._owner.append(intent.id)

        self._empty = not docs
        if self._empty:
            self._vec = None
            self._matrix = None
            return

        # CHARACTER n-grams, not words. A word match would be back where we started: "حركاته" is
        # not the word "حركات", but it contains four of its five 3-grams.
        #
        # sublinear_tf because a phrasing that repeats a word is not twice as much about it, and
        # min_df=1 because with this little data every n-gram is evidence.
        self._vec = TfidfVectorizer(
            analyzer="char_wb",
            ngram_range=(3, 5),
            sublinear_tf=True,
            min_df=1,
        )
        self._matrix = self._vec.fit_transform(docs)

    def understand(self, question: str) -> Understanding:
        q = normalise(question)
        if self._empty or not q:
            return Understanding(intent=None, confidence=0.0, ranked=())

        sims = cosine_similarity(self._vec.transform([q]), self._matrix)[0]

        # BEST PHRASING PER INTENT, not the average of its phrasings. An intent that lists ten
        # wordings would otherwise be punished for being thorough: nine irrelevant ones drag its
        # mean down while the tenth is an exact hit. What matters is whether ANY known way of
        # asking this is close to what was typed.
        best: dict[str, float] = {}
        for owner, score in zip(self._owner, sims):
            if score > best.get(owner, -1.0):
                best[owner] = float(score)

        ranked = tuple(
            Match(intent=k, score=round(v, 4))
            for k, v in sorted(best.items(), key=lambda kv: (-kv[1], kv[0]))
        )
        top = ranked[0] if ranked else None
        if top is None or top.score < ANSWER_FLOOR:
            return Understanding(
                intent=None,
                confidence=round(top.score, 4) if top else 0.0,
                ranked=ranked,
            )
        return Understanding(intent=top.intent, confidence=top.score, ranked=ranked)


# ---------------------------------------------------------------------------------------------
# A small cache. The catalogue is the same on every request and changes only when code does, so
# refitting per request would be pure waste; but it is keyed by CONTENT, so an edit takes effect
# without a restart.
# ---------------------------------------------------------------------------------------------
_cache: dict[str, Understander] = {}
_CACHE_MAX = 8


def understander_for(intents: Iterable[Intent]) -> Understander:
    intents = tuple(intents)
    key = fingerprint(intents)
    hit = _cache.get(key)
    if hit is not None:
        return hit
    built = Understander(intents)
    if len(_cache) >= _CACHE_MAX:
        _cache.clear()
    _cache[key] = built
    return built
