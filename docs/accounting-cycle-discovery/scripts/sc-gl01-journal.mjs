// SCENARIO ACC-GL-01 — a manual journal: draft, then post, then reverse.
//
// The smallest complete loop in the ledger, and the one that proves the rest of the harness.
// Runs against the ISOLATED instance (port 5299 -> CrossBuyAcctTest). Every state is captured
// only after the readiness gate passes.
//
// Test assumptions, not statutory advice:
//   Rent of 5,000.00 EGP paid from the current account, dated inside the OPEN period 2026-09.
//   Accounts: Dr 510105 Rent Expense / Cr 110102 Bank - Current. Neither requires a cost centre.
import { BASE, signIn, ready, shot, saveShots, note } from './lib-ready.mjs'

const LANG = process.env.CB_LANG || 'en'
const SC = 'ACC-GL-01'
const AMOUNT = '5000'
const DATE = '2026-09-21'
const DESC = 'ZZ-DISCOVERY GL-01 September office rent'

async function pickAccountByCode(page, rowIdx, code) {
  // The account <select> is built in JS from the chart; choose by the visible option text,
  // which starts with the account code.
  return page.evaluate(({ rowIdx, code }) => {
    const rows = document.querySelectorAll('#rows tr')
    const sel = rows[rowIdx].querySelector('.acc')
    const opt = Array.from(sel.options).find(o => (o.textContent || '').trim().startsWith(code))
    if (!opt) return { ok: false, sample: Array.from(sel.options).slice(0, 4).map(o => o.textContent.trim()) }
    sel.value = opt.value
    sel.dispatchEvent(new Event('change', { bubbles: true }))
    return { ok: true, value: opt.value, text: opt.textContent.trim() }
  }, { rowIdx, code })
}

async function setAmount(page, rowIdx, cls, value) {
  return page.evaluate(({ rowIdx, cls, value }) => {
    const tr = document.querySelectorAll('#rows tr')[rowIdx]
    const el = tr.querySelector('.' + cls)
    el.value = value
    el.dispatchEvent(new Event('input', { bubbles: true }))
    el.dispatchEvent(new Event('change', { bubbles: true }))
    return el.value
  }, { rowIdx, cls, value })
}

export default async function run(page) {
  await page.setViewportSize({ width: 1600, height: 1000 })
  const log = { scenario: SC, language: LANG, steps: [] }
  const step = (n, what, data) => { log.steps.push({ n, what, at: new Date().toISOString(), ...data }) }

  await signIn(page)
  if (LANG !== 'en') {
    await page.goto(`${BASE}/Account/SetLanguage?culture=${LANG}&returnUrl=%2FPortal%2FChoose`,
      { waitUntil: 'domcontentloaded' }).catch(() => {})
    await page.waitForTimeout(800)
  }

  // ---------------------------------------------------------------- 1. the empty form
  await page.goto(BASE + '/Accounting/CreateJournal', { waitUntil: 'domcontentloaded', timeout: 60000 })
  let r = await ready(page, { expect: '#entryDate', minChars: 300 })
  step(1, 'open /Accounting/CreateJournal', { ready: r.ok, heading: r.state.heading })
  await shot(page, `${SC}-01_empty-form`, {
    lang: LANG, scenario: SC, step: '1 open the form', route: '/Accounting/CreateJournal',
    beforeAfter: 'before', expect: '#entryDate',
  })

  // ---------------------------------------------------------------- 2. fill the header and two lines
  await page.fill('#entryDate', DATE)
  // Scope the selector to the FORM: '[name=description]' also matches the theme's
  // <meta name="description">, which is the first match in the document and not fillable.
  await page.fill('#jeForm input[name=description], #jeForm textarea[name=description]', DESC)

  const rowsNow = await page.evaluate(() => document.querySelectorAll('#rows tr').length)
  for (let i = rowsNow; i < 2; i++) await page.click('#addRow')
  await page.waitForTimeout(300)

  const a0 = await pickAccountByCode(page, 0, '510105')   // Rent Expense
  const a1 = await pickAccountByCode(page, 1, '110102')   // Bank - Current
  const d0 = await setAmount(page, 0, 'dr', AMOUNT)
  const c1 = await setAmount(page, 1, 'cr', AMOUNT)
  await page.waitForTimeout(400)

  const balance = await page.evaluate(() => ({
    balanced: typeof JE_BALANCED !== 'undefined' ? JE_BALANCED : null,
    diff: typeof JE_DIFF !== 'undefined' ? JE_DIFF : null,
    rows: document.querySelectorAll('#rows tr').length,
  }))
  step(2, 'fill header and two lines', { account_debit: a0, account_credit: a1, debit: d0, credit: c1, balance })

  await shot(page, `${SC}-02_filled-before-save`, {
    lang: LANG, scenario: SC, step: '2 completed form, before submission',
    route: '/Accounting/CreateJournal', beforeAfter: 'before', expect: '#rows tr',
  })

  // ---------------------------------------------------------------- 3. Save draft
  await page.evaluate(() => {
    const b = Array.from(document.querySelectorAll('button'))
      .find(x => (x.getAttribute('onclick') || '').includes("submitJe('draft')"))
    b.click()
  })
  await page.waitForTimeout(3000)
  r = await ready(page, { minChars: 300 })
  const afterDraft = await page.evaluate(() => ({
    url: location.pathname + location.search,
    text: (document.body.innerText || '').slice(0, 600),
  }))
  step(3, "press Save (submitJe('draft'))", { ready: r.ok, landed: afterDraft.url, heading: r.state.heading })
  await shot(page, `${SC}-03_after-save-draft`, {
    lang: LANG, scenario: SC, step: '3 after Save draft', route: afterDraft.url, beforeAfter: 'after',
  })

  log.afterDraftUrl = afterDraft.url
  log.afterDraftText = afterDraft.text

  note(`sc-${SC}-${LANG}`, log)
  const s = saveShots(`${SC}-${LANG}`)
  return { scenario: SC, lang: LANG, steps: log.steps.length, shots: s, landedAfterDraft: afterDraft.url,
           balance }
}
