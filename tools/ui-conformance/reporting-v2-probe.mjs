// =================================================================================================
// REPORT STUDIO V2 — AUTHENTICATED RUNTIME PROBE.
//
// It drives the REAL designer in a REAL browser against a REAL database, as a REAL signed-in user,
// and it builds a report the way a person would: pick a data set, drop elements on the page, move
// one, resize one, undo, redo, save, reopen, preview, print, PDF.
//
// WHY A PROBE AND NOT A UNIT TEST. The 35 tests in ReportStudioVisualTests prove the SERVER's rules
// and deliberately never touch the browser, because a rule that only holds when the designer behaves
// is not a rule. This asks the opposite question — does the product actually work when a person uses
// it — and that one cannot be answered without the DOM, the drag events and the round trip.
//
// IT CHANGES NO APPLICATION FILE. It signs in, drives the UI, captures screenshots and reports.
//
//   node tools/ui-conformance/reporting-v2-probe.mjs --base http://localhost:5188 \
//        --user dev.superadmin --password <supplied at call time> --out <dir>
//
// The password is a REQUIRED argument and is never written to disk by this script.
// =================================================================================================
import { chromium } from 'playwright';
import { mkdirSync, writeFileSync } from 'fs';
import { join } from 'path';

const arg = (name, fallback) => {
  const i = process.argv.indexOf('--' + name);
  return i >= 0 && process.argv[i + 1] ? process.argv[i + 1] : fallback;
};

const BASE = arg('base', 'http://localhost:5188');
const USER = arg('user', 'dev.superadmin');
const PASS = arg('password', '');
const OUT = arg('out', 'artifacts/v2');

if (!PASS) {
  console.error('FAIL: --password is required; it is never stored in this repository.');
  process.exit(2);
}

mkdirSync(OUT, { recursive: true });

const steps = [];
const record = (name, ok, detail) => {
  steps.push({ name, ok, detail: detail ?? '' });
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? '  — ' + detail : ''}`);
};

const shot = async (page, name) => {
  const file = join(OUT, name + '.png');
  await page.screenshot({ path: file, fullPage: false });
  return file;
};

const run = async () => {
  const browser = await chromium.launch();

  for (const lang of ['en', 'ar']) {
    const context = await browser.newContext({
      viewport: { width: 1600, height: 1000 },
      locale: lang === 'ar' ? 'ar-EG' : 'en-US',
    });
    const page = await context.newPage();

    const errors = [];
    // Tagged with the step that was running, so a shell error can be attributed to an ACTION rather
    // than merely to the page — the same attribution discipline the earlier UI findings pass used.
    page.on('pageerror', e => errors.push('[after: ' + (steps.length ? steps[steps.length - 1].name : 'load') + '] ' + String(e).slice(0, 90)));
    page.on('console', m => { if (m.type() === 'error') errors.push(m.text()); });

    // ---- 1. sign in, through the ordinary login form -----------------------------------------
    await page.goto(BASE + '/Account/Login', { waitUntil: 'domcontentloaded' });
    await page.fill('input[name="UserName"], input[name="Username"], input[name="Email"]', USER);
    await page.fill('input[name="Password"]', PASS);
    await Promise.all([
      page.waitForLoadState('networkidle').catch(() => {}),
      page.click('button[type="submit"], input[type="submit"]'),
    ]);
    const signedIn = !page.url().includes('/Account/Login');
    record(`[${lang}] sign in as ${USER}`, signedIn, page.url());
    if (!signedIn) { await shot(page, `${lang}-login-failed`); continue; }

    // The culture cookie is how this product switches language; setting it is what a user does by
    // clicking the language menu.
    if (lang === 'ar') {
      await context.addCookies([{
        name: '.AspNetCore.Culture',
        value: 'c%3Dar%7Cuic%3Dar',
        url: BASE,
      }]);
    }

    // ---- 2. the designer loads ----------------------------------------------------------------
    //
    // ERRORS ARE RESET HERE, and that is attribution rather than convenience: /Account/Login raises a
    // TypeError from Metronic's own plugins.bundle.js on every load, signed out, with no Reporting page
    // ever visited — it is an inherited shared-shell fault and not this module's to own or to fix. What
    // follows measures the DESIGNER, which is what this probe is for. The login-page fault is reported
    // separately rather than swept up here or silently absorbed.
    errors.length = 0;

    await page.goto(BASE + '/Reports/Studio', { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(1200);

    const hasCanvas = await page.locator('#cbd-page').count() > 0;
    const noDatasets = await page.locator('.card-body:has-text("No data sets")').count() > 0;
    record(`[${lang}] designer renders`, hasCanvas, hasCanvas ? '' : (noDatasets ? 'no datasets for this persona' : 'canvas absent'));
    if (!hasCanvas) { await shot(page, `${lang}-studio-empty`); continue; }

    await shot(page, `${lang}-01-designer`);

    // ---- 3. seven bands, on the page ----------------------------------------------------------
    const bandCount = await page.locator('.cbd-band').count();
    record(`[${lang}] seven bands drawn`, bandCount === 7, `${bandCount} band(s)`);

    // ---- 4. a toolbox with the placeable things ----------------------------------------------
    const toolCount = await page.locator('#cbd-toolbox [data-tool]').count();
    record(`[${lang}] toolbox offers 20+ elements`, toolCount >= 20, `${toolCount} tool(s)`);

    // ---- 5. pick a data set and get its fields -----------------------------------------------
    const options = await page.locator('#cbd-dataset option').evaluateAll(
      els => els.map(e => e.value).filter(Boolean));
    record(`[${lang}] data sets offered`, options.length > 0, options.join(', '));
    if (!options.length) { await shot(page, `${lang}-no-datasets`); continue; }

    await page.click('a[href="#cbd-tab-data"]');
    await page.selectOption('#cbd-dataset', options[0]);
    await page.waitForTimeout(900);

    const fieldCount = await page.locator('#cbd-fields [data-field]').count();
    record(`[${lang}] fields load for ${options[0]}`, fieldCount > 0, `${fieldCount} field(s)`);

    // §9 — the parameters the dataset declares, which V1 could not surface at all.
    const paramCount = await page.locator('#cbd-params input, #cbd-params select').count();
    record(`[${lang}] dataset parameters surfaced (§9)`, paramCount > 0, `${paramCount} input(s)`);

    // ---- 6. PLACE elements, by clicking the toolbox (the click path adds to the selected band)
    await page.click('a[href="#cbd-tab-toolbox"]');

    // A title in the report header…
    await page.click('.cbd-band[data-band="0"]');
    await page.click('#cbd-toolbox [data-tool="title"]');
    await page.waitForTimeout(200);

    // …a logo, anywhere the user likes — §5's rule that nothing is pinned to a corner.
    await page.click('#cbd-toolbox [data-tool="logo"]');
    await page.waitForTimeout(200);

    // …a bound field in the detail band…
    await page.click('.cbd-band[data-band="3"]');
    await page.click('a[href="#cbd-tab-data"]');
    await page.click('#cbd-fields [data-field]');
    await page.waitForTimeout(200);

    // …a page number in the page footer…
    await page.click('a[href="#cbd-tab-toolbox"]');
    await page.click('.cbd-band[data-band="5"]');
    await page.click('#cbd-toolbox [data-tool="pagexy"]');
    await page.waitForTimeout(200);

    // …and a signature in the report footer.
    await page.click('.cbd-band[data-band="6"]');
    await page.click('#cbd-toolbox [data-tool="sign"]');
    await page.waitForTimeout(300);

    const placed = await page.locator('.cbd-el').count();
    record(`[${lang}] five elements placed across four bands`, placed === 5, `${placed} element(s)`);
    await shot(page, `${lang}-02-elements-placed`);

    // ---- 7. MOVE one, with a real drag --------------------------------------------------------
    const target = page.locator('.cbd-band[data-band="0"] .cbd-el').first();
    const before = await target.evaluate(el => el.style.insetInlineStart);
    const box = await target.boundingBox();
    await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
    await page.mouse.down();
    await page.mouse.move(box.x + box.width / 2 + 90, box.y + box.height / 2 + 26, { steps: 12 });
    await page.mouse.up();
    await page.waitForTimeout(250);
    const after = await page.locator('.cbd-band[data-band="0"] .cbd-el').first()
      .evaluate(el => el.style.insetInlineStart);
    record(`[${lang}] an element can be dragged`, before !== after, `${before} → ${after}`);

    // ---- 8. UNDO puts it back, REDO moves it again --------------------------------------------
    await page.click('#cbd-undo');
    await page.waitForTimeout(200);
    const undone = await page.locator('.cbd-band[data-band="0"] .cbd-el').first()
      .evaluate(el => el.style.insetInlineStart);
    record(`[${lang}] undo restores the position`, undone === before, `${undone}`);

    await page.click('#cbd-redo');
    await page.waitForTimeout(200);
    const redone = await page.locator('.cbd-band[data-band="0"] .cbd-el').first()
      .evaluate(el => el.style.insetInlineStart);
    record(`[${lang}] redo re-applies it`, redone === after, `${redone}`);

    // ---- 9. RESIZE with a handle --------------------------------------------------------------
    await page.locator('.cbd-band[data-band="0"] .cbd-el').first().click();
    await page.waitForTimeout(150);
    const handle = page.locator('.cbd-band[data-band="0"] .cbd-el .cbd-handle.se').first();
    if (await handle.count()) {
      const wBefore = await page.locator('.cbd-band[data-band="0"] .cbd-el').first()
        .evaluate(el => el.style.width);
      const hb = await handle.boundingBox();
      await page.mouse.move(hb.x + 4, hb.y + 4);
      await page.mouse.down();
      await page.mouse.move(hb.x + 60, hb.y + 20, { steps: 10 });
      await page.mouse.up();
      await page.waitForTimeout(250);
      const wAfter = await page.locator('.cbd-band[data-band="0"] .cbd-el').first()
        .evaluate(el => el.style.width);
      record(`[${lang}] an element can be resized`, wBefore !== wAfter, `${wBefore} → ${wAfter}`);
    } else {
      record(`[${lang}] an element can be resized`, false, 'no resize handle rendered');
    }

    // ---- 10. the properties panel edits the selected element ----------------------------------
    const propInputs = await page.locator('#cbd-props input, #cbd-props select').count();
    record(`[${lang}] properties panel binds the selection`, propInputs > 0, `${propInputs} control(s)`);
    await shot(page, `${lang}-03-properties`);

    // ---- 11. paper and orientation change the sheet -------------------------------------------
    const widthA4 = await page.locator('#cbd-page').evaluate(el => el.style.width);
    await page.selectOption('#cbd-paper', '1');           // A5
    await page.waitForTimeout(250);
    const widthA5 = await page.locator('#cbd-page').evaluate(el => el.style.width);
    await page.selectOption('#cbd-orient', '1');          // landscape
    await page.waitForTimeout(250);
    const widthLand = await page.locator('#cbd-page').evaluate(el => el.style.width);
    record(`[${lang}] paper size and orientation resize the page`,
      widthA4 !== widthA5 && widthA5 !== widthLand, `A4 ${widthA4} · A5 ${widthA5} · A5-land ${widthLand}`);

    // Back to A4 portrait for the artefacts.
    await page.selectOption('#cbd-orient', '0');
    await page.selectOption('#cbd-paper', '0');
    await page.waitForTimeout(250);

    // ---- 12. NAME it and SAVE ------------------------------------------------------------------
    const name = `V2 probe ${lang} ${new Date().toISOString().slice(0, 19)}`;
    await page.fill('#cbd-name', name);
    await page.click('#cbd-save');
    // WAIT FOR THE OUTCOME, not for a stopwatch. A fixed sleep made this step fail once in three runs on a
    // cold server — a flaky verification artefact is worse than none, because it teaches you to ignore it.
    await page.waitForFunction(() => {
      const s = document.getElementById('cbd-status');
      const e = document.getElementById('cbd-errors');
      return (s && /Saved|تم الحفظ/.test(s.textContent || '')) || (e && !e.classList.contains('d-none'));
    }, null, { timeout: 30000 }).catch(() => {});

    const status = await page.locator('#cbd-status').textContent();
    const errorBox = await page.locator('#cbd-errors:not(.d-none)').count();
    const savedText = await page.locator('#cbd-errors').textContent().catch(() => '');
    const saved = /Saved|تم الحفظ/.test(status || '') && errorBox === 0;
    record(`[${lang}] the design saves`, saved, saved ? status.trim() : `errors: ${savedText}`);
    await shot(page, `${lang}-04-saved`);

    // ---- 13. PREVIEW renders the designed document, not a column table -------------------------
    await page.click('#cbd-preview');
    await page.waitForTimeout(3500);

    const previewPages = await page.locator('#cbd-preview-body .cbv-page').count();
    const previewTable = await page.locator('#cbd-preview-body .cbrep').count();
    record(`[${lang}] preview renders the DESIGNED document`,
      previewPages > 0 && previewTable === 0,
      `${previewPages} designed page(s), ${previewTable} column-table render(s)`);
    await shot(page, `${lang}-05-preview`);

    const meta = await page.locator('#cbd-preview-meta').textContent().catch(() => '');
    record(`[${lang}] preview reports a real row count`, /\d/.test(meta || ''), (meta || '').trim());

    await page.keyboard.press('Escape');
    await page.waitForTimeout(600);

    // ---- 14. PRINT returns a standalone document ----------------------------------------------
    const [popup] = await Promise.all([
      page.context().waitForEvent('page', { timeout: 15000 }).catch(() => null),
      page.click('#cbd-print'),
    ]);
    if (popup) {
      await popup.waitForTimeout(2500);
      const printPages = await popup.locator('.cbv-page').count();
      record(`[${lang}] print preview opens the same document`, printPages > 0, `${printPages} page(s)`);
      await popup.screenshot({ path: join(OUT, `${lang}-06-print.png`) }).catch(() => {});
      await popup.close();
    } else {
      record(`[${lang}] print preview opens the same document`, false, 'no print window appeared');
    }

    // ---- 15. PDF downloads --------------------------------------------------------------------
    const [download] = await Promise.all([
      page.waitForEvent('download', { timeout: 60000 }).catch(() => null),
      page.click('#cbd-pdf'),
    ]);
    if (download) {
      const path = join(OUT, `${lang}-report.pdf`);
      await download.saveAs(path);
      const { statSync, readFileSync } = await import('fs');
      const size = statSync(path).size;
      const head = readFileSync(path).subarray(0, 5).toString('latin1');
      record(`[${lang}] PDF is produced`, head === '%PDF-' && size > 1000, `${size} bytes, header ${head}`);
    } else {
      const err = await page.locator('#cbd-errors').textContent().catch(() => '');
      record(`[${lang}] PDF is produced`, false, `no download; ${err.trim()}`);
    }

    // ---- 16. REOPEN the saved report ----------------------------------------------------------
    await page.goto(BASE + '/Reports/Studio', { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(1200);
    const savedButton = page.locator(`[data-open]`).first();
    if (await savedButton.count()) {
      await savedButton.click();
      await page.waitForTimeout(2500);
      const reopenedElements = await page.locator('.cbd-el').count();
      record(`[${lang}] a saved design reopens with its elements`, reopenedElements > 0,
        `${reopenedElements} element(s)`);
      await shot(page, `${lang}-07-reopened`);
    } else {
      record(`[${lang}] a saved design reopens with its elements`, false, 'no saved report listed');
    }

    // ---- 17. direction ------------------------------------------------------------------------
    const dir = await page.evaluate(() => document.documentElement.getAttribute('dir') || 'ltr');
    record(`[${lang}] page direction`, lang === 'ar' ? dir === 'rtl' : dir !== 'rtl', dir);

    // ---- 18. tablet: the rails collapse, the canvas survives -----------------------------------
    await page.setViewportSize({ width: 1024, height: 900 });
    await page.waitForTimeout(500);
    const overflow = await page.evaluate(() =>
      document.documentElement.scrollWidth - document.documentElement.clientWidth);
    record(`[${lang}] tablet layout does not scroll the page sideways`, overflow <= 2, `${overflow}px`);
    await shot(page, `${lang}-08-tablet`);

    record(`[${lang}] no uncaught errors in the designer`, errors.length === 0, errors.slice(0, 4).join(' ||| '));

    await context.close();
  }

  await browser.close();

  const failed = steps.filter(s => !s.ok);
  writeFileSync(join(OUT, 'result.json'), JSON.stringify({ steps, failed: failed.length }, null, 2));

  console.log(`\n${steps.length - failed.length}/${steps.length} checks passed`);
  process.exit(failed.length ? 1 : 0);
};

run().catch(e => { console.error('PROBE CRASHED:', e); process.exit(3); });
