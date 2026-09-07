// ============================================================================================
// REPORT VIEWER (/Reports/Viewer/{code}) — LANGUAGE PROBE.
//
// The report: Arabic text on the Viewer while the UI language is English. Two distinct causes,
// both fixed, and this probe holds each one down separately:
//
//   1. THE SAVED-LAYOUT CHIPS.  ReportStudioService.SaveAsync ran `NameEn = name`, copying whatever
//      the author typed into BOTH name columns. Nine layouts were saved from the designer with an
//      Arabic name, so the English column held Arabic and every presenter's correct
//      `arabic ? Name : (NameEn ?? Name)` had nothing English to show. The copy is gone; the nine
//      rows it already wrote were given a real English name.
//
//   2. THE RUN-HISTORY "BY" COLUMN.  ReportHistoryService selected Employee.FullName alone, so the
//      person who ran the report was named in Arabic. It now resolves FullNameEn by UI language.
//      (No data work was needed: all eleven employees in the run history already had one.)
//
// It also SWEEPS the whole English page for Arabic script and reports every element that still
// carries any, so a leak somewhere this change did not look at is named rather than missed. The
// sweep separates the SCREEN from the SHELL: the header's user-name dropdown renders
// @employee.FullName with no English twin, which is a real defect but lives in four SHARED layouts
// (SHF-06, Integration Owner approval) and shows on every screen in the product, not just this one.
// It is reported as a note naming the file so it can be routed, rather than as a failure of this fix
// or as something quietly excluded.
//
// AND IT CHECKS THE OTHER DIRECTION. A common way to "fix" this is to show the English name always,
// which silently breaks the Arabic screen. So the same page is loaded in Arabic and the chips must
// read Arabic there.
//
// IT NEVER WRITES. Every step is a GET.
//
//   node tools/ui-conformance/reports-viewer-language-probe.mjs --base http://localhost:5411 \
//        --user Admin --password <chosen at call time>
//
//   --code       report code to open (default Accounting.SalesRevenue, the one reported)
//   --headless   run without a window
// ============================================================================================

import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const HERE = dirname(fileURLToPath(import.meta.url));
const { chromium } = createRequire(import.meta.url)(join(HERE, 'node_modules', 'playwright'));

const arg = (name, fallback) => {
  const i = process.argv.indexOf('--' + name);
  return i >= 0 && process.argv[i + 1] && !process.argv[i + 1].startsWith('--')
    ? process.argv[i + 1] : fallback;
};
const flag = name => process.argv.includes('--' + name);

const BASE = arg('base', 'http://localhost:5411').replace(/\/+$/, '');
const USER = arg('user', 'Admin');
const PASS = arg('password', '');
const CODE = arg('code', 'Accounting.SalesRevenue');
const OUT = arg('out', join(HERE, 'artifacts', 'reports-viewer-language'));
const HEADLESS = flag('headless');

if (!PASS) {
  console.error('FAIL: --password is required; it is never stored in this repository.');
  process.exit(2);
}
mkdirSync(OUT, { recursive: true });

const steps = [];
const record = (name, ok, detail) => {
  steps.push({ name, ok: !!ok, detail: detail == null ? '' : String(detail) });
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? '  --  ' + detail : ''}`);
};
const note = (name, detail) => {
  steps.push({ name, ok: null, detail: String(detail) });
  console.log(`NOTE  ${name}  --  ${detail}`);
};

function finish() {
  const hard = steps.filter(s => s.ok === false);
  writeFileSync(join(OUT, 'result.json'), JSON.stringify({ base: BASE, code: CODE, steps }, null, 2));
  console.log(`\n${steps.filter(s => s.ok === true).length} passed, ${hard.length} failed, ` +
    `${steps.filter(s => s.ok === null).length} noted   (artifacts: ${OUT})`);
  process.exit(hard.length ? 1 : 0);
}

// U+0600..U+06FF plus the Arabic presentation forms. Tested per element rather than over the whole
// page text, so a hit can be pointed at.
const ARABIC = /[؀-ۿݐ-ݿﭐ-﷿ﹰ-﻿]/;

(async () => {
  const browser = await chromium.launch({
    headless: HEADLESS,
    slowMo: HEADLESS ? 0 : 250,
    args: ['--ignore-certificate-errors'],
  });
  const context = await browser.newContext({
    viewport: { width: 1680, height: 1100 },
    ignoreHTTPSErrors: true,
  });
  const page = await context.newPage();

  await page.goto(BASE + '/Account/Login', { waitUntil: 'domcontentloaded' });
  await page.fill('input[name="UserName"], input[name="Username"], input[name="Email"]', USER);
  await page.fill('input[name="Password"]', PASS);
  await page.locator('#loginForm button[type="submit"], form button[type="submit"]').first().click();
  await page.waitForURL(u => !u.toString().includes('/Account/Login'), { timeout: 60000 }).catch(() => {});
  const signedIn = !page.url().includes('/Account/Login');
  record(`sign in as ${USER}`, signedIn, page.url());
  if (!signedIn) { await browser.close(); return finish(); }

  const setCulture = c => context.addCookies([{
    name: '.AspNetCore.Culture', value: `c%3D${c}%7Cuic%3D${c}`, url: BASE,
  }]);
  const open = async () => {
    await page.goto(`${BASE}/Reports/Viewer/${CODE}`, { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(1000);
  };

  // The saved-layout strip is the card holding the "Saved layouts" / "التصاميم المحفوظة" label; the
  // chips are its links. Addressed by the strip's own links so the sidebar's menu is never counted.
  const chipTexts = async () => {
    const strip = page.locator('.card', { has: page.locator('a[href*="templateId="]') }).first();
    const links = strip.locator('a[href*="templateId="]');
    const n = await links.count().catch(() => 0);
    const out = [];
    for (let i = 0; i < n; i++) {
      // The scope badge is a child of the chip; only the chip's own name matters here.
      const t = (await links.nth(i).innerText().catch(() => '')).split('\n')[0].replace(/\s+/g, ' ').trim();
      if (t) out.push(t);
    }
    return out;
  };

  const historyBy = async () => {
    const card = page.locator('.card')
      .filter({ has: page.locator('a[href*="/Reports/Rerun/"]') }).first();
    const rows = card.locator('tbody tr');
    const n = await rows.count().catch(() => 0);
    const out = [];
    for (let i = 0; i < n; i++) {
      if (await rows.nth(i).locator('td[colspan]').count().catch(() => 0)) continue;
      const cells = rows.nth(i).locator('td');
      if (await cells.count() < 3) continue;
      const by = (await cells.nth(2).innerText().catch(() => '')).replace(/\s+/g, ' ').trim();
      if (by) out.push(by);
    }
    return out;
  };

  // ---- ENGLISH -------------------------------------------------------------------------------
  await setCulture('en');
  await open();
  await page.screenshot({ path: join(OUT, 'en-viewer.png'), fullPage: true }).catch(() => {});

  const enChips = await chipTexts();
  const arabicChips = enChips.filter(t => ARABIC.test(t));
  record('English page: no Arabic in the saved-layout chips',
    enChips.length > 0 && arabicChips.length === 0,
    `${enChips.length} chips, ${arabicChips.length} Arabic${arabicChips.length ? ': ' + arabicChips.slice(0, 3).join(' | ') : ''}`);

  const enBy = await historyBy();
  const arabicBy = enBy.filter(t => ARABIC.test(t));
  record('English page: no Arabic in the run-history By column',
    enBy.length > 0 && arabicBy.length === 0,
    `${enBy.length} rows, ${arabicBy.length} Arabic${arabicBy.length ? ': ' + arabicBy.slice(0, 3).join(' | ') : ''}`);
  note('By column, as rendered in English', enBy.slice(0, 5).join(' | ') || '(no rows)');

  // ---- THE SWEEP -----------------------------------------------------------------------------
  // Every leaf element carrying Arabic script, with the path to it, so a remaining leak is named.
  const sweep = await page.evaluate(sel => {
    const re = new RegExp(sel);
    const out = [], shellLeaks = [];
    const own2 = el => Array.from(el.childNodes).filter(n => n.nodeType === 3)
      .map(n => n.textContent).join(' ').replace(/\s+/g, ' ').trim().slice(0, 60);
    const path = el => {
      const bits = [];
      for (let n = el; n && n.nodeType === 1 && bits.length < 4; n = n.parentElement) {
        bits.unshift(n.tagName.toLowerCase() +
          (n.className && typeof n.className === 'string'
            ? '.' + n.className.trim().split(/\s+/).slice(0, 2).join('.') : ''));
      }
      return bits.join(' > ');
    };
    for (const el of document.querySelectorAll('body *')) {
      if (el.closest('.app-sidebar, #kt_app_sidebar, script, style, select, option')) continue;
      // The shell, not the screen: the header's user dropdown and the language switcher. The switcher
      // is correct as it is - a language menu names each language in its own language.
      const shell = el.closest('#kt_app_header, .app-header, .menu-sub-dropdown');
      if (shell) { shellLeaks.push({ where: path(el), text: own2(el) }); continue; }
      // Leaf text only: a container would report its children's text as its own.
      const own = Array.from(el.childNodes)
        .filter(n => n.nodeType === 3).map(n => n.textContent).join(' ').replace(/\s+/g, ' ').trim();
      if (own && re.test(own)) out.push({ where: path(el), text: own.slice(0, 60) });
    }
    return { out, shellLeaks: shellLeaks.filter(l => l.text && re.test(l.text)) };
  }, ARABIC.source);

  if (sweep.out.length === 0) {
    record("English page: no Arabic anywhere in the screen's own content", true);
  } else {
    record("English page: no Arabic anywhere in the screen's own content", false,
      sweep.out.slice(0, 6).map(l => `${l.where} = "${l.text}"`).join('  ||  '));
  }

  for (const l of sweep.shellLeaks) {
    note('shell, not this screen', `${l.where} = "${l.text}"`);
  }
  if (sweep.shellLeaks.length) {
    note('where the shell leak lives',
      '@employee.FullName in _LayoutInventory / _LayoutAccounting / _LayoutBackend / ' +
      '_LayoutManufacturing (SHARED, SHF-06). Employee.FullNameEn exists and is populated.');
  }

  // ---- ARABIC, so the fix did not simply hardcode English ------------------------------------
  await setCulture('ar');
  await open();
  await page.screenshot({ path: join(OUT, 'ar-viewer.png'), fullPage: true }).catch(() => {});

  const arChips = await chipTexts();
  const arabicPresent = arChips.filter(t => ARABIC.test(t));
  record('Arabic page: the saved-layout chips still read Arabic',
    arChips.length > 0 && arabicPresent.length > 0,
    `${arChips.length} chips, ${arabicPresent.length} Arabic`);

  const arBy = await historyBy();
  record('Arabic page: the By column reads Arabic',
    arBy.length > 0 && arBy.some(t => ARABIC.test(t)),
    arBy.slice(0, 4).join(' | ') || '(no rows)');

  await browser.close();
  finish();
})().catch(e => {
  record('probe crashed', false, String(e.message || e).replace(/\s+/g, ' ').slice(0, 300));
  finish();
});
