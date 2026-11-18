// H189: cardiology (QTc from QT and the ECG rate, HAS-BLED), thyroid (ACR TI-RADS), bone density (WHO class) and
// psoriasis (PASI) — built through the dictionary and the designer, as a clinical lead would, on the H188 calculators.
export default async function run(page) {
  const B = 'https://localhost:7183', out = { bad: {} };
  page.setDefaultNavigationTimeout(150000); page.setDefaultTimeout(60000);
  await page.goto(B + '/api/dev/culture-check?key=seed123');
  await page.goto(B + '/Hospital/Concepts', { waitUntil: 'networkidle', timeout: 150000 });
  const post = (u, b) => page.evaluate(async ([u, b]) => { const t = document.querySelector('input[name="__RequestVerificationToken"]').value;
    const r = await fetch(u, { method: 'POST', headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': t, 'X-Requested-With': 'XMLHttpRequest' }, body: JSON.stringify(b || {}) });
    return await r.json().catch(() => ({ ok: false, error: 'HTTP ' + r.status })); }, [u, b]);
  const get = (u) => page.evaluate(async (u) => await (await fetch(u)).text(), u);
  const concept = async (code) => { const h = await get('/Hospital/Concepts/Data?q=' + encodeURIComponent(code)); return Number((h.match(/data-edit="(\d+)"/) || [])[1]); };
  const C = (code, name, nameEn, type, unit, dec, x = {}) => ({ code, codeSystem: 'Local', name, nameEn, valueType: type, unit, decimals: dec, isActive: true, ...x });
  const add = async (c) => { const r = await post('/Hospital/Concepts', c); if (!r.ok) out.bad[c.code] = r.error; };
  const field = async (v, code, sec, secEn, x = {}) => { const r = await post('/Hospital/Templates/Versions/' + v + '/Fields', { conceptId: await concept(code), section: sec, sectionEn: secEn, valueSource: 'Manual', ...x }); if (!r.ok) out.bad['field ' + code] = r.error; };
  const versionOf = async (t) => Number(((await get('/Hospital/Templates?t=' + t)).match(/here = \{ t: \d+, v: (\d+)/) || [])[1]);

  // ---------------- cardiology: QTc by both formulas from the ECG; HAS-BLED ----------------
  await add(C('ECG-QT', 'فترة QT على تخطيط القلب', 'QT interval (ECG)', 'Number', 'ms', 0));
  await add(C('ECG-HR', 'معدل القلب على تخطيط القلب', 'Heart rate (ECG)', 'Number', 'bpm', 0, { normalLow: 60, normalHigh: 100 }));
  await add(C('QTC-B', 'QTc (بازيت)', 'QTc (Bazett)', 'Calculated', 'ms', 0, { normalHigh: 460, criticalHigh: 500, calculator: 'QtcBazett', calcInputs: 'ECG-QT, ECG-HR' }));
  await add(C('QTC-F', 'QTc (فريدريشيا)', 'QTc (Fridericia)', 'Calculated', 'ms', 0, { normalHigh: 460, criticalHigh: 500, calculator: 'QtcFridericia', calcInputs: 'ECG-QT, ECG-HR' }));
  const HB = [['HASBLED-H', 'ارتفاع ضغط غير منضبط (انقباضي > 160)', 'Uncontrolled hypertension (SBP > 160)'], ['HASBLED-A1', 'خلل وظائف الكلى', 'Abnormal renal function'],
    ['HASBLED-A2', 'خلل وظائف الكبد', 'Abnormal liver function'], ['HASBLED-S', 'سكتة دماغية سابقة', 'Prior stroke'], ['HASBLED-B', 'نزف سابق أو استعداد للنزف', 'Prior major bleeding or predisposition'],
    ['HASBLED-L', 'INR غير مستقر', 'Labile INR'], ['HASBLED-E', 'العمر فوق 65', 'Age over 65'], ['HASBLED-D1', 'أدوية تزيد النزف (مضادات الصفائح، مضادات الالتهاب)', 'Drugs predisposing to bleeding (antiplatelets, NSAIDs)'],
    ['HASBLED-D2', 'الكحول (8 وحدات أسبوعيًا فأكثر)', 'Alcohol (8 or more units a week)']];
  for (const [c, a, e] of HB) await add(C(c, 'HAS-BLED · ' + a, 'HAS-BLED · ' + e, 'Number', null, 0));
  await add(C('HAS-BLED', 'درجة HAS-BLED', 'HAS-BLED score', 'Score', '/9', 0, { normalHigh: 2, calculator: 'Sum', calcInputs: HB.map(x => x[0]).join(', '),
    choices: '0|خطر نزف منخفض|Low bleeding risk\n3|خطر نزف مرتفع — عالج العوامل القابلة للتعديل وراقب|High bleeding risk — correct the modifiable factors, review closely' }));
  const cv = (await post('/Hospital/Templates/8/NewVersion', {})).id; out.cardV = cv;
  await field(cv, 'ECG-QT', 'تخطيط القلب', 'ECG', { note: 'من رسم القلب', noteEn: 'As measured on the ECG' });
  await field(cv, 'ECG-HR', 'تخطيط القلب', 'ECG');
  await field(cv, 'QTC-B', 'تخطيط القلب', 'ECG', { showTrend: true });
  await field(cv, 'QTC-F', 'تخطيط القلب', 'ECG', { showTrend: true, alertAbove: 500, alertText: 'QTc أكثر من ٥٠٠ مللي ثانية — خطر اضطراب النظم: راجع الأدوية والأملاح', alertTextEn: 'QTc above 500 ms — arrhythmia risk: review the drugs and electrolytes' });
  for (const [c] of HB) await field(cv, c, 'خطر النزف HAS-BLED', 'Bleeding risk (HAS-BLED)', { note: '١ نعم · ٠ لا', noteEn: '1 yes · 0 no' });
  await field(cv, 'HAS-BLED', 'خطر النزف HAS-BLED', 'Bleeding risk (HAS-BLED)', { showTrend: true });
  out.pubCard = await post('/Hospital/Templates/Versions/' + cv + '/Publish', { note: 'QTc من QT ومعدل القلب بمعادلتي بازيت وفريدريشيا، ودرجة HAS-BLED', noteEn: 'QTc from QT and the ECG rate (Bazett and Fridericia), and the HAS-BLED score' });

  // ---------------- thyroid nodule: ACR TI-RADS ----------------
  const TI = [['TIR-COMP', 'التركيب', 'Composition', '٠ كيسي أو إسفنجي · ١ مختلط · ٢ صلب', '0 cystic/spongiform · 1 mixed · 2 solid'],
    ['TIR-ECHO', 'الصدى', 'Echogenicity', '٠ عديم الصدى · ١ مرتفع أو مماثل · ٢ منخفض · ٣ منخفض جدًا', '0 anechoic · 1 hyper/isoechoic · 2 hypoechoic · 3 very hypoechoic'],
    ['TIR-SHAPE', 'الشكل', 'Shape', '٠ أعرض من طوله · ٣ أطول من عرضه', '0 wider-than-tall · 3 taller-than-wide'],
    ['TIR-MARGIN', 'الحواف', 'Margin', '٠ ناعمة أو غير واضحة · ٢ مفصّصة أو غير منتظمة · ٣ امتداد خارج الدرقية', '0 smooth/ill-defined · 2 lobulated/irregular · 3 extra-thyroidal extension'],
    ['TIR-FOCI', 'البؤر الصدوية', 'Echogenic foci', 'مجموع: ٠ لا شيء · ١ تكلسات كبيرة · ٢ حافية · ٣ دقيقة', 'Sum: 0 none · 1 macrocalcifications · 2 peripheral · 3 punctate']];
  for (const [c, a, e] of TI) await add(C(c, 'TI-RADS · ' + a, 'TI-RADS · ' + e, 'Number', null, 0));
  await add(C('TIR-PTS', 'نقاط TI-RADS', 'TI-RADS points', 'Score', null, 0, { calculator: 'Sum', calcInputs: TI.map(x => x[0]).join(', ') }));
  await add(C('TIR-SIZE', 'أكبر قطر للعقيدة', 'Nodule largest diameter', 'Number', 'cm', 1));
  const lv = { 1: ['TR1 حميدة', 'TR1 benign'], 2: ['TR2 غير مشتبهة', 'TR2 not suspicious'], 3: ['TR3 اشتباه خفيف', 'TR3 mildly suspicious'], 4: ['TR4 اشتباه متوسط', 'TR4 moderately suspicious'], 5: ['TR5 اشتباه شديد', 'TR5 highly suspicious'] };
  const ad = { None: ['لا خزعة ولا متابعة', 'no FNA, no follow-up'], FollowUp: ['متابعة بالموجات', 'ultrasound follow-up'], FNA: ['خزعة بالإبرة الدقيقة', 'fine-needle aspiration'] };
  const choices = [];
  for (const l of [1, 2]) choices.push('TR' + l + '-None|' + lv[l][0] + ' — ' + ad.None[0] + '|' + lv[l][1] + ' — ' + ad.None[1]);
  for (const l of [3, 4, 5]) for (const k of ['None', 'FollowUp', 'FNA']) choices.push('TR' + l + '-' + k + '|' + lv[l][0] + ' — ' + ad[k][0] + '|' + lv[l][1] + ' — ' + ad[k][1]);
  await add(C('TIRADS', 'TI-RADS', 'TI-RADS', 'Calculated', null, 0, { calculator: 'TiRads', calcInputs: 'TIR-PTS, TIR-SIZE', choices: choices.join('\n') }));
  const tv = (await post('/Hospital/Templates/10/NewVersion', {})).id; out.thyV = tv;
  for (const [c, , , n, nEn] of TI) await field(tv, c, 'عقيدة الدرقية (ACR TI-RADS)', 'Thyroid nodule (ACR TI-RADS)', { note: n, noteEn: nEn });
  await field(tv, 'TIR-PTS', 'عقيدة الدرقية (ACR TI-RADS)', 'Thyroid nodule (ACR TI-RADS)');
  await field(tv, 'TIR-SIZE', 'عقيدة الدرقية (ACR TI-RADS)', 'Thyroid nodule (ACR TI-RADS)', { showTrend: true });
  await field(tv, 'TIRADS', 'عقيدة الدرقية (ACR TI-RADS)', 'Thyroid nodule (ACR TI-RADS)');
  out.pubThy = await post('/Hospital/Templates/Versions/' + tv + '/Publish', { note: 'تقييم عقيدة الدرقية وفق ACR TI-RADS 2017 مع توصية الخزعة أو المتابعة حسب الحجم', noteEn: 'Thyroid nodule by ACR TI-RADS 2017, with the FNA or follow-up the size calls for' });

  // ---------------- bone density: the WHO class ----------------
  await add(C('DXA-T', 'أدنى T-score (العمود الفقري أو الورك)', 'Lowest T-score (spine or hip)', 'Number', 'SD', 1, { normalLow: -1 }));
  await add(C('BMD-CLASS', 'تصنيف كثافة العظام (WHO)', 'Bone density class (WHO)', 'Calculated', 'SD', 1, { calculator: 'TScoreClass', calcInputs: 'DXA-T',
    choices: 'Normal|طبيعية|Normal\nOsteopenia|نقص كثافة العظام|Osteopenia\nOsteoporosis|هشاشة العظام|Osteoporosis' }));
  const bt = await post('/Hospital/Templates', { code: 'BONE-DXA', name: 'كثافة العظام', nameEn: 'Bone density (DXA)', departmentId: null }); out.bone = bt;
  const bv = await versionOf(bt.id);
  await field(bv, 'DXA-T', 'قياس كثافة العظام', 'DXA', { required: true, showTrend: true });
  await field(bv, 'BMD-CLASS', 'قياس كثافة العظام', 'DXA');
  out.pubBone = await post('/Hospital/Templates/Versions/' + bv + '/Publish', { note: 'النسخة الأولى: التصنيف وفق منظمة الصحة العالمية', noteEn: 'First version: the WHO classification' });

  // ---------------- psoriasis: PASI ----------------
  const RG = [['H', 'الرأس', 'Head'], ['U', 'الأطراف العلوية', 'Upper limbs'], ['T', 'الجذع', 'Trunk'], ['L', 'الأطراف السفلية', 'Lower limbs']];
  const PT = [['A', 'المساحة (٠–٦)', 'Area (0–6)'], ['E', 'الاحمرار (٠–٤)', 'Erythema (0–4)'], ['I', 'التسمّك (٠–٤)', 'Induration (0–4)'], ['S', 'التقشّر (٠–٤)', 'Scaling (0–4)']];
  const pasiCodes = [];
  for (const [r, ra, re] of RG) for (const [p, pa, pe] of PT) { const code = 'PASI-' + r + '-' + p; pasiCodes.push(code); await add(C(code, 'PASI · ' + ra + ' · ' + pa, 'PASI · ' + re + ' · ' + pe, 'Number', null, 0)); }
  await add(C('PASI', 'مؤشر PASI', 'PASI', 'Calculated', null, 1, { normalHigh: 10, calculator: 'Pasi', calcInputs: pasiCodes.join(',') }));
  await add(C('PSO-BSA', 'المساحة المصابة من الجسم', 'Body surface area affected', 'Number', '%', 0, { normalHigh: 10 }));
  const dt = await post('/Hospital/Templates', { code: 'DERM-PSO', name: 'متابعة الصدفية', nameEn: 'Psoriasis follow-up', departmentId: 8 }); out.derm = dt;
  const dv = await versionOf(dt.id);
  for (const [r, ra, re] of RG) for (const [p] of PT) await field(dv, 'PASI-' + r + '-' + p, 'PASI · ' + ra, 'PASI · ' + re, { required: true });
  await field(dv, 'PASI', 'النتيجة', 'Result', { showTrend: true, alertAbove: 10, alertText: 'PASI أكثر من ١٠ — صدفية متوسطة إلى شديدة: فكّر في العلاج الجهازي', alertTextEn: 'PASI above 10 — moderate to severe psoriasis: consider systemic treatment' });
  await field(dv, 'PSO-BSA', 'النتيجة', 'Result', { showTrend: true });
  out.pubDerm = await post('/Hospital/Templates/Versions/' + dv + '/Publish', { note: 'النسخة الأولى: PASI بالمناطق الأربع والمساحة المصابة', noteEn: 'First version: PASI by its four regions, and the body surface area' });
  return out;
}
