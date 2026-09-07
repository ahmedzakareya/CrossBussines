// ============================================================================================
// REPORTS CENTER (/Reports) — PAGING PROBE.
//
// The reported defect: "the tables on this page have no paging and the data is presented badly."
// Two of the three tables were at fault, in different ways:
//
//   Saved reports — NO limit at all. The presenter looped over every visible report definition and
//                   appended every one of its templates, so the card rendered all 58 of them at once.
//   Recent runs   — a silent ceiling. Take = 12 with no way to ask for row 13, so 243 of this
//                   company's 255 run rows were unreachable from this screen.
//   Archive       — also capped at 12, and NOT fixed: IReportArchiveService.ListAsync accepts a
//                   `take` and no `skip`, so paging it means changing that service's contract.
//                   It holds no rows today. This probe reports that state rather than passing over it.
//
// Both fixed tables use THE PAGER THE REST OF THE PRODUCT USES — the one renderPager() in
// Backend-assets/js/crossbuy-tagify.js builds for Items, StockBalances, Customers and the other
// list screens: a muted "1-10 of 58" beside a numbered pagination list with chevrons and an
// ellipsis. This probe asserts that shape specifically, because a one-off pager on one screen is a
// defect of its own even when it pages correctly.
//
// WHAT IT ASSERTS, from the rendered page rather than from the code:
//
//   1. each table renders at most its page size
//   2. the pager is the house pager: a range line, numbered pages, an active page, chevrons
//   3. page 2 holds DIFFERENT rows than page 1 (no overlap) — the check a bad sort order would
//      fail while still looking paged
//   4. the two tables page INDEPENDENTLY: moving one leaves the other on its own page
//   5. a page link carries the active filters, so paging does not silently reset a search
//   6. a page number past the end lands on the last real page, not on an empty table
//
// IT NEVER WRITES. Every step is a GET of /Reports.
//
//   node tools/ui-conformance/reports-center-paging-probe.mjs --base https://localhost:44368 \
//        --user dev.superadmin --password <chosen at call time>
//
//   --headless   run without a window (default: a VISIBLE window, slowed down to be watchable)
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

const BASE = arg('base', 'https://localhost:44368').replace(/\/+$/, '');
const USER = arg('user', 'dev.superadmin');
const PASS = arg('password', '');
const OUT = arg('out', join(HERE, 'artifacts', 'reports-paging'));
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
const shot = async (page, name) =>
  page.screenshot({ path: join(OUT, `${name}.png`), fullPage: false }).catch(() => {});

// A card is addressed by its own heading — the view puts no id on either table, and the heading is
// what a reader of the screen would use to tell the two apart.
const card = (page, heading) =>
  page.locator('.card').filter({ has: page.getByRole('heading', { name: heading, exact: true }) }).first();

// The rows of one card's table, IDENTIFIED BY THE ROW'S OWN LINK rather than by its visible text.
// Text is not an identity here: this database holds nine distinct saved templates all named
// "Sales register - designed" under the same report, so a text comparison reports rows as shared
// across pages when the pages are in fact disjoint. Every row's first anchor carries the record's
// key (templateId= for a saved report, the run id for a run), which is unique.
const rowsOf = async (page, heading) => {
  const c = card(page, heading);
  const trs = c.locator('tbody tr');
  const n = await trs.count().catch(() => 0);
  const out = [];
  for (let i = 0; i < n; i++) {
    const tr = trs.nth(i);
    // The "nothing here yet" row is a single <td colspan>. Counting it as data reported the archive
    // as holding one row when the screen and the KPI tile both said zero.
    if (await tr.locator('td[colspan]').count().catch(() => 0)) continue;
    const text = (await tr.innerText().catch(() => '')).replace(/\s+/g, ' ').trim();
    if (!text) continue;
    const href = await tr.locator('a[href]').first().getAttribute('href').catch(() => null);
    out.push(href || text);                       // href when there is one, text as a last resort
  }
  return out;
};

const pagerOf = (page, heading) => card(page, heading).locator('ul.pagination').first();

function finish() {
  const hard = steps.filter(s => s.ok === false);
  writeFileSync(join(OUT, 'result.json'), JSON.stringify({ base: BASE, steps }, null, 2));
  console.log(`\n${steps.filter(s => s.ok === true).length} passed, ${hard.length} failed, ` +
    `${steps.filter(s => s.ok === null).length} noted   (artifacts: ${OUT})`);
  process.exit(hard.length ? 1 : 0);
}

(async () => {
  const browser = await chromium.launch({
    headless: HEADLESS,
    slowMo: HEADLESS ? 0 : 250,
    args: ['--ignore-certificate-errors'],
  });
  const context = await browser.newContext({
    viewport: { width: 1680, height: 1000 },
    ignoreHTTPSErrors: true,
  });
  const page = await context.newPage();

  const errors = [];
  // The STACK, not just the message: a bare "cannot read properties of null" says nothing about
  // which script produced it, and this page loads the whole theme bundle.
  page.on('pageerror', e => errors.push('pageerror: ' + String(e.stack || e).slice(0, 400)));
  page.on('console', m => { if (m.type() === 'error') errors.push('console: ' + m.text().slice(0, 160)); });

  // ---- sign in ------------------------------------------------------------------------------
  await page.goto(BASE + '/Account/Login', { waitUntil: 'domcontentloaded' });
  await page.fill('input[name="UserName"], input[name="Username"], input[name="Email"]', USER);
  await page.fill('input[name="Password"]', PASS);
  // The FORM's own submit, not the first submit-looking button on the page.
  await page.locator('#loginForm button[type="submit"], form button[type="submit"]').first().click();
  await page.waitForURL(u => !u.toString().includes('/Account/Login'), { timeout: 60000 }).catch(() => {});
  const signedIn = !page.url().includes('/Account/Login');
  record(`sign in as ${USER}`, signedIn, page.url());
  if (!signedIn) { await shot(page, '00-login-failed'); await browser.close(); return finish(); }

  // English, the culture the owner is testing in.
  await context.addCookies([{ name: '.AspNetCore.Culture', value: 'c%3Den%7Cuic%3Den', url: BASE }]);

  // ---- 1. page 1 ----------------------------------------------------------------------------
  // The error bucket is emptied HERE, not at the start of the run. Sign-in lands on /Portal/Choose,
  // whose own theme scripts are still settling when the next navigation begins, and leaving them in
  // the bucket reported a portal error as an error on /Reports. Measured separately: /Reports,
  // /Inventory/Reports, /Inventory/Items and /Accounting/IncomeStatement all load with none.
  errors.length = 0;
  await page.goto(BASE + '/Reports', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(800);
  await shot(page, '01-page1');

  const saved1 = await rowsOf(page, 'Saved reports');
  const recent1 = await rowsOf(page, 'Recent runs');
  const archive1 = await rowsOf(page, 'Archive');

  record('saved reports: at most one page of rows (<= 10)', saved1.length > 0 && saved1.length <= 10,
    `${saved1.length} rows`);
  record('recent runs: at most one page of rows (<= 12)', recent1.length > 0 && recent1.length <= 12,
    `${recent1.length} rows`);
  note('archive rows', `${archive1.length} (unpaged: IReportArchiveService.ListAsync has no skip)`);

  // ---- 2. the pagers exist and say where we are ---------------------------------------------
  record('saved reports: pager rendered', await pagerOf(page, 'Saved reports').isVisible().catch(() => false));
  record('recent runs: pager rendered', await pagerOf(page, 'Recent runs').isVisible().catch(() => false));

  // The house pager puts the range in the sibling div of the <ul>, the same place cbServerList's
  // #pageInfo sits.
  const rangeOf = async heading => (await card(page, heading)
    .locator('.pagination').first().locator('xpath=preceding-sibling::div[1]')
    .innerText().catch(() => '')).replace(/\s+/g, ' ').trim();
  const savedRange = await rangeOf('Saved reports');
  const recentRange = await rangeOf('Recent runs');
  // "1-10 of 58" in English, "1-10 من 58" in Arabic. En-dash, as the house pager uses.
  record('saved reports: house range line', /^1\u2013\d+ of [\d,]+$/.test(savedRange), savedRange);
  record('recent runs: house range line', /^1\u2013\d+ of [\d,]+$/.test(recentRange), recentRange);

  // NUMBERED pages with one marked active, and a chevron at each end — not a bespoke Prev/Next.
  const shapeOf = async heading => {
    const ul = pagerOf(page, heading);
    const items = await ul.locator('li').allInnerTexts().catch(() => []);
    const numbers = items.map(t => t.trim()).filter(t => /^\d+$/.test(t));
    const active = (await ul.locator('li.active').innerText().catch(() => '')).trim();
    const chevrons = await ul.locator('li i.ki-outline').count().catch(() => 0);
    return { numbers, active, chevrons };
  };
  for (const heading of ['Saved reports', 'Recent runs']) {
    const sh = await shapeOf(heading);
    record(`${heading.toLowerCase()}: numbered pages, page 1 active, two chevrons`,
      sh.numbers.length >= 2 && sh.active === '1' && sh.chevrons === 2,
      `pages [${sh.numbers.join(',')}], active "${sh.active}", ${sh.chevrons} chevrons`);
  }

  // ---- 3. page 2 is DIFFERENT data ----------------------------------------------------------
  // Clicked by its NUMBER, the way the house pager is used.
  await pagerOf(page, 'Saved reports').getByRole('link', { name: '2', exact: true }).click();
  await page.waitForLoadState('domcontentloaded');
  await page.waitForTimeout(600);
  await shot(page, '02-saved-page2');

  record('saved reports: clicking 2 moved savedPage to 2', /savedPage=2/.test(page.url()),
    page.url().replace(BASE, ''));
  const saved2 = await rowsOf(page, 'Saved reports');
  const overlap = saved2.filter(r => saved1.includes(r));
  record('saved reports: page 2 holds different rows than page 1', saved2.length > 0 && overlap.length === 0,
    `${saved2.length} rows, ${overlap.length} shared with page 1`);

  // ---- 4. the tables page independently -----------------------------------------------------
  const recentStill1 = await rowsOf(page, 'Recent runs');
  record('recent runs: unmoved while the saved table paged',
    recentStill1.length === recent1.length && recentStill1.every((r, i) => r === recent1[i]),
    `recentPage in url: ${/recentPage=(\d+)/.exec(page.url())?.[1] ?? 'absent'}`);

  await pagerOf(page, 'Recent runs').getByRole('link', { name: '2', exact: true }).click();
  await page.waitForLoadState('domcontentloaded');
  await page.waitForTimeout(600);
  await shot(page, '03-both-page2');

  const bothUrl = page.url();
  record('recent runs: moved recentPage to 2 and KEPT savedPage=2',
    /recentPage=2/.test(bothUrl) && /savedPage=2/.test(bothUrl), bothUrl.replace(BASE, ''));
  const recent2 = await rowsOf(page, 'Recent runs');
  const rOverlap = recent2.filter(r => recent1.includes(r));
  record('recent runs: page 2 holds different rows than page 1', recent2.length > 0 && rOverlap.length === 0,
    `${recent2.length} rows, ${rOverlap.length} shared with page 1`);
  const savedStill2 = await rowsOf(page, 'Saved reports');
  record('saved reports: still on its own page 2',
    savedStill2.length === saved2.length && savedStill2.every((r, i) => r === saved2[i]));

  // ---- 5. a page link carries the active filters --------------------------------------------
  await page.goto(BASE + '/Reports?search=stock', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(600);
  const savedFiltered = await rowsOf(page, 'Saved reports');
  const nextHref = await pagerOf(page, 'Saved reports').getByRole('link', { name: '2', exact: true })
    .getAttribute('href').catch(() => null);
  if (nextHref) {
    record('a saved-table page link carries the search', /search=stock/.test(nextHref), nextHref);
  } else {
    // A filter narrow enough to fit on one page has no pager at all. Report that rather than
    // claiming a pass that was not measured.
    note('search=stock fits on one page', `${savedFiltered.length} rows, no page-2 link to inspect`);
  }

  // ---- 6. past the end clamps onto the last real page ---------------------------------------
  await page.goto(BASE + '/Reports?savedPage=999', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(600);
  await shot(page, '04-saved-past-end');
  const clamped = await rowsOf(page, 'Saved reports');
  const clampedActive = (await pagerOf(page, 'Saved reports').locator('li.active').innerText()
    .catch(() => '')).trim();
  const clampedLast = (await pagerOf(page, 'Saved reports').locator('li:not(.disabled) a')
    .filter({ hasText: /^\d+$/ }).last().innerText().catch(() => '')).trim();
  record('saved reports: savedPage=999 lands on the last real page with rows on it',
    clamped.length > 0 && clampedActive !== '' && clampedActive === clampedLast,
    `${clamped.length} rows, active page "${clampedActive}", highest page "${clampedLast}"`);

  // ---- the pager is new markup inside a card, so measure the page for overflow ---------------
  const overflow = await page.evaluate(() =>
    Math.max(0, document.documentElement.scrollWidth - document.documentElement.clientWidth));
  record('no horizontal page overflow', overflow === 0, `${overflow}px`);

  record('no javascript errors while paging', errors.length === 0, errors.slice(0, 2).join(' || '));

  await browser.close();
  finish();
})().catch(e => {
  record('probe crashed', false, String(e.message || e).replace(/\s+/g, ' ').slice(0, 300));
  finish();
});
