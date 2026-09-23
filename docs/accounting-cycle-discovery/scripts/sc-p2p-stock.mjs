// ACC-P2P-04 — a STOCK purchase invoice and the purchase return that depends on it.
//
// The first attempt at a purchase return failed, and the failure is the finding: the service
// requires an ITEM line with a warehouse —
//
//   var itemLines = lines.Where(l => l.ItemId != null && l.WarehouseId != null && l.Qty > 0)
//   if (itemLines.Count == 0) return (false, "The return must contain at least one item line …")
//
// A sales return accepted a service line; a purchase return will not. So this scenario buys a
// stocked item first, then returns part of it, which also exercises the inventory-accounting
// path (stock in, stock out, GRNI/inventory valuation).
import { BASE, signIn, shot, saveShots, note } from './lib-ready.mjs'
import { formPost, stepRec } from './lib-exec.mjs'

const LANG = process.env.CB_LANG || 'en'
const VENDOR = parseInt(process.env.CB_VENDOR || '0', 10)
const ITEM = parseInt(process.env.CB_ITEM || '0', 10)
const WH = parseInt(process.env.CB_WH || '1', 10)
const DATE = '2026-09-21'
const TAX = 14
const EXPENSE_ACCT = 4072

export default async function run(page) {
  await page.setViewportSize({ width: 1600, height: 1000 })
  const log = { language: LANG, vendorId: VENDOR, itemId: ITEM, warehouseId: WH, steps: [] }
  const S = (...a) => log.steps.push(stepRec(...a))

  await signIn(page)
  if (LANG !== 'en') {
    await page.goto(`${BASE}/Account/SetLanguage?culture=${LANG}&returnUrl=%2FAccounting%2FIndex`,
      { waitUntil: 'domcontentloaded' }).catch(() => {})
    await page.waitForTimeout(800)
  }

  // ------------------------------------------------ stock purchase invoice: 20 @ 50.00 + 14%
  const piLines = JSON.stringify([{
    itemDescription: 'ZZ-DISCOVERY stocked goods', qty: 20, unitPrice: 50,
    discountAmount: 0, taxRate: TAX, expenseAccountId: EXPENSE_ACCT,
    itemId: ITEM, warehouseId: WH,
  }])
  let r = await formPost(page, {
    tokenFrom: '/Accounting/NewPurchaseInvoice', action: '/Accounting/CreatePurchaseInvoice',
    fields: { vendorId: VENDOR, invoiceDate: DATE, notes: 'ZZ-DISCOVERY P2P-04 stock purchase', linesJson: piLines },
  })
  S(1, 'ACC-P2P-04 stock purchase invoice, 20 units @ 50.00 + 14% VAT',
    { vendorId: VENDOR, itemId: ITEM, warehouseId: WH, qty: 20, unitPrice: 50, expectedGross: 1140 }, r)
  await shot(page, 'ACC-P2P-04-01_stock-invoice-posted', {
    lang: LANG, scenario: 'ACC-P2P-04', step: '1 stock purchase invoice posted', route: r.landed,
    recordId: `vendor ${VENDOR} item ${ITEM}`, beforeAfter: 'after posting',
  })

  // ------------------------------------------------ purchase return: 5 of the 20 units
  const prLines = JSON.stringify([{
    itemDescription: 'ZZ-DISCOVERY stocked goods returned', qty: 5, unitPrice: 50,
    discountAmount: 0, taxRate: TAX, expenseAccountId: EXPENSE_ACCT,
    itemId: ITEM, warehouseId: WH,
  }])
  r = await formPost(page, {
    tokenFrom: '/Accounting/NewPurchaseReturn', action: '/Accounting/CreatePurchaseReturn',
    fields: { vendorId: VENDOR, returnDate: DATE,
              notes: 'ZZ-DISCOVERY P2P-05 purchase return, 5 units', linesJson: prLines },
  })
  S(2, 'ACC-P2P-05 purchase return, 5 of the 20 units (valued at item cost, not invoice price)',
    { vendorId: VENDOR, itemId: ITEM, warehouseId: WH, qty: 5 }, r)
  await shot(page, 'ACC-P2P-05-01_purchase-return', {
    lang: LANG, scenario: 'ACC-P2P-05', step: '2 purchase return posted', route: r.landed,
    recordId: `vendor ${VENDOR}`, beforeAfter: 'after posting the debit note',
  })

  note(`sc-p2p-stock-${LANG}`, log)
  const s = saveShots(`p2p-stock-${LANG}`)
  return { lang: LANG, results: log.steps.map(x => `${x.n}. landed=${x.landed}`), shots: s }
}
