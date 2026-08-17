// Diagnostic probe for the shared visual-authority contrast decision.
//
// axe reports ONE representative selector per route x culture x viewport, so its output cannot
// tell you WHICH shared utility classes are actually failing, nor how many nodes each one owns.
// This probe reads the COMPUTED colour of every text node from a live render, resolves the real
// painted background by walking ancestors past transparent layers, and groups the failures by the
// utility class that produced them. That is what makes the token decision evidence-based rather
// than name-based.
//
// Usage: node authority-contrast-probe.mjs [route ...]
import { chromium } from 'playwright';

const BASE = process.env.UI_BASE_URL ?? 'http://localhost:5268';
const USER = process.env.UI_USER ?? 'Admin';
const PASS = process.env.UI_PASS ?? 'Admin@123';

const ROUTES = process.argv.slice(2).length ? process.argv.slice(2) : [
    '/Inventory/Index', '/Tasks', '/Calendar', '/Workspace', '/Reports', '/BusinessEventMonitor',
];

const browser = await chromium.launch();
const ctx = await browser.newContext({ ignoreHTTPSErrors: true });
const page = await ctx.newPage();
await page.setViewportSize({ width: 1440, height: 900 });

await page.goto(`${BASE}/Account/Login`, { waitUntil: 'domcontentloaded' });
await page.fill('input[name="UserName"]', USER);
await page.fill('input[type="password"]', PASS);
await Promise.all([page.waitForNavigation({ waitUntil: 'domcontentloaded' }).catch(() => {}),
                   page.click('button[type="submit"], input[type="submit"]')]);

const AUDIT = () => {
    const srgb = (c) => { c /= 255; return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4); };
    const parse = (s) => (String(s).match(/[\d.]+/g) || []).map(Number);
    const lum = ([r, g, b]) => 0.2126 * srgb(r) + 0.7152 * srgb(g) + 0.0722 * srgb(b);
    const ratio = (a, b) => { const [x, y] = [lum(a), lum(b)].sort((m, n) => n - m); return (x + 0.05) / (y + 0.05); };
    const hex = (v) => '#' + v.slice(0, 3).map((n) => Math.round(n).toString(16).padStart(2, '0')).join('').toUpperCase();
    const opaque = (s) => { const p = parse(s); return p.length >= 4 && p[3] === 0 ? null : p; };

    const bgOf = (el) => {
        let n = el;
        while (n && n !== document.documentElement) {
            const c = opaque(getComputedStyle(n).backgroundColor);
            if (c) return c;
            n = n.parentElement;
        }
        return [255, 255, 255];
    };

    const out = {};
    for (const el of document.querySelectorAll('body *')) {
        // only elements that themselves render visible text
        const own = [...el.childNodes].filter((n) => n.nodeType === 3 && n.textContent.trim()).map((n) => n.textContent.trim()).join(' ');
        if (!own) continue;
        const cs = getComputedStyle(el);
        if (cs.visibility === 'hidden' || cs.display === 'none' || Number(cs.opacity) === 0) continue;
        const r = el.getBoundingClientRect();
        if (r.width === 0 || r.height === 0) continue;

        const fg = parse(cs.color);
        const bg = bgOf(el);
        const cr = ratio(fg, bg);
        const px = parseFloat(cs.fontSize);
        const bold = Number(cs.fontWeight) >= 700;
        const large = px >= 24 || (px >= 18.66 && bold);          // WCAG large-text definition
        const need = large ? 3.0 : 4.5;
        if (cr >= need) continue;

        // attribute the failure to the utility class that set the colour
        const utils = [...el.classList].filter((c) => /^(text-|badge|menu-|form-|fs-)/.test(c));
        const key = `${utils.join('.') || '(no text utility)'} | ${hex(fg)} on ${hex(bg)}`;
        out[key] = out[key] || { count: 0, ratio: +cr.toFixed(2), need, large, sample: own.slice(0, 40), tag: el.tagName.toLowerCase() };
        out[key].count++;
    }
    return out;
};

const totals = {};
for (const culture of ['en', 'ar']) {
    await page.goto(`${BASE}/Account/SetLanguage?culture=${culture}&returnUrl=%2F`, { waitUntil: 'domcontentloaded' }).catch(() => {});
    for (const route of ROUTES) {
        await page.goto(`${BASE}${route}`, { waitUntil: 'networkidle' }).catch(() => {});
        await page.waitForTimeout(800);
        const res = await page.evaluate(AUDIT);
        console.log(`\n=== ${culture} ${route} ===`);
        const rows = Object.entries(res).sort((a, b) => b[1].count - a[1].count);
        if (!rows.length) { console.log('   (no contrast failures)'); continue; }
        for (const [k, v] of rows) {
            console.log(`   ${String(v.count).padStart(4)}x  ratio=${v.ratio.toFixed(2)} (need ${v.need})  ${k}   e.g. <${v.tag}> "${v.sample}"`);
            totals[k] = (totals[k] || 0) + v.count;
        }
    }
}

console.log('\n\n=========== TOTAL FAILING NODES BY UTILITY CLASS / COLOUR PAIR ===========');
for (const [k, n] of Object.entries(totals).sort((a, b) => b[1] - a[1])) console.log(`   ${String(n).padStart(5)}  ${k}`);

await browser.close();
