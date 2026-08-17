// What ACTUALLY changes on the unstable routes as wall-clock time passes?
// Captures each route twice with a gap, diffs the visible TEXT node-by-node, and reports the exact
// strings that changed. Names the culprit instead of inferring it.
import { chromium } from 'playwright';

const BASE = process.env.UI_BASE_URL ?? 'http://localhost:5299';
const GAP = Number(process.env.DRIFT_GAP_MS ?? 65000);

const ROUTES = [
    ['authority-inventory',    '/Inventory/Index',        1440],
    ['business-event-monitor', '/BusinessEventMonitor',    1440],
    ['workspace-notifications','/Workspace/Notifications', 1440],
    ['tasks-hours-report',     '/Tasks/HoursReport',        390],
    ['tasks-all',              '/Tasks/All',                992],
    ['tasks',                  '/Tasks',                   1440],   // control: was stable
];

// Same fixed instant the official runner uses. With UI_CONFORMANCE=1 on a Development host this
// freezes the reference point for relative-time labels; without it the header is inert, so this
// probe reports the REAL drift either way.
const CERT_NOW = process.env.UI_CONFORMANCE_NOW ?? '2026-03-14T09:26:53.0000000';
const FREEZE = process.env.DRIFT_FREEZE !== '0';
const browser = await chromium.launch();
const ctx = await browser.newContext({
    ignoreHTTPSErrors: true,
    ...(FREEZE ? { extraHTTPHeaders: { 'X-UI-Conformance-Now': CERT_NOW }, reducedMotion: 'reduce' } : {}),
});
console.log(`clock: ${FREEZE ? 'FROZEN at ' + CERT_NOW : 'REAL (control run)'}`);
const page = await ctx.newPage();
await page.goto(`${BASE}/Account/Login`, { waitUntil: 'domcontentloaded' });
await page.fill('input[name="UserName"]', process.env.UI_USER ?? 'Admin');
await page.fill('input[type="password"]', process.env.UI_PASS ?? 'Admin@123');
await Promise.all([page.waitForNavigation({ waitUntil: 'domcontentloaded' }).catch(() => {}),
                   page.click('button[type="submit"], input[type="submit"]')]);

const snap = async (path, w) => {
    await page.setViewportSize({ width: w, height: 900 });
    await page.goto(`${BASE}${path}`, { waitUntil: 'networkidle', timeout: 45000 });
    await page.waitForTimeout(2500);
    return page.evaluate(() => {
        const texts = [];
        const walk = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
        let n;
        while ((n = walk.nextNode())) {
            const t = (n.nodeValue || '').trim();
            if (t) texts.push(t);
        }
        return { texts, height: document.documentElement.scrollHeight };
    });
};

const first = new Map();
for (const [id, path, w] of ROUTES) first.set(id, await snap(path, w));
console.log(`captured pass 1; waiting ${GAP / 1000}s ...\n`);
await page.waitForTimeout(GAP);

for (const [id, path, w] of ROUTES) {
    const a = first.get(id);
    const b = await snap(path, w);
    const changed = [];
    const max = Math.max(a.texts.length, b.texts.length);
    for (let i = 0; i < max; i++) {
        if (a.texts[i] !== b.texts[i]) changed.push({ i, before: a.texts[i], after: b.texts[i] });
    }
    console.log(`=== ${id} (${path} @${w}px) ===`);
    console.log(`   text nodes: ${a.texts.length} -> ${b.texts.length}   page height: ${a.height} -> ${b.height}`);
    if (!changed.length) { console.log('   NO TEXT CHANGED'); }
    else {
        console.log(`   ${changed.length} text node(s) changed:`);
        changed.slice(0, 8).forEach((c) => console.log(`      [${c.i}] "${String(c.before).slice(0, 45)}"  ->  "${String(c.after).slice(0, 45)}"`));
    }
    console.log('');
}
await browser.close();
