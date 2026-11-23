"""THE ASSISTANT ON THE GRAPHICS CARD (owner, 2026-11-23: "معاك الاذن").

The machine has an Intel Arc 140T (no NVIDIA), and the Python llama.cpp package here is built for the CPU only. The
Vulkan build of llama.cpp's own server runs the SAME model on that card. Measured with llama-bench on this machine
(Qwen3-4B Q4_K_M): reading 618 tokens/s on the card against 46 on the CPU, writing 12.6 against 8.1 — a summary of
~4,500 tokens in and ~300 out goes from about 135 s to about 30 s.

So the summaries, the assistant and translation (calls._engine) ask that server when it is there:
  * The AI service STARTS it itself, as its own child process, on first use — no service or startup task of its own.
    It listens on 127.0.0.1 only and is stopped when the AI service stops.
  * Same model file, same 8k context, one completion at a time (calls._llm_lock still serialises callers).
  * Anything wrong — no executable, no card, the server will not start or dies later — and the caller gets the CPU
    engine exactly as before. A faster path must never be the reason the assistant stops answering.

CROSSBUY_LLAMA_SERVER: the llama-server.exe to use; "off" keeps everything on the CPU.
"""
from __future__ import annotations

import atexit
import json
import logging
import os
import subprocess
import threading
import time
import urllib.error
import urllib.request
from pathlib import Path

log = logging.getLogger("crossbuy.gpu_llm")

DEFAULT_EXE = r"C:\CrossBuy\engines\llama-b12211-vulkan\llama-server.exe"
PORT = int(os.environ.get("CROSSBUY_LLAMA_SERVER_PORT", "8091"))
START_WAIT = 120          # seconds for the weights to load onto the card
REQUEST_TIMEOUT = 900     # one completion, at most

_proc: subprocess.Popen | None = None
_lock = threading.Lock()
_failed_at = 0.0          # when the server last failed: try again only after a while


def _exe() -> str | None:
    v = os.environ.get("CROSSBUY_LLAMA_SERVER", DEFAULT_EXE).strip()
    if v.lower() in ("", "off", "0", "false", "no"):
        return None
    return v if Path(v).is_file() else None


def _get(path: str, timeout: float = 3) -> int:
    try:
        with urllib.request.urlopen(f"http://127.0.0.1:{PORT}{path}", timeout=timeout) as r:
            return r.status
    except urllib.error.HTTPError as e:
        return e.code
    except Exception:
        return 0


def _stop() -> None:
    global _proc
    p, _proc = _proc, None
    if p and p.poll() is None:
        try:
            p.terminate(); p.wait(timeout=10)
        except Exception:
            try: p.kill()
            except Exception: pass


atexit.register(_stop)


def server(model_path: str) -> "ServerLlm | None":
    """The card's engine, started if needed; None when it cannot be had (the caller uses the CPU)."""
    global _proc, _failed_at
    exe = _exe()
    if not exe:
        return None
    with _lock:
        if _proc is not None and _proc.poll() is None and _get("/health") == 200:
            return ServerLlm()
        if time.time() - _failed_at < 300:      # it failed a moment ago: not again on every request
            return None
        _stop()
        args = [exe, "-m", model_path, "--host", "127.0.0.1", "--port", str(PORT), "-ngl", "99", "-c", "8192",
                "--parallel", "1", "--no-webui"]
        try:
            flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
            _proc = subprocess.Popen(args, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=flags)
        except Exception as e:
            log.warning("gpu_llm: could not start %s: %s", exe, e)
            _failed_at = time.time(); return None
        t0 = time.time()
        while time.time() - t0 < START_WAIT:
            if _proc.poll() is not None:
                break
            if _get("/health") == 200:
                log.info("gpu_llm: llama-server on the graphics card is ready in %.1fs (port %d)", time.time() - t0, PORT)
                return ServerLlm()
            time.sleep(1)
        log.warning("gpu_llm: llama-server did not become ready; the CPU engine is used")
        _stop(); _failed_at = time.time()
        return None


def mark_failed() -> None:
    global _failed_at
    with _lock:
        _stop(); _failed_at = time.time()


class ServerLlm:
    """The same call shape as llama_cpp.Llama.create_chat_completion, answered by the server on the card."""

    # WHY NOT THE SCHEMA'S GRAMMAR. With a JSON schema the server constrains every byte, and Arabic (several bytes a
    # letter) then comes out a piece of a letter at a time: one short summary took 530 tokens and 32 s. Asked for plain
    # JSON with the shape written into the instructions, the same answer took 121 tokens and 6 s. So: plain JSON
    # (still always valid JSON), its shape checked here, and the strict schema only as the retry for a wrong shape.
    def create_chat_completion(self, messages, response_format=None, temperature=0.2, max_tokens=900, **_):
        schema = (response_format or {}).get("schema") if isinstance(response_format, dict) else None
        if not schema:
            return self._post(messages, response_format, temperature, max_tokens)
        shaped = [dict(m) for m in messages]
        note = "\n\nReply with ONE JSON object only, of exactly this JSON schema:\n" + json.dumps(schema, ensure_ascii=False)
        if shaped and shaped[0].get("role") == "system":
            shaped[0]["content"] = str(shaped[0].get("content", "")) + note
        else:
            shaped.insert(0, {"role": "system", "content": note.strip()})
        out = self._post(shaped, {"type": "json_object"}, temperature, max_tokens)
        try:
            if _fits(json.loads(out["choices"][0]["message"]["content"]), schema):
                return out
        except Exception:
            pass
        log.info("gpu_llm: the answer did not fit its schema; asked again with the schema enforced")
        return self._post(messages, response_format, temperature, max_tokens)

    def _post(self, messages, response_format, temperature, max_tokens):
        body = {"messages": messages, "temperature": temperature, "max_tokens": max_tokens, "stream": False}
        if response_format:
            body["response_format"] = response_format
        req = urllib.request.Request(f"http://127.0.0.1:{PORT}/v1/chat/completions", data=json.dumps(body).encode("utf-8"),
                                     headers={"Content-Type": "application/json"}, method="POST")
        with urllib.request.urlopen(req, timeout=REQUEST_TIMEOUT) as r:
            return json.loads(r.read().decode("utf-8"))


def _fits(value, schema: dict) -> bool:
    """Enough of JSON Schema for the shapes the assistant asks for: type, required, properties, items."""
    t = schema.get("type")
    if t == "object":
        if not isinstance(value, dict):
            return False
        if any(k not in value for k in schema.get("required", [])):
            return False
        props = schema.get("properties", {})
        return all(_fits(value[k], s) for k, s in props.items() if k in value and isinstance(s, dict))
    if t == "array":
        return isinstance(value, list) and all(_fits(v, schema.get("items", {})) for v in value)
    if t == "string":
        return isinstance(value, str)
    if t in ("integer", "number"):
        return isinstance(value, (int, float)) and not isinstance(value, bool)
    if t == "boolean":
        return isinstance(value, bool)
    return True
