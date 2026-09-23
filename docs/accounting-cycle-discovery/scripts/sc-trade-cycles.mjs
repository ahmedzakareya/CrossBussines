// ACC-P2P-01..03 and ACC-O2C-01..03 — the connected trade cycles, executed.
//
// Dependencies are preserved: the invoice must exist before it can be paid, and the payment
// before the settlement is final. Every step captures the state it produced.
//
// Test assumptions: VAT 14%, EGP functional, dated inside the open period 2026-09.
import { BASE, signIn, shot, saveShots, note } from './lib-ready.mjs'
import { formPost, stepRec } from './lib-exec.mjs'

const LANG = process.env.CB_LANG || 'en'
const VENDOR = parseInt(process.env.CB_VENDOR || '0', 10)
const CUST = parseInt(process.env.CB_CUST || '0', 10)
const DATE = '2026-09-21'
const TAX = 14
const EXPENSE_ACCT = 4072
const REVENUE_ACCT = 22
const CASH_ACCT = 5

export default async function run(page) {
  if (!VENDOR || !CUST) throw new Error('Set CB_VENDOR and CB_CUST.')
  await page.setViewportSize({ width: 1600, height: 1000 })
  const log = {
    language: LANG, date: DATE, vendorId: VENDOR, customerId: CUST,
    assumptions: { vatPercent: TAX, currency: 'EGP (functional)',
                   note: 'rates and tax are TEST ASSUMPTIONS, not statutory advice' },
    steps: [],
  }
  const S = (...a) => log.steps.push(stepRec(...a))

  await signIn(page)
  if (LANG !== 'en') {
    await page.goto(`${BASE}/Account/SetLanguage?culture=${LANG}&returnUrl=%2FAccounting%2FIndex`,
      { waitUntil: 'domcontentloaded' }).catch(() => {})
    await page.waitForTimeout(800)
  }

  // ========================================================= ACC-P2P-01 purchase invoice
  // 10 x 100.00 = 1,000.00 net + 14% VAT 140.00 = 1,140.00 gross
  const piLines = JSON.stringify([{
    itemDescription: 'ZZ-DISCOVERY consultancy hours', qty: 10, unitPrice: 100,
    discountAmount: 0, taxRate: TAX, expenseAccountId: EXPENSE_ACCT,
  }])
  let r = await formPost(page, {
    tokenFrom: '/Accounting/NewPurchaseInvoice', action: '/Accounting/CreatePurchaseInvoice',
    fields: { vendorId: VENDOR, invoiceDate: DATE, notes: 'ZZ-DISCOVERY P2P-01 supplier invoice', linesJson: piLines },
  })
  S(1, 'ACC-P2P-01 create and post the purchase invoice',
    { vendorId: VENDOR, qty: 10, unitPrice: 100, taxRate: TAX, expectedGross: 1140 }, r)
  await shot(page, 'ACC-P2P-01-01_invoice-posted', {
    lang: LANG, scenario: 'ACC-P2P-01', step: '1 purchase invoice posted', route: r.landed,
    recordId: `vendor ${VENDOR}`, beforeAfter: 'after posting',
  })

  // ========================================================= ACC-P2P-02 partial payment
  r = await formPost(page, {
    tokenFrom: '/Accounting/Payments', action: '/Accounting/CreatePayment',
    fields: { vendorId: VENDOR, paymentDate: DATE, amount: 500, method: 'Bank',
              cashAccountId: CASH_ACCT, notes: 'ZZ-DISCOVERY P2P-02 partial payment', whtRate: 0 },
  })
  S(2, 'ACC-P2P-02 partial payment 500.00 of 1,140.00',
    { vendorId: VENDOR, amount: 500, method: 'Bank', cashAccountId: CASH_ACCT }, r)
  await shot(page, 'ACC-P2P-02-01_partial-payment', {
    lang: LANG, scenario: 'ACC-P2P-02', step: '2 partial payment recorded', route: r.landed,
    recordId: `vendor ${VENDOR}`, beforeAfter: 'after partial payment',
  })

  // remaining balance AFTER the partial payment — the allocation view the brief asks for
  await page.goto(`${BASE}/Accounting/ApAging`, { waitUntil: 'domcontentloaded', timeout: 60000 })
  await shot(page, 'ACC-P2P-02-02_remaining-balance', {
    lang: LANG, scenario: 'ACC-P2P-02', step: '3 remaining supplier balance', route: '/Accounting/ApAging',
    recordId: `vendor ${VENDOR}`, beforeAfter: 'balance after partial payment',
    expectText: 'ZZ-DISCOVERY',
  })

  // ========================================================= ACC-P2P-02b final settlement
  r = await formPost(page, {
    tokenFrom: '/Accounting/Payments', action: '/Accounting/CreatePayment',
    fields: { vendorId: VENDOR, paymentDate: DATE, amount: 640, method: 'Bank',
              cashAccountId: CASH_ACCT, notes: 'ZZ-DISCOVERY P2P-02b final settlement', whtRate: 0 },
  })
  S(3, 'ACC-P2P-02b final settlement 640.00 (1,140 - 500)',
    { vendorId: VENDOR, amount: 640 }, r)
  await shot(page, 'ACC-P2P-02-03_final-settlement', {
    lang: LANG, scenario: 'ACC-P2P-02', step: '4 final settlement', route: r.landed,
    recordId: `vendor ${VENDOR}`, beforeAfter: 'after final settlement',
  })

  // ========================================================= ACC-O2C-01 sales invoice
  // 5 x 300.00 = 1,500.00 net + 14% VAT 210.00 = 1,710.00 gross
  const siLines = JSON.stringify([{
    itemDescription: 'ZZ-DISCOVERY service delivered', qty: 5, unitPrice: 300,
    discountAmount: 0, taxRate: TAX, revenueAccountId: REVENUE_ACCT,
  }])
  r = await formPost(page, {
    tokenFrom: '/Accounting/NewSalesInvoice', action: '/Accounting/CreateSalesInvoice',
    fields: { customerId: CUST, invoiceDate: DATE, notes: 'ZZ-DISCOVERY O2C-01 customer invoice', linesJson: siLines },
  })
  S(4, 'ACC-O2C-01 create and post the sales invoice',
    { customerId: CUST, qty: 5, unitPrice: 300, taxRate: TAX, expectedGross: 1710 }, r)
  await shot(page, 'ACC-O2C-01-01_invoice-posted', {
    lang: LANG, scenario: 'ACC-O2C-01', step: '1 sales invoice posted', route: r.landed,
    recordId: `customer ${CUST}`, beforeAfter: 'after posting',
  })

  // ========================================================= ACC-O2C-02 partial receipt
  r = await formPost(page, {
    tokenFrom: '/Accounting/Receipts', action: '/Accounting/CreateReceipt',
    fields: { customerId: CUST, receiptDate: DATE, amount: 1000, method: 'Bank',
              cashAccountId: CASH_ACCT, notes: 'ZZ-DISCOVERY O2C-02 partial receipt' },
  })
  S(5, 'ACC-O2C-02 partial receipt 1,000.00 of 1,710.00',
    { customerId: CUST, amount: 1000 }, r)
  await shot(page, 'ACC-O2C-02-01_partial-receipt', {
    lang: LANG, scenario: 'ACC-O2C-02', step: '2 partial receipt recorded', route: r.landed,
    recordId: `customer ${CUST}`, beforeAfter: 'after partial receipt',
  })

  await page.goto(`${BASE}/Accounting/ArAging`, { waitUntil: 'domcontentloaded', timeout: 60000 })
  await shot(page, 'ACC-O2C-02-02_remaining-balance', {
    lang: LANG, scenario: 'ACC-O2C-02', step: '3 remaining customer balance', route: '/Accounting/ArAging',
    recordId: `customer ${CUST}`, beforeAfter: 'balance after partial receipt',
    expectText: 'ZZ-DISCOVERY',
  })

  // ========================================================= ACC-O2C-02b final collection
  r = await formPost(page, {
    tokenFrom: '/Accounting/Receipts', action: '/Accounting/CreateReceipt',
    fields: { customerId: CUST, receiptDate: DATE, amount: 710, method: 'Bank',
              cashAccountId: CASH_ACCT, notes: 'ZZ-DISCOVERY O2C-02b final collection' },
  })
  S(6, 'ACC-O2C-02b final collection 710.00 (1,710 - 1,000)',
    { customerId: CUST, amount: 710 }, r)
  await shot(page, 'ACC-O2C-02-03_final-collection', {
    lang: LANG, scenario: 'ACC-O2C-02', step: '4 final collection', route: r.landed,
    recordId: `customer ${CUST}`, beforeAfter: 'after final collection',
  })

  note(`sc-trade-cycles-${LANG}`, log)
  const s = saveShots(`trade-cycles-${LANG}`)
  return {
    lang: LANG, steps: log.steps.length,
    results: log.steps.map(x => `${x.n}. ${x.ok ? 'OK' : 'FAIL'} — ${(x.banner || x.landed || '').slice(0, 70)}`),
    shots: s,
  }
}
