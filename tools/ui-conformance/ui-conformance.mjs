// =================================================================================================
// CrossBusiness — RENDERED UI CONFORMANCE VERIFICATION
//
// The static gates (CrossBuy.Tests/UiConformance) decide everything that can be decided from source.
// Four things cannot be, and this file exists for exactly those four:
//
//     visual regression  ·  responsive  ·  RTL  ·  accessibility
//
// THE RULE THIS ENCODES: if the app cannot be reached, or a route cannot be rendered, the affected
// checks are reported NOT VERIFIED and the run exits non-zero. They are NEVER reported as passed.
// A green run that silently skipped four checks is worth less than a red one.
//
// VERIFICATION ONLY. This script starts no build, edits no file, and writes nothing outside its own
// artifacts/ and baseline/screenshots/ folders.
//
//   node ui-conformance.mjs            verify against recorded baselines
//   node ui-conformance.mjs --bless    record baselines (deliberate act, dedicated commit)
//
// Environment:
//   UI_BASE_URL   default https://localhost:44368
//   UI_USER       sign-in email        (required - every governed route is [SessionValidation])
//   UI_PASS       sign-in password
// =================================================================================================

import { chromium } from 'playwright';
import { AxeBuilder } from '@axe-core/playwright';
import { PNG } from 'pngjs';
import pixelmatch from 'pixelmatch';
import { readFileSync, writeFileSync, mkdirSync, existsSync, rmSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createWarmUpEvaluator } from './certification-gate.mjs';

const HERE = dirname(fileURLToPath(import.meta.url));
const CONFIG = JSON.parse(readFileSync(join(HERE, 'routes.json'), 'utf8'));

const BASE = process.env.UI_BASE_URL ?? 'https://localhost:44368';
const USER = process.env.UI_USER ?? '';
const PASS = process.env.UI_PASS ?? '';
const BLESS = process.argv.includes('--bless');

const SHOTS = join(HERE, 'baseline', 'screenshots');
const ARTIFACTS = join(HERE, 'artifacts');

const findings = [];
const notVerified = [];

const fail = (f) => findings.push(f);
const skip = (route, check, why) => notVerified.push({ route, check, why });

// -------------------------------------------------------------------------------------------------
// Sign in once and reuse the session. Every governed route is behind [SessionValidation]; an
// unauthenticated run would redirect to /Account/Login and then "verify" the login page eleven times
// while reporting PASS. That failure mode is the whole reason this returns a hard error.
// -------------------------------------------------------------------------------------------------
async function signIn(context) {
    if (!USER || !PASS) {
        throw new Error('UI_USER / UI_PASS are not set. Every governed route requires a signed-in session; ' +
                        'without one this tool would verify the login page and call it a pass.');
    }

    const page = await context.newPage();
    await page.goto(`${BASE}/Account/Login`, { waitUntil: 'domcontentloaded' });

    await page.fill('input[name="Email"], input[type="email"], input[name="UserName"]', USER);
    await page.fill('input[name="Password"], input[type="password"]', PASS);
    await Promise.all([
        page.waitForNavigation({ waitUntil: 'domcontentloaded' }).catch(() => {}),
        page.click('button[type="submit"], input[type="submit"]'),
    ]);

    const landed = page.url();
    await page.close();

    if (/\/Account\/Login/i.test(landed)) {
        throw new Error(`Sign-in failed - still on ${landed}. Check UI_USER / UI_PASS.`);
    }
}

// -------------------------------------------------------------------------------------------------
// WAIT FOR THE HOST'S OWN STARTUP TRANSIENT TO SETTLE, before anything is captured or compared.
//
// /BusinessEventMonitor renders the dispatcher's worker role, and Index.cshtml renders EXTRA notice
// content while that role is not yet resolved. WorkerGate acquires the background-worker lease on a
// 60-second cycle, so a run started right after boot captures "Unknown" and every later run captures
// "Primary" — a ~77px height difference that made three captures unverifiable across runs.
//
// This waits for the role reported by /BusinessEventMonitor/Runtime to reach its terminal value and
// hold. It is a named, observable condition rather than a fixed sleep, and a host that never settles
// is reported instead of being certified against a moving page.
//
// THE RULE IS SHARED, NOT COPIED. This check and determinism-proof.mjs are the same decision asked in
// two places, and they had drifted: the determinism gate learned about certification mode while this
// one still demanded Primary, so a certification run passed the proof and was then reported NOT
// VERIFIED here for the very state the proof had just certified. Both now call
// certification-gate.mjs, so "which contract applies" has exactly one implementation and one set of
// proofs (certification-gate-proof.mjs).
// -------------------------------------------------------------------------------------------------
async function waitForRuntimeSettled(context) {
    const page = await context.newPage();
    const evaluator = createWarmUpEvaluator();
    try {
        // Navigate FIRST. A fresh page is about:blank, and a same-origin fetch from there resolves
        // nothing — the probe would report null forever and look like a host that never settles.
        await page.goto(`${BASE}/BusinessEventMonitor`, { waitUntil: 'domcontentloaded', timeout: 45000 }).catch(() => {});

        for (let i = 0; i < 60; i++) {
            const sample = await page.evaluate(async (b) => {
                try {
                    const r = await fetch(`${b}/BusinessEventMonitor/Runtime`, { credentials: 'same-origin' });
                    if (!r.ok) return { error: `HTTP ${r.status}` };
                    const j = await r.json();
                    return { workerRole: j.workerRole ?? null,
                             certificationMode: j.certificationMode ?? null,
                             backgroundWritersSuppressed: j.backgroundWritersSuppressed ?? null };
                } catch (e) { return { error: String((e && e.message) || e) }; }
            }, BASE).catch((e) => ({ error: String((e && e.message) || e) }));

            const r = evaluator.feed(sample);
            if (r.verdict === 'pass') { console.log(`runtime settled: ${r.reason}`); return true; }
            if (r.verdict === 'fail') {
                skip('*', 'runtime-settle', `${r.reason}; captures would race the host's startup transient`);
                return false;
            }
            await page.waitForTimeout(3000);
        }
        skip('*', 'runtime-settle', `${evaluator.exhausted().reason}; captures would race the host's startup transient`);
        return false;
    } finally { await page.close(); }
}

async function setCulture(context, culture) {
    const page = await context.newPage();
    await page.goto(`${BASE}/Account/SetLanguage?culture=${culture}&returnUrl=%2F`, { waitUntil: 'domcontentloaded' })
              .catch(() => {});
    await page.close();
}

// -------------------------------------------------------------------------------------------------
// One route, one culture, one viewport.
// -------------------------------------------------------------------------------------------------
async function verify(context, route, culture, viewport) {
    const id = `${route.id}.${culture.id}.${viewport.id}`;
    const page = await context.newPage();
    await page.setViewportSize({ width: viewport.width, height: viewport.height });

    const consoleErrors = [];
    const failedRequests = [];

    // The harness deliberately aborts the external webfont requests (see main()) so rendering does
    // not depend on a third-party CDN. Those aborts surface as requestfailed + a console error, and
    // counting them would mean reporting our own instrumentation as an application defect — 345 of
    // each, on every route, for a failure we caused on purpose. Real console errors and real failed
    // requests are still reported in full.
    const deliberatelyBlocked = /fonts\.(googleapis|gstatic)\.com/i;

    page.on('console', (m) => {
        if (m.type() !== 'error') return;
        const text = m.text();
        // Chromium reports the blocked fetch only as net::ERR_FAILED with no URL on the message, so
        // it is matched by location instead.
        const from = m.location?.()?.url ?? '';
        if (deliberatelyBlocked.test(from) || deliberatelyBlocked.test(text)) return;
        if (/net::ERR_FAILED/.test(text) && deliberatelyBlocked.test(from)) return;
        consoleErrors.push(text);
    });
    page.on('requestfailed', (r) => {
        if (deliberatelyBlocked.test(r.url())) return;
        failedRequests.push(`${r.method()} ${r.url()}`);
    });

    let response;
    try {
        response = await page.goto(`${BASE}${route.path}`, { waitUntil: 'networkidle', timeout: 30_000 });
    } catch (error) {
        skip(id, 'all', `navigation failed: ${error.message}`);
        await page.close();
        return;
    }

    if (!response || response.status() >= 400) {
        fail({ id, check: 'render', severity: 'Critical', owner: route.module,
               reason: `HTTP ${response ? response.status() : 'no response'} for ${route.path}` });
        await page.close();
        return;
    }

    // --- RTL -------------------------------------------------------------------------------------
    const dir = await page.evaluate(() => document.documentElement.getAttribute('dir'));
    if (dir !== culture.dir) {
        fail({ id, check: 'rtl', severity: 'High', owner: route.module,
               reason: `<html dir> is "${dir}", expected "${culture.dir}" under culture ${culture.id}` });
    }

    // --- RESPONSIVE: the page body must never scroll horizontally --------------------------------
    const overflow = await page.evaluate(() => ({
        scrollWidth: document.documentElement.scrollWidth,
        clientWidth: document.documentElement.clientWidth,
    }));
    if (overflow.scrollWidth > overflow.clientWidth + 2) {
        fail({ id, check: 'responsive', severity: 'High', owner: route.module,
               reason: `horizontal overflow at ${viewport.width}px: content ${overflow.scrollWidth}px vs viewport ${overflow.clientWidth}px` });
    }

    // --- ACCESSIBILITY ---------------------------------------------------------------------------
    try {
        const axe = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze();
        for (const v of axe.violations.filter((v) => CONFIG.accessibility.failOn.includes(v.impact))) {
            fail({ id, check: 'accessibility', severity: v.impact === 'critical' ? 'Critical' : 'High',
                   owner: route.module,
                   reason: `${v.id}: ${v.help} (${v.nodes.length} node(s)) - ${v.nodes[0]?.target?.join(' ') ?? ''}` });
        }
    } catch (error) {
        skip(id, 'accessibility', `axe run failed: ${error.message}`);
    }

    // --- CONSOLE / NETWORK -----------------------------------------------------------------------
    for (const e of consoleErrors.slice(0, 3)) {
        fail({ id, check: 'console', severity: 'Medium', owner: route.module, reason: `console error: ${e}` });
    }
    for (const r of failedRequests.slice(0, 3)) {
        fail({ id, check: 'network', severity: 'Medium', owner: route.module, reason: `failed request: ${r}` });
    }

    // --- VISUAL REGRESSION -----------------------------------------------------------------------
    // WAIT FOR THE PAINT TO SETTLE, and prove it settled rather than sleeping a guessed interval.
    // `networkidle` says the network is quiet; it says nothing about a chart still animating. The
    // Inventory dashboard drove ApexCharts and its screenshot changed while NO text on the page
    // changed at all — a pure animation artefact. So the page is sampled repeatedly and capture
    // only proceeds once two consecutive frames are byte-identical. A page that never settles is
    // reported, not silently accepted.
    const shot = await settledScreenshot(page, id);
    const baselinePath = join(SHOTS, `${id}.png`);

    if (BLESS) {
        mkdirSync(SHOTS, { recursive: true });
        writeFileSync(baselinePath, shot);
    } else if (!existsSync(baselinePath)) {
        skip(id, 'visual', 'no recorded baseline - run with --bless once the screen is approved');
    } else {
        const diff = compare(readFileSync(baselinePath), shot, id);
        if (diff && diff.ratio > CONFIG.visual.maxDifferingRatio) {
            fail({ id, check: 'visual', severity: 'High', owner: route.module,
                   reason: `${(diff.ratio * 100).toFixed(2)}% of pixels differ from baseline ` +
                           `(limit ${(CONFIG.visual.maxDifferingRatio * 100).toFixed(2)}%) - diff at artifacts/${id}.diff.png` });
        }
    }

    await page.close();
}

// Capture only once the rendering has stopped moving. Deterministic by construction: it compares
// consecutive frames and returns the first that repeats, rather than waiting a fixed guessed time.
async function settledScreenshot(page, id, { attempts = 8, interval = 400 } = {}) {
    let previous = await page.screenshot({ fullPage: true });
    for (let i = 0; i < attempts; i++) {
        await page.waitForTimeout(interval);
        const current = await page.screenshot({ fullPage: true });
        if (current.equals(previous)) return current;
        previous = current;
    }
    // Never settled. That is itself a finding — a page that repaints forever cannot have a visual
    // baseline, and silently screenshotting a moving target is what made the last baseline unusable.
    fail({ id, check: 'stability', severity: 'High', owner: 'unknown',
           reason: `page never reached a stable frame after ${attempts} samples ${interval}ms apart — ` +
                   'its rendering is still changing, so no screenshot baseline can be verified' });
    return previous;
}

function compare(baselineBuffer, actualBuffer, id) {
    const a = PNG.sync.read(baselineBuffer);
    const b = PNG.sync.read(actualBuffer);

    // A size change IS a layout regression - report it rather than crashing on mismatched buffers.
    if (a.width !== b.width || a.height !== b.height) {
        fail({ id, check: 'layout', severity: 'High', owner: 'unknown',
               reason: `page size changed: baseline ${a.width}x${a.height}, now ${b.width}x${b.height}` });
        return null;
    }

    const diff = new PNG({ width: a.width, height: a.height });
    const differing = pixelmatch(a.data, b.data, diff.data, a.width, a.height,
                                 { threshold: CONFIG.visual.pixelThreshold });

    if (differing > 0) {
        mkdirSync(ARTIFACTS, { recursive: true });
        writeFileSync(join(ARTIFACTS, `${id}.diff.png`), PNG.sync.write(diff));
    }
    return { ratio: differing / (a.width * a.height) };
}

// -------------------------------------------------------------------------------------------------
async function main() {
    rmSync(ARTIFACTS, { recursive: true, force: true });

    const browser = await chromium.launch();

    // ---- THE CERTIFICATION CLOCK ------------------------------------------------------------
    // ONE instant for the ENTIRE run: every route, culture, viewport, capture and comparison sees
    // the same "now". Screenshot baselines were previously unverifiable because /Workspace/*
    // renders relative-time labels ("3h" at capture, "4h" an hour later) — a DOM text diff proved
    // those labels were the ONLY thing changing. Freezing the reference instant fixes the labels
    // without touching a single stored timestamp.
    //
    // The server honours this header only in Development with UI_CONFORMANCE=1 (see
    // BL/Platform/ConformanceClock.cs); in any other configuration the middleware is not even in
    // the pipeline, so this header is inert.
    const CERT_NOW = process.env.UI_CONFORMANCE_NOW ?? '2026-03-14T09:26:53.0000000';
    const context = await browser.newContext({
        ignoreHTTPSErrors: true,                       // dev cert
        extraHTTPHeaders: { 'X-UI-Conformance-Now': CERT_NOW },
        reducedMotion: 'reduce',                       // CSS transitions/animations settle immediately
    });
    console.log(`certification clock: ${CERT_NOW}   (reducedMotion: reduce)`);

    // ---- REMOVE THE EXTERNAL FONT DEPENDENCY ------------------------------------------------
    // Both governed shells link Inter and Cairo from fonts.googleapis.com. Whether that CDN answers
    // decides which font every page is measured in, and it is outside this machine's control: the
    // bless run captured with the webfonts UNAVAILABLE (3 failed requests) and the very next run
    // rendered with Inter loaded, so 827 of 138 captures differed on text metrics alone. A visual
    // baseline that depends on a third party's availability can never be re-verified.
    //
    // Blocking the requests makes the run render in the SAME fallback face every time, online or
    // offline. It hides nothing and masks no region — it removes a variable the certification does
    // not own. The product is untouched: real users still get the webfonts.
    await context.route('**://fonts.googleapis.com/**', (r) => r.abort());
    await context.route('**://fonts.gstatic.com/**', (r) => r.abort());
    console.log('external webfonts blocked for determinism (fonts.googleapis.com, fonts.gstatic.com)');

    try {
        await signIn(context);
        await waitForRuntimeSettled(context);

        // The authority is rendered in the SAME run as everything else, so conformance is judged
        // against a live Inventory render rather than a remembered one.
        const all = [CONFIG.control, ...CONFIG.routes];

        for (const culture of CONFIG.cultures) {
            await setCulture(context, culture.id);
            for (const route of all) {
                for (const viewport of CONFIG.viewports) {
                    await verify(context, route, culture, viewport);
                }
            }
        }
    } catch (error) {
        skip('*', 'all', error.message);
    } finally {
        await context.close();
        await browser.close();
    }

    report();
}

function report() {
    mkdirSync(ARTIFACTS, { recursive: true });
    writeFileSync(join(ARTIFACTS, 'report.json'),
                  JSON.stringify({ findings, notVerified, blessed: BLESS }, null, 2));

    if (BLESS) {
        console.log(`\nBASELINES RECORDED -> baseline/screenshots  (${findings.length} finding(s) ignored while blessing)\n`);
        process.exit(0);
    }

    const order = { Critical: 0, High: 1, Medium: 2, Low: 3 };
    findings.sort((x, y) => (order[x.severity] ?? 9) - (order[y.severity] ?? 9));

    for (const f of findings) {
        console.log(`  [${f.severity}] ${f.check.padEnd(13)} ${f.id}\n      ${f.reason}\n      owner: ${f.owner}`);
    }
    for (const s of notVerified) {
        console.log(`  [NOT VERIFIED] ${s.check.padEnd(13)} ${s.route}\n      ${s.why}`);
    }

    // NOT VERIFIED is not a pass. Silence about a check that never ran is the failure mode this
    // whole tool exists to prevent.
    const verdict = findings.length === 0 && notVerified.length === 0 ? 'PASS' : 'FAIL';
    console.log(`\n${verdict}  (${findings.length} finding(s), ${notVerified.length} not verified)\n`);
    process.exit(verdict === 'PASS' ? 0 : 1);
}

await main();
