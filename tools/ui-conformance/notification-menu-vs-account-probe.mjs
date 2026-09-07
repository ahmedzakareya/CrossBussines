// Measures the notification dropdown against the ACCOUNT menu in the same layout - the reference
// the notification menu is meant to resemble - on the things that actually make them look alike or
// not: row height, how many filled backgrounds each contains, and label weight/colour.
//
// Matching the class contract was not enough on its own. With every class in place the rows were
// still visibly denser and louder than the reference, because this file was ADDING things on top of
// the contract: a hand-forced bold dark title over menu-gray-800/fw-semibold, a third text line,
// and a full-width tint on every unread row. Those only show up as numbers when the two menus are
// opened and measured against each other.
//
// It also re-checks the one behaviour the tint removal could silently break: the unread badge used
// to count down by testing for that tint class, so with the tint gone the countdown had to be
// re-keyed to the dot. A row is clicked and the dots are counted before and after.
//
//   node tools/ui-conformance/notification-menu-vs-account-probe.mjs --base http://localhost:5411
import { createRequire } from 'node:module';
const require = createRequire(new URL('./package.json', import.meta.url));
const { chromium } = require('playwright');

const arg = (n, d) => { const i = process.argv.indexOf('--' + n); return i > 0 ? process.argv[i + 1] : d; };
const BASE = arg('base', 'http://localhost:5411');
const USER = arg('user', 'Admin'), PASS = arg('password', 'Admin@123');

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

    // ONE MENU AT A TIME. A closed menu has no layout, so each has to be open to be measured - and
    // Metronic CLOSES the others when one opens, so they can never both be open. A first run of this
    // probe opened the account menu, then the bell, and reported the reference's height as 0px
    // because opening the bell had shut it again. Two passes, and the reference is measured first.
    //
    // The account trigger is the element the layout declares as
    //   <div class="cursor-pointer symbol symbol-circle ..." data-kt-menu-trigger="...">
    // - a looser "any avatar image" selector picked up the language flag inside the closed menu.
    await page.locator('div.cursor-pointer.symbol[data-kt-menu-trigger]').first().click();
    await page.waitForTimeout(500);
    const reference = await page.evaluate(() => {
      const isFilled = (el) => {
        const b = getComputedStyle(el).backgroundColor;
        return !!b && b !== 'rgba(0, 0, 0, 0)' && b !== 'transparent';
      };
      const account = [...document.querySelectorAll('.menu-sub-dropdown.show, .menu-sub-dropdown')]
        .find(x => !x.querySelector('#cbnb_list') && x.querySelector('.menu-content .symbol img'));
      if (!account) return null;
      const links = [...account.querySelectorAll('.menu-item > .menu-link')]
        .filter(l => l.getBoundingClientRect().height > 0);
      // Some reference rows wrap their label in a span (.menu-title / .menu-text) and some put the
      // text directly in the .menu-link - "My Profile" is bare text. Reading only the span reported
      // `undefined` for those and then claimed the bell did not match. Fall back to the link.
      const label = (links.length ? links[0].querySelector('.menu-text, .menu-title') : null) || links[0] || null;
      return {
        rows: links.length,
        rowH: links.length ? Math.round(links.reduce((a, e) => a + e.getBoundingClientRect().height, 0) / links.length) : 0,
        tinted: links.filter(l => isFilled(l) || isFilled(l.parentElement)).length,
        label: label ? { weight: getComputedStyle(label).fontWeight, color: getComputedStyle(label).color, size: getComputedStyle(label).fontSize } : null,
        lines: links.length ? links[0].querySelectorAll('.menu-text, .menu-title').length : 0,
      };
    });

    await page.keyboard.press('Escape');
    await page.waitForTimeout(300);
    await page.locator('[data-kt-menu-trigger] .ki-notification-on').first().click();
    await page.waitForSelector('#cbnb_list .menu-item.cbnb-item > .menu-link', { state: 'visible', timeout: 15000 });

    const m = await page.evaluate(() => {
      const isFilled = (el) => {
        const b = getComputedStyle(el).backgroundColor;
        return !!b && b !== 'rgba(0, 0, 0, 0)' && b !== 'transparent';
      };
      const list = document.getElementById('cbnb_list');
      const bell = list.closest('.menu');
      // The reference identified by SHAPE - a menu-content block holding an avatar - because it
      // carries no id of its own.
      const account = [...document.querySelectorAll('.menu-sub-dropdown')]
        .find(x => x !== bell && x.querySelector('.menu-content .symbol img'));

      const accLinks = account ? [...account.querySelectorAll('.menu-item > .menu-link')] : [];
      const accLabel = account
        ? account.querySelector('.menu-item > .menu-link .menu-text, .menu-item > .menu-link .menu-title')
        : null;
      const bellLinks = [...bell.querySelectorAll('.menu-item.cbnb-item > .menu-link')];
      const bellLabel = bellLinks.length ? bellLinks[0].querySelector('.menu-text > span') : null;

      const avgH = (els) => els.length
        ? Math.round(els.reduce((a, e) => a + e.getBoundingClientRect().height, 0) / els.length) : 0;
      const style = (el) => el
        ? { weight: getComputedStyle(el).fontWeight, color: getComputedStyle(el).color, size: getComputedStyle(el).fontSize }
        : null;

      return {
        accountRows: accLinks.length,
        accountRowH: avgH(accLinks),
        accountLabel: style(accLabel),
        // How many row-level elements paint a background in the reference. Expected: zero.
        accountTintedRows: accLinks.filter(l => isFilled(l) || isFilled(l.parentElement)).length,

        bellRows: bellLinks.length,
        bellRowH: avgH(bellLinks.slice(0, 10)),
        bellLabel: style(bellLabel),
        bellTintedRows: bellLinks.filter(l => isFilled(l) || isFilled(l.parentElement)).length,
        bellDots: bell.querySelectorAll('.cbnb-item .bullet-dot').length,
        // The text lines per row - the reference has one label (plus a value on the right).
        bellLinesPerRow: bellLinks.length ? bellLinks[0].querySelectorAll('.menu-text > span').length : 0,
      };
    });

    console.log(`\n===== ${lang.toUpperCase()} =====`);
    console.log(`account (reference) : ${reference?.rows} rows, avg height ${reference?.rowH}px, ${reference?.tinted} tinted, ${reference?.lines} text line/row`);
    console.log(`   label            : weight ${reference?.label?.weight}  size ${reference?.label?.size}  colour ${reference?.label?.color}`);
    console.log(`bell                : ${m.bellRows} rows, avg height ${m.bellRowH}px, ${m.bellTintedRows} tinted`);
    console.log(`   label            : weight ${m.bellLabel?.weight}  size ${m.bellLabel?.size}  colour ${m.bellLabel?.color}`);
    const sameLabel = reference?.label && m.bellLabel
      && reference.label.weight === m.bellLabel.weight
      && reference.label.color === m.bellLabel.color;
    console.log(`   label matches ref: ${sameLabel ? 'YES' : 'NO - still overriding the menu'}`);
    console.log(`   tint             : ${m.bellTintedRows === 0 ? 'none, as in the reference' : 'PRESENT - the reference has none'}`);
    console.log(`   text lines/row   : ${m.bellLinesPerRow} (reference shows 1 label + a value on the right)`);
    console.log(`   unread dots      : ${m.bellDots}`);

    // THE BEHAVIOUR THE TINT REMOVAL COULD HAVE BROKEN.
    const before = (await page.locator('#cbnb_badge').textContent() || '').trim();
    const dotsBefore = m.bellDots;
    await page.evaluate(() => {
      const row = [...document.querySelectorAll('#cbnb_list .cbnb-item')].find(r => r.querySelector('.bullet-dot'));
      if (!row) return;
      // Strip the href so the click records the read without navigating away mid-measurement.
      const a = row.querySelector('a.menu-link');
      if (a) a.removeAttribute('href');
      row.click();
    });
    await page.waitForTimeout(400);
    const after = (await page.locator('#cbnb_badge').textContent() || '').trim();
    const dotsAfter = await page.evaluate(() => document.querySelectorAll('#cbnb_list .cbnb-item .bullet-dot').length);
    console.log(`   click one unread : badge "${before}" -> "${after}",  dots ${dotsBefore} -> ${dotsAfter}  ${dotsAfter === dotsBefore - 1 ? 'COUNTS DOWN' : 'DID NOT COUNT DOWN'}`);
    console.log(`js errors           : ${errs.length ? errs.join(' | ') : 'none'}`);
  }
  await browser.close();
})();
