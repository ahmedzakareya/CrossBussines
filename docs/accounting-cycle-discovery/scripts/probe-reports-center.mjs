// What does the Reports Center actually offer this user, and what do its links look like?
// Answering this from the page beats guessing a route format.
import { BASE, signIn, ready, note, shot, saveShots } from './lib-ready.mjs'

const LANG = process.env.CB_LANG || 'en'

export default async function run(page) {
  await page.setViewportSize({ width: 1600, height: 1000 })
  await signIn(page)
  await page.goto(BASE + '/Reports/Index', { waitUntil: 'domcontentloaded', timeout: 60000 })
  const r = await ready(page, { minChars: 200 })
  const catalogue = await page.evaluate(() => {
    const links = Array.from(document.querySelectorAll('a[href]'))
      .map(a => ({ href: a.getAttribute('href'), text: (a.innerText || '').trim().slice(0, 50) }))
      .filter(x => /Viewer|Export|report/i.test(x.href || ''))
    return {
      heading: (document.querySelector('h1,.page-heading') || {}).textContent?.trim(),
      cards: document.querySelectorAll('.card').length,
      reportLinks: links.slice(0, 30),
      bodyExcerpt: (document.body.innerText || '').replace(/\s+/g, ' ').slice(0, 700),
    }
  })
  await shot(page, 'ACC-RPT-10_reports-center', {
    lang: LANG, scenario: 'ACC-REPORTS', step: 'the reporting platform catalogue',
    route: '/Reports/Index', beforeAfter: 'catalogue',
  })
  note(`reports-center-${LANG}`, { ready: r.ok, catalogue })
  const s = saveShots(`reports-center-${LANG}`)
  return { ready: r.ok, heading: catalogue.heading, cards: catalogue.cards,
           links: catalogue.reportLinks.slice(0, 12), excerpt: catalogue.bodyExcerpt.slice(0, 400), shots: s }
}
