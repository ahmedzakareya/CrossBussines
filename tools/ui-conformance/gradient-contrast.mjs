// Gradient-aware close-out for the axe "incomplete" colour-contrast nodes.
// A naive background walk returns the element's transparent background-color and reports a false
// 1.00:1. Here every gradient STOP is extracted and the WORST stop is measured, which is the
// honest bound for text painted anywhere along that gradient.
import { chromium } from 'playwright';
import { AxeBuilder } from '@axe-core/playwright';

const BASE = process.env.UI_BASE_URL ?? 'http://localhost:5299';
const srgb = (c) => { c /= 255; return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4); };
const lum = ([r, g, b]) => 0.2126 * srgb(r) + 0.7152 * srgb(g) + 0.0722 * srgb(b);
const parse = (s) => {
    if (!s) return null;
    if (s.startsWith('#')) { const h = s.length === 4 ? s.slice(1).split('').map((c) => c + c).join('') : s.slice(1); return [0, 2, 4].map((i) => parseInt(h.substr(i, 2), 16)); }
    const m = (s.match(/\d+(\.\d+)?/g) || []).slice(0, 3).map(Number);
    return m.length === 3 ? m : null;
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

const ROUTES = ['/Inventory/Index', '/Tasks', '/Calendar', '/Comm', '/Workspace', '/Reports',
                '/BusinessEventMonitor', '/Tasks/Board', '/Calendar/Timeline'];
const buckets = new Map();
let total = 0, worstFail = null;

for (const p of ROUTES) {
    await page.goto(`${BASE}${p}`, { waitUntil: 'networkidle' });
    await page.waitForTimeout(1000);
    const a = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze();
    const inc = a.incomplete.find((x) => x.id === 'color-contrast');
    if (!inc) continue;

    for (const n of inc.nodes) {
        total++;
        const d = await page.evaluate((sel) => {
            const el = document.querySelector(sel);
            if (!el) return null;
            const cs = getComputedStyle(el);
            // nearest ancestor that actually paints: a gradient, or an opaque colour
            let node = el, grad = null, solid = null, hops = 0;
            while (node && hops < 12) {
                const s = getComputedStyle(node);
                if (!grad && s.backgroundImage && s.backgroundImage !== 'none' && /gradient/.test(s.backgroundImage)) grad = s.backgroundImage;
                const bc = s.backgroundColor;
                if (!solid && bc && bc !== 'rgba(0, 0, 0, 0)' && bc !== 'transparent') solid = bc;
                if (grad || solid) break;
                node = node.parentElement; hops++;
            }
            return { cls: (el.className?.toString() || '').slice(0, 45), color: cs.color,
                     size: parseFloat(cs.fontSize), weight: cs.fontWeight, grad, solid,
                     text: (el.textContent || '').trim().slice(0, 22) };
        }, n.target.join(' ')).catch(() => null);
        if (!d) continue;

        // every colour stop in the gradient
        const stops = d.grad ? (d.grad.match(/rgba?\([^)]+\)|#[0-9a-fA-F]{3,6}/g) || []) : [];
        const surfaces = stops.length ? stops : [d.solid ?? 'rgb(255,255,255)'];
        let worst = Infinity, worstOn = null;
        for (const s of surfaces) { const r = ratio(d.color, s); if (r < worst) { worst = r; worstOn = s; } }

        const large = d.size >= 24 || (d.size >= 18.66 && Number(d.weight) >= 700);
        const need = large ? 3.0 : 4.5;
        const pass = worst >= need;
        if (!pass && (!worstFail || worst < worstFail.worst)) worstFail = { ...d, worst, worstOn, need };

        const key = `${d.cls}|${d.grad ? 'gradient' : 'solid'}`;
        if (!buckets.has(key)) buckets.set(key, { ...d, worst, worstOn, need, pass, count: 0, surfaces: surfaces.length, route: p });
        buckets.get(key).count++;
    }
}

console.log(`=== GRADIENT-AWARE CLOSE-OUT of axe "incomplete" colour-contrast — ${total} node(s) ===\n`);
let allPass = true;
for (const b of buckets.values()) {
    if (!b.pass) allPass = false;
    console.log(`  ${b.pass ? 'PASS' : 'FAIL'}  x${String(b.count).padStart(3)}  <.${b.cls}>  "${b.text}"   (${b.route})`);
    console.log(`        fg ${b.color}  ${b.grad ? `gradient with ${b.surfaces} stop(s)` : `solid ${b.solid}`}`);
    console.log(`        worst surface ${b.worstOn}  ->  ${b.worst.toFixed(2)}:1   (needs ${b.need})`);
}
console.log(`\n  ALL INCOMPLETE NODES RESOLVE TO PASS: ${allPass}`);
if (worstFail) console.log(`  WORST FAILING: .${worstFail.cls} ${worstFail.worst.toFixed(2)}:1 on ${worstFail.worstOn}`);

// correct cbev measurement
await page.goto(`${BASE}/Calendar`, { waitUntil: 'networkidle' });
await page.waitForTimeout(2500);
const cbev = await page.evaluate(() => {
    const g = (s) => { const e = document.querySelector(s); if (!e) return null; const cs = getComputedStyle(e); return { c: cs.color, b: cs.backgroundColor }; };
    return { personal: g('.cbev-personal'), company: g('.cbev-company') };
});
console.log('\n=== cbev approved event colours (correct computation) ===');
for (const [k, v] of Object.entries(cbev)) {
    if (!v) { console.log(`  ${k}: not rendered`); continue; }
    const r = ratio(v.c, v.b);
    console.log(`  ${k.padEnd(9)} ${v.c} on ${v.b} = ${r.toFixed(2)}:1  ${r >= 4.5 ? 'PASS' : 'FAIL'}`);
}
await browser.close();
