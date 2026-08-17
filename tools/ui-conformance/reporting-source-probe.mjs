// =================================================================================================
// REPORTING SOURCE-ATTRIBUTION PROBE  (diagnostic only — TAB-2 / Reporting owner)
//
// WHY THIS EXISTS: ui-conformance.mjs stamps every finding with `owner: route.module`, i.e. the
// module that owns the ROUTE. It never inspects where in the DOM the offending node came from. So a
// defect that lives in the shared shell (_LayoutInventory.cshtml) is reported as "owner: Reporting"
// on /Reports and as "owner: Inventory" on /Inventory/Index — the same defect, two owners, decided
// by which URL happened to render it.
//
// Reporting cannot close findings it does not own, and must not "fix" the shared shell to make its
// own column go green. This probe answers the only question that matters for that decision:
//
//     for EVERY violating node, is it inside the Reporting view's own content, or inside the shell?
//
// The discriminator is structural, not a guess: the Inventory shell renders the page content inside
// #kt_app_content (the Metronic content region). Everything outside it — header, toolbar chrome,
// sidebar, drawers, chat/activity demo panels, footer — is shell. Reporting's views contribute only
// what is inside that region, plus their own toolbar block.
//
// It also reports the REAL overflow culprit at 390px by walking every element and finding those whose
// right edge exceeds the document width, which the aggregate scrollWidth number cannot tell you.
//
// VERIFICATION ONLY. Writes one JSON artifact. Records no baseline, blesses nothing, edits no view.
//
//   node reporting-source-probe.mjs
// =================================================================================================

import { chromium } from 'playwright';
import { AxeBuilder } from '@axe-core/playwright';
import { writeFileSync, mkdirSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const BASE = process.env.UI_BASE_URL ?? 'http://localhost:5241';
const USER = process.env.UI_USER ?? '';
const PASS = process.env.UI_PASS ?? '';

const ROUTES = [
    // THE AUTHORITY IS PROBED TOO, and that is the point of this file for colour-contrast: if a token
    // fails on Inventory itself, it is a shared design-system defect and Reporting must not "fix" it
    // locally — doing so would replace an Inventory class with a Reporting-specific one, i.e. invent
    // the Reporting palette the brief forbids. Parity with the authority is the evidence that decides
    // whether a finding is Reporting's to close.
    { id: 'authority-inventory', path: '/Inventory/Index' },
    { id: 'reports-center', path: '/Reports' },
    { id: 'report-viewer', path: '/Reports/Viewer/Platform.BusinessEventLog' },
];
const VIEWPORTS = [
    { id: 'desktop', width: 1440, height: 900 },
    { id: 'tablet', width: 992, height: 900 },
    { id: 'mobile', width: 390, height: 844 },
];
const CULTURES = ['en', 'ar'];

async function signIn(context) {
    const page = await context.newPage();
    await page.goto(`${BASE}/Account/Login`, { waitUntil: 'domcontentloaded' });
    await page.fill('input[name="UserName"]', USER);
    await page.fill('input[name="Password"]', PASS);
    await Promise.all([
        page.waitForNavigation({ waitUntil: 'domcontentloaded' }).catch(() => {}),
        page.click('button[type="submit"]'),
    ]);
    const landed = page.url();
    await page.close();
    if (/\/Account\/Login/i.test(landed)) throw new Error(`sign-in failed, still on ${landed}`);
}

async function setCulture(context, culture) {
    const page = await context.newPage();
    await page.goto(`${BASE}/Account/SetLanguage?culture=${culture}&returnUrl=%2F`, { waitUntil: 'domcontentloaded' })
              .catch(() => {});
    await page.close();
}

const rows = [];

async function probe(context, route, culture, viewport) {
    const id = `${route.id}.${culture}.${viewport.id}`;
    const page = await context.newPage();
    await page.setViewportSize({ width: viewport.width, height: viewport.height });
    await page.goto(`${BASE}${route.path}`, { waitUntil: 'networkidle', timeout: 30_000 });

    // ---- axe, but retaining EVERY node and asking where each one lives ---------------------------
    const axe = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze();

    for (const v of axe.violations.filter((x) => x.impact === 'critical' || x.impact === 'serious')) {
        for (const node of v.nodes) {
            const target = node.target.join(' ');
            const where = await page.evaluate((sel) => {
                let el;
                try { el = document.querySelector(sel); } catch { el = null; }
                if (!el) return { region: 'unresolved', tag: '', snippet: '' };

                // Structural discriminator. #kt_app_content is the Metronic content region the view
                // renders into; the toolbar block sits in #kt_app_toolbar. Anything else is shell.
                const inContent = !!el.closest('#kt_app_content');
                const inToolbar = !!el.closest('#kt_app_toolbar');
                const inDrawer = !!el.closest('.drawer, #kt_drawer_chat, #kt_activities, #kt_help, .app-header, .app-sidebar, .app-footer, #kt_app_header, #kt_app_sidebar, #kt_app_footer');

                return {
                    region: inDrawer ? 'shell-chrome'
                          : inContent ? 'view-content'
                          : inToolbar ? 'view-toolbar'
                          : 'shell-other',
                    tag: el.tagName.toLowerCase(),
                    snippet: (el.outerHTML || '').slice(0, 160).replace(/\s+/g, ' '),
                };
            }, target);

            rows.push({ id, kind: 'a11y', rule: v.id, impact: v.impact, target, ...where });
        }
    }

    // ---- the REAL overflow culprits --------------------------------------------------------------
    const overflow = await page.evaluate(() => {
        const docWidth = document.documentElement.clientWidth;
        const scrollWidth = document.documentElement.scrollWidth;
        if (scrollWidth <= docWidth + 2) return { scrollWidth, docWidth, culprits: [] };

        const culprits = [];
        for (const el of document.querySelectorAll('*')) {
            const r = el.getBoundingClientRect();
            const right = r.right + window.scrollX;
            const left = r.left + window.scrollX;
            if (right > docWidth + 2 || left < -2) {
                // Only the element itself, not every ancestor that inherits the width.
                const parent = el.parentElement;
                const pr = parent ? parent.getBoundingClientRect().right + window.scrollX : Infinity;
                if (right > pr + 2 || !parent) {
                    culprits.push({
                        tag: el.tagName.toLowerCase(),
                        cls: (el.className || '').toString().slice(0, 120),
                        id: el.id || '',
                        left: Math.round(left), right: Math.round(right),
                        width: Math.round(r.width),
                        inContent: !!el.closest('#kt_app_content'),
                        inToolbar: !!el.closest('#kt_app_toolbar'),
                        snippet: (el.outerHTML || '').slice(0, 140).replace(/\s+/g, ' '),
                    });
                }
            }
        }
        return { scrollWidth, docWidth, culprits: culprits.slice(0, 25) };
    });

    if (overflow.culprits.length || overflow.scrollWidth > overflow.docWidth + 2) {
        rows.push({ id, kind: 'overflow', rule: 'horizontal-overflow', impact: 'serious',
                    target: `${overflow.scrollWidth}px vs ${overflow.docWidth}px`,
                    region: '-', tag: '-', snippet: JSON.stringify(overflow.culprits) });
    }

    await page.close();
}

const browser = await chromium.launch();
const context = await browser.newContext({ ignoreHTTPSErrors: true });
try {
    await signIn(context);
    for (const culture of CULTURES) {
        await setCulture(context, culture);
        for (const route of ROUTES) {
            for (const viewport of VIEWPORTS) await probe(context, route, culture, viewport);
        }
    }
} finally {
    await context.close();
    await browser.close();
}

mkdirSync(join(HERE, 'artifacts'), { recursive: true });
writeFileSync(join(HERE, 'artifacts', 'reporting-source-probe.json'), JSON.stringify(rows, null, 2));

// ---- summary: the attribution table ------------------------------------------------------------
const byRuleRegion = new Map();
for (const r of rows.filter((x) => x.kind === 'a11y')) {
    const key = `${r.rule}|${r.region}`;
    byRuleRegion.set(key, (byRuleRegion.get(key) ?? 0) + 1);
}
console.log('\n=== a11y violating NODES by rule x region ===');
for (const [key, count] of [...byRuleRegion.entries()].sort()) {
    const [rule, region] = key.split('|');
    console.log(`  ${rule.padEnd(30)} ${region.padEnd(14)} ${count}`);
}
console.log('\n=== overflow ===');
for (const r of rows.filter((x) => x.kind === 'overflow')) {
    console.log(`  ${r.id}: ${r.target}`);
    for (const c of JSON.parse(r.snippet)) {
        console.log(`      <${c.tag} class="${c.cls}"> left=${c.left} right=${c.right} w=${c.width} inContent=${c.inContent} inToolbar=${c.inToolbar}`);
    }
}
console.log(`\nwrote artifacts/reporting-source-probe.json (${rows.length} rows)\n`);
