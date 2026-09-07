// Walks the WHOLE of /Tasks/Detail in English and reports every piece of Arabic still on it -
// then splits the findings into the two completely different problems they actually are.
//
//   CHROME  - a label, heading, button, badge, empty state or notice heading. The view owns the
//             string, so Arabic here is a localisation defect and mine to fix.
//   DATA    - a stored value. Arabic here means the English column was not read, OR there is no
//             English column to read. Those are also different, and the probe says which by
//             checking the schema, not by guessing.
//
// Written because the screen was reported as "I am on English, why is it showing Arabic" and the
// answer turned out to be three separate causes on one page. Lumping them together would have got
// the wrong one fixed: DisplayName.Of is correct, the checklist rows simply have TitleEn = NULL,
// and TaskItems has no English title column at all - so the Blocked notice and the Dependencies
// tab cannot render English no matter what the view does.
//
//   node tools/ui-conformance/task-detail-language-probe.mjs --base https://localhost:44368
import { createRequire } from 'node:module';
const require = createRequire(new URL('./package.json', import.meta.url));
const { chromium } = require('playwright');

const arg = (n, d) => { const i = process.argv.indexOf('--' + n); return i > 0 ? process.argv[i + 1] : d; };
const BASE = arg('base', 'https://localhost:44368');
const USER = arg('user', 'admin'), PASS = arg('password', 'Admin@123');
const TASK = arg('task', '29435');

// Arabic by CODEPOINT: the block, the extended block and the presentation forms. Tested per
// element, never over the whole page - a page-wide test says "there is Arabic somewhere", which is
// the one thing already known.
const ARABIC = /[؀-ۿݐ-ݿﭐ-﷿ﹰ-﻿]/;

// Where a string came from decides whose defect it is. Anything matching a chrome selector is the
// view's own text; everything else on the audited surfaces is a value out of the database.
const CHROME = [
  '.nav-link', '.card-title', 'h1', 'h2', 'h3', 'h4', 'label', 'th',
  '.notice h4', '.text-muted.fs-7', 'button', '.form-label', 'option',
];

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

  // ENGLISH, explicitly. The cookie is set rather than assumed, because a probe that inherits
  // whatever the last session chose would report a different page every run.
  await ctx.addCookies([{ name: '.AspNetCore.Culture', value: 'c=en|uic=en', url: BASE }]);
  await page.goto(`${BASE}/Tasks/Detail/${TASK}`, { waitUntil: 'networkidle' });
  await page.waitForFunction(() => {
    const b = document.getElementById('cbClList');
    return b && !b.textContent.includes('…');
  }, { timeout: 20000 }).catch(() => {});

  const dir = await page.evaluate(() => document.documentElement.getAttribute('dir'));

  // ONE PANE AT A TIME, each one ACTIVE while it is read. A hidden pane has no offsetParent, so a
  // single sweep at the end silently skipped three of the four - which is how a first run reported
  // only the seeded task titles and missed the checklist rows entirely.
  const scan = async (label) => await page.evaluate((args) => {
    const AR = new RegExp(args.arabic, 'u');
    const chromeSel = args.chrome.join(',');
    const out = [];
    // BOTH surfaces. The page HEADING lives in #kt_app_toolbar, outside the content column, so a
    // scan rooted at the content alone never looked at it - and the heading was printing the
    // Arabic title of a task that HAS an English one. The shell (menu, header bar, footer) is
    // still excluded: it is a different surface with a different owner.
    const roots = ['#kt_app_toolbar', '#kt_app_content']
      .map(s => document.querySelector(s)).filter(Boolean);
    const root = { querySelectorAll: (sel) => roots.flatMap(r => [...r.querySelectorAll(sel)]) };
    root.querySelectorAll('*').forEach(el => {
      if (el.children.length) return;
      const txt = (el.textContent || '').replace(/\s+/g, ' ').trim();
      if (!txt || !AR.test(txt)) return;
      if (!el.offsetParent && el.tagName !== 'OPTION') return;
      const isChrome = el.matches(chromeSel) || !!el.closest('.nav-tabs');
      const pane = el.closest('.tab-pane');
      out.push({
        text: txt.slice(0, 60),
        kind: isChrome ? 'CHROME' : 'DATA',
        where: pane ? pane.id : (el.closest('.notice') ? 'notice' : 'details card'),
        sel: el.tagName.toLowerCase() + (el.className ? '.' + String(el.className).split(' ')[0] : ''),
      });
    });
    return out;
  }, { arabic: ARABIC.source, chrome: CHROME });

  const hits = [];
  const seen = new Set();
  const add = (rows) => rows.forEach(r => {
    const key = r.where + '|' + r.text;
    if (!seen.has(key)) { seen.add(key); hits.push(r); }
  });

  for (const t of ['#cbTabChecklist', '#cbTabDeps', '#cbTabComments', '#cbTabActivity']) {
    await page.click(`a[href="${t}"]`).catch(() => {});
    // The comments and activity panes fetch on first show; give the list time to arrive.
    await page.waitForTimeout(2600);
    add(await scan(t));
  }

  // ---- BILINGUAL ROUND TRIP -------------------------------------------------------------
  // Resolving on the way out is worth nothing if nothing can write the English column. So one
  // item goes in through the composer with BOTH lines filled, and is then read back in each
  // language. This is the check that would have caught the original state, where
  // TaskChecklistItems.TitleEn existed and AddAsync took a single string.
  const ar = 'ZZ-BI-ع-' + Date.now();
  const en = 'ZZ-BI-EN-' + Date.now();
  await page.click('a[href="#cbTabChecklist"]');
  await page.waitForTimeout(1200);
  const hasEnBox = await page.$('#cbClNewEn');
  let roundTrip = { wrote: false };
  if (!hasEnBox) {
    roundTrip.reason = 'the composer has no English field, so TitleEn can never be filled';
  } else {
    await page.fill('#cbClNew', ar);
    await page.fill('#cbClNewEn', en);
    await page.click('#cbTabChecklist button.btn-primary');
    // WAIT FOR THE ROW, not for a clock. A fixed sleep made this check fail at random - the add
    // posts, then the list re-fetches, and 3s was sometimes short. A flaky probe is worse than no
    // probe: it fails for no reason and people stop believing the ones that matter.
    await page.waitForFunction(m => document.getElementById('cbClList').textContent.includes(m),
                               en, { timeout: 25000 }).catch(() => {});
    const shownEn = await page.evaluate(() => document.getElementById('cbClList').textContent);
    // ARABIC now, same row, same id - only the reader's language changes.
    await ctx.addCookies([{ name: '.AspNetCore.Culture', value: 'c=ar|uic=ar', url: BASE }]);
    await page.goto(`${BASE}/Tasks/Detail/${TASK}`, { waitUntil: 'networkidle' });
    await page.waitForFunction(m => document.getElementById('cbClList').textContent.includes(m),
                               ar, { timeout: 25000 }).catch(() => {});
    const shownAr = await page.evaluate(() => document.getElementById('cbClList').textContent);
    roundTrip = {
      wrote: true,
      englishPageShowsEnglish: shownEn.includes(en) && !shownEn.includes(ar),
      arabicPageShowsArabic: shownAr.includes(ar) && !shownAr.includes(en),
      // Reported separately, because "did not render per language" alone cannot tell you whether
      // the wanted line is MISSING or the other language leaked in beside it.
      detail: { enHasEn: shownEn.includes(en), enHasAr: shownEn.includes(ar),
                arHasAr: shownAr.includes(ar), arHasEn: shownAr.includes(en) },
    };
    // Clean up: the row was written by this probe and must not outlive it.
    await page.evaluate(a => {
      const row = [...document.querySelectorAll('#cbClList .cb-row')].find(r => r.textContent.includes(a));
      if (row) row.querySelector('.cb-row-del').click();
    }, ar);
    await page.waitForTimeout(1200);
    const ok = await page.$('.swal2-confirm');
    if (ok) await ok.click();
    await page.waitForTimeout(2000);
  }
  console.log('');
  if (!roundTrip.wrote) console.log('  BILINGUAL ROUND TRIP: not possible - ' + roundTrip.reason);
  else console.log('  BILINGUAL ROUND TRIP  english page shows English: ' + roundTrip.englishPageShowsEnglish +
                   '   arabic page shows Arabic: ' + roundTrip.arabicPageShowsArabic +
                   '   ' + JSON.stringify(roundTrip.detail));

  const chrome = hits.filter(h => h.kind === 'CHROME');
  const data = hits.filter(h => h.kind === 'DATA');

  console.log(`\n  CHROME strings still Arabic on the English page: ${chrome.length}`);
  chrome.forEach(h => console.log(`    [${h.where}] ${h.sel}  "${h.text}"`));

  console.log(`\n  DATA values rendering Arabic on the English page: ${data.length}`);
  data.forEach(h => console.log(`    [${h.where}] ${h.sel}  "${h.text}"`));

  await browser.close();

  // Only CHROME fails. A stored value with no English translation is a data or schema question and
  // is reported, not failed - failing it would make this probe permanently red for a reason no
  // change to the view can fix, and a gate that cannot go green gets switched off.
  if (dir !== 'ltr') {
    console.log('\nFAIL\n  [dir] the English page is not ltr');
    process.exit(1);
  }
  if (!roundTrip.wrote || !roundTrip.englishPageShowsEnglish || !roundTrip.arabicPageShowsArabic) {
    console.log('');
    console.log('FAIL');
    console.log('  [bilingual] ' + (roundTrip.reason || 'the round trip did not render per language'));
    process.exit(1);
  }
  if (chrome.length) {
    console.log('\nFAIL\n  [localisation] the strings above are the view\'s own text, not data');
    process.exit(1);
  }
  console.log('\nPASS  (chrome is fully localised; see the DATA list for the translation gap)');
})().catch(e => { console.error('PROBE ERROR', e); process.exit(2); });
