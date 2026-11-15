# The local language model is OFF, and why

`INTENT_LLM_ENABLED=false`. The weights are still in `models/`, the code is still wired, every
test still passes. It is switched off because it was measured and did not earn its place.

## What it was for

The tail of the question nobody anticipated: when the trained classifier and the nearest-neighbour
matcher both decline, a 4B GGUF model composes the intent or the query instead of refusing.

## What it actually did

| | trained classifier | local 4B |
|---|---|---|
| hand-written benchmark | 43 of 45 | 25 of 27 |
| answered the wrong one | 0 | 0 |
| answered one we cannot | 0 | 0 |
| latency, idle machine | 11 ms | ~1 s |
| latency, while CrossBuy builds | 11 ms | 30 s – 190 s |

Across every live probe of the running product it answered **nothing the cheaper engines had not
already answered**, and it repeatedly turned a refusal into a two-minute silence. "انت معايا؟" —
a greeting — cost 123 seconds, because an unmatched question walks the whole chain and the last
link is the model.

## Why not just make it faster

Tried. A prompt cache (`LlamaRAMCache`) barely moved it, because the cost is not prompt evaluation
on an idle box — it is sixteen cores shared with a .NET build. The honest conclusion was that it
does not belong in a request path on this machine, not that it needed tuning.

## What it cost to find out

Two production faults, both of which only appear when it runs:

- Concurrent use **aborts the process** (`GGML_ASSERT` in llama-kv-cache). One `Llama` context is
  not thread-safe and the failure is not an exception — the whole AI service died, every ML
  endpoint with it. Fixed with a single inference lock and one entry point.
- A loading model **queues every caller behind 2.3 GB**. Fixed with a readiness flag and a bounded
  wait, so "not ready" is an immediate no.

Both fixes are worth keeping whatever happens to the switch.

## When to turn it back on

- A machine where it is not competing with the build — a server, or a GPU.
- Or evidence that real users ask questions the classifier refuses AND the model would answer.
  That evidence does not exist yet; the right way to get it is to log refused questions, which is
  not built.

Turning it on is one line here. Turning it on without one of those two reasons would be putting a
two-minute pause back into a chat box.
