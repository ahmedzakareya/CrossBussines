// COMPLETE GOVERNED MATRIX with node counts, selectors and TRUE SOURCE OWNERSHIP.
// Reads routes.json so coverage cannot silently drift from the certification matrix.
// Ownership is decided from the DOM zone the node lives in, never from route.module.
import { chromium } from 'playwright';
import { AxeBuilder } from '@axe-core/playwright';
import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const CFG = JSON.parse(readFileSync(join(HERE, 'routes.json'), 'utf8'));
const BASE = process.env.UI_BASE_URL ?? 'http://localhost:5299';
const LABEL = process.env.PROBE_LABEL ?? 'RUN';

const browser = await chromium.launch();
const ctx = await browser.newContext({ ignoreHTTPSErrors: true });
const page = await ctx.newPage();
await page.goto(`${BASE}/Account/Login`, { waitUntil: 'domcontentloaded' });
await page.fill('input[name="UserName"]', process.env.UI_USER ?? 'Admin');
await page.fill('input[type="password"]', process.env.UI_PASS ?? 'Admin@123');
await Promise.all([page.waitForNavigation({ waitUntil: 'domcontentloaded' }).catch(() => {}),
                   page.click('button[type="submit"], input[type="submit"]')]);

const ROUTES = [CFG.control, ...CFG.routes];
const byRule = {}, nodesByRule = {}, byRoute = {}, byZone = {};
const specimens = new Map();
let overflow = 0, rtlBad = 0, consoleErr = 0, netFail = 0, renderFail = 0, refused = 0;
const netFailUrls = new Set(), consoleMsgs = new Set();

for (const culture of CFG.cultures) {
    await page.goto(`${BASE}/Account/SetLanguage?culture=${culture.id}&returnUrl=%2F`, { waitUntil: 'domcontentloaded' }).catch(() => {});
    for (const route of ROUTES) {
        for (const vp of CFG.viewports) {
            await page.setViewportSize({ width: vp.width, height: vp.height });
            const errs = [], fails = [];
            const onC = (m) => { if (m.type() === 'error') errs.push(m.text()); };
            const onR = (r) => fails.push(`${r.method()} ${r.url()}`);
            page.on('console', onC); page.on('requestfailed', onR);

            let resp = null;
            try { resp = await page.goto(`${BASE}${route.path}`, { waitUntil: 'networkidle', timeout: 45000 }); }
            catch (e) { if (/ERR_CONNECTION_REFUSED/.test(e.message)) refused++; }

            if (!resp || resp.status() >= 400) {
                renderFail++; console.log(`  !! RENDER ${route.id}.${culture.id}.${vp.id} -> ${resp ? resp.status() : 'NO RESPONSE'}`);
                page.off('console', onC); page.off('requestfailed', onR); continue;
            }
            await page.waitForTimeout(900);

            const dir = await page.evaluate(() => document.documentElement.getAttribute('dir'));
            if (dir !== culture.dir) { rtlBad++; console.log(`  !! RTL ${route.id}.${culture.id}.${vp.id} dir=${dir} expected ${culture.dir}`); }

            const ov = await page.evaluate(() => ({ s: document.documentElement.scrollWidth, c: document.documentElement.clientWidth }));
            if (ov.s > ov.c + 2) { overflow++; console.log(`  !! OVERFLOW ${route.id}.${culture.id}.${vp.id}: ${ov.s} vs ${ov.c}`); }

            const axe = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze();
            for (const v of axe.violations.filter((x) => ['critical', 'serious'].includes(x.impact))) {
                byRule[v.id] = (byRule[v.id] || 0) + 1;
                nodesByRule[v.id] = (nodesByRule[v.id] || 0) + v.nodes.length;
                byRoute[route.id] = byRoute[route.id] || {};
                byRoute[route.id][v.id] = (byRoute[route.id][v.id] || 0) + 1;

                for (const n of v.nodes) {
                    const d = await page.evaluate((sel) => {
                        const el = document.querySelector(sel);
                        if (!el) return { zone: 'unknown' };
                        const inBanner = el.closest('#cbAnnouncementsBanner, .cb-announcements, [data-cb-announcements]');
                        const inContent = el.closest('#kt_app_content, #kt_app_toolbar');
                        const inShell = el.closest('#kt_app_sidebar, #kt_app_header, #kt_app_footer, .drawer');
                        const cs = getComputedStyle(el);
                        let bg = cs.backgroundColor, p = el.parentElement;
                        while (p && (bg === 'rgba(0, 0, 0, 0)' || bg === 'transparent')) { bg = getComputedStyle(p).backgroundColor; p = p.parentElement; }
                        return { zone: inBanner ? 'announcements-banner' : inContent ? 'page-content' : inShell ? 'shared-shell' : 'unknown',
                                 cls: (el.className?.toString() || '').slice(0, 70), color: cs.color, bg,
                                 html: el.outerHTML.slice(0, 130) };
                    }, n.target.join(' ')).catch(() => ({ zone: 'unknown' }));

                    byZone[d.zone] = byZone[d.zone] || {};
                    byZone[d.zone][v.id] = (byZone[d.zone][v.id] || 0) + 1;

                    const key = `${v.id}|${d.cls}|${d.color}|${d.bg}`;
                    if (!specimens.has(key)) specimens.set(key, { rule: v.id, impact: v.impact, zone: d.zone,
                        where: `${route.id}.${culture.id}.${vp.id}`, target: n.target.join(' ').slice(0, 120),
                        cls: d.cls, color: d.color, bg: d.bg, html: d.html,
                        msg: (n.any[0]?.message ?? '').trim().slice(0, 125) });
                }
            }
            errs.forEach((e) => consoleMsgs.add(`${route.id}: ${e.slice(0, 110)}`));
            fails.forEach((f) => netFailUrls.add(`${route.id}: ${f.slice(0, 110)}`));
            consoleErr += errs.length; netFail += fails.length;
            page.off('console', onC); page.off('requestfailed', onR);
        }
    }
}

const total = Object.values(byRule).reduce((a, b) => a + b, 0);
console.log(`\n================ ${LABEL} — COMPLETE GOVERNED MATRIX ================`);
console.log(`  routes ${ROUTES.length} x cultures ${CFG.cultures.length} x viewports ${CFG.viewports.length} = ${ROUTES.length * CFG.cultures.length * CFG.viewports.length} renders`);
console.log(`  TOTAL Critical/High findings : ${total}`);
console.log(`  by rule   : ${JSON.stringify(byRule)}`);
console.log(`  NODES     : ${JSON.stringify(nodesByRule)}`);
console.log(`  by ZONE   : ${JSON.stringify(byZone)}`);
console.log(`  overflow=${overflow} rtl=${rtlBad} console=${consoleErr} netFail=${netFail} renderFail=${renderFail} connRefused=${refused}`);
if (Object.keys(byRoute).length) { console.log('\n  by route:'); for (const [r, m] of Object.entries(byRoute)) console.log(`     ${r.padEnd(24)} ${JSON.stringify(m)}`); }
if (consoleMsgs.size) { console.log('\n  console errors:'); [...consoleMsgs].slice(0, 8).forEach((m) => console.log(`     ${m}`)); }
if (netFailUrls.size) { console.log('\n  failed requests:'); [...netFailUrls].slice(0, 8).forEach((m) => console.log(`     ${m}`)); }

if (specimens.size) {
    console.log('\n================ SPECIMENS ================');
    for (const s of specimens.values()) {
        console.log(`\n  [${s.impact}] ${s.rule}   zone=${s.zone}   first: ${s.where}`);
        console.log(`     class : ${s.cls}`);
        console.log(`     colour: ${s.color} on ${s.bg}`);
        console.log(`     axe   : ${s.msg}`);
        console.log(`     html  : ${s.html}`);
    }
}
await browser.close();
