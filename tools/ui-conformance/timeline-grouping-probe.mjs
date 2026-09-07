// Reads /Calendar/Timeline in BOTH languages and reports what the grid actually says about where
// each person works. Written because the first branch lookup pointed at the wrong table entirely
// (Hierarchicals rather than dbo.Branches) and a build-clean, DI-clean, exception-free page still
// rendered "no branch" for every one of 25 employees - only the rendered text catches that.
//
//   node tools/ui-conformance/timeline-grouping-probe.mjs --base http://localhost:5411
import { createRequire } from 'node:module';
const require = createRequire(new URL('./package.json', import.meta.url));
const { chromium } = require('playwright');

const arg = (n, d) => { const i = process.argv.indexOf('--' + n); return i > 0 ? process.argv[i + 1] : d; };
const BASE = arg('base', 'http://localhost:5411');
const USER = arg('user', 'Admin'), PASS = arg('password', 'Admin@123');

const read = async (page) => await page.evaluate(() => {
  const txt = (el) => (el ? el.textContent.replace(/\s+/g, ' ').trim() : null);
  const tables = [...document.querySelectorAll('table')];
  // The hour grid is the table whose header row starts with the employee column and then hours.
  const grid = tables.find(t => t.querySelectorAll('thead th').length > 5);
  const groups = [];
  if (grid) {
    for (const tr of grid.querySelectorAll('tbody tr')) {
      const span = tr.querySelector('td[colspan]');
      if (span && span.getAttribute('colspan') !== '1') {
        groups.push({ heading: txt(span), people: [] });
      } else if (groups.length) {
        const first = tr.querySelector('td');
        const busy = [...tr.querySelectorAll('td .badge')].length;
        groups.at(-1).people.push({ name: txt(first), busyCells: [...tr.querySelectorAll('td')].filter(td => td.querySelector('.badge')).length });
      }
    }
  }
  const tiles = [...document.querySelectorAll('.fs-2hx')].map(el => ({
    n: txt(el), label: txt(el.nextElementSibling),
  }));
  const bookings = tables.length && tables[0] !== grid
    ? [...tables[0].querySelectorAll('tbody tr')].map(tr => [...tr.querySelectorAll('td')].map(td => txt(td)))
    : [];
  return {
    company: txt(document.querySelector('.card-title .text-muted')),
    tiles, groups, bookings,
    notice: txt(document.querySelector('.notice')),
  };
});

(async () => {
  const browser = await chromium.launch();
  const ctx = await browser.newContext({ ignoreHTTPSErrors: true });
  const page = await ctx.newPage();

  // Same login sequence the other probes in this folder use - the selectors are the app's, and a
  // probe that silently fails to sign in reports an empty page as an empty screen.
  await page.goto(BASE + '/Account/Login', { waitUntil: 'domcontentloaded' });
  await page.fill('input[name="UserName"], input[name="Username"], input[name="Email"]', USER);
  await page.fill('input[name="Password"]', PASS);
  await page.click('button[type="submit"]');
  await page.waitForURL(u => !u.toString().includes('/Account/Login'), { timeout: 60000 }).catch(() => {});
  if (page.url().includes('/Account/Login')) { console.error('SIGN-IN FAILED - nothing below is meaningful'); process.exit(2); }

  for (const lang of ['en', 'ar']) {
    await ctx.addCookies([{ name: '.AspNetCore.Culture', value: `c=${lang}|uic=${lang}`, url: BASE }]);
    await page.goto(BASE + '/Calendar/Timeline', { waitUntil: 'networkidle' });
    const r = await read(page);
    console.log(`\n===== ${lang.toUpperCase()} =====`);
    console.log('company line :', r.company);
    console.log('tiles        :', r.tiles.map(t => `${t.n} ${t.label}`).join('  |  '));
    console.log('notice       :', r.notice);
    console.log('bookings     :');
    for (const b of r.bookings) console.log('   ', b.join('  |  '));
    console.log('groups       :');
    for (const g of r.groups) {
      console.log('   ▸', g.heading);
      for (const p of g.people) console.log('       ', p.name, p.busyCells ? `busyCells=${p.busyCells}` : '');
    }
  }
  await browser.close();
})();
