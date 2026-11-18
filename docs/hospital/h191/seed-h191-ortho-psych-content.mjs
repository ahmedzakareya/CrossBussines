// H191: orthopaedics (range of motion, MRC strength, the Oxford knee and hip scores, QuickDASH, the compartment
// syndrome watch) and psychiatry (the mental status examination) — built through the dictionary and the designer.
// Items are labelled by their domain; the licensed questionnaire wording stays on the clinic's printed forms.
export default async function run(page) {
  const B = 'https://localhost:7183', out = { bad: {} }, id = {};
  page.setDefaultNavigationTimeout(150000); page.setDefaultTimeout(60000);
  await page.goto(B + '/api/dev/culture-check?key=seed123');
  await page.goto(B + '/Hospital/Concepts', { waitUntil: 'networkidle', timeout: 150000 });
  const post = (u, b) => page.evaluate(async ([u, b]) => { const t = document.querySelector('input[name="__RequestVerificationToken"]').value;
    const r = await fetch(u, { method: 'POST', headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': t, 'X-Requested-With': 'XMLHttpRequest' }, body: JSON.stringify(b || {}) });
    return await r.json().catch(() => ({ ok: false, error: 'HTTP ' + r.status })); }, [u, b]);
  const get = (u) => page.evaluate(async (u) => await (await fetch(u)).text(), u);
  const C = (code, name, nameEn, type, unit, dec, x = {}) => ({ code, codeSystem: 'Local', name, nameEn, valueType: type, unit, decimals: dec, isActive: true, ...x });
  const add = async (c) => { const r = await post('/Hospital/Concepts', c); if (r.ok) id[c.code] = r.id; else out.bad[c.code] = r.error; };
  const field = async (v, code, sec, secEn, x = {}) => { const r = await post('/Hospital/Templates/Versions/' + v + '/Fields', { conceptId: id[code], section: sec, sectionEn: secEn, valueSource: 'Manual', ...x }); if (!r.ok) out.bad['field ' + code] = r.error; };
  const template = async (code, name, nameEn, dept) => { const t = await post('/Hospital/Templates', { code, name, nameEn, departmentId: dept }); if (!t.ok) { out.bad['tpl ' + code] = t.error; return 0; }
    return Number(((await get('/Hospital/Templates?t=' + t.id)).match(/here = \{ t: \d+, v: (\d+)/) || [])[1]); };
  const publish = async (v, key) => { out[key] = (await post('/Hospital/Templates/Versions/' + v + '/Publish', { note: 'النسخة الأولى', noteEn: 'First version' })).ok; };
  const ORTH = 5;

  // ---------------- range of motion and strength (each side) ----------------
  const ROM = [['ROM-KNEE-FLEX', 'ثني الركبة', 'Knee flexion', 120], ['ROM-HIP-FLEX', 'ثني الورك', 'Hip flexion', 100], ['ROM-HIP-ABD', 'تبعيد الورك', 'Hip abduction', 35],
    ['ROM-SHO-FLEX', 'ثني الكتف', 'Shoulder flexion', 160], ['ROM-SHO-ABD', 'تبعيد الكتف', 'Shoulder abduction', 160], ['ROM-SHO-ER', 'الدوران الخارجي للكتف', 'Shoulder external rotation', 60]];
  for (const [c, a, e, lo] of ROM) await add(C(c, 'مدى الحركة · ' + a, 'Range of motion · ' + e, 'Number', '°', 0, { hasLaterality: true, normalLow: lo }));
  await add(C('ROM-KNEE-LAG', 'نقص مدّ الركبة', 'Knee extension lag', 'Number', '°', 0, { hasLaterality: true, normalHigh: 0 }));
  const MRC = [['MRC-QUAD', 'العضلة رباعية الرؤوس', 'Quadriceps'], ['MRC-HIP-ABD', 'مبعِّدات الورك', 'Hip abductors'], ['MRC-DELT', 'العضلة الدالية', 'Deltoid']];
  for (const [c, a, e] of MRC) await add(C(c, 'القوة (MRC ٠–٥) · ' + a, 'Strength (MRC 0–5) · ' + e, 'Number', '/5', 0, { hasLaterality: true, normalLow: 5 }));

  // ---------------- Oxford knee and hip scores (12 items, 0–4, 4 = best) ----------------
  const OKS = ['ألم الركبة المعتاد|Usual knee pain', 'الاستحمام والتجفيف|Washing and drying', 'ركوب السيارة والنزول منها|Getting in and out of a car', 'مدة المشي قبل الألم الشديد|Walking time before severe pain',
    'الألم عند القيام من الكرسي|Pain standing up from a chair', 'العرج|Limping', 'الركوع والقيام|Kneeling and getting up', 'الألم في الفراش ليلًا|Pain in bed at night',
    'تأثير الألم على العمل|Pain interfering with work', 'انثناء الركبة فجأة|Knee giving way', 'التسوق المنزلي وحده|Household shopping alone', 'نزول الدرج|Walking down stairs'];
  const OHS = ['ألم الورك المعتاد|Usual hip pain', 'الاستحمام والتجفيف|Washing and drying', 'ركوب السيارة أو المواصلات|Getting in a car or public transport', 'لبس الجوارب|Putting on socks',
    'التسوق المنزلي وحده|Household shopping alone', 'مدة المشي قبل الألم الشديد|Walking time before severe pain', 'صعود الدرج|Climbing stairs', 'القيام من الكرسي|Standing up from a chair',
    'العرج|Limping', 'ألم مفاجئ شديد|Sudden severe pain', 'تأثير الألم على العمل|Pain interfering with work', 'الألم في الفراش ليلًا|Pain in bed at night'];
  const BANDS = '0|شديد|Severe\n20|متوسط إلى شديد|Moderate to severe\n30|خفيف إلى متوسط|Mild to moderate\n40|مُرضٍ|Satisfactory';
  for (const [pre, list, nm, nmEn] of [['OKS', OKS, 'مقياس أكسفورد للركبة', 'Oxford Knee Score'], ['OHS', OHS, 'مقياس أكسفورد للورك', 'Oxford Hip Score']]) {
    for (let i = 0; i < 12; i++) { const [a, e] = list[i].split('|'); await add(C(pre + '-Q' + (i + 1), pre + ' · ' + a, pre + ' · ' + e, 'Number', null, 0)); }
    await add(C(pre, nm, nmEn, 'Score', '/48', 0, { normalLow: 40, calculator: 'Sum', calcInputs: list.map((_, i) => pre + '-Q' + (i + 1)).join(', '), choices: BANDS }));
  }
  // ---------------- QuickDASH (11 items, 1–5) ----------------
  const QD = ['فتح برطمان محكم|Open a tight jar', 'أعمال منزلية شاقة|Heavy household chores', 'حمل كيس التسوق|Carry a shopping bag', 'غسل الظهر|Wash your back', 'تقطيع الطعام بالسكين|Use a knife to cut food',
    'نشاط ترفيهي يتطلب قوة في الذراع|Recreation that takes force through the arm', 'تأثيره على الأنشطة الاجتماعية|Interference with social activities', 'تقييد العمل أو النشاط اليومي|Limits on work or daily activities',
    'ألم الذراع أو الكتف أو اليد|Arm, shoulder or hand pain', 'التنميل|Tingling', 'صعوبة النوم بسبب الألم|Difficulty sleeping from the pain'];
  for (let i = 0; i < 11; i++) { const [a, e] = QD[i].split('|'); await add(C('QDASH-Q' + (i + 1), 'QuickDASH · ' + a, 'QuickDASH · ' + e, 'Number', null, 0)); }
  await add(C('QDASH', 'QuickDASH', 'QuickDASH', 'Calculated', '/100', 1, { normalHigh: 20, calculator: 'QuickDash', calcInputs: QD.map((_, i) => 'QDASH-Q' + (i + 1)).join(', ') }));
  // ---------------- the compartment syndrome watch ----------------
  const CS = [['CS-PAIN', 'ألم لا يتناسب مع الإصابة', 'Pain out of proportion'], ['CS-STRETCH', 'ألم عند الشد السلبي', 'Pain on passive stretch'], ['CS-TENSE', 'حجرة متوترة متورمة', 'Tense, swollen compartment'],
    ['CS-PARA', 'تنميل', 'Paraesthesia'], ['CS-PALLOR', 'شحوب', 'Pallor'], ['CS-PULSE', 'غياب النبض', 'Absent pulse'], ['CS-PARALYSIS', 'شلل', 'Paralysis']];
  for (const [c, a, e] of CS) await add(C(c, 'متلازمة الحيّز · ' + a, 'Compartment syndrome · ' + e, 'Number', null, 0));
  await add(C('CS-SIGNS', 'علامات متلازمة الحيّز', 'Compartment syndrome signs', 'Score', '/7', 0, { normalHigh: 1, calculator: 'Sum', calcInputs: CS.map(x => x[0]).join(', ') }));

  // ---------------- the orthopaedic templates ----------------
  let v = await template('ORTHO-KNEE', 'متابعة الركبة', 'Knee follow-up', ORTH); out.knee = v;
  for (const c of ['ROM-KNEE-FLEX', 'ROM-KNEE-LAG']) await field(v, c, 'مدى الحركة', 'Range of motion', { showTrend: true });
  await field(v, 'MRC-QUAD', 'القوة', 'Strength');
  for (let i = 1; i <= 12; i++) await field(v, 'OKS-Q' + i, 'مقياس أكسفورد للركبة', 'Oxford Knee Score', { required: true, note: '٠ الأسوأ … ٤ الأفضل', noteEn: '0 worst … 4 best' });
  await field(v, 'OKS', 'مقياس أكسفورد للركبة', 'Oxford Knee Score', { showTrend: true });
  await publish(v, 'pubKnee');
  v = await template('ORTHO-HIP', 'متابعة الورك', 'Hip follow-up', ORTH); out.hip = v;
  for (const c of ['ROM-HIP-FLEX', 'ROM-HIP-ABD']) await field(v, c, 'مدى الحركة', 'Range of motion', { showTrend: true });
  await field(v, 'MRC-HIP-ABD', 'القوة', 'Strength');
  for (let i = 1; i <= 12; i++) await field(v, 'OHS-Q' + i, 'مقياس أكسفورد للورك', 'Oxford Hip Score', { required: true, note: '٠ الأسوأ … ٤ الأفضل', noteEn: '0 worst … 4 best' });
  await field(v, 'OHS', 'مقياس أكسفورد للورك', 'Oxford Hip Score', { showTrend: true });
  await publish(v, 'pubHip');
  v = await template('ORTHO-UL', 'متابعة الطرف العلوي', 'Upper limb follow-up', ORTH); out.ul = v;
  for (const c of ['ROM-SHO-FLEX', 'ROM-SHO-ABD', 'ROM-SHO-ER']) await field(v, c, 'مدى الحركة', 'Range of motion', { showTrend: true });
  await field(v, 'MRC-DELT', 'القوة', 'Strength');
  for (let i = 1; i <= 11; i++) await field(v, 'QDASH-Q' + i, 'QuickDASH', 'QuickDASH', { note: '١ لا صعوبة … ٥ لا أستطيع', noteEn: '1 no difficulty … 5 unable' });
  await field(v, 'QDASH', 'QuickDASH', 'QuickDASH', { showTrend: true });
  await publish(v, 'pubUl');
  v = await template('ORTHO-NV', 'المراقبة العصبية الوعائية للطرف', 'Limb neurovascular watch', ORTH); out.nv = v;
  for (const [c] of CS) await field(v, c, 'علامات متلازمة الحيّز', 'Compartment syndrome signs', { required: true, note: '١ موجودة · ٠ غير موجودة', noteEn: '1 present · 0 absent' });
  await field(v, 'CS-SIGNS', 'علامات متلازمة الحيّز', 'Compartment syndrome signs', { alertAbove: 2, alertNotify: true,
    alertText: 'اشتباه متلازمة الحيّز — مراجعة عاجلة من الاستشاري وقياس ضغط الحيّز، وفُكّ الجبيرة والضمادات الآن', alertTextEn: 'Suspected compartment syndrome — urgent senior review and compartment pressure; release splints and dressings now' });
  await publish(v, 'pubNv');

  // ---------------- psychiatry: the mental status examination ----------------
  const CH = (s) => s.map(x => x.join('|')).join('\n');
  const MSE = [
    ['MSE-APP', 'المظهر', 'Appearance', [['Appropriate', 'مناسب', 'Appropriate'], ['Unkempt', 'مهمل', 'Unkempt'], ['Bizarre', 'غريب', 'Bizarre']]],
    ['MSE-BEH', 'السلوك', 'Behaviour', [['Calm', 'هادئ ومتعاون', 'Calm and cooperative'], ['Agitated', 'مهتاج', 'Agitated'], ['Withdrawn', 'منسحب', 'Withdrawn'], ['Hostile', 'عدائي', 'Hostile']]],
    ['MSE-SPEECH', 'الكلام', 'Speech', [['Normal', 'طبيعي', 'Normal'], ['Pressured', 'متسارع ضاغط', 'Pressured'], ['Slowed', 'بطيء', 'Slowed'], ['Mute', 'صامت', 'Mute']]],
    ['MSE-MOOD', 'المزاج', 'Mood', [['Euthymic', 'معتدل', 'Euthymic'], ['Depressed', 'مكتئب', 'Depressed'], ['Elevated', 'مرتفع', 'Elevated'], ['Anxious', 'قلق', 'Anxious'], ['Irritable', 'سريع الانفعال', 'Irritable']]],
    ['MSE-AFFECT', 'الوجدان', 'Affect', [['Reactive', 'متفاعل متوافق', 'Reactive, congruent'], ['Flat', 'متبلد', 'Flat'], ['Labile', 'متقلب', 'Labile'], ['Incongruent', 'غير متوافق', 'Incongruent']]],
    ['MSE-TFORM', 'شكل التفكير', 'Thought form', [['Linear', 'مترابط', 'Linear'], ['Tangential', 'استطرادي أو عرضي', 'Circumstantial or tangential'], ['Flight', 'تطاير الأفكار', 'Flight of ideas'], ['Loose', 'تفكك الترابط', 'Loosening of associations']]],
    ['MSE-TCONT', 'محتوى التفكير', 'Thought content', [['None', 'لا اضطراب', 'No abnormality'], ['Delusions', 'ضلالات', 'Delusions'], ['Obsessions', 'وساوس', 'Obsessions'], ['Preoccupation', 'انشغال مفرط', 'Preoccupations']]],
    ['MSE-PERC', 'الإدراك', 'Perception', [['None', 'لا هلاوس', 'No hallucinations'], ['Auditory', 'هلاوس سمعية', 'Auditory hallucinations'], ['Visual', 'هلاوس بصرية', 'Visual hallucinations'], ['Other', 'أخرى', 'Other']]],
    ['MSE-COG', 'الإدراك المعرفي والتوجّه', 'Cognition and orientation', [['Oriented', 'متوجّه للزمان والمكان والأشخاص', 'Oriented to time, place and person'], ['Impaired', 'مضطرب التوجّه', 'Disoriented']]],
    ['MSE-INSIGHT', 'البصيرة', 'Insight', [['Good', 'جيدة', 'Good'], ['Partial', 'جزئية', 'Partial'], ['Poor', 'ضعيفة', 'Poor']]],
    ['MSE-JUDG', 'الحكم', 'Judgement', [['Good', 'سليم', 'Good'], ['Impaired', 'مضطرب', 'Impaired']]],
  ];
  for (const [c, a, e, ch] of MSE) await add(C(c, 'فحص الحالة العقلية · ' + a, 'Mental status · ' + e, 'Choice', null, 0, { choices: CH(ch) }));
  await add(C('MSE-RISK', 'أفكار إيذاء النفس أو الآخرين', 'Thoughts of harming self or others', 'Number', null, 0));
  v = await template('PSY-MSE', 'فحص الحالة العقلية', 'Mental status examination', null); out.mse = v;
  for (const [c] of MSE) await field(v, c, 'فحص الحالة العقلية', 'Mental status examination', { required: true });
  await field(v, 'MSE-RISK', 'الخطر', 'Risk', { required: true, note: '١ نعم · ٠ لا', noteEn: '1 yes · 0 no', alertAbove: 1, alertNotify: true, alertTemplateId: 9,
    alertText: 'أفكار إيذاء النفس أو الآخرين — قيّم الخطر بمقياس C-SSRS قبل خروج المريض', alertTextEn: 'Thoughts of harming self or others — assess the risk (C-SSRS) before the patient leaves' });
  await publish(v, 'pubMse');
  out.ids = Object.keys(id).length;
  return out;
}
