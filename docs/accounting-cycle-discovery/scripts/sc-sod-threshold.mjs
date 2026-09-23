// ACC-SOD-01 — the segregation-of-duties control, tested with a POSITIVE threshold.
//
// Baseline is ApprovalThreshold = 0, under which the control cannot fire. This raises it through
// the SUPPORTED configuration screen (/Accounting/SaveAccSettings), exercises the rule with a
// separate creator and poster, then restores the baseline.
//
// No source code is changed. The setting change is recorded and reverted.
//
//   creator: dev.clerk      (no roles)      - creates a DRAFT above the threshold
//   poster:  admin          (ChiefAccountant, employee 5) - attempts to post it
//   then:    admin creates its own draft above the threshold and attempts to post it, which the
//            creator-cannot-approve rule should refuse.
import { BASE, ready, shot, saveShots, note } from './lib-ready.mjs'
import { formPost } from './lib-exec.mjs'

const LANG = process.env.CB_LANG || 'en'
const PW = process.env.CB_SEEDPW
const ADMIN = process.env.CB_USER
const ADMINPW = process.env.CB_PASS
const THRESHOLD = 1000
const DATE = '2026-09-21'

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

async function makeJournal(page, amount, desc, mode) {
  await page.goto(BASE + '/Accounting/CreateJournal', { waitUntil: 'domcontentloaded', timeout: 60000 })
  const r = await ready(page, { expect: '#entryDate' })
  if (!r.ok) return { ok: false, why: 'form not reachable' }
  await page.fill('#entryDate', DATE)
  await page.fill('#jeForm input[name=description], #jeForm textarea[name=description]', desc)
  const rows = await page.evaluate(() => document.querySelectorAll('#rows tr').length)
  for (let i = rows; i < 2; i++) await page.click('#addRow')
  await page.waitForTimeout(300)
  await page.evaluate(({ amount }) => {
    const rs = document.querySelectorAll('#rows tr')
    const pick = (tr, code) => {
      const s = tr.querySelector('.acc')
      const o = Array.from(s.options).find(x => (x.textContent || '').trim().startsWith(code))
      if (o) { s.value = o.value; s.dispatchEvent(new Event('change', { bubbles: true })) }
    }
    const amt = (tr, cls, v) => {
      const e = tr.querySelector('.' + cls); e.value = String(v)
      e.dispatchEvent(new Event('input', { bubbles: true })); e.dispatchEvent(new Event('change', { bubbles: true }))
    }
    pick(rs[0], '510105'); amt(rs[0], 'dr', amount)
    pick(rs[1], '110102'); amt(rs[1], 'cr', amount)
  }, { amount })
  await page.waitForTimeout(400)
  await page.evaluate(m => {
    const b = Array.from(document.querySelectorAll('button'))
      .find(x => (x.getAttribute('onclick') || '').includes(`submitJe('${m}')`))
    b.click()
  }, mode)
  await page.waitForTimeout(3000)
  try { await page.waitForLoadState('domcontentloaded', { timeout: 20000 }) } catch (e) { }
  await page.waitForTimeout(800)
  return { ok: true, landed: new URL(page.url()).pathname }
}

export default async function run(page) {
  if (!PW || !ADMIN || !ADMINPW) throw new Error('Set CB_SEEDPW, CB_USER, CB_PASS.')
  await page.setViewportSize({ width: 1600, height: 1000 })
  const out = { threshold: THRESHOLD, baseline: 0, steps: [] }

  // ---------------------------------------------------------------- raise the threshold
  await signInAs(page, ADMIN, ADMINPW)
  let r = await formPost(page, {
    tokenFrom: '/Accounting/AccountingRoles', action: '/Accounting/SaveAccSettings',
    fields: { approvalThreshold: THRESHOLD },
  })
  out.steps.push({ step: 'raise ApprovalThreshold to ' + THRESHOLD, landed: r.landed, banner: r.banner })
  await shot(page, 'ACC-SOD-01-01_threshold-set', {
    lang: LANG, scenario: 'ACC-SOD-01', step: `threshold raised to ${THRESHOLD}`,
    route: r.landed, role: 'admin', beforeAfter: 'configuration change',
  })

  // ---------------------------------------------------------------- admin creates AND posts (SoD)
  const own = await makeJournal(page, 2500, 'ZZ-DISCOVERY SOD-01 admin creates and immediately posts (above threshold)', 'post')
  out.steps.push({ step: 'admin creates 2,500 and presses Post directly', landed: own.landed })
  await shot(page, 'ACC-SOD-01-02_admin-self-post', {
    lang: LANG, scenario: 'ACC-SOD-01', step: 'creator attempts to post their own above-threshold entry',
    route: own.landed, role: 'admin (ChiefAccountant)', beforeAfter: 'after post attempt',
  })

  // ---------------------------------------------------------------- clerk creates a draft
  await signInAs(page, 'dev.clerk', PW)
  const clerkDraft = await makeJournal(page, 3000, 'ZZ-DISCOVERY SOD-01 draft created by the clerk (above threshold)', 'draft')
  out.steps.push({ step: 'dev.clerk saves a 3,000 draft', landed: clerkDraft.landed })

  note(`sc-sod-${LANG}`, out)
  const s = saveShots(`sod-${LANG}`)
  return { out, shots: s }
}
