// axe "incomplete" = could not decide. It is NOT a pass, so every incomplete colour-contrast node
// is triaged here: what is it, why could axe not decide, and what does it actually measure when the
// background is resolved by walking the real paint chain.
import { chromium } from 'playwright';
import { AxeBuilder } from '@axe-core/playwright';

const BASE = process.env.UI_BASE_URL ?? 'http://localhost:5299';
const srgb = (c) => { c /= 255; return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4); };
const lum = ([r, g, b]) => 0.2126 * srgb(r) + 0.7152 * srgb(g) + 0.0722 * srgb(b);
const parse = (s) => {
    if (!s) return [0, 0, 0];
    if (s.startsWith('#')) { const h = s.slice(1); return [0, 2, 4].map((i) => parseInt(h.substr(i, 2), 16)); }
    return (s.match(/\d+(\.\d+)?/g) || []).slice(0, 3).map(Number);
};
const ratio = (a, b) => { const [x, y] = [lum(parse(a)), lum(parse(b))].sort((m, n) => n - m); return (x + 0.05) / (y + 0.05); };

const browser = await chromium.launch();
const ctx = await browser.newContext({ ignoreHTTPSErrors: true });
const page = await ctx.newPage();
await page.setViewportSize({ width: 1440, height: 900 });
await page.goto(`${BASE}/Account/Login`, { waitUntil: 'domcontentloaded' });
await page.fill('input[name="UserName"]', process.env.UI_USER ?? 'Admin');
await page.fill('input[type="password"]', process.env.UI_PASS ?? 'Admin@123');
await Promise.all([page.waitForNavigation({ waitUntil: 'domcontentloaded' }).catch(() => {}),
                   page.click('button[type="submit"], input[type="submit"]')]);

const ROUTES = ['/Inventory/Index', '/Tasks', '/Calendar', '/Comm', '/Workspace', '/Reports'];
const kinds = new Map();
let totalIncomplete = 0;

for (const p of ROUTES) {
    await page.goto(`${BASE}${p}`, { waitUntil: 'networkidle' });
    await page.waitForTimeout(1000);
    const a = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze();
    const inc = a.incomplete.find((x) => x.id === 'color-contrast');
    if (!inc) continue;
    totalIncomplete += inc.nodes.length;

    for (const n of inc.nodes) {
        const why = (n.any[0]?.message ?? '').trim();
        const d = await page.evaluate((sel) => {
            const el = document.querySelector(sel);
            if (!el) return null;
            const cs = getComputedStyle(el);
            let bg = cs.backgroundColor, q = el.parentElement, hops = 0;
            while (q && (bg === 'rgba(0, 0, 0, 0)' || bg === 'transparent') && hops < 12) { bg = getComputedStyle(q).backgroundColor; q = q.parentElement; hops++; }
            const img = (() => { let e = el, h = 0; while (e && h < 6) { const b = getComputedStyle(e).backgroundImage; if (b && b !== 'none') return b.slice(0, 60); e = e.parentElement; h++; } return 'none'; })();
            return { cls: (el.className?.toString() || '').slice(0, 55), tag: el.tagName.toLowerCase(),
                     color: cs.color, bg, bgImage: img, size: cs.fontSize, weight: cs.fontWeight,
                     text: (el.textContent || '').trim().slice(0, 26) };
        }, n.target.join(' ')).catch(() => null);
        if (!d) continue;
        const key = `${d.cls}|${why.slice(0, 45)}`;
        if (!kinds.has(key)) kinds.set(key, { ...d, why, route: p, count: 0 });
        kinds.get(key).count++;
    }
}

console.log(`=== color-contrast INCOMPLETE triage — ${totalIncomplete} node(s) across ${ROUTES.length} routes ===\n`);
if (!kinds.size) console.log('  none');
for (const k of kinds.values()) {
    const r = ratio(k.color, k.bg);
    console.log(`  <${k.tag} class="${k.cls}">  x${k.count}   first seen ${k.route}`);
    console.log(`     text        : "${k.text}"`);
    console.log(`     axe reason  : ${k.why.slice(0, 110)}`);
    console.log(`     computed    : ${k.color} on ${k.bg}  (${k.size}/${k.weight})`);
    console.log(`     bg-image    : ${k.bgImage}`);
    console.log(`     RESOLVED    : ${r.toFixed(2)}:1  ${r >= 4.5 ? 'PASS' : (r >= 3 && parseFloat(k.size) >= 18.66 ? 'PASS (large text)' : 'FAIL')}`);
    console.log('');
}

// correct cbev measurement (the earlier NaN was a parser bug, not a finding)
await page.goto(`${BASE}/Calendar`, { waitUntil: 'networkidle' });
await page.waitForTimeout(2500);
const cbev = await page.evaluate(() => {
    const g = (s) => { const e = document.querySelector(s); if (!e) return null; const cs = getComputedStyle(e); return { c: cs.color, b: cs.backgroundColor }; };
    return { personal: g('.cbev-personal'), company: g('.cbev-company') };
});
console.log('=== cbev event colours (correctly computed) ===');
for (const [k, v] of Object.entries(cbev)) {
    if (!v) { console.log(`  ${k}: not rendered`); continue; }
    console.log(`  ${k.padEnd(9)} ${v.c} on ${v.b} = ${ratio(v.c, v.b).toFixed(2)}:1  ${ratio(v.c, v.b) >= 4.5 ? 'PASS' : 'FAIL'}`);
}
await browser.close();
