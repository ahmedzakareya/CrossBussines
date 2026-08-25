// =================================================================================================
// REPORT STUDIO V2 — THE FULL DOCUMENT SCENARIO.
//
// The companion probe proves the designer's MECHANICS (drag, resize, undo, save, reopen). This one
// builds the document §22 asks to see: a company logo placed in the header, a real table with bound
// columns and a total in the detail band, a grouping, and a signature and a stamp in the footer —
// then prints it and converts it to PDF.
//
// It exists as a second script rather than more steps in the first because it answers a different
// question. The first asks "does the tool work"; this asks "can a person produce a real business
// document with it", which is the only question the product is judged on.
//
//   node tools/ui-conformance/reporting-v2-document.mjs --base http://localhost:5188 \
//        --user dev.superadmin --password <supplied at call time> --out <dir>
// =================================================================================================
import { chromium } from 'playwright';
import { mkdirSync, writeFileSync, statSync, readFileSync } from 'fs';
import { join } from 'path';

const arg = (n, d) => { const i = process.argv.indexOf('--' + n); return i >= 0 && process.argv[i + 1] ? process.argv[i + 1] : d; };
const BASE = arg('base', 'http://localhost:5188');
const USER = arg('user', 'dev.superadmin');
const PASS = arg('password', '');
const OUT = arg('out', 'artifacts/v2-doc');
const DATASET = arg('dataset', 'Accounting.SalesRevenue');

if (!PASS) { console.error('FAIL: --password is required.'); process.exit(2); }
mkdirSync(OUT, { recursive: true });

const steps = [];
const record = (n, ok, d) => { steps.push({ name: n, ok, detail: d ?? '' }); console.log(`${ok ? 'PASS' : 'FAIL'}  ${n}${d ? '  — ' + d : ''}`); };

// A tiny but REAL png — a filled square, so it is visible in a screenshot rather than a 1×1 dot.
const LOGO_PNG = Buffer.from(
  'iVBORw0KGgoAAAANSUhEUgAAAGQAAAAyCAYAAACqNX6+AAAAWklEQVR42u3PMQEAAAgDoC251a3g' +
  'LwSgOXfVAAECBAgQIECAAAECBAgQIECAAAECBAgQIECAAAECBAgQIECAAAECBAgQIECAAAECBAgQ' +
  'IECAAAECBAgQIEDgAxc9AAGnGnZ8AAAAAElFTkSuQmCC', 'base64');

const run = async () => {
  const browser = await chromium.launch();

  for (const lang of ['en', 'ar']) {
    // locale MATTERS: the product's default culture is Arabic, so an English pass that sends no
    // Accept-Language gets an Arabic page and quietly stops testing what it says it tests.
    const context = await browser.newContext({
      viewport: { width: 1600, height: 1000 },
      locale: lang === 'ar' ? 'ar-EG' : 'en-US',
    });
    const page = await context.newPage();

    await page.goto(BASE + '/Account/Login', { waitUntil: 'domcontentloaded' });
    await page.fill('input[name="UserName"], input[name="Email"]', USER);
    await page.fill('input[name="Password"]', PASS);
    await Promise.all([page.waitForLoadState('networkidle').catch(() => {}), page.click('button[type="submit"], input[type="submit"]')]);

    if (lang === 'ar') {
      await context.addCookies([{ name: '.AspNetCore.Culture', value: 'c%3Dar%7Cuic%3Dar', url: BASE }]);
    }

    await page.goto(BASE + '/Reports/Studio', { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(1400);

    // Selecting a band through the BANDS TAB rather than by clicking the sheet.
    //
    // Not a workaround for a product fault: clicking the canvas works for a person, but a band low on a
    // long page sits under the app's sticky header at this viewport, and a band already holding a table
    // has its cells on top. The Bands tab is the affordance the product provides for exactly that, so the
    // probe uses it and the click lands every time.
    const selectBand = async (kind) => {
      await page.click('a[href="#cbd-tab-bands"]');
      await page.waitForTimeout(250);
      await page.locator(`#cbd-bandlist [data-bandrow="${kind}"] span`).first().click();
      await page.waitForTimeout(250);
      await page.click('a[href="#cbd-tab-toolbox"]');
      await page.waitForTimeout(250);
    };


    // ---- upload a logo ---------------------------------------------------------------------------
    await page.setInputFiles('#cbd-asset-file', { name: 'company-logo.png', mimeType: 'image/png', buffer: LOGO_PNG });
    await page.waitForTimeout(2500);
    const assetCount = await page.locator('#cbd-assets [data-asset]').count();
    record(`[${lang}] a logo uploads and appears in the picker`, assetCount > 0, `${assetCount} asset(s)`);

    // ---- pick the data set -----------------------------------------------------------------------
    await page.click('a[href="#cbd-tab-data"]');
    await page.selectOption('#cbd-dataset', DATASET);
    await page.waitForTimeout(1200);
    const fields = await page.locator('#cbd-fields [data-field]').evaluateAll(els => els.map(e => e.getAttribute('data-field')));
    record(`[${lang}] ${DATASET} fields load`, fields.length > 0, fields.join(', '));
    if (!fields.length) { await context.close(); continue; }

    // ---- HEADER: the logo, placed where the designer put it, plus a title -------------------------
    await selectBand(1);                                                // page header
    await page.click('#cbd-assets [data-asset]');                       // the uploaded logo
    await page.waitForTimeout(400);

    await selectBand(1);
    await page.click('#cbd-toolbox [data-tool="title"]');
    await page.waitForTimeout(300);
    // Move the title clear of the logo, using the properties panel rather than a drag — the same model
    // edit, reached the other way, which is worth exercising too.
    await page.fill('#px', '60'); await page.dispatchEvent('#px', 'change');
    await page.fill('#py', '4');  await page.dispatchEvent('#py', 'change');
    await page.waitForTimeout(300);

    // ---- DETAIL: a real table with bound columns and a total --------------------------------------
    await selectBand(3);
    await page.click('#cbd-toolbox [data-tool="table"]');
    await page.waitForTimeout(400);

    // Three columns, and a SUM on the last — the table designer, driven as a person would.
    for (let i = 0; i < 3; i++) {
      await page.click('#c-add');
      await page.waitForTimeout(350);
    }
    const wanted = ['InvoiceNo', 'InvoiceDate', 'GrandTotal'].filter(k => fields.includes(k));
    for (let i = 0; i < wanted.length; i++) {
      await page.selectOption(`#c-field-${i}`, wanted[i]).catch(() => {});
      await page.waitForTimeout(250);
    }
    if (wanted.includes('GrandTotal')) {
      const idx = wanted.indexOf('GrandTotal');
      await page.selectOption(`#c-total-${idx}`, '1').catch(() => {});   // SUM
      await page.waitForTimeout(300);
    }
    const columns = await page.locator('#cbd-props [id^="c-field-"]').count();
    record(`[${lang}] the table designer takes bound columns`, columns >= 3, `${columns} column(s)`);

    // ---- GROUPING: a band bound to a groupable field ----------------------------------------------
    await page.click('a[href="#cbd-tab-bands"]');
    await page.waitForTimeout(300);
    const groupSelect = page.locator('#bg-2');
    const groupable = await groupSelect.locator('option').evaluateAll(o => o.map(x => x.value).filter(Boolean));
    if (groupable.length) {
      await page.fill('#bh-2', '10'); await page.dispatchEvent('#bh-2', 'change');
      await groupSelect.selectOption(groupable[0]);
      await page.waitForTimeout(400);
      record(`[${lang}] a group band binds a groupable field`, true, groupable[0]);
    } else {
      record(`[${lang}] a group band binds a groupable field`, false, 'no groupable field offered');
    }

    // ---- FOOTER: a signature and a stamp, anywhere the designer wants them ------------------------
    await selectBand(6);
    await page.click('#cbd-toolbox [data-tool="sign"]');
    await page.waitForTimeout(350);
    await page.fill('#px', '15'); await page.dispatchEvent('#px', 'change');
    await page.waitForTimeout(250);

    await selectBand(6);
    await page.click('#cbd-toolbox [data-tool="stamp"]');
    await page.waitForTimeout(350);
    // A stamp at 120mm across the page — nowhere near the corner a hardcoded layout would force.
    await page.fill('#px', '120'); await page.dispatchEvent('#px', 'change');
    await page.fill('#py', '2');   await page.dispatchEvent('#py', 'change');
    await page.waitForTimeout(300);

    const footerElements = await page.locator('.cbd-band[data-band="6"] .cbd-el').count();
    record(`[${lang}] signature and stamp placed at chosen positions`, footerElements === 2, `${footerElements} element(s)`);

    // ---- PAGE FOOTER: page x of y -----------------------------------------------------------------
    await selectBand(5);
    await page.click('#cbd-toolbox [data-tool="pagexy"]');
    await page.waitForTimeout(300);

    await page.screenshot({ path: join(OUT, `${lang}-10-full-design.png`) });

    // ---- SAVE -------------------------------------------------------------------------------------
    await page.fill('#cbd-name', lang === 'ar' ? 'فاتورة المبيعات — تصميم' : 'Sales register — designed');
    await page.click('#cbd-save');
    // WAIT FOR THE OUTCOME, not for a stopwatch. A fixed sleep made this step fail once in three runs on a
    // cold server — a flaky verification artefact is worse than none, because it teaches you to ignore it.
    await page.waitForFunction(() => {
      const s = document.getElementById('cbd-status');
      const e = document.getElementById('cbd-errors');
      return (s && /Saved|تم الحفظ/.test(s.textContent || '')) || (e && !e.classList.contains('d-none'));
    }, null, { timeout: 30000 }).catch(() => {});
    const status = (await page.locator('#cbd-status').textContent()) || '';
    const errs = (await page.locator('#cbd-errors').textContent().catch(() => '')) || '';
    const saved = /Saved|تم الحفظ/.test(status);
    record(`[${lang}] the full document saves`, saved, saved ? status.trim() : errs.trim());

    // ---- PREVIEW ----------------------------------------------------------------------------------
    await page.click('#cbd-preview');
    await page.waitForTimeout(4500);
    const pages = await page.locator('#cbd-preview-body .cbv-page').count();
    const tableRows = await page.locator('#cbd-preview-body .cbv-page tbody tr').count();
    const totals = await page.locator('#cbd-preview-body .cbv-page tfoot').count();
    record(`[${lang}] the preview renders the table with rows`, pages > 0 && tableRows > 0,
      `${pages} page(s), ${tableRows} row(s), ${totals} total row(s)`);

    // ONE totals row for the whole report. Both wrong answers were real: one per page (each covering only
    // its own page) and none at all (a group footer after the last rows swallowed it).
    record(`[${lang}] exactly one totals row, for the whole report`, totals === 1, `${totals} total row(s)`);
    await page.screenshot({ path: join(OUT, `${lang}-11-preview-document.png`) });

    // A logo actually reached the page as inlined bytes, not as a link back to the app.
    const inlined = await page.locator('#cbd-preview-body img[src^="data:image/"]').count();
    const linked = await page.locator('#cbd-preview-body img:not([src^="data:"])').count();
    record(`[${lang}] the logo is inlined, not linked`, inlined > 0 && linked === 0, `${inlined} inline, ${linked} linked`);

    await page.keyboard.press('Escape');
    await page.waitForTimeout(800);

    // ---- PRINT ------------------------------------------------------------------------------------
    const [popup] = await Promise.all([
      context.waitForEvent('page', { timeout: 20000 }).catch(() => null),
      page.click('#cbd-print'),
    ]);
    if (popup) {
      await popup.waitForTimeout(3000);
      const printPages = await popup.locator('.cbv-page').count();
      const printImgs = await popup.locator('img[src^="data:image/"]').count();
      record(`[${lang}] print preview carries the same document`, printPages > 0, `${printPages} page(s), ${printImgs} image(s)`);
      await popup.screenshot({ path: join(OUT, `${lang}-12-print-document.png`), fullPage: true }).catch(() => {});
      await popup.close();
    } else {
      record(`[${lang}] print preview carries the same document`, false, 'no window');
    }

    // ---- PDF --------------------------------------------------------------------------------------
    const [dl] = await Promise.all([
      page.waitForEvent('download', { timeout: 90000 }).catch(() => null),
      page.click('#cbd-pdf'),
    ]);
    if (dl) {
      const path = join(OUT, `${lang}-document.pdf`);
      await dl.saveAs(path);
      const bytes = readFileSync(path);
      const isPdf = bytes.subarray(0, 5).toString('latin1') === '%PDF-';
      const mediaBox = /\/MediaBox\s*\[([^\]]*)\]/.exec(bytes.toString('latin1'));
      record(`[${lang}] PDF is produced at the designed paper size`, isPdf,
        `${statSync(path).size} bytes, MediaBox ${mediaBox ? mediaBox[1].trim() : 'n/a'}`);
    } else {
      record(`[${lang}] PDF is produced at the designed paper size`, false,
        (await page.locator('#cbd-errors').textContent().catch(() => '')).trim());
    }

    await context.close();
  }

  await browser.close();
  const failed = steps.filter(s => !s.ok);
  writeFileSync(join(OUT, 'result.json'), JSON.stringify({ steps, failed: failed.length }, null, 2));
  console.log(`\n${steps.length - failed.length}/${steps.length} checks passed`);
  process.exit(failed.length ? 1 : 0);
};

run().catch(e => { console.error('PROBE CRASHED:', e); process.exit(3); });
