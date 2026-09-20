// Second evidence pass, driven by what the first one measured.
//
// The first pass ran entirely in English (every page reported lang="en", dir="ltr"), so it proved
// nothing about the bilingual/RTL claim. This pass switches the culture to Arabic and re-captures,
// and it also NAMES the amber elements the first pass only counted — a count is an accusation, a
// selector is evidence.
import fs from 'node:fs'
import path from 'node:path'

const OUT = 'C:/CrossBuy/CrossBuy/docs/business-brand-discovery/screenshots'
const BASE = 'http://localhost:5268'

const AR_PASS = [
  ['04-inventory-dash', '/Inventory/Index'],
  ['07-trial-balance', '/Accounting/TrialBalance'],
  ['11-admin-hr', '/Admin/Index'],
  ['01-login', '/Account/Login'],
]

// Where amber actually lands, with enough identity to find it in the source.
const AMBER = [
  '/Inventory/Index', '/Accounting/Index', '/Crm/Pipeline', '/Admin/Index',
]

async function measure(page) {
  return page.evaluate(() => {
    const cs = getComputedStyle(document.documentElement)
    const body = getComputedStyle(document.body)
    return {
      title: document.title,
      dir: document.documentElement.getAttribute('dir') || body.direction,
      lang: document.documentElement.getAttribute('lang'),
      bodyDirection: body.direction,
      bodyFontFamily: body.fontFamily,
      brand: (cs.getPropertyValue('--cb-brand') || '').trim(),
      // Which stylesheet is actually serving the shell: the RTL bundle or the LTR one.
      styleBundles: Array.from(document.querySelectorAll('link[rel=stylesheet]'))
        .map(l => l.getAttribute('href')).filter(h => h && /style\.bundle/i.test(h)),
      // Did a Cairo face actually load, or did the page fall back?
      cairoLoaded: (() => {
        try { return document.fonts.check('600 16px Cairo') } catch (e) { return null }
      })(),
      fontFaces: (() => {
        const seen = new Set()
        try { document.fonts.forEach(f => { if (f.status === 'loaded') seen.add(f.family) }) } catch (e) { }
        return Array.from(seen)
      })(),
      heading: (document.querySelector('h1, .page-heading') || {}).textContent?.trim().slice(0, 90) || null,
    }
  })
}

async function amberReport(page) {
  return page.evaluate(() => {
    const hits = []
    for (const el of document.querySelectorAll('*')) {
      const c = getComputedStyle(el)
      const bg = c.backgroundColor, bc = c.borderTopColor, fg = c.color
      const amber = v => /rgb\(245,\s*158,\s*11\)/.test(v)
      if (!(amber(bg) || amber(fg))) continue
      hits.push({
        tag: el.tagName.toLowerCase(),
        cls: (el.className || '').toString().slice(0, 90),
        role: amber(bg) ? 'background' : 'text',
        text: (el.innerText || '').trim().slice(0, 50),
        // Contrast of whatever sits on it, measured — the ban is about legibility as well as taste.
        colorOnIt: fg,
      })
      if (hits.length >= 25) break
    }
    return hits
  })
}

export default async function run(page) {
  page.setDefaultTimeout(45000)
  const out = { arabicPass: [], amber: {} }

  // sign in
  await page.goto(BASE + '/Account/Login', { waitUntil: 'domcontentloaded' })
  await page.locator('input[name="UserName"], input[type="text"]').first().fill('admin')
  await page.locator('input[type="password"]').first().fill('Admin@123')
  await page.locator('button[type="submit"], input[type="submit"]').first().click()
  await page.waitForTimeout(2500)

  // ---- amber, named
  for (const url of AMBER) {
    try {
      await page.goto(BASE + url, { waitUntil: 'domcontentloaded', timeout: 60000 })
      await page.waitForTimeout(1800)
      out.amber[url] = await amberReport(page)
    } catch (e) {
      out.amber[url] = [{ error: String(e).slice(0, 150) }]
      try { await page.goto('about:blank') } catch (e2) { }
    }
  }

  // ---- switch the culture to Arabic and re-capture
  await page.goto(BASE + '/Account/SetLanguage?culture=ar&returnUrl=%2FPortal%2FChoose',
    { waitUntil: 'domcontentloaded', timeout: 60000 })
  await page.waitForTimeout(2000)
  out.afterSwitch = await measure(page)

  for (const [id, url] of AR_PASS) {
    try {
      await page.goto(BASE + url, { waitUntil: 'domcontentloaded', timeout: 60000 })
      await page.waitForTimeout(2200)
      const m = await measure(page)
      out.arabicPass.push({ id: id + '-ar', url, ...m })
      await page.screenshot({ path: path.join(OUT, id + '-ar.png'), fullPage: false })
    } catch (e) {
      out.arabicPass.push({ id: id + '-ar', url, error: String(e).slice(0, 200) })
      try { await page.goto('about:blank') } catch (e2) { }
    }
  }

  // put the session back to English so the next reader of this machine finds it as it was
  try { await page.goto(BASE + '/Account/SetLanguage?culture=en&returnUrl=%2FPortal%2FChoose') } catch (e) { }

  fs.writeFileSync(path.join(OUT, 'probe-rtl-and-amber.json'), JSON.stringify(out, null, 2), 'utf8')
  return {
    afterSwitchDir: out.afterSwitch?.dir, afterSwitchLang: out.afterSwitch?.lang,
    arabic: out.arabicPass.map(r => `${r.id} dir=${r.dir} lang=${r.lang} cairo=${r.cairoLoaded}`),
    amberCounts: Object.fromEntries(Object.entries(out.amber).map(([k, v]) => [k, v.length])),
  }
}
