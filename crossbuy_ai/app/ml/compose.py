"""Composing a query over a governed surface — the step that removes "someone must have thought of
this question first".

WHAT THIS REPLACES. Twelve hand-written questions meant twelve questions. A surface declares its
columns instead, and this turns a sentence into a query over those columns, so the thirteenth
question needs no code.

WHY THE OUTPUT IS A FOUR-SLOT LINE AND NOT JSON. A 4B model asked for JSON produces JSON that is
nearly right: a trailing comma, a renamed key, a column it invented because the name sounded
plausible. Constraining it with a grammar fixes the SHAPE but a JSON grammar is large, and every
rule in it is a rule the model can wander inside.

So it emits one line with four slots — dimension, measure, direction, limit — and the grammar
admits only the literal column names the surface sent, plus the few allowed directions and limits.
There is nothing to get syntactically wrong, nothing to invent, and the whole answer is a handful
of tokens, which is also why it is fast. The .NET side turns the line into a query and validates it
again against the surface anyway, because a constraint that is only enforced in one place is a
constraint that will one day be enforced nowhere.

WHAT IT CANNOT DO, STATED SO NOBODY RELIES ON IT. No filters, no second grouping level, no date
ranges. Those are the next slots; they are absent rather than approximated. A question that needs
one gets the ungrouped answer and the caller says so, which is a worse answer than the asker wanted
and not a wrong one.
"""
from __future__ import annotations

import re
from dataclasses import dataclass

from app.ml.understand import normalise

#: The limits a caller may ask for. A closed set because it is one more thing the model cannot get
#: wrong, and because an unbounded row count is a row dump wearing an aggregate.
LIMITS = ("5", "10", "20", "50")
DIRECTIONS = ("desc", "asc")
NONE = "none"


@dataclass(frozen=True)
class Composition:
    group_by: str | None
    measure: str
    descending: bool
    limit: int
    raw: str
    engine: str = "compose-v1"


def grammar(dimensions: list[str], measures: list[str]) -> str:
    """A GBNF admitting exactly: <dimension|none> | <measure> | <desc|asc> | <limit>.

    Built from the columns the CALLER sent, every time. A grammar baked at build time would forbid
    every column added afterwards, and the failure would look like the model being stupid.
    """
    def alts(values: list[str]) -> str:
        return " | ".join('"' + v.replace("\\", "\\\\").replace('"', '\\"') + '"' for v in values)

    return (
        f"root ::= dim \" | \" measure \" | \" dir \" | \" lim\n"
        f"dim ::= {alts(dimensions + [NONE])}\n"
        f"measure ::= {alts(measures)}\n"
        f"dir ::= {alts(list(DIRECTIONS))}\n"
        f"lim ::= {alts(list(LIMITS))}\n"
    )


PROMPT = """You turn a business question into a query over one table.

Table: {code}
What it holds: {description}

Columns you may GROUP BY:
{dimensions}

Columns you may TOTAL:
{measures}

Answer with exactly one line in this form:
<group-by column or none> | <column to total> | <desc or asc> | <row count>

Rules:
- Pick the grouping the question asks for. "by item" -> item. "by month" -> month. "per customer"
  -> customer. If the question asks for one overall number, use none.
- Pick the column the question is actually about. Asking what sells -> revenue. Asking what it
  costs us -> cost. Asking what we make -> margin. Asking how many -> qty.
- "highest", "best", "top", "most" -> desc. "lowest", "worst", "least" -> asc.
- The question may be in Arabic (formal or Egyptian colloquial), English, or a mix.

Examples:
Question: ايه الايراد لكل شهر
Answer: month | revenue | desc | 10
Question: مين اكبر زباين عندنا
Answer: customer | revenue | desc | 10
Question: ايه الاصناف اللي بنكسب فيها اكتر
Answer: item | margin | desc | 10
Question: اقل الاصناف مبيعا
Answer: item | revenue | asc | 10
Question: بعنا كام اجمالا
Answer: none | revenue | desc | 10
Question: which products cost us the most
Answer: item | cost | desc | 10

Question: {question}
Answer:"""


def parse(line: str, dimensions: list[str], measures: list[str]) -> Composition | None:
    """Strict. The grammar should make every part legal; this verifies it rather than trusting it."""
    parts = [p.strip() for p in (line or "").split("|")]
    if len(parts) != 4:
        return None

    dim, measure, direction, limit = parts
    if dim != NONE and dim not in dimensions:
        return None
    if measure not in measures:
        return None
    if direction not in DIRECTIONS:
        return None
    if not re.fullmatch(r"\d{1,3}", limit) or limit not in LIMITS:
        return None

    return Composition(
        group_by=None if dim == NONE else dim,
        measure=measure,
        descending=direction == "desc",
        limit=int(limit),
        raw=line.strip(),
    )


def compose(engine, question: str, code: str, description: str,
            dimensions: list[tuple[str, str]], measures: list[tuple[str, str]]) -> Composition | None:
    """`engine` is an LlmIntentEngine; dimensions/measures are (name, label) pairs.

    Returns None whenever anything is off — no engine, an empty question, a line that does not
    parse. The caller must then fall back to a known-good query and SAY it substituted, because a
    silently different question answered with real figures is the worst outcome available here.
    """
    if engine is None or not question or not question.strip():
        return None
    if not dimensions or not measures:
        return None

    dim_names = [n for n, _ in dimensions]
    measure_names = [n for n, _ in measures]

    prompt = PROMPT.format(
        code=code,
        description=description,
        question=question.strip(),
        dimensions="\n".join(f"- {n}: {label}" for n, label in dimensions),
        measures="\n".join(f"- {n}: {label}" for n, label in measures),
    )

    # Through the engine, which holds the inference lock. Driving the context directly
    # from here is what crashed the service: two completions on one context abort the
    # process rather than raising.
    line = engine.complete(prompt, grammar(dim_names, measure_names), max_tokens=32)
    if line is None:
        return None

    return parse(line, dim_names, measure_names)


def fold_hint(question: str) -> str:
    """Normalised question, for callers that want to log what was asked without the spelling noise."""
    return normalise(question)
