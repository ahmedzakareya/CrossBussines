// Certification probe for the .cbev-company colour decision (hardcoded #1b84ff -> var(--bs-info)).
// Reads the COMPUTED colours from a live render and computes WCAG contrast, so the decision is
// certified against what a user actually sees rather than against the token's name.
import { chromium } from 'playwright';

const BASE = process.env.UI_BASE_URL ?? 'http://localhost:5299';
const USER = process.env.UI_USER ?? 'Admin';
const PASS = process.env.UI_PASS ?? 'Admin@123';

const srgb = (c) => { c /= 255; return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4); };
const lum = ([r, g, b]) => 0.2126 * srgb(r) + 0.7152 * srgb(g) + 0.0722 * srgb(b);
const parse = (s) => (s.match(/\d+(\.\d+)?/g) || []).slice(0, 3).map(Number);
const contrast = (a, b) => { const [x, y] = [lum(parse(a)), lum(parse(b))].sort((m, n) => n - m); return (x + 0.05) / (y + 0.05); };
const hex = (s) => '#' + parse(s).map((n) => n.toString(16).padStart(2, '0')).join('');

const browser = await chromium.launch();
const ctx = await browser.newContext({ ignoreHTTPSErrors: true });
const page = await ctx.newPage();
await page.setViewportSize({ width: 1440, height: 900 });

await page.goto(`${BASE}/Account/Login`, { waitUntil: 'domcontentloaded' });
await page.fill('input[name="UserName"]', USER);
await page.fill('input[type="password"]', PASS);
await Promise.all([page.waitForNavigation({ waitUntil: 'domcontentloaded' }).catch(() => {}),
                   page.click('button[type="submit"], input[type="submit"]')]);

for (const culture of ['en', 'ar']) {
    await page.goto(`${BASE}/Account/SetLanguage?culture=${culture}&returnUrl=%2F`, { waitUntil: 'domcontentloaded' }).catch(() => {});
    await page.goto(`${BASE}/Calendar`, { waitUntil: 'networkidle' });
    await page.waitForTimeout(2500);   // FullCalendar fetches /Calendar/Events then paints

    const probe = await page.evaluate(() => {
        const read = (sel) => {
            const el = document.querySelector(sel);
            if (!el) return null;
            const cs = getComputedStyle(el);
            return { bg: cs.backgroundColor, fg: cs.color, border: cs.borderColor, text: (el.textContent || '').trim().slice(0, 40) };
        };
        return {
            dir: document.documentElement.getAttribute('dir'),
            personal: read('.cbev-personal'),
            company: read('.cbev-company'),
            counts: {
                personal: document.querySelectorAll('.cbev-personal').length,
                company: document.querySelectorAll('.cbev-company').length,
            },
        };
    });

    console.log(`\n=== culture=${culture}  dir=${probe.dir} ===`);
    console.log(`   rendered events: personal=${probe.counts.personal}  company=${probe.counts.company}`);
    for (const kind of ['personal', 'company']) {
        const p = probe[kind];
        if (!p) { console.log(`   ${kind}: NOT RENDERED (no element in the month view)`); continue; }
        console.log(`   ${kind.padEnd(8)} bg=${hex(p.bg)} (${p.bg})  fg=${hex(p.fg)}  text-contrast=${contrast(p.bg, p.fg).toFixed(2)}:1`);
    }
    if (probe.personal && probe.company) {
        console.log(`   personal-vs-company background separation = ${contrast(probe.personal.bg, probe.company.bg).toFixed(2)}:1`);
    }
    await page.screenshot({ path: `artifacts/cbev-${culture}.png`, fullPage: false });
}

await browser.close();
