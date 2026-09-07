// ============================================================================================
// REPORT STUDIO — "I cannot import the image" PROBE.
//
// Reproduces the reported flow EXACTLY, in a VISIBLE browser, and reports what the SERVER
// answered at every step rather than what the screen appeared to do:
//
//   1. sign in through the ordinary login form
//   2. open /Reports/Studio in Arabic (the culture the report was made in)
//   3. GET  /api/reports/studio/assets            — does the picker have anything to offer?
//   4. GET  /api/reports/studio/assets/{id}       — do the BYTES behind each row still exist?
//   5. click the توقيع (signature) tool, open its properties, count the الصورة options
//   6. is the رفع (upload) control REACHABLE without scrolling? (discoverability, not a bug)
//   7. POST /api/reports/studio/assets            — a real upload, and what came back
//   8. pick the uploaded image on the signature element — does the canvas <img> actually paint?
//   9. upload from the element's OWN Picture row, and by DOUBLE-CLICKING the element
//  10. is the placed picture actually INSIDE its frame, measured in pixels?
//  11. can an element be moved BETWEEN bands — by dragging, and from the properties panel?
//  12. can a band be made taller or shorter by dragging its bottom edge?
//  13. Clear and New — do they ask first, and does Clear survive Ctrl+Z while New does not?
//
// IT WRITES ONCE, DELIBERATELY. Step 7 is an upload; there is no way to test an upload without
// uploading. It sends a 1×1 PNG titled `probe-*.png` so the row is identifiable, and step 9
// soft-deletes it through the product's own delete endpoint. Both are reported, not implied.
//
//   node tools/ui-conformance/studio-image-import-probe.mjs --base http://localhost:5200 \
//        --user dev.superadmin --password <chosen at call time>
//
//   --headless   run without a window (default: a VISIBLE window, slowed down to be watchable)
//   --keep       skip the soft-delete in step 9
// ============================================================================================

import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const HERE = dirname(fileURLToPath(import.meta.url));
const { chromium } = createRequire(import.meta.url)(join(HERE, 'node_modules', 'playwright'));

const arg = (name, fallback) => {
  const i = process.argv.indexOf('--' + name);
  return i >= 0 && process.argv[i + 1] && !process.argv[i + 1].startsWith('--')
    ? process.argv[i + 1] : fallback;
};
const flag = name => process.argv.includes('--' + name);

const BASE = arg('base', 'http://localhost:5200').replace(/\/+$/, '');
const USER = arg('user', 'dev.superadmin');
const PASS = arg('password', '');
const OUT = arg('out', join(HERE, 'artifacts', 'studio-image'));
const HEADLESS = flag('headless');

if (!PASS) {
  console.error('FAIL: --password is required; it is never stored in this repository.');
  process.exit(2);
}

mkdirSync(OUT, { recursive: true });

const steps = [];
const record = (name, ok, detail) => {
  steps.push({ name, ok, detail: detail ?? '' });
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? '  — ' + detail : ''}`);
};
const note = (name, detail) => {
  steps.push({ name, ok: null, detail: detail ?? '' });
  console.log(`INFO  ${name}${detail ? '  — ' + detail : ''}`);
};

// A TINY SVG THAT IS UNIQUE TO THIS RUN, and the uniqueness is the point rather than a detail.
//
// The service de-duplicates by SHA-256 of the bytes: identical bytes reuse the existing row and write
// nothing. So a probe that uploads the same fixed image every time silently stops testing the upload
// path after its first run — the second run's "PASS" is a row created minutes earlier. The timestamp
// in the comment makes every run's bytes new, so every run really does exercise a create.
const STAMP = new Date().toISOString();
const PROBE_SVG = Buffer.from(
  `<svg xmlns="http://www.w3.org/2000/svg" width="24" height="12"><!-- ${STAMP} -->` +
  `<rect width="24" height="12" fill="#1b6"/></svg>`, 'utf8');
const PROBE_NAME = 'probe-studio-image.svg';
const PROBE_FILE = join(OUT, PROBE_NAME);
writeFileSync(PROBE_FILE, PROBE_SVG);

const responses = [];

// Held at module scope so the CRASH path can still clean up: a run that dies half-way used to leave its
// uploaded rows behind, and the next run then measured a picker full of the previous run's debris.
let livePage = null;

const cleanup = async page => {
  if (!page || flag('keep')) { note('probe rows KEPT', flag('keep') ? '--keep was passed' : 'no page'); return; }
  try {
    const del = await page.evaluate(async () => {
      const r0 = await fetch('/api/reports/studio/assets');
      const d = await r0.json();
      const mine = (d.assets || []).filter(a => (a.title || a.fileName || '').startsWith('probe-'));
      const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value ?? '';
      const out = [];
      for (const a of mine) {
        const r = await fetch('/api/reports/studio/assets/' + a.id + '/delete',
          { method: 'POST', headers: { RequestVerificationToken: token } });
        out.push({ id: a.id, status: r.status });
      }
      return out;
    });
    note('probe rows soft-deleted', JSON.stringify(del));
  } catch (e) {
    note('probe rows COULD NOT be cleaned up', String(e.message).slice(0, 120));
  }
};
const shot = async (page, name) => {
  const file = join(OUT, name + '.png');
  await page.screenshot({ path: file, fullPage: false });
  return file;
};

const run = async () => {
  const browser = await chromium.launch({
    headless: HEADLESS,
    slowMo: HEADLESS ? 0 : 350,   // watchable, on purpose
    args: ['--ignore-certificate-errors'],
  });
  const context = await browser.newContext({
    viewport: { width: 1680, height: 1000 },
    locale: 'ar-EG',
    ignoreHTTPSErrors: true,
  });
  const page = await context.newPage();
  livePage = page;

  const errors = [];
  page.on('pageerror', e => errors.push('pageerror: ' + String(e).slice(0, 160)));
  page.on('console', m => { if (m.type() === 'error') errors.push('console: ' + m.text().slice(0, 160)); });
  page.on('response', r => {
    const u = r.url();
    if (!u.includes('/api/reports/studio')) return;
    responses.push({ status: r.status(), method: r.request().method(), url: u.replace(BASE, '') });
  });

  // ---- 1. sign in ---------------------------------------------------------------------------
  await page.goto(BASE + '/Account/Login', { waitUntil: 'domcontentloaded' });
  await page.fill('input[name="UserName"], input[name="Username"], input[name="Email"]', USER);
  await page.fill('input[name="Password"]', PASS);
  // The FORM's OWN submit, not the first submit-looking button on the page: the social sign-in
  // buttons sit above it, and a generic selector picks one of those and never posts. Found the
  // hard way — the first run reported a failed sign-in for credentials that were in fact correct.
  await page.locator('#loginForm button[type="submit"], form button[type="submit"]').first().click();
  // WAIT FOR THE URL TO CHANGE, not for a fixed number of seconds. A cold-started instance takes longer
  // than any timeout worth hard-coding, and a fixed wait reported a failed sign-in for a session that had
  // in fact signed in — the screenshot showed the portal while the probe printed FAIL.
  await page.waitForURL(u => !u.toString().includes('/Account/Login'), { timeout: 45000 }).catch(() => {});
  const signedIn = !page.url().includes('/Account/Login');
  record(`sign in as ${USER}`, signedIn, page.url());
  if (!signedIn) { await shot(page, '00-login-failed'); await browser.close(); return finish(); }

  // Arabic, the way the product switches language.
  await context.addCookies([{ name: '.AspNetCore.Culture', value: 'c%3Dar%7Cuic%3Dar', url: BASE }]);

  // ---- 2. the designer ----------------------------------------------------------------------
  await page.goto(BASE + '/Reports/Studio', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(1500);
  const hasCanvas = await page.locator('#cbd-page').count() > 0;
  record('designer renders', hasCanvas, hasCanvas ? '' : 'no #cbd-page — no datasets for this persona?');
  await shot(page, '01-designer');
  if (!hasCanvas) { await browser.close(); return finish(); }

  // ---- 3. what the picker was given ---------------------------------------------------------
  const list = await page.evaluate(async () => {
    try {
      const r = await fetch('/api/reports/studio/assets', { headers: { Accept: 'application/json' } });
      const text = await r.text();
      let json = null; try { json = JSON.parse(text); } catch { /* not JSON — that is the finding */ }
      return { status: r.status, json, body: text.slice(0, 200) };
    } catch (e) { return { status: -1, json: null, body: String(e).slice(0, 200) }; }
  });
  record('GET /assets answers 200 JSON', list.status === 200 && !!list.json,
    `status ${list.status}` + (list.json ? `, ${(list.json.assets || []).length} asset(s)` : `, body: ${list.body}`));

  // ---- 4. do the BYTES still exist for each row? --------------------------------------------
  for (const a of (list.json?.assets ?? [])) {
    const b = await page.evaluate(async id => {
      try {
        const r = await fetch('/api/reports/studio/assets/' + id);
        return { status: r.status, len: r.ok ? (await r.blob()).size : 0 };
      } catch (e) { return { status: -1, len: 0, error: String(e).slice(0, 120) }; }
    }, a.id);
    record(`GET /assets/${a.id} serves bytes ("${a.title || a.fileName}")`, b.status === 200 && b.len > 0,
      `status ${b.status}, ${b.len} byte(s)` + (b.status === 404 ? ' — ROW EXISTS, FILE GONE' : ''));
  }

  // ---- 5. the reported flow: add a signature, look at its الصورة picker --------------------
  await page.click('[data-tool="sign"]');
  await page.waitForTimeout(500);
  const placed = await page.locator('#cbd-page [data-el]').count();
  record('signature element placed on the page', placed > 0, `${placed} element(s) on the canvas`);

  await page.locator('#cbd-page [data-el]').last().click();
  await page.waitForTimeout(400);
  const picker = await page.evaluate(() => {
    const s = document.getElementById('p-asset');
    return s ? { present: true, options: Array.from(s.options).map(o => o.text) } : { present: false, options: [] };
  });
  record('الصورة (picture) picker is present', picker.present, picker.options.join(' | '));
  record('الصورة picker offers at least one image', picker.options.length > 1,
    `${Math.max(0, picker.options.length - 1)} image(s) besides "— بلا —"`);
  await shot(page, '02-signature-selected');

  // ---- 6. can the user REACH the upload control without hunting for it? --------------------
  const reach = await page.evaluate(() => {
    const input = document.getElementById('cbd-asset-file');
    if (!input) return { present: false };
    const label = input.closest('label');
    const box = label.getBoundingClientRect();
    const body = input.closest('.cbd-rail-body');
    const rail = body.getBoundingClientRect();
    return {
      present: true,
      inViewport: box.top >= 0 && box.bottom <= window.innerHeight,
      insideRailView: box.top >= rail.top && box.bottom <= rail.bottom,
      scrollNeeded: Math.max(0, Math.round(body.scrollHeight - body.clientHeight)),
    };
  });
  record('رفع (upload) control exists', reach.present, JSON.stringify(reach));
  note('upload control is below the fold',
    reach.present && !reach.insideRailView
      ? `the toolbox rail must be scrolled ~${reach.scrollNeeded}px to reveal it`
      : 'visible without scrolling');

  // ---- 7. the upload itself -----------------------------------------------------------------
  const before = await page.locator('#cbd-assets img').count();
  await page.evaluate(() => document.getElementById('cbd-asset-file').closest('label').scrollIntoView({ block: 'center' }));
  await page.setInputFiles('#cbd-asset-file', PROBE_FILE);
  await page.waitForTimeout(2500);

  const post = responses.filter(r => r.method === 'POST' && r.url.endsWith('/api/reports/studio/assets')).pop();
  record('POST /assets accepted the upload', !!post && post.status === 200,
    post ? `status ${post.status}` : 'NO REQUEST WAS SENT');

  const after = await page.evaluate(() => ({
    thumbs: Array.from(document.querySelectorAll('#cbd-assets img')).map(i => ({ src: i.getAttribute('src'), painted: i.naturalWidth > 0 })),
    error: document.getElementById('cbd-errors').className.includes('d-none')
      ? '' : document.getElementById('cbd-errors').textContent.trim().slice(0, 200),
  }));
  record('the new thumbnail appears in the picker', after.thumbs.length > before,
    `${after.thumbs.length} thumbnail(s), was ${before}`);

  // THE ROLE AND THE TITLE the designer SENT must be the role and title that came BACK. They are
  // multipart form fields, and [ApiController] infers [FromQuery] for simple types — so without an
  // explicit [FromForm] both are silently dropped and every image lands as role 0 with no title.
  const sentRole = await page.evaluate(() => document.getElementById('cbd-asset-role').value);
  // READ BACK FROM THE LIST, not from the POST's response body. Sniffing the body through
  // page.on('response') is flaky — Playwright cannot always hand it over after the fact, and when it
  // came back empty every later check silently measured the WRONG asset (it fell through to the oldest
  // broken row and reported five failures that had nothing to do with the upload).
  const stored = await page.evaluate(async name => {
    try {
      const r = await fetch('/api/reports/studio/assets');
      const d = await r.json();
      const mine = (d.assets || []).filter(a => (a.fileName || '') === name);
      mine.sort((x, y) => y.id - x.id);
      return mine[0] ?? null;
    } catch { return null; }
  }, PROBE_NAME);
  record('the ROLE chosen in the picker was stored', !!stored && String(stored.role) === String(sentRole),
    `sent role=${sentRole}, stored role=${stored ? stored.role : 'n/a'} (asset ${stored ? stored.id : '?'})`);
  record('the TITLE sent with the file was stored', !!stored && !!stored.title,
    `stored title=${JSON.stringify(stored ? stored.title : null)}`);
  if (stored) {
    const bytes = await page.evaluate(async id => {
      try {
        const r = await fetch('/api/reports/studio/assets/' + id);
        return { status: r.status, len: r.ok ? (await r.blob()).size : 0 };
      } catch (e) { return { status: -1, len: 0, error: String(e).slice(0, 120) }; }
    }, stored.id);
    record('the image JUST UPLOADED serves its bytes back', bytes.status === 200 && bytes.len > 0,
      `GET /assets/${stored.id} -> ${bytes.status}, ${bytes.len} byte(s)`);
  }
  record('every thumbnail actually PAINTS (bytes served)', after.thumbs.length > 0 && after.thumbs.every(t => t.painted),
    JSON.stringify(after.thumbs));
  if (after.error) note('the designer showed an error banner', after.error);
  await shot(page, '03-after-upload');

  // ---- 8. place it on the signature element -------------------------------------------------
  // Pick THE IMAGE THIS RUN UPLOADED, not simply the last option: the picker also lists rows left by
  // earlier runs and by other instances, and "the canvas did not paint" means nothing if the option
  // chosen was somebody else's orphaned row.
  const pickResult = await page.evaluate(async wantId => {
    const s = document.getElementById('p-asset');
    if (!s || s.options.length < 2) return { picked: false, reason: 'picker still empty' };
    const target = Array.from(s.options).find(o => String(o.value) === String(wantId))
                ?? s.options[s.options.length - 1];
    s.value = target.value;
    s.dispatchEvent(new Event('change', { bubbles: true }));
    await new Promise(r => setTimeout(r, 800));
    const img = document.querySelector('#cbd-page [data-el] img');
    return {
      picked: true,
      chose: target.text,
      chosenId: target.value,
      onCanvas: !!img,
      src: img ? img.getAttribute('src') : '',
      painted: img ? img.naturalWidth > 0 : false,
    };
  }, stored ? stored.id : null);
  record('the image can be assigned to the signature element', !!pickResult.picked, JSON.stringify(pickResult));
  record('the image PAINTS on the canvas', !!pickResult.painted, JSON.stringify(pickResult));
  await shot(page, '04-image-on-canvas');

  // ---- 8b. THE FLOW THAT WAS REPORTED: upload from the element's OWN Picture row -----------
  //
  // "I add a signature, and I cannot import the image." The upload control used to exist only at the
  // bottom of the toolbox rail, below the fold, so this row was a list with nothing in it and no way to
  // add anything. This asserts the button beside the picker, that it carries the element's role, and
  // that what comes back is placed on the element that asked for it.
  await page.click('[data-tool="sign"]');
  await page.waitForTimeout(400);
  await page.locator('#cbd-page [data-el]').last().click();
  await page.waitForTimeout(400);

  const hasRowButton = await page.locator('#p-asset-upload').count() === 1;
  record('the Picture row has its own upload button', hasRowButton,
    hasRowButton ? 'visible: ' + await page.locator('#p-asset-upload').isVisible() : 'absent');

  if (hasRowButton) {
    const second = join(OUT, 'probe-from-props.svg');
    writeFileSync(second, Buffer.from(
      `<svg xmlns="http://www.w3.org/2000/svg" width="40" height="18"><!-- ${STAMP} props -->` +
      `<rect width="40" height="18" fill="#26a"/></svg>`, 'utf8'));

    const [chooser] = await Promise.all([
      page.waitForEvent('filechooser'),
      page.click('#p-asset-upload'),
    ]);
    const roleCarried = await page.evaluate(() => document.getElementById('cbd-asset-role').value);
    record("the element's role is carried into the upload", roleCarried === '2',
      `signature element, role selector = ${roleCarried} (2 = signature)`);

    await chooser.setFiles(second);
    await page.waitForTimeout(4000);

    const placed2 = await page.evaluate(() => {
      const img = document.querySelector('#cbd-page [data-el] img');
      const sel = document.getElementById('p-asset');
      return {
        chose: sel ? sel.options[sel.selectedIndex].text : null,
        src: img ? img.getAttribute('src') : null,
        painted: img ? img.naturalWidth > 0 : false,
        error: document.getElementById('cbd-errors').className.includes('d-none')
          ? '' : document.getElementById('cbd-errors').textContent.trim().slice(0, 200),
      };
    });
    record('the uploaded image lands on the element that asked for it', !!placed2.painted,
      JSON.stringify(placed2));
    await shot(page, '05-upload-from-properties');
  }

  // ---- 8c. DOUBLE-CLICK AN IMAGE ELEMENT = import a picture for it ------------------------
  //
  // Asserted through the browser's real file dialog, because that is the only thing that proves the
  // gesture reached the file input. Note the two negative checks either side of it: a double-click on a
  // TEXT element must not open anything, and the detection must not be the browser's dblclick event —
  // the canvas re-renders on mousedown, so that event never fires here at all.
  await page.click('[data-tool="sign"]');
  await page.waitForTimeout(400);

  // A DELIBERATELY TALL image: 20 x 60, dropped into a 40 x 18mm signature frame. The aspect ratio is
  // the test. An image shaped like its frame fits by accident and proves nothing; this one only fits if
  // the frame actually governs the picture's size, which is asserted a few lines below.
  const dblFile = join(OUT, 'probe-doubleclick.svg');
  writeFileSync(dblFile, Buffer.from(
    `<svg xmlns="http://www.w3.org/2000/svg" width="20" height="60"><!-- ${STAMP} dbl -->` +
    `<rect width="20" height="60" fill="#b52"/></svg>`, 'utf8'));

  let dblChooser = null;
  try {
    [dblChooser] = await Promise.all([
      page.waitForEvent('filechooser', { timeout: 8000 }),
      page.locator('#cbd-page [data-el]').last().dblclick(),
    ]);
  } catch { dblChooser = null; }
  record('double-clicking an image element opens the import dialog', !!dblChooser,
    dblChooser ? 'file dialog opened' : 'no file dialog appeared within 8s');

  if (dblChooser) {
    const roleFromDbl = await page.evaluate(() => document.getElementById('cbd-asset-role').value);
    record("the double-clicked element's role is carried", roleFromDbl === '2',
      `signature element, role selector = ${roleFromDbl}`);
    await dblChooser.setFiles(dblFile);
    await page.waitForTimeout(4000);
    const dblPlaced = await page.evaluate(() => {
      const img = document.querySelector('#cbd-page [data-el] img');
      return { src: img ? img.getAttribute('src') : null, painted: img ? img.naturalWidth > 0 : false };
    });
    record('the double-click import lands on that element', !!dblPlaced.painted, JSON.stringify(dblPlaced));

    // THE PICTURE MUST STAY INSIDE ITS FRAME — measured, in pixels, against the element's own box.
    //
    // This is the regression that had the image hanging out of the bottom of its frame: the body span's
    // height was auto, so height:100% on the img was an indefinite percentage and resolved to auto, the
    // picture kept its intrinsic aspect ratio, and object-fit had no definite box to act on. Selecting
    // the element turns off the clip that was hiding it (the resize handles need overflow:visible), so
    // the spill became visible exactly when the element was selected — which is how it was reported.
    // MEASURED WITHOUT SELECTING IT, deliberately. Selection only made the fault VISIBLE — it turns off
    // the clip that was cropping the spill — while the fault itself is the picture's SIZE, which is the
    // same either way. Asserting the size needs no click, and a click here was fragile: the element
    // holding the picture is not the last one added by this point, and Playwright timed out on it.
    const fit = await page.evaluate(() => {
      const el = document.querySelector('#cbd-page [data-el] img');
      if (!el) return null;
      const box = el.closest('[data-el]');
      const e = box.getBoundingClientRect(), i = el.getBoundingClientRect();
      return {
        selected: box.classList.contains('sel'),
        frame: { w: Math.round(e.width), h: Math.round(e.height) },
        image: { w: Math.round(i.width), h: Math.round(i.height) },
        spillBottomPx: Math.round(i.bottom - e.bottom),
        spillRightPx: Math.round(i.right - e.right),
        objectFit: getComputedStyle(el).objectFit,
      };
    });
    record('a tall image stays inside its frame',
      !!fit && fit.spillBottomPx <= 1 && fit.spillRightPx <= 1, JSON.stringify(fit));
    await shot(page, '08-image-fit');
  }

  // A text element has no picture to import, so nothing may open.
  await page.click('[data-tool="text"]');
  await page.waitForTimeout(400);
  // BY ID, and clicked near its top-left rather than at its centre: "the last element in the DOM" is not
  // the one just added once several bands hold elements, and an element's centre can fall under the
  // band's resize grip, which leaves Playwright waiting for a point that never becomes hittable.
  const textId = await page.evaluate(() => {
    const el = document.querySelector('#cbd-content [data-el].sel');
    return el ? el.getAttribute('data-el') : null;
  });
  let strayDialog = false;
  page.once('filechooser', () => { strayDialog = true; });
  await page.locator('#cbd-content [data-el="' + textId + '"]')
           .dblclick({ position: { x: 6, y: 4 } });
  await page.waitForTimeout(1500);
  record('double-clicking a NON-image element opens nothing', !strayDialog,
    strayDialog ? 'a file dialog opened on a text element' : 'no dialog, as intended');

  // ---- 8c-2. MOVING AN ELEMENT BETWEEN BANDS ----------------------------------------------
  //
  // "I cannot add the image to the band marked in red." An element's band used to be fixed at creation:
  // dragging only changed x/y within its own band and the properties panel printed the band as text, so
  // a picture that landed in Detail could never reach the page header. Both ways in are asserted here.
  //
  // NOTE THE DRAG AIM. The band follows the element's own TOP-LEFT corner — that is where its Y is
  // measured from — not the cursor. Aiming the CURSOR at the band puts the top edge half an element
  // above it and lands in the band before, which is correct and worth stating in a test rather than
  // rediscovering.
  await page.click('[data-tool="image"]');
  await page.waitForTimeout(500);

  // TRACKED BY ID, not by "the selected one": undo deliberately clears the selection (the selected id
  // may no longer exist in the restored snapshot), so a `.sel` selector reports null after Ctrl+Z and
  // reads as the undo having failed. Element ids survive the JSON round-trip a snapshot is made of.
  const newId = await page.evaluate(() => {
    const el = document.querySelector('#cbd-content [data-el].sel');
    return el ? el.getAttribute('data-el') : null;
  });
  const bandOf = () => page.evaluate(id => {
    const el = id ? document.querySelector('#cbd-content [data-el="' + id + '"]') : null;
    const b = el ? el.closest('.cbd-band') : null;
    return b ? +b.getAttribute('data-band') : null;
  }, newId);

  const startBand = await bandOf();
  const elBox = await page.locator('#cbd-content [data-el="' + newId + '"]').boundingBox();
  const phBox = await page.locator("#cbd-content .cbd-band[data-band='1']").boundingBox();

  if (elBox && phBox) {
    await page.mouse.move(elBox.x + elBox.width / 2, elBox.y + elBox.height / 2);
    await page.mouse.down();
    await page.mouse.move(elBox.x + elBox.width / 2, phBox.y + elBox.height / 2 + 8, { steps: 12 });
    await page.mouse.up();
    await page.waitForTimeout(800);
    const dragged = await bandOf();
    record('dragging an element into another band moves it there', dragged === 1,
      `band ${startBand} -> ${dragged} (1 = page header)`);

    const bandPicker = await page.evaluate(() => {
      const s = document.getElementById('p-band');
      return s ? { present: true, value: s.value, options: Array.from(s.options).map(o => o.text) } : { present: false };
    });
    record('the properties panel offers the band as a CHOICE', !!bandPicker.present,
      JSON.stringify(bandPicker.options || []));

    if (bandPicker.present) {
      await page.selectOption('#p-band', '5');
      await page.waitForTimeout(800);
      const picked = await bandOf();
      record('the band picker moves the element', picked === 5, `-> band ${picked} (5 = page footer)`);

      await page.keyboard.press('Control+z');
      await page.waitForTimeout(700);
      const undone = await bandOf();
      record('Ctrl+Z undoes a band change', undone === 1, `-> band ${undone}, expected 1`);
    }
  }

  // ---- 8c-3. RESIZING A BAND BY DRAGGING ITS BOTTOM EDGE ----------------------------------
  //
  // The height was only ever a number box in the Bands tab. The grip writes to the same heightMm, so the
  // box is checked after every drag: if the two ever disagree, one of them is lying about the model.
  const bandState = kind => page.evaluate(k => {
    const b = document.querySelector('#cbd-content .cbd-band[data-band="' + k + '"]');
    const input = document.getElementById('bh-' + k);
    if (!b) return null;
    const grip = b.querySelector('.cbd-band-resize');
    return {
      px: Math.round(b.getBoundingClientRect().height),
      mm: input ? input.value : null,
      grip: !!grip,
      cursor: grip ? getComputedStyle(grip).cursor : null,
    };
  }, kind);

  const header0 = await bandState(1);
  record('the page header has a resize grip', !!header0 && header0.grip && header0.cursor === 'row-resize',
    JSON.stringify(header0));

  // A 0mm band deliberately has none — two overlapping splitters make the boundary ambiguous to grab.
  const zero = await bandState(2);
  record('a 0mm band has no grip', !!zero && zero.grip === false, JSON.stringify(zero));

  const grip1 = await page.locator("#cbd-content .cbd-band[data-band='1'] .cbd-band-resize").boundingBox();
  if (grip1) {
    await page.mouse.move(grip1.x + grip1.width / 2, grip1.y + grip1.height / 2);
    await page.mouse.down();
    await page.mouse.move(grip1.x + grip1.width / 2, grip1.y + grip1.height / 2 + 40, { steps: 10 });
    await page.mouse.up();
    await page.waitForTimeout(600);
    const grown = await bandState(1);
    record('dragging the edge down makes the band taller',
      !!grown && Number(grown.mm) > Number(header0.mm),
      `${header0.mm}mm -> ${grown.mm}mm (${header0.px}px -> ${grown.px}px)`);
    // The box and the canvas must describe the SAME band: same pixels-per-mm before and after, which is
    // only true if the number box moved with the drag rather than keeping a stale value.
    const ratioBefore = header0.px / Number(header0.mm);
    const ratioAfter = grown ? grown.px / Number(grown.mm) : 0;
    record("the Bands tab's number box agrees with the drag",
      !!grown && grown.mm !== header0.mm && Math.abs(ratioAfter - ratioBefore) < 0.15,
      `box reads ${grown.mm}mm; ${ratioBefore.toFixed(2)} vs ${ratioAfter.toFixed(2)} px/mm`);

    // Hard up: the floor exists so the gesture cannot delete its own handle at 0.
    const g2 = await page.locator("#cbd-content .cbd-band[data-band='1'] .cbd-band-resize").boundingBox();
    await page.mouse.move(g2.x + g2.width / 2, g2.y + g2.height / 2);
    await page.mouse.down();
    await page.mouse.move(g2.x + g2.width / 2, g2.y - 400, { steps: 12 });
    await page.mouse.up();
    await page.waitForTimeout(600);
    const floored = await bandState(1);
    record('dragging hard up floors the band and keeps its grip',
      !!floored && Number(floored.mm) === 2 && floored.grip === true, JSON.stringify(floored));

    await page.keyboard.press('Control+z');
    await page.waitForTimeout(600);
    await page.keyboard.press('Control+z');
    await page.waitForTimeout(600);
    const restored = await bandState(1);
    record('Ctrl+Z restores the band height', !!restored && restored.mm === header0.mm,
      `${floored ? floored.mm : '?'}mm -> ${restored ? restored.mm : '?'}mm, expected ${header0.mm}mm`);
    await shot(page, '09-band-resize');
  }

  // ---- 8d. CLEAR and NEW ------------------------------------------------------------------
  //
  // Both are destructive, so both must ASK first — and they must differ in what survives. CLEAR keeps
  // the name and is one undo step; NEW keeps nothing and empties the undo history.
  await page.fill('#cbd-name', 'probe report name');
  const beforeClear = await page.locator('#cbd-page [data-el]').count();

  await page.click('#cbd-clear');
  await page.waitForTimeout(900);
  const clearAsked = await page.locator('.swal2-container').count() > 0;
  record('Clear asks before emptying the page', clearAsked, clearAsked ? '' : 'no confirmation appeared');
  if (clearAsked) {
    await shot(page, '06-clear-confirm');
    await page.click('.swal2-confirm');
    await page.waitForTimeout(1200);
    const afterClear = await page.evaluate(() => ({
      els: document.querySelectorAll('#cbd-page [data-el]').length,
      name: document.getElementById('cbd-name').value,
      undo: !document.getElementById('cbd-undo').disabled,
    }));
    record('Clear empties the page and keeps the name',
      afterClear.els === 0 && afterClear.name === 'probe report name',
      `${beforeClear} element(s) -> ${afterClear.els}, name "${afterClear.name}"`);
    await page.keyboard.press('Control+z');
    await page.waitForTimeout(900);
    const undone = await page.locator('#cbd-page [data-el]').count();
    record('Ctrl+Z brings the cleared design back', undone === beforeClear,
      `${undone} element(s) restored, expected ${beforeClear}`);
  }

  await page.click('#cbd-new');
  await page.waitForTimeout(900);
  const newAsked = await page.locator('.swal2-container').count() > 0;
  record('New asks before discarding the draft', newAsked, newAsked ? '' : 'no confirmation appeared');
  if (newAsked) {
    await page.click('.swal2-confirm');
    await page.waitForTimeout(1500);
    const fresh = await page.evaluate(() => ({
      els: document.querySelectorAll('#cbd-page [data-el]').length,
      name: document.getElementById('cbd-name').value,
      dataset: document.getElementById('cbd-dataset').value,
      undo: !document.getElementById('cbd-undo').disabled,
    }));
    record('New leaves an empty, untitled, un-undoable page',
      fresh.els === 0 && fresh.name === '' && fresh.dataset === '' && fresh.undo === false,
      JSON.stringify(fresh));
    await shot(page, '07-after-new');
  }

  // ---- 9. clean up the rows this probe created ---------------------------------------------
  await cleanup(page);

  if (errors.length) note('javascript errors seen on the page', errors.slice(0, 6).join(' || '));
  await browser.close();
  return finish();
};

const finish = () => {
  writeFileSync(join(OUT, 'responses.json'),
    JSON.stringify({ base: BASE, steps, responses }, null, 2));
  const failed = steps.filter(s => s.ok === false);
  console.log('');
  console.log('---- every Report Studio response this session saw ----');
  for (const r of responses) console.log(`  ${String(r.status).padEnd(4)} ${r.method.padEnd(5)} ${r.url}`);
  console.log('');
  console.log(failed.length ? `${failed.length} FAILED:` : 'all checks passed');
  for (const f of failed) console.log(`  - ${f.name} — ${f.detail}`);
  console.log(`evidence: ${OUT}`);
  process.exit(failed.length ? 1 : 0);
};

run().catch(async e => {
  await cleanup(livePage);
  // A crash must still leave evidence and still print the steps that DID run — a probe that dies with a
  // stack trace and no report is indistinguishable from one that was never run.
  record('the probe ran to completion', false, 'crashed: ' + e.message);
  finish();
});
