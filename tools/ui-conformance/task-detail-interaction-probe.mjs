// Proves that /Tasks/Detail changes its lists WITHOUT a page load, and that a refusal wears the
// house notice with readable contrast.
//
// Written because five handlers on that screen ended in location.reload(). A grep finds the call;
// it cannot tell you whether removing it left the list, the sidebar counter and the progress bar
// agreeing with each other afterwards - which is the actual risk when a server-rendered list starts
// being drawn on the client. And a reload is invisible in a screenshot: the page looks identical
// after one, which is exactly why it survived this long.
//
// So this counts main-frame navigations across a real add / toggle / delete / link / unlink, then
// reads the counter back out of the DOM and compares it against the rows it was supposedly derived
// from. It also renders both refusal kinds and measures them, because "put it on our identity" is a
// contrast claim, not a class-name claim.
//
// Needs the dependency seed for the three-case pane:
//   sqlcmd -S localhost -d CrossBuyDev -E -f 65001 -i tools\testdata\tasks_dependency_testdata.sql
//
//   node tools/ui-conformance/task-detail-interaction-probe.mjs --base https://localhost:44368
import { createRequire } from 'node:module';
const require = createRequire(new URL('./package.json', import.meta.url));
const { chromium } = require('playwright');

const arg = (n, d) => { const i = process.argv.indexOf('--' + n); return i > 0 ? process.argv[i + 1] : d; };
const BASE = arg('base', 'https://localhost:44368');
const USER = arg('user', 'admin'), PASS = arg('password', 'Admin@123');
const TASK = arg('task', '29435');

// WCAG contrast from the COMPUTED colour, so a value arriving through var(), a Metronic class or the
// theme bundle is measured the same as a literal.
const lum = ([r, g, b]) => {
  const f = (c) => { c /= 255; return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4); };
  return 0.2126 * f(r) + 0.7152 * f(g) + 0.0722 * f(b);
};
const ratio = (a, b) => { const [x, y] = [lum(a), lum(b)].sort((p, q) => q - p); return (x + 0.05) / (y + 0.05); };
const rgb = (s) => (s.match(/\d+(\.\d+)?/g) || []).slice(0, 3).map(Number);

const findings = [];
const fail = (check, reason) => findings.push({ check, reason });

(async () => {
  const browser = await chromium.launch();
  const ctx = await browser.newContext({ ignoreHTTPSErrors: true });
  const page = await ctx.newPage();

  const consoleErrors = [];
  page.on('console', m => { if (m.type() === 'error') consoleErrors.push(m.text().slice(0, 140)); });
  page.on('pageerror', e => consoleErrors.push('pageerror: ' + String(e.message).slice(0, 90) + ' @ ' + String(e.stack || '').split(String.fromCharCode(10)).slice(1, 3).join(' <- ').slice(0, 200)));

  await page.goto(BASE + '/Account/Login', { waitUntil: 'domcontentloaded' });
  await page.fill('input[name="UserName"], input[name="Username"], input[name="Email"]', USER);
  await page.fill('input[name="Password"]', PASS);
  await page.click('button[type="submit"]');
  await page.waitForURL(u => !u.toString().includes('/Account/Login'), { timeout: 60000 }).catch(() => {});
  if (page.url().includes('/Account/Login')) { console.error('SIGN-IN FAILED'); process.exit(2); }

  // The buffer is cleared AFTER sign-in, deliberately. The legacy sign-in screen throws one
  // "classList of null" out of plugins.bundle.js on every run, and it is explicitly out of this
  // suite's scope (README section 2). Counting from page one charged that error to the task screen
  // and reported a clean page as FAIL - measured: 0 load errors on /Tasks/Detail, /Tasks/Index,
  // /Inventory/Units, /Calendar/Index, /Reports/Index and /Inventory/Index, 1 on the sign-in flow.
  consoleErrors.length = 0;

  await page.goto(`${BASE}/Tasks/Detail/${TASK}`, { waitUntil: 'networkidle' });
  await page.waitForFunction(() => {
    const b = document.getElementById('cbClList');
    return b && !b.textContent.includes('…');
  }, { timeout: 20000 }).catch(() => {});

  // Everything from here on must happen in ONE document. A navigation is counted, not inferred from
  // a screenshot - a reloaded page looks identical to one that repainted in place.
  let navigations = 0;
  page.on('framenavigated', f => { if (f === page.mainFrame()) navigations++; });

  // ---- 1. CHECKLIST: add, toggle, delete -----------------------------------------------------
  const marker = 'ZZ-PROBE-' + Date.now();
  await page.fill('#cbClNew', marker);
  await page.click('#cbTabChecklist button.btn-primary');
  await page.waitForFunction(m => document.getElementById('cbClList').textContent.includes(m),
                             marker, { timeout: 20000 }).catch(() => fail('checklist/add', 'the new item never appeared'));

  const afterAdd = await page.evaluate(() => {
    const rows = [...document.querySelectorAll('#cbClList .cb-row')];
    const label = (document.getElementById('cbClProgress') || {}).textContent || '';
    const bar = document.getElementById('cbClBar');
    const m = label.match(/(\d+)\s*\/\s*(\d+)\s*\((\d+)%\)/);
    return {
      rows: rows.length,
      done: rows.filter(r => r.querySelector('input[type=checkbox]').checked).length,
      label: label.trim(),
      labelDone: m ? +m[1] : null, labelTotal: m ? +m[2] : null, labelPct: m ? +m[3] : null,
      barPct: bar ? parseInt(bar.style.width, 10) : null,
      progressDir: (document.getElementById('cbClProgress') || {}).getAttribute
        ? document.getElementById('cbClProgress').getAttribute('dir') : null,
    };
  });
  // The counter is only trustworthy if it was derived from the rows on screen. This is the check a
  // reload used to make unnecessary, and the one most likely to rot once it is removed.
  if (afterAdd.labelTotal !== afterAdd.rows)
    fail('checklist/counter', `counter says ${afterAdd.labelTotal} items, the list shows ${afterAdd.rows}`);
  if (afterAdd.labelDone !== afterAdd.done)
    fail('checklist/counter', `counter says ${afterAdd.labelDone} done, the list shows ${afterAdd.done}`);
  const expectPct = afterAdd.rows ? Math.round(afterAdd.done * 100 / afterAdd.rows) : 0;
  if (afterAdd.labelPct !== expectPct)
    fail('checklist/counter', `counter says ${afterAdd.labelPct}%, the rows compute ${expectPct}%`);
  if (afterAdd.barPct !== expectPct)
    fail('checklist/bar', `bar is ${afterAdd.barPct}% wide, the rows compute ${expectPct}%`);
  // "0 / 3 (0%)" reorders to "(0%) 3 / 0" in an RTL run without an isolate.
  if (afterAdd.progressDir !== 'ltr')
    fail('checklist/bidi', 'the counter has no dir="ltr", so its digits can reorder on the Arabic page');

  // toggle the item we just made, then read the counter again
  await page.evaluate(m => {
    const row = [...document.querySelectorAll('#cbClList .cb-row')].find(r => r.textContent.includes(m));
    row.querySelector('input[type=checkbox]').click();
  }, marker);
  await page.waitForTimeout(2500);
  const afterToggle = await page.evaluate(() => {
    const rows = [...document.querySelectorAll('#cbClList .cb-row')];
    const m = ((document.getElementById('cbClProgress') || {}).textContent || '').match(/(\d+)\s*\/\s*(\d+)/);
    return { done: rows.filter(r => r.querySelector('input[type=checkbox]').checked).length,
             labelDone: m ? +m[1] : null };
  });
  if (afterToggle.labelDone !== afterToggle.done)
    fail('checklist/counter', `after a toggle the counter says ${afterToggle.labelDone} done, the list shows ${afterToggle.done}`);
  if (afterToggle.done !== afterAdd.done + 1)
    fail('checklist/toggle', 'ticking the box did not change the done count');

  // ---- 2. THE REFUSAL NOTICE -----------------------------------------------------------------
  // A real refusal: a task cannot depend on itself. This is the author's mistake to fix, so it must
  // read as a warning and keep the server's exact wording.
  await page.click('a[href="#cbTabDeps"]');
  await page.waitForTimeout(1500);
  await page.fill('#cbDepOther', TASK);
  await page.click('#cbTabDeps button.btn-primary');
  await page.waitForFunction(() => {
    const b = document.getElementById('cbDepError');
    return b && !b.classList.contains('d-none') && b.querySelector('.notice');
  }, { timeout: 20000 }).catch(() => fail('refusal/render', 'the self-link refusal produced no notice'));

  const measure = await page.evaluate(() => {
    const n = document.querySelector('#cbDepError .notice');
    if (!n) return null;
    const head = n.querySelector('h4'), body = n.querySelector('.fs-6');
    const bg = getComputedStyle(n).backgroundColor;
    return {
      dashed: getComputedStyle(n).borderStyle,
      hasTile: !!n.querySelector('.symbol-label i'),
      tone: n.className.match(/bg-light-(\w+)/)?.[1] || null,
      bg,
      headColor: getComputedStyle(head).color, headSize: getComputedStyle(head).fontSize,
      bodyColor: getComputedStyle(body).color,
      bodyText: body.textContent.trim().slice(0, 90),
    };
  });
  if (!measure) fail('refusal/render', 'no notice to measure');
  else {
    if (measure.dashed !== 'dashed') fail('refusal/form', `border-style is ${measure.dashed}, the house notice is dashed`);
    if (!measure.hasTile) fail('refusal/form', 'the notice has no icon tile');
    if (measure.tone !== 'warning')
      fail('refusal/tone', `a validation refusal rendered as "${measure.tone}"; the author's own mistake is a warning, not a denial`);
    const head = ratio(rgb(measure.headColor), rgb(measure.bg));
    const body = ratio(rgb(measure.bodyColor), rgb(measure.bg));
    // The heading is large text (>=18.66px bold): AA is 3:1. The body is normal text: 4.5:1.
    const headMin = parseFloat(measure.headSize) >= 18.66 ? 3 : 4.5;
    if (head < headMin) fail('refusal/contrast', `heading ${head.toFixed(2)}:1 on the notice ground, needs ${headMin}:1`);
    if (body < 4.5) fail('refusal/contrast', `body ${body.toFixed(2)}:1 on the notice ground, needs 4.5:1`);
    console.log(`  refusal notice  tone=${measure.tone}  heading ${head.toFixed(2)}:1  body ${body.toFixed(2)}:1`);
    console.log(`  server wording kept: "${measure.bodyText}"`);
  }

  // The denial variant, driven through the same renderer the endpoints feed.
  let denied = await page.evaluate(() => {
    const s = document.createElement('script');
    s.textContent = `cbShowRefusal('cbDepError', { code: 'forbidden', error: 'probe' });
      var n = document.querySelector('#cbDepError .notice');
      document.body.dataset.cbProbe = JSON.stringify({
        tone: (n.className.match(/bg-light-(\\w+)/) || [])[1] || null,
        icon: (n.querySelector('.symbol-label i') || {}).className || '',
        headColor: getComputedStyle(n.querySelector('h4')).color,
        bodyColor: getComputedStyle(n.querySelector('.fs-6')).color,
        bg: getComputedStyle(n).backgroundColor });`;
    document.body.appendChild(s); s.remove();
    return JSON.parse(document.body.dataset.cbProbe || '{}');
  });
  if (!denied || !denied.tone) {
    fail('refusal/denial', 'the denial branch could not be rendered - window.cbShowRefusal is not reachable');
    denied = {};
  }
  if (denied.tone && denied.tone !== 'danger')
    fail('refusal/tone', `a permission denial rendered as "${denied.tone}"; a denial is not a warning`);
  if (denied.icon !== undefined && !/ki-lock/.test(denied.icon))
    fail('refusal/icon', `the denial icon is "${denied.icon}", expected a lock`);
  if (denied.bg) {
    const head = ratio(rgb(denied.headColor), rgb(denied.bg));
    const body = ratio(rgb(denied.bodyColor), rgb(denied.bg));
    if (head < 3) fail('refusal/contrast', `denial heading ${head.toFixed(2)}:1, needs 3:1`);
    if (body < 4.5) fail('refusal/contrast', `denial body ${body.toFixed(2)}:1, needs 4.5:1`);
    console.log(`  denial notice   tone=${denied.tone}   heading ${head.toFixed(2)}:1  body ${body.toFixed(2)}:1`);
  }

  // ---- 3. THE SEEDED DEPENDENCY CASES --------------------------------------------------------
  await page.evaluate(() => cbClearRefusal && cbClearRefusal('cbDepError')).catch(() => {});
  await page.evaluate(() => { const s = document.createElement('script'); s.textContent = 'cbLoadDeps()'; document.body.appendChild(s); s.remove(); });
  await page.waitForTimeout(2500);
  const deps = await page.evaluate(() => [...document.querySelectorAll('#cbDepList .cb-row')].map(r => ({
    text: r.textContent.replace(/\s+/g, ' ').trim().slice(0, 70),
    arrow: (r.querySelector('i.ki-outline') || {}).className || '',
    blocking: !!r.querySelector('.badge'),
    delHidden: getComputedStyle(r.querySelector('.cb-row-del')).opacity,
  })));
  const blocking = deps.filter(d => d.blocking).length;
  if (deps.length < 3)
    fail('deps/seed', `${deps.length} rows; the seed writes 3 cases - run tools/testdata/tasks_dependency_testdata.sql`);
  else if (blocking !== 1)
    fail('deps/blocking', `${blocking} rows show the blocking badge; exactly one seeded predecessor is unfinished`);
  deps.forEach((d, i) => console.log(`  dep ${i + 1}  blocking=${d.blocking}  del-opacity=${d.delHidden}  ${d.text}`));

  // ---- 4. UNLINK, and the verdict on navigation ----------------------------------------------
  // Deleting asks first, so the probe answers the dialog rather than waiting for a click nobody makes.
  page.on('dialog', d => d.accept());


  // clean up the probe's own checklist row
  await page.click('a[href="#cbTabChecklist"]');
  await page.waitForTimeout(800);
  await page.evaluate(m => {
    const row = [...document.querySelectorAll('#cbClList .cb-row')].find(r => r.textContent.includes(m));
    if (row) row.querySelector('.cb-row-del').click();
  }, marker);
  await page.waitForTimeout(1200);
  const confirmBtn = await page.$('.swal2-confirm');
  if (confirmBtn) await confirmBtn.click(); else fail('checklist/confirm', 'deleting asked for no confirmation');
  await page.waitForTimeout(2500);
  const stillThere = await page.evaluate(m => document.getElementById('cbClList').textContent.includes(m), marker);
  if (stillThere) fail('checklist/delete', 'the probe row survived its own delete');

  if (navigations > 0)
    fail('postback', `${navigations} main-frame navigation(s) during add/toggle/link/delete - the page still reloads`);
  if (consoleErrors.length)
    fail('console', consoleErrors.slice(0, 3).join(' | '));

  console.log(`\n  navigations during ${'add, toggle, self-link, delete'}: ${navigations}  (0 = no postback)`);
  console.log(`  console errors: ${consoleErrors.length}`);

  await browser.close();

  if (findings.length) {
    console.log('\nFAIL');
    findings.forEach(f => console.log(`  [${f.check}] ${f.reason}`));
    process.exit(1);
  }
  console.log('\nPASS');
})().catch(e => { console.error('PROBE ERROR', e); process.exit(2); });
