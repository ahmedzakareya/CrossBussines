// Why is .fc-event-title laid out OUTSIDE its own dot-event pill at 390px RTL?
// Dump the computed box model + flex properties of the title and of every sibling in the pill.
import { chromium } from 'playwright';

const BASE = process.env.UI_BASE_URL ?? 'http://localhost:5299';
const browser = await chromium.launch();
const ctx = await browser.newContext({ ignoreHTTPSErrors: true });
const page = await ctx.newPage();
await page.setViewportSize({ width: 390, height: 844 });
await page.goto(`${BASE}/Account/Login`, { waitUntil: 'domcontentloaded' });
await page.fill('input[name="UserName"]', process.env.UI_USER ?? 'Admin');
await page.fill('input[type="password"]', process.env.UI_PASS ?? 'Admin@123');
await Promise.all([page.waitForNavigation({ waitUntil: 'domcontentloaded' }).catch(() => {}),
                   page.click('button[type="submit"], input[type="submit"]')]);
await page.goto(`${BASE}/Calendar`, { waitUntil: 'networkidle' });
await page.waitForTimeout(2500);

const out = await page.evaluate(() => {
    const ev = document.querySelector('.fc-daygrid-dot-event');
    if (!ev) return { err: 'no dot event' };
    const box = (e) => { const r = e.getBoundingClientRect(); return `${Math.round(r.x)},${Math.round(r.y)} ${Math.round(r.width)}x${Math.round(r.height)}`; };
    const evCs = getComputedStyle(ev);
    const kids = [...ev.children].map((k) => {
        const cs = getComputedStyle(k);
        return { cls: k.className?.toString().slice(0, 40), box: box(k),
                 display: cs.display, flex: cs.flex, minWidth: cs.minWidth,
                 overflow: cs.overflow, textOverflow: cs.textOverflow, whiteSpace: cs.whiteSpace,
                 position: cs.position, direction: cs.direction, text: (k.textContent || '').trim().slice(0, 20) };
    });
    return {
        evCls: ev.className.toString().slice(0, 80),
        evBox: box(ev),
        evDisplay: evCs.display, evOverflow: evCs.overflow, evDirection: evCs.direction,
        evPosition: evCs.position, evWhiteSpace: evCs.whiteSpace,
        htmlDir: document.documentElement.getAttribute('dir'),
        kids,
        // is my rule present in any stylesheet and does it match?
        ruleMatches: (() => {
            const t = ev.querySelector('.fc-event-title');
            if (!t) return 'no title child';
            try { return t.matches('.fc-daygrid-dot-event .fc-event-title') ? 'SELECTOR MATCHES' : 'selector does NOT match'; }
            catch { return 'match() threw'; }
        })(),
    };
});
console.log(JSON.stringify(out, null, 2));
await browser.close();
