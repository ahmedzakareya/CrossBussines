// TASKS SOURCE-OWNERSHIP PROBE.
//
// The certification matrix attributes a finding to the module whose ROUTE rendered it. That is the
// wrong unit for a fix: the shared shell renders on every route, so a shell defect shows up as a
// "Tasks" finding on /Tasks. This probe classifies every violating node by WHERE IT LIVES IN THE DOM
// -- page content/toolbar (Tasks source) vs sidebar/header/drawer/footer (shared shell) -- and prints
// its outerHTML so the node can be traced back to a .cshtml before anything is edited.
import { chromium } from 'playwright';
import { AxeBuilder } from '@axe-core/playwright';

const BASE = process.env.UI_BASE_URL ?? 'http://localhost:5299';
const USER = process.env.UI_USER ?? 'Admin';
const PASS = process.env.UI_PASS ?? 'Admin@123';
// PROBE_RULES=ALL widens this to every critical/serious WCAG 2 A/AA rule, which is what proves
// "Tasks source-owned Critical = 0 and High = 0" rather than only the two rules under repair.
const ONLY = process.env.PROBE_RULES === 'ALL' ? null
           : (process.env.PROBE_RULES?.split(',') ?? ['button-name', 'link-name']);
const wanted = (v) => (ONLY ? ONLY.includes(v.id) : ['critical', 'serious'].includes(v.impact));

const ROUTES = [
    ['tasks',                  '/Tasks'],
    ['tasks-all',              '/Tasks/All'],
    ['tasks-board',            '/Tasks/Board'],
    ['tasks-templates',        '/Tasks/Templates'],
    ['tasks-reports',          '/Tasks/Reports'],
    ['tasks-hours-report',     '/Tasks/HoursReport'],
    ['tasks-auto-rules',       '/Tasks/AutoRules'],
    ['tasks-match-suggestions','/Tasks/MatchSuggestions'],
    ['tasks-detail-long',      '/Tasks/Detail?id=29217'],
    ['tasks-detail-typical',   '/Tasks/Detail?id=29225'],
];
const VIEWPORTS = [['desktop', 1440, 900], ['tablet', 992, 900], ['mobile', 390, 844]];
const CULTURES = ['en', 'ar'];

const browser = await chromium.launch();
const ctx = await browser.newContext({ ignoreHTTPSErrors: true });
const page = await ctx.newPage();

await page.goto(`${BASE}/Account/Login`, { waitUntil: 'domcontentloaded' });
await page.fill('input[name="UserName"]', USER);
await page.fill('input[type="password"]', PASS);
await Promise.all([page.waitForNavigation({ waitUntil: 'domcontentloaded' }).catch(() => {}),
                   page.click('button[type="submit"], input[type="submit"]')]);

const tally = { source: {}, shell: {}, unknown: {} };
const specimens = new Map();
let overflow = 0, consoleErrors = 0, netFails = 0, rtlBad = 0;

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
            if (!resp || resp.status() >= 400) { console.log(`  !! ${id}.${culture}.${vp} HTTP ${resp?.status()}`); page.off('console', onC); page.off('requestfailed', onR); continue; }

            const dir = await page.evaluate(() => document.documentElement.getAttribute('dir'));
            if (dir !== (culture === 'ar' ? 'rtl' : 'ltr')) { rtlBad++; console.log(`  !! RTL ${id}.${culture}.${vp} dir=${dir}`); }

            const ov = await page.evaluate(() => ({ s: document.documentElement.scrollWidth, c: document.documentElement.clientWidth }));
            if (ov.s > ov.c + 2) { overflow++; console.log(`  !! OVERFLOW ${id}.${culture}.${vp}: ${ov.s}px vs ${ov.c}px`); }

            const axe = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze();
            for (const v of axe.violations.filter(wanted)) {
                for (const n of v.nodes) {
                    const info = await page.evaluate((sel) => {
                        const el = document.querySelector(sel);
                        if (!el) return { zone: 'unknown', html: sel };
                        const inContent = el.closest('#kt_app_content, #kt_app_toolbar');
                        const inShell = el.closest('#kt_app_sidebar, #kt_app_header, #kt_app_footer, .drawer, .modal, #kt_app_sidebar_menu');
                        return {
                            zone: inContent ? 'source' : (inShell ? 'shell' : 'unknown'),
                            html: el.outerHTML.slice(0, 190),
                            anchor: (el.closest('[id]')?.id) || '',
                        };
                    }, n.target.join(' ')).catch(() => ({ zone: 'unknown', html: n.html.slice(0, 190), anchor: '' }));

                    tally[info.zone][v.id] = (tally[info.zone][v.id] || 0) + 1;
                    const key = `${info.zone}|${v.id}|${info.html.slice(0, 90)}`;
                    if (!specimens.has(key)) specimens.set(key, { zone: info.zone, rule: v.id, html: info.html, where: `${id}.${culture}.${vp}`, anchor: info.anchor });
                }
            }
            consoleErrors += errs.length; netFails += fails.length;
            page.off('console', onC); page.off('requestfailed', onR);
        }
    }
}

console.log('\n================ TASKS ROUTES: NODES BY TRUE SOURCE ZONE ================');
console.log('  page content/toolbar (TASKS SOURCE) :', JSON.stringify(tally.source));
console.log('  sidebar/header/drawer (SHARED SHELL):', JSON.stringify(tally.shell));
console.log('  unclassified                        :', JSON.stringify(tally.unknown));
console.log(`\n  horizontal overflow: ${overflow}   rtl defects: ${rtlBad}   console errors: ${consoleErrors}   failed requests: ${netFails}`);

console.log('\n================ DISTINCT SPECIMENS (for source tracing) ================');
for (const s of specimens.values()) {
    console.log(`\n  [${s.zone.toUpperCase()}] ${s.rule}   first seen: ${s.where}`);
    console.log(`     ${s.html}`);
}
await browser.close();
