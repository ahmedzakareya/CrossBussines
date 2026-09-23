// ACC-ROLE-02 — the two questions the read matrix could not answer.
//
//   1. Does dev.otherco (company 65) actually SEE company 1's figures, or merely reach the URL?
//      AccountingController carries `private const int DefaultCompanyId = 1`, so this is the
//      runtime test of that constant rather than an inference from reading it.
//   2. Can dev.clerk (NO roles) actually POST a journal, or only open the form?
//
// The write probe creates a small balanced journal. It runs on the isolated database and is
// reversed by the caller if it succeeds.
import { BASE, ready, shot, saveShots, note } from './lib-ready.mjs'

const PW = process.env.CB_SEEDPW
const LANG = process.env.CB_LANG || 'en'

async function signInAs(page, user, pw) {
  await page.goto(BASE + '/Account/Logout', { waitUntil: 'domcontentloaded', timeout: 45000 }).catch(() => {})
  await page.context().clearCookies()
  await page.goto(BASE + '/Account/Login', { waitUntil: 'domcontentloaded', timeout: 60000 })
  await page.locator('input[name="UserName"], input[type="text"]').first().fill(user)
  await page.locator('input[type="password"]').first().fill(pw)
  await page.locator('button[type="submit"], input[type="submit"]').first().click()
  await page.waitForTimeout(2500)
  return !/\/Account\/Login/i.test(page.url())
}

// read the trial-balance figure the signed-in user is actually shown
async function readTrialBalance(page) {
  await page.goto(BASE + '/Accounting/TrialBalance', { waitUntil: 'domcontentloaded', timeout: 60000 })
  await ready(page, { minChars: 300 })
  return page.evaluate(() => {
    const f = Array.from(document.querySelectorAll('tfoot tr td, tfoot tr th'))
      .map(e => (e.innerText || '').trim()).filter(t => /[0-9]/.test(t))
    const rows = document.querySelectorAll('tbody tr').length
    return { footer: f.slice(0, 3), accountRows: rows }
  })
}

export default async function run(page) {
  if (!PW) throw new Error('Set CB_SEEDPW.')
  await page.setViewportSize({ width: 1600, height: 1000 })
  const out = {}

  // ---------------------------------------------------------------- 1. cross-company visibility
  for (const u of ['dev.superadmin', 'dev.otherco']) {
    const ok = await signInAs(page, u, PW)
    out[u] = { signedIn: ok }
    if (!ok) continue
    out[u].trialBalance = await readTrialBalance(page)
    await shot(page, `ACC-ROLE-02_${u.replace('.', '-')}-trial-balance`, {
      lang: LANG, scenario: 'ACC-ROLE-02', step: `trial balance as seen by ${u}`,
      route: '/Accounting/TrialBalance', role: u, beforeAfter: 'role view',
    })
  }

  // ---------------------------------------------------------------- 2. can a roleless clerk POST?
  const ok = await signInAs(page, 'dev.clerk', PW)
  out['dev.clerk'] = { signedIn: ok }
  if (ok) {
    await page.goto(BASE + '/Accounting/CreateJournal', { waitUntil: 'domcontentloaded', timeout: 60000 })
    const r = await ready(page, { expect: '#entryDate' })
    out['dev.clerk'].formReachable = r.ok
    if (r.ok) {
      await page.fill('#entryDate', '2026-09-21')
      await page.fill('#jeForm input[name=description], #jeForm textarea[name=description]',
        'ZZ-DISCOVERY ROLE-02 write probe by a user with NO roles')
      const rows = await page.evaluate(() => document.querySelectorAll('#rows tr').length)
      for (let i = rows; i < 2; i++) await page.click('#addRow')
      await page.waitForTimeout(300)
      await page.evaluate(() => {
        const rs = document.querySelectorAll('#rows tr')
        const pick = (tr, code) => {
          const s = tr.querySelector('.acc')
          const o = Array.from(s.options).find(x => (x.textContent || '').trim().startsWith(code))
          if (o) { s.value = o.value; s.dispatchEvent(new Event('change', { bubbles: true })) }
        }
        const amt = (tr, cls, v) => {
          const e = tr.querySelector('.' + cls); e.value = v
          e.dispatchEvent(new Event('input', { bubbles: true })); e.dispatchEvent(new Event('change', { bubbles: true }))
        }
        pick(rs[0], '510105'); amt(rs[0], 'dr', '77')
        pick(rs[1], '110102'); amt(rs[1], 'cr', '77')
      })
      await page.waitForTimeout(400)
      await page.evaluate(() => {
        const b = Array.from(document.querySelectorAll('button'))
          .find(x => (x.getAttribute('onclick') || '').includes("submitJe('post')"))
        b.click()
      })
      await page.waitForTimeout(3500)
      // The submit navigates; reading location too early destroys the execution context.
      try { await page.waitForLoadState('domcontentloaded', { timeout: 30000 }) } catch (e) { }
      await page.waitForTimeout(1200)
      const after = { url: new URL(page.url()).pathname }
      out['dev.clerk'].afterPost = after.url
      out['dev.clerk'].verdict = /\/Accounting\/Journals/i.test(after.url)
        ? 'POST ACCEPTED — a user with NO roles posted to the ledger'
        : 'post refused or returned to the form'
      await shot(page, 'ACC-ROLE-02_clerk-post-attempt', {
        lang: LANG, scenario: 'ACC-ROLE-02', step: 'roleless user attempts to post',
        route: after.url, role: 'dev.clerk', beforeAfter: 'after post attempt',
      })
    }
  }

  note(`sc-roles-write-${LANG}`, out)
  const s = saveShots(`roles-write-${LANG}`)
  return { out, shots: s }
}
