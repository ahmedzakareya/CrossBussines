// Compares the notification dropdown against the ACCOUNT menu in the same layout - the app's own
// reference dropdown - class contract first, then the two behaviours that contract buys.
//
// Written because the bell menu declared six fewer classes than the account menu
// (menu-rounded, menu-gray-800, menu-state-bg, menu-state-color, fw-semibold, fs-6) and the
// practical result was rows that did not react to the pointer at all. Every class present was
// valid, so nothing flagged it; the difference only shows when the two menus are measured
// side by side and the pointer is actually moved.
//
//   node tools/ui-conformance/notification-menu-conformance-probe.mjs --base http://localhost:5411
import { createRequire } from 'node:module';
const require = createRequire(new URL('./package.json', import.meta.url));
const { chromium } = require('playwright');

const arg = (n, d) => { const i = process.argv.indexOf('--' + n); return i > 0 ? process.argv[i + 1] : d; };
const BASE = arg('base', 'http://localhost:5411');
const USER = arg('user', 'Admin'), PASS = arg('password', 'Admin@123');

// The classes that make a Metronic menu behave like one. Width is deliberately excluded: the bell
// carries three lines per row and the account menu one, so 350 vs 275 is a real difference.
const CONTRACT = ['menu', 'menu-sub', 'menu-sub-dropdown', 'menu-column', 'menu-rounded',
                  'menu-gray-800', 'menu-state-bg', 'menu-state-color', 'fw-semibold', 'fs-6'];

(async () => {
  const browser = await chromium.launch();
  const ctx = await browser.newContext({ ignoreHTTPSErrors: true });
  const page = await ctx.newPage();
  const errs = [];
  page.on('console', m => { if (m.type() === 'error') errs.push(m.text().slice(0, 140)); });

  await page.goto(BASE + '/Account/Login', { waitUntil: 'domcontentloaded' });
  await page.fill('input[name="UserName"], input[name="Username"], input[name="Email"]', USER);
  await page.fill('input[name="Password"]', PASS);
  await page.click('button[type="submit"]');
  await page.waitForURL(u => !u.toString().includes('/Account/Login'), { timeout: 60000 }).catch(() => {});
  if (page.url().includes('/Account/Login')) { console.error('SIGN-IN FAILED'); process.exit(2); }

  for (const lang of ['en', 'ar']) {
    await ctx.addCookies([{ name: '.AspNetCore.Culture', value: `c=${lang}|uic=${lang}`, url: BASE }]);
    errs.length = 0;
    await page.goto(BASE + '/Calendar/Index', { waitUntil: 'networkidle' });
    await page.waitForFunction(() => {
      const l = document.getElementById('cbnb_list');
      return l && l.querySelectorAll('.cbnb-item').length > 0;
    }, { timeout: 20000 }).catch(() => {});

    const contract = await page.evaluate((CONTRACT) => {
      const list = document.getElementById('cbnb_list');
      const bell = list ? list.closest('.menu') : null;
      // The account menu is the other .menu-sub-dropdown that contains a menu-content block with an
      // avatar - identified by shape rather than by an id it does not have.
      const account = [...document.querySelectorAll('.menu-sub-dropdown')]
        .find(m => m !== bell && m.querySelector('.menu-content .symbol img'));
      const cls = (el) => (el ? [...el.classList] : []);
      const a = cls(account), b = cls(bell);
      return {
        foundAccount: !!account,
        accountMissing: CONTRACT.filter(c => !a.includes(c)),
        bellMissing: CONTRACT.filter(c => !b.includes(c)),
        // Does the bell use the account menu's own item/link structure?
        items: bell ? bell.querySelectorAll('.menu-item.cbnb-item').length : 0,
        links: bell ? bell.querySelectorAll('.menu-item.cbnb-item > .menu-link').length : 0,
        anchors: bell ? bell.querySelectorAll('.menu-item.cbnb-item > a.menu-link[href]').length : 0,
        badges: bell ? bell.querySelectorAll('.menu-item.cbnb-item .menu-badge').length : 0,
        texts: bell ? bell.querySelectorAll('.menu-item.cbnb-item .menu-text').length : 0,
        separators: bell ? bell.querySelectorAll('.separator').length : 0,
      };
    }, CONTRACT);

    console.log(`\n===== ${lang.toUpperCase()} =====`);
    console.log(`account menu found : ${contract.foundAccount}`);
    console.log(`contract missing   : account [${contract.accountMissing.join(', ') || 'none'}]`);
    console.log(`                   : bell    [${contract.bellMissing.join(', ') || 'none'}]`);
    console.log(`structure          : ${contract.items} menu-item, ${contract.links} menu-link (${contract.anchors} real anchors), ${contract.texts} menu-text, ${contract.badges} menu-badge, ${contract.separators} separator`);

    // THE BEHAVIOUR THE CONTRACT BUYS. menu-state-bg is only observable by moving the pointer, and
    // the pointer can only reach a row while the menu is OPEN - so the bell is clicked first. A
    // hover measured on a closed menu times out, which is what a first run of this probe did.
    await page.locator('[data-kt-menu-trigger] .ki-notification-on').first().click();
    await page.waitForSelector('#cbnb_list .menu-item.cbnb-item > .menu-link', { state: 'visible', timeout: 15000 });

    const row = page.locator('#cbnb_list .menu-item.cbnb-item > .menu-link').first();
    const rest = await row.evaluate(el => getComputedStyle(el).backgroundColor);
    await row.hover();
    await page.waitForTimeout(250);
    const hover = await row.evaluate(el => getComputedStyle(el).backgroundColor);
    const radius = await row.evaluate(el => getComputedStyle(el).borderRadius);
    console.log(`hover              : rest=${rest}  hover=${hover}  ${rest !== hover ? 'REACTS' : 'NO REACTION (menu-state-bg not taking effect)'}`);
    console.log(`item radius        : ${radius}  ${radius !== '0px' ? '(menu-rounded applied)' : '(square - menu-rounded not applied)'}`);
    console.log(`js errors          : ${errs.length ? errs.join(' | ') : 'none'}`);
  }
  await browser.close();
})();
