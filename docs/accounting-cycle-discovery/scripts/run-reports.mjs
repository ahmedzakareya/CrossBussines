// Run every in-module accounting report, capture it in one language, and read its totals back
// out of the DOM so the reconciliation workbook can be built from observed figures rather than
// from an assumption about what the screen shows.
//
// Read-only: these are all GET screens. Nothing is submitted.
import { BASE, signIn, ready, shot, saveShots, note } from './lib-ready.mjs'

const LANG = process.env.CB_LANG || 'en'

// route, id, what it answers, the selector that proves it rendered
const REPORTS = [
  ['/Accounting/TrialBalance',    'ACC-RPT-01_trial-balance',    'Do the books balance?',            'table'],
  ['/Accounting/BalanceSheet',    'ACC-RPT-02_balance-sheet',    'What do we own and owe?',          'table'],
  ['/Accounting/IncomeStatement', 'ACC-RPT-03_income-statement', 'Did we make a profit?',            'table'],
  ['/Accounting/CashFlow',        'ACC-RPT-04_cash-flow',        'Where did the cash go?',           'table'],
  ['/Accounting/ArAging',         'ACC-RPT-05_ar-aging',         'Who owes us, and for how long?',   'table'],
  ['/Accounting/ApAging',         'ACC-RPT-06_ap-aging',         'Whom do we owe, and for how long?','table'],
  ['/Accounting/Journals',        'ACC-RPT-07_journals',         'What was posted, and when?',       'table'],
  ['/Accounting/CustomerStatement','ACC-RPT-08_customer-statement','One customer’s account',    null],
  ['/Accounting/VendorStatement', 'ACC-RPT-09_vendor-statement', 'One supplier’s account',      null],
]

// Pull the numeric shape of the rendered report: headers, row count and any total row.
async function figures(page) {
  return page.evaluate(() => {
    const clean = s => (s || '').replace(/\s+/g, ' ').trim()
    const tables = Array.from(document.querySelectorAll('table')).slice(0, 3).map(t => ({
      headers: Array.from(t.querySelectorAll('thead th')).map(th => clean(th.innerText)).slice(0, 12),
      bodyRows: t.querySelectorAll('tbody tr').length,
      footer: Array.from(t.querySelectorAll('tfoot tr')).map(tr =>
        Array.from(tr.children).map(td => clean(td.innerText))).slice(0, 4),
      lastRows: Array.from(t.querySelectorAll('tbody tr')).slice(-2).map(tr =>
        Array.from(tr.children).map(td => clean(td.innerText)).slice(0, 12)),
    }))
    // any big summary figure on the page (the tiles the accounting screens use)
    const tiles = Array.from(document.querySelectorAll('.fs-lg-2tx, .fs-2tx, .fs-1, .fs-2'))
      .map(e => clean(e.innerText)).filter(t => /[0-9]/.test(t)).slice(0, 8)
    return { tables, tiles, text: clean(document.body.innerText).slice(0, 1200) }
  })
}

export default async function run(page) {
  await page.setViewportSize({ width: 1600, height: 1000 })
  await signIn(page)
  if (LANG !== 'en') {
    await page.goto(`${BASE}/Account/SetLanguage?culture=${LANG}&returnUrl=%2FAccounting%2FIndex`,
      { waitUntil: 'domcontentloaded' }).catch(() => {})
    await page.waitForTimeout(800)
  }

  const out = []
  for (const [route, id, question, expect] of REPORTS) {
    try {
      await page.goto(BASE + route, { waitUntil: 'domcontentloaded', timeout: 60000 })
      const r = await ready(page, { expect, minChars: 300 })
      const f = await figures(page)
      const rec = await shot(page, id, {
        lang: LANG, scenario: 'ACC-REPORTS', step: question, route,
        beforeAfter: 'result', expect,
      })
      out.push({ route, id, question, ready: r.ok, heading: r.state.heading,
                 captured: !!rec.filename, checks: r.checks, figures: f })
    } catch (e) {
      out.push({ route, id, question, error: String(e).slice(0, 200) })
      try { await page.goto('about:blank') } catch (e2) {}
    }
  }

  note(`reports-${LANG}`, out)
  const s = saveShots(`reports-${LANG}`)
  return {
    lang: LANG, attempted: REPORTS.length,
    captured: out.filter(o => o.captured).length,
    notReady: out.filter(o => o.ready === false).map(o => o.route),
    errors: out.filter(o => o.error).map(o => o.route),
    shots: s,
  }
}
