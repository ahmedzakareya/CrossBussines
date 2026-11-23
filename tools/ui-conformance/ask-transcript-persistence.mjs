// Does the assistant's transcript survive a reload, and a move to another screen?
//
// Asserts on what the PANEL contains after the navigation, not on what sessionStorage holds: the
// storage key is an implementation detail, and a transcript that is stored but never replayed
// would pass a storage check while still showing the asker an empty panel.
import { chromium } from 'playwright';

const BASE = process.env.UI_BASE_URL ?? 'https://localhost:7183';
const browser = await chromium.launch({ args: ['--ignore-certificate-errors'] });
const ctx = await browser.newContext({ ignoreHTTPSErrors: true, locale: 'ar-EG' });
const page = await ctx.newPage();
const out = [];
let failed = 0;
const check = (name, ok, detail) => { out.push(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? '  — ' + detail : ''}`); if (!ok) failed++; };

try {
  await page.goto(`${BASE}/Account/Login`, { waitUntil: 'domcontentloaded' });
  await page.fill('input[name="UserName"]', process.env.UI_USER ?? 'Admin');
  await page.fill('input[type="password"]', process.env.UI_PASS ?? 'Admin@123');
  await Promise.all([page.waitForNavigation({ waitUntil: 'domcontentloaded' }).catch(() => {}), page.click('button[type="submit"]')]);
  if (page.url().includes('/Account/Login')) throw new Error('sign-in failed');

  await page.goto(`${BASE}/Inventory/StockBalances`, { waitUntil: 'domcontentloaded' });

  // Ask through the panel's own code path, so this exercises what a person exercises.
  const QUESTION = 'كام موظف عندنا';
  const asked = await page.evaluate(async (q) => {
    const input = document.getElementById('cbAiInput') || document.querySelector('.cb-ai input[type=text], .cb-ai input');
    const send = document.getElementById('cbAiSend') || document.querySelector('.cb-ai button[type=submit], .cb-ai .cb-ai-send');
    if (!input || !send) return { ok: false, why: 'panel input/send not found' };
    input.value = q;
    send.click();
    return { ok: true };
  }, QUESTION);
  check('panel accepts a question', asked.ok, asked.why);
  if (!asked.ok) throw new Error(asked.why);

  // Wait for an answer card rather than a fixed delay.
  await page.waitForFunction(() => document.querySelectorAll('.cb-ai-a').length > 0 &&
    !document.querySelector('.cb-ai-dots'), null, { timeout: 60000 });

  const before = await page.evaluate(() => ({
    questions: [...document.querySelectorAll('.cb-ai-q')].map(e => e.textContent.trim()),
    answers: document.querySelectorAll('.cb-ai-a').length,
  }));
  check('answer rendered before reload', before.answers > 0, `q=${before.questions.length} a=${before.answers}`);

  // ---- RELOAD ----
  await page.reload({ waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(800);
  const afterReload = await page.evaluate(() => ({
    questions: [...document.querySelectorAll('.cb-ai-q')].map(e => e.textContent.trim()),
    answers: document.querySelectorAll('.cb-ai-a').length,
    introGone: !document.getElementById('cbAiIntro'),
  }));
  check('question survives a reload', afterReload.questions.includes(QUESTION),
        `found ${afterReload.questions.length} question(s)`);
  check('answer survives a reload', afterReload.answers >= before.answers,
        `before=${before.answers} after=${afterReload.answers}`);
  check('intro block removed on restore', afterReload.introGone);

  // ---- MOVE TO ANOTHER SCREEN (a different layout) ----
  await page.goto(`${BASE}/Accounting/ChartOfAccounts`, { waitUntil: 'domcontentloaded' }).catch(() => {});
  await page.waitForTimeout(800);
  const afterNav = await page.evaluate(() => ({
    questions: [...document.querySelectorAll('.cb-ai-q')].map(e => e.textContent.trim()),
    answers: document.querySelectorAll('.cb-ai-a').length,
  }));
  check('transcript survives moving to another screen', afterNav.questions.includes(QUESTION),
        `found ${afterNav.questions.length} question(s), ${afterNav.answers} answer(s)`);

  // ---- A NEW TAB IS A NEW CONVERSATION ----
  const page2 = await ctx.newPage();
  await page2.goto(`${BASE}/Inventory/StockBalances`, { waitUntil: 'domcontentloaded' });
  await page2.waitForTimeout(600);
  const fresh = await page2.evaluate(() => document.querySelectorAll('.cb-ai-q').length);
  check('a new tab starts empty, as the per-tab design says', fresh === 0, `found ${fresh}`);
  await page2.close();
} catch (e) {
  out.push(`NOT VERIFIED — ${e?.message ?? e}`);
  failed++;
}

await browser.close();
console.log(out.join('\n'));
process.exit(failed === 0 ? 0 : 1);
