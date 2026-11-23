"""The assistant in a conversation, and message translation (owner, 2026-11-19 — after the second evaluation against
Teams/Slack/Gemini/Zoom: "the real gap is AI inside the conversation").

On this machine, for free, with the local model the call summaries already load (one model in memory, one completion
at a time — app/ml/calls.py owns the engine and its lock):

  * SUMMARY of a conversation or a thread: an overview, key points, decisions, and tasks with who does them — the
    same JSON schema and the same guards as a call summary: too few words are never summarised, and an item that shares
    no words with what was written is dropped (a local model, asked to fill fields, invents a meeting out of nothing).
  * ASK: a question about the conversation. The model must name the messages its answer rests on; an answer that cites
    no real message, or none that share its words, is replaced by "not found in this conversation". It may say it
    does not know; it may not make something up.
  * TRANSLATE one message, Arabic ↔ English (Fusha for Arabic), on request.

Summary and ask run as jobs in the background (a long conversation takes minutes on a processor); .NET asks for the
status. Translation is one short completion and answers directly.
"""
from __future__ import annotations

import json
import logging
import threading
import time
import uuid

from app.ml import calls

log = logging.getLogger("uvicorn.error")

_jobs: dict[str, dict] = {}
_jobs_lock = threading.Lock()
JOB_TTL = 3600
MAX_CHARS = 14_000           # the newest messages that fit the 8,192-token context (Arabic runs ~2.5 characters a
                             # token, and the answer needs room); older ones are left out and the result says so
LOCK_WAIT = 600              # a job waits at most this long for the model (a call summary may hold it); then it fails

ASK_SCHEMA = {
    "type": "object",
    "properties": {
        "found": {"type": "boolean"},
        "answer": {"type": "string"},
        "messageIds": {"type": "array", "items": {"type": "integer"}, "maxItems": 8},
    },
    "required": ["found", "answer", "messageIds"],
}
TRANSLATE_SCHEMA = {"type": "object", "properties": {"text": {"type": "string"}}, "required": ["text"]}
REPLY_SCHEMA = TRANSLATE_SCHEMA


def _lines(messages: list[dict]) -> tuple[str, dict[int, str], bool]:
    """'[id] sender: text' newest-last, cut to MAX_CHARS from the newest end."""
    rows, size, cut = [], 0, False
    for m in reversed(messages):
        t = (m.get("text") or "").strip()
        if not t:
            continue
        line = f"[{int(m.get('id', 0))}] {m.get('sender') or ''}: {t}"
        if size + len(line) > MAX_CHARS:
            cut = True
            break
        rows.append(line); size += len(line) + 1
    rows.reverse()
    by_id = {int(m.get("id", 0)): (m.get("text") or "") for m in messages}
    return "\n".join(rows), by_id, cut


def _complete(system: str, user: str, schema: dict, max_tokens: int = 900) -> dict:
    llm = calls._engine()
    t0 = time.perf_counter()
    if not calls._llm_lock.acquire(timeout=LOCK_WAIT):      # one context, one completion at a time
        raise TimeoutError("the model was busy")
    try:
        waited = time.perf_counter() - t0
        out = llm.create_chat_completion(
            messages=[{"role": "system", "content": system}, {"role": "user", "content": user}],
            response_format={"type": "json_object", "schema": schema}, temperature=0.2, max_tokens=max_tokens)
    finally:
        calls._llm_lock.release()
    log.info("assist: completion of %d chars in %.1fs (waited %.1fs for the model)", len(user), time.perf_counter() - t0 - waited, waited)
    return json.loads(out["choices"][0]["message"]["content"])


# ------------------------------------------------------------------------------------------------ summary
def _summary(messages: list[dict], lang: str, participants: list[str]) -> dict:
    text, _, cut = _lines(messages)
    if len(text.split()) < calls.MIN_WORDS:
        return {"overview": "", "points": [], "decisions": [], "actions": [], "tooShort": True, "cut": cut}
    system = calls._system(lang, participants).replace("مكالمة عمل", "محادثة عمل مكتوبة").replace("business call", "written business conversation")
    s = calls._grounded(_summarize_text(system, text, lang), text)
    s["cut"] = cut
    return s


def _summarize_text(system: str, text: str, lang: str) -> dict:
    if len(text) <= calls.CHUNK_CHARS:
        return _complete(system, text, calls.SCHEMA)
    parts = [text[i:i + calls.CHUNK_CHARS] for i in range(0, len(text), calls.CHUNK_CHARS)]
    partial = [_complete(system, p, calls.SCHEMA) for p in parts]
    lead = "هذه ملخصات أجزاء المحادثة بالترتيب؛ اجمعها في ملخص واحد دون تكرار:\n" if lang == "ar" else \
        "These are summaries of the conversation's parts in order; merge them into one summary without repetition:\n"
    return _complete(system, lead + json.dumps(partial, ensure_ascii=False)[:calls.CHUNK_CHARS * 2], calls.SCHEMA)


# ------------------------------------------------------------------------------------------------ ask
def _ask(messages: list[dict], question: str, lang: str) -> dict:
    text, by_id, cut = _lines(messages)
    not_found = {"found": False, "answer": "", "messageIds": [], "cut": cut}
    if not text.strip() or not question.strip():
        return not_found
    system = ("أنت تجيب عن سؤال حول محادثة عمل مكتوبة. استخدم نص المحادثة وحده. اكتب بالعربية الفصحى بجملة أو جملتين. "
              "اذكر في messageIds أرقام الرسائل (بين القوسين المربعين) التي بنيت عليها الإجابة. "
              "إن لم تجد الإجابة في المحادثة فاجعل found=false ولا تخمّن.") if lang == "ar" else \
             ("You answer a question about a written business conversation, using only its text. Answer in one or two "
              "sentences. Put in messageIds the numbers (in square brackets) of the messages your answer rests on. "
              "If the answer is not in the conversation, set found=false and do not guess.")
    # the newest part of a long conversation goes in whole; the question goes last so it is never cut off
    r = _complete(system, text[-calls.CHUNK_CHARS * 2:] + "\n\n" + ("السؤال: " if lang == "ar" else "Question: ") + question.strip(), ASK_SCHEMA, 400)
    ids = [i for i in (r.get("messageIds") or []) if isinstance(i, int) and i in by_id]
    answer = (r.get("answer") or "").strip()
    # GROUNDED OR NOTHING: the cited messages must exist and share words with the answer
    cited = " ".join(by_id[i] for i in ids)
    if not r.get("found") or not ids or not answer or not (calls._words(answer) & calls._words(cited)):
        return not_found
    return {"found": True, "answer": answer, "messageIds": ids, "cut": cut}


# ------------------------------------------------------------------------------------------------ reply (email)
def _reply(messages: list[dict], lang: str, participants: list[str], instruction: str | None) -> dict:
    """A DRAFT answer to the last message of a mail conversation, written for participants[0] to edit and send
    (owner 2026-11-23: mail like the chat, with the assistant). It answers only what was written: no number, date or
    promise that is not in the conversation or in the writer's own instruction."""
    text, _, cut = _lines(messages)
    if not text.strip():
        return {"text": "", "cut": cut}
    me = (participants[0] if participants else "").strip()
    hint = (instruction or "").strip()
    if lang == "ar":
        system = ("أنت تكتب مسودة رد على رسالة بريد عمل" + (f" باسم {me}" if me else "") + ". اكتب بالعربية الفصحى وحدها، "
                  "بنبرة مهنية مهذبة، في فقرة أو فقرتين قصيرتين. أجب عن آخر رسالة في المحادثة مستنداً إلى ما ورد فيها فقط، "
                  "ولا تخترع أرقاماً أو مواعيد أو التزامات غير مذكورة. لا تكتب سطر الموضوع ولا التوقيع."
                  + (f" التزم بتوجيه الكاتب: {hint}" if hint else ""))
        lead = "المحادثة (الأقدم أولاً):\n"
    else:
        system = ("You write a draft reply to a business email" + (f" on behalf of {me}" if me else "") + ". Write plain, "
                  "polite, professional English in one or two short paragraphs. Answer the LAST message using only what the "
                  "conversation says; do not invent numbers, dates or commitments. No subject line, no signature."
                  + (f" Follow the writer's instruction: {hint}" if hint else ""))
        lead = "The conversation (oldest first):\n"
    r = _complete(system, lead + text[-calls.CHUNK_CHARS * 2:], REPLY_SCHEMA, 600)
    return {"text": (r.get("text") or "").strip(), "cut": cut}


# ------------------------------------------------------------------------------------------------ jobs
def _sweep() -> None:
    now = time.time()
    with _jobs_lock:
        for k in [k for k, v in _jobs.items() if now - v["created"] > JOB_TTL]:
            _jobs.pop(k, None)


def start(mode: str, messages: list[dict], lang: str, participants: list[str], question: str | None) -> str:
    _sweep()
    job = uuid.uuid4().hex
    with _jobs_lock:
        _jobs[job] = {"status": "working", "created": time.time(), "mode": mode}

    def run() -> None:
        started = time.perf_counter()
        try:
            if mode == "translate":
                # a translation waits its turn for the model like a summary does (owner 2026-11-22: it failed when
                # a summary was running); None only after LOCK_WAIT
                text = translate((messages[0].get("text") if messages else "") or "", lang, wait=LOCK_WAIT)
                if text is None:
                    raise TimeoutError("the model was busy")
                result = {"text": text}
            else:
                result = (_ask(messages, question or "", lang) if mode == "ask"
                          else _reply(messages, lang, participants, question) if mode == "reply"
                          else _summary(messages, lang, participants))
            with _jobs_lock:
                _jobs[job].update(status="done", result=result, seconds=round(time.perf_counter() - started, 1))
        except Exception as e:     # a failed job says so; it never takes the service down
            log.warning("assist: %s job failed: %s", mode, e)
            with _jobs_lock:
                _jobs[job].update(status="failed", error=type(e).__name__ + ": " + str(e)[:160])

    threading.Thread(target=run, name=f"assist-{mode}", daemon=True).start()
    return job


def status(job: str) -> dict:
    with _jobs_lock:
        j = _jobs.get(job)
        if j is None:
            raise KeyError(job)
        out = {k: j.get(k) for k in ("status", "result", "error", "seconds")}
        if j["status"] in ("done", "failed"):
            _jobs.pop(job, None)          # read once: the words do not stay here
        return out


# ------------------------------------------------------------------------------------------------ translate
def translate(text: str, to: str, wait: float = 90) -> str | None:
    """One message into Arabic (Fusha) or English. None when the model is busy for too long (the caller says so)."""
    text = (text or "").strip()
    if not text:
        return ""
    system = ("ترجم النص الذي يرسله المستخدم إلى العربية الفصحى ترجمةً أمينة، واحتفظ بالأسماء والأرقام والروابط كما هي. "
              "لا تشرح ولا تضف شيئاً.") if to == "ar" else \
             ("Translate the user's text into clear English, faithfully, keeping names, numbers and links as they are. "
              "Do not explain or add anything.")
    llm = calls._engine()
    if not calls._llm_lock.acquire(timeout=wait):
        return None
    try:
        out = llm.create_chat_completion(
            messages=[{"role": "system", "content": system}, {"role": "user", "content": text[:4000]}],
            response_format={"type": "json_object", "schema": TRANSLATE_SCHEMA}, temperature=0.1, max_tokens=1200)
        return (json.loads(out["choices"][0]["message"]["content"]).get("text") or "").strip()
    finally:
        calls._llm_lock.release()
