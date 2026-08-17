// Measure the whole semantic ALERT family as the authority paints it, plus the text-emphasis
// tokens the badge/button fixes already use, so the banner fix reuses proven vocabulary.
import { chromium } from 'playwright';

const BASE = process.env.UI_BASE_URL ?? 'http://localhost:5299';
const srgb = (c) => { c /= 255; return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4); };
const lum = ([r, g, b]) => 0.2126 * srgb(r) + 0.7152 * srgb(g) + 0.0722 * srgb(b);
const px = (s) => (String(s).match(/\d+(\.\d+)?/g) || []).slice(0, 3).map(Number);
const ratio = (a, b) => { const [x, y] = [lum(px(a)), lum(px(b))].sort((m, n) => n - m); return ((x + 0.05) / (y + 0.05)); };
const hex = (s) => '#' + px(s).map((n) => Math.round(n).toString(16).padStart(2, '0')).join('');

const browser = await chromium.launch();
const ctx = await browser.newContext({ ignoreHTTPSErrors: true });
const page = await ctx.newPage();
await page.goto(`${BASE}/Account/Login`, { waitUntil: 'domcontentloaded' });
await page.fill('input[name="UserName"]', process.env.UI_USER ?? 'Admin');
await page.fill('input[type="password"]', process.env.UI_PASS ?? 'Admin@123');
await Promise.all([page.waitForNavigation({ waitUntil: 'domcontentloaded' }).catch(() => {}),
                   page.click('button[type="submit"], input[type="submit"]')]);
await page.goto(`${BASE}/Inventory/Index`, { waitUntil: 'networkidle' });

const out = await page.evaluate(() => {
    const host = document.createElement('div');
    host.style.cssText = 'position:absolute;left:-9999px;top:0;background:#fff';
    document.body.appendChild(host);
    const rows = [];
    for (const v of ['alert-primary', 'alert-success', 'alert-warning', 'alert-danger', 'alert-info']) {
        const d = document.createElement('div');
        d.className = `alert ${v}`; d.textContent = 'Sample';
        host.appendChild(d);
        const cs = getComputedStyle(d);
        let bg = cs.backgroundColor, p = d.parentElement;
        while (p && (bg === 'rgba(0, 0, 0, 0)' || bg === 'transparent')) { bg = getComputedStyle(p).backgroundColor; p = p.parentElement; }
        rows.push({ v, color: cs.color, bg });
    }
    // what the emphasis tokens resolve to, on each alert tint
    const cs = getComputedStyle(document.documentElement);
    const tokens = {};
    for (const t of ['--bs-primary-text-emphasis', '--bs-success-text-emphasis', '--bs-warning-text-emphasis',
                     '--bs-danger-text-emphasis', '--bs-info-text-emphasis',
                     '--bs-text-warning', '--bs-text-danger', '--bs-text-success']) {
        tokens[t] = cs.getPropertyValue(t).trim() || '(unset)';
    }
    host.remove();
    return { rows, tokens };
});

console.log('=== semantic ALERT family as the authority paints it ===\n');
console.log('  variant           foreground   background   ratio   AA(4.5)');
for (const r of out.rows) {
    const cr = ratio(r.color, r.bg);
    console.log(`  ${r.v.padEnd(16)} ${hex(r.color).padEnd(12)} ${hex(r.bg).padEnd(12)} ${cr.toFixed(2).padStart(5)}   ${cr >= 4.5 ? 'PASS' : 'FAIL'}`);
}
console.log('\n=== emphasis tokens available (already used by the badge/button fixes) ===');
for (const [k, v] of Object.entries(out.tokens)) console.log(`  ${k.padEnd(30)} ${v}`);

console.log('\n=== projected ratio if each alert took its own emphasis token ===');
const emph = { 'alert-primary': out.tokens['--bs-primary-text-emphasis'], 'alert-success': out.tokens['--bs-success-text-emphasis'],
               'alert-warning': out.tokens['--bs-warning-text-emphasis'], 'alert-danger': out.tokens['--bs-danger-text-emphasis'],
               'alert-info': out.tokens['--bs-info-text-emphasis'] };
for (const r of out.rows) {
    const fg = emph[r.v];
    if (!fg || fg === '(unset)') { console.log(`  ${r.v.padEnd(16)} emphasis token unset`); continue; }
    const cr = ratio(fg, r.bg);
    console.log(`  ${r.v.padEnd(16)} ${fg.padEnd(12)} on ${hex(r.bg)} -> ${cr.toFixed(2)}  ${cr >= 4.5 ? 'PASS' : 'FAIL'}`);
}
await browser.close();
