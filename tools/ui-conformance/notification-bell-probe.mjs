// Opens the notification bell and reports what each row actually renders, plus the measured
// contrast of the header's text against the header's real background.
//
// Written because the dropdown and /Notifications/Index render the SAME rows from the same table
// and had drifted: the page coloured the icon tile by category and showed category, priority and an
// unread dot; the dropdown showed none of them, so five escalations were five identical grey lines.
// A grep cannot see that - the classes were present and valid, just not the ones the page uses.
//
//   node tools/ui-conformance/notification-bell-probe.mjs --base http://localhost:5411
import { createRequire } from 'node:module';
const require = createRequire(new URL('./package.json', import.meta.url));
const { chromium } = require('playwright');

const arg = (n, d) => { const i = process.argv.indexOf('--' + n); return i > 0 ? process.argv[i + 1] : d; };
const BASE = arg('base', 'http://localhost:5411');
const USER = arg('user', 'Admin'), PASS = arg('password', 'Admin@123');

// Relative luminance / contrast, straight from WCAG - computed from the COMPUTED colour, so a value
// arriving through var(), a class or the theme bundle is measured the same as a literal.
const lum = ([r, g, b]) => {
  const f = (c) => { c /= 255; return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4); };
  return 0.2126 * f(r) + 0.7152 * f(g) + 0.0722 * f(b);
};
const ratio = (a, b) => { const [x, y] = [lum(a), lum(b)].sort((p, q) => q - p); return (x + 0.05) / (y + 0.05); };
const rgb = (s) => (s.match(/\d+(\.\d+)?/g) || []).slice(0, 3).map(Number);
const alpha = (s) => { const m = s.match(/rgba?\([^)]*,\s*([\d.]+)\s*\)/); return m ? parseFloat(m[1]) : 1; };
// Flatten a translucent foreground onto its background, which is what the eye actually sees.
const over = (fg, a, bg) => fg.map((c, i) => c * a + bg[i] * (1 - a));

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

  for (const lang of ['en', 'ar']) {
    await ctx.addCookies([{ name: '.AspNetCore.Culture', value: `c=${lang}|uic=${lang}`, url: BASE }]);
    await page.goto(BASE + '/Calendar/Index', { waitUntil: 'networkidle' });

    // The bell fetches on load; give the list a moment to arrive before opening the menu.
    await page.waitForFunction(() => {
      const l = document.getElementById('cbnb_list');
      return l && (l.querySelectorAll('.cbnb-item').length > 0 || l.querySelector('#cbnb_empty'));
    }, { timeout: 20000 }).catch(() => {});

    const out = await page.evaluate(() => {
      const t = (el) => (el ? el.textContent.replace(/\s+/g, ' ').trim() : '');
      const list = document.getElementById('cbnb_list');
      const rows = [...(list ? list.querySelectorAll('.cbnb-item') : [])].slice(0, 6).map(el => ({
        title: t(el.querySelector('.fw-bold')),
        badges: [...el.querySelectorAll('.badge')].map(b => t(b)),
        // The icon tile's class is what carries the category colour.
        tile: (el.querySelector('.symbol-label') || {}).className || '(avatar)',
        iconColor: el.querySelector('.symbol-label i') ? getComputedStyle(el.querySelector('.symbol-label i')).color : '',
        unreadDot: !!el.querySelector('.bullet-dot'),
        tinted: el.classList.contains('bg-light-primary'),
        stamp: t(el.querySelector('.fs-9.text-gray-400')),
        // A third line that still holds a locale string would show a comma or a meridiem.
        stampLooksLocale: /[,]|AM|PM|ص|م$/.test(t(el.querySelector('.fs-9.text-gray-400'))),
      }));
      const hdr = list ? list.previousElementSibling : null;
      const link = hdr ? hdr.querySelector('a') : null;
      const label = hdr ? hdr.querySelector('span') : null;
      const cs = (el) => (el ? getComputedStyle(el) : null);
      return {
        rows,
        header: hdr ? {
          bg: cs(hdr).backgroundColor,
          titleColor: label ? cs(label).color : '',
          linkColor: link ? cs(link).color : '',
          linkOpacity: link ? cs(link).opacity : '1',
          linkText: t(link),
          titleText: t(label),
        } : null,
      };
    });

    console.log(`\n===== ${lang.toUpperCase()} =====`);
    if (out.header) {
      const bg = rgb(out.header.bg);
      const lc = rgb(out.header.linkColor);
      const la = Math.min(alpha(out.header.linkColor), parseFloat(out.header.linkOpacity) || 1);
      const tc = rgb(out.header.titleColor);
      console.log(`header      : "${out.header.titleText}" / "${out.header.linkText}"  bg=${out.header.bg}`);
      console.log(`  title     : ${out.header.titleColor}  ratio ${ratio(tc, bg).toFixed(2)}  ${ratio(tc, bg) >= 4.5 ? 'PASS' : 'FAIL (needs 4.5 for small text)'}`);
      const eff = over(lc, la, bg);
      console.log(`  read-all  : ${out.header.linkColor} @ opacity ${out.header.linkOpacity}  effective ratio ${ratio(eff, bg).toFixed(2)}  ${ratio(eff, bg) >= 4.5 ? 'PASS' : 'FAIL (needs 4.5 for small text)'}`);
    }
    console.log(`rows        : ${out.rows.length}`);
    for (const r of out.rows) {
      console.log(`  · ${r.title}`);
      console.log(`      badges=[${r.badges.join(', ')}]  dot=${r.unreadDot}  tinted=${r.tinted}`);
      console.log(`      tile="${r.tile}"  iconColor=${r.iconColor}`);
      console.log(`      stamp="${r.stamp}"${r.stampLooksLocale ? '   <-- STILL A LOCALE STRING' : ''}`);
    }
    const distinct = new Set(out.rows.map(r => r.badges.join('|') + r.tile)).size;
    console.log(`  -- ${distinct} visually distinct row signatures out of ${out.rows.length}`);
  }
  await browser.close();
})();
