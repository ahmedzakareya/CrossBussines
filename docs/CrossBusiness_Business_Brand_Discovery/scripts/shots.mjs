// Visual evidence capture for the business & brand discovery package.
//
// READ-ONLY BY CONSTRUCTION: it signs in and navigates. It never submits a form other than the
// sign-in one, so nothing in the database changes. Each screen is recorded with what the page
// actually reported — HTTP status, title, direction, the resolved brand token and the fonts the
// browser really used — so the written findings can cite a measurement rather than an impression.
import fs from 'node:fs'
import path from 'node:path'

const OUT = 'C:/CrossBuy/CrossBuy/docs/business-brand-discovery/screenshots'
const BASE = 'http://localhost:5268'

const SCREENS = [
  ['01-login',              '/Account/Login',                  'The front door — the only unauthenticated screen'],
  ['02-portal-choose',      '/Portal/Choose',                  'Module chooser: which products this install offers'],
  ['03-workspace',          '/Workspace/Index',                      'Platform workspace — the cross-module home'],
  ['04-inventory-dash',     '/Inventory/Index',                      'Inventory dashboard (_LayoutInventory)'],
  ['05-inventory-items',    '/Inventory/Items',                'Item master — the busiest list in the product'],
  ['06-accounting-dash',    '/Accounting/Index',                     'Accounting dashboard (_LayoutAccounting)'],
  ['07-trial-balance',      '/Accounting/TrialBalance',        'Trial balance — the stated visual authority'],
  ['08-chart-of-accounts',  '/Accounting/ChartOfAccounts',     'Chart of accounts'],
  ['09-crm-dashboard',      '/Crm/Index',                            'CRM dashboard'],
  ['10-crm-pipeline',       '/Crm/Pipeline',                   'Opportunity pipeline (Kanban)'],
  ['11-admin-hr',           '/Admin/Index',                          'HR dashboard (_LayoutBackend)'],
  ['12-tasks',              '/Tasks/Index',                          'Tasks — my tasks'],
  ['13-projects',           '/Project/Dashboard',              'Projects & contracting dashboard'],
  ['14-restaurant',         '/Pos/Dashboard',                  'Restaurant dashboard'],
  ['15-hyper',              '/Hyper/Dashboard',                'Hyper (supermarket) dashboard'],
  ['16-reports-center',     '/Reports/Index',                        'Reports Center — the report catalogue'],
  ['17-report-studio',      '/Reports/Studio',                 'Report Studio — the layout designer'],
  ['18-calendar',           '/Calendar/Index',                       'Calendar'],
  ['19-chat',               '/Chat/Index',                           'Internal chat'],
  ['20-manufacturing',      '/Inventory/ManufDashboard',       'Manufacturing dashboard (_LayoutManufacturing)'],
]

// A second pass over three screens in English, to evidence the bilingual/RTL claim with a
// side-by-side rather than an assertion.
const EN_PASS = ['04-inventory-dash', '07-trial-balance', '11-admin-hr']

async function measure(page) {
  return page.evaluate(() => {
    const cs = getComputedStyle(document.documentElement)
    const body = getComputedStyle(document.body)
    const tok = n => (cs.getPropertyValue(n) || '').trim() || null
    // Which font FAMILIES the page asks for, and which faces the document actually loaded.
    const loaded = []
    try { document.fonts.forEach(f => loaded.push(f.family + ' ' + f.weight + ' ' + f.status)) } catch (e) { }
    const h1 = document.querySelector('h1, .page-heading')
    return {
      title: document.title,
      dir: document.documentElement.getAttribute('dir') || getComputedStyle(document.body).direction,
      lang: document.documentElement.getAttribute('lang'),
      heading: h1 ? h1.textContent.trim().slice(0, 120) : null,
      bodyFontFamily: body.fontFamily,
      bodyBackground: body.backgroundColor,
      tokens: {
        '--cb-brand': tok('--cb-brand'),
        '--cb-brand-fill': tok('--cb-brand-fill'),
        '--cb-brand-ink': tok('--cb-brand-ink'),
        '--bs-primary': tok('--bs-primary'),
        '--kt-primary': tok('--kt-primary'),
      },
      fontsLoaded: Array.from(new Set(loaded)).slice(0, 12),
      // The amber ban is a standing rule; count anything painted with it so the claim is measured.
      amberElements: Array.from(document.querySelectorAll('*')).filter(el => {
        const c = getComputedStyle(el)
        return /rgb\(245,\s*158,\s*11\)|#f59e0b/i.test(c.backgroundColor)
      }).length,
      sidebarLinks: document.querySelectorAll('.menu-item a.menu-link, .app-sidebar a').length,
      bodyChars: document.body.innerText.length,
    }
  })
}

export default async function run(page, ui) {
  fs.mkdirSync(OUT, { recursive: true })
  page.setDefaultTimeout(45000)
  const log = []

  // ---- 1. the login screen itself, before signing in
  await page.goto(BASE + '/Account/Login', { waitUntil: 'domcontentloaded' })
  await page.waitForTimeout(1200)
  log.push({ id: '01-login', url: '/Account/Login', note: SCREENS[0][2], status: 200, ...(await measure(page)) })
  await page.screenshot({ path: path.join(OUT, '01-login.png'), fullPage: false })

  // ---- 2. sign in
  const u = page.locator('input[name="UserName"], input[name="username"], input[type="text"]').first()
  const p = page.locator('input[type="password"]').first()
  await u.fill('admin')
  await p.fill('Admin@123')
  await Promise.all([
    page.waitForLoadState('domcontentloaded'),
    page.locator('button[type="submit"], input[type="submit"]').first().click(),
  ])
  await page.waitForTimeout(2500)
  const landed = page.url()

  // ---- 3. walk the screens
  for (const [id, url, note] of SCREENS.slice(1)) {
    let status = null
    try {
      const resp = await page.goto(BASE + url, { waitUntil: 'domcontentloaded', timeout: 60000 })
      status = resp ? resp.status() : null
      await page.waitForTimeout(2200)
      const m = await measure(page)
      log.push({ id, url, note, status, finalUrl: page.url().replace(BASE, ''), ...m })
      await page.screenshot({ path: path.join(OUT, id + '.png'), fullPage: false })
    } catch (e) {
      log.push({ id, url, note, status, error: String(e).slice(0, 300) })
      // A FAILED goto LEAVES A PENDING NAVIGATION. Without parking the page here, every later
      // goto dies with "interrupted by another navigation" and one 404 takes the whole run down —
      // which is exactly what happened on the first pass.
      try { await page.goto('about:blank', { waitUntil: 'domcontentloaded', timeout: 15000 }) } catch (e2) { }
    }
  }

  // ---- 4. the English pass
  for (const id of EN_PASS) {
    const row = SCREENS.find(s => s[0] === id)
    try {
      await page.goto(BASE + '/Account/SetLanguage?culture=en&returnUrl=' + encodeURIComponent(row[1]),
        { waitUntil: 'domcontentloaded', timeout: 60000 })
      await page.waitForTimeout(2200)
      const m = await measure(page)
      log.push({ id: id + '-en', url: row[1] + ' (culture=en)', note: row[2] + ' — English pass', status: 200, ...m })
      await page.screenshot({ path: path.join(OUT, id + '-en.png'), fullPage: false })
    } catch (e) {
      log.push({ id: id + '-en', error: String(e).slice(0, 300) })
      try { await page.goto('about:blank', { waitUntil: 'domcontentloaded', timeout: 15000 }) } catch (e2) { }
    }
  }

  fs.writeFileSync(path.join(OUT, 'capture-log.json'), JSON.stringify(log, null, 2), 'utf8')
  return { landedAfterLogin: landed, captured: log.length, files: fs.readdirSync(OUT).length }
}
