// Is <a class="app-sidebar-logo"> genuinely unnamed, or does my simple probe miss its name?
// axe says link-name passes; axe understands <img alt>, aria-label and sr-only text. Decide from
// the DOM: dump every naming source the accessible-name algorithm would consult.
import { chromium } from 'playwright';

const BASE = process.env.UI_BASE_URL ?? 'http://localhost:5299';
const browser = await chromium.launch();
const ctx = await browser.newContext({ ignoreHTTPSErrors: true });
const page = await ctx.newPage();
await page.goto(`${BASE}/Account/Login`, { waitUntil: 'domcontentloaded' });
await page.fill('input[name="UserName"]', process.env.UI_USER ?? 'Admin');
await page.fill('input[type="password"]', process.env.UI_PASS ?? 'Admin@123');
await Promise.all([page.waitForNavigation({ waitUntil: 'domcontentloaded' }).catch(() => {}),
                   page.click('button[type="submit"], input[type="submit"]')]);
await page.goto(`${BASE}/BusinessEventMonitor`, { waitUntil: 'networkidle' });

const info = await page.evaluate(() => {
    const el = document.querySelector('a.app-sidebar-logo');
    if (!el) return { missing: true };
    const imgs = [...el.querySelectorAll('img')].map((i) => ({ alt: i.getAttribute('alt'), src: (i.getAttribute('src') || '').split('/').pop() }));
    const svgTitles = [...el.querySelectorAll('svg title')].map((t) => t.textContent);
    return {
        outer: el.outerHTML.replace(/\s+/g, ' ').slice(0, 260),
        text: (el.textContent || '').trim(),
        ariaLabel: el.getAttribute('aria-label'),
        title: el.getAttribute('title'),
        ariaLabelledby: el.getAttribute('aria-labelledby'),
        imgs, svgTitles,
        srOnly: [...el.querySelectorAll('.visually-hidden, .sr-only')].map((s) => s.textContent.trim()),
        href: el.getAttribute('href'),
    };
});
console.log(JSON.stringify(info, null, 2));

// Chromium's own accessible name for the node — the authoritative answer
const name = await page.evaluate(async () => {
    const el = document.querySelector('a.app-sidebar-logo');
    return el ? (el.ariaLabel ?? null) : null;
});
const snap = await page.accessibility.snapshot({ interestingOnly: false });
const find = (n) => { if (!n) return null; if (n.role === 'link' && /logo|crossbuy/i.test(n.name || '')) return n; for (const c of n.children || []) { const r = find(c); if (r) return r; } return null; };
console.log('\nChromium accessibility tree entry for the logo link:', JSON.stringify(find(snap)));
await browser.close();
