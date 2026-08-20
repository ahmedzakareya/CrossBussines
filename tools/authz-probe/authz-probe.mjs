// ============================================================================================
// AUTHORIZATION PROBE — asserts what the SERVER actually answered, not what the screen showed.
//
// WHY THIS EXISTS. A build proves types line up. A DI proof proves services resolve. Neither
// proves that POST /Project/DeleteBoqLine actually refuses a user without the right. The only
// thing that proves an authorization boundary is the STATUS CODE the server returned when it
// was asked. So this drives a real browser session and reads page.on('response') — the same
// discipline as the UI conformance runner, pointed at authorization instead of pixels.
//
//   node tools/authz-probe/authz-probe.mjs
//
//   UI_BASE_URL   default https://localhost:44368
//   UI_USER       sign-in email        (required)
//   UI_PASS       sign-in password     (required)
//   AUTHZ_HEADED  "1" to watch it work (default: headed, as configured below)
//
// ---------------------------------------------------------------------------------------------
// IT NEVER WRITES, and the guarantee is structural rather than a promise:
//
//   1. Every mutating probe is sent as a request the server MUST refuse — and refusal happens
//      in the authorization filter, before any handler touches data.
//   2. Every mutating probe additionally targets a DELIBERATELY NON-EXISTENT id
//      (PROBE_ID below). So even in the failure case this exists to detect — authorization
//      silently allowing the call — the handler finds no row and changes nothing.
//   3. No probe sends a well-formed payload for a real record. Ever.
//
// That second point is the important one. A probe that could only be safe if the code under
// test is correct is not a safe probe: it is an experiment that writes to real data exactly
// when it discovers the bug.
//
// IT REPORTS WHAT IT DID NOT DO. A gate that prints "12/12 passed" without naming what it
// skipped is how a suite goes green while missing things. Every skip is listed at the end.
// ============================================================================================

import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const HERE = dirname(fileURLToPath(import.meta.url));

// playwright is reused from the ui-conformance install rather than duplicating a node_modules
// tree. createRequire, not a bare import: ESM ignores NODE_PATH, so a bare specifier cannot find
// a package installed in a sibling directory.
const { chromium } = createRequire(import.meta.url)(
    join(HERE, '..', 'ui-conformance', 'node_modules', 'playwright'));
const BASE = process.env.UI_BASE_URL ?? 'https://localhost:44368';
const USER = process.env.UI_USER;
const PASS = process.env.UI_PASS;
const SHOTS = join(HERE, 'shots-authz');
const PROFILE = join(HERE, 'chrome-profile');

// An id chosen to match nothing. Mutating probes aim here so that a probe cannot damage data
// even if the authorization it is testing turns out to be broken.
const PROBE_ID = 987654321;

const results = [];
const skipped = [];
const pass = (name, detail) => { results.push({ ok: true, name, detail }); console.log(`  PASS  ${name}${detail ? ' — ' + detail : ''}`); };
const fail = (name, detail) => { results.push({ ok: false, name, detail }); console.log(`  FAIL  ${name}${detail ? ' — ' + detail : ''}`); };
const skip = (name, why) => { skipped.push({ name, why }); };

// ---------------------------------------------------------------------------------------------
// The endpoints under test.
//
// CORRECTION, and it matters more than anything else in this file. The first version of this
// list expected 403 from the mutating endpoints, which is only true for an UNPRIVILEGED user.
// Run with an administrator — the identity actually available — authorization would ALLOW those
// calls, and the "aim at a non-existent id" safeguard does not help for CREATE endpoints:
// VendorQuickAdd and CustomerQuickAdd need no existing row, so a permitted POST would have
// written a vendor and a customer into CrossBuyDev.
//
// So with a privileged identity this probe sends only:
//
//   * GET  — reads, which cannot mutate; and
//   * POST that the server must reject BEFORE reaching a handler, i.e. missing the
//     anti-forgery token. ValidateAntiForgeryToken rejects at the filter, so no business code
//     runs whether or not the caller holds the permission.
//
// The unprivileged-DENY direction is the more interesting test and it is NOT run here — it
// needs a low-privilege identity. It is listed under NOT EXERCISED rather than faked.
// ---------------------------------------------------------------------------------------------
const PROBES = [
    // The API refuses an ambient browser session. AccountingApiController is
    // [Authorize(AuthenticationSchemes = JwtBearerDefaults)], so a cookie-authenticated caller must
    // get 401 — the signed-in browser session carries no bearer token. That is a real boundary and
    // is asserted as one. (Expecting 'allowed' here was a bug in this file, not in the product.)
    { area: 'Accounting', path: '/api/acc/summary',  method: 'GET',  expect: 'denied' },

    // Reads — Accounting authorization landed in baca581; must stay reachable for an admin.
    // Routes read off the source, not invented: an invented path answers 404, and a 404 is not a boundary.
    { area: 'Accounting', path: '/Accounting/Index', method: 'GET',  expect: 'allowed' },

    // Anti-forgery boundary. A POST with no token must be refused at the filter, for anyone.
    // Safe by construction: the handler never runs, so nothing is created or changed.
    // The action is StampInvoiceCustomer — the helper it calls is OfficialInvoiceHelper.StampCustomer,
    // and naming the probe after the helper produced a 404 that looked like a failing boundary.
    { area: 'Accounting', path: '/Accounting/StampInvoiceCustomer', method: 'POST_NOCSRF', expect: 'denied' },

    // Projects — Phase 3C-1. Reads must stay reachable for an authorized admin.
    { area: 'Projects',   path: '/Project/Projects',       method: 'GET',  expect: 'allowed' },
    { area: 'Projects',   path: '/Project/Dashboard',      method: 'GET',  expect: 'allowed' },
    { area: 'Projects',   path: '/Project/Billing',        method: 'GET',  expect: 'allowed' },

    // Project mutating endpoints, probed ONLY through the anti-forgery boundary. Every one of these
    // is [HttpPost][ValidateAntiForgeryToken], so a token-less POST is refused at the filter before
    // GateAsync or any service runs — the refusal is real and nothing can be written. The
    // insufficient-permission path is a different question and is NOT answered here; see the skips.
    { area: 'Projects', path: '/Project/SaveBilling',         method: 'POST_NOCSRF', expect: 'denied' },
    { area: 'Projects', path: '/Project/ApproveBilling',      method: 'POST_NOCSRF', expect: 'denied' },
    { area: 'Projects', path: '/Project/PostBilling',         method: 'POST_NOCSRF', expect: 'denied' },
    { area: 'Projects', path: '/Project/DeleteBilling',       method: 'POST_NOCSRF', expect: 'denied' },
    { area: 'Projects', path: '/Project/SaveBoq',             method: 'POST_NOCSRF', expect: 'denied' },
    { area: 'Projects', path: '/Project/PostMaterialIssue',   method: 'POST_NOCSRF', expect: 'denied' },
    { area: 'Projects', path: '/Project/ApproveSubBilling',   method: 'POST_NOCSRF', expect: 'denied' },
    { area: 'Projects', path: '/Project/ApproveVariationOrder', method: 'POST_NOCSRF', expect: 'denied' },

    // POS — Phase 3C-2. Same anti-forgery-only discipline. OpenShift/CloseShift/PrepareFinished/
    // PrepareSemi are [HttpPost][ValidateAntiForgeryToken]; CustomerQuickAdd is an ApiPerm CREATE
    // endpoint and is therefore NOT posted to at all — see the skips.
    { area: 'POS', path: '/Pos/Terminals',       method: 'GET',         expect: 'allowed' },
    { area: 'POS', path: '/Pos/OpenShift',       method: 'POST_NOCSRF', expect: 'denied' },
    { area: 'POS', path: '/Pos/CloseShift',      method: 'POST_NOCSRF', expect: 'denied' },
    { area: 'POS', path: '/Pos/PrepareFinished', method: 'POST_NOCSRF', expect: 'denied' },
    { area: 'POS', path: '/Pos/PrepareSemi',     method: 'POST_NOCSRF', expect: 'denied' },

    // Admin — Phase 3C-3. All four gated actions are [HttpPost][ValidateAntiForgeryToken], so the
    // token-less probe is refused at the filter. SaveSalaryPolicy carries [ApiPerm(Hr, payroll-manage)]
    // with NO anti-forgery, so a token-less POST would reach the permission filter instead — and as
    // admin that permission is held, so it is NOT posted to. See the skips.
    { area: 'Admin', path: '/Admin/PostFinalSettlement', method: 'POST_NOCSRF', expect: 'denied' },
    { area: 'Admin', path: '/Admin/PostLeaveProvision',  method: 'POST_NOCSRF', expect: 'denied' },
    { area: 'Admin', path: '/Admin/RunLeaveCarryOver',   method: 'POST_NOCSRF', expect: 'denied' },
    { area: 'Admin', path: '/Admin/Encash',              method: 'POST_NOCSRF', expect: 'denied' },

    // Tasks — Phase 3C-4. All three gated actions are [HttpPost][ValidateAntiForgeryToken].
    { area: 'Tasks', path: '/Tasks/Index',           method: 'GET',         expect: 'allowed' },
    { area: 'Tasks', path: '/Tasks/ConfirmMatch',    method: 'POST_NOCSRF', expect: 'denied' },
    { area: 'Tasks', path: '/Tasks/GenerateInvoice', method: 'POST_NOCSRF', expect: 'denied' },
    { area: 'Tasks', path: '/Tasks/PostLaborToWO',   method: 'POST_NOCSRF', expect: 'denied' },

    // People + Inventory — Phase 3C-5, the final two CBA001 findings. Both are
    // [HttpPost][ValidateAntiForgeryToken], so the token-less probe is refused at the filter.
    { area: 'People',    path: '/People/DecideLeave',            method: 'POST_NOCSRF', expect: 'denied' },
    { area: 'Inventory', path: '/Inventory/WarehouseQuickAdd',   method: 'POST_NOCSRF', expect: 'denied' },

    // FileManager — Phase 3D-1. Reads are company-scoped from the caller's own employee row.
    { area: 'FileManager', path: '/FileManager/Index',        method: 'GET',         expect: 'allowed' },
    { area: 'FileManager', path: '/FileManager/CreateFolder', method: 'POST_NOCSRF', expect: 'denied' },
    { area: 'FileManager', path: '/FileManager/Rename',       method: 'POST_NOCSRF', expect: 'denied' },
    { area: 'FileManager', path: '/FileManager/Move',         method: 'POST_NOCSRF', expect: 'denied' },
    { area: 'FileManager', path: '/FileManager/Delete',       method: 'POST_NOCSRF', expect: 'denied' },
    { area: 'FileManager', path: '/FileManager/Upload',       method: 'POST_NOCSRF', expect: 'denied' },
    // Announcements — Phase 3D-2. Create/Dismiss/Toggle are [HttpPost] with NO anti-forgery
    // attribute and NO permission check; all three are recorded gaps in authorization-baseline.json.
    // Because nothing would refuse them, they are NEVER POSTed here — GET_405 proves the route
    // exists while leaving the handler uninvoked.
    { area: 'Announcements', path: '/Announcements/Active',  method: 'GET',     expect: 'allowed' },
    { area: 'Announcements', path: '/Announcements/Index',   method: 'GET',     expect: 'allowed' },
    { area: 'Announcements', path: '/Announcements/Create',  method: 'GET_405', expect: 'routed' },
    { area: 'Announcements', path: '/Announcements/Dismiss', method: 'GET_405', expect: 'routed' },
    { area: 'Announcements', path: '/Announcements/Toggle',  method: 'GET_405', expect: 'routed' },

    // Communication — Phase 3D-3. Send/Trash/Resend ARE [ValidateAntiForgeryToken], so a
    // token-less POST is refused at the filter. Star is NOT, so it is never POSTed.
    { area: 'Comm', path: '/Comm',          method: 'GET',         expect: 'allowed' },
    { area: 'Comm', path: '/Comm/Index',    method: 'GET',         expect: 'allowed' },
    { area: 'Comm', path: '/Comm/Compose',  method: 'GET',         expect: 'allowed' },
    { area: 'Comm', path: '/Comm/Contacts', method: 'GET',         expect: 'allowed' },
    { area: 'Comm', path: '/Comm/Send',     method: 'POST_NOCSRF', expect: 'denied' },
    { area: 'Comm', path: '/Comm/Trash',    method: 'POST_NOCSRF', expect: 'denied' },
    { area: 'Comm', path: '/Comm/Resend',   method: 'POST_NOCSRF', expect: 'denied' },
    { area: 'Comm', path: '/Comm/Star',     method: 'GET_405',     expect: 'routed' },
    // Comments — Phase 3D-4. Add and Delete are [HttpPost] with NO anti-forgery attribute and no
    // permission check; both are recorded gaps in authorization-baseline.json. Nothing would refuse
    // a POST, so neither is ever POSTed here. Delete is author-scoped (CreatedBy == caller) and Add
    // validates EntityType against IEntityRegistry, but neither guard is reachable without writing.
    { area: 'Comments', path: '/Comments/List?entityType=Quotation&entityId=987654321', method: 'GET', expect: 'allowed' },
    { area: 'Comments', path: '/Comments/Add',    method: 'GET_405', expect: 'routed' },
    { area: 'Comments', path: '/Comments/Delete', method: 'GET_405', expect: 'routed' },
    // Calendar — Phase 3D-5, the final CBA004 owner. Unlike Announcements/Comments/Star, ALL three
    // mutating actions here DO carry [ValidateAntiForgeryToken], so a token-less POST is refused at
    // the filter before the handler and is a genuine denial proof. Save/Delete are the two baselined
    // entries; SaveSchedule is additionally permission-guarded via IBusinessContextAccessor +
    // ICalendarAccessService.CanEventAsync, which is why it never appears in the baseline.
    { area: 'Calendar', path: '/Calendar',              method: 'GET', expect: 'allowed' },
    { area: 'Calendar', path: '/Calendar/Index',        method: 'GET', expect: 'allowed' },
    { area: 'Calendar', path: '/Calendar/Timeline',     method: 'GET', expect: 'allowed' },
    { area: 'Calendar', path: '/Calendar/ResourceView', method: 'GET', expect: 'allowed' },
    { area: 'Calendar', path: '/Calendar/Upcoming',     method: 'GET', expect: 'allowed' },
    { area: 'Calendar', path: '/Calendar/Save',         method: 'POST_NOCSRF', expect: 'denied' },
    { area: 'Calendar', path: '/Calendar/Delete',       method: 'POST_NOCSRF', expect: 'denied' },
    { area: 'Calendar', path: '/Calendar/SaveSchedule', method: 'POST_NOCSRF', expect: 'denied' },
    // Reporting — Phase 3F-1. The Report Center APIs are [Authorize] for AUTHENTICATION only; the
    // authorization is IN THE BODY via IReportAuthorizationService, which is why these count as
    // in-body protected rather than attribute-protected. They carry NO anti-forgery token (they are
    // JSON APIs driven by fetch), so a POST as an authorized admin would really write. None is
    // POSTed here: GET_405 proves the route exists with the handler never entered.
    { area: 'Reporting', path: '/Reports',                  method: 'GET', expect: 'allowed' },
    { area: 'Reporting', path: '/Reports/Index',            method: 'GET', expect: 'allowed' },
    { area: 'Reporting', path: '/api/reports/catalog',      method: 'GET', expect: 'allowed' },
    { area: 'Reporting', path: '/api/reports/categories',   method: 'GET', expect: 'allowed' },
    { area: 'Reporting', path: '/api/reports/saved',                 method: 'GET_405', expect: 'routed' },
    { area: 'Reporting', path: '/api/reports/favorites',              method: 'GET_405', expect: 'routed' },
    { area: 'Reporting', path: '/api/reports/favorites/reorder',      method: 'GET_405', expect: 'routed' },
    { area: 'Reporting', path: '/api/reports/saved/987654321/fork',    method: 'GET_405', expect: 'routed' },
    { area: 'Reporting', path: '/api/reports/saved/987654321/default', method: 'GET_405', expect: 'routed' },
    // Workspace — Phase 3F-2. The controller is thin by construction and has NO mutating endpoint at
    // all (its mark-as-read handover is deliberately withdrawn), so every probe here is a read. The
    // Reports surface additionally exercises IWorkspaceReportSource -> ReportingWorkspaceSource, the
    // adapter the Reporting phase deferred until these contracts landed.
    { area: 'Workspace', path: '/Workspace',               method: 'GET', expect: 'allowed' },
    { area: 'Workspace', path: '/Workspace/Index',         method: 'GET', expect: 'allowed' },
    { area: 'Workspace', path: '/Workspace/Agenda',        method: 'GET', expect: 'allowed' },
    { area: 'Workspace', path: '/Workspace/Reports',       method: 'GET', expect: 'allowed' },
    { area: 'Workspace', path: '/Workspace/Notifications', method: 'GET', expect: 'allowed' },
    { area: 'Workspace', path: '/Workspace/Mentions',      method: 'GET', expect: 'allowed' },
    // Business Events — Phase 3F-3. The controller carries [PlatformOps] at CLASS level, so every
    // action is attribute-protected; as admin the reads are reachable. Retry is the single mutating
    // action and it DOES carry [ValidateAntiForgeryToken], so a token-less POST is refused at the
    // filter before the handler — a real denial proof. Details is omitted deliberately: a
    // non-existent id answers 404, which this tool treats as a mistyped route rather than a boundary.
    { area: 'BusinessEvents', path: '/BusinessEventMonitor',         method: 'GET', expect: 'allowed' },
    { area: 'BusinessEvents', path: '/BusinessEventMonitor/Index',   method: 'GET', expect: 'allowed' },
    { area: 'BusinessEvents', path: '/BusinessEventMonitor/Runtime', method: 'GET', expect: 'allowed' },
    { area: 'BusinessEvents', path: '/BusinessEventMonitor/Rows',    method: 'GET', expect: 'allowed' },
    { area: 'BusinessEvents', path: '/BusinessEventMonitor/Retry',   method: 'POST_NOCSRF', expect: 'denied' },
    // Platform Grants + Timeline — Phase 3F-4. Both are [Authorize]/[SessionValidation] for
    // AUTHENTICATION; the authorization is IN THE BODY — PlatformGrantWriter resolves an authority
    // and refuses fail-closed with an audited denial plus a privilege CEILING check, and the timeline
    // throws PlatformAccessDeniedException which the controller turns into Forbid.
    //
    // Create is POST on the SAME path as List, so its routing is established by the List probe; it is
    // never POSTed because [ApiController] takes a JSON body with no anti-forgery gate, so a POST as an
    // authorized admin would really write a grant. revoke and validity have their own POST-only paths,
    // so GET_405 proves those exist with the handler never entered. Get(id) is omitted: a non-existent
    // id answers a correct 404, which this tool reads as a mistyped route rather than a boundary.
    { area: 'Platform', path: '/api/platform/grants?companyId=1',                     method: 'GET', expect: 'allowed' },
    { area: 'Platform', path: '/api/platform/grants/987654321/revoke',                method: 'GET_405', expect: 'routed' },
    { area: 'Platform', path: '/api/platform/grants/987654321/validity',              method: 'GET_405', expect: 'routed' },
    { area: 'Platform', path: '/PlatformTimeline/List?entityType=SalesInvoice&entityId=987654321', method: 'GET', expect: 'allowed' },
    // Tasks Ecosystem — Phase 3F-5A, the final 11 mutating endpoints. EVERY new mutating action
    // carries [ValidateAntiForgeryToken] (24 attributes for 24 mutating actions), so a token-less POST
    // is refused at the filter before the handler — a genuine denial proof, and nothing can be written.
    { area: 'TasksEco', path: '/Tasks/Board',        method: 'GET', expect: 'allowed' },
    { area: 'TasksEco', path: '/Tasks/Templates',    method: 'GET', expect: 'allowed' },
    { area: 'TasksEco', path: '/Tasks/BoardMove',        method: 'POST_NOCSRF', expect: 'denied' },
    { area: 'TasksEco', path: '/Tasks/ChecklistAdd',     method: 'POST_NOCSRF', expect: 'denied' },
    { area: 'TasksEco', path: '/Tasks/ChecklistRemove',  method: 'POST_NOCSRF', expect: 'denied' },
    { area: 'TasksEco', path: '/Tasks/ChecklistReorder', method: 'POST_NOCSRF', expect: 'denied' },
    { area: 'TasksEco', path: '/Tasks/ChecklistToggle',  method: 'POST_NOCSRF', expect: 'denied' },
    { area: 'TasksEco', path: '/Tasks/DependencyAdd',    method: 'POST_NOCSRF', expect: 'denied' },
    { area: 'TasksEco', path: '/Tasks/DependencyRemove', method: 'POST_NOCSRF', expect: 'denied' },
    { area: 'TasksEco', path: '/Tasks/TaskCommentAdd',   method: 'POST_NOCSRF', expect: 'denied' },
    { area: 'TasksEco', path: '/Tasks/TemplateApply',    method: 'POST_NOCSRF', expect: 'denied' },
    { area: 'TasksEco', path: '/Tasks/TemplateSave',     method: 'POST_NOCSRF', expect: 'denied' },
    { area: 'TasksEco', path: '/Tasks/TemplateSetActive',method: 'POST_NOCSRF', expect: 'denied' },
    // UAT — Phase 3F-5 retry, the final inventory owner. [AllowAnonymous] + [DevOnly] + a hardcoded
    // key; the analyzer counts ZERO mutating endpoints here because all seven actions are [HttpGet].
    // prime / provision / seed / cleanup are GET but OPERATIONALLY mutating. So is preflight: despite
    // the name, PreflightAsync contains two write operations. CountsAsync is the only genuinely
    // read-only one. NO probe here supplies the key, so [ApiController] answers 400 on the missing
    // required parameter and NO handler body is entered for any of them - route existence proven
    // without the risk of running a seed.
    { area: 'UAT', path: '/api/uat/counts',    method: 'GET', expect: 'denied' },
    { area: 'UAT', path: '/api/uat/preflight', method: 'GET', expect: 'denied' },
];

function classify(status) {
    if (status === 401 || status === 403) return 'denied';
    if (status === 302 || status === 301) return 'redirected';   // MVC deny pattern
    if (status >= 200 && status < 300) return 'allowed';
    if (status === 404) return 'notfound';
    return `http-${status}`;
}

async function main() {
    if (!USER || !PASS) {
        console.error('NOT_AUTHENTICATED: set UI_USER and UI_PASS. Without them this probe would ' +
                      'test the login page and call every boundary "denied" — a green run that proves nothing.');
        process.exit(2);
    }

    mkdirSync(SHOTS, { recursive: true });
    mkdirSync(PROFILE, { recursive: true });

    const context = await chromium.launchPersistentContext(PROFILE, {
        headless: false,
        viewport: { width: 1680, height: 1050 },
        ignoreHTTPSErrors: true,          // local dev certificate
    });

    const page = context.pages()[0] ?? await context.newPage();

    // Every response the session sees, keyed by URL — this is the evidence, not the DOM.
    const seen = [];
    page.on('response', r => seen.push({ url: r.url(), status: r.status(), method: r.request().method() }));

    console.log(`\nAUTHORIZATION PROBE  ->  ${BASE}`);
    console.log('sign-in');
    await page.goto(`${BASE}/Account/Login`, { waitUntil: 'domcontentloaded' });
    await page.fill('input[name="UserName"], input[name="Email"], input[type="email"]', USER);
    await page.fill('input[name="Password"], input[type="password"]', PASS);

    // waitForNavigation, NOT waitForLoadState. The first version used waitForLoadState, which
    // resolves against the page ALREADY loaded — so it returned before the POST had gone anywhere,
    // read the still-unchanged URL, and reported NOT_AUTHENTICATED on a sign-in that worked.
    await Promise.all([
        page.waitForNavigation({ waitUntil: 'domcontentloaded' }).catch(() => null),
        page.click('button[type="submit"], input[type="submit"]'),
    ]);

    if (/\/Account\/Login/i.test(page.url())) {
        await page.screenshot({ path: join(SHOTS, 'login-failed.png') });
        console.error('NOT_AUTHENTICATED: still on the login page after submitting. Refusing to continue — ' +
                      'a probe that measures the login page would report every endpoint as correctly denied.');
        await context.close();
        process.exit(2);
    }
    console.log(`  signed in as ${USER}  ->  ${new URL(page.url()).pathname}`);

    // Sign-in lands on /Portal/Choose. That page is not decoration: IRequestCompanyResolver reads
    // the company the session settled on, so probing before choosing measures an UNRESOLVED
    // company — every endpoint would refuse and the run would look like a working boundary.
    if (/\/Portal\/Choose/i.test(page.url())) {
        const entry = page.locator('a[href*="/Portal/"], form[action*="/Portal/"] button, .portal-card a').first();
        if (await entry.count() > 0) {
            await Promise.all([
                page.waitForNavigation({ waitUntil: 'domcontentloaded' }).catch(() => null),
                entry.click(),
            ]);
            console.log(`  portal selected     ->  ${new URL(page.url()).pathname}`);
        } else {
            console.log('  WARNING: on /Portal/Choose with no selectable portal — company may stay unresolved');
        }
    }
    console.log('');

    console.log('probes');
    for (const p of PROBES) {
        const url = `${BASE}${p.path}`;
        const name = `${p.area} ${p.method} ${p.path}`;
        let status;
        try {
            if (p.method === 'GET') {
                status = (await page.request.get(url, { failOnStatusCode: false, maxRedirects: 0 })).status();
            } else if (p.method === 'POST_NOCSRF') {
                // No anti-forgery token, and the id points at nothing. Two independent reasons this
                // cannot write: the filter rejects before the handler, and there is no such row.
                status = (await page.request.post(url, {
                    failOnStatusCode: false,
                    maxRedirects: 0,
                    form: { id: PROBE_ID, name: '', taxNo: '' },
                })).status();
            } else if (p.method === 'GET_405') {
                // The action is [HttpPost] and has NO anti-forgery attribute, so a POST would
                // actually execute and write. A GET cannot reach the handler: MVC answers 405
                // from routing, which proves the route exists and nothing ran.
                status = (await page.request.get(url, { failOnStatusCode: false, maxRedirects: 0 })).status();
            } else {
                fail(name, `unsupported probe method ${p.method} — refusing to guess`);
                continue;
            }
        } catch (e) {
            fail(name, `request threw: ${e.message.split('\n')[0]}`);
            continue;
        }

        const verdict = classify(status);
        const detail = `HTTP ${status} (${verdict})` +
            (p.method === 'POST_NOCSRF' ? ` [no anti-forgery token, id ${PROBE_ID} matches nothing]` : '');

        // A 404 is never evidence about authorization — it means the route does not exist, which is
        // almost always a typo in THIS file. Called out separately so a mistyped path can never be
        // read as "correctly denied".
        if (verdict === 'notfound') {
            fail(name, `${detail} — route does not exist; fix the probe, this says nothing about the boundary`);
            continue;
        }

        // A 400 from the anti-forgery filter is a refusal — the request never reached a handler.
        const refused = verdict === 'denied' || verdict === 'redirected' || status === 400;

        if (p.expect === 'routed') {
            status === 405
                ? pass(name, 'HTTP 405 — [HttpPost] route exists, handler NOT invoked (never POSTed: this action has no anti-forgery guard, so a POST would really write)')
                : fail(name, `${detail} — expected 405; anything else means the route shape changed`);
        } else if (p.expect === 'denied') {
            refused ? pass(name, detail)
                    : fail(name, `${detail} — expected a refusal before any handler ran`);
        } else {
            (verdict === 'allowed' || verdict === 'redirected')
                ? pass(name, detail)
                : fail(name, `${detail} — expected this to be reachable`);
        }
    }

    await page.screenshot({ path: join(SHOTS, 'session.png'), fullPage: false });
    writeFileSync(join(SHOTS, 'responses.json'), JSON.stringify(seen, null, 2));

    // ---- what this run did NOT establish -----------------------------------------------------
    skip('unprivileged DENY on mutating endpoints',
         'THE test that matters, and it is not run: the only identity available is an administrator, for ' +
         'whom authorization correctly ALLOWS. Proving a boundary refuses needs a low-privilege user. ' +
         'Running it as admin would not test the boundary — it would write.');
    skip('authorized-ALLOW paths on mutating endpoints',
         'deliberately never sent. VendorQuickAdd and CustomerQuickAdd are CREATE endpoints, so a ' +
         'permitted POST writes a real vendor or customer; no non-existent id can make that safe.');
    skip('ProjectController PERMISSION boundary (GateAsync)',
         'the 8 Project POSTs above were refused by the ANTI-FORGERY filter (400), which runs before ' +
         'GateAsync. So this run proves those routes reject a token-less request — it does NOT prove ' +
         'ProjectsAccessService denies a user lacking billing/budget-manage. That needs a low-privilege ' +
         'identity, and asking for it as admin would write.');
    skip('POS PERMISSION boundary (PosGateAsync) and branch scoping',
         'the POS POSTs above are refused by the ANTI-FORGERY filter, before PosGateAsync runs. So this ' +
         'proves the routes reject a token-less request; it does NOT prove PosAccessService denies a user ' +
         'whose POS role belongs to a DIFFERENT branch — the branch check (target.BranchId != pos.BranchId) ' +
         'is the interesting one and needs a second identity.');
    skip('Pos/CustomerQuickAdd',
         'never posted to: it is an ApiPerm CREATE endpoint, so a permitted POST writes a real customer. ' +
         'Same reason VendorQuickAdd is excluded.');
    skip('Admin/SaveSalaryPolicy',
         'never posted to. It has [ApiPerm(Hr, payroll-manage)] and NO anti-forgery token, so a token-less ' +
         'POST would reach the permission filter — which an administrator passes. The request would then ' +
         'write a salary policy. The only safe test is a user lacking payroll-manage.');
    skip('Admin PERMISSION boundary (HrGateAsync) and subject scoping',
         'the four Admin POSTs above were refused by ANTI-FORGERY, before HrGateAsync. So this does not ' +
         'prove HrAccessService denies a user without leave-manage/payroll-manage, nor that ' +
         'PermissionTarget.ForSubjectEmployee stops an HR officer acting on an employee outside their scope.');
    skip('ProjectController ungated-but-baselined actions',
         'SaveProject, DeleteProject, SaveProgress, ConfirmProgress, DeleteProgress, SaveActivityType and ' +
         'DeleteActivityType carry no gate; they are declared debt in authorization-baseline.json, not ' +
         'fixed here. Listed so "CBA001 = 0" is not read as "every mutating action is authorized".');
    skip('per-role matrix (viewer vs member vs manager)',
         'needs seeded role fixtures; this run uses one signed-in identity only.');
    skip('JWT-authenticated /api/acc/* behaviour',
         'the API takes a bearer token, so a browser session can only prove it REFUSES cookies. The ' +
         'authorized side — that AccountingApiAuthorization allows read and denies an unknown action — ' +
         'needs a token this probe does not mint.');

    console.log('\nNOT EXERCISED');
    for (const s of skipped) console.log(`  SKIP  ${s.name}\n        ${s.why}`);

    const failed = results.filter(r => !r.ok).length;
    console.log(`\nRESULT: ${results.length - failed} passed, ${failed} failed, ${skipped.length} not exercised`);
    console.log(`evidence: ${SHOTS}`);

    await context.close();
    process.exit(failed === 0 ? 0 : 1);
}

main().catch(e => { console.error('PROBE ERROR:', e); process.exit(3); });
