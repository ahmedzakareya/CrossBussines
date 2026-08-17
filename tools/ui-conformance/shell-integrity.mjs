// §10 shell integrity on the RENDERED governed shells:
//   - external Metronic/Keenthemes/Envato demo destinations still linked?
//   - data-kt-* / data-bs-* toggles pointing at a target that does not exist (dangling)?
//   - logout / home / brand links resolve to a real destination?
//   - any icon-only control left without an accessible name?
import { chromium } from 'playwright';

const BASE = process.env.UI_BASE_URL ?? 'http://localhost:5299';
const browser = await chromium.launch();
const ctx = await browser.newContext({ ignoreHTTPSErrors: true });
const page = await ctx.newPage();
await page.setViewportSize({ width: 1440, height: 900 });
await page.goto(`${BASE}/Account/Login`, { waitUntil: 'domcontentloaded' });
await page.fill('input[name="UserName"]', process.env.UI_USER ?? 'Admin');
await page.fill('input[type="password"]', process.env.UI_PASS ?? 'Admin@123');
await Promise.all([page.waitForNavigation({ waitUntil: 'domcontentloaded' }).catch(() => {}),
                   page.click('button[type="submit"], input[type="submit"]')]);

const ROUTES = ['/Inventory/Index', '/Tasks', '/Calendar', '/Comm', '/Workspace', '/Reports', '/BusinessEventMonitor'];
const demoHosts = /keenthemes|envato|themeforest|preview\.keenthemes/i;
const agg = { demo: new Map(), dangling: new Map(), unnamed: new Map(), nav: new Map() };

for (const p of ROUTES) {
    await page.goto(`${BASE}${p}`, { waitUntil: 'networkidle' });
    await page.waitForTimeout(700);
    const r = await page.evaluate((demoSrc) => {
        const demoRe = new RegExp(demoSrc, 'i');
        const out = { demo: [], dangling: [], unnamed: [], nav: [] };

        document.querySelectorAll('a[href]').forEach((a) => {
            if (demoRe.test(a.href)) out.demo.push({ href: a.href.slice(0, 70), text: (a.textContent || '').trim().slice(0, 30) });
        });

        // toggles whose target selector matches nothing
        document.querySelectorAll('[data-bs-target],[data-kt-drawer-toggle],[data-kt-menu-target]').forEach((el) => {
            const sel = el.getAttribute('data-bs-target') || el.getAttribute('data-kt-drawer-toggle') || el.getAttribute('data-kt-menu-target');
            if (!sel || !sel.startsWith('#')) return;
            let found = false;
            try { found = !!document.querySelector(sel); } catch { found = false; }
            if (!found) out.dangling.push({ sel, tag: el.tagName.toLowerCase(), cls: (el.className?.toString() || '').slice(0, 40) });
        });

        // icon-only controls with no accessible name
        const named = (el) => {
            const t = (el.textContent || '').replace(/\s+/g, '').length > 0;
            return t || el.getAttribute('aria-label') || el.getAttribute('title') ||
                   (el.getAttribute('aria-labelledby') && document.getElementById(el.getAttribute('aria-labelledby')));
        };
        document.querySelectorAll('button, a[href]').forEach((el) => {
            if (el.offsetParent === null) return;
            if (!named(el)) out.unnamed.push({ tag: el.tagName.toLowerCase(), cls: (el.className?.toString() || '').slice(0, 45) });
        });

        // key navigation destinations
        ['Logout', 'SignOut', 'Home', 'Portal'].forEach((k) => {
            document.querySelectorAll(`a[href*="${k}"]`).forEach((a) => out.nav.push({ k, href: new URL(a.href).pathname }));
        });
        return out;
    }, demoHosts.source);

    r.demo.forEach((d) => agg.demo.set(d.href, { ...d, route: p }));
    r.dangling.forEach((d) => agg.dangling.set(d.sel + d.cls, { ...d, route: p }));
    r.unnamed.forEach((d) => agg.unnamed.set(d.tag + d.cls, { ...d, route: p }));
    r.nav.forEach((d) => agg.nav.set(d.href, d));
}

console.log(`=== §10 SHELL INTEGRITY across ${ROUTES.length} governed routes ===\n`);
console.log(`  external demo destinations (keenthemes/envato/themeforest) : ${agg.demo.size}`);
[...agg.demo.values()].slice(0, 8).forEach((d) => console.log(`      ${d.route}  "${d.text}" -> ${d.href}`));
console.log(`\n  dangling toggles (target selector matches nothing)         : ${agg.dangling.size}`);
[...agg.dangling.values()].slice(0, 8).forEach((d) => console.log(`      ${d.route}  <${d.tag} class="${d.cls}"> -> ${d.sel}`));
console.log(`\n  icon-only controls with NO accessible name                : ${agg.unnamed.size}`);
[...agg.unnamed.values()].slice(0, 8).forEach((d) => console.log(`      ${d.route}  <${d.tag} class="${d.cls}">`));

console.log(`\n  key navigation destinations found:`);
[...agg.nav.values()].forEach((d) => console.log(`      ${d.k.padEnd(8)} ${d.href}`));

// resolve the nav destinations
console.log('\n  destination status:');
for (const d of new Set([...agg.nav.values()].map((x) => x.href))) {
    try { const res = await page.goto(`${BASE}${d}`, { waitUntil: 'domcontentloaded', timeout: 20000 }); console.log(`      ${String(res.status()).padEnd(4)} ${d}`); }
    catch { console.log(`      ERR  ${d}`); }
}
await browser.close();
