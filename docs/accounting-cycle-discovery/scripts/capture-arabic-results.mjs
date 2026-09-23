// Arabic evidence for the executed scenarios.
//
// The transactions were executed once, in English. Re-executing them in Arabic would create
// duplicate documents and corrupt the worked-example ledger, so instead the RESULT screens are
// captured in Arabic: they show the SAME records, in the Arabic interface. That is genuine
// bilingual evidence of the same accounting state, not a second set of transactions.
import { BASE, signIn, ready, shot, saveShots, note } from './lib-ready.mjs'

const SCREENS = [
  ['/Accounting/PurchaseInvoices', 'ACC-P2P-01-01_invoice-posted', 'ACC-P2P-01', 'purchase invoices incl. PI-2026-05073/74'],
  ['/Accounting/Payments', 'ACC-P2P-02-01_partial-payment', 'ACC-P2P-02', 'payments incl. PY-2026-00008/9'],
  ['/Accounting/ApAging', 'ACC-P2P-02-02_remaining-balance', 'ACC-P2P-02', 'supplier aging after settlement'],
  ['/Accounting/PurchaseReturns', 'ACC-P2P-05-01_purchase-return', 'ACC-P2P-05', 'purchase return DN-2026-03016'],
  ['/Accounting/SalesInvoices', 'ACC-O2C-01-01_invoice-posted', 'ACC-O2C-01', 'sales invoices incl. SV-2026-17613'],
  ['/Accounting/Receipts', 'ACC-O2C-02-01_partial-receipt', 'ACC-O2C-02', 'receipts incl. RC-2026-15485/6'],
  ['/Accounting/ArAging', 'ACC-O2C-02-02_remaining-balance', 'ACC-O2C-02', 'customer aging after collection'],
  ['/Accounting/SalesReturns', 'ACC-O2C-03-01_sales-return', 'ACC-O2C-03', 'sales return CN-2026-04050'],
  ['/Accounting/Journals', 'ACC-GL-02_journal-register', 'ACC-GL-01', 'the register showing every posting'],
  ['/Accounting/FixedAssets', 'ACC-FA-01-01_asset-created', 'ACC-FA-01', 'the capitalised asset'],
  ['/Accounting/DepreciationRuns', 'ACC-FA-02-01_depreciation-run', 'ACC-FA-02', 'depreciation runs'],
  ['/Accounting/Transfer', 'ACC-BNK-01-01_transfer', 'ACC-BNK-01', 'cash/bank transfer screen'],
  ['/Accounting/Periods', 'ACC-CLS-01-01_periods-before', 'ACC-CLS-01', 'fiscal periods'],
  ['/Accounting/YearEndClose', 'ACC-CLS-02-01_year-end-before', 'ACC-CLS-02', 'year-end close'],
  ['/Currency/Revaluation', 'ACC-FX-01-01_revaluation', 'ACC-FX-01', 'foreign-currency revaluation'],
  ['/Reports/Index', 'ACC-RPT-10_reports-center', 'ACC-REPORTS', 'the reporting catalogue'],
]

export default async function run(page) {
  await page.setViewportSize({ width: 1600, height: 1000 })
  await signIn(page)
  await page.goto(`${BASE}/Account/SetLanguage?culture=ar&returnUrl=%2FAccounting%2FIndex`,
    { waitUntil: 'domcontentloaded' }).catch(() => {})
  await page.waitForTimeout(900)
  await page.goto(BASE + '/Accounting/Index', { waitUntil: 'domcontentloaded', timeout: 60000 })
  const lang = await page.evaluate(() => document.documentElement.getAttribute('lang'))
  if (lang !== 'ar') throw new Error('culture did not switch to ar, got ' + lang)

  const out = []
  for (const [route, id, scenario, what] of SCREENS) {
    try {
      await page.goto(BASE + route, { waitUntil: 'domcontentloaded', timeout: 60000 })
      const rec = await shot(page, id, {
        lang: 'ar', scenario, step: what, route, beforeAfter: 'result (Arabic)',
      })
      out.push({ id, route, captured: !!rec.filename, ready: rec.readiness_ok, dir: rec.direction })
    } catch (e) {
      out.push({ id, route, error: String(e).slice(0, 140) })
      try { await page.goto('about:blank') } catch (e2) { }
    }
  }
  note('arabic-results', out)
  const s = saveShots('arabic-results')
  return { attempted: SCREENS.length, captured: out.filter(o => o.captured).length,
           rtl: out.filter(o => o.dir === 'rtl').length, shots: s }
}
