// Why is a rule in crossbuy-brand.css not winning? Dumps, from the LIVE document,
// every stylesheet rule that sets `color` and matches a given element, in cascade order.
import { chromium } from 'playwright';
const BASE = process.env.UI_BASE_URL ?? 'http://localhost:5268';
const browser = await chromium.launch();
const page = await (await browser.newContext({ ignoreHTTPSErrors: true })).newPage();
await page.setViewportSize({ width: 1440, height: 900 });
await page.goto(`${BASE}/Account/Login`, { waitUntil: 'domcontentloaded' });
await page.fill('input[name="UserName"]', process.env.UI_USER ?? 'Admin');
await page.fill('input[type="password"]', process.env.UI_PASS ?? 'Admin@123');
await Promise.all([page.waitForNavigation({ waitUntil: 'domcontentloaded' }).catch(() => {}),
                   page.click('button[type="submit"], input[type="submit"]')]);
const ROUTE = process.env.DIAG_ROUTE ?? '/BusinessEventMonitor';
const SEL = process.env.DIAG_SELECTOR ?? '.form-check-label';
await page.goto(`${BASE}${ROUTE}`, { waitUntil: 'networkidle' });
await page.waitForTimeout(1500);

const out = await page.evaluate((SEL) => {
    const res = { brandLoaded: false, brandRules: [], matches: [], target: null };
    for (const sh of document.styleSheets) {
        const href = sh.href || '(inline)';
        if (/crossbuy-brand/.test(href)) {
            res.brandLoaded = true;
            try {
                for (const r of sh.cssRules) {
                    if (r.cssText && /color/.test(r.cssText)) res.brandRules.push(r.cssText.slice(0, 160));
                }
            } catch (e) { res.brandRules.push('CANNOT READ: ' + e.message); }
        }
    }
    const el = document.querySelector(SEL);
    if (el) {
        res.target = { tag: el.tagName, cls: el.className, computed: getComputedStyle(el).color };
        for (const sh of document.styleSheets) {
            let rules; try { rules = sh.cssRules; } catch (e) { continue; }
            for (const r of rules) {
                if (!r.selectorText || !r.style || !r.style.color) continue;
                try { if (el.matches(r.selectorText)) res.matches.push({ href: (sh.href || '').split('/').pop(), sel: r.selectorText.slice(0, 120), color: r.style.color, prio: r.style.getPropertyPriority('color') }); } catch (e) {}
            }
        }
    }
    return res;
}, SEL);
console.log('brand stylesheet loaded:', out.brandLoaded);
console.log('\ncolor-bearing rules parsed FROM crossbuy-brand.css:');
for (const r of out.brandRules) console.log('   ', r);
console.log('\ntarget element:', JSON.stringify(out.target));
console.log('\nmatching color rules in cascade order:');
for (const m of out.matches) console.log(`   [${m.href}] ${m.sel}  ->  ${m.color} ${m.prio}`);
await browser.close();
