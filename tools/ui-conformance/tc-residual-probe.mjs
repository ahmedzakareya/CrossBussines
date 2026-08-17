// TASKS + CALENDAR RESIDUAL PROBE.
//
// Reports every critical/serious axe finding on the governed Tasks + Calendar routes, and for each
// contrast node captures the COMPUTED foreground/background, opacity, font metrics and the CSS rule
// that actually set the colour. Generated ids (#fc-dom-82) identify nothing; the effective style does.
import { chromium } from 'playwright';
import { AxeBuilder } from '@axe-core/playwright';

const BASE = process.env.UI_BASE_URL ?? 'http://localhost:5299';
const LABEL = process.env.PROBE_LABEL ?? 'RUN';

const ROUTES = [
    ['tasks',                   '/Tasks'],
    ['tasks-all',               '/Tasks/All'],
    ['tasks-board',             '/Tasks/Board'],
    ['tasks-templates',         '/Tasks/Templates'],
    ['tasks-reports',           '/Tasks/Reports'],
    ['tasks-hours-report',      '/Tasks/HoursReport'],
    ['tasks-auto-rules',        '/Tasks/AutoRules'],
    ['tasks-match-suggestions', '/Tasks/MatchSuggestions'],
    ['tasks-detail-long',       '/Tasks/Detail?id=29217'],
    ['tasks-detail-typical',    '/Tasks/Detail?id=29225'],
    ['calendar',                '/Calendar'],
    ['calendar-timeline',       '/Calendar/Timeline'],
    ['calendar-resource-view',  '/Calendar/ResourceView'],
];
const VIEWPORTS = [['desktop', 1440, 900], ['tablet', 992, 900], ['mobile', 390, 844]];
const CULTURES = ['en', 'ar'];

const browser = await chromium.launch();
const ctx = await browser.newContext({ ignoreHTTPSErrors: true });
const page = await ctx.newPage();

await page.goto(`${BASE}/Account/Login`, { waitUntil: 'domcontentloaded' });
await page.fill('input[name="UserName"]', process.env.UI_USER ?? 'Admin');
await page.fill('input[type="password"]', process.env.UI_PASS ?? 'Admin@123');
await Promise.all([page.waitForNavigation({ waitUntil: 'domcontentloaded' }).catch(() => {}),
                   page.click('button[type="submit"], input[type="submit"]')]);

const findings = {};        // rule -> finding count (route x culture x viewport occurrences)
const nodes = {};           // rule -> node count
const perRoute = {};        // route -> rule -> findings
const specimens = new Map();
let overflow = 0, rtlBad = 0, consoleErrors = 0, netFails = 0, renderFail = 0;

for (const culture of CULTURES) {
    await page.goto(`${BASE}/Account/SetLanguage?culture=${culture}&returnUrl=%2F`, { waitUntil: 'domcontentloaded' }).catch(() => {});
    for (const [id, path] of ROUTES) {
        for (const [vp, w, h] of VIEWPORTS) {
            await page.setViewportSize({ width: w, height: h });
            const errs = [], fails = [];
            const onC = (m) => { if (m.type() === 'error') errs.push(m.text()); };
            const onR = (r) => fails.push(`${r.method()} ${r.url()}`);
            page.on('console', onC); page.on('requestfailed', onR);

            const resp = await page.goto(`${BASE}${path}`, { waitUntil: 'networkidle', timeout: 45000 }).catch(() => null);
            if (!resp || resp.status() >= 400) {
                renderFail++; console.log(`  !! RENDER ${id}.${culture}.${vp} -> ${resp ? resp.status() : 'NO RESPONSE'}`);
                page.off('console', onC); page.off('requestfailed', onR); continue;
            }
            await page.waitForTimeout(1200);   // FullCalendar paints after its fetch

            const dir = await page.evaluate(() => document.documentElement.getAttribute('dir'));
            if (dir !== (culture === 'ar' ? 'rtl' : 'ltr')) { rtlBad++; console.log(`  !! RTL ${id}.${culture}.${vp} dir=${dir}`); }

            const ov = await page.evaluate(() => ({ s: document.documentElement.scrollWidth, c: document.documentElement.clientWidth }));
            if (ov.s > ov.c + 2) { overflow++; console.log(`  !! OVERFLOW ${id}.${culture}.${vp}: ${ov.s} vs ${ov.c}`); }

            const axe = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze();
            for (const v of axe.violations.filter((x) => ['critical', 'serious'].includes(x.impact))) {
                findings[v.id] = (findings[v.id] || 0) + 1;
                nodes[v.id] = (nodes[v.id] || 0) + v.nodes.length;
                perRoute[id] = perRoute[id] || {};
                perRoute[id][v.id] = (perRoute[id][v.id] || 0) + 1;

                for (const n of v.nodes) {
                    const detail = await page.evaluate((sel) => {
                        const el = document.querySelector(sel);
                        if (!el) return null;
                        const cs = getComputedStyle(el);
                        // walk up for the first painted background, the way a human reads it
                        let bg = cs.backgroundColor, p = el.parentElement;
                        while (p && (bg === 'rgba(0, 0, 0, 0)' || bg === 'transparent')) { bg = getComputedStyle(p).backgroundColor; p = p.parentElement; }
                        // which stylesheet rule set the colour?
                        let origin = '(unresolved)';
                        for (const sheet of document.styleSheets) {
                            let rules; try { rules = sheet.cssRules; } catch { continue; }
                            for (const r of rules) {
                                if (!r.selectorText || !r.style?.color) continue;
                                try { if (el.matches(r.selectorText)) origin = `${(sheet.href || 'inline').split('/').pop()} :: ${r.selectorText.slice(0, 70)} -> ${r.style.color}`; } catch { }
                            }
                        }
                        return {
                            zone: el.closest('#kt_app_content, #kt_app_toolbar') ? 'source'
                                : (el.closest('#kt_app_sidebar, #kt_app_header, #kt_app_footer, .drawer') ? 'shell' : 'unknown'),
                            cls: el.className?.toString().slice(0, 80),
                            color: cs.color, bg, opacity: cs.opacity,
                            font: `${cs.fontSize}/${cs.fontWeight}`,
                            origin,
                            html: el.outerHTML.slice(0, 150),
                        };
                    }, n.target.join(' ')).catch(() => null);

                    const key = `${v.id}|${detail?.cls ?? n.target.join(' ')}|${detail?.color}`;
                    if (!specimens.has(key)) {
                        specimens.set(key, { rule: v.id, impact: v.impact, where: `${id}.${culture}.${vp}`,
                                             target: n.target.join(' '), msg: (n.any[0]?.message ?? '').trim().slice(0, 130), ...detail });
                    }
                }
            }
            consoleErrors += errs.length; netFails += fails.length;
            page.off('console', onC); page.off('requestfailed', onR);
        }
    }
}

console.log(`\n================ ${LABEL}: TASKS + CALENDAR GOVERNED ROUTES ================`);
console.log('  findings by rule :', JSON.stringify(findings));
console.log('  NODES by rule    :', JSON.stringify(nodes));
console.log(`  overflow=${overflow}  rtl=${rtlBad}  console=${consoleErrors}  netFail=${netFails}  renderFail=${renderFail}`);
console.log('\n  per route:');
for (const [r, m] of Object.entries(perRoute)) console.log(`     ${r.padEnd(24)} ${JSON.stringify(m)}`);

console.log('\n================ DISTINCT SPECIMENS WITH COMPUTED EVIDENCE ================');
for (const s of specimens.values()) {
    console.log(`\n  [${s.impact}] ${s.rule}   zone=${s.zone}   first: ${s.where}`);
    console.log(`     target : ${s.target}`);
    console.log(`     class  : ${s.cls}`);
    console.log(`     computed color=${s.color}  bg=${s.bg}  opacity=${s.opacity}  font=${s.font}`);
    console.log(`     css    : ${s.origin}`);
    console.log(`     axe    : ${s.msg}`);
    console.log(`     html   : ${s.html}`);
}
await browser.close();
