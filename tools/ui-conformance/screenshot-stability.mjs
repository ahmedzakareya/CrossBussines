// Are the failing renders REPRODUCIBLE at all?
// Capture each affected route twice in ONE session, at the tool's own timing (screenshot straight
// after networkidle) and again after a settle delay, and compare byte-for-byte.
//   - differs at tool timing but identical after settling -> capture happens mid-animation
//   - differs both ways                                   -> genuinely non-deterministic content
import { chromium } from 'playwright';
import { PNG } from 'pngjs';
import pixelmatch from 'pixelmatch';

const BASE = process.env.UI_BASE_URL ?? 'http://localhost:5299';
const ROUTES = [
    ['authority-inventory', '/Inventory/Index'],
    ['business-event-monitor', '/BusinessEventMonitor'],
    ['workspace-notifications', '/Workspace/Notifications'],
    ['tasks-hours-report', '/Tasks/HoursReport'],
    ['tasks', '/Tasks'],                 // control: a route that does NOT fail
];

const browser = await chromium.launch();
const ctx = await browser.newContext({ ignoreHTTPSErrors: true });
const page = await ctx.newPage();
await page.setViewportSize({ width: 1440, height: 900 });
await page.goto(`${BASE}/Account/Login`, { waitUntil: 'domcontentloaded' });
await page.fill('input[name="UserName"]', process.env.UI_USER ?? 'Admin');
await page.fill('input[type="password"]', process.env.UI_PASS ?? 'Admin@123');
await Promise.all([page.waitForNavigation({ waitUntil: 'domcontentloaded' }).catch(() => {}),
                   page.click('button[type="submit"], input[type="submit"]')]);
await page.goto(`${BASE}/Account/SetLanguage?culture=en&returnUrl=%2F`, { waitUntil: 'domcontentloaded' }).catch(() => {});

const diff = (a, b) => {
    const x = PNG.sync.read(a), y = PNG.sync.read(b);
    if (x.width !== y.width || x.height !== y.height) return { size: `${x.width}x${x.height} vs ${y.width}x${y.height}` };
    const d = pixelmatch(x.data, y.data, null, x.width, x.height, { threshold: 0.1 });
    return { ratio: d / (x.width * x.height) };
};

const shot = async (path, settleMs) => {
    await page.goto(`${BASE}${path}`, { waitUntil: 'networkidle', timeout: 45000 });
    if (settleMs) await page.waitForTimeout(settleMs);
    return page.screenshot({ fullPage: true });
};

console.log('route                      tool-timing (no settle)     after 3s settle');
console.log('-------------------------  --------------------------  --------------------------');
for (const [id, path] of ROUTES) {
    const a1 = await shot(path, 0);
    const a2 = await shot(path, 0);
    const b1 = await shot(path, 3000);
    const b2 = await shot(path, 3000);
    const fmt = (r) => r.size ? `SIZE ${r.size}` : (r.ratio === 0 ? 'identical' : `${(r.ratio * 100).toFixed(2)}% differ`);
    console.log(`${id.padEnd(25)}  ${fmt(diff(a1, a2)).padEnd(26)}  ${fmt(diff(b1, b2))}`);
}
await browser.close();
