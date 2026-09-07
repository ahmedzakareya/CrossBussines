// ============================================================================================
// EMPLOYEE NAMES ACROSS THE PRODUCT — LANGUAGE PROBE.
//
// Employee carries FullName (Arabic, required) and FullNameEn (English, optional). An audit of the
// whole solution found 66 places in 40 files that rendered FullName alone, so an English screen
// showed "احمد زكريا" in the HR dashboard's leave list, the report run history, the calendar
// attendees, the employee pickers, the assignee columns and the header user menu.
//
// They now all go through CrossBuy.BL.EmployeeNames, and this probe holds the result down from the
// OUTSIDE, in a real browser.
//
// IT TARGETS THE ELEMENT THAT HOLDS A PERSON'S NAME, not the whole page. An earlier version failed
// any Arabic anywhere in the content and was wrong four times out of seven - it flagged calendar
// EVENT TITLES, APPRAISAL CYCLE names and HIERARCHICAL BRANCH names, none of which are employee
// names, and it flagged the employees list, which shows both names side by side on purpose because
// it is the master-data screen for exactly those two fields. Those are recorded at the bottom as
// separate findings rather than folded into this one.
//
// Each screen declares WHERE its employee name is and WHAT must be true:
//
//   english : that element must carry no Arabic script
//   arabic  : the same element must carry Arabic again - the easy wrong fix is to show the English
//             name always, which breaks the Arabic UI silently and would otherwise pass
//
// IT NEVER WRITES. Every step is a GET.
//
//   node tools/ui-conformance/employee-name-language-probe.mjs --base http://localhost:5411 \
//        --user Admin --password <chosen at call time>
//
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
const OUT = arg('out', join(HERE, 'artifacts', 'employee-names'));
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

const ARABIC = /[؀-ۿݐ-ݿﭐ-﷿ﹰ-﻿]/;

// WHERE the employee name is on each screen. `cells` returns the strings to judge.
const SCREENS = [
  {
    path: '/Admin/Index',
    what: 'HR dashboard, recent leave requests - the reported screen',
    cells: async page => {
      const card = page.locator('.card').filter({ hasText: /Recent leave requests|أحدث طلبات/ }).first();
      return (await card.locator('tbody tr td:first-child').allInnerTexts())
        .map(t => t.replace(/\s+/g, ' ').trim()).filter(Boolean);
    },
  },
  {
    path: '/Reports/Viewer/Accounting.SalesRevenue',
    what: 'report viewer, run-history By column',
    cells: async page => {
      const card = page.locator('.card').filter({ has: page.locator('a[href*="/Reports/Rerun/"]') }).first();
      const rows = card.locator('tbody tr');
      const out = [];
      for (let i = 0; i < await rows.count(); i++) {
        if (await rows.nth(i).locator('td[colspan]').count()) continue;
        out.push((await rows.nth(i).locator('td').nth(2).innerText()).replace(/\s+/g, ' ').trim());
      }
      return out.filter(Boolean);
    },
  },
  {
    path: '/Inventory/Warehouses',
    what: 'warehouse keeper picker',
    cells: async page => (await page.locator('select[name="KeeperEmployeeId"] option').allInnerTexts())
      .map(t => t.trim()).filter(t => t && t !== '—'),
  },
  {
    path: '/Admin/Appraisals',
    what: 'appraisal employee pickers',
    cells: async page => (await page.locator('select[name="employeeId"] option, select[name="managerId"] option')
      .allInnerTexts()).map(t => t.trim()).filter(t => t && t !== '—'),
  },
  {
    path: '/Account/Settings',
    what: 'account settings, own name',
    // Scoped to the Account-info card. A bare .fw-bold.text-gray-900 matched a HIDDEN element in the
    // header first, and innerText on a hidden node is the empty string - the probe reported no values
    // on a screen that was rendering the name correctly.
    cells: async page => {
      const card = page.locator('.card').filter({ hasText: /Account info|بيانات الحساب/ }).first();
      const t = await card.locator('.fw-bold.text-gray-900').first().innerText().catch(() => '');
      return [t.replace(/\s+/g, ' ').trim()].filter(Boolean);
    },
  },
];

function finish() {
  const hard = steps.filter(s => s.ok === false);
  writeFileSync(join(OUT, 'result.json'), JSON.stringify({ base: BASE, steps }, null, 2));
  console.log(`\n${steps.filter(s => s.ok === true).length} passed, ${hard.length} failed, ` +
    `${steps.filter(s => s.ok === null).length} noted   (artifacts: ${OUT})`);
  process.exit(hard.length ? 1 : 0);
}

(async () => {
  const browser = await chromium.launch({
    headless: HEADLESS, slowMo: HEADLESS ? 0 : 200, args: ['--ignore-certificate-errors'],
  });
  const context = await browser.newContext({
    viewport: { width: 1680, height: 1100 }, ignoreHTTPSErrors: true,
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

  const visit = async (path, name) => {
    await page.goto(BASE + path, { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(1500);
    await page.screenshot({ path: join(OUT, name + '.png'), fullPage: false }).catch(() => {});
  };

  // ---- ENGLISH -------------------------------------------------------------------------------
  await setCulture('en');
  const seen = {};
  for (const s of SCREENS) {
    await visit(s.path, 'en' + s.path.replace(/[\/.]/g, '_'));
    const cells = await s.cells(page).catch(() => []);
    seen[s.path] = cells;
    const arabic = cells.filter(t => ARABIC.test(t));
    record(`EN ${s.what}`, cells.length > 0 && arabic.length === 0,
      `${cells.length} values, ${arabic.length} Arabic` +
      (arabic.length ? ': ' + arabic.slice(0, 3).join(' | ') : ': ' + cells.slice(0, 3).join(' | ')));
  }

  // ---- THE HEADER, on every one of those screens ----------------------------------------------
  const headerName = async () => (await page.locator(
    '#kt_app_header .menu-content .fw-bold, .app-header .menu-content .fw-bold').first()
    .innerText().catch(() => '')).replace(/\s+/g, ' ').replace(/\s*Pro\s*$/, '').trim();
  await visit('/Admin/Index', 'en-header');
  const enHeader = await headerName();
  record('EN header user menu', !!enHeader && !ARABIC.test(enHeader), JSON.stringify(enHeader));

  // ---- ARABIC: the names must come BACK -------------------------------------------------------
  await setCulture('ar');
  for (const s of SCREENS) {
    await visit(s.path, 'ar' + s.path.replace(/[\/.]/g, '_'));
    const cells = await s.cells(page).catch(() => []);
    record(`AR ${s.what} - Arabic is back`, cells.length > 0 && cells.some(t => ARABIC.test(t)),
      cells.slice(0, 3).join(' | '));
  }
  await visit('/Admin/Index', 'ar-header');
  const arHeader = await headerName();
  record('AR header user menu - Arabic is back', !!arHeader && ARABIC.test(arHeader),
    JSON.stringify(arHeader));

  // ---- ADJACENT FINDINGS: Arabic that is NOT an employee name ---------------------------------
  // Found by the earlier page-wide sweep. Reported here so they are not mistaken for part of this
  // change, and not lost either. Each is the same shape - a bilingual pair where the English half is
  // empty or unread - but a different entity, so a different fix.
  note('other entity: calendar EVENT TITLES',
    'CalendarEvent.TitleEn is empty on the seeded events, so /Calendar shows Arabic titles in English. Data, not code.');
  note('other entity: APPRAISAL cycle and template names',
    'AppraisalCycle.Name / AppraisalTemplate.Name are rendered without their NameEn twin on /Admin/Appraisals.');
  note('other entity: HIERARCHICAL branch names',
    'The branch picker on /Inventory/Warehouses renders Hierarchicals through Opts(branches, isAr); the Arabic options are branch names.');
  note('by design: /Admin/EmployeesList shows BOTH names',
    'It is the master-data screen for FullName and FullNameEn, current language first. Not a defect.');

  await browser.close();
  finish();
})().catch(e => {
  record('probe crashed', false, String(e.message || e).replace(/\s+/g, ' ').slice(0, 300));
  finish();
});
