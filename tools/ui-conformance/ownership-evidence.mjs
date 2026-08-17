// OWNERSHIP EVIDENCE for the residual Tasks + Calendar contrast findings.
//
//  A. Do the semantic BUTTON classes fail identically on the Inventory authority? If yes the token
//     is authority-owned and a Tasks-local override would be divergence, not a fix.
//  B. For each failing Calendar node, walk the FULL ancestor chain recording background-color and
//     opacity at every level, so "which surface is actually painted behind this text" is answered
//     from the DOM instead of from a generated id.
import { chromium } from 'playwright';
import { AxeBuilder } from '@axe-core/playwright';

const BASE = process.env.UI_BASE_URL ?? 'http://localhost:5299';
const srgb = (c) => { c /= 255; return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4); };
const lum = ([r, g, b]) => 0.2126 * srgb(r) + 0.7152 * srgb(g) + 0.0722 * srgb(b);
const px = (s) => (String(s).match(/\d+(\.\d+)?/g) || []).slice(0, 3).map(Number);
const ratio = (a, b) => { const [x, y] = [lum(px(a)), lum(px(b))].sort((m, n) => n - m); return ((x + 0.05) / (y + 0.05)).toFixed(2); };

const browser = await chromium.launch();
const ctx = await browser.newContext({ ignoreHTTPSErrors: true });
const page = await ctx.newPage();
await page.setViewportSize({ width: 1440, height: 900 });
await page.goto(`${BASE}/Account/Login`, { waitUntil: 'domcontentloaded' });
await page.fill('input[name="UserName"]', process.env.UI_USER ?? 'Admin');
await page.fill('input[type="password"]', process.env.UI_PASS ?? 'Admin@123');
await Promise.all([page.waitForNavigation({ waitUntil: 'domcontentloaded' }).catch(() => {}),
                   page.click('button[type="submit"], input[type="submit"]')]);

// ---------- A. semantic button tokens, measured wherever they render ----------
console.log('=== A. SEMANTIC BUTTON TOKENS — measured on each route that renders them ===');
for (const path of ['/Inventory/Index', '/Tasks/MatchSuggestions', '/Tasks', '/Calendar']) {
    await page.goto(`${BASE}${path}`, { waitUntil: 'networkidle' });
    await page.waitForTimeout(800);
    const rows = await page.evaluate(() => {
        const out = [];
        for (const sel of ['.btn-success', '.btn-light-danger', '.btn-light-success', '.btn-danger']) {
            const el = document.querySelector(sel);
            if (!el) continue;
            const cs = getComputedStyle(el);
            let bg = cs.backgroundColor, p = el.parentElement;
            while (p && (bg === 'rgba(0, 0, 0, 0)' || bg === 'transparent')) { bg = getComputedStyle(p).backgroundColor; p = p.parentElement; }
            out.push({ sel, color: cs.color, bg, size: cs.fontSize, weight: cs.fontWeight });
        }
        return out;
    });
    if (!rows.length) { console.log(`   ${path.padEnd(28)} (none rendered)`); continue; }
    rows.forEach((r) => console.log(`   ${path.padEnd(28)} ${r.sel.padEnd(20)} ${r.color} on ${r.bg}  = ${ratio(r.color, r.bg)}:1   ${r.size}/${r.weight}`));
}

// ---------- B. Calendar failing nodes: full ancestor paint chain ----------
console.log('\n=== B. CALENDAR FAILING NODES — full ancestor background/opacity chain ===');
for (const vp of [[1440, 900, 'desktop'], [390, 844, 'mobile']]) {
    await page.setViewportSize({ width: vp[0], height: vp[1] });
    await page.goto(`${BASE}/Calendar`, { waitUntil: 'networkidle' });
    await page.waitForTimeout(2500);

    const axe = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze();
    const cc = axe.violations.find((v) => v.id === 'color-contrast');
    console.log(`\n--- viewport ${vp[2]} : ${cc ? cc.nodes.length : 0} contrast node(s) ---`);
    if (!cc) continue;

    const seen = new Set();
    for (const n of cc.nodes.slice(0, 40)) {
        const info = await page.evaluate((sel) => {
            const el = document.querySelector(sel);
            if (!el) return null;
            const chain = [];
            let cur = el, depth = 0;
            while (cur && depth < 7) {
                const cs = getComputedStyle(cur);
                chain.push({ tag: cur.tagName.toLowerCase(), cls: (cur.className?.toString() || '').slice(0, 55),
                             bg: cs.backgroundColor, op: cs.opacity, color: cs.color });
                cur = cur.parentElement; depth++;
            }
            return { cls: (el.className?.toString() || ''), text: (el.textContent || '').trim().slice(0, 30), chain };
        }, n.target.join(' ')).catch(() => null);
        if (!info) continue;
        const key = info.cls + '|' + (info.chain[1]?.cls ?? '');
        if (seen.has(key)) continue; seen.add(key);

        console.log(`\n   NODE .${info.cls}   text="${info.text}"`);
        console.log(`      axe says: ${(n.any[0]?.message ?? '').trim().slice(0, 145)}`);
        info.chain.forEach((c, i) => console.log(`      [${i}] <${c.tag} class="${c.cls}">  bg=${c.bg}  opacity=${c.op}  color=${c.color}`));
    }
}
await browser.close();
