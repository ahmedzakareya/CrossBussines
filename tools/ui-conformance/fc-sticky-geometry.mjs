// Is the .fc-event-title contrast failure REAL or an axe background-resolution artifact?
//
// Test: for every node axe flags, compare the title's painted box with its own event's box and with
// the #dfffea "today" cell. If the title sits entirely inside its coloured event, the white text is
// genuinely on that colour and axe sampled the wrong surface.
import { chromium } from 'playwright';
import { AxeBuilder } from '@axe-core/playwright';

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

const axe = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze();
const cc = axe.violations.find((v) => v.id === 'color-contrast');
console.log(`axe color-contrast nodes at 390px: ${cc ? cc.nodes.length : 0}\n`);

for (const n of (cc?.nodes ?? [])) {
    const sel = n.target.join(' ');
    const info = await page.evaluate((s) => {
        const el = document.querySelector(s);
        if (!el) return null;
        const norm = (c) => { const m = (c.match(/\d+/g) || []).slice(0, 3).map(Number); return m.length === 3 ? '#' + m.map((x) => x.toString(16).padStart(2, '0')).join('') : c; };
        const box = (e) => { const r = e.getBoundingClientRect(); return { x: Math.round(r.x), y: Math.round(r.y), w: Math.round(r.width), h: Math.round(r.height) }; };
        const ev = el.closest('.fc-event');
        const cell = el.closest('td');
        const b = box(el);
        const inside = ev ? (() => { const e = box(ev); return b.x >= e.x - 1 && b.y >= e.y - 1 && b.x + b.w <= e.x + e.w + 1 && b.y + b.h <= e.y + e.h + 1; })() : null;
        return {
            cls: el.className?.toString().slice(0, 60),
            text: (el.textContent || '').trim().slice(0, 26),
            position: getComputedStyle(el).position,
            colour: norm(getComputedStyle(el).color),
            titleBox: b,
            eventCls: ev ? ev.className.toString().slice(0, 70) : null,
            eventBox: ev ? box(ev) : null,
            eventBg: ev ? norm(getComputedStyle(ev).backgroundColor) : null,
            cellCls: cell ? cell.className.toString().slice(0, 55) : null,
            cellBg: cell ? norm(getComputedStyle(cell).backgroundColor) : null,
            titleInsideEvent: inside,
        };
    }, sel).catch(() => null);
    if (!info) continue;

    console.log(`NODE .${info.cls}  "${info.text}"`);
    console.log(`   axe        : ${(n.any[0]?.message ?? '').trim().slice(0, 120)}`);
    console.log(`   position   : ${info.position}   colour: ${info.colour}`);
    console.log(`   title box  : ${JSON.stringify(info.titleBox)}`);
    if (info.eventBox) {
        console.log(`   event      : ${info.eventCls}`);
        console.log(`   event box  : ${JSON.stringify(info.eventBox)}   bg=${info.eventBg}`);
        console.log(`   TITLE FULLY INSIDE ITS COLOURED EVENT: ${info.titleInsideEvent}`);
    }
    console.log(`   day cell   : ${info.cellCls}  bg=${info.cellBg}`);
    console.log('');
}
await browser.close();
