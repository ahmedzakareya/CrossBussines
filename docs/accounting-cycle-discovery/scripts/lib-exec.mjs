// Execution helpers: submit an antiforgery-protected form the way the page itself would.
//
// These are NOT a bypass. Every action driven here is a [HttpPost][ValidateAntiForgeryToken]
// endpoint that the interface reaches with exactly this shape; the token is taken from the page
// the user would be standing on. Doing it this way makes a long connected scenario reproducible,
// which clicking through 40 modals by hand would not be.
import { BASE, ready } from './lib-ready.mjs'

// Navigate somewhere that carries an antiforgery token, then POST `fields` to `action`.
export async function formPost(page, { tokenFrom, action, fields }) {
  await page.goto(BASE + tokenFrom, { waitUntil: 'domcontentloaded', timeout: 60000 })
  await ready(page, { minChars: 200 })
  const r = await page.evaluate(({ action, fields }) => {
    const tok = document.querySelector('input[name="__RequestVerificationToken"]')
    if (!tok) return { ok: false, why: 'no antiforgery token on ' + location.pathname }
    const f = document.createElement('form')
    f.method = 'POST'; f.action = action
    const add = (n, v) => {
      const i = document.createElement('input')
      i.type = 'hidden'; i.name = n; i.value = v == null ? '' : String(v)
      f.appendChild(i)
    }
    add('__RequestVerificationToken', tok.value)
    for (const [k, v] of Object.entries(fields)) add(k, v)
    document.body.appendChild(f)
    f.submit()
    return { ok: true }
  }, { action, fields })
  if (!r.ok) return { ok: false, why: r.why }
  await page.waitForTimeout(3000)
  const st = await ready(page, { minChars: 200 })
  return {
    ok: true,
    landed: st.state.url,
    banner: await page.evaluate(() => {
      const el = document.querySelector('.alert, [role=alert], .toast-body, .swal2-html-container')
      return el ? (el.innerText || '').trim().replace(/\s+/g, ' ').slice(0, 300) : null
    }),
    readiness: st.ok,
  }
}

// A compact record of one executed step, for the scenario log.
export function stepRec(n, what, input, result, extra = {}) {
  return {
    n, what, at: new Date().toISOString(),
    input, landed: result?.landed ?? null, banner: result?.banner ?? null,
    ok: result?.ok ?? false, ...extra,
  }
}
