// FEASIBILITY PROBE — can a journal actually be created and posted through the UI?
//
// Read-only: it opens the form and reports its shape. Nothing is submitted here. The answer
// decides whether the worked examples can be driven through the interface or have to fall back
// to a documented limitation.
import { BASE, signIn, ready, note } from './lib-ready.mjs'

export default async function run(page) {
  await page.setViewportSize({ width: 1600, height: 1000 })
  const landed = await signIn(page)

  const out = { landedAfterSignIn: landed, screens: {} }

  for (const route of ['/Accounting/CreateJournal', '/Accounting/Journals',
                       '/Accounting/Periods', '/Accounting/ChartOfAccounts']) {
    await page.goto(BASE + route, { waitUntil: 'domcontentloaded', timeout: 60000 })
    const r = await ready(page, { minChars: 300 })
    const shape = await page.evaluate(() => {
      const vis = el => el.offsetParent !== null
      return {
        forms: document.querySelectorAll('form').length,
        inputs: Array.from(document.querySelectorAll('input:not([type=hidden]),select,textarea'))
          .filter(vis)
          .slice(0, 40)
          .map(e => ({ tag: e.tagName.toLowerCase(), type: e.type || '', id: e.id || '',
                       name: e.name || '', ph: e.placeholder || '',
                       required: e.required, dataControl: e.getAttribute('data-control') || '' })),
        buttons: Array.from(document.querySelectorAll('button, a.btn')).filter(vis).slice(0, 30)
          .map(b => ({ text: (b.innerText || '').trim().slice(0, 40), id: b.id || '',
                       cls: (b.className || '').toString().slice(0, 70),
                       type: b.getAttribute('type') || '' })),
        tables: document.querySelectorAll('table').length,
        rows: document.querySelectorAll('tbody tr').length,
        // the journal grid is built in JS; count the line-entry rows specifically
        lineRows: document.querySelectorAll('[id*=line] tr, tbody[id] tr').length,
      }
    })
    out.screens[route] = { ready: r.ok, checks: r.checks, heading: r.state.heading,
                           title: r.state.title, chars: r.state.chars, shape }
  }

  note('probe-journal-form', out)
  return {
    landed,
    summary: Object.fromEntries(Object.entries(out.screens).map(([k, v]) =>
      [k, `ready=${v.ready} heading="${v.heading}" inputs=${v.shape.inputs.length} buttons=${v.shape.buttons.length}`])),
  }
}
