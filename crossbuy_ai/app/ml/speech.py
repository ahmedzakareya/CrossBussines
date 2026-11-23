"""Speech to text — a voice message becomes words, on this machine (owner, 2026-11-19).

THE ENGINE. faster-whisper 1.2.1 (MIT) running OpenAI's Whisper weights (MIT) through CTranslate2, int8 on the
processor. No graphics card, no provider, no API key, and no audio ever leaves the service: the weights sit in
crossbuy_ai/models/whisper-<size>/ and are loaded from there, never downloaded at run time. PyAV is pinned to 15.1.0
because 19.x removed an argument faster-whisper 1.2.1 still passes.

WHICH WEIGHTS — measured on a 16-second Egyptian-Arabic recording, on this machine while it was also building:
  * medium: one word wrong in twenty-five ("1200" even written as a number); 29–60 s of work.
  * small : five words wrong; 11–20 s of work.
So "auto" (the default) gives a short message (≤ 30 s, most voice notes) to medium and a longer one to small, and
refuses past MAX_SECONDS: the .NET client waits 120 s, and a reply that arrives after it gave up helps nobody.

WHY IT IS NOT IN A HOT PATH. The local language model was switched off because it put two-minute silences into a
chat box while the machine was busy building (LOCAL_MODEL.md). Transcription is the same weight class, so:
  * it runs only when someone asks for a message's text, never on every message;
  * .NET stores the result, so each recording is transcribed once for everyone;
  * one transcription at a time (a lock), on at most eight threads, so it cannot starve the .NET server;
  * the weights load on first use and stay loaded.
"""
from __future__ import annotations

import io
import os
import threading
import time
from dataclasses import dataclass
from pathlib import Path

_MODELS = Path(__file__).resolve().parents[2] / "models"
_lock = threading.Lock()
_models: dict[str, object] = {}

SHORT_SECONDS = 30.0     # up to here, "auto" uses medium
MAX_SECONDS = 95.0       # past here, nothing is transcribed (the caller waits 120 s)


class TooLong(Exception):
    def __init__(self, seconds: float):
        super().__init__(f"{seconds:.0f}s")
        self.seconds = seconds


@dataclass
class Transcript:
    text: str
    language: str
    language_probability: float
    duration: float
    seconds: float
    model: str


def _threads() -> int:
    # half the cores, at most eight: the .NET server shares this machine
    return max(1, min(8, (os.cpu_count() or 2) // 2))


def available(size: str) -> bool:
    if size == "auto":
        return available("small") or available("medium")
    return (_MODELS / f"whisper-{size}" / "model.bin").exists()


def _load(size: str):
    if size in _models:
        return _models[size]
    from faster_whisper import WhisperModel   # imported here: the service starts without it

    path = _MODELS / f"whisper-{size}"
    if not (path / "model.bin").exists():
        raise FileNotFoundError(f"whisper-{size} is not in {_MODELS}")
    _models[size] = WhisperModel(str(path), device="cpu", compute_type="int8", cpu_threads=_threads())
    return _models[size]


def _pick(size: str, seconds: float) -> str:
    if size != "auto":
        return size
    want = "medium" if seconds <= SHORT_SECONDS else "small"
    return want if available(want) else ("small" if available("small") else "medium")


def transcribe_samples(samples, size: str, language: str | None = None) -> Transcript:
    """Already-decoded audio (16 kHz mono floats) — a window of a recorded call. Same lock, same settings."""
    with _lock:
        started = time.perf_counter()
        chosen = _pick(size, len(samples) / 16000.0)
        model = _load(chosen)
        segments, info = model.transcribe(samples, language=language or None, beam_size=5, vad_filter=True,
                                          condition_on_previous_text=False)
        text = " ".join(s.text.strip() for s in segments).strip()
        return Transcript(text=text, language=info.language, language_probability=round(float(info.language_probability), 3),
                          duration=round(float(info.duration), 2), seconds=round(time.perf_counter() - started, 2),
                          model=f"whisper-{chosen}")


CAPTION_MAX_SECONDS = 8.0   # a caption chunk is about three seconds; anything much longer is not one
CAPTION_WAIT = 2.0          # a busy model skips the chunk: a caption that arrives late is worth nothing


def caption(audio: bytes, language: str | None = None) -> str | None:
    """LIVE CAPTIONS in a call (owner 2026-11-22): a few seconds of one person's voice, as quickly as possible.
    The small weights and greedy decoding (beam 1), silence skipped. None when the model stays busy past CAPTION_WAIT
    (the chunk is dropped, never queued); "" when nothing was said."""
    from faster_whisper.audio import decode_audio

    samples = decode_audio(io.BytesIO(audio), sampling_rate=16000)
    if len(samples) / 16000.0 > CAPTION_MAX_SECONDS:
        samples = samples[: int(CAPTION_MAX_SECONDS * 16000)]
    if not _lock.acquire(timeout=CAPTION_WAIT):
        return None
    try:
        size = "small" if available("small") else "medium"
        model = _load(size)
        segments, _ = model.transcribe(samples, language=language or None, beam_size=1, vad_filter=True,
                                       condition_on_previous_text=False, without_timestamps=True)
        text = " ".join(s.text.strip() for s in segments).strip()
        # Whisper's known inventions on near-silence (it was trained on subtitled video): a caption line nobody said
        # is worse than none, so these alone are dropped.
        return "" if text.strip(" .!؟?،,").lower() in _CAPTION_NOISE else text
    finally:
        _lock.release()


# Only lines nobody says alone in a call — "thank you", «شكراً», "okay" are real and stay.
_CAPTION_NOISE = {
    "you", "thanks for watching", "thank you for watching", "please subscribe", "subscribe to the channel",
    "ترجمة نانسي قنقر", "اشتركوا في القناة", "شكرا على المشاهدة", "شكراً على المشاهدة", "شكرا للمشاهدة",
    "موسيقى", "[موسيقى]", "music", "[music]",
}


def transcribe(audio: bytes, size: str, language: str | None = None) -> Transcript:
    """The words in one recording. language=None lets Whisper detect it (Arabic and English both occur)."""
    from faster_whisper.audio import decode_audio

    samples = decode_audio(io.BytesIO(audio), sampling_rate=16000)   # once: its length chooses the weights
    seconds = len(samples) / 16000.0
    if seconds > MAX_SECONDS:
        raise TooLong(seconds)
    with _lock:
        started = time.perf_counter()
        chosen = _pick(size, seconds)
        model = _load(chosen)
        segments, info = model.transcribe(
            samples,
            language=language or None,
            beam_size=5,
            vad_filter=True,                       # silence is skipped, not hallucinated into words
            condition_on_previous_text=False,      # a stuck phrase cannot repeat itself to the end
        )
        text = " ".join(s.text.strip() for s in segments).strip()
        return Transcript(
            text=text,
            language=info.language,
            language_probability=round(float(info.language_probability), 3),
            duration=round(float(info.duration), 2),
            seconds=round(time.perf_counter() - started, 2),
            model=f"whisper-{chosen}",
        )
