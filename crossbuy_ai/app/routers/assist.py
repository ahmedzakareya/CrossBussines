"""Assistant router — the conversation's summary and questions (background jobs), and message translation.

Called only by .NET, after it has checked the caller may read the conversation and the egress policy approved the
purpose (ChatAssistance, free text, Internal only). All POST, like every route .NET's one AI client calls.
  /assist/jobs           {mode: summary|ask, language, participants[], messages[{id,sender,text}], question?} -> {job}
  /assist/jobs/status    {job} -> {status: working|done|failed, result?, error?}   (a finished job is read once)
  /assist/translate      {text, to: ar|en} -> {text}   (503 when the model stays busy)
"""
from fastapi import APIRouter, Depends, HTTPException
from fastapi.concurrency import run_in_threadpool
from pydantic import BaseModel, Field

from app.ml import assist
from app.security import require_secret

router = APIRouter(prefix="/assist", tags=["assist"], dependencies=[Depends(require_secret)])


class Msg(BaseModel):
    id: int
    sender: str = ""
    text: str = ""


class JobRequest(BaseModel):
    mode: str = Field(pattern="^(summary|ask|translate)$")   # translate: messages[0].text into `language`
    language: str = Field(default="ar", max_length=8)
    participants: list[str] = Field(default_factory=list, max_length=50)
    messages: list[Msg] = Field(max_length=2000)
    question: str | None = Field(default=None, max_length=500)


class JobRef(BaseModel):
    job: str = Field(min_length=32, max_length=32)


class TranslateRequest(BaseModel):
    text: str = Field(max_length=4000)
    to: str = Field(pattern="^(ar|en)$")


@router.post("/jobs")
async def start(body: JobRequest) -> dict:
    msgs = [m.model_dump() for m in body.messages]
    return {"job": assist.start(body.mode, msgs, "ar" if body.language.startswith("ar") else "en", [p[:80] for p in body.participants], body.question)}


@router.post("/jobs/status")
async def status(body: JobRef) -> dict:
    try:
        return assist.status(body.job)
    except KeyError:
        raise HTTPException(status_code=404, detail="no_such_job")


@router.post("/translate")
async def translate(body: TranslateRequest) -> dict:
    t = await run_in_threadpool(assist.translate, body.text, body.to)
    if t is None:
        raise HTTPException(status_code=503, detail="busy")
    return {"text": t}
