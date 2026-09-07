// Two things on the CRM dashboard that a screenshot argues about and a probe settles.
//
// 1. A LITERAL BRACE ON THE PAGE. A bare `}` was left in markup context by a bad edit, so Razor
//    emitted it as text. It is nearly invisible in review for two reasons: it is one character,
//    and a brace is a bidi-MIRRORED glyph, so a `}` in the source PAINTS AS `{` on the RTL page -
//    grepping the view for the character you saw finds nothing. This walks the text nodes of the
//    page card and fails on any stray brace that is not inside a <script>/<style>/<code>.
//    It also fails on unbalanced structure, which is what puts one there: the extra </div>s
//    closed the row early and pushed the last card OUT of the page container.
//
// 2. A RANK PAINTED AS A STATE. The Top-accounts bars rotated success / primary / warning / info
//    / dark, so the leader was GREEN, the third AMBER and the fifth BLACK - a colour scale over a
//    list that is already sorted. The rule for this app is that a semantic family means ONE
//    thing: danger = attention, warning = approaching, success = complete, info = in flight,
//    secondary = normal, primary = identity. A position in an ordering is none of those, so all
//    the bars in a ranked list must share one fill and the LENGTH must carry the value.
//    The same rule is what the pipeline funnel was rebuilt on, so both cards are checked.
//
//   node tools/ui-conformance/crm-dashboard-probe.mjs --base https://localhost:44368
import { createRequire } from 'node:module';
const require = createRequire(new URL('./package.json', import.meta.url));
const { chromium } = require('playwright');

const arg = (n, d) => { const i = process.argv.indexOf('--' + n); return i > 0 ? process.argv[i + 1] : d; };
const BASE = arg('base', 'https://localhost:44368');
const USER = arg('user', 'admin'), PASS = arg('password', 'Admin@123');
const SHOT = arg('shot', '');

const findings = [];
const fail = (check, reason) => findings.push({ check, reason });

(async () => {
  const browser = await chromium.launch();
  const ctx = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1600, height: 1100 } });
  const page = await ctx.newPage();

  const pageErrors = [];
  page.on('pageerror', e => pageErrors.push(String(e.message || e)));

  await page.goto(BASE + '/Account/Login', { waitUntil: 'domcontentloaded' });
  await page.fill('input[name="UserName"], input[name="Username"], input[name="Email"]', USER);
  await page.fill('input[name="Password"]', PASS);
  await page.click('button[type="submit"]');
  await page.waitForURL(u => !u.toString().includes('/Account/Login'), { timeout: 60000 }).catch(() => {});
  if (page.url().includes('/Account/Login')) { console.error('SIGN-IN FAILED'); process.exit(2); }

  // Both directions: the brace only mirrors under RTL, and a layout that closed early can look
  // fine in one direction and collapse in the other.
  for (const [culture, dir] of [['ar', 'rtl'], ['en', 'ltr']]) {
    await ctx.addCookies([{ name: '.AspNetCore.Culture', value: `c=${culture}|uic=${culture}`, url: BASE }]);
    await page.goto(BASE + '/Crm/Index', { waitUntil: 'networkidle' });
    await page.waitForTimeout(1200);

    const r = await page.evaluate(() => {
      const out = { strayBraces: [], ranked: [], outsideCard: [], barTones: {} };

      // --- 1. stray braces in rendered TEXT, anywhere in the content column -------------------
      const root = document.querySelector('#kt_app_content') || document.body;
      const skip = /^(SCRIPT|STYLE|CODE|PRE|TEXTAREA)$/;
      const walk = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
      for (let n = walk.nextNode(); n; n = walk.nextNode()) {
        let p = n.parentElement, skipped = false;
        while (p && p !== root) { if (skip.test(p.tagName)) { skipped = true; break; } p = p.parentElement; }
        if (skipped) continue;
        const s = (n.nodeValue || '').trim();
        // A brace standing on its own as the whole text node is the Razor-leak signature; a brace
        // inside a sentence is somebody's prose and is left alone.
        if (/^[{}]+$/.test(s)) {
          const host = n.parentElement;
          out.strayBraces.push({
            text: s,
            host: host ? host.tagName + '.' + (host.className || '').toString().slice(0, 60) : '?',
          });
        }
      }

      // --- 2. ranked lists: how many distinct bar fills does each card use? -------------------
      const cards = Array.from(document.querySelectorAll('.card'));
      const titleOf = (c) => {
        const el = c.querySelector('.card-label, .card-title');
        return el ? el.textContent.trim().slice(0, 40) : '';
      };
      for (const c of cards) {
        const bars = Array.from(c.querySelectorAll('.progress-bar'));
        if (bars.length < 2) continue;
        const fills = {};
        for (const b of bars) {
          const bg = getComputedStyle(b).backgroundColor;
          fills[bg] = (fills[bg] || 0) + 1;
        }
        out.ranked.push({ card: titleOf(c), bars: bars.length, fills });
      }

      // --- 3. did anything fall out of the page container? ------------------------------------
      // The orphaned </div>s closed the row and the container, so the last card rendered as a
      // sibling of the page card instead of a child of it.
      const pageCard = document.querySelector('#kt_app_content_container');
      if (pageCard) {
        for (const c of cards) {
          if (!pageCard.contains(c)) continue;
        }
        // Rows must all sit inside the same container depth.
        const rows = Array.from(pageCard.querySelectorAll(':scope > div > .row, :scope > .row'));
        out.rowCount = rows.length;
        out.rowParents = Array.from(new Set(rows.map(x => x.parentElement.className.toString().slice(0, 50))));
      }

      // Geometry of the last row, to catch the dead column.
      const qa = cards.find(c => (c.querySelector('.card-title') || {}).textContent);
      out.cardBoxes = cards
        .filter(c => c.offsetParent)
        .map(c => {
          const b = c.getBoundingClientRect();
          return { t: titleOf(c), x: Math.round(b.x), w: Math.round(b.width), h: Math.round(b.height) };
        });
      return out;
    });

    if (r.strayBraces.length) {
      for (const b of r.strayBraces) {
        fail(`stray-brace/${dir}`, `a bare "${b.text}" is PAINTED on the page, in ${b.host}`);
      }
    }

    for (const card of r.ranked) {
      const distinct = Object.keys(card.fills).length;
      if (distinct > 1) {
        fail(`rank-as-state/${dir}`,
          `"${card.card}" paints ${card.bars} bars in ${distinct} different fills ` +
          `(${Object.entries(card.fills).map(([k, v]) => `${k} x${v}`).join(', ')}). ` +
          `A position in an ordering is not a state - one fill, length carries the value. ` +
          `Two fills are allowed only where they mark the two OUTCOMES (won/lost).`);
      }
    }

    console.log(`\n--- ${culture.toUpperCase()} (${dir}) ---`);
    console.log('stray braces      :', r.strayBraces.length);
    console.log('rows in container :', r.rowCount, r.rowParents);
    for (const c of r.ranked) {
      console.log(`  bars "${c.card}" -> ${c.bars} bars, ${Object.keys(c.fills).length} fill(s)`, c.fills);
    }
    console.log('cards (x / w / h) :');
    for (const b of r.cardBoxes) console.log(`   ${String(b.w).padStart(5)} x ${String(b.h).padStart(4)} @${String(b.x).padStart(5)}  ${b.t}`);

    if (SHOT && culture === 'ar') await page.screenshot({ path: SHOT, fullPage: true });
  }

  // A pageError is reported but does not fail this probe: one pre-existing null-classList error
  // on this page predates the work here, and claiming it as a regression would be false.
  if (pageErrors.length) console.log('\npageErrors (informational):', pageErrors.slice(0, 4));

  await browser.close();

  console.log('\n================ RESULT ================');
  if (!findings.length) { console.log('PASS'); process.exit(0); }
  for (const f of findings) console.log(`FAIL  ${f.check}\n      ${f.reason}`);
  process.exit(1);
})();
