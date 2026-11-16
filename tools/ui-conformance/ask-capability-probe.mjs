// Proves the LIVE assistant still answers what it can and REFUSES what it cannot.
//
// WHY THIS EXISTS, AND WHY IT TESTS REFUSALS AS HARD AS ANSWERS.
//
// The assistant's governed surfaces each compete for the same words. Adding one makes every older
// one slightly more likely to lose a question it used to win, and the model's vocabulary is
// retrained with each. Two real regressions on 2026-11-10 appeared only because something NEW was
// added, not because the old thing broke:
//
//   * a new item-categories intent took "اعلي الاصناف بيعا" from the sales surface
//   * a new POS intent took "كام ساعة اشتغل محمد" — an hours-worked question with no honest answer
//
// And one defect was invisible to every per-surface check that passed:
//
//   * ChainedIntentResolver treated the trained model's "no" as "no opinion", so the keyword
//     matcher below it answered "اعملي تقرير مبيعات سنوي" — a request to produce a DOCUMENT —
//     with a revenue total, because the word "مبيعات" is in the sales keywords. Every refusal the
//     model made could be overruled by one shared word. The other refusals passed by luck.
//
// A suite that only asserts that answers are correct cannot see a refusal that quietly became an
// answer. So the MUST-REFUSE list below is not a tidy extra: it is the half that catches the
// expensive failure, and a question listed there is as much a required behaviour as any answer.
//
//   node ask-capability-probe.mjs
//   UI_BASE_URL=https://localhost:7183 node ask-capability-probe.mjs
//
// Exits non-zero on any failure, and on being unable to reach the app — never silently "passes"
// because nothing could be checked.
//
// The conversation id starts with `probe:` on purpose. AskRefusalLog ignores that marker, so
// the six questions below — which MUST be refused — do not land in the refused-question log
// and rank themselves on /Inventory/AskGaps as things worth building. A verification tool
// that corrupts the diagnostic beside it is worse than no tool: on 2026-11-10 my own probes
// were 33 of the 37 entries on that screen.

import { chromium } from 'playwright';

const BASE = process.env.UI_BASE_URL ?? 'https://localhost:7183';

// want: 'answer' = must return rows · 'refuse' = must NOT answer, because the data cannot support
// an honest one. The note says why, so a future reader can judge whether it is still true.
const CASES = [
  ['اعلي الاصناف بيعا',                   'answer', 'sales surface, by item'],
  ['ايه الايراد لكل شهر',                 'answer', 'sales surface, by month'],
  ['طيب هات الاصناف المشتراه',            'answer', 'purchases surface'],
  ['من مين اشترينا',                      'answer', 'purchases, grouped by vendor not item'],
  ['ايه الوارد لكل مخزن',                 'answer', 'stock movements, by warehouse'],
  ['الحركات حسب النوع',                   'answer', 'stock movements, by type not a grand total'],
  ['اي ساعة اكتر زحمة',                   'answer', 'POS, by hour of day'],
  ['اي فرع بيبيع اكتر',                   'answer', 'POS, by branch'],
  ['كام موظف عندنا',                      'answer', 'workforce headcount'],
  ['الموظفين حسب الوظيفة',                'answer', 'workforce by job title'],
  ['كام رصيد مياه معدنية',                'answer', 'item stock, the oldest handler'],
  ['كام رصيد النقدية',                    'answer', 'cash balance'],
  ['ايه الفئات',                          'answer', 'item categories'],
  ['ايه الاصناف الراكدة',                 'answer', 'dormant items'],
  ['ايه المخازن اللي اقدر اشوفها',        'answer', 'warehouses the caller may see'],
  ['كام المستحق على العملاء',             'answer', 'open receivables'],
  ['المبيعات مقارنة بالشهر اللي فات',     'answer', 'period comparison, sales'],
  ['حركة المخزون مقارنة بالشهر اللي فات', 'answer', 'period comparison, stock'],

  // ---- GENERAL QUESTIONS, WHICH MUST NOT BE MISTAKEN FOR UNANSWERABLE ONES -------------------
  //
  // A surface refuses a question that names something it has never heard of. These name nothing
  // in particular: every word in them is the surface's own vocabulary or ordinary scaffolding, so
  // the surface's default answer is the honest one and a refusal here would be the fix overshooting.
  // "what did we buy" is the case that caught it — a declared keyword of the purchases surface
  // that composes no column at all, and so was refused by the first version of that rule.
  ['المبيعات',                            'answer', 'bare topic word: a total, not a refusal'],
  ['عايز اعرف المشتريات',                 'answer', 'scaffolding plus one topic word'],
  // NOT listed here: "ايه اخبار المخزون". I wrote it as a must-answer and it failed, but reading
  // the refusal showed it is the CATALOGUE saying the question reached no surface at all — a
  // routing gap, not the rule above overshooting. It is genuinely ambiguous between two surfaces
  // (stock BALANCES and stock MOVEMENTS) and neither claims it, so asserting an answer here would
  // have been asserting a capability nobody built. Read which refusal came back before believing
  // a failure: the two kinds mean opposite things.

  // ---- MUST REFUSE -------------------------------------------------------------------------
  ['متوسط الرواتب',          'refuse', 'a salary is recorded for 1 employee of 21'],
  ['كام ساعة اشتغل محمد',    'refuse', 'attendance is recorded for 3 employees of 21'],
  ['عايز كشف حضور',          'refuse', 'same: no attendance coverage'],
  ['مين اللي كسر الباب امبارح', 'refuse', 'not a business question at all'],
  ['اعملي تقرير مبيعات سنوي', 'refuse', 'a request to PRODUCE a document; Report Studio does that'],
  ['مين احسن مورد عندنا',    'refuse', 'no supplier rating exists in this product'],

  ['هات فاتورة رقم 12',       'refuse', 'one document: no surface names its invoices'],

  // COUNTED BEFORE DECIDING, AND THE COUNT SAID REFUSE. The Items table records no reorder point —
  // MinMarginPct is its only threshold — and demand cannot supply one either, with 28 active stock
  // days out of 312. Nothing records what a supplier charged before, so price movement has no
  // basis at all. Both would be easy to answer from a number I picked, and that is the reason not
  // to: a surface over an invented threshold answers confidently and wrongly forever.
  ['ايه الاصناف اللي قربت تخلص', 'refuse', 'no reorder point is recorded anywhere'],
  ['مين المورد اللي رفع اسعاره', 'refuse', 'no price history exists to compare against'],
];

// ---- ASKING ABOUT A PERSON BY NAME ------------------------------------------------------------
//
// The owner asked «بسأل عن احمد زكريا موظف» and got a headcount table; refusing it was the first
// fix, and he came back with «ليه كدا؟». He was right. A refusal was honest about the SURFACE and
// wrong about the PRODUCT: every employee's name is recorded, so "is there an employee called X"
// has a correct answer in both directions and neither of them is "I cannot".
//
// These are checked apart from the list above because both outcomes are ANSWERS and the pass
// condition is the row count, not ok:
//   found   — rows come back, and the person is shown with their photo
//   missing — zero rows, and the sentence says nobody by that name, which is the fact asked for
// A suite that only knew ok/refuse would score "nobody by that name" and "I don't understand"
// identically, and those are the two things this whole fix is about telling apart.
// ---- A NAME THE SURFACE DOES NOT KNOW MUST NOT BE DROPPED ------------------------------------
//
// «في عميل اسمه النيل؟» used to compose fine — "عميل" is a column — and return a RANKING of all
// customers, having silently thrown away the one word that said which customer. «في مورد اسمه مورد
// الطاقة؟» did the same and looked correct only because that vendor happened to sort first. An
// ignored name is worse than a refused one: the answer arrives dressed as the right answer.
//
// Each case states the ROW COUNT it expects, because that is what tells a filtered answer from an
// unfiltered one — both come back ok:true with figures that look equally plausible.
const NAMED = [
  ['في مورد اسمه مورد الطاقة ؟', 1,    'filtered to that vendor, not a ranking of all of them'],
  ['Nile Co sales',              1,    'the customer whose ARABIC name is destroyed in the data'],
  ['اعلي الاصناف بيعا',          null, 'carries no "could not find" note: اعلي is not a name'],
  ['ايه الايراد لكل شهر',        null, 'unchanged by any of this'],

  // ROUTING, not composing. These reached NO surface at all until the intent model was taught that
  // a topic word can be followed by a proper noun — every training example had been the bare topic
  // word. The surface could already answer them, so the capability existed with nothing able to
  // reach it, and the refusal looked like a missing feature rather than a missing training row.
  ['مبيعات مياه معدنية',         1,    'a named item: revenue for that item alone'],
  ['ايراد برجر دجاج',            1,    'another wording of the same shape'],
  ['في موظف اسمه احمد زكريا',    1,    'a named colleague, with no question mark'],

  // A NAMED STRETCH OF TIME. "مبيعات الشهر ده" is checked rather than "مبيعات امبارح" because
  // yesterday's answer depends on the clock: this data ends in August, so most days are empty and
  // the honest reply is a NoData refusal naming the date. That is correct behaviour and a moving
  // target for a suite, so the case that must always answer is the one with a window wide enough
  // to contain something. The empty-day behaviour is covered by the refusal list, not here.
  ['مبيعات الشهر ده',            null, 'this month: a date filter, not the revenue of all time'],
  ['المبيعات مقارنة بالشهر اللي فات', null,
   'the comparison path must not be stolen by the month filter, nor مقارنة read as a name'],

  // ARABIC PARTICLES GLUED TO A WORD. "ارباح" has always been a declared synonym of the margin
  // measure, and "الارباح كام" still answered REVENUE by item — the right surface, the wrong
  // measure, and nothing on screen to say so, because the ل of the article made a letter-boundary
  // test read the word as the middle of a longer one. These three are the same word under three
  // different particles; if one of them regresses the boundary has stopped understanding Arabic.
  ['الارباح كام',                null, 'ال — must answer MARGIN, not revenue'],
  ['والارباح ؟',                 null, 'و + ال'],
  ['بالمبيعات',                  null, 'ب + ال on a measure'],

  // SPENDING. A surface built because the question was being answered WRONGLY — "المصروفات حسب
  // النوع" used to reach stock movements and return movement types.
  ['اكبر بند مصروف',             null, 'expenses grouped by account'],
  ['المصروفات حسب مركز التكلفة', null, 'expenses by cost centre'],

  // ---- THE HOSPITAL ---------------------------------------------------------------------------
  //
  // The owner asked «جيب قائمة الاطباء» and then «طيب الادوية» and was refused both times, then
  // offered questions about revenue and stock. Six surfaces existed and none was the hospital.
  //
  // Each case below is also a guard on a mistake made while building them, and all three are the
  // same mistake: a word that names a VALUE must never be a synonym of the COLUMN, or the question
  // becomes one ABOUT that column and the surface ranks everything instead of filtering to the one
  // thing that was named. It cost "الاطباء الزائرين", "الادوية المخدرة" and — via the handler's
  // routing keywords, which are read as vocabulary too — "العيادات".
  // A COUNT THAT MOVES WITH THE DATA IS NOT AN ASSERTION. This said 8 and failed the same day,
  // because another session added practitioners. What the case is actually about is that the
  // answer is a LIST and not a single number, so that is what it now says.
  ['جيب قائمة الاطباء',  (n) => n > 1, 'a LIST of records, not one count'],
  ['كام طبيب عندنا',             1,    'asked how many: stays a number'],
  ['الاطباء الزائرين',           1,    'filtered to visiting, not a ranking of all engagements'],
  ['جيب قائمة العيادات',         null, 'the CLINICS, not the doctors sitting in them'],
  ['طيب الادوية',                null, 'inpatient medication orders by drug'],
  ['الادوية المخدرة',            1,    'filtered to the controlled ones'],

  // ---- THE HOSPITAL'S MONEY -------------------------------------------------------------------
  //
  // Twenty-six billing tables carrying 458 rows had no window at all: the assistant could say how
  // many doctors there are and not how much the place billed. Two surfaces, not one, because
  // BILLED and CLAIMED are different figures over different rows and answering one under the
  // other's name is the failure this layer exists to stop.
  //
  // Every case below is bilingual on purpose. Each dimension carries a search key holding the
  // stored English AND the Arabic rendering together, so a value can be named in either language
  // whatever the UI is set to. An English case that passes only because the UI is English would
  // prove nothing; the English block at the end of this file runs the same questions the other way.
  ['كام فوترنا الشهر ده',       null, 'billing surface, not the claims one'],
  ['فواتير حسب القسم',         null, 'billed, grouped by department'],
  ['فواتير الاشعة',             1,    'filtered to radiology, not a ranking of every category'],
  ['حالة المطالبات',           null, 'claims surface, by status'],
  ['المطالبات حسب جهة الدفع',  null, 'claims grouped by payer, which comes from the batch'],
  // A ROW COUNT THAT CANNOT TELL A FILTER FROM NO FILTER IS NOT AN ASSERTION.
  // «المطالبات المعتمدة» grouped by the total gives ONE row whether or not it filtered,
  // so it passed while answering about all 19 lines. Grouped by payer it gives 1 when the
  // filter held and 2 when it did not, because every accepted line belongs to one insurer.
  ['المطالبات المعتمدة حسب جهة الدفع', 1, 'filtered to accepted: one payer, not two'],

  // ---- THE CLINIC'S DAY -----------------------------------------------------------------------
  //
  // «جيب مواعيد اليوم» was typed into the assistant and refused with the catalogue offered
  // instead - 138 appointments in one table and no window onto them.
  //
  // The cancelled case is here because ONE RENDERING IS NOT ONE WORD. The status shows as
  // «ملغي» and the question says «الملغاة»; before the search key carried the spoken forms, it
  // answered about all 138 with a "could not find" opener while the English twin filtered
  // correctly to 4. Four is the discriminator: unfiltered it is one group over 138 rows.
  ['جيب مواعيد اليوم',          null, "today's appointments, not the whole book"],
  ['المواعيد الملغاة',        1,    'filtered to cancelled, by the spoken spelling'],
  ['المواعيد حسب الطبيب',     (n) => n > 1, 'grouped by practitioner'],
  ['اي ساعة فيها مواعيد اكتر', (n) => n > 1, 'the attached pronoun is scaffolding, not a value to hunt'],

  // ---- WHAT THE HOSPITAL IS TREATING ----------------------------------------------------------
  //
  // 85 diagnoses on 68 consultations, and «ايه اكتر الامراض» used to land on the appointments
  // surface, which knows when people came and nothing about why.
  //
  // THE SECONDARY CASE IS THE ONE THAT EARNS ITS KEEP. Grouped by diagnosis, an unfiltered answer
  // returns the full limit of fifteen; filtered to the additional diagnoses it returns the handful
  // of codes that are ever recorded alongside something else. A bound rather than an exact count,
  // because the exact one moves the first time anybody sees a patient — but fifteen and a handful
  // are far enough apart that the bound still tells a filter from no filter, which is the whole
  // point of asserting a row count here at all.
  ['ايه اكتر الامراض عندنا',   (n) => n > 1, 'the diagnoses ranked, not the appointment book'],
  ['حالات الربو',               1,            'filtered to asthma: one code, not a ranking of fifteen'],
  ['التشخيصات حسب القسم',      (n) => n > 1, 'grouped by department, which comes from the practitioner'],
  ['التشخيصات الثانوية',        (n) => n > 0 && n < 10,
   'filtered to the ADDITIONAL diagnoses: a handful of codes, not the full fifteen'],



];

const PEOPLE = [
  ['في موظف اسمه احمد زكريا ؟',   'found',   'he is on the roster'],
  ['بسأل عن احمد زكريا موظف',     'found',   "the owner's original wording"],
  ['في موظف اسمه زغلول الفيشاوي ؟', 'missing', 'nobody by that name: answered, not refused'],
];

const browser = await chromium.launch({ args: ['--ignore-certificate-errors'] });
const ctx = await browser.newContext({ ignoreHTTPSErrors: true, locale: 'ar-EG' });
const page = await ctx.newPage();
page.setDefaultTimeout(120000);

let pass = 0;
let total = CASES.length;
const failures = [];

try {
  await page.goto(`${BASE}/Account/Login`, { waitUntil: 'domcontentloaded' });
  await page.fill('input[name="UserName"]', process.env.UI_USER ?? 'Admin');
  await page.fill('input[type="password"]', process.env.UI_PASS ?? 'Admin@123');
  await Promise.all([
    page.waitForNavigation({ waitUntil: 'domcontentloaded' }).catch(() => {}),
    page.click('button[type="submit"], input[type="submit"]'),
  ]);
  if (page.url().includes('/Account/Login')) throw new Error('sign-in failed');

  // PIN THE SESSION LANGUAGE. Without this the Arabic cases above ran in whatever language the
  // account was last left in, and the suite's result changed with it: a person-by-name question
  // passed in one run and named the WRONG COLLEAGUE in the next, because the lookup was reading
  // the English spelling of the name. A probe whose outcome depends on state it does not set is
  // not measuring the product. The English block below sets it again, on purpose.
  await page.goto(`${BASE}/Account/SetLanguage?culture=ar&returnUrl=%2F`, { waitUntil: 'domcontentloaded' }).catch(() => {});
  await page.goto(`${BASE}/Inventory/StockBalances`, { waitUntil: 'domcontentloaded' });

  for (const [q, want, why] of CASES) {
    const r = await page.evaluate(async ([base, question]) => {
      const res = await fetch(`${base}/Inventory/Ask?question=${encodeURIComponent(question)}&c=probe:${Date.now()}`,
        { credentials: 'same-origin', headers: { Accept: 'application/json' } });
      const t = await res.text();
      let d = null;
      try { d = JSON.parse(t); } catch { /* not json */ }
      return { status: res.status, ok: d ? d.ok : null, rows: d && d.rows ? d.rows.length : 0,
               say: d ? `${d.spoken || d.message || ''}`.slice(0, 100) : t.slice(0, 100) };
    }, [BASE, q]);

    // An answer must carry rows. A refusal must not be an answer at all. An HTTP error is neither
    // and fails both — a 500 must never read as a successful refusal.
    const good = r.status === 200 && (want === 'answer' ? (r.ok === true && r.rows > 0) : r.ok === false);
    if (good) { pass++; }
    else { failures.push({ q, want, why, ...r }); }
  }
} catch (e) {
  console.error('NOT VERIFIED —', e?.message ?? e);
  await browser.close();
  process.exit(2);
}

// ---- A NAMED THING, RUN -----------------------------------------------------------------------
try {
  for (const [q, wantRows, why] of NAMED) {
    const r = await page.evaluate(async ([base, question]) => {
      const res = await fetch(`${base}/Inventory/Ask?question=${encodeURIComponent(question)}&c=probe:${Date.now()}`,
        { credentials: 'same-origin', headers: { Accept: 'application/json' } });
      const t = await res.text();
      let d = null;
      try { d = JSON.parse(t); } catch { /* not json */ }
      return { status: res.status, ok: d ? d.ok : null, rows: d && d.rows ? d.rows.length : 0,
               say: d ? `${d.spoken || ''}` : t.slice(0, 120) };
    }, [BASE, q]);

    // A "could not find" opener on a question that names nothing is a FALSE unknown — it staples a
    // doubt to a correct answer, and it means the scaffolding word list has a gap.
    const notFound = r.say.includes('مالقيتش') || r.say.includes('could not find');
    const good = r.status === 200 && r.ok === true && !notFound
      && (wantRows == null
          || (typeof wantRows === 'function' ? wantRows(r.rows) : r.rows === wantRows));
    if (good) { pass++; } else { failures.push({ q, want: `named:${wantRows ?? 'clean'}`, why, ...r, say: r.say.slice(0, 120) }); }
    total++;
  }
} catch (e) {
  console.error('NOT VERIFIED (named value) —', e?.message ?? e);
  await browser.close();
  process.exit(2);
}

// ---- ASKING ABOUT A PERSON BY NAME, RUN -------------------------------------------------------
try {
  for (const [q, want, why] of PEOPLE) {
    const r = await page.evaluate(async ([base, question]) => {
      const res = await fetch(`${base}/Inventory/Ask?question=${encodeURIComponent(question)}&c=probe:${Date.now()}`,
        { credentials: 'same-origin', headers: { Accept: 'application/json' } });
      const t = await res.text();
      let d = null;
      try { d = JSON.parse(t); } catch { /* not json */ }
      return { status: res.status, ok: d ? d.ok : null, rows: d && d.rows ? d.rows.length : 0,
               photo: d && d.rows && d.rows[0] ? !!d.rows[0].photo : false,
               say: d ? `${d.spoken || d.message || ''}`.slice(0, 130) : t.slice(0, 130) };
    }, [BASE, q]);

    // BOTH outcomes are answers. 'found' additionally requires the PHOTO, because an answer about
    // a colleague that shows a glyph instead of their face is the house rule's own failure case,
    // and it is invisible to a check that only counts rows.
    const good = r.status === 200 && r.ok === true
      && (want === 'found' ? (r.rows > 0 && r.photo) : r.rows === 0);
    if (good) { pass++; } else { failures.push({ q, want: `person:${want}`, why, ...r }); }
    total++;
  }
} catch (e) {
  console.error('NOT VERIFIED (person lookup) —', e?.message ?? e);
  await browser.close();
  process.exit(2);
}

// ---- IS IT STILL A CONVERSATION? ------------------------------------------------------------
//
// The owner's first complaint about this feature was not accuracy. It was «الشات لا يزال غير
// تفاعلي يعني انا اتكلم وهو يرد» — it answers, it does not converse. Three behaviours were built
// for that and all three run through the resolver chain, so a change there can silently take them
// away while every single-question case above still passes.
//
// ONE conversation id across the turns is what makes this a thread rather than five unrelated
// questions, and it is the only way these can be tested at all.
const THREAD = [
  ['انت معايا ؟',  'reply',   'being spoken to: a reply, not "I cannot answer that"'],
  ['كام رصيد',     'asks',    'names no item, so it must ASK which one'],
  ['مياه معدنية',  'answers', 'read as the ANSWER to that question, not a new one'],
  ['والتكلفة؟',    'carries', 'must stay on mineral water'],

  // NARROWING THE LAST ANSWER. The owner asked for the list of doctors, got it, said «الاسنان فقط»
  // and was refused — two words that name no intent on their own and take all the meaning they
  // need from the sentence before them. The last ANSWERING surface is now offered a question that
  // reached nothing, but only when it says it holds that value.
  ['هات قائمة الاطباء', 'answers', 'the list, which is what the next two narrow'],
  ['الاسنان فقط',       'answers', 'narrows the previous answer to one department'],
  ['والاطفال؟',         'answers', 'and again, to another'],
  // THE GUARD, and it caught a real one: an empty search key matched EVERY word, because
  // x.Contains("") is true for every x. A practitioner with no clinic carries an empty clinic key,
  // so «ايه سعر الدولار» came back as the doctors filtered to "(no clinic)". An unrelated question
  // answered with the previous subject's figures is the failure this whole check exists to stop.
  ['ايه سعر الدولار',   'reply',   'UNRELATED: must be refused, never swallowed by the last surface'],
];

try {
  const conv = `talk${Date.now()}`;
  for (const [q, want, why] of THREAD) {
    const r = await page.evaluate(async ([base, question, c]) => {
      const res = await fetch(`${base}/Inventory/Ask?question=${encodeURIComponent(question)}&c=${c}`,
        { credentials: 'same-origin', headers: { Accept: 'application/json' } });
      const t = await res.text();
      let d = null;
      try { d = JSON.parse(t); } catch { /* not json */ }
      return { status: res.status, ok: d ? d.ok : null, rows: d && d.rows ? d.rows.length : 0,
               about: d ? `${d.about || ''}` : '',
               say: d ? `${d.spoken || d.message || ''}`.slice(0, 120) : t.slice(0, 120) };
    }, [BASE, q, conv]);

    // 'reply'   — not an answer with rows, but it must SAY something back.
    // 'asks'    — not an answer either; the question it asks is the whole behaviour.
    // 'answers' — the follow-up was read as the answer, so rows come back.
    // 'carries' — answered AND visibly about the subject from two turns ago.
    const good = r.status === 200 && (
      want === 'reply'   ? (r.ok === false && r.say.length > 0)
    : want === 'asks'    ? (r.ok === false && r.say.length > 0)
    : want === 'answers' ? (r.ok === true && r.rows > 0)
    :                      (r.ok === true && r.about.length > 0));
    if (good) { pass++; } else { failures.push({ q, want: `conversation:${want}`, why, ...r }); }
    total++;
  }
} catch (e) {
  console.error('NOT VERIFIED (conversation) —', e?.message ?? e);
  await browser.close();
  process.exit(2);
}

// ---- AND IN ENGLISH ---------------------------------------------------------------------------
//
// Every surface declares English labels and every intent carries English phrasings, so it is easy
// to assume this works. It is a separate path and it fails in two separate ways:
//
//   ROUTING   — does an English question reach the right surface at all?
//   RENDERING — does the ANSWER come back in English? A correct answer rendered in Arabic to an
//               English session is still a defect, and a model-only test cannot see it: the model
//               routes the same either way, and the language is decided downstream.
//
// The rules text travels with every answer, so it is checked too — a reader who cannot read the
// rules cannot judge the figure they are attached to.
const ARABIC = /[؀-ۿ]/;
const ENGLISH = [
  ['which hour is busiest',        'answer', 'pos, by hour'],
  ['how many employees',           'answer', 'workforce'],
  ['what did we buy',              'answer', 'purchases'],
  ['revenue by month',             'answer', 'sales'],
  ['average salary',               'refuse', 'a salary is recorded for 1 employee of 21'],

  // A FILTER PROVED IN ONE LANGUAGE IS NOT PROVED IN THE OTHER.
  // The Arabic twin of this case passed while the English one silently answered about all 19
  // lines, because the measure was still NAMED "accepted" and a column's name is vocabulary
  // too. Grouped by payer the counts separate: 1 when the status filter held, 2 when it did not.
  ['accepted claims by payer',      1,        'filtered to accepted: one payer, not two'],
  ['todays appointments',           'answer', "the relative window resolves to today"],
  ['cancelled appointments',        1,        'the same filter the Arabic twin needs spoken forms for'],



  // THE SAME TWO SURFACES, ASKED IN ENGLISH WITH THE UI IN ENGLISH.
  //
  // The Arabic block above already proved these questions route. What these prove is different:
  // that a value can be NAMED in the reader's own language. Every dimension stores one search
  // key carrying the stored English and the Arabic rendering together, so 'radiology' and
  // 'الاشعة' both reach the same filter whichever way the session is set. A surface that folded
  // only the displayed language would pass in one block and fail in the other, and nothing in
  // a single-language run could tell you which.
  ['hospital revenue by department', 'answer', 'billing surface, grouped by department'],
  ['radiology billing',              'answer', 'a VALUE named in English reaches the filter'],
  ['rejected claims',                'answer', 'claims surface, filtered by status'],
  ['claims by payer',                'answer', 'payer comes from the batch, not the claim'],

  // ---- THE WORD "PRIMARY" IS INSIDE A DIAGNOSIS NAME ------------------------------------------
  //
  // This hospital records "Essential (primary) hypertension". The named-value filter walks the
  // dimensions in DECLARATION order and takes the first that matches, so with the diagnosis column
  // declared before the rank, «primary diagnoses» filtered to hypertension and returned a single
  // confident row. Fifteen rows means it resolved to the RANK; one means the collision is back.
  // There is no Arabic twin of this case because the collision does not exist in Arabic — which is
  // exactly why a single-language run could never have found it.
  ['primary diagnoses',              (n) => n > 1,
   '"primary" must resolve to the rank, not to the hypertension that is spelt with it'],
  ['secondary diagnoses',            (n) => n > 0 && n < 10, 'the Arabic twin of this filter, in English'],
  ['asthma cases',                   1,        'a diagnosis NAMED in English reaches the filter'],
  ['diagnoses by department',        'answer', 'the clinical surface, grouped'],

];

try {
  // The session's language, not the browser's: this product decides its language six ways and
  // only one of them is the Accept-Language header.
  await page.goto(`${BASE}/Account/SetLanguage?culture=en&returnUrl=%2F`, { waitUntil: 'domcontentloaded' }).catch(() => {});
  await page.goto(`${BASE}/Inventory/StockBalances`, { waitUntil: 'domcontentloaded' });

  for (const [q, want, why] of ENGLISH) {
    const r = await page.evaluate(async ([base, question]) => {
      const res = await fetch(`${base}/Inventory/Ask?question=${encodeURIComponent(question)}&c=probe:en${Date.now()}`,
        { credentials: 'same-origin', headers: { Accept: 'application/json' } });
      const t = await res.text();
      let d = null;
      try { d = JSON.parse(t); } catch { /* not json */ }
      return { status: res.status, ok: d ? d.ok : null, rows: d && d.rows ? d.rows.length : 0,
               say: d ? `${d.spoken || d.message || ''}` : t.slice(0, 120),
               understood: d ? `${d.understood || ''}` : '' };
    }, [BASE, q]);

    // A BOUND IS AN ASSERTION TOO, and this loop could not express one. It understood a number and
    // the word 'answer', and anything else fell through to the final branch — which expects a
    // REFUSAL. So a case written as a predicate did not merely fail to be checked: it was checked
    // against the opposite of what it claimed, and two correct answers were reported as defects.
    // The Arabic loop above has taken a predicate since it was written; this is that window widened
    // to match, not a claim relaxed to fit it.
    const routed = r.status === 200 && (typeof want === 'function'
      ? (r.ok === true && want(r.rows))
      : typeof want === 'number'
      ? (r.ok === true && r.rows === want)
      : want === 'answer' ? (r.ok === true && r.rows > 0) : r.ok === false);
    const arabicLeak = ARABIC.test(r.say) || ARABIC.test(r.understood);
    if (routed && !arabicLeak) { pass++; }
    else { failures.push({ q, want: `english:${want}${arabicLeak ? ' (Arabic leaked)' : ''}`, why, ...r, say: r.say.slice(0, 110) }); }
    total++;
  }
} catch (e) {
  console.error('NOT VERIFIED (english) —', e?.message ?? e);
  await browser.close();
  process.exit(2);
}


// A VALUE THAT EXISTS IN THE DOMAIN BUT HAS NO ROWS MUST BE SAID, NOT SILENTLY DROPPED.
//
// There are no rejected claim lines in this data. Asked about them, the honest answer names the
// gap and then says what it IS showing. The failure mode is the opposite and it is silent: the
// word gets swallowed as column vocabulary, no filter is built, and all 19 lines come back under
// a question about refusals - a confident wrong answer with nothing on screen to question.
// It happened in English and not in Arabic, purely because "rejected".Contains("reject") is true
// and "المرفوضه".Contains("الرفض") is not. One language passing proves nothing about the other.
const MISSINGVALUE = [
  ['ar', 'المطالبات المرفوضة', 'no rejected lines exist: say so rather than answer about all of them'],
  ['en', 'rejected claims',  'same question, same obligation, in the other language'],

  // MIGRAINE IS IN THE CATALOGUE AND NOBODY HAS EVER BEEN DIAGNOSED WITH IT. Eighteen of the
  // fifty-three codes have ever been used, so most of this hospital's own vocabulary names zero
  // rows — and a question using one of those words is the easiest place for this surface to answer
  // confidently about everything instead of admitting it found nothing.
  ['ar', 'كام حالة صداع نصفي',    'a code in the catalogue that no doctor has recorded'],
  ['en', 'how many migraine cases', 'the same absent value, asked in the other language'],
];

try {
  for (const [culture, q, why] of MISSINGVALUE) {
    await page.goto(`${BASE}/Account/SetLanguage?culture=${culture}&returnUrl=%2F`, { waitUntil: 'domcontentloaded' }).catch(() => {});
    const r = await page.evaluate(async ([base, question]) => {
      const res = await fetch(`${base}/Inventory/Ask?question=${encodeURIComponent(question)}&c=probe:mv${Date.now()}`,
        { credentials: 'same-origin', headers: { Accept: 'application/json' } });
      const t = await res.text();
      let d = null;
      try { d = JSON.parse(t); } catch { /* not json */ }
      return { status: res.status, ok: d ? d.ok : null, rows: d && d.rows ? d.rows.length : 0,
               say: d ? `${d.spoken || d.message || ''}` : t.slice(0, 120),
               understood: d ? `${d.understood || ''}` : '' };
    }, [BASE, q]);
    const said = `${r.say} ${r.understood}`;
    const admits = said.includes('مالقيتش') || /could not find|couldn't find|no .* found/i.test(said);
    const good = r.status === 200 && admits;
    if (good) { pass++; }
    else { failures.push({ q: `[${culture}] ${q}`, want: 'says the value was not found', why, ...r, say: said.slice(0, 130) }); }
    total++;
  }
} catch (e) {
  console.error('NOT VERIFIED (missing value) —', e?.message ?? e);
  await browser.close();
  process.exit(2);
}

// ---- A FIGURE THAT IS RIGHT FOR A DIFFERENT QUESTION ------------------------------------------
//
// Every assertion above this point can be satisfied by a number. These cannot: they check that a
// particular SENTENCE travels with the answer, because the failure they guard has a correct-looking
// figure and no row count that could ever expose it.
//
// «كام زيارة عندنا» is the case that forced the block into existence, and it failed twice over.
// First it was answered with the CASH BALANCE — 239,605 under a question about visits — because
// "كام ... عندنا" is the shape of «كام فلوس عندنا» and "زيارة" was a word the router had never
// seen. Teaching it fixed the routing and left the subtler half behind: asked for a number, the
// composer collapses the appointments surface to a grand total, and that total includes the
// cancelled, the no-shows and the bookings for dates that have not happened yet. None of those is
// a visit. The count is honest about what the surface holds and wrong about what was asked, and
// the only thing on screen that can tell the two apart is the caveat.
const SAYS = [
  ['ar', 'كام زيارة عندنا', 'مش زيارات حصلت',
   'the total counts cancelled, no-shows and future bookings: it must say so'],
  ['en', 'how many visits do we have', 'a visit anyone made',
   'the same obligation in the other language'],
  // NOT «كام زيارة فيها تشخيص». I wrote that one first, expecting it to reach the diagnoses
  // surface, and it reached APPOINTMENTS — correctly, because "زيارة" now routes there. Reading
  // the answer rather than the verdict showed the case was the mistake: no surface holds that
  // figure, so asserting one would have been asserting a capability nobody built. This asks about
  // the diagnoses and names PATIENTS, which the surface really does answer and really does
  // mislead about — one patient can be seen several times and collect a diagnosis each visit.
  ['ar', 'التشخيصات لكام مريض', 'مش عدد المرضى',
   'the diagnoses surface counts diagnoses; patients are fewer and it must not imply otherwise'],
];

try {
  for (const [culture, q, must, why] of SAYS) {
    await page.goto(`${BASE}/Account/SetLanguage?culture=${culture}&returnUrl=%2F`, { waitUntil: 'domcontentloaded' }).catch(() => {});
    const r = await page.evaluate(async ([base, question]) => {
      const res = await fetch(`${base}/Inventory/Ask?question=${encodeURIComponent(question)}&c=probe:say${Date.now()}`,
        { credentials: 'same-origin', headers: { Accept: 'application/json' } });
      const t = await res.text();
      let d = null;
      try { d = JSON.parse(t); } catch { /* not json */ }
      return { status: res.status, ok: d ? d.ok : null, rows: d && d.rows ? d.rows.length : 0,
               say: d ? `${d.about || ''} ${d.spoken || d.message || ''}` : t.slice(0, 160),
               understood: d ? `${d.understood || ''}` : '' };
    }, [BASE, q]);
    const said = `${r.say} ${r.understood}`;
    const good = r.status === 200 && r.ok === true && said.includes(must);
    if (good) { pass++; }
    else { failures.push({ q: `[${culture}] ${q}`, want: `says "${must}"`, why, ...r, say: said.slice(0, 160) }); }
    total++;
  }
} catch (e) {
  console.error('NOT VERIFIED (caveat text) —', e?.message ?? e);
  await browser.close();
  process.exit(2);
}

await browser.close();

console.log(`ask capability probe: ${pass} / ${total} passed`);
for (const f of failures) {
  console.log(`  FAIL  want=${f.want}  ok=${f.ok}  rows=${f.rows}  http=${f.status}`);
  console.log(`        ${f.q}`);
  console.log(`        expected because: ${f.why}`);
  console.log(`        got: ${f.say}`);
}
process.exit(failures.length === 0 ? 0 : 1);
