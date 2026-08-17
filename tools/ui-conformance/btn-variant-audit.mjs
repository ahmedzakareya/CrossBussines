// Is there ANY compliant semantic button variant in the authority vocabulary today?
// Measures every btn-*success/danger variant as the authority actually paints it. Read-only: the
// probe elements are created in a detached test container and removed again; no app file changes.
import { chromium } from 'playwright';

const BASE = process.env.UI_BASE_URL ?? 'http://localhost:5299';
const srgb = (c) => { c /= 255; return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4); };
const lum = ([r, g, b]) => 0.2126 * srgb(r) + 0.7152 * srgb(g) + 0.0722 * srgb(b);
const px = (s) => (String(s).match(/\d+(\.\d+)?/g) || []).slice(0, 3).map(Number);
const ratio = (a, b) => { const [x, y] = [lum(px(a)), lum(px(b))].sort((m, n) => n - m); return ((x + 0.05) / (y + 0.05)); };

const browser = await chromium.launch();
const ctx = await browser.newContext({ ignoreHTTPSErrors: true });
const page = await ctx.newPage();
await page.goto(`${BASE}/Account/Login`, { waitUntil: 'domcontentloaded' });
await page.fill('input[name="UserName"]', process.env.UI_USER ?? 'Admin');
await page.fill('input[type="password"]', process.env.UI_PASS ?? 'Admin@123');
await Promise.all([page.waitForNavigation({ waitUntil: 'domcontentloaded' }).catch(() => {}),
                   page.click('button[type="submit"], input[type="submit"]')]);
await page.goto(`${BASE}/Tasks/MatchSuggestions`, { waitUntil: 'networkidle' });

const rows = await page.evaluate(() => {
    const variants = ['btn-success', 'btn-light-success', 'btn-active-light-success',
                      'btn-danger', 'btn-light-danger', 'btn-active-light-danger',
                      'btn-primary', 'btn-light-primary'];
    const host = document.createElement('div');
    host.style.cssText = 'position:absolute;left:-9999px;top:0;background:#fff';
    document.body.appendChild(host);
    const out = [];
    for (const v of variants) {
        const b = document.createElement('button');
        b.className = `btn btn-sm ${v}`;
        b.textContent = 'Sample';
        host.appendChild(b);
        const cs = getComputedStyle(b);
        let bg = cs.backgroundColor, p = b.parentElement;
        while (p && (bg === 'rgba(0, 0, 0, 0)' || bg === 'transparent')) { bg = getComputedStyle(p).backgroundColor; p = p.parentElement; }
        out.push({ v, color: cs.color, bg });
    }
    host.remove();
    return out;
});

console.log('=== semantic BUTTON variants as the authority paints them (white page) ===\n');
console.log('  variant                     foreground        background        ratio   AA(4.5)');
for (const r of rows) {
    const cr = ratio(r.color, r.bg);
    console.log(`  ${r.v.padEnd(26)} ${r.color.padEnd(17)} ${r.bg.padEnd(17)} ${cr.toFixed(2).padStart(5)}   ${cr >= 4.5 ? 'PASS' : 'FAIL'}`);
}
await browser.close();
