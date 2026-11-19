"""Speech router — POST /speech/transcribe: the words in a voice message.

Called only by .NET, after it has decided the caller may read that message and the egress policy has approved the
purpose (SpeechTranscription, PersonalData, Internal only). The audio arrives base64-encoded in JSON so the one
proxy client .NET already has carries it; the payload limit (2 MB) is the policy's.
"""
import base64
import binascii

from fastapi import APIRouter, Depends, HTTPException
from fastapi.concurrency import run_in_threadpool
from pydantic import BaseModel, Field

from app.config import get_settings
from app.ml import speech
from app.security import require_secret

router = APIRouter(prefix="/speech", tags=["speech"], dependencies=[Depends(require_secret)])


class TranscribeRequest(BaseModel):
    audioBase64: str = Field(min_length=16)
    language: str | None = Field(default=None, max_length=8)   # "ar", "en"; empty = detect


class TranscribeResponse(BaseModel):
    text: str
    language: str
    languageProbability: float
    duration: float
    seconds: float
    model: str


@router.get("/status")
async def status() -> dict:
    size = get_settings().whisper_model
    return {"model": f"whisper-{size}", "available": speech.available(size), "maxSeconds": speech.MAX_SECONDS}


@router.post("/transcribe", response_model=TranscribeResponse)
async def transcribe(body: TranscribeRequest) -> TranscribeResponse:
    size = get_settings().whisper_model
    if not speech.available(size):
        raise HTTPException(status_code=503, detail="speech_model_missing")
    try:
        audio = base64.b64decode(body.audioBase64, validate=True)
    except (binascii.Error, ValueError):
        raise HTTPException(status_code=400, detail="audio_not_base64")
    try:
        # in a worker thread: the event loop keeps answering /health and every cheap route meanwhile
        t = await run_in_threadpool(speech.transcribe, audio, size, body.language)
    except speech.TooLong as e:
        raise HTTPException(status_code=413, detail=f"too_long:{e.seconds:.0f}")
    except Exception as e:   # an unreadable file is the caller's problem, not a crash
        raise HTTPException(status_code=422, detail=f"audio_unreadable:{type(e).__name__}")
    return TranscribeResponse(text=t.text, language=t.language, languageProbability=t.language_probability,
                              duration=t.duration, seconds=t.seconds, model=t.model)
