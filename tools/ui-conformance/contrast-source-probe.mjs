// Where does the color-contrast failure actually come from?
// If the SAME utility class fails on the Inventory authority (the control) as it does on a Tasks
// screen, the defect is in the shared token that defines the colour - not in the Tasks view that
// consumes it. This probe answers that by comparing the two directly and reporting the computed
// colours and the CSS rule that set them.
import { chromium } from 'playwright';
import { AxeBuilder } from '@axe-core/playwright';

const BASE = process.env.UI_BASE_URL ?? 'http://localhost:5299';
const browser = await chromium.launch();
const ctx = await browser.newContext({ ignoreHTTPSErrors: true });
const page = await ctx.newPage();
await page.setViewportSize({ width: 1440, height: 900 });

await page.goto(`${BASE}/Account/Login`, { waitUntil: 'domcontentloaded' });
await page.fill('input[name="UserName"]', process.env.UI_USER ?? 'Admin');
await page.fill('input[type="password"]', process.env.UI_PASS ?? 'Admin@123');
await Promise.all([page.waitForNavigation({ waitUntil: 'domcontentloaded' }).catch(() => {}),
                   page.click('button[type="submit"], input[type="submit"]')]);

for (const [label, path] of [['INVENTORY AUTHORITY (control)', '/Inventory/Index'], ['TASKS', '/Tasks']]) {
    await page.goto(`${BASE}${path}`, { waitUntil: 'networkidle' });
    const axe = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze();
    const cc = axe.violations.find((v) => v.id === 'color-contrast');

    console.log(`\n=== ${label}  ${path} ===`);
    console.log(`   color-contrast violation: ${cc ? `${cc.impact}, ${cc.nodes.length} node(s)` : 'NONE'}`);
    if (cc) {
        const classes = {};
        cc.nodes.forEach((n) => {
            const m = /class="([^"]*)"/.exec(n.html);
            (m ? m[1].split(/\s+/) : ['(no class)']).forEach((c) => { if (c) classes[c] = (classes[c] || 0) + 1; });
        });
        const top = Object.entries(classes).sort((a, b) => b[1] - a[1]).slice(0, 6);
        console.log('   most common classes on failing nodes:', top.map(([c, n]) => `${c}×${n}`).join(', '));
        console.log('   sample message:', (cc.nodes[0].any[0]?.message ?? '').trim().slice(0, 170));
    }

    // The computed colour of .text-muted, wherever it is defined.
    const probe = await page.evaluate(() => {
        const el = document.querySelector('.text-muted');
        if (!el) return null;
        const cs = getComputedStyle(el);
        let bg = 'rgba(0, 0, 0, 0)', n = el;
        while (n && (bg === 'rgba(0, 0, 0, 0)' || bg === 'transparent')) { bg = getComputedStyle(n).backgroundColor; n = n.parentElement; }
        return { color: cs.color, bg, cssText: cs.getPropertyValue('--bs-text-muted') || '(token not set)' };
    });
    if (probe) console.log(`   .text-muted computed: color=${probe.color} on bg=${probe.bg}   --bs-text-muted=${probe.cssText}`);
}

// Which stylesheet defines it?
const origin = await page.evaluate(() => {
    const out = [];
    for (const sheet of document.styleSheets) {
        let rules; try { rules = sheet.cssRules; } catch { continue; }
        for (const r of rules) {
            if (r.selectorText && /(^|,)\s*\.text-muted\s*(,|$)/.test(r.selectorText) && r.style?.color) {
                out.push({ href: (sheet.href || 'inline').split('/').slice(-1)[0], sel: r.selectorText.slice(0, 60), color: r.style.color });
            }
        }
    }
    return out;
});
console.log('\n=== stylesheet rules defining .text-muted colour ===');
origin.forEach((o) => console.log(`   ${o.href}   ${o.sel}  ->  color: ${o.color}`));

await browser.close();
