// Reads /Calendar/ResourceView in BOTH languages and reports the resource name, its kind label and
// how many hour cells are occupied. Written because CalendarResources.NameEn existed all along and
// carried a value for every one of the eleven active rows, while the service read r.Name - so the
// English page printed Arabic resource names and nothing about the build, the DI graph or the logs
// said so. Only the rendered cell does.
//
//   node tools/ui-conformance/resourceview-language-probe.mjs --base http://localhost:5411
import { createRequire } from 'node:module';
const require = createRequire(new URL('./package.json', import.meta.url));
const { chromium } = require('playwright');

const arg = (n, d) => { const i = process.argv.indexOf('--' + n); return i > 0 ? process.argv[i + 1] : d; };
const BASE = arg('base', 'http://localhost:5411');
const USER = arg('user', 'Admin'), PASS = arg('password', 'Admin@123');

// Arabic by CODEPOINT, not by a hex grep: the name arrives as text content and the only reliable
// question is whether the string contains characters in the Arabic block.
const AR = /[؀-ۿ]/;

(async () => {
  const browser = await chromium.launch();
  const ctx = await browser.newContext({ ignoreHTTPSErrors: true });
  const page = await ctx.newPage();

  await page.goto(BASE + '/Account/Login', { waitUntil: 'domcontentloaded' });
  await page.fill('input[name="UserName"], input[name="Username"], input[name="Email"]', USER);
  await page.fill('input[name="Password"]', PASS);
  await page.click('button[type="submit"]');
  await page.waitForURL(u => !u.toString().includes('/Account/Login'), { timeout: 60000 }).catch(() => {});
  if (page.url().includes('/Account/Login')) { console.error('SIGN-IN FAILED'); process.exit(2); }

  for (const lang of ['en', 'ar']) {
    await ctx.addCookies([{ name: '.AspNetCore.Culture', value: `c=${lang}|uic=${lang}`, url: BASE }]);
    await page.goto(BASE + '/Calendar/ResourceView', { waitUntil: 'networkidle' });

    const rows = await page.evaluate(() => {
      const t = (el) => (el ? el.textContent.replace(/\s+/g, ' ').trim() : '');
      const grid = [...document.querySelectorAll('table')].find(x => x.querySelectorAll('thead th').length > 5);
      if (!grid) return [];
      return [...grid.querySelectorAll('tbody tr')].map(tr => {
        const tds = [...tr.querySelectorAll('td')];
        return {
          name: t(tds[0]?.querySelector('span')),
          kind: t(tds[0]?.querySelector('div')),
          busy: tds.slice(1).filter(td => td.querySelector('.badge')).length,
          warn: t(tr).includes('⚠'),
        };
      });
    });

    console.log(`\n===== ${lang.toUpperCase()} =====`);
    let wrong = 0;
    for (const r of rows) {
      // On the English page an Arabic resource name or kind is the defect; on the Arabic page a
      // kind label still in English is the same defect pointing the other way.
      const bad = lang === 'en' ? (AR.test(r.name) || AR.test(r.kind))
                                : !AR.test(r.kind);
      if (bad) wrong++;
      console.log(`  ${bad ? 'WRONG-LANG' : 'ok        '}  ${r.name}  |  ${r.kind}  |  busy=${r.busy}${r.warn ? '  ⚠ double-booked' : ''}`);
    }
    console.log(`  -- ${rows.length} rows, ${wrong} in the wrong language, ${rows.filter(r => r.busy > 0).length} with bookings, ${rows.filter(r => r.warn).length} double-booked`);
  }
  await browser.close();
})();
