// §6 + §7 certification gates.
//  §6  For each NAMED rule prove it was actually EVALUATED, not merely absent. axe reports every
//      rule as violations / passes / incomplete / inapplicable; "inapplicable" means no matching
//      node existed, which is a different claim from "checked and clean".
//  §7  Measure the computed contrast of every family repaired during closure, on a real render.
import { chromium } from 'playwright';
import { AxeBuilder } from '@axe-core/playwright';

const BASE = process.env.UI_BASE_URL ?? 'http://localhost:5299';
const NAMED = ['button-name', 'link-name', 'label', 'color-contrast', 'aria-allowed-attr',
               'aria-valid-attr', 'scrollable-region-focusable', 'list', 'link-in-text-block'];

const srgb = (c) => { c /= 255; return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4); };
const lum = ([r, g, b]) => 0.2126 * srgb(r) + 0.7152 * srgb(g) + 0.0722 * srgb(b);
const rgb = (s) => (String(s).match(/\d+(\.\d+)?/g) || []).slice(0, 3).map(Number);
const ratio = (a, b) => { const [x, y] = [lum(rgb(a)), lum(rgb(b))].sort((m, n) => n - m); return (x + 0.05) / (y + 0.05); };
const hx = (s) => '#' + rgb(s).map((n) => Math.round(n).toString(16).padStart(2, '0')).join('');

const browser = await chromium.launch();
const ctx = await browser.newContext({ ignoreHTTPSErrors: true });
const page = await ctx.newPage();
await page.setViewportSize({ width: 1440, height: 900 });
await page.goto(`${BASE}/Account/Login`, { waitUntil: 'domcontentloaded' });
await page.fill('input[name="UserName"]', process.env.UI_USER ?? 'Admin');
await page.fill('input[type="password"]', process.env.UI_PASS ?? 'Admin@123');
await Promise.all([page.waitForNavigation({ waitUntil: 'domcontentloaded' }).catch(() => {}),
                   page.click('button[type="submit"], input[type="submit"]')]);

// ---------- §6 ----------
const ROUTES = ['/Inventory/Index', '/Workspace', '/Reports', '/BusinessEventMonitor',
                '/Tasks', '/Tasks/Board', '/Tasks/MatchSuggestions', '/Calendar',
                '/Calendar/Timeline', '/Calendar/ResourceView', '/Comm'];
const status = {};
for (const r of NAMED) status[r] = { violations: 0, passedOn: 0, incomplete: 0, inapplicableOn: 0, nodes: 0 };

for (const p of ROUTES) {
    await page.goto(`${BASE}${p}`, { waitUntil: 'networkidle' });
    await page.waitForTimeout(800);
    const a = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze();
    for (const r of NAMED) {
        const v = a.violations.find((x) => x.id === r);
        if (v) { status[r].violations++; status[r].nodes += v.nodes.length; }
        if (a.passes.some((x) => x.id === r)) status[r].passedOn++;
        if (a.incomplete.some((x) => x.id === r)) status[r].incomplete++;
        if (a.inapplicable.some((x) => x.id === r)) status[r].inapplicableOn++;
    }
}
console.log(`=== §6 NAMED RULES across ${ROUTES.length} governed routes ===\n`);
console.log('  rule                          violations  nodes  passedOn  incomplete  inapplicable');
for (const r of NAMED) {
    const s = status[r];
    console.log(`  ${r.padEnd(30)} ${String(s.violations).padStart(9)} ${String(s.nodes).padStart(6)} ${String(s.passedOn).padStart(9)} ${String(s.incomplete).padStart(11)} ${String(s.inapplicableOn).padStart(13)}`);
}

// ---------- §7 ----------
console.log('\n=== §7 VISUAL AUTHORITY FAMILIES (computed on a real render) ===\n');
await page.goto(`${BASE}/Inventory/Index`, { waitUntil: 'networkidle' });
const fam = await page.evaluate(() => {
    const host = document.createElement('div');
    host.style.cssText = 'position:absolute;left:-9999px;top:0;background:#fff';
    document.body.appendChild(host);
    const out = [];
    const mk = (tag, cls, group) => {
        const e = document.createElement(tag); e.className = cls; e.textContent = 'Sample';
        host.appendChild(e);
        const cs = getComputedStyle(e);
        let bg = cs.backgroundColor, p = e.parentElement;
        while (p && (bg === 'rgba(0, 0, 0, 0)' || bg === 'transparent')) { bg = getComputedStyle(p).backgroundColor; p = p.parentElement; }
        out.push({ group, cls, color: cs.color, bg });
    };
    ['text-muted', 'text-gray-500', 'text-gray-600', 'text-success', 'text-warning', 'text-danger'].forEach((c) => mk('span', c, 'TEXT'));
    ['badge badge-light-success', 'badge badge-light-warning', 'badge badge-light-danger'].forEach((c) => mk('span', c, 'BADGE'));
    ['btn btn-success', 'btn btn-light-success', 'btn btn-warning', 'btn btn-light-warning', 'btn btn-danger', 'btn btn-light-danger'].forEach((c) => mk('button', c, 'BUTTON'));
    ['alert alert-primary', 'alert alert-success', 'alert alert-warning', 'alert alert-danger', 'alert alert-info'].forEach((c) => mk('div', c, 'ALERT'));
    host.remove();
    return out;
});
let group = '';
for (const f of fam) {
    if (f.group !== group) { group = f.group; console.log(`  -- ${group} --`); }
    const cr = ratio(f.color, f.bg);
    console.log(`     ${f.cls.padEnd(28)} ${hx(f.color)} on ${hx(f.bg)}  ${cr.toFixed(2).padStart(6)}:1  ${cr >= 4.5 ? 'PASS' : 'FAIL'}`);
}

// ---------- §7 FullCalendar ----------
console.log('\n  -- FULLCALENDAR --');
for (const [w, h, lbl] of [[1440, 900, 'desktop'], [390, 844, 'mobile']]) {
    await page.setViewportSize({ width: w, height: h });
    await page.goto(`${BASE}/Calendar`, { waitUntil: 'networkidle' });
    await page.waitForTimeout(2500);
    const fc = await page.evaluate(() => {
        const norm = (c) => { const m = (c.match(/\d+/g) || []).slice(0, 3).map(Number); return m.length === 3 ? '#' + m.map((n) => n.toString(16).padStart(2, '0')).join('') : c; };
        const other = document.querySelector('.fc-day-other .fc-daygrid-day-number');
        const top = other?.closest('.fc-daygrid-day-top');
        const per = document.querySelector('.cbev-personal');
        const com = document.querySelector('.cbev-company');
        const more = document.querySelector('.fc-daygrid-more-link');
        // do any event titles escape their own painted event?
        let escaped = 0, checked = 0;
        document.querySelectorAll('.fc-event-title').forEach((t) => {
            const ev = t.closest('.fc-event'); if (!ev) return;
            const a = t.getBoundingClientRect(), b = ev.getBoundingClientRect();
            if (a.width === 0) return;
            checked++;
            if (a.left < b.left - 1 || a.right > b.right + 1) escaped++;
        });
        return {
            otherColor: other ? norm(getComputedStyle(other).color) : null,
            otherTopOpacity: top ? getComputedStyle(top).opacity : null,
            personal: per ? { c: norm(getComputedStyle(per).color), b: norm(getComputedStyle(per).backgroundColor) } : null,
            company: com ? { c: norm(getComputedStyle(com).color), b: norm(getComputedStyle(com).backgroundColor) } : null,
            moreRole: more ? more.getAttribute('role') : null,
            moreAriaControls: more ? more.getAttribute('aria-controls') : '(absent)',
            escaped, checked,
        };
    });
    console.log(`     [${lbl}] other-month day number colour=${fc.otherColor} dayTop opacity=${fc.otherTopOpacity}`);
    if (fc.personal) console.log(`     [${lbl}] cbev-personal ${fc.personal.c} on ${fc.personal.b} = ${ratio(fc.personal.c, fc.personal.b).toFixed(2)}:1`);
    if (fc.company) console.log(`     [${lbl}] cbev-company  ${fc.company.c} on ${fc.company.b} = ${ratio(fc.company.c, fc.company.b).toFixed(2)}:1`);
    console.log(`     [${lbl}] more-link role=${fc.moreRole} aria-controls=${fc.moreAriaControls}`);
    console.log(`     [${lbl}] event titles checked=${fc.checked}  ESCAPING their event=${fc.escaped}`);
}
await browser.close();
