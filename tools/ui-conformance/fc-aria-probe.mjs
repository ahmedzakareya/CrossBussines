// Evidence probe for the aria-allowed-attr Critical on FullCalendar's "+N more" link.
// Dumps the element's real attributes and asks axe for the exact failure message, so the
// vendor-vs-integration question is answered from the DOM rather than from assumption.
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

await page.goto(`${BASE}/Calendar`, { waitUntil: 'networkidle' });
await page.waitForTimeout(2500);

const dump = await page.evaluate(() => {
    const out = [];
    document.querySelectorAll('a.fc-daygrid-more-link, a[title*="more"], a[title*="أخرى"]').forEach((el) => {
        out.push({
            tag: el.tagName,
            cls: el.className,
            attrs: [...el.attributes].map((a) => `${a.name}="${a.value}"`),
            html: el.outerHTML.slice(0, 200),
        });
    });
    return out;
});
console.log('=== "+N more" elements as rendered ===');
dump.forEach((d, i) => {
    console.log(`  [${i}] <${d.tag.toLowerCase()} class="${d.cls}">`);
    d.attrs.forEach((a) => console.log(`        ${a}`));
});
if (dump.length === 0) console.log('  (none rendered - no day has more events than dayMaxEvents allows)');

const axe = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze();
const v = axe.violations.find((x) => x.id === 'aria-allowed-attr');
console.log('\n=== axe aria-allowed-attr ===');
if (!v) console.log('  NOT PRESENT');
else {
    console.log(`  impact: ${v.impact}   nodes: ${v.nodes.length}`);
    v.nodes.slice(0, 3).forEach((n) => {
        console.log(`   target : ${n.target.join(' ')}`);
        console.log(`   message: ${(n.any[0]?.message ?? n.all[0]?.message ?? '').trim()}`);
        console.log(`   html   : ${n.html.slice(0, 180)}`);
    });
}
console.log('\n=== all remaining violations on /Calendar ===');
axe.violations.filter((x) => ['critical', 'serious'].includes(x.impact))
   .forEach((x) => console.log(`  [${x.impact}] ${x.id} x${x.nodes.length}`));

await browser.close();
