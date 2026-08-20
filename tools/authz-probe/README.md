# Authorization probe

Asserts what the **server actually answered** for a protected endpoint — not what the page rendered.

```
UI_USER=you@example.com UI_PASS=... node tools/authz-probe/authz-probe.mjs
```

| | |
|---|---|
| Library | `playwright` (reused from `tools/ui-conformance/node_modules` via `NODE_PATH`) |
| Browser | Chromium, `headless: false` — a real visible window |
| Viewport | 1680 × 1050 |
| Profile | `launchPersistentContext` against `chrome-profile/`, so the signed-in session is reused |
| Target | `UI_BASE_URL`, default `https://localhost:44368` |
| Evidence | `shots-authz/` + `responses.json` (every status the session saw) |

It exits `NOT_AUTHENTICATED` rather than silently measuring the login page — where every
endpoint would look correctly denied and the run would pass while proving nothing.

## It never writes

Not as a promise, as structure:

1. Mutating probes are requests the server must refuse, and refusal happens in the
   authorization filter before any handler touches data.
2. They additionally target a deliberately non-existent id, so even in the failure case this
   exists to detect — authorization silently allowing the call — the handler finds no row.
3. No probe ever sends a well-formed payload for a real record.

The authorized-ALLOW path on a mutating endpoint is **never** sent. A POST that authorization
permits would write to real data, so it is listed under *not exercised* instead.

## It reports what it did not do

Every run ends with a `NOT EXERCISED` list. A gate that prints `12/12` without naming its
skips is how a suite goes green while missing things.
