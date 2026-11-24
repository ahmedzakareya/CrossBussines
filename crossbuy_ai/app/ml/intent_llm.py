"""The tail engine — a local language model, for the question nobody anticipated.

WHY A THIRD ENGINE, AND WHY HERE IN THE ORDER.

The owner's point, and it is correct: "السؤال الطبيعي يكن غير متوقع". The trained classifier
handles variation in the SHAPE of a question it has seen — prefixes, suffixes, typos, word order,
register. What it cannot handle is a wording that shares no vocabulary with anything it was shown,
because there is nothing for its features to latch onto. No amount of generated data fixes that:
the whole premise of "unpredictable" is that the template does not exist.

A language model does handle it, and this is the one place in the stack where its cost is worth
paying — because it runs ONLY when the cheap engines have already declined. Its latency is
therefore never paid instead of a fast answer; it is paid instead of a failure. That is also why
it is last and not first: a 4-billion-parameter model is the wrong tool for "كام رصيد مياه معدنية",
which the classifier settles in five milliseconds and gets right.

WHY IT RUNS LOCALLY, AND WHY THAT IS NOT A COMPROMISE. A typed question is free-text business
content; the egress matrix permits that at an Internal destination only. A model on this machine
IS an Internal destination, so nothing needs signing and no question leaves the building. The
external path stays blocked by the same matrix, as it should.

THE OUTPUT IS CONSTRAINED BY A GRAMMAR, NOT BY A PROMPT. llama.cpp is given a GBNF grammar that
admits exactly the intent ids we sent plus "none". The model therefore CANNOT return an id that
does not exist, cannot return prose, cannot return JSON with an extra field, and cannot be talked
out of the format by anything written inside the question. A prompt instruction is a request; a
grammar is a constraint. This is what makes a generative model safe to put behind a closed
catalogue — and it is also why it is fast here, since the whole answer is a handful of tokens.

Everything downstream is unchanged: the id is still checked against the offered catalogue in .NET,
the company is still re-resolved, the module authority is still re-asked, the rows are still
re-scoped. The model chooses a question. It has never been able to reach an answer.
"""
from __future__ import annotations

import glob
import os
import threading
from dataclasses import dataclass

MODELS_DIR = os.path.join(
    os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))), "models")

#: How many tokens the answer may be. The grammar makes the answer one short id, so this only
#: needs to be large enough for the longest id plus a stop.
MAX_TOKENS = 24

#: Small on purpose. The prompt is the catalogue plus one question; anything larger would only
#: slow the first load and invite someone to start pasting rows into it.
CONTEXT = 2048

#: How much prompt cache to keep. Enough for the handful of distinct prefixes
#: this engine sends (one per catalogue shape, one per surface), and bounded so a
#: long-lived service cannot grow into the machine.
CACHE_BYTES = 256 * 1024 * 1024

#: How long a caller will wait for the one context to free up before declining.
#: Past this the answer is late enough that falling back is the better answer.
WAIT_SECONDS = 8.0


@dataclass(frozen=True)
class LlmChoice:
    intent: str | None
    engine: str
    ran: bool
    raw: str = ""


def find_model() -> str | None:
    """The first .gguf in models/. None when there is none, which is a normal checkout."""
    found = sorted(glob.glob(os.path.join(MODELS_DIR, "*.gguf")))
    return found[0] if found else None


def _grammar_for(ids: list[str]) -> str:
    """A GBNF grammar admitting exactly these ids, or "none".

    Built from the ids the CALLER sent, every time. A grammar baked at build time would go stale
    the moment .NET adds an intent, and a stale grammar silently forbids the new question.
    """
    def quote(s: str) -> str:
        return '"' + s.replace("\\", "\\\\").replace('"', '\\"') + '"'

    alternatives = " | ".join(quote(i) for i in ids + ["none"])
    return f"root ::= {alternatives}\n"


# SHOWN, NOT TOLD. The first version of this prompt described each intent in English and gave no
# examples, and the model scored 21/27 with two wrong answers and one leak. A description tells a
# 4B model what a category means in the abstract; an example in the language the question will
# arrive in shows it. The examples below include a REFUSAL, because "none" is the answer the model
# is least willing to give and most needs to see chosen.
_PROMPT = """You map a user's question to exactly one intent id from the list, or to "none".

Intents, each with an example of how someone asks it:
{catalogue}

Rules:
- Answer with one id from the list above, or none. Nothing else.
- Choose none if the question is small talk, is about another part of the business, or is a
  business question that no intent in the list covers. Choosing none is correct and expected.
- The question may be in Arabic (formal or Egyptian colloquial), English, or a mix.
- Match on WHAT IS BEING ASKED FOR, not on the words. "how much is left" and "فيه كام" both ask
  for a quantity; "بكام اشترينا" and "تكلفته ايه" both ask for a cost; "اتصرف كام" and "كشف حركة"
  both ask for movements.

Examples:
Question: فيه كام زجاجة مياه
Answer: inventory.item-stock
Question: تكلفة الكرتونة كام
Answer: inventory.item-value
Question: اعرضلي الوارد والمنصرف للصنف ده
Answer: inventory.item-movements
Question: فيه قيود متكررة في الدفتر
Answer: accounting.journal-anomalies
Question: كام صنف تحت الحد الادنى
Answer: none
Question: مرتبات الشهر
Answer: none

Question: {question}
Answer:"""


class LlmIntentEngine:
    """Loaded once, lazily, and kept warm."""

    def __init__(self, model_path: str, threads: int | None = None):
        self.model_path = model_path
        self.threads = threads or max(1, (os.cpu_count() or 4))
        self._llm = None
        self._lock = threading.Lock()
        # INFERENCE IS SERIALISED, NOT JUST LOADING.
        #
        # One Llama context is not thread-safe, and this is not a race that raises something you can
        # catch: two completions on one context corrupted the KV cache and llama.cpp called
        # GGML_ASSERT, which aborts the process. The whole AI service went down on the first
        # concurrent pair — the startup warmup meeting the first real request.
        #
        # Serialising is the right fix rather than a mitigation: one context serves one request at a
        # time and the queue is honest. Serving two at once needs two contexts and twice the memory,
        # which is a decision for whoever sizes the machine, not something to find out from a crash.
        self._infer = threading.Lock()
        #: True only once the weights are loaded AND one completion has succeeded. Until then every
        #: caller is told "not available" instantly rather than queued behind the load.
        self._ready = False

    @property
    def name(self) -> str:
        return "llm:" + os.path.basename(self.model_path)

    def _ensure(self):
        """First call pays the load; everything after it is warm.

        Double-checked under a lock because two requests arriving together would otherwise each
        load several gigabytes of weights.
        """
        if self._llm is not None:
            return self._llm
        with self._lock:
            if self._llm is not None:
                return self._llm
            from llama_cpp import Llama
            self._llm = Llama(
                model_path=self.model_path,
                n_ctx=CONTEXT,
                n_threads=self.threads,
                verbose=False,
            )

            # A PROMPT CACHE, because the prefix is the same every time.
            #
            # Every prompt this engine sends is a long static block — the catalogue or the surface's
            # columns, plus the examples — followed by one short question. Without a cache the CPU
            # re-evaluates that whole prefix on every call, which is where 4.5 to 33 seconds per
            # composition went; the answer itself is a handful of tokens.
            #
            # Bounded on purpose. An unbounded cache on a long-lived service is a memory leak that
            # takes a week to show up.
            try:
                from llama_cpp import LlamaRAMCache
                self._llm.set_cache(LlamaRAMCache(capacity_bytes=CACHE_BYTES))
            except Exception:
                # Not having a cache is slow, not broken. An optional speed-up must never be the
                # reason the assistant stops answering.
                pass

            return self._llm


    def warm(self) -> bool:
        """Load the weights and prove one completion works. Called by the startup thread only.

        Separate from complete() on purpose: complete() must never load, or the first caller after a
        restart pays the load and every caller behind it waits on the lock. That is precisely the
        190-second request this exists to prevent.
        """
        try:
            # the graphics card's server, started in the background (owner 2026-11-24): the Ask layer prefers it
            from app.ml import gpu_llm
            gpu_llm.start_async(self.model_path)
        except Exception:
            pass
        try:
            llm = self._ensure()
            from llama_cpp import LlamaGrammar
            grammar = LlamaGrammar.from_string('root ::= "ok"\n', verbose=False)
            with self._infer:
                llm.create_completion(prompt="warmup", max_tokens=4, temperature=0.0, grammar=grammar)
            self._ready = True
            return True
        except Exception:
            self._ready = False
            return False

    def complete(self, prompt: str, grammar_text: str, max_tokens: int = MAX_TOKENS) -> str | None:
        """One grammar-constrained completion, under the inference lock.

        THE ONLY PLACE anything in this package may run the model. Callers used to reach for the
        private loader and drive the context themselves, which is exactly how two of them ended up
        inside it at once.
        """
        # ON THE GRAPHICS CARD FIRST (owner 2026-11-24): the same model on the card's server answers in a fraction of
        # the CPU's time. Busy (a summary holds it) or away, and this falls through to the CPU path below unchanged.
        try:
            from app.ml import gpu_llm
            out = gpu_llm.complete_raw(prompt, grammar_text, max_tokens, timeout=WAIT_SECONDS + 2)
            if out is not None:
                return out
        except Exception:
            pass

        # STILL LOADING IS A "NO", NOT A WAIT. The weights take the better part of a minute on CPU,
        # and a caller that blocks on them turns an optional improvement into a 190-second request.
        # The warmup thread sets `_ready` when the model can actually answer.
        if not self._ready:
            return None

        # AND A BOUNDED WAIT FOR THE LOCK. One context serves one request at a time; if another is
        # mid-completion for longer than a person will sit still, decline and let the caller fall
        # back to something it can answer now.
        if not self._infer.acquire(timeout=WAIT_SECONDS):
            return None
        try:
            llm = self._llm
            if llm is None:
                return None
            from llama_cpp import LlamaGrammar
            grammar = LlamaGrammar.from_string(grammar_text, verbose=False)
            out = llm.create_completion(
                prompt=prompt,
                max_tokens=max_tokens,
                temperature=0.0,
                grammar=grammar,
            )
            return (out["choices"][0]["text"] or "").strip()
        except Exception:
            return None
        finally:
            self._infer.release()

    def choose(self, question: str, catalogue: list[tuple[str, str]]) -> LlmChoice:
        """`catalogue` is (id, one-line description) for the intents this asker may use."""
        ids = [i for i, _ in catalogue]
        if not ids or not question.strip():
            return LlmChoice(None, self.name, ran=False)

        rendered = "\n".join(f"- {i}: {d}" for i, d in catalogue)
        # THE GRAMMAR STAYS, on the card too (measured 2026-11-24 on tools/eval_intent_llm.py, 79 questions): without it
        # the card answered in a median 0.61 s but answered 5 of 13 questions nobody handles; with it, 3 of 13 at a
        # median 1.46 s. Fewer confident wrong answers wins - and neither is leak-free, so INTENT_LLM_ENABLED stays off.
        raw = self.complete(
            _PROMPT.format(catalogue=rendered, question=question.strip()),
            _grammar_for(ids))
        if raw is None:
            # The tail engine is the optional one. An assistant that stops answering
            # because its most expensive component is missing has made it mandatory.
            return LlmChoice(None, self.name, ran=False)

        if raw == "none" or raw not in ids:
            # `not in ids` should be impossible under the grammar. Checked anyway, because
            # "impossible" is what a constraint claims and this is what verifies the claim.
            return LlmChoice(None, self.name, ran=True, raw=raw)
        return LlmChoice(raw, self.name, ran=True, raw=raw)


_engine: LlmIntentEngine | None = None
_tried = False


def engine() -> LlmIntentEngine | None:
    """None when no weights are present — a normal checkout, not an error."""
    global _engine, _tried
    if _engine is not None:
        return _engine
    if _tried:
        return None
    _tried = True
    path = find_model()
    if not path:
        return None
    try:
        import llama_cpp  # noqa: F401
    except ImportError:
        return None
    _engine = LlmIntentEngine(path)
    return _engine


def reset_cache() -> None:
    global _engine, _tried
    _engine = None
    _tried = False
