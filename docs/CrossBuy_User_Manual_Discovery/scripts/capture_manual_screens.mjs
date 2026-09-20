// Screenshot capture for the CrossBuy user manual — Arabic and English editions.
//
// READ-ONLY BY CONSTRUCTION. It signs in once and then only navigates. The single form it ever
// submits is the sign-in form. No create, edit, delete, post or approve action is clicked, so no
// business record changes and no transaction is executed.
//
// Per screen it records what the page itself reported, so the manual can cite a measurement rather
// than an impression: HTTP status, final URL after redirects, the document title, direction and
// language, the visible heading, control counts, and an automatic privacy scan of the rendered
// text. Anything the privacy scan flags is written to the manifest for human review rather than
// silently shipped.
import fs from 'node:fs'
import path from 'node:path'

const OUT = 'C:/CrossBuy/CrossBuy/docs/user-manual-discovery/screenshots'
const BASE = process.env.CB_BASE || 'http://localhost:54328'
const ROUTE_FILE = process.env.CB_ROUTES ||
  'C:/Users/Lenovo/AppData/Local/Temp/claude/c--CrossBuy/7d5e59b2-fa47-4fc5-945e-07614ef3105c/scratchpad/routes.json'
const ROUTES = JSON.parse(fs.readFileSync(ROUTE_FILE, 'utf8'))

// A slice can be taken per run so one long run does not have to succeed all at once.
const FROM = parseInt(process.env.CB_FROM || '0', 10)
const TO = parseInt(process.env.CB_TO || String(ROUTES.length), 10)
const LANG = process.env.CB_LANG || 'en'
const USER = process.env.CB_USER
const PASS = process.env.CB_PASS
if (!USER || !PASS) {
  throw new Error('Set CB_USER and CB_PASS in the environment before running this capture.')
}

const slug = r => r.replace(/^\//, '').replace(/[^A-Za-z0-9]+/g, '-').toLowerCase()

// The privacy scan. Development data is still data: an e-mail address or a phone number that
// reached a screenshot has to be visible to a reviewer, not discovered by a reader of the manual.
function privacyScan(text) {
  const flags = []
  const email = text.match(/[\w.+-]+@[\w-]+\.[\w.]{2,}/g)
  const phone = text.match(/\+?\d[\d\s\-()]{8,}\d/g)
  const iban = text.match(/\b[A-Z]{2}\d{2}[A-Z0-9]{10,}\b/g)
  const natid = text.match(/\b\d{14}\b/g)
  if (email) flags.push('email:' + new Set(email).size)
  if (phone) flags.push('phone-like:' + new Set(phone).size)
  if (iban) flags.push('iban-like:' + new Set(iban).size)
  if (natid) flags.push('14-digit-id:' + new Set(natid).size)
  return flags
}

async function measure(page) {
  return page.evaluate(() => {
    const h = document.querySelector('h1, .page-heading')
    const txt = (document.body.innerText || '')
    return {
      title: document.title,
      lang: document.documentElement.getAttribute('lang'),
      dir: document.documentElement.getAttribute('dir') || getComputedStyle(document.body).direction,
      heading: h ? h.textContent.trim().replace(/\s+/g, ' ').slice(0, 120) : null,
      bodyChars: txt.length,
      text: txt.slice(0, 20000),
      inputs: document.querySelectorAll('input:not([type=hidden]),select,textarea').length,
      buttons: document.querySelectorAll('button, a.btn').length,
      tables: document.querySelectorAll('table').length,
      rows: document.querySelectorAll('tbody tr').length,
      modals: document.querySelectorAll('.modal').length,
      // A screen that renders only an empty state is a legitimate manual illustration, but the
      // manual must say so rather than implying the feature has no content.
      emptyState: !!document.querySelector('.text-center.text-muted:not(.d-none)') &&
        document.querySelectorAll('tbody tr').length === 0,
      // Nothing that looks like a developer overlay should reach a published manual.
      devOverlay: !!document.querySelector('#browserLink, .browser-link, #aspnetcore-browser-refresh, .dev-banner'),
    }
  })
}

export default async function run(page) {
  fs.mkdirSync(OUT, { recursive: true })
  await page.setViewportSize({ width: 1440, height: 900 })
  page.setDefaultTimeout(40000)
  const log = []

  // ---- sign in
  //
  // SessionValidation reads a server-side Session key and REDIRECTS TO LOGIN when it is gone.
  // A redirect arriving mid-navigation surfaces as "Navigation to X is interrupted by another
  // navigation to Y", and on the first full run that took out 174 of 305 screens once the
  // session lapsed part-way through. So sign-in is a function, and the loop re-runs it the
  // moment a route bounces to the sign-in page.
  async function signIn() {
    await page.goto(BASE + '/Account/Login', { waitUntil: 'domcontentloaded', timeout: 60000 })
    // Credentials come from the ENVIRONMENT, never from this file: the script ships inside the
    // discovery package and a package that carries a password is a package that leaks one.
    //   set CB_USER / CB_PASS before running.
    await page.locator('input[name="UserName"], input[type="text"]').first().fill(USER)
    await page.locator('input[type="password"]').first().fill(PASS)
    await page.locator('button[type="submit"], input[type="submit"]').first().click()
    await page.waitForTimeout(2500)
  }
  await signIn()
  let reauths = 0

  // ---- set the culture for this pass
  //
  // SetLanguage answers with a redirect, and on the plain-HTTP port that redirect ALSO crosses to
  // the HTTPS origin. Chromium aborts the double hop, which killed the whole Arabic run at its
  // first step. So the switch is attempted, the abort is tolerated, and the culture is then
  // CONFIRMED by reading <html lang> from a real page rather than assumed from the request.
  try {
    await page.goto(BASE + '/Account/SetLanguage?culture=' + LANG + '&returnUrl=%2FPortal%2FChoose',
      { waitUntil: 'domcontentloaded', timeout: 30000 })
  } catch (e) { /* the redirect aborted; the cookie is still set */ }
  await page.waitForTimeout(800)
  try {
    await page.goto(BASE + '/Portal/Choose', { waitUntil: 'domcontentloaded', timeout: 45000 })
  } catch (e) { }
  await page.waitForTimeout(1200)
  const confirmed = await page.evaluate(() => document.documentElement.getAttribute('lang'))
  if (confirmed !== LANG) {
    return { fatal: 'culture did not switch', wanted: LANG, got: confirmed }
  }

  for (const route of ROUTES.slice(FROM, TO)) {
    const file = `${slug(route)}.${LANG}.png`
    const rec = { route, lang: LANG, file }
    try {
      const resp = await page.goto(BASE + route, { waitUntil: 'domcontentloaded', timeout: 45000 })
      rec.status = resp ? resp.status() : null
      await page.waitForTimeout(1600)
      const m = await measure(page)
      rec.finalUrl = page.url().replace(BASE, '')
      rec.redirected = rec.finalUrl.split('?')[0].toLowerCase() !== route.toLowerCase()
      const { text, ...rest } = m
      Object.assign(rec, rest)
      rec.privacyFlags = privacyScan(text)

      // A redirect to sign-in means the route refused us; a picture of the sign-in page filed
      // under another screen's name would be a lie in the manifest.
      if (/\/Account\/Login/i.test(rec.finalUrl) && route !== '/Account/Login') {
        // The session lapsed rather than the route refusing us. Sign in again and try once more.
        reauths++
        await signIn()
        const r2 = await page.goto(BASE + route, { waitUntil: 'domcontentloaded', timeout: 45000 })
        rec.status = r2 ? r2.status() : null
        await page.waitForTimeout(1600)
        const m2 = await measure(page)
        rec.finalUrl = page.url().replace(BASE, '')
        const { text: t2, ...rest2 } = m2
        Object.assign(rec, rest2)
        rec.privacyFlags = privacyScan(t2)
        rec.reauthenticated = true
      }

      if (/\/Account\/Login/i.test(rec.finalUrl)) {
        rec.state = 'refused-redirected-to-signin'
      } else if (rec.status !== 200) {
        rec.state = 'http-' + rec.status
      } else if (rec.bodyChars < 200) {
        rec.state = 'rendered-but-near-empty'
      } else if (m.emptyState) {
        rec.state = 'rendered-empty-state'
      } else {
        rec.state = 'rendered-with-data'
      }

      // 403 IS A USER-FACING STATE, so it is captured too: "Access denied" is a screen the manual
      // has to show, and refusing to photograph it would leave the commonest support question
      // illustrated by nothing.
      if (rec.state !== 'refused-redirected-to-signin' && (rec.status === 200 || rec.status === 403)) {
        await page.screenshot({ path: path.join(OUT, file), fullPage: false })
        rec.captured = true
      } else {
        rec.captured = false
      }
    } catch (e) {
      rec.error = String(e).slice(0, 220)
      // A failed goto leaves a pending navigation that kills every later one.
      try { await page.goto('about:blank', { waitUntil: 'domcontentloaded', timeout: 15000 }) } catch (e2) { }
      // "interrupted by another navigation" is what a mid-flight redirect to the sign-in page
      // looks like from goto()'s side. Re-authenticate and give the route one more chance
      // before recording it as a failure.
      if (/interrupted by another navigation/i.test(rec.error)) {
        try {
          reauths++
          await signIn()
          const r3 = await page.goto(BASE + route, { waitUntil: 'domcontentloaded', timeout: 45000 })
          rec.status = r3 ? r3.status() : null
          await page.waitForTimeout(1600)
          const m3 = await measure(page)
          rec.finalUrl = page.url().replace(BASE, '')
          const { text: t3, ...rest3 } = m3
          Object.assign(rec, rest3)
          rec.privacyFlags = privacyScan(t3)
          rec.reauthenticated = true
          rec.state = rec.status === 200 ? 'rendered-with-data (after re-authentication)'
                                         : 'http-' + rec.status
          if (rec.status === 200 || rec.status === 403) {
            await page.screenshot({ path: path.join(OUT, file), fullPage: false })
            rec.captured = true
            rec.error = null
          }
        } catch (e3) {
          rec.state = 'error'
          rec.captured = false
          try { await page.goto('about:blank', { timeout: 15000 }) } catch (e4) { }
        }
      } else {
        rec.state = 'error'
        rec.captured = false
      }
    }
    log.push(rec)
  }

  const tag = process.env.CB_TAG || `${FROM}-${TO}`
  const out = path.join(OUT, `capture-log.${LANG}.${tag}.json`)
  fs.writeFileSync(out, JSON.stringify(log, null, 1), 'utf8')
  return {
    lang: LANG, attempted: log.length,
    captured: log.filter(r => r.captured).length,
    refused: log.filter(r => r.state === 'refused-redirected-to-signin').length,
    errors: log.filter(r => r.state === 'error').length,
    flagged: log.filter(r => (r.privacyFlags || []).length).length,
    reauthentications: reauths,
    log: out,
  }
}
