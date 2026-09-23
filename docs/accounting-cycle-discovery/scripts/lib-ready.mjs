// Readiness, capture and evidence helpers for the accounting-cycle discovery.
//
// The previous package shipped loading screens as if they were evidence. Nothing here writes a
// PNG until the page has actually settled AND the expected thing is on it, and every capture
// records the checks that passed so the manifest can be audited rather than trusted.

import fs from 'node:fs'
import path from 'node:path'
import crypto from 'node:crypto'

export const BASE = process.env.CB_BASE || 'http://localhost:5299'
export const OUT = 'C:/CrossBuy/CrossBuy/docs/accounting-cycle-discovery/screenshots'
export const EVID = 'C:/CrossBuy/CrossBuy/docs/accounting-cycle-discovery/reference'

const USER = process.env.CB_USER
const PASS = process.env.CB_PASS

export async function signIn(page) {
  if (!USER || !PASS) throw new Error('Set CB_USER and CB_PASS in the environment.')
  await page.goto(BASE + '/Account/Login', { waitUntil: 'domcontentloaded', timeout: 60000 })
  await page.locator('input[name="UserName"], input[type="text"]').first().fill(USER)
  await page.locator('input[type="password"]').first().fill(PASS)
  await page.locator('button[type="submit"], input[type="submit"]').first().click()
  await page.waitForTimeout(2500)
  return page.url()
}

export async function setCulture(page, lang) {
  try {
    await page.goto(`${BASE}/Account/SetLanguage?culture=${lang}&returnUrl=%2FPortal%2FChoose`,
      { waitUntil: 'domcontentloaded', timeout: 30000 })
  } catch (e) { /* the redirect can abort; the cookie is still set */ }
  await page.waitForTimeout(700)
  await page.goto(BASE + '/Portal/Choose', { waitUntil: 'domcontentloaded', timeout: 45000 })
  const got = await page.evaluate(() => document.documentElement.getAttribute('lang'))
  if (got !== lang) throw new Error(`culture did not switch: wanted ${lang}, got ${got}`)
  return got
}

// ---------------------------------------------------------------------------------------------
// THE READINESS GATE.
//
// Returns a record of every check, so a capture can never be counted as good merely because a
// file was produced. `expect` is the selector or text that proves the intended content arrived.
export async function ready(page, { expect = null, expectText = null, minChars = 400,
                                    settleMs = 900, timeout = 30000 } = {}) {
  const checks = {}
  const t0 = Date.now()

  // 1. the document itself
  try {
    await page.waitForLoadState('domcontentloaded', { timeout })
    checks.domContentLoaded = true
  } catch (e) { checks.domContentLoaded = false }

  // 2. no network in flight (best effort - an app that polls never goes idle)
  try {
    await page.waitForLoadState('networkidle', { timeout: 8000 })
    checks.networkIdle = true
  } catch (e) { checks.networkIdle = 'timed out (page may poll); continuing' }

  // 3. every spinner / overlay / skeleton gone
  try {
    await page.waitForFunction(() => {
      const sel = ['.spinner-border', '.spinner-grow', '.page-loader', '.overlay-block',
                   '[data-kt-indicator="on"]', '.skeleton', '.blockUI', '#splash']
      for (const s of sel) {
        for (const el of document.querySelectorAll(s)) {
          const cs = getComputedStyle(el)
          if (cs.display !== 'none' && cs.visibility !== 'hidden' && el.offsetParent !== null) return false
        }
      }
      return true
    }, { timeout: 15000 })
    checks.noSpinners = true
  } catch (e) { checks.noSpinners = false }

  // 3b. ANIMATED COUNTERS MUST HAVE FINISHED.
  //
  // The accounting summary tiles use Metronic's data-kt-countup, which animates from zero to the
  // real figure. A screenshot taken while it is still counting shows a NUMBER THAT WAS NEVER
  // TRUE: the first trial-balance capture recorded 113,047,014.46 in the tiles against
  // 113,047,021.50 in the table footer, and both are rendered from the same model property. That
  // is a capture defect, not a product defect, and a reconciliation workbook built on it would
  // have chased a 7.04 difference that does not exist. Wait until every counter's text has
  // stopped changing.
  try {
    await page.waitForFunction(() => {
      const els = Array.from(document.querySelectorAll('[data-kt-countup]'))
      if (!els.length) return true
      const now = els.map(e => (e.textContent || '').trim()).join('|')
      const prev = window.__cbCountupPrev
      window.__cbCountupPrev = now
      const stable = (window.__cbCountupStable || 0)
      window.__cbCountupStable = (prev === now) ? stable + 1 : 0
      return window.__cbCountupStable >= 3
    }, { timeout: 15000, polling: 250 })
    checks.countersSettled = true
  } catch (e) { checks.countersSettled = false }

  // 4. the expected content actually rendered
  if (expect) {
    try { await page.waitForSelector(expect, { state: 'visible', timeout }); checks.expectSelector = expect }
    catch (e) { checks.expectSelector = `MISSING: ${expect}` }
  }
  if (expectText) {
    try {
      await page.waitForFunction(t => (document.body.innerText || '').includes(t), expectText, { timeout })
      checks.expectText = expectText
    } catch (e) { checks.expectText = `MISSING: ${expectText}` }
  }

  // 5. the page is not the sign-in page, an error page, or empty
  const st = await page.evaluate(() => ({
    url: location.pathname + location.search,
    title: document.title,
    heading: (document.querySelector('h1, .page-heading') || {}).textContent?.trim().slice(0, 140) || null,
    chars: (document.body.innerText || '').length,
    lang: document.documentElement.getAttribute('lang'),
    dir: document.documentElement.getAttribute('dir') || getComputedStyle(document.body).direction,
    devOverlay: !!document.querySelector('#browserLink,.browser-link,#aspnetcore-browser-refresh,.dev-banner'),
    visibleSpinners: document.querySelectorAll('.spinner-border:not(.d-none)').length,
  }))
  checks.notSignIn = !/\/Account\/Login/i.test(st.url)
  checks.notErrorPage = !/\/Home\/Error|\/Shared\/Error/i.test(st.url)
  checks.hasContent = st.chars >= minChars
  checks.noDevOverlay = !st.devOverlay
  checks.settleMs = settleMs
  await page.waitForTimeout(settleMs)
  checks.elapsedMs = Date.now() - t0

  const ok = checks.domContentLoaded && checks.noSpinners !== false &&
             checks.countersSettled !== false && checks.notSignIn &&
             checks.notErrorPage && checks.hasContent && checks.noDevOverlay &&
             !String(checks.expectSelector || '').startsWith('MISSING') &&
             !String(checks.expectText || '').startsWith('MISSING')

  return { ok, checks, state: st }
}

// ---------------------------------------------------------------------------------------------
// CAPTURE. Refuses to write a file that failed the gate unless it is a deliberate error state.
const shots = []

export async function shot(page, id, opts = {}) {
  const { lang = 'en', scenario = '', step = '', route = '', role = 'ChiefAccountant/admin',
          recordId = '', beforeAfter = '', intentionalErrorState = false,
          expect = null, expectText = null, minChars = 400, fullPage = false } = opts

  const r = await ready(page, { expect, expectText, minChars })
  const file = `${id}.${lang}.png`
  const full = path.join(OUT, file)
  fs.mkdirSync(OUT, { recursive: true })

  let written = false, sha = null, bytes = 0
  if (r.ok || intentionalErrorState) {
    await page.screenshot({ path: full, fullPage })
    const buf = fs.readFileSync(full)
    sha = crypto.createHash('sha256').update(buf).digest('hex')
    bytes = buf.length
    written = true
  }

  const rec = {
    screenshot_id: id, scenario, step, language: lang, route: route || r.state.url,
    role, record_id: recordId, before_after: beforeAfter,
    filename: written ? file : '', viewport: '1600x1000',
    capture_time: new Date().toISOString(),
    readiness_ok: r.ok, readiness_checks: r.checks,
    page_title: r.state.title, heading: r.state.heading, html_lang: r.state.lang,
    direction: r.state.dir, body_chars: r.state.chars,
    intentional_error_state: intentionalErrorState,
    sha256: sha, bytes,
    privacy_treatment: 'synthetic data on an isolated disposable database (CrossBuyAcctTest)',
    evidence: written ? 'runtime observed' : 'NOT CAPTURED — readiness gate failed',
  }
  shots.push(rec)
  return rec
}

export function saveShots(tag) {
  fs.mkdirSync(EVID, { recursive: true })
  const p = path.join(EVID, `capture-log.${tag}.json`)
  fs.writeFileSync(p, JSON.stringify(shots, null, 1), 'utf8')
  return { file: p, count: shots.length, captured: shots.filter(s => s.filename).length }
}

export function note(tag, data) {
  fs.mkdirSync(EVID, { recursive: true })
  const p = path.join(EVID, `${tag}.json`)
  fs.writeFileSync(p, JSON.stringify(data, null, 1), 'utf8')
  return p
}
