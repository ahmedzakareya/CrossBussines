// ACC-CLS-01 period close, ACC-CLS-02 year-end close, ACC-CLS-03 reopen.
//
// DESTRUCTIVE to later tests: closing a period refuses subsequent postings into it. Run only
// after checkpoint CP2, which can be restored to undo everything here.
//
// The order is deliberate: close a period, PROVE the refusal by attempting a posting into it,
// then reopen, then run the year-end close as a separate scenario.
import { BASE, signIn, ready, shot, saveShots, note } from './lib-ready.mjs'
import { formPost, stepRec } from './lib-exec.mjs'

const LANG = process.env.CB_LANG || 'en'
const PERIOD = parseInt(process.env.CB_PERIOD || '9', 10)   // 2026-09
const FY = parseInt(process.env.CB_FY || '1', 10)           // FY 2026
const DATE = '2026-09-21'

export default async function run(page) {
  await page.setViewportSize({ width: 1600, height: 1000 })
  const log = { language: LANG, periodId: PERIOD, fiscalYearId: FY, steps: [] }
  const S = (...a) => log.steps.push(stepRec(...a))

  await signIn(page)
  if (LANG !== 'en') {
    await page.goto(`${BASE}/Account/SetLanguage?culture=${LANG}&returnUrl=%2FAccounting%2FPeriods`,
      { waitUntil: 'domcontentloaded' }).catch(() => {})
    await page.waitForTimeout(800)
  }

  // ---------------------------------------------------- the period list before closing
  await page.goto(`${BASE}/Accounting/Periods`, { waitUntil: 'domcontentloaded', timeout: 60000 })
  await shot(page, 'ACC-CLS-01-01_periods-before', {
    lang: LANG, scenario: 'ACC-CLS-01', step: '1 fiscal periods before closing',
    route: '/Accounting/Periods', beforeAfter: 'before', recordId: `period ${PERIOD}`,
  })

  // discover the close endpoint the page itself offers
  const endpoints = await page.evaluate(() => {
    const out = new Set()
    document.querySelectorAll('form').forEach(f => { if (f.action) out.add(f.getAttribute('action') || f.action) })
    document.querySelectorAll('[onclick]').forEach(e => {
      const m = (e.getAttribute('onclick') || '').match(/['"](\/[A-Za-z]+\/[A-Za-z]+)['"]/)
      if (m) out.add(m[1])
    })
    return Array.from(out)
  })
  log.discoveredEndpoints = endpoints

  // ---------------------------------------------------- ACC-CLS-01 close the period
  let r = await formPost(page, {
    tokenFrom: '/Accounting/Periods', action: '/Accounting/SetPeriodStatus',
    fields: { id: PERIOD, status: 'Closed', reason: '', overrideWarnings: 'true' },
  })
  S(1, 'ACC-CLS-01 close fiscal period ' + PERIOD, { periodId: PERIOD }, r)
  await shot(page, 'ACC-CLS-01-02_period-closed', {
    lang: LANG, scenario: 'ACC-CLS-01', step: '2 after closing the period', route: r.landed,
    beforeAfter: 'after close', recordId: `period ${PERIOD}`,
  })

  // ---------------------------------------------------- PROVE the refusal
  // A journal dated inside the closed period must now be refused. This is the control the guide
  // needs illustrated, and the only honest way to show it is to trigger it.
  await page.goto(`${BASE}/Accounting/CreateJournal`, { waitUntil: 'domcontentloaded', timeout: 60000 })
  await ready(page, { expect: '#entryDate' })
  await page.fill('#entryDate', DATE)
  await page.fill('#jeForm input[name=description], #jeForm textarea[name=description]',
                  'ZZ-DISCOVERY CLS-01 posting into a CLOSED period (expected to be refused)')
  const rows = await page.evaluate(() => document.querySelectorAll('#rows tr').length)
  for (let i = rows; i < 2; i++) await page.click('#addRow')
  await page.waitForTimeout(300)
  await page.evaluate(() => {
    const rs = document.querySelectorAll('#rows tr')
    const pick = (tr, code) => {
      const sel = tr.querySelector('.acc')
      const o = Array.from(sel.options).find(x => (x.textContent || '').trim().startsWith(code))
      if (o) { sel.value = o.value; sel.dispatchEvent(new Event('change', { bubbles: true })) }
    }
    const amt = (tr, cls, v) => {
      const el = tr.querySelector('.' + cls); el.value = v
      el.dispatchEvent(new Event('input', { bubbles: true })); el.dispatchEvent(new Event('change', { bubbles: true }))
    }
    pick(rs[0], '510105'); amt(rs[0], 'dr', '111')
    pick(rs[1], '110102'); amt(rs[1], 'cr', '111')
  })
  await page.waitForTimeout(400)
  await page.evaluate(() => {
    const b = Array.from(document.querySelectorAll('button'))
      .find(x => (x.getAttribute('onclick') || '').includes("submitJe('post')"))
    b.click()
  })
  await page.waitForTimeout(3500)
  const refusal = await page.evaluate(() => ({
    url: location.pathname,
    text: (document.body.innerText || '').slice(0, 1500),
  }))
  S(2, 'ACC-CLS-01b attempt to POST into the closed period (expected refusal)',
    { date: DATE, amount: 111 }, { ok: true, landed: refusal.url, banner: null },
    { bodyExcerpt: refusal.text.slice(0, 400) })
  await shot(page, 'ACC-CLS-01-03_closed-period-refusal', {
    lang: LANG, scenario: 'ACC-CLS-01', step: '3 refusal when posting into a closed period',
    route: refusal.url, beforeAfter: 'refusal', intentionalErrorState: true,
  })
  log.refusalUrl = refusal.url

  // ---------------------------------------------------- ACC-CLS-03 reopen
  r = await formPost(page, {
    tokenFrom: '/Accounting/Periods', action: '/Accounting/SetPeriodStatus',
    fields: { id: PERIOD, status: 'Open', reason: 'ZZ-DISCOVERY reopened for continued testing' },
  })
  S(3, 'ACC-CLS-03 reopen the period with a recorded reason', { periodId: PERIOD }, r)
  await shot(page, 'ACC-CLS-03-01_period-reopened', {
    lang: LANG, scenario: 'ACC-CLS-03', step: '4 after reopening', route: r.landed,
    beforeAfter: 'after reopen', recordId: `period ${PERIOD}`,
  })

  // ---------------------------------------------------- ACC-CLS-02 year-end close
  await page.goto(`${BASE}/Accounting/YearEndClose`, { waitUntil: 'domcontentloaded', timeout: 60000 })
  await shot(page, 'ACC-CLS-02-01_year-end-before', {
    lang: LANG, scenario: 'ACC-CLS-02', step: '5 year-end close screen', route: '/Accounting/YearEndClose',
    beforeAfter: 'before',
  })
  r = await formPost(page, {
    tokenFrom: '/Accounting/YearEndClose', action: '/Accounting/CloseYear',
    fields: { fiscalYearId: FY },
  })
  S(4, 'ACC-CLS-02 close fiscal year ' + FY, { fiscalYearId: FY }, r)
  await shot(page, 'ACC-CLS-02-02_year-end-after', {
    lang: LANG, scenario: 'ACC-CLS-02', step: '6 after the year-end close', route: r.landed,
    beforeAfter: 'after', recordId: `FY ${FY}`,
  })

  note(`sc-closing-${LANG}`, log)
  const s = saveShots(`closing-${LANG}`)
  return { lang: LANG, endpoints,
           results: log.steps.map(x => `${x.n}. landed=${x.landed}`), shots: s }
}
