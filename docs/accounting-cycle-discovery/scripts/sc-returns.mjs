// ACC-P2P-03 and ACC-O2C-03 — returns (debit note and credit note), linked to their originals.
//
// Both are partial returns of the invoices posted in sc-trade-cycles.mjs, so the dependency
// chain is preserved: the return references the original invoice id.
//
//   purchase return: 2 of the 10 units at 100.00 = 200.00 net + 14% VAT = 228.00
//   sales return:    1 of the 5 units at 300.00  = 300.00 net + 14% VAT = 342.00
import { BASE, signIn, shot, saveShots, note } from './lib-ready.mjs'
import { formPost, stepRec } from './lib-exec.mjs'

const LANG = process.env.CB_LANG || 'en'
const VENDOR = parseInt(process.env.CB_VENDOR || '0', 10)
const CUST = parseInt(process.env.CB_CUST || '0', 10)
const PI = parseInt(process.env.CB_PI || '0', 10)
const SI = parseInt(process.env.CB_SI || '0', 10)
const DATE = '2026-09-21'
const TAX = 14
const EXPENSE_ACCT = 4072
const REVENUE_ACCT = 22

export default async function run(page) {
  await page.setViewportSize({ width: 1600, height: 1000 })
  const log = { language: LANG, vendorId: VENDOR, customerId: CUST,
                originalPurchaseInvoice: PI, originalSalesInvoice: SI, steps: [] }
  const S = (...a) => log.steps.push(stepRec(...a))

  await signIn(page)
  if (LANG !== 'en') {
    await page.goto(`${BASE}/Account/SetLanguage?culture=${LANG}&returnUrl=%2FAccounting%2FIndex`,
      { waitUntil: 'domcontentloaded' }).catch(() => {})
    await page.waitForTimeout(800)
  }

  // ---------------------------------------------------- ACC-P2P-03 purchase return (debit note)
  const prLines = JSON.stringify([{
    itemDescription: 'ZZ-DISCOVERY consultancy hours returned', qty: 2, unitPrice: 100,
    discountAmount: 0, taxRate: TAX, expenseAccountId: EXPENSE_ACCT,
  }])
  let r = await formPost(page, {
    tokenFrom: '/Accounting/NewPurchaseReturn', action: '/Accounting/CreatePurchaseReturn',
    fields: { vendorId: VENDOR, originalInvoiceId: PI, returnDate: DATE,
              notes: 'ZZ-DISCOVERY P2P-03 purchase return (debit note)', linesJson: prLines },
  })
  S(1, 'ACC-P2P-03 purchase return, 2 units, linked to the original invoice',
    { vendorId: VENDOR, originalInvoiceId: PI, qty: 2, unitPrice: 100, expectedGross: 228 }, r)
  await shot(page, 'ACC-P2P-03-01_purchase-return', {
    lang: LANG, scenario: 'ACC-P2P-03', step: '1 purchase return posted', route: r.landed,
    recordId: `PI ${PI}`, beforeAfter: 'after posting the debit note',
  })

  // ---------------------------------------------------- ACC-O2C-03 sales return (credit note)
  const srLines = JSON.stringify([{
    itemDescription: 'ZZ-DISCOVERY service credited back', qty: 1, unitPrice: 300,
    discountAmount: 0, taxRate: TAX, revenueAccountId: REVENUE_ACCT,
  }])
  r = await formPost(page, {
    tokenFrom: '/Accounting/NewSalesReturn', action: '/Accounting/CreateSalesReturn',
    fields: { customerId: CUST, originalInvoiceId: SI, returnDate: DATE,
              notes: 'ZZ-DISCOVERY O2C-03 sales return (credit note)', linesJson: srLines },
  })
  S(2, 'ACC-O2C-03 sales return, 1 unit, linked to the original invoice',
    { customerId: CUST, originalInvoiceId: SI, qty: 1, unitPrice: 300, expectedGross: 342 }, r)
  await shot(page, 'ACC-O2C-03-01_sales-return', {
    lang: LANG, scenario: 'ACC-O2C-03', step: '1 sales return posted', route: r.landed,
    recordId: `SI ${SI}`, beforeAfter: 'after posting the credit note',
  })

  note(`sc-returns-${LANG}`, log)
  const s = saveShots(`returns-${LANG}`)
  return { lang: LANG, results: log.steps.map(x => `${x.n}. ${x.ok ? 'OK' : 'FAIL'} — ${x.landed}`), shots: s }
}
