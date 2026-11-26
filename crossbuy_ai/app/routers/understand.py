"""Question understanding router — which of the catalogue's questions is this?

TWO ENGINES, CHOSEN BY COMPETENCE RATHER THAN BY PREFERENCE.

  * THE TRAINED MODEL (app/ml/intent_model.py) is used whenever the catalogue the caller sent is
    one it was trained on. It is better at every question it has seen the shape of, it can say
    "none of these" as a learned answer rather than as a threshold trick, and it answers in
    about five milliseconds.

  * THE NEAREST-NEIGHBOUR MATCHER (app/ml/understand.py) needs no training and adapts to whatever
    catalogue arrives at runtime. It covers the cases the trained model structurally cannot: a
    new intent added in .NET, a catalogue filtered down to one question by somebody's
    permissions, a checkout with no trained artifact in it.

This is not a fallback chain in the usual sense, where the second thing is a worse version of the
first. They have different competences, and the choice is made on a fact — does this model know
these intents — rather than on a confidence score, which neither could compare across engines
anyway.

No LLM and no API key in either path.
"""
import asyncio

from fastapi import APIRouter, Depends

from app.config import get_settings
from app.ml.intent_data import FRENCH_NEGATIVES, FRENCH_TEMPLATES, NEGATIVES, OUT_OF_SCOPE
from app.ml.fallback_refusals import FALLBACK_REFUSALS
from app.ml.intent_llm import engine as llm_engine
from app.ml.intent_model import load as load_intent_model
from app.ml.understand import ANSWER_FLOOR, OFFER_FLOOR, Intent, understander_for
from app.schemas.understand import UnderstandRequest, UnderstandResponse
from app.security import require_secret

router = APIRouter(prefix="/understand", tags=["understand"], dependencies=[Depends(require_secret)])
_FRENCH_OUT_OF_SCOPE = "__crossbuy.out-of-scope.fr"


def _is_french_question(text: str) -> bool:
    """Conservative, dependency-free detector for French requests.

    The trained artifact currently contains Arabic and English examples. Sending French through
    it would produce a confident-looking result from unrelated character n-grams. French is a
    supported UI culture, so route clear French wording through the runtime catalogue matcher,
    which receives French examples below. Ambiguous short words stay on the existing path.
    """
    import re

    words = set(re.findall(r"[\wÀ-ÿ]+", (text or "").casefold()))
    markers = {
        "combien", "quel", "quelle", "quels", "quelles", "estce", "avonsnous",
        "puisje", "montre", "liste", "donne", "achats", "ventes", "factures",
        "créances", "dépenses", "employés", "médecins", "médicaments", "entrepôts",
        "trésorerie", "mouvements", "aujourd", "hui", "rendez", "vous", "écritures",
        "articles", "fournisseur", "fournisseurs", "impayées", "disponibles",
    }
    common = {"de", "du", "des", "le", "la", "les", "un", "une", "et", "par",
              "pour", "avec", "dans", "sur", "est", "sont", "mois", "notre", "nos"}
    accented = any(ch in (text or "").casefold() for ch in "àâçéèêëîïôùûüÿœ")
    return accented or bool(words & markers) or len(words & common) >= 2


@router.post("", response_model=UnderstandResponse)
async def understand(body: UnderstandRequest) -> UnderstandResponse:
    requested = [i.id for i in body.intents]

    # French is registered in the product, but the persisted classifier was trained on Arabic
    # and English only. Do not let that model infer across unrelated scripts/vocabulary. Build a
    # permission-filtered, request-local French catalogue and use the same bounded nearest-neighbour
    # matcher used for newly added/unknown catalogues. This selects only an offered intent; it never
    # composes SQL or accesses business data.
    if _is_french_question(body.question):
        french_intents = tuple(
            Intent(
                id=i.id,
                phrasings=tuple(dict.fromkeys(
                    [p for p in i.phrasings if p and p.strip()]
                    + FRENCH_TEMPLATES.get(i.id, [])
                )),
            )
            for i in body.intents
        ) + (Intent(id=_FRENCH_OUT_OF_SCOPE, phrasings=tuple(FRENCH_NEGATIVES)),)
        result = understander_for(french_intents).understand(body.question)
        ranked = [
            {"intent": m.intent, "score": m.score}
            for m in result.ranked if m.intent != _FRENCH_OUT_OF_SCOPE
        ]
        if result.intent is not None and result.intent != _FRENCH_OUT_OF_SCOPE:
            return UnderstandResponse(
                intent=result.intent, confidence=result.confidence, ranked=ranked,
                engine="intent-knn-fr-v1", answerFloor=ANSWER_FLOOR, offerFloor=OFFER_FLOOR)
        return UnderstandResponse(
            intent=None, confidence=result.confidence, ranked=ranked,
            engine="intent-knn-fr-v1", answerFloor=ANSWER_FLOOR, offerFloor=OFFER_FLOOR)

    # ---- 1. the trained classifier, when it knows this catalogue ------------------------------
    ranked: list[dict] = []
    confidence = 0.0
    engine_name = "none"
    floor = ANSWER_FLOOR

    model = load_intent_model()
    if model is not None and model.covers(requested):
        p = model.predict(body.question, requested)
        ranked = [{"intent": k, "score": v} for k, v in p.ranked]
        confidence, engine_name, floor = p.confidence, p.engine, model.floor
        if p.intent is not None:
            return UnderstandResponse(
                intent=p.intent, confidence=confidence, ranked=ranked,
                # Reported as the engine's OWN decision rule, so a caller reading the numbers is
                # reading the thresholds that actually applied rather than another engine's.
                engine=engine_name, answerFloor=floor, offerFloor=OFFER_FLOOR)
    else:
        # ---- 2. the untrained matcher, which adapts to any catalogue sent at runtime ----------
        intents = tuple(
            Intent(id=i.id, phrasings=tuple(p for p in i.phrasings if p and p.strip()))
            for i in body.intents
        )
        # The dynamic fallback must learn a refusal boundary too. Nearest-neighbour without a
        # negative class necessarily assigns an unseen business question to something nearby.
        known_phrasings = {p.casefold().strip() for intent in intents for p in intent.phrasings}
        refusal = Intent(id=OUT_OF_SCOPE, phrasings=tuple(
            p for p in (*NEGATIVES, *FALLBACK_REFUSALS)
            if p.casefold().strip() not in known_phrasings
        ))
        result = understander_for(intents + (refusal,)).understand(body.question)
        ranked = [{"intent": m.intent, "score": m.score} for m in result.ranked
                  if m.intent != OUT_OF_SCOPE]
        confidence, engine_name = result.confidence, result.engine
        if result.intent is not None and result.intent != OUT_OF_SCOPE:
            return UnderstandResponse(
                intent=result.intent, confidence=confidence, ranked=ranked,
                engine=engine_name, answerFloor=ANSWER_FLOOR, offerFloor=OFFER_FLOOR)

    # ---- 3. THE TAIL. Both cheap engines declined, so this question is one nobody anticipated -
    #
    # Only reached on a refusal, which is what makes a second or two acceptable: it is spent
    # instead of failing, never instead of answering quickly. Off unless switched on.
    if get_settings().intent_llm_enabled:
        eng = llm_engine()
        if eng is not None:
            # THE SAME CATALOGUE SHAPE THE EVALUATION MEASURED. .NET sends keywords first and
            # then example-ar, example-en, description-ar, description-en; the model is given the
            # English description plus one real Arabic phrasing, which is what took it from 21/27
            # with a leak to 25/27 with none. Measuring one prompt and shipping another would make
            # the measurement a story.
            catalogue = []
            for i in body.intents:
                english = i.phrasings[-1] if i.phrasings else i.id
                arabic = next((p for p in reversed(i.phrasings)
                               if any("؀" <= c <= "ۿ" for c in p)), "")
                catalogue.append((i.id, english + (" | e.g. " + arabic if arabic else "")))
            # Run the blocking model off the event loop so one slow question does not stall the
            # service for every other caller.
            choice = await asyncio.to_thread(eng.choose, body.question, catalogue)
            if choice.ran and choice.intent:
                return UnderstandResponse(
                    intent=choice.intent,
                    # A generative model has no comparable probability, and inventing one would
                    # be worse than admitting there is none. The CALLER is told which engine
                    # answered and is expected to say so to the person reading the figures.
                    confidence=0.0,
                    ranked=ranked,
                    engine=choice.engine,
                    answerFloor=0.0,
                    offerFloor=OFFER_FLOOR,
                )

    # ---- nothing understood it. The ranking still travels, so a refusal can offer. ------------
    return UnderstandResponse(
        intent=None, confidence=confidence, ranked=ranked,
        engine=engine_name, answerFloor=floor, offerFloor=OFFER_FLOOR)
