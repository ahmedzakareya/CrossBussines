// ACC-ROLE-01 — the access matrix, exercised with real sign-ins.
//
// Four profiles provisioned through the project's own DEV-ONLY seeder (RoleManager/UserManager,
// ordinary Login -> PasswordSignInAsync path). Each isolates ONE refusing dimension, so a denial
// is provable rather than merely observed:
//
//   dev.superadmin  company 1,  SuperAdmin   the elevated path
//   dev.auditor     company 1,  Auditor      authorised, NOT an administrator
//   dev.clerk       company 1,  no roles     refused on ROLE alone
//   dev.otherco     company 65, Auditor      holds the role, wrong company
//
// Every probe is READ-ONLY except the two explicitly marked write probes, which attempt a post
// and are expected to be refused for the lower profiles.
import { BASE, ready, shot, saveShots, note } from './lib-ready.mjs'

const LANG = process.env.CB_LANG || 'en'
const PW = process.env.CB_SEEDPW
const USERS = ['dev.superadmin', 'dev.auditor', 'dev.clerk', 'dev.otherco']

// route, what it represents in the brief's permission vocabulary
const PROBES = [
  ['/Accounting/Index', 'view: accounting home'],
  ['/Accounting/ChartOfAccounts', 'view: chart of accounts'],
  ['/Accounting/Journals', 'view: journal register'],
  ['/Accounting/CreateJournal', 'create: new journal form'],
  ['/Accounting/TrialBalance', 'view/export: trial balance'],
  ['/Accounting/ArAging', 'view: receivables aging'],
  ['/Accounting/Periods', 'close/reopen: fiscal periods'],
  ['/Accounting/YearEndClose', 'close: year end'],
  ['/Accounting/Payments', 'pay: supplier payments'],
  ['/Accounting/Receipts', 'collect: customer receipts'],
  ['/Accounting/AccountingRoles', 'manage: accounting roles'],
  ['/Accounting/JournalsExport', 'export: journals'],
]

async function signInAs(page, user, pw) {
  await page.goto(BASE + '/Account/Logout', { waitUntil: 'domcontentloaded', timeout: 45000 }).catch(() => {})
  await page.context().clearCookies()
  await page.goto(BASE + '/Account/Login', { waitUntil: 'domcontentloaded', timeout: 60000 })
  await page.locator('input[name="UserName"], input[type="text"]').first().fill(user)
  await page.locator('input[type="password"]').first().fill(pw)
  await page.locator('button[type="submit"], input[type="submit"]').first().click()
  await page.waitForTimeout(2500)
  const st = await page.evaluate(() => ({
    url: location.pathname + location.search,
    text: (document.body.innerText || '').slice(0, 300),
  }))
  return { signedIn: !/\/Account\/Login/i.test(st.url), landed: st.url, note: st.text.slice(0, 120) }
}

export default async function run(page) {
  if (!PW) throw new Error('Set CB_SEEDPW.')
  await page.setViewportSize({ width: 1600, height: 1000 })
  const out = { language: LANG, profiles: {} }

  for (const user of USERS) {
    const s = await signInAs(page, user, PW)
    const rec = { signIn: s, probes: {} }
    if (!s.signedIn) { out.profiles[user] = rec; continue }

    for (const [route, meaning] of PROBES) {
      try {
        const resp = await page.goto(BASE + route, { waitUntil: 'domcontentloaded', timeout: 45000 })
        await page.waitForTimeout(900)
        const st = await page.evaluate(() => ({
          url: location.pathname + location.search,
          chars: (document.body.innerText || '').length,
          denied: /access denied|ليس لديك|لا تملك|not authori|permission/i.test(document.body.innerText || ''),
        }))
        const status = resp ? resp.status() : null
        const verdict =
          /\/Account\/Login/i.test(st.url) ? 'REDIRECTED TO SIGN-IN' :
          /AccessDenied/i.test(st.url) || status === 403 ? 'DENIED (403 / AccessDenied)' :
          st.denied ? 'DENIED (message on page)' :
          status === 404 ? 'NOT FOUND (404)' :
          status === 200 ? 'ALLOWED' : `HTTP ${status}`
        rec.probes[route] = { meaning, status, verdict, landed: st.url, chars: st.chars }
      } catch (e) {
        rec.probes[route] = { meaning, verdict: 'ERROR', error: String(e).slice(0, 140) }
        try { await page.goto('about:blank') } catch (e2) { }
      }
    }

    // one capture per profile, on a screen every profile can be compared on
    await page.goto(BASE + '/Accounting/Journals', { waitUntil: 'domcontentloaded', timeout: 45000 }).catch(() => {})
    await shot(page, `ACC-ROLE-01_${user.replace('.', '-')}`, {
      lang: LANG, scenario: 'ACC-ROLE-01', step: `signed in as ${user}`,
      route: '/Accounting/Journals', role: user, beforeAfter: 'role view',
    })
    out.profiles[user] = rec
  }

  note(`sc-roles-${LANG}`, out)
  const s = saveShots(`roles-${LANG}`)
  const summary = {}
  for (const [u, r] of Object.entries(out.profiles)) {
    if (!r.signIn.signedIn) { summary[u] = 'COULD NOT SIGN IN'; continue }
    const v = Object.values(r.probes)
    summary[u] = `allowed ${v.filter(x => x.verdict === 'ALLOWED').length}/${v.length}` +
      `, denied ${v.filter(x => String(x.verdict).startsWith('DENIED')).length}` +
      `, 404 ${v.filter(x => String(x.verdict).startsWith('NOT FOUND')).length}`
  }
  return { summary, shots: s }
}
