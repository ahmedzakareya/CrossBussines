// SCENARIOS ACC-MD-01, ACC-P2P-01..03, ACC-O2C-01..03 — the connected trade cycles.
//
// One coherent synthetic case, executed in dependency order against the ISOLATED instance:
//
//   master data  -> purchase invoice -> partial payment -> final settlement -> purchase return
//                -> sales invoice    -> partial receipt -> final settlement -> sales return
//
// Test assumptions (not statutory advice): VAT 14%, functional currency EGP, all dated inside
// the open period 2026-09.
import { BASE, signIn, ready, shot, saveShots, note } from './lib-ready.mjs'
import { formPost, stepRec } from './lib-exec.mjs'

const LANG = process.env.CB_LANG || 'en'
const DATE = '2026-09-21'
const TAX = 14
const EXPENSE_ACCT = 4072   // 510105 Rent Expense - a postable expense that needs no cost centre
const REVENUE_ACCT = 22     // 4101 Operating Revenue
const CASH_ACCT = 5         // 110102 Bank - Current (cashAccountId is an ACCOUNT id)

export default async function run(page) {
  await page.setViewportSize({ width: 1600, height: 1000 })
  const log = { language: LANG, date: DATE, assumptions: { vatPercent: TAX, currency: 'EGP (functional)' }, steps: [] }
  const S = (...a) => log.steps.push(stepRec(...a))

  await signIn(page)
  if (LANG !== 'en') {
    await page.goto(`${BASE}/Account/SetLanguage?culture=${LANG}&returnUrl=%2FAccounting%2FIndex`,
      { waitUntil: 'domcontentloaded' }).catch(() => {})
    await page.waitForTimeout(800)
  }

  // ============================================================ ACC-MD-01 master data
  const vendorName = 'ZZ-DISCOVERY Supplier Co'
  const custName = 'ZZ-DISCOVERY Customer Co'

  let r = await formPost(page, {
    tokenFrom: '/Accounting/Vendors', action: '/Accounting/SaveVendor',
    fields: { id: 0, name: 'ZZ-DISCOVERY مورّد الاكتشاف', nameEn: vendorName,
              taxRegNo: 'ZZ-TAX-V-001', paymentTermsDays: 30, isActive: true },
  })
  S(1, 'create the synthetic supplier', { name: vendorName }, r)
  await shot(page, 'ACC-MD-01-01_vendor-created', {
    lang: LANG, scenario: 'ACC-MD-01', step: '1 supplier created', route: r.landed,
    beforeAfter: 'after', expectText: 'ZZ-DISCOVERY',
  })

  r = await formPost(page, {
    tokenFrom: '/Accounting/Customers', action: '/Accounting/SaveCustomer',
    fields: { id: 0, name: 'ZZ-DISCOVERY عميل الاكتشاف', nameEn: custName,
              taxRegNo: 'ZZ-TAX-C-001', creditLimit: 100000, paymentTermsDays: 30, isActive: true },
  })
  S(2, 'create the synthetic customer', { name: custName }, r)
  await shot(page, 'ACC-MD-01-02_customer-created', {
    lang: LANG, scenario: 'ACC-MD-01', step: '2 customer created', route: r.landed,
    beforeAfter: 'after', expectText: 'ZZ-DISCOVERY',
  })

  // resolve the ids the app just assigned, from the app's own list pages
  const ids = await page.evaluate(() => ({}))
  log.masterData = { vendorName, custName }

  // ============================================================ ACC-P2P-01 purchase invoice
  // 10 x 100.00 = 1,000.00 net, VAT 14% = 140.00, gross 1,140.00
  const piLines = JSON.stringify([{
    itemDescription: 'ZZ-DISCOVERY consultancy hours', qty: 10, unitPrice: 100,
    discountAmount: 0, taxRate: TAX, expenseAccountId: EXPENSE_ACCT,
  }])
  log.pendingPurchase = { net: 1000, vat: 140, gross: 1140 }

  note(`sc-p2p-o2c-${LANG}-stage1`, log)
  const s1 = saveShots(`p2p-o2c-${LANG}-stage1`)
  return { stage: 'master data created', steps: log.steps.length, shots: s1,
           note: 'vendor and customer ids are resolved by SQL between stages' }
}
