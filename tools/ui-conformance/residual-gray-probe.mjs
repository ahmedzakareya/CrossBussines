// Identifies the EXACT elements still rendering the failing Metronic grays after the
// --bs-text-* token fix, so the remaining override list is derived from evidence rather
// than from grepping 63 candidate rules and guessing which ones actually paint text.
import { chromium } from 'playwright';

const BASE = process.env.UI_BASE_URL ?? 'http://localhost:5268';
const USER = process.env.UI_USER ?? 'Admin';
const PASS = process.env.UI_PASS ?? 'Admin@123';
const ROUTES = ['/Inventory/Index', '/Tasks', '/Calendar', '/Workspace', '/Reports', '/BusinessEventMonitor'];

const browser = await chromium.launch();
const page = await (await browser.newContext({ ignoreHTTPSErrors: true })).newPage();
await page.setViewportSize({ width: 1440, height: 900 });
await page.goto(`${BASE}/Account/Login`, { waitUntil: 'domcontentloaded' });
await page.fill('input[name="UserName"]', USER);
await page.fill('input[type="password"]', PASS);
await Promise.all([page.waitForNavigation({ waitUntil: 'domcontentloaded' }).catch(() => {}),
                   page.click('button[type="submit"], input[type="submit"]')]);

const totals = {};
for (const route of ROUTES) {
    await page.goto(`${BASE}${route}`, { waitUntil: 'networkidle' }).catch(() => {});
    await page.waitForTimeout(600);
    const rows = await page.evaluate(() => {
        const BAD = { 'rgb(153, 161, 183)': '#99A1B7', 'rgb(120, 130, 157)': '#78829D' };
        const out = [];
        for (const el of document.querySelectorAll('body *')) {
            const own = [...el.childNodes].filter((n) => n.nodeType === 3 && n.textContent.trim()).length;
            if (!own) continue;
            const cs = getComputedStyle(el);
            const hit = BAD[cs.color];
            if (!hit) continue;
            const r = el.getBoundingClientRect();
            if (!r.width || !r.height) continue;
            // nearest ancestor chain that explains the inheritance
            const chain = [];
            for (let n = el; n && n !== document.body && chain.length < 4; n = n.parentElement) {
                chain.push(n.tagName.toLowerCase() + (n.className && typeof n.className === 'string' ? '.' + n.className.trim().split(/\s+/).slice(0, 3).join('.') : ''));
            }
            out.push({ hit, tag: el.tagName.toLowerCase(), cls: (typeof el.className === 'string' ? el.className : ''), chain: chain.join(' < ') });
        }
        return out;
    });
    for (const r of rows) {
        const k = `${r.hit}  <${r.tag} class="${r.cls}">\n            in: ${r.chain}`;
        totals[k] = (totals[k] || 0) + 1;
    }
}
console.log('=== ELEMENTS STILL RENDERING FAILING GRAYS ===');
const ent = Object.entries(totals).sort((a, b) => b[1] - a[1]);
if (!ent.length) console.log('   NONE');
for (const [k, n] of ent) console.log(`   ${String(n).padStart(4)}x  ${k}`);
await browser.close();
