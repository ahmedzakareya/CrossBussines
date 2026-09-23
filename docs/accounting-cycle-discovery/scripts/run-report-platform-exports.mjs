// ACC-EXP-02 — the PLATFORM reporting exports: Pdf, Xlsx, Csv from /Reports/Export.
//
// The in-module exports are all .xlsx. PDF is only offered by the reporting platform, whose
// renderer is Playwright/Chromium — so whether a PDF can actually be produced on this install is
// a real question, not a documentation one. Each attempt is recorded with what came back.
import fs from 'node:fs'
import path from 'node:path'
import { BASE, signIn, ready, note } from './lib-ready.mjs'

const OUT = 'C:/CrossBuy/CrossBuy/docs/accounting-cycle-discovery/exports'
const LANG = process.env.CB_LANG || 'en'
const REPORT = process.env.CB_REPORT || 'Accounting.TrialBalance'
const FORMATS = ['Csv', 'Xlsx', 'Pdf']

function sniff(buf) {
  if (buf.subarray(0, 4).toString('latin1') === '%PDF') return 'PDF'
  if (buf[0] === 0x50 && buf[1] === 0x4b) return 'ZIP container (xlsx)'
  const s = buf.subarray(0, 300).toString('utf8')
  if (/^\s*</.test(s)) return 'HTML/XML'
  return 'text/other'
}

export default async function run(page) {
  fs.mkdirSync(OUT, { recursive: true })
  await page.setViewportSize({ width: 1600, height: 1000 })
  await signIn(page)

  const results = []

  // does the report even resolve for this user?
  await page.goto(`${BASE}/Reports/Viewer/${REPORT}`, { waitUntil: 'domcontentloaded', timeout: 60000 })
  const r0 = await ready(page, { minChars: 200 })
  results.push({ probe: 'viewer', report: REPORT, landed: r0.state.url,
                 heading: r0.state.heading, ready: r0.ok })

  for (const fmt of FORMATS) {
    const rec = { report: REPORT, format: fmt, language: LANG }
    const url = `${BASE}/Reports/Export?id=${encodeURIComponent(REPORT)}&format=${fmt}`
    try {
      const [dl, resp] = await Promise.all([
        page.waitForEvent('download', { timeout: 60000 }).catch(() => null),
        page.goto(url, { waitUntil: 'domcontentloaded', timeout: 60000 }).catch(e => ({ _err: String(e).slice(0, 120) })),
      ])
      if (dl) {
        const suggested = dl.suggestedFilename() || `${REPORT}.${fmt}`
        const ext = path.extname(suggested) || '.' + fmt.toLowerCase()
        const file = `ACC-EXP-PLATFORM_${REPORT.replace(/\./g, '-')}.${LANG}${ext}`
        const full = path.join(OUT, file)
        await dl.saveAs(full)
        const buf = fs.readFileSync(full)
        rec.status = 'DOWNLOADED'
        rec.filename = file
        rec.bytes = buf.length
        rec.sniffedType = sniff(buf)
        rec.matchesRequestedFormat =
          (fmt === 'Pdf' && rec.sniffedType === 'PDF') ||
          (fmt === 'Xlsx' && rec.sniffedType.startsWith('ZIP')) ||
          (fmt === 'Csv' && /text/.test(rec.sniffedType))
      } else {
        const st = await ready(page, { minChars: 50 })
        rec.status = 'NO FILE'
        rec.landed = st.state.url
        rec.httpNote = resp && resp._err ? resp._err : (resp && resp.status ? `HTTP ${resp.status()}` : 'no response')
        rec.pageExcerpt = (st.state.heading || '').slice(0, 120)
      }
    } catch (e) {
      rec.status = 'ERROR'; rec.error = String(e).slice(0, 180)
      try { await page.goto('about:blank') } catch (e2) { }
    }
    results.push(rec)
  }

  note(`platform-exports-${LANG}`, results)
  return {
    report: REPORT,
    detail: results.filter(r => r.format).map(r =>
      `${r.format}: ${r.status}${r.bytes ? ` ${r.bytes}B ${r.sniffedType} match=${r.matchesRequestedFormat}` : ` (${r.httpNote || r.error || ''})`}`),
  }
}
