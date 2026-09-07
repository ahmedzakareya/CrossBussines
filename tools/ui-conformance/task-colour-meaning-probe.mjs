// Reads every coloured element in the tasks table and reports what each colour MEANS, so a
// family carrying two unrelated meanings is visible instead of arguable.
//
// Written because the table used six semantic families for eleven meanings. Three of them were
// outright contradictions: a Finished task's time column was `danger`, the Overdue KPI card was
// `warning` while an overdue ROW was `danger`, and `info` meant both "Medium priority" and
// "In progress" in adjacent columns. None of that is visible in a screenshot - every badge looks
// deliberate on its own - and none of it is findable by grep, because the colour is chosen by a
// switch expression three hundred lines from where it renders.
//
// So the probe collects (family -> set of meanings) from the rendered page and fails when a
// family means more than one thing, or when a meaning is painted two different colours.
//
//   node tools/ui-conformance/task-colour-meaning-probe.mjs --base https://localhost:44368
import { createRequire } from 'node:module';
const require = createRequire(new URL('./package.json', import.meta.url));
const { chromium } = require('playwright');

const arg = (n, d) => { const i = process.argv.indexOf('--' + n); return i > 0 ? process.argv[i + 1] : d; };
const BASE = arg('base', 'https://localhost:44368');
const USER = arg('user', 'admin'), PASS = arg('password', 'Admin@123');

// WCAG from the computed colour, so a value arriving through var() is measured like a literal.
const lum = ([r, g, b]) => {
  const f = (c) => { c /= 255; return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4); };
  return 0.2126 * f(r) + 0.7152 * f(g) + 0.0722 * f(b);
};
const ratio = (a, b) => { const [x, y] = [lum(a), lum(b)].sort((p, q) => q - p); return (x + 0.05) / (y + 0.05); };
const rgb = (s) => (s.match(/\d+(\.\d+)?/g) || []).slice(0, 3).map(Number);

// The rule the table is held to. `primary` is IDENTITY, not a state, so it is allowed on the
// brand surfaces and nowhere else.
const ALLOWED = {
  danger: 'attention: a breach or the top of the risk ramp',
  warning: 'approaching',
  success: 'complete, or comfortably clear',
  info: 'in flight',
  secondary: 'normal',
  primary: 'identity (brand), never a state',
};

const findings = [];
const fail = (check, reason) => findings.push({ check, reason });

(async () => {
  const browser = await chromium.launch();
  const ctx = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1600, height: 1100 } });
  const page = await ctx.newPage();

  await page.goto(BASE + '/Account/Login', { waitUntil: 'domcontentloaded' });
  await page.fill('input[name="UserName"], input[name="Username"], input[name="Email"]', USER);
  await page.fill('input[name="Password"]', PASS);
  await page.click('button[type="submit"]');
  await page.waitForURL(u => !u.toString().includes('/Account/Login'), { timeout: 60000 }).catch(() => {});
  if (page.url().includes('/Account/Login')) { console.error('SIGN-IN FAILED'); process.exit(2); }

  await ctx.addCookies([{ name: '.AspNetCore.Culture', value: 'c=en|uic=en', url: BASE }]);
  await page.goto(BASE + '/Tasks/All', { waitUntil: 'networkidle' });
  await page.waitForTimeout(1800);

  const seen = await page.evaluate(() => {
    const fam = (el) => (el.className.match(/(?:badge-light-|bg-light-|bg-)(\w+)/) || [])[1] || null;
    const out = [];
    // The KPI cards, by their own icon tile.
    document.querySelectorAll('.card .symbol-label, [class*="kpi"]').forEach(el => {
      const f = fam(el); if (!f) return;
      const card = el.closest('.card, a, div');
      const label = (card ? card.textContent : '').replace(/\s+/g, ' ').trim().slice(0, 24);
      if (label) out.push({ where: 'kpi', family: f, meaning: label });
    });
    // Every badge in the body, with the column it sits in.
    const heads = [...document.querySelectorAll('thead th')].map(th => th.textContent.trim());
    document.querySelectorAll('tbody tr').forEach(tr => {
      [...tr.children].forEach((td, i) => {
        td.querySelectorAll('.badge').forEach(bd => {
          const f = fam(bd); if (!f) return;
          out.push({ where: heads[i] || ('col' + i), family: f,
                     meaning: bd.textContent.replace(/\s+/g, ' ').trim(),
                     fg: getComputedStyle(bd).color, bg: getComputedStyle(bd).backgroundColor });
        });
        td.querySelectorAll('.progress').forEach(p => {
          const f = fam(p.querySelector('.progress-bar')); if (!f) return;
          out.push({ where: heads[i] || ('col' + i), family: f,
                     meaning: p.getAttribute('aria-valuenow') === '100' ? 'progress complete' : 'progress in flight' });
        });
      });
    });
    return out;
  });

  // ---- one family, one meaning ---------------------------------------------------------------
  const byFamily = {};
  const byMeaning = {};
  for (const s of seen) {
    if (!ALLOWED[s.family]) fail('palette', `"${s.family}" is not in the rule (${s.where}: ${s.meaning})`);
    (byFamily[s.family] ||= new Set()).add(s.where + ' = ' + s.meaning);
    (byMeaning[s.meaning] ||= new Set()).add(s.family);
  }
  for (const [m, fams] of Object.entries(byMeaning)) {
    if (fams.size > 1) fail('one-meaning-two-colours', `"${m}" is painted ${[...fams].join(' and ')}`);
  }

  console.log('  what each colour is used for:');
  for (const f of Object.keys(ALLOWED)) {
    if (!byFamily[f]) continue;
    console.log(`    ${f.padEnd(10)} ${ALLOWED[f]}`);
    [...byFamily[f]].sort().forEach(u => console.log(`               - ${u}`));
  }

  // ---- and it still has to be readable -------------------------------------------------------
  let worst = 99, worstWhat = '';
  for (const s of seen) {
    if (!s.fg || !s.bg) continue;
    const c = ratio(rgb(s.fg), rgb(s.bg));
    if (c < worst) { worst = c; worstWhat = `${s.family} "${s.meaning}"`; }
    if (c < 4.5) fail('contrast', `${s.family} "${s.meaning}" is ${c.toFixed(2)}:1, needs 4.5:1`);
  }
  console.log(`\n  lowest label contrast: ${worst.toFixed(2)}:1  (${worstWhat})`);

  await browser.close();
  if (findings.length) {
    console.log('\nFAIL');
    findings.forEach(f => console.log(`  [${f.check}] ${f.reason}`));
    process.exit(1);
  }
  console.log('\nPASS: every family carries one meaning, every meaning one family, all labels >= 4.5:1');
})().catch(e => { console.error('PROBE ERROR', e); process.exit(2); });
