// §8 DETERMINISM PROOF — the gate that decides whether blessing is permitted.
//
// Runs the COMPLETE governed matrix three times against an unchanged tree and unchanged database,
// using the fixed certification clock, capturing to run-scoped folders. Then compares A vs B and
// B vs C screenshot-by-screenshot. Baselines are irrelevant here: what must be proven is that two
// captures of the same page agree. Re-blessing without this is how the last certification failed.
import { chromium } from 'playwright';
import { AxeBuilder } from '@axe-core/playwright';
import { PNG } from 'pngjs';
import pixelmatch from 'pixelmatch';
import { readFileSync, writeFileSync, mkdirSync, rmSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createWarmUpEvaluator } from './certification-gate.mjs';

const HERE = dirname(fileURLToPath(import.meta.url));
const CFG = JSON.parse(readFileSync(join(HERE, 'routes.json'), 'utf8'));
const BASE = process.env.UI_BASE_URL ?? 'http://localhost:5299';
const CERT_NOW = process.env.UI_CONFORMANCE_NOW ?? '2026-03-14T09:26:53.0000000';
const RUNS = Number(process.env.DETERMINISM_RUNS ?? 3);
const OUT = join(HERE, 'artifacts', 'determinism');

rmSync(OUT, { recursive: true, force: true });
mkdirSync(OUT, { recursive: true });

const ROUTES = [CFG.control, ...CFG.routes];
const EXPECTED = ROUTES.length * CFG.cultures.length * CFG.viewports.length;

// WARM-UP: do not start capturing until the application's own startup transients have settled.
//
// Proven necessary, not assumed: run A once differed from B on exactly three captures, all
// /BusinessEventMonitor, all page-HEIGHT changes (2940->2863), while B and C were identical. A DOM
// text diff showed the changing node was the worker-role notice — the background worker-lease
// resolving shortly after the host starts. Polling document height was the wrong condition, because
// the page is briefly stable *while the role is still unresolved*; the condition has to be the role
// itself.
//
// The RULE now lives in certification-gate.mjs, because it decides whether a baseline may be blessed
// and the only way to exercise it here is a 45-minute three-matrix run — too expensive to re-prove
// after a change. This file supplies runtime samples; that module decides, and its two contracts
// (normal requires Primary; certification requires a present, single-valued, non-transitioning role
// because the dispatch worker is deliberately not registered) are proved there in milliseconds.
const WARMUP_PROBES = 60, WARMUP_INTERVAL_MS = 3000;

async function readRuntime(page) {
    return page.evaluate(async (b) => {
        try {
            const r = await fetch(`${b}/BusinessEventMonitor/Runtime`, { credentials: 'same-origin' });
            if (!r.ok) return { error: `HTTP ${r.status}` };
            const j = await r.json();
            return { workerRole: j.workerRole ?? null,
                     certificationMode: j.certificationMode ?? null,
                     backgroundWritersSuppressed: j.backgroundWritersSuppressed ?? null };
        } catch (e) { return { error: String(e && e.message || e) }; }
    }, BASE);
}

async function warmUp(page, label) {
    const evaluator = createWarmUpEvaluator();

    for (let i = 0; i < WARMUP_PROBES; i++) {
        const r = evaluator.feed(await readRuntime(page));
        if (r.verdict === 'pass') { console.log(`  warm-up (${label}): ${r.reason}`); return true; }
        if (r.verdict === 'fail') { console.log(`  warm-up (${label}): ${r.reason} — refusing to certify`); return false; }
        await page.waitForTimeout(WARMUP_INTERVAL_MS);
    }

    console.log(`  warm-up (${label}): ${evaluator.exhausted().reason} — reporting rather than certifying a moving page`);
    return false;
}

async function settled(page, attempts = 8, interval = 400) {
    let prev = await page.screenshot({ fullPage: true });
    for (let i = 0; i < attempts; i++) {
        await page.waitForTimeout(interval);
        const cur = await page.screenshot({ fullPage: true });
        if (cur.equals(prev)) return { buf: cur, settledAfter: i + 1 };
        prev = cur;
    }
    return { buf: prev, settledAfter: null };
}

async function doRun(label) {
    const dir = join(OUT, label);
    mkdirSync(dir, { recursive: true });
    const browser = await chromium.launch();
    const ctx = await browser.newContext({
        ignoreHTTPSErrors: true,
        extraHTTPHeaders: { 'X-UI-Conformance-Now': CERT_NOW },
        reducedMotion: 'reduce',
    });
    // Same external-webfont block the official runner applies: whether fonts.googleapis.com answers
    // decides which face every page is measured in, and three consecutive runs in one network state
    // cannot detect that. See ui-conformance.mjs for the evidence.
    await ctx.route('**://fonts.googleapis.com/**', (r) => r.abort());
    await ctx.route('**://fonts.gstatic.com/**', (r) => r.abort());

    const page = await ctx.newPage();
    await page.goto(`${BASE}/Account/Login`, { waitUntil: 'domcontentloaded' });
    await page.fill('input[name="UserName"]', process.env.UI_USER ?? 'Admin');
    await page.fill('input[type="password"]', process.env.UI_PASS ?? 'Admin@123');
    await Promise.all([page.waitForNavigation({ waitUntil: 'domcontentloaded' }).catch(() => {}),
                       page.click('button[type="submit"], input[type="submit"]')]);

    // FAIL CLOSED. The warm-up verdict is carried into the run result and into the final verdict
    // below. Previously it was awaited and discarded, so a run that refused to certify still went on
    // to capture 138 screenshots and could still report DETERMINISTIC: true — the gate reported a
    // problem and then passed anyway. A refusal now fails the proof.
    const warmedUp = await warmUp(page, label);

    let completed = 0, renderFail = 0, refused = 0, neverSettled = 0, axeFindings = 0;
    for (const culture of CFG.cultures) {
        await page.goto(`${BASE}/Account/SetLanguage?culture=${culture.id}&returnUrl=%2F`, { waitUntil: 'domcontentloaded' }).catch(() => {});
        for (const route of ROUTES) {
            for (const vp of CFG.viewports) {
                const id = `${route.id}.${culture.id}.${vp.id}`;
                await page.setViewportSize({ width: vp.width, height: vp.height });
                let resp = null;
                try { resp = await page.goto(`${BASE}${route.path}`, { waitUntil: 'networkidle', timeout: 45000 }); }
                catch (e) { if (/ERR_CONNECTION_REFUSED/.test(e.message)) refused++; }
                if (!resp || resp.status() >= 400) { renderFail++; continue; }

                const { buf, settledAfter } = await settled(page);
                if (settledAfter === null) { neverSettled++; console.log(`   !! ${id} never settled`); }
                writeFileSync(join(dir, `${id}.png`), buf);
                completed++;

                if (label === 'A') {
                    const axe = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze();
                    axeFindings += axe.violations.filter((v) => ['critical', 'serious'].includes(v.impact)).length;
                }
            }
        }
    }
    await browser.close();
    console.log(`  run ${label}: completed ${completed}/${EXPECTED}  renderFail=${renderFail}  connRefused=${refused}  neverSettled=${neverSettled}` +
                (label === 'A' ? `  axeCritical/Serious=${axeFindings}` : ''));
    return { completed, renderFail, refused, neverSettled, dir, warmedUp };
}

const labels = ['A', 'B', 'C'].slice(0, RUNS);
const results = {};
for (const l of labels) results[l] = await doRun(l);

function comparePair(x, y) {
    const dirX = results[x].dir, dirY = results[y].dir;
    const diffs = [];
    for (const culture of CFG.cultures) {
        for (const route of ROUTES) {
            for (const vp of CFG.viewports) {
                const id = `${route.id}.${culture.id}.${vp.id}`;
                const px = join(dirX, `${id}.png`), py = join(dirY, `${id}.png`);
                if (!existsSync(px) || !existsSync(py)) { diffs.push({ id, why: 'missing capture' }); continue; }
                const a = readFileSync(px), b = readFileSync(py);
                if (a.equals(b)) continue;                       // byte-identical
                const ia = PNG.sync.read(a), ib = PNG.sync.read(b);
                if (ia.width !== ib.width || ia.height !== ib.height) {
                    diffs.push({ id, why: `size ${ia.width}x${ia.height} vs ${ib.width}x${ib.height}` }); continue;
                }
                const n = pixelmatch(ia.data, ib.data, null, ia.width, ia.height, { threshold: CFG.visual.pixelThreshold });
                if (n > 0) diffs.push({ id, why: `${((n / (ia.width * ia.height)) * 100).toFixed(3)}% of pixels` });
            }
        }
    }
    return diffs;
}

console.log(`\n================ §8 DETERMINISM: ${labels.join(' vs ')} ================`);
let clean = true;
for (let i = 0; i < labels.length - 1; i++) {
    const x = labels[i], y = labels[i + 1];
    const d = comparePair(x, y);
    console.log(`\n  ${x} vs ${y}: ${d.length === 0 ? 'IDENTICAL on all captures' : `${d.length} DIFFERING capture(s)`}`);
    d.slice(0, 15).forEach((r) => console.log(`      ${r.id.padEnd(34)} ${r.why}`));
    if (d.length) clean = false;
}
const notWarmed = labels.filter((l) => !results[l].warmedUp);
if (notWarmed.length) {
    console.log(`
  WARM-UP REFUSED in run(s): ${notWarmed.join(', ')} — the runtime never reached a terminal state,`);
    console.log('  so these captures are not evidence of anything. Failing closed.');
    clean = false;
}
const totals = labels.map((l) => `${l}:${results[l].completed}/${EXPECTED}`).join('  ');
console.log(`\n  completed renders  ${totals}`);
console.log(`  DETERMINISTIC: ${clean}`);
process.exit(clean ? 0 : 1);
