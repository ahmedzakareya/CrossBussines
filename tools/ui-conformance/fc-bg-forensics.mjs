// Where does axe's "#dfffea" background come from for .cbev-company .fc-event-title?
// No ancestor paints it, so either something OVERLAPS the title, or axe is resolving the wrong
// surface. This locates every element painting that colour and tests overlap geometrically.
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
    const norm = (c) => { const m = (c.match(/\d+/g) || []).slice(0, 3).map(Number); return m.length === 3 ? '#' + m.map((n) => n.toString(16).padStart(2, '0')).join('') : c; };

    // 1. every element painting #dfffea
    const painters = [];
    document.querySelectorAll('*').forEach((el) => {
        const bg = getComputedStyle(el).backgroundColor;
        if (norm(bg) === '#dfffea') {
            const r = el.getBoundingClientRect();
            painters.push({ tag: el.tagName.toLowerCase(), cls: (el.className?.toString() || '').slice(0, 70),
                            rect: `${Math.round(r.x)},${Math.round(r.y)} ${Math.round(r.width)}x${Math.round(r.height)}` });
        }
    });

    // 2. the failing title, its own box, and what elementFromPoint says is on top at its centre
    const title = [...document.querySelectorAll('.cbev-company .fc-event-title')][0]
               || [...document.querySelectorAll('.fc-event-title')][0];
    let probe = null;
    if (title) {
        const r = title.getBoundingClientRect();
        const cx = r.x + r.width / 2, cy = r.y + r.height / 2;
        const stack = document.elementsFromPoint(cx, cy).slice(0, 6).map((e) => ({
            tag: e.tagName.toLowerCase(), cls: (e.className?.toString() || '').slice(0, 60),
            bg: norm(getComputedStyle(e).backgroundColor),
        }));
        probe = {
            cls: title.className, text: (title.textContent || '').trim().slice(0, 40),
            rect: `${Math.round(r.x)},${Math.round(r.y)} ${Math.round(r.width)}x${Math.round(r.height)}`,
            visible: r.width > 0 && r.height > 0,
            parentBg: norm(getComputedStyle(title.parentElement).backgroundColor),
            colour: norm(getComputedStyle(title).color),
            stack,
        };
    }

    // 3. does our .cbev-company rule actually apply to a DOT event?
    const dot = document.querySelector('.cbev-company.fc-daygrid-dot-event');
    const dotInfo = dot ? { bg: norm(getComputedStyle(dot).backgroundColor), cls: dot.className.slice(0, 80) } : null;

    return { painters: painters.slice(0, 8), probe, dotInfo,
             totalEvents: document.querySelectorAll('.fc-event').length,
             dotEvents: document.querySelectorAll('.fc-daygrid-dot-event').length };
});

console.log('=== elements painting #dfffea ===');
if (!out.painters.length) console.log('   NONE — nothing on the page paints that colour');
out.painters.forEach((p) => console.log(`   <${p.tag} class="${p.cls}">  box ${p.rect}`));

console.log('\n=== the failing .fc-event-title ===');
if (out.probe) {
    console.log(`   class   : ${out.probe.cls}`);
    console.log(`   text    : ${out.probe.text}`);
    console.log(`   box     : ${out.probe.rect}   visible=${out.probe.visible}`);
    console.log(`   colour  : ${out.probe.colour}   parent background: ${out.probe.parentBg}`);
    console.log('   hit-test stack at its centre (topmost first):');
    out.probe.stack.forEach((s, i) => console.log(`      [${i}] <${s.tag} class="${s.cls}"> bg=${s.bg}`));
}
console.log(`\n=== event rendering mode ===`);
console.log(`   .fc-event total: ${out.totalEvents}   .fc-daygrid-dot-event: ${out.dotEvents}`);
console.log(`   .cbev-company dot event computed background: ${out.dotInfo ? out.dotInfo.bg : '(no dot event carries .cbev-company)'}`);
await browser.close();
