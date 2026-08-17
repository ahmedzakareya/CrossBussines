// Two things a "0 findings" result must survive before it can be believed:
//   1. axe really ran and really can still report — shown by listing ALL impacts, including the
//      moderate/minor ones the gate deliberately does not fail on.
//   2. The Announcements banner is actually PRESENT and measured, not merely absent from the run.
import { chromium } from 'playwright';
import { AxeBuilder } from '@axe-core/playwright';

const BASE = process.env.UI_BASE_URL ?? 'http://localhost:5299';
const srgb = (c) => { c /= 255; return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4); };
const lum = ([r, g, b]) => 0.2126 * srgb(r) + 0.7152 * srgb(g) + 0.0722 * srgb(b);
const px = (s) => (String(s).match(/\d+(\.\d+)?/g) || []).slice(0, 3).map(Number);
const ratio = (a, b) => { const [x, y] = [lum(px(a)), lum(px(b))].sort((m, n) => n - m); return ((x + 0.05) / (y + 0.05)); };
const hex = (s) => '#' + px(s).map((n) => Math.round(n).toString(16).padStart(2, '0')).join('');

const browser = await chromium.launch();
const ctx = await browser.newContext({ ignoreHTTPSErrors: true });
const page = await ctx.newPage();
await page.setViewportSize({ width: 1440, height: 900 });
await page.goto(`${BASE}/Account/Login`, { waitUntil: 'domcontentloaded' });
await page.fill('input[name="UserName"]', process.env.UI_USER ?? 'Admin');
await page.fill('input[type="password"]', process.env.UI_PASS ?? 'Admin@123');
await Promise.all([page.waitForNavigation({ waitUntil: 'domcontentloaded' }).catch(() => {}),
                   page.click('button[type="submit"], input[type="submit"]')]);

console.log('=== 1. PROOF axe IS LIVE: all impacts on three governed routes ===');
for (const p of ['/Inventory/Index', '/Tasks/MatchSuggestions', '/Comm']) {
    await page.goto(`${BASE}${p}`, { waitUntil: 'networkidle' });
    await page.waitForTimeout(700);
    const axe = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze();
    const byImpact = axe.violations.reduce((a, v) => { a[v.impact] = (a[v.impact] || 0) + 1; return a; }, {});
    console.log(`   ${p.padEnd(26)} violations=${axe.violations.length} ${JSON.stringify(byImpact)}  passes=${axe.passes.length}`);
    axe.violations.forEach((v) => console.log(`        - [${v.impact}] ${v.id} x${v.nodes.length}`));
}

console.log('\n=== 2. ANNOUNCEMENTS BANNER: does it RENDER, and what does it measure? ===');
for (const culture of ['en', 'ar']) {
    await page.goto(`${BASE}/Account/SetLanguage?culture=${culture}&returnUrl=%2F`, { waitUntil: 'domcontentloaded' }).catch(() => {});
    for (const p of ['/Inventory/Index', '/Comm', '/Tasks']) {
        await page.goto(`${BASE}${p}`, { waitUntil: 'networkidle' });
        await page.waitForTimeout(900);
        const info = await page.evaluate(() => {
            const alerts = [...document.querySelectorAll('.alert, [class*="alert-"]')]
                .filter((a) => a.offsetParent !== null);
            return alerts.slice(0, 4).map((a) => {
                const cs = getComputedStyle(a);
                let bg = cs.backgroundColor, n = a;
                while (n && (bg === 'rgba(0, 0, 0, 0)' || bg === 'transparent')) { bg = getComputedStyle(n).backgroundColor; n = n.parentElement; }
                return { cls: a.className.toString().slice(0, 60), color: cs.color, bg,
                         text: (a.textContent || '').trim().replace(/\s+/g, ' ').slice(0, 40) };
            });
        });
        if (!info.length) { console.log(`   ${culture} ${p.padEnd(22)} no visible alert/banner rendered`); continue; }
        info.forEach((a) => console.log(`   ${culture} ${p.padEnd(22)} .${a.cls}\n        ${hex(a.color)} on ${hex(a.bg)} = ${ratio(a.color, a.bg).toFixed(2)}:1  ${ratio(a.color, a.bg) >= 4.5 ? 'PASS' : 'FAIL'}   "${a.text}"`));
    }
}

console.log('\n=== 3. Are there ANY announcements in the dataset to show? ===');
const api = await page.evaluate(async (b) => {
    try { const r = await fetch(`${b}/Announcements/Active`, { credentials: 'same-origin' }); return { ok: r.ok, status: r.status, body: (await r.text()).slice(0, 160) }; }
    catch (e) { return { err: e.message }; }
}, BASE);
console.log('   /Announcements/Active ->', JSON.stringify(api));
await browser.close();
