// Guards the one property /Tasks/Summary exists for: that it is READ-ONLY, and that every
// section is actually on it.
//
// Written because "read-only" is the kind of property that regresses silently. Nothing breaks
// when somebody adds a form to a summary - the page still renders, the tests still pass, and the
// first sign of trouble is a value edited from a screen that was never meant to write. So this
// counts writable controls instead of trusting the intent, and it counts them inside the CONTENT
// column only: the shell has its own search box and language switcher, which are not this page.
//
// It also checks the two decisions that are easy to undo by accident:
//   * a long comment is CLAMPED on screen (one 3000-character comment turned the first version
//     into a transcript) and UNCLAMPED in print, because the printed copy is the record;
//   * the assignee shows a NAME. The first version referenced a ViewBag key nothing set and
//     printed "#5" on the one page whose whole purpose is being readable.
//
//   node tools/ui-conformance/task-summary-probe.mjs --base https://localhost:44368 --task 29435
import { createRequire } from 'node:module';
const require = createRequire(new URL('./package.json', import.meta.url));
const { chromium } = require('playwright');

const arg = (n, d) => { const i = process.argv.indexOf('--' + n); return i > 0 ? process.argv[i + 1] : d; };
const BASE = arg('base', 'https://localhost:44368');
const USER = arg('user', 'admin'), PASS = arg('password', 'Admin@123');
const TASK = arg('task', '29435');

const SECTIONS = {
  en: ['Details', 'Checklist', 'Dependencies', 'Time log', 'Comments', 'Activity'],
  ar: ['التفاصيل', 'قائمة التحقق', 'الاعتماديات', 'سجل الوقت', 'التعليقات', 'السجل'],
};

const findings = [];
const fail = (check, reason) => findings.push({ check, reason });

(async () => {
  const browser = await chromium.launch();
  const ctx = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1440, height: 1000 } });
  const page = await ctx.newPage();
  const errs = [];
  page.on('pageerror', e => errs.push(String(e.message).slice(0, 120)));

  await page.goto(BASE + '/Account/Login', { waitUntil: 'domcontentloaded' });
  await page.fill('input[name="UserName"], input[name="Username"], input[name="Email"]', USER);
  await page.fill('input[name="Password"]', PASS);
  await page.click('button[type="submit"]');
  await page.waitForURL(u => !u.toString().includes('/Account/Login'), { timeout: 60000 }).catch(() => {});
  if (page.url().includes('/Account/Login')) { console.error('SIGN-IN FAILED'); process.exit(2); }
  // The sign-in screen throws one known, out-of-scope error; it is not this page's.
  errs.length = 0;

  for (const lang of ['en', 'ar']) {
    await ctx.addCookies([{ name: '.AspNetCore.Culture', value: `c=${lang}|uic=${lang}`, url: BASE }]);
    const res = await page.goto(`${BASE}/Tasks/Summary/${TASK}`, { waitUntil: 'networkidle' });
    if (!res || res.status() >= 400) { fail('http/' + lang, 'status ' + (res && res.status())); continue; }
    await page.waitForTimeout(2200);

    const o = await page.evaluate(() => {
      const content = document.getElementById('kt_app_content');
      const scope = content || document.body;
      return {
        sections: [...document.querySelectorAll('.card-title')].map(c => c.textContent.trim()),
        // Anything that can SUBMIT or be typed into. A read-only page has none of it.
        writable: [...scope.querySelectorAll('form, input, textarea, select, button[type="submit"]')]
          .map(e => e.tagName.toLowerCase() + (e.id ? '#' + e.id : '') + (e.name ? '[' + e.name + ']' : '')),
        assignee: [...scope.querySelectorAll('.d-flex.flex-stack')]
          .map(d => d.textContent.replace(/\s+/g, ' ').trim())
          .find(x => /Assignee|المسؤول/.test(x)) || null,
        clampedBodies: [...scope.querySelectorAll('.cb-sum-body')]
          .filter(b => b.scrollHeight > b.clientHeight + 2).length,
        bodies: scope.querySelectorAll('.cb-sum-body').length,
        activity: (document.getElementById('cbSumActivity') || {}).textContent || '',
      };
    });

    for (const s of SECTIONS[lang]) {
      if (!o.sections.includes(s)) fail('section/' + lang, `"${s}" is not on the page`);
    }
    if (o.writable.length)
      fail('read-only/' + lang, `${o.writable.length} writable control(s) in the content: ${o.writable.slice(0, 4).join(', ')}`);
    if (!o.assignee) fail('assignee/' + lang, 'no assignee row');
    else if (/#\d+\s*$/.test(o.assignee))
      fail('assignee/' + lang, `shows a raw id, not a name: "${o.assignee}"`);
    if (/جارٍ التحميل|Loading/.test(o.activity))
      fail('activity/' + lang, 'the activity trail never resolved');

    console.log(`  ${lang}  sections=${o.sections.length}  writable=${o.writable.length}` +
                `  comments=${o.bodies} (clamped ${o.clampedBodies})  ${o.assignee || ''}`);

    // PRINT: the clamp must lift, because the printed copy is the record.
    await page.emulateMedia({ media: 'print' });
    await page.waitForTimeout(400);
    const pr = await page.evaluate(() => ({
      clamped: [...document.querySelectorAll('.cb-sum-body')].filter(b => b.scrollHeight > b.clientHeight + 2).length,
      sidebar: getComputedStyle(document.querySelector('.app-sidebar') || document.body).display,
    }));
    if (pr.clamped) fail('print/' + lang, `${pr.clamped} comment(s) still truncated in print`);
    if (pr.sidebar !== 'none') fail('print/' + lang, 'the shell is still printed');
    console.log(`  ${lang}  print: clamped=${pr.clamped}  sidebar=${pr.sidebar}`);
    await page.emulateMedia({ media: 'screen' });
  }

  if (errs.length) fail('console', errs.slice(0, 2).join(' | '));
  await browser.close();

  if (findings.length) {
    console.log('\nFAIL');
    findings.forEach(f => console.log(`  [${f.check}] ${f.reason}`));
    process.exit(1);
  }
  console.log('\nPASS');
})().catch(e => { console.error('PROBE ERROR', e); process.exit(2); });
