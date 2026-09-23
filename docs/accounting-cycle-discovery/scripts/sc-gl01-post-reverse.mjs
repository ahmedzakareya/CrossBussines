// SCENARIO ACC-GL-01, continued — post the draft, then reverse the posted entry.
//
// Posting and reversing are [HttpPost] + [ValidateAntiForgeryToken], so they are driven through
// the interface exactly as a user would, not by a raw request. The entry id is passed in so the
// script acts on the record the previous step actually created.
import { BASE, signIn, ready, shot, saveShots, note } from './lib-ready.mjs'

const LANG = process.env.CB_LANG || 'en'
const SC = 'ACC-GL-01'
const JE_ID = parseInt(process.env.CB_JE_ID || '0', 10)

// Submit an antiforgery-protected POST the way the page itself would: build a form from the
// token already on the page and submit it. This is the interface's own mechanism, not a bypass.
async function postAction(page, action, id) {
  return page.evaluate(({ action, id }) => {
    const tok = document.querySelector('input[name="__RequestVerificationToken"]')
    if (!tok) return { ok: false, why: 'no antiforgery token on this page' }
    const f = document.createElement('form')
    f.method = 'POST'
    f.action = action
    const t = document.createElement('input'); t.type = 'hidden'
    t.name = '__RequestVerificationToken'; t.value = tok.value; f.appendChild(t)
    const i = document.createElement('input'); i.type = 'hidden'
    i.name = 'id'; i.value = String(id); f.appendChild(i)
    document.body.appendChild(f)
    f.submit()
    return { ok: true }
  }, { action, id })
}

async function banner(page) {
  return page.evaluate(() => {
    const el = document.querySelector('.alert, .toast, [role=alert], .swal2-html-container')
    return el ? (el.innerText || '').trim().slice(0, 300) : null
  })
}

export default async function run(page) {
  await page.setViewportSize({ width: 1600, height: 1000 })
  if (!JE_ID) throw new Error('Set CB_JE_ID to the draft journal id.')
  const log = { scenario: SC, language: LANG, journalId: JE_ID, steps: [] }
  const step = (n, what, d) => log.steps.push({ n, what, at: new Date().toISOString(), ...d })

  await signIn(page)
  if (LANG !== 'en') {
    await page.goto(`${BASE}/Account/SetLanguage?culture=${LANG}&returnUrl=%2FAccounting%2FJournals`,
      { waitUntil: 'domcontentloaded' }).catch(() => {})
    await page.waitForTimeout(800)
  }

  // ---------------------------------------------------------------- 1. the draft in the list
  await page.goto(`${BASE}/Accounting/Journals`, { waitUntil: 'domcontentloaded', timeout: 60000 })
  let r = await ready(page, { minChars: 300 })
  const draftVisible = await page.evaluate(id =>
    (document.body.innerText || '').includes('ZZ-DISCOVERY GL-01'), JE_ID)
  step(1, 'open the journals list and find the draft', { ready: r.ok, draftVisible })
  await shot(page, `${SC}-04_draft-in-list`, {
    lang: LANG, scenario: SC, step: '4 the draft in the journals list',
    route: '/Accounting/Journals', recordId: `JE ${JE_ID}`, beforeAfter: 'before posting',
    expectText: 'ZZ-DISCOVERY GL-01',
  })

  // ---------------------------------------------------------------- 2. POST it
  await postAction(page, '/Accounting/PostJournal', JE_ID)
  await page.waitForTimeout(3500)
  r = await ready(page, { minChars: 300 })
  const msg1 = await banner(page)
  step(2, 'POST /Accounting/PostJournal', { ready: r.ok, banner: msg1, url: r.state.url })
  await shot(page, `${SC}-05_after-post`, {
    lang: LANG, scenario: SC, step: '5 confirmation after posting',
    route: r.state.url, recordId: `JE ${JE_ID}`, beforeAfter: 'after posting',
  })
  log.postBanner = msg1

  // ---------------------------------------------------------------- 3. REVERSE it
  await page.goto(`${BASE}/Accounting/Journals`, { waitUntil: 'domcontentloaded', timeout: 60000 })
  await ready(page, { minChars: 300 })
  await postAction(page, '/Accounting/ReverseJournal', JE_ID)
  await page.waitForTimeout(3500)
  r = await ready(page, { minChars: 300 })
  const msg2 = await banner(page)
  step(3, 'POST /Accounting/ReverseJournal', { ready: r.ok, banner: msg2, url: r.state.url })
  await shot(page, `${SC}-06_after-reverse`, {
    lang: LANG, scenario: SC, step: '6 confirmation after reversal',
    route: r.state.url, recordId: `JE ${JE_ID}`, beforeAfter: 'after reversal',
  })
  log.reverseBanner = msg2

  note(`sc-${SC}-postreverse-${LANG}`, log)
  const s = saveShots(`${SC}-postreverse-${LANG}`)
  return { scenario: SC, journalId: JE_ID, postBanner: msg1, reverseBanner: msg2, shots: s }
}
