"""Call summaries — a recorded call becomes its words, then a short summary (owner, 2026-11-19: "since it is free, do it").

WHAT IT NEEDS, AND WHY ONLY A RECORDED CALL. A summary is made from what was said, so the call must have been
recorded — and recording already asks both sides for consent. Nothing is captured for this feature that the
people in the call did not agree to.

HOW, ON THIS MACHINE, FOR FREE.
  1. The recording arrives from .NET in pieces (each under the egress policy's 2 MB), into a job folder.
  2. Whisper (app/ml/speech.py, the same weights as voice messages) turns it into words — in WINDOWS of
     WINDOW_SECONDS, taking the speech lock one window at a time, so a voice message someone is waiting on is never
     stuck behind a forty-minute call.
  3. The local model already in models/ (Qwen3-4B-Instruct, GGUF, llama.cpp) writes the summary. Its output is
     CONSTRAINED BY A JSON SCHEMA (a grammar, not a request in the prompt), so it can only return the four fields
     .NET reads: an overview, key points, decisions, and tasks with who does them. A long call is summarised in
     parts and the parts are summarised again.
  4. .NET fetches the result and tells the service to forget the job: the audio and the words are deleted here.

WHY THIS DOES NOT REPEAT THE MISTAKE LOCAL_MODEL.md RECORDS. The model was switched off because it sat inside a
request path and turned refusals into two-minute silences. Here nobody waits on it: one background thread works
through a queue, .NET asks for the status, and the result appears when it is ready.
"""
from __future__ import annotations

import base64
import json
import logging
import os
import shutil
import threading
import time
import uuid
from pathlib import Path

from app.ml import speech
from app.ml.intent_llm import find_model

_ROOT = Path(__file__).resolve().parents[2]
JOBS = _ROOT / "data" / "call-jobs"
WINDOW_SECONDS = 45.0
MAX_BYTES = 300 * 1024 * 1024          # a long video call; past this a job is refused
MAX_CALL_SECONDS = 2 * 3600.0
CHUNK_CHARS = 9000                     # transcript characters per summary part (fits the 8k context with room)
MIN_WORDS = 12                         # fewer words than this are not a conversation to summarise

_queue: list[str] = []
_cv = threading.Condition()
_worker: threading.Thread | None = None
_llm = None
_llm_lock = threading.RLock()   # re-entrant: the card's fallback loads the CPU engine while its caller holds it
log = logging.getLogger("crossbuy.calls")

SCHEMA = {
    "type": "object",
    "properties": {
        "overview": {"type": "string"},
        "points": {"type": "array", "items": {"type": "string"}, "maxItems": 8},
        "decisions": {"type": "array", "items": {"type": "string"}, "maxItems": 8},
        "actions": {"type": "array", "maxItems": 10, "items": {
            "type": "object",
            "properties": {"owner": {"type": "string"}, "task": {"type": "string"}},
            "required": ["owner", "task"]}},
    },
    "required": ["overview", "points", "decisions", "actions"],
}


# ---------------------------------------------------------------------------------------------- the job folder
def _dir(job: str) -> Path:
    if not job or any(c not in "0123456789abcdef" for c in job) or len(job) != 32:
        raise KeyError(job)
    return JOBS / job


def _state(job: str) -> dict:
    p = _dir(job) / "state.json"
    if not p.exists():
        raise KeyError(job)
    return json.loads(p.read_text(encoding="utf-8"))


def _save(job: str, st: dict) -> None:
    p = _dir(job) / "state.json"
    tmp = p.with_suffix(".tmp")
    tmp.write_text(json.dumps(st, ensure_ascii=False), encoding="utf-8")
    os.replace(tmp, p)


def create(language: str | None, participants: list[str]) -> str:
    job = uuid.uuid4().hex
    d = JOBS / job
    d.mkdir(parents=True, exist_ok=False)
    _save(job, {"status": "uploading", "language": language or None, "participants": participants[:12], "bytes": 0,
                "chunks": 0, "created": time.time(), "progress": 0.0})
    return job


def append(job: str, index: int, data_b64: str) -> dict:
    st = _state(job)
    if st["status"] != "uploading":
        raise ValueError("not_uploading")
    if index != st["chunks"]:
        raise ValueError("out_of_order")      # pieces arrive in order; a gap would corrupt the file
    data = base64.b64decode(data_b64, validate=True)
    if st["bytes"] + len(data) > MAX_BYTES:
        raise ValueError("too_large")
    with open(_dir(job) / "audio.bin", "ab") as f:
        f.write(data)
    st["bytes"] += len(data); st["chunks"] += 1
    _save(job, st)
    return st


def start(job: str) -> dict:
    st = _state(job)
    if st["status"] == "uploading":
        st["status"] = "queued"; _save(job, st)
        _enqueue(job)
    return st


def status(job: str) -> dict:
    st = _state(job)
    out = {k: st.get(k) for k in ("status", "progress", "error", "language", "model", "audioSeconds", "workSeconds")}
    if st["status"] == "done":
        out["transcript"] = st.get("transcript", "")
        out["summary"] = st.get("summary")
    return out


def forget(job: str) -> None:
    shutil.rmtree(_dir(job), ignore_errors=True)


# ---------------------------------------------------------------------------------------------- the worker
def _enqueue(job: str) -> None:
    global _worker
    with _cv:
        if job not in _queue:
            _queue.append(job)
        if _worker is None or not _worker.is_alive():
            _worker = threading.Thread(target=_run, name="call-summaries", daemon=True)
            _worker.start()
        _cv.notify()


def resume_unfinished() -> None:
    """After a restart: a job that was queued or half-done starts again from its audio (nothing is lost)."""
    if not JOBS.exists():
        return
    for d in sorted(JOBS.iterdir(), key=lambda p: p.stat().st_mtime):
        try:
            st = _state(d.name)
        except Exception:
            continue
        if st["status"] in ("queued", "transcribing", "summarizing"):
            st["status"] = "queued"; _save(d.name, st)
            _enqueue(d.name)


def _run() -> None:
    while True:
        with _cv:
            while not _queue:
                _cv.wait(timeout=300)
                if not _queue:
                    return            # idle five minutes: the thread ends; the next job starts a new one
            job = _queue.pop(0)
        try:
            _process(job)
        except Exception as e:      # one bad file must not stop the queue
            try:
                st = _state(job); st["status"] = "failed"; st["error"] = type(e).__name__ + ": " + str(e)[:160]; _save(job, st)
            except Exception:
                pass


def _process(job: str) -> None:
    st = _state(job)
    started = time.perf_counter()
    st["status"] = "transcribing"; st["progress"] = 0.0; _save(job, st)

    from faster_whisper.audio import decode_audio
    samples = decode_audio(str(_dir(job) / "audio.bin"), sampling_rate=16000)
    total = len(samples) / 16000.0
    if total > MAX_CALL_SECONDS:
        raise ValueError("call_too_long")
    st["audioSeconds"] = round(total, 1)

    # medium reads Arabic far better; on a processor it is about twice as slow, so a long call gets small
    size = "medium" if total <= 6 * 60 and speech.available("medium") else "small"
    win = int(WINDOW_SECONDS * 16000)
    texts: list[str] = []
    lang = st.get("language")
    for i in range(0, len(samples), win):
        piece = samples[i:i + win]
        if len(piece) < 16000 * 0.5:
            continue
        t = speech.transcribe_samples(piece, size, lang)   # takes the speech lock for this window only
        if t.text:
            texts.append(t.text)
        lang = lang or (t.language if t.language_probability >= 0.6 else None)   # the first sure guess holds
        st["progress"] = round(min(0.95, (i + win) / max(1, len(samples))) * 0.7, 3); _save(job, st)
    transcript = "\n".join(texts).strip()
    st.update(transcript=transcript, language=lang, model=f"whisper-{size}")

    st["status"] = "summarizing"; st["progress"] = 0.75; _save(job, st)
    # TOO LITTLE WAS SAID, SO NOTHING IS SUMMARISED. Found on a real recording: its whole transcript was "Hmm" and the
    # model, asked to fill four fields, invented an entire meeting — points, decisions, tasks with owners. A summary
    # that makes things up is worse than none, so below MIN_WORDS the model is not asked at all; above it, every item
    # it returns must share words with what was actually said (_grounded).
    if len(transcript.split()) < MIN_WORDS:
        st["summary"] = {"overview": "", "points": [], "decisions": [], "actions": [], "tooShort": True}
    else:
        st["summary"] = _grounded(_summarize(transcript, lang or "ar", st.get("participants") or []), transcript)
    st["model"] = f"whisper-{size}+" + os.path.basename(find_model() or "none")
    st["workSeconds"] = round(time.perf_counter() - started, 1)
    st["status"] = "done"; st["progress"] = 1.0
    try:
        os.remove(_dir(job) / "audio.bin")      # the words are kept until .NET fetches them; the audio is not needed
    except OSError:
        pass
    _save(job, st)


# ---------------------------------------------------------------------------------------------- the summary
def _cpu_engine():
    global _llm
    with _llm_lock:
        if _llm is None:
            path = find_model()
            if not path:
                raise FileNotFoundError("no .gguf in models/")
            from llama_cpp import Llama
            _llm = Llama(model_path=path, n_ctx=8192, n_threads=max(1, min(8, (os.cpu_count() or 2) // 2)), verbose=False)
        return _llm


class _CardOrCpu:
    """The graphics card's engine (gpu_llm), and the CPU's the moment it fails — the caller never sees which."""
    def __init__(self, card):
        self._card = card

    def create_chat_completion(self, **kw):
        try:
            return self._card.create_chat_completion(**kw)
        except Exception as e:
            from app.ml import gpu_llm
            log.warning("summary engine: the graphics card failed (%s); the CPU answers", e)
            gpu_llm.mark_failed()
            return _cpu_engine().create_chat_completion(**kw)


def _engine():
    """The engine of the summaries, the assistant and translation: on the graphics card when it can be had
    (gpu_llm: ~4x faster here), else on the CPU as before."""
    path = find_model()
    if not path:
        raise FileNotFoundError("no .gguf in models/")
    from app.ml import gpu_llm
    card = gpu_llm.server(path)
    return _CardOrCpu(card) if card is not None else _cpu_engine()


def _ask(system: str, user: str) -> dict:
    llm = _engine()
    with _llm_lock:       # one context, one completion at a time (two at once aborts llama.cpp)
        out = llm.create_chat_completion(
            messages=[{"role": "system", "content": system}, {"role": "user", "content": user}],
            response_format={"type": "json_object", "schema": SCHEMA},
            temperature=0.2, max_tokens=900)
    return json.loads(out["choices"][0]["message"]["content"])


def _system(lang: str, participants: list[str]) -> str:
    who = ("، ".join(participants) if lang == "ar" else ", ".join(participants)) if participants else ""
    if lang == "ar":
        return ("أنت تلخّص مكالمة عمل داخل شركة. اكتب بالعربية الفصحى وحدها، بجمل قصيرة واضحة. لا تذكر أي معلومة غير موجودة في النص حرفياً أو معنىً؛ وإن لم يكن في النص كلام عمل حقيقي فاترك كل الحقول فارغة. "
                "overview: فقرة من جملتين أو ثلاث. points: أهم النقاط. decisions: ما اتُّفق عليه فقط. "
                "actions: المهام التي طُلبت صراحةً في المكالمة فقط، ومن يقوم بها (owner) إن ذُكر وإلا فاكتب «غير محدد»؛ لا تستنتج مهمة لم تُطلب، وإن لم تُطلب مهام فاترك القائمة فارغة. "
                + (f"المشاركون: {who}." if who else ""))
    return ("You summarise a business call inside a company. Write in plain, short sentences. State nothing that is "
            "not in the text; if the text holds no real business conversation, leave every field empty. overview: two or three sentences. points: the key points. decisions: only what was agreed. "
            "actions: only tasks explicitly asked for in the call, and who does it (owner) if named, otherwise 'unassigned'; do not infer a task nobody asked for, and leave the list empty if none was. "
            + (f"Participants: {who}." if who else ""))


def _words(text: str) -> set[str]:
    import re
    # Arabic and Latin words of three letters or more, without the article and common clitics, digits kept
    out = set()
    for w in re.findall(r"[\w؀-ۿ]+", text.lower()):
        w = re.sub(r"^(وال|بال|فال|كال|لل|ال|و|ف|ب)(?=\w{3,})", "", w)
        if len(w) >= 3 or w.isdigit():
            out.add(w)
    return out


def _grounded(summary: dict, transcript: str) -> dict:
    """Keep only what the call actually contains: an item sharing no word with the transcript is dropped."""
    said = _words(transcript)
    def ok(text: str) -> bool:
        w = _words(text)
        return bool(w) and len(w & said) >= max(1, min(2, len(w) // 3))
    out = {
        "overview": summary.get("overview", "") if ok(summary.get("overview", "")) else "",
        "points": [p for p in summary.get("points", []) if ok(p)],
        "decisions": [d for d in summary.get("decisions", []) if ok(d)],
        "actions": [a for a in summary.get("actions", []) if isinstance(a, dict) and ok(a.get("task", ""))],
    }
    if not (out["overview"] or out["points"] or out["decisions"] or out["actions"]):
        out["tooShort"] = True
    return out


def _summarize(transcript: str, lang: str, participants: list[str]) -> dict:
    system = _system(lang, participants)
    if len(transcript) <= CHUNK_CHARS:
        return _ask(system, transcript)
    # a long call: each part on its own, then the parts together
    parts = [transcript[i:i + CHUNK_CHARS] for i in range(0, len(transcript), CHUNK_CHARS)]
    partial = [_ask(system, p) for p in parts]
    merged = json.dumps(partial, ensure_ascii=False)
    lead = "هذه ملخصات أجزاء المكالمة بالترتيب؛ اجمعها في ملخص واحد دون تكرار:\n" if lang == "ar" else \
        "These are summaries of the call's parts in order; merge them into one summary without repetition:\n"
    return _ask(system, lead + merged[:CHUNK_CHARS * 2])
