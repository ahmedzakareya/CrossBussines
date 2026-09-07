// Measures whether an UNREAD notification row is actually distinguishable from a READ one, and
// whether a read row is still legible after being de-emphasised.
//
// This exists because of a real regression cycle in this file:
//
//   1. unread rows carried bg-light-primary. Measured: 39 of the 50 rows the menu shows are
//      unread, so the band painted 78% of the list and marked nothing - it read as zebra striping.
//   2. the band was removed to match the account menu, which has no filled backgrounds. That left
//      one small dot in the right-hand slot as the ENTIRE unread signal - no distinction at all.
//   3. the emphasis was inverted instead: unread is the full-strength row, read recedes. The 11
//      rows change rather than the 39.
//
// Both failures were invisible to every static check - the classes were valid in all three
// versions. What separates them is the computed weight and colour of the two states side by side,
// and whether the recessive one still clears the contrast floor for its own primary text.
//
//   node tools/ui-conformance/notification-read-state-probe.mjs --base http://localhost:5411
import { createRequire } from 'node:module';
const require = createRequire(new URL('./package.json', import.meta.url));
const { chromium } = require('playwright');

const arg = (n, d) => { const i = process.argv.indexOf('--' + n); return i > 0 ? process.argv[i + 1] : d; };
const BASE = arg('base', 'http://localhost:5411');
const USER = arg('user', 'Admin'), PASS = arg('password', 'Admin@123');

const lum = ([r, g, b]) => { const f = c => { c /= 255; return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4); }; return 0.2126 * f(r) + 0.7152 * f(g) + 0.0722 * f(b); };
const ratio = (a, b) => { const [x, y] = [lum(a), lum(b)].sort((p, q) => q - p); return (x + 0.05) / (y + 0.05); };
const rgb = s => (s.match(/\d+(\.\d+)?/g) || []).slice(0, 3).map(Number);

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
    await page.locator('[data-kt-menu-trigger] .ki-notification-on').first().click();
    await page.waitForSelector('#cbnb_list .menu-item.cbnb-item > .menu-link', { state: 'visible', timeout: 15000 });

    const m = await page.evaluate(() => {
      const pick = (sel) => document.querySelector('#cbnb_list ' + sel);
      const read = (row) => {
        if (!row) return null;
        const title = row.querySelector('.menu-text > span');
        const body = row.querySelector('.menu-text > span + span');
        const sym = row.querySelector('.symbol');
        const link = row.querySelector('.menu-link');
        const cs = (el) => (el ? getComputedStyle(el) : null);
        return {
          title: title ? { weight: cs(title).fontWeight, color: cs(title).color, text: title.textContent.trim().slice(0, 34) } : null,
          body: body ? { color: cs(body).color } : null,
          symOpacity: sym ? cs(sym).opacity : null,
          dot: !!row.querySelector('.bullet-dot'),
          rowBg: cs(link).backgroundColor,
          pageBg: getComputedStyle(row.closest('.menu')).backgroundColor,
          // THE GROUND THE TEXT ACTUALLY SITS ON. A shaded row has its own background, so measuring
          // against the menu's white describes a pixel that is not there. Walk up from the text
          // until something actually paints.
          textGround: (function () {
            var el = title || link;
            while (el) {
              var b = getComputedStyle(el).backgroundColor;
              if (b && b !== 'rgba(0, 0, 0, 0)' && b !== 'transparent') return b;
              el = el.parentElement;
            }
            return 'rgb(255, 255, 255)';
          })(),
        };
      };
      return {
        unread: read(pick('.cbnb-item:not(.cbnb-read)')),
        alreadyRead: read(pick('.cbnb-item.cbnb-read')),
        counts: {
          unread: document.querySelectorAll('#cbnb_list .cbnb-item:not(.cbnb-read)').length,
          read: document.querySelectorAll('#cbnb_list .cbnb-item.cbnb-read').length,
        },
      };
    });

    console.log(`\n===== ${lang.toUpperCase()} =====`);
    console.log(`rows              : ${m.counts.unread} unread, ${m.counts.read} read`);
    for (const [name, st] of [['UNREAD', m.unread], ['READ  ', m.alreadyRead]]) {
      if (!st) { console.log(`${name}            : none present`); continue; }
      // Against the row's OWN ground, which for a shaded unread row is the shade.
      const ground = rgb(st.textGround);
      const tr = ratio(rgb(st.title.color), ground);
      const br = ratio(rgb(st.body.color), ground);
      console.log(`${name}            : "${st.title.text}"   on ${st.textGround}`);
      console.log(`   title          : weight ${st.title.weight}  ${st.title.color}  ratio ${tr.toFixed(2)} ${tr >= 4.5 ? 'PASS' : 'FAIL'}`);
      console.log(`   body           : ${st.body.color}  ratio ${br.toFixed(2)} ${br >= 4.5 ? 'PASS' : 'FAIL'}`);
      console.log(`   symbol opacity : ${st.symOpacity}    dot: ${st.dot}    row bg: ${st.rowBg}`);
    }

    if (m.unread && m.alreadyRead) {
      const distinct = m.unread.title.weight !== m.alreadyRead.title.weight
        || m.unread.title.color !== m.alreadyRead.title.color
        || m.unread.symOpacity !== m.alreadyRead.symOpacity
        || m.unread.dot !== m.alreadyRead.dot;
      const signals = [
        m.unread.title.weight !== m.alreadyRead.title.weight ? 'title weight' : null,
        m.unread.title.color !== m.alreadyRead.title.color ? 'title colour' : null,
        m.unread.body.color !== m.alreadyRead.body.color ? 'body colour' : null,
        m.unread.symOpacity !== m.alreadyRead.symOpacity ? 'symbol opacity' : null,
        m.unread.dot !== m.alreadyRead.dot ? 'dot' : null,
      ].filter(Boolean);
      console.log(`DISTINGUISHABLE   : ${distinct ? 'YES' : 'NO'} - ${signals.length} independent signals [${signals.join(', ')}]`);
      const fills = [m.unread.rowBg, m.alreadyRead.rowBg]
        .filter(c => c && c !== 'rgba(0, 0, 0, 0)' && c !== 'transparent').length;
      console.log(`filled row bgs    : ${fills} ${fills === 0 ? '(none - the reference has none either)' : '(the reference has none)'}`);
    }

    // Clicking an unread row must make the WHOLE row recede, not just drop its dot.
    const before = await page.evaluate(() => document.querySelectorAll('#cbnb_list .cbnb-item.cbnb-read').length);
    const changed = await page.evaluate(() => {
      const row = document.querySelector('#cbnb_list .cbnb-item:not(.cbnb-read)');
      if (!row) return null;
      const a = row.querySelector('a.menu-link');
      if (a) a.removeAttribute('href');
      row.click();
      const t = row.querySelector('.menu-text > span');
      const sym = row.querySelector('.symbol');
      return {
        nowRead: row.classList.contains('cbnb-read'),
        weight: getComputedStyle(t).fontWeight,
        color: getComputedStyle(t).color,
        symOpacity: getComputedStyle(sym).opacity,
        dotGone: !row.querySelector('.bullet-dot'),
      };
    });
    const after = await page.evaluate(() => document.querySelectorAll('#cbnb_list .cbnb-item.cbnb-read').length);
    if (changed) {
      const matchesRead = m.alreadyRead
        && changed.weight === m.alreadyRead.title.weight
        && changed.color === m.alreadyRead.title.color
        && changed.symOpacity === m.alreadyRead.symOpacity;
      console.log(`click one unread  : read rows ${before} -> ${after}, dot gone ${changed.dotGone}, now looks like a read row: ${matchesRead ? 'YES' : 'NO'}`);
    }
    console.log(`js errors         : ${errs.length ? errs.join(' | ') : 'none'}`);
  }
  await browser.close();
})();
