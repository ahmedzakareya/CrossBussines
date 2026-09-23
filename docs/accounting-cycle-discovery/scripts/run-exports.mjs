// ACC-EXP-01 — actually download every accounting export and keep the file.
//
// An export button is not a tested export. Each download is intercepted, written to
// exports/, and its size and first bytes recorded so the manifest can say whether the file
// really opens as the type it claims.
import fs from 'node:fs'
import path from 'node:path'
import { BASE, signIn, ready, note } from './lib-ready.mjs'

const OUT = 'C:/CrossBuy/CrossBuy/docs/accounting-cycle-discovery/exports'
const LANG = process.env.CB_LANG || 'en'

const EXPORTS = [
  ['/Accounting/JournalsExport', 'journals'],
  ['/Accounting/SalesInvoicesExport', 'sales-invoices'],
  ['/Accounting/PurchaseInvoicesExport', 'purchase-invoices'],
  ['/Accounting/ReceiptsExport', 'receipts'],
  ['/Accounting/PaymentsExport', 'payments'],
  ['/Accounting/CustomersExport', 'customers'],
  ['/Accounting/VendorsExport', 'vendors'],
  ['/Accounting/CustomerAnalyticsExport', 'customer-analytics'],
]

function sniff(buf) {
  const h = buf.subarray(0, 8)
  if (h[0] === 0x50 && h[1] === 0x4b) return 'ZIP container (xlsx/docx family)'
  if (buf.subarray(0, 4).toString('latin1') === '%PDF') return 'PDF'
  if (h[0] === 0xef && h[1] === 0xbb && h[2] === 0xbf) return 'UTF-8 text with BOM (CSV likely)'
  const s = buf.subarray(0, 200).toString('utf8')
  if (/^\s*</.test(s)) return 'HTML/XML'
  if (/[,;\t].*[\r\n]/.test(s)) return 'delimited text (CSV likely)'
  return 'unknown'
}

export default async function run(page) {
  fs.mkdirSync(OUT, { recursive: true })
  await page.setViewportSize({ width: 1600, height: 1000 })
  await signIn(page)
  if (LANG !== 'en') {
    await page.goto(`${BASE}/Account/SetLanguage?culture=${LANG}&returnUrl=%2FAccounting%2FIndex`,
      { waitUntil: 'domcontentloaded' }).catch(() => {})
    await page.waitForTimeout(800)
  }

  const results = []
  for (const [route, name] of EXPORTS) {
    const rec = { route, name, language: LANG }
    try {
      const [dl] = await Promise.all([
        page.waitForEvent('download', { timeout: 45000 }).catch(() => null),
        page.goto(BASE + route, { waitUntil: 'domcontentloaded', timeout: 45000 }).catch(() => null),
      ])
      if (dl) {
        const suggested = dl.suggestedFilename() || `${name}.bin`
        const ext = path.extname(suggested) || '.bin'
        const file = `ACC-EXP_${name}.${LANG}${ext}`
        const full = path.join(OUT, file)
        await dl.saveAs(full)
        const buf = fs.readFileSync(full)
        rec.status = 'DOWNLOADED'
        rec.filename = file
        rec.suggestedFilename = suggested
        rec.bytes = buf.length
        rec.sniffedType = sniff(buf)
        // for text formats, record the header row and a row count so contents can be checked
        if (/text|CSV/i.test(rec.sniffedType)) {
          const txt = buf.toString('utf8')
          const lines = txt.split(/\r?\n/).filter(l => l.trim())
          rec.headerRow = (lines[0] || '').slice(0, 220)
          rec.dataRows = Math.max(0, lines.length - 1)
          rec.containsSyntheticRecord = /ZZ-DISCOVERY/.test(txt)
          rec.containsArabic = /[\u0600-\u06FF]/.test(txt)
        }
      } else {
        const r = await ready(page, { minChars: 100 })
        rec.status = 'NO DOWNLOAD'
        rec.landed = r.state.url
        rec.note = 'the route returned a page rather than a file'
      }
    } catch (e) {
      rec.status = 'ERROR'
      rec.error = String(e).slice(0, 180)
      try { await page.goto('about:blank') } catch (e2) { }
    }
    results.push(rec)
  }

  note(`exports-${LANG}`, results)
  return {
    lang: LANG,
    downloaded: results.filter(r => r.status === 'DOWNLOADED').length,
    of: results.length,
    detail: results.map(r => `${r.name}: ${r.status}${r.bytes ? ` ${r.bytes}B ${r.sniffedType}` : ''}`),
  }
}
