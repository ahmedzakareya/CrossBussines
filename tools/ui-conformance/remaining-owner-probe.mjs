// Establishes the TRUE OWNER of the contrast findings that survived the authority fix.
// For each reported selector it reports the computed foreground, the real painted background,
// the measured ratio, and — decisively — whether the colour comes from an authority token
// (which TAB-1 owns) or from a value set on the element/view itself.
import { chromium } from 'playwright';

const BASE = process.env.UI_BASE_URL ?? 'http://localhost:5268';
const TARGETS = [
    ['/Tasks/Reports', 'a[href="/Tasks/Reports?r=summary"]'],
    ['/Tasks/MatchSuggestions', 'table thead th'],
    ['/Calendar', '#fc-dom-2'],
    ['/Calendar', '.fc-event-title'],
    ['/Calendar', '.cbev-company .fc-event-title'],
];

const browser = await chromium.launch();
const page = await (await browser.newContext({ ignoreHTTPSErrors: true })).newPage();
await page.setViewportSize({ width: 1440, height: 900 });
await page.goto(`${BASE}/Account/Login`, { waitUntil: 'domcontentloaded' });
await page.fill('input[name="UserName"]', process.env.UI_USER ?? 'Admin');
await page.fill('input[type="password"]', process.env.UI_PASS ?? 'Admin@123');
await Promise.all([page.waitForNavigation({ waitUntil: 'domcontentloaded' }).catch(() => {}),
                   page.click('button[type="submit"], input[type="submit"]')]);

for (const [route, sel] of TARGETS) {
    await page.goto(`${BASE}${route}`, { waitUntil: 'networkidle' }).catch(() => {});
    await page.waitForTimeout(1500);
    const info = await page.evaluate(([sel]) => {
        const srgb = (c) => { c /= 255; return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4); };
        const parse = (s) => (String(s).match(/[\d.]+/g) || []).map(Number);
        const lum = ([r, g, b]) => 0.2126 * srgb(r) + 0.7152 * srgb(g) + 0.0722 * srgb(b);
        const ratio = (a, b) => { const [x, y] = [lum(a), lum(b)].sort((m, n) => n - m); return (x + 0.05) / (y + 0.05); };
        const hex = (v) => '#' + v.slice(0, 3).map((n) => Math.round(n).toString(16).padStart(2, '0')).join('').toUpperCase();
        const opaque = (s) => { const p = parse(s); return p.length >= 4 && p[3] === 0 ? null : p; };
        const bgOf = (el) => { let n = el; while (n && n !== document.documentElement) { const c = opaque(getComputedStyle(n).backgroundColor); if (c) return c; n = n.parentElement; } return [255, 255, 255]; };

        const els = [...document.querySelectorAll(sel)].slice(0, 4);
        return els.map((el) => {
            const cs = getComputedStyle(el);
            const fg = parse(cs.color), bg = bgOf(el);
            // which rules set this colour?
            const rules = [];
            for (const sh of document.styleSheets) {
                let rs; try { rs = sh.cssRules; } catch (e) { continue; }
                for (const r of rs) {
                    if (!r.selectorText || !r.style || !r.style.color) continue;
                    try { if (el.matches(r.selectorText)) rules.push(`${(sh.href || 'inline').split('/').pop()} :: ${r.selectorText.slice(0, 70)} -> ${r.style.color}${r.style.getPropertyPriority('color') ? ' !imp' : ''}`); } catch (e) {}
                }
            }
            return {
                tag: el.tagName.toLowerCase(), cls: (typeof el.className === 'string' ? el.className : '').slice(0, 80),
                inlineStyle: el.getAttribute('style') || '', fg: hex(fg), bg: hex(bg), ratio: +ratio(fg, bg).toFixed(2),
                fontPx: cs.fontSize, weight: cs.fontWeight, text: (el.textContent || '').trim().slice(0, 30),
                rules: rules.slice(-3),
            };
        });
    }, [sel]);
    console.log(`\n=== ${route}   ${sel}   (${info.length} matched) ===`);
    for (const i of info) {
        console.log(`   <${i.tag} class="${i.cls}"> "${i.text}"`);
        console.log(`      fg=${i.fg} bg=${i.bg} ratio=${i.ratio}  font=${i.fontPx}/${i.weight}  inline="${i.inlineStyle}"`);
        for (const r of i.rules) console.log(`      rule: ${r}`);
    }
}
await browser.close();
