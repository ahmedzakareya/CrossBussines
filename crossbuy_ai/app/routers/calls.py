"""Call summaries router — a recorded call's words and summary, made in the background (app/ml/calls.py).

All POST: .NET's one AI client posts, and every call it makes passes the egress policy first. The flow:
  /calls/jobs          {language?, participants[]}   -> {job}
  /calls/jobs/chunk    {job, index, dataBase64}      -> {bytes}      (in order, each piece under 2 MB)
  /calls/jobs/start    {job}                         -> {status}
  /calls/jobs/status   {job}                         -> {status, progress, transcript?, summary?}
  /calls/jobs/forget   {job}                         -> {}           (the audio and the words are deleted here)
"""
import binascii

from fastapi import APIRouter, Depends, HTTPException
from pydantic import BaseModel, Field

from app.ml import calls
from app.security import require_secret

router = APIRouter(prefix="/calls/jobs", tags=["calls"], dependencies=[Depends(require_secret)])


class CreateRequest(BaseModel):
    language: str | None = Field(default=None, max_length=8)
    participants: list[str] = Field(default_factory=list, max_length=12)


class JobRequest(BaseModel):
    job: str = Field(min_length=32, max_length=32)


class ChunkRequest(JobRequest):
    index: int = Field(ge=0)
    dataBase64: str = Field(min_length=4)


def _job(fn, *a):
    try:
        return fn(*a)
    except KeyError:
        raise HTTPException(status_code=404, detail="no_such_job")
    except (ValueError, binascii.Error) as e:
        raise HTTPException(status_code=409, detail=str(e) or "bad_request")


@router.post("")
async def create(body: CreateRequest) -> dict:
    return {"job": calls.create(body.language, [p[:80] for p in body.participants])}


@router.post("/chunk")
async def chunk(body: ChunkRequest) -> dict:
    st = _job(calls.append, body.job, body.index, body.dataBase64)
    return {"bytes": st["bytes"], "chunks": st["chunks"]}


@router.post("/start")
async def start(body: JobRequest) -> dict:
    return {"status": _job(calls.start, body.job)["status"]}


@router.post("/status")
async def status(body: JobRequest) -> dict:
    return _job(calls.status, body.job)


@router.post("/forget")
async def forget(body: JobRequest) -> dict:
    _job(calls.forget, body.job)
    return {}
