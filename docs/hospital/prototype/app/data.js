/* Sample data for the prototype. Every shown field carries an {ar, en} twin (the bilingual display
   rule). Country profiles are DEFAULTS a facility edits in settings — the tax and e-claims values are
   starting points to confirm with the facility's tax and regulatory advisers, not legal statements. */
(function () {
	'use strict';
	var D = HMS.data;

	D.countries = {
		SA: { flag: 'saudi-arabia', name: { ar: 'المملكة العربية السعودية', en: 'Saudi Arabia' },
			currency: { code: 'SAR', symAr: 'ر.س', dec: 2, name: { ar: 'ريال سعودي', en: 'Saudi riyal' } },
			vat: 15, vatRule: { ar: 'المواطن السعودي: الضريبة تتحملها الدولة في القطاع الخاص (قابل للتعديل)', en: 'Saudi citizens: VAT borne by the state in private care (editable)' },
			einvoice: { ar: 'فاتورة — هيئة الزكاة والضريبة والجمارك (المرحلة الثانية)', en: 'ZATCA Fatoora (phase 2)' },
			eclaims: { ar: 'نفيس NPHIES', en: 'NPHIES' }, erx: { ar: 'وصفتي', en: 'Wasfaty' },
			idLabel: { ar: 'الهوية الوطنية / الإقامة', en: 'National ID / Iqama' }, idFormat: '10', coding: 'ICD-10-AM · ACHI · SBS',
			regulator: { ar: 'وزارة الصحة · مجلس الضمان الصحي', en: 'MOH · CHI' }, weekend: { ar: 'الجمعة والسبت', en: 'Fri – Sat' }, hijri: true },
		EG: { flag: 'egypt', name: { ar: 'جمهورية مصر العربية', en: 'Egypt' },
			currency: { code: 'EGP', symAr: 'ج.م', dec: 2, name: { ar: 'جنيه مصري', en: 'Egyptian pound' } },
			vat: 14, vatRule: { ar: 'الخدمات الطبية معفاة افتراضياً (قابل للتعديل)', en: 'Medical services exempt by default (editable)' },
			einvoice: { ar: 'الفاتورة والإيصال الإلكتروني — مصلحة الضرائب', en: 'ETA e-Invoice & e-Receipt' },
			eclaims: { ar: 'التأمين الصحي الشامل · شركات إدارة النفقات الطبية', en: 'UHI · medical TPAs' }, erx: { ar: 'روشتة إلكترونية داخلية', en: 'In-house e-prescription' },
			idLabel: { ar: 'الرقم القومي', en: 'National ID' }, idFormat: '14', coding: 'ICD-10 (WHO)',
			regulator: { ar: 'وزارة الصحة والسكان · هيئة الدواء', en: 'MOHP · EDA' }, weekend: { ar: 'الجمعة والسبت', en: 'Fri – Sat' }, hijri: false },
		AE: { flag: 'united-arab-emirates', name: { ar: 'الإمارات العربية المتحدة', en: 'United Arab Emirates' },
			currency: { code: 'AED', symAr: 'د.إ', dec: 2, name: { ar: 'درهم إماراتي', en: 'UAE dirham' } },
			vat: 5, vatRule: { ar: 'الرعاية الصحية الأساسية بنسبة صفر (قابل للتعديل)', en: 'Qualifying healthcare zero-rated (editable)' },
			einvoice: { ar: 'الفوترة الإلكترونية — الهيئة الاتحادية للضرائب', en: 'FTA e-invoicing' },
			eclaims: { ar: 'eClaimLink (دبي) · شفافية (أبوظبي)', en: 'eClaimLink (Dubai) · Shafafiya (Abu Dhabi)' }, erx: { ar: 'الوصفة الإلكترونية للجهة الصحية', en: 'Authority e-prescription' },
			idLabel: { ar: 'الهوية الإماراتية', en: 'Emirates ID' }, idFormat: '15', coding: 'ICD-10-CM · CPT',
			regulator: { ar: 'هيئة الصحة بدبي · دائرة الصحة أبوظبي · وزارة الصحة', en: 'DHA · DOH · MOHAP' }, weekend: { ar: 'السبت والأحد', en: 'Sat – Sun' }, hijri: false },
		KW: { flag: 'kuwait', name: { ar: 'دولة الكويت', en: 'Kuwait' },
			currency: { code: 'KWD', symAr: 'د.ك', dec: 3, name: { ar: 'دينار كويتي', en: 'Kuwaiti dinar' } },
			vat: 0, vatRule: { ar: 'لا توجد ضريبة قيمة مضافة', en: 'No VAT in force' },
			einvoice: { ar: 'غير مطلوب', en: 'Not required' }, eclaims: { ar: 'شركات التأمين مباشرة', en: 'Direct to insurers' }, erx: { ar: 'روشتة إلكترونية داخلية', en: 'In-house e-prescription' },
			idLabel: { ar: 'الرقم المدني', en: 'Civil ID' }, idFormat: '12', coding: 'ICD-10 (WHO)',
			regulator: { ar: 'وزارة الصحة', en: 'Ministry of Health' }, weekend: { ar: 'الجمعة والسبت', en: 'Fri – Sat' }, hijri: true },
		JO: { flag: 'jordan', name: { ar: 'المملكة الأردنية الهاشمية', en: 'Jordan' },
			currency: { code: 'JOD', symAr: 'د.أ', dec: 3, name: { ar: 'دينار أردني', en: 'Jordanian dinar' } },
			vat: 16, vatRule: { ar: 'ضريبة المبيعات العامة مع إعفاءات طبية (قابل للتعديل)', en: 'General sales tax with medical exemptions (editable)' },
			einvoice: { ar: 'جوفوترة', en: 'JoFotara' }, eclaims: { ar: 'شركات التأمين وإدارة النفقات', en: 'Insurers & TPAs' }, erx: { ar: 'حكيم (حسب المنشأة)', en: 'Hakeem (where applicable)' },
			idLabel: { ar: 'الرقم الوطني', en: 'National number' }, idFormat: '10', coding: 'ICD-10 (WHO)',
			regulator: { ar: 'وزارة الصحة · مؤسسة الغذاء والدواء', en: 'MOH · JFDA' }, weekend: { ar: 'الجمعة والسبت', en: 'Fri – Sat' }, hijri: false }
	};

	function P(ar, en) { return { ar: ar, en: en }; }

	D.departments = {
		IM: P('الباطنة', 'Internal medicine'), PED: P('الأطفال', 'Paediatrics'), OBG: P('النساء والولادة', 'Obstetrics & gynaecology'),
		ORTH: P('العظام', 'Orthopaedics'), CARD: P('القلب', 'Cardiology'), GS: P('الجراحة العامة', 'General surgery'),
		ENT: P('الأنف والأذن', 'ENT'), DERM: P('الجلدية', 'Dermatology'), ER: P('الطوارئ', 'Emergency'), ANES: P('التخدير', 'Anaesthesia')
	};

	D.staff = {
		me: { id: 'S0', name: P('د. أحمد زكريا', 'Dr. Ahmed Zakarya'), role: P('المدير الطبي', 'Medical director'), photo: '300-1.jpg' },
		doctors: [
			{ id: 'D1', name: P('د. سارة القحطاني', 'Dr. Sara Alqahtani'), dept: 'IM', title: P('استشاري باطنة', 'Consultant internist'), photo: '300-12.jpg', room: '101' },
			{ id: 'D2', name: P('د. خالد العتيبي', 'Dr. Khaled Alotaibi'), dept: 'CARD', title: P('استشاري قلب', 'Consultant cardiologist'), photo: '300-5.jpg', room: '204' },
			{ id: 'D3', name: P('د. منى السيد', 'Dr. Mona Elsayed'), dept: 'PED', title: P('أخصائي أطفال', 'Paediatric specialist'), photo: '300-6.jpg', room: '110' },
			{ id: 'D4', name: P('د. ياسر الحربي', 'Dr. Yasser Alharbi'), dept: 'ORTH', title: P('استشاري عظام', 'Consultant orthopaedic surgeon'), photo: '300-17.jpg', room: '301' },
			{ id: 'D5', name: P('د. ريم الشهري', 'Dr. Reem Alshehri'), dept: 'OBG', title: P('استشاري نساء وولادة', 'Consultant obstetrician'), photo: '300-4.jpg', room: '120' },
			{ id: 'D6', name: P('د. عمر النجار', 'Dr. Omar Alnajjar'), dept: 'GS', title: P('استشاري جراحة', 'Consultant surgeon'), photo: '300-13.jpg', room: '305' },
			{ id: 'D7', name: P('د. هالة منصور', 'Dr. Hala Mansour'), dept: 'ER', title: P('طبيب طوارئ', 'Emergency physician'), photo: '300-20.jpg', room: 'ER' },
			{ id: 'D8', name: P('د. فهد الدوسري', 'Dr. Fahad Aldosari'), dept: 'ANES', title: P('استشاري تخدير', 'Consultant anaesthetist'), photo: '300-15.jpg', room: 'OR' }
		],
		nurses: [
			{ id: 'N1', name: P('نورة الزهراني', 'Noura Alzahrani'), title: P('رئيسة تمريض الجناح ٣', 'Charge nurse, Ward 3'), photo: '300-24.jpg' },
			{ id: 'N2', name: P('ماريا سانتوس', 'Maria Santos'), title: P('ممرضة', 'Staff nurse'), photo: '300-2.jpg' },
			{ id: 'N3', name: P('عبدالله يوسف', 'Abdullah Youssef'), title: P('ممرض', 'Staff nurse'), photo: '300-21.jpg' }
		]
	};
	D.doc = function (id) { return D.staff.doctors.filter(function (d) { return d.id === id; })[0] || D.staff.doctors[0]; };

	D.payers = [
		{ id: 'CASH', name: P('نقدي', 'Cash'), kind: 'cash' },
		{ id: 'NIC', name: P('الوطنية للتأمين الصحي', 'National Health Insurance Co.'), kind: 'ins', classes: 'VIP · A · B · C', discount: 15, copay: 20, cap: 500, priceList: P('قائمة A ٢٠٢٦', 'List A 2026'), from: '2026-01-01', to: '2026-12-31', claims: 412, rejectRate: 4.2 },
		{ id: 'GHC', name: P('الخليج للرعاية الصحية', 'Gulf Health Care'), kind: 'ins', classes: 'A · B', discount: 12, copay: 10, cap: 300, priceList: P('قائمة الخليج', 'Gulf list'), from: '2026-03-01', to: '2027-02-28', claims: 268, rejectRate: 7.9 },
		{ id: 'TKF', name: P('تكافل الشرق', 'Al Sharq Takaful'), kind: 'ins', classes: 'Gold · Silver', discount: 10, copay: 15, cap: 400, priceList: P('القائمة العامة', 'General list'), from: '2025-07-01', to: '2026-06-30', claims: 133, rejectRate: 11.4 },
		{ id: 'CORP', name: P('شركة النخبة للمقاولات (عقد شركات)', 'Elite Contracting (corporate)'), kind: 'corp', classes: '—', discount: 20, copay: 0, cap: 0, priceList: P('قائمة الشركات', 'Corporate list'), from: '2026-01-01', to: '2026-12-31', claims: 58, rejectRate: 1.7 },
		{ id: 'GOV', name: P('برنامج حكومي', 'Government programme'), kind: 'gov', classes: '—', discount: 25, copay: 0, cap: 0, priceList: P('تسعيرة البرنامج', 'Programme tariff'), from: '2026-01-01', to: '2026-12-31', claims: 96, rejectRate: 2.1 }
	];
	D.payer = function (id) { return D.payers.filter(function (p) { return p.id === id; })[0] || D.payers[0]; };

	D.patients = [
		{ mrn: 'MRN-100231', name: P('محمد عبدالرحمن الغامدي', 'Mohammed Abdulrahman Alghamdi'), sex: 'M', dob: '1968-04-12', nid: '1023847561', phone: '+966 55 120 4471', nat: P('سعودي', 'Saudi'), payer: 'NIC', cls: 'A', policy: 'NIC-88412-A', blood: 'O+', allergies: [P('البنسلين', 'Penicillin')], chronic: [P('سكري النوع الثاني', 'Type 2 diabetes'), P('ارتفاع ضغط الدم', 'Hypertension')], status: 'ip', loc: P('جناح ٣ · سرير ٣٠٢', 'Ward 3 · Bed 302'), photo: '300-7.jpg', last: '2026-11-01' },
		{ mrn: 'MRN-100245', name: P('فاطمة حسن الزهراني', 'Fatimah Hassan Alzahrani'), sex: 'F', dob: '1991-09-03', nid: '1098834120', phone: '+966 50 778 3301', nat: P('سعودية', 'Saudi'), payer: 'GHC', cls: 'B', policy: 'GHC-55120-B', blood: 'A+', allergies: [], chronic: [], status: 'op', loc: P('عيادة النساء ١٢٠', 'OBG clinic 120'), photo: '300-27.jpg', last: '2026-11-01' },
		{ mrn: 'MRN-100260', name: P('أحمد محمود عبدالله', 'Ahmed Mahmoud Abdullah'), sex: 'M', dob: '1985-01-22', nid: '2456789012', phone: '+966 56 401 2290', nat: P('مصري', 'Egyptian'), payer: 'CORP', cls: '—', policy: 'ELITE-0412', blood: 'B+', allergies: [P('الأسبرين', 'Aspirin')], chronic: [], status: 'er', loc: P('الطوارئ · سرير ٤', 'ER · Bay 4'), photo: '300-25.jpg', last: '2026-11-01' },
		{ mrn: 'MRN-100274', name: P('ليان سعد القحطاني', 'Layan Saad Alqahtani'), sex: 'F', dob: '2019-06-15', nid: '1145502210', phone: '+966 55 933 1120', nat: P('سعودية', 'Saudi'), payer: 'NIC', cls: 'VIP', policy: 'NIC-77310-V', blood: 'O-', allergies: [P('الفول السوداني', 'Peanuts')], chronic: [P('ربو', 'Asthma')], status: 'op', loc: P('عيادة الأطفال ١١٠', 'Paediatrics 110'), photo: null, last: '2026-11-01' },
		{ mrn: 'MRN-100288', name: P('عبدالعزيز فهد المطيري', 'Abdulaziz Fahad Almutairi'), sex: 'M', dob: '1957-11-30', nid: '1002233441', phone: '+966 50 300 8812', nat: P('سعودي', 'Saudi'), payer: 'GOV', cls: '—', policy: 'GOV-2210', blood: 'A-', allergies: [], chronic: [P('قصور القلب', 'Heart failure'), P('رجفان أذيني', 'Atrial fibrillation')], status: 'ip', loc: P('العناية المركزة · سرير ٢', 'ICU · Bed 2'), photo: null, last: '2026-10-29' },
		{ mrn: 'MRN-100301', name: P('جون ماثيو', 'John Mathew'), sex: 'M', dob: '1979-03-08', nid: '2398871100', phone: '+966 59 112 0043', nat: P('هندي', 'Indian'), payer: 'GHC', cls: 'A', policy: 'GHC-11873-A', blood: 'B-', allergies: [], chronic: [], status: 'op', loc: P('عيادة العظام ٣٠١', 'Orthopaedics 301'), photo: '300-29.jpg', last: '2026-11-01' },
		{ mrn: 'MRN-100317', name: P('نوف عبدالله الشمري', 'Nouf Abdullah Alshammari'), sex: 'F', dob: '1996-12-19', nid: '1123345570', phone: '+966 53 220 9910', nat: P('سعودية', 'Saudi'), payer: 'TKF', cls: 'Gold', policy: 'TKF-30091-G', blood: 'AB+', allergies: [], chronic: [], status: 'ip', loc: P('جناح الولادة · سرير ٥٠٤', 'Maternity · Bed 504'), photo: '300-14.jpg', last: '2026-10-31' },
		{ mrn: 'MRN-100329', name: P('سامي إبراهيم حداد', 'Sami Ibrahim Haddad'), sex: 'M', dob: '1972-07-01', nid: '2345120098', phone: '+966 54 812 7765', nat: P('أردني', 'Jordanian'), payer: 'CASH', cls: '—', policy: '—', blood: 'O+', allergies: [P('اليود', 'Iodine contrast')], chronic: [P('حصوات الكلى', 'Kidney stones')], status: 'op', loc: P('الأشعة', 'Radiology'), photo: '300-30.jpg', last: '2026-11-01' },
		{ mrn: 'MRN-100342', name: P('هيا خالد العنزي', 'Haya Khaled Alanazi'), sex: 'F', dob: '1988-02-27', nid: '1087761123', phone: '+966 55 441 0093', nat: P('سعودية', 'Saudi'), payer: 'NIC', cls: 'B', policy: 'NIC-90233-B', blood: 'A+', allergies: [], chronic: [P('قصور الغدة الدرقية', 'Hypothyroidism')], status: 'none', loc: null, photo: null, last: '2026-10-14' },
		{ mrn: 'MRN-100356', name: P('يوسف علي البلوشي', 'Yousef Ali Albalushi'), sex: 'M', dob: '2001-05-05', nid: '1156678823', phone: '+966 56 990 1022', nat: P('سعودي', 'Saudi'), payer: 'GHC', cls: 'B', policy: 'GHC-66310-B', blood: 'O+', allergies: [], chronic: [], status: 'or', loc: P('غرفة العمليات ٢', 'Theatre 2'), photo: '300-9.jpg', last: '2026-11-01' },
		{ mrn: 'MRN-100368', name: P('مريم أحمد الدوسري', 'Maryam Ahmed Aldosari'), sex: 'F', dob: '1949-08-18', nid: '1000981123', phone: '+966 50 120 7781', nat: P('سعودية', 'Saudi'), payer: 'GOV', cls: '—', policy: 'GOV-8812', blood: 'B+', allergies: [P('السلفا', 'Sulfa drugs')], chronic: [P('فشل كلوي مزمن', 'Chronic kidney disease')], status: 'ip', loc: P('العناية المركزة · سرير ٤', 'ICU · Bed 4'), photo: null, last: '2026-10-30' },
		{ mrn: 'MRN-100371', name: P('كريم وليد مصطفى', 'Karim Walid Mostafa'), sex: 'M', dob: '1993-10-10', nid: '2501122334', phone: '+966 58 775 3302', nat: P('مصري', 'Egyptian'), payer: 'TKF', cls: 'Silver', policy: 'TKF-44120-S', blood: 'A+', allergies: [], chronic: [], status: 'er', loc: P('الطوارئ · انتظار', 'ER · Waiting'), photo: '300-23.jpg', last: '2026-11-01' }
	];
	// Further inpatients so every occupied bed holds a different person.
	[
		['MRN-100402', 'سلمان ناصر العمري', 'Salman Nasser Alomari', 'M', '1964-02-11', 'NIC', null],
		['MRN-100405', 'عائشة محمد باوزير', 'Aisha Mohammed Bawazir', 'F', '1983-06-21', 'GHC', '300-18.jpg'],
		['MRN-100409', 'تركي سعود الشهري', 'Turki Saud Alshehri', 'M', '1971-09-14', 'GOV', null],
		['MRN-100411', 'رنا جمال الخطيب', 'Rana Jamal Alkhatib', 'F', '1990-03-30', 'TKF', '300-10.jpg'],
		['MRN-100414', 'بدر عبدالله الرشيد', 'Badr Abdullah Alrashid', 'M', '1959-12-02', 'NIC', null],
		['MRN-100418', 'غادة صالح المالكي', 'Ghada Saleh Almalki', 'F', '1994-07-08', 'NIC', '300-28.jpg'],
		['MRN-100421', 'إبراهيم حسين قاسم', 'Ibrahim Hussein Qasem', 'M', '1977-05-17', 'CASH', '300-19.jpg'],
		['MRN-100425', 'منيرة فيصل السبيعي', 'Munirah Faisal Alsubaie', 'F', '1952-10-25', 'GOV', null],
		['MRN-100428', 'راكان مشعل الجهني', 'Rakan Mishal Aljohani', 'M', '2008-01-09', 'GHC', '300-11.jpg'],
		['MRN-100431', 'دانة وليد الصالح', 'Dana Walid Alsaleh', 'F', '1999-11-11', 'TKF', '300-22.jpg'],
		['MRN-100434', 'حمد علي اليامي', 'Hamad Ali Alyami', 'M', '1966-08-03', 'CORP', null],
		['MRN-100437', 'شهد ماجد الحربي', 'Shahad Majed Alharbi', 'F', '2015-04-19', 'NIC', null]
	].forEach(function (r) {
		D.patients.push({ mrn: r[0], name: P(r[1], r[2]), sex: r[3], dob: r[4], nid: '10' + r[0].slice(-6) + '17', phone: '+966 5' + r[0].slice(-4) + ' 0000', nat: P('سعودي', 'Saudi'), payer: r[5], cls: 'B', policy: r[5] + '-' + r[0].slice(-5), blood: 'O+', allergies: [], chronic: [], status: 'ip', loc: null, photo: r[6], last: '2026-10-30' });
	});
	D.patient = function (mrn) { return D.patients.filter(function (p) { return p.mrn === mrn; })[0] || D.patients[0]; };

	// Appointments: one day, slots by doctor. st: booked | arrived | inroom | done | noshow
	D.slots = ['08:00', '08:30', '09:00', '09:30', '10:00', '10:30', '11:00', '11:30', '12:00', '12:30', '13:00', '13:30'];
	D.appointments = [
		{ d: 'D1', t: '08:00', p: 'MRN-100342', st: 'done', type: 'fu' }, { d: 'D1', t: '08:30', p: 'MRN-100301', st: 'done', type: 'new' },
		{ d: 'D1', t: '09:30', p: 'MRN-100260', st: 'noshow', type: 'fu' }, { d: 'D1', t: '10:00', p: 'MRN-100245', st: 'inroom', type: 'new' },
		{ d: 'D1', t: '10:30', p: 'MRN-100329', st: 'arrived', type: 'fu' }, { d: 'D1', t: '11:00', p: 'MRN-100356', st: 'booked', type: 'new' },
		{ d: 'D1', t: '12:00', p: 'MRN-100368', st: 'booked', type: 'fu' },
		{ d: 'D2', t: '08:00', p: 'MRN-100288', st: 'done', type: 'fu' }, { d: 'D2', t: '09:00', p: 'MRN-100231', st: 'done', type: 'fu' },
		{ d: 'D2', t: '10:00', p: 'MRN-100371', st: 'inroom', type: 'new' }, { d: 'D2', t: '11:30', p: 'MRN-100342', st: 'booked', type: 'new' },
		{ d: 'D3', t: '08:30', p: 'MRN-100274', st: 'done', type: 'fu' }, { d: 'D3', t: '09:30', p: 'MRN-100274', st: 'arrived', type: 'proc' },
		{ d: 'D3', t: '10:30', p: 'MRN-100245', st: 'booked', type: 'new' }, { d: 'D3', t: '12:30', p: 'MRN-100317', st: 'booked', type: 'fu' },
		{ d: 'D4', t: '09:00', p: 'MRN-100301', st: 'inroom', type: 'fu' }, { d: 'D4', t: '10:00', p: 'MRN-100356', st: 'arrived', type: 'new' },
		{ d: 'D4', t: '11:00', p: 'MRN-100329', st: 'booked', type: 'proc' }, { d: 'D4', t: '13:00', p: 'MRN-100260', st: 'booked', type: 'fu' },
		{ d: 'D5', t: '08:00', p: 'MRN-100317', st: 'done', type: 'fu' }, { d: 'D5', t: '09:30', p: 'MRN-100245', st: 'done', type: 'new' },
		{ d: 'D5', t: '10:30', p: 'MRN-100342', st: 'inroom', type: 'fu' }, { d: 'D5', t: '12:00', p: 'MRN-100317', st: 'booked', type: 'proc' }
	];

	// Emergency: stage triage | waiting | treatment | observation | disposition
	D.er = [
		{ p: 'MRN-100260', esi: 2, stage: 'treatment', cc: P('ألم في الصدر منذ ساعتين', 'Chest pain for 2 hours'), mins: 38, bay: '4', doc: 'D7', vitals: 'BP 158/96 · HR 112 · SpO₂ 95%' },
		{ p: 'MRN-100371', esi: 3, stage: 'waiting', cc: P('ألم في البطن وقيء', 'Abdominal pain and vomiting'), mins: 52, bay: '—', doc: null, vitals: 'BP 124/80 · HR 96 · T 38.1' },
		{ p: 'MRN-100356', esi: 4, stage: 'disposition', cc: P('جرح في اليد', 'Hand laceration'), mins: 95, bay: '7', doc: 'D7', vitals: 'BP 118/76 · HR 82' },
		{ p: 'MRN-100431', esi: 1, stage: 'treatment', cc: P('ضيق تنفس شديد', 'Severe shortness of breath'), mins: 12, bay: 'R1', doc: 'D2', vitals: 'BP 92/58 · HR 134 · SpO₂ 86%' },
		{ p: 'MRN-100342', esi: 5, stage: 'triage', cc: P('تجديد وصفة', 'Prescription refill'), mins: 6, bay: '—', doc: null, vitals: '—' },
		{ p: 'MRN-100329', esi: 3, stage: 'observation', cc: P('مغص كلوي', 'Renal colic'), mins: 140, bay: 'O2', doc: 'D7', vitals: 'BP 136/88 · HR 90' },
		{ p: 'MRN-100274', esi: 3, stage: 'waiting', cc: P('حرارة وكحة لطفلة', 'Child with fever and cough'), mins: 24, bay: '—', doc: null, vitals: 'T 38.9 · SpO₂ 96%' },
		{ p: 'MRN-100245', esi: 4, stage: 'triage', cc: P('التواء الكاحل', 'Ankle sprain'), mins: 4, bay: '—', doc: null, vitals: '—' }
	];

	// Wards and beds. st: occupied | vacant | cleaning | reserved | blocked
	function beds(prefix, list) { return list.map(function (b, i) { return Object.assign({ no: prefix + String(i + 1).padStart(2, '0') }, b); }); }
	D.wards = [
		{ id: 'W3', name: P('جناح ٣ — باطنة رجال', 'Ward 3 — Medical (male)'), beds: beds('3', [
			{ st: 'occupied', p: 'MRN-100231', dx: P('التهاب رئوي', 'Pneumonia'), los: 3, doc: 'D1' }, { st: 'occupied', p: 'MRN-100402', dx: P('التهاب خلوي', 'Cellulitis'), los: 1, doc: 'D1' },
			{ st: 'vacant' }, { st: 'cleaning' }, { st: 'occupied', p: 'MRN-100434', dx: P('قصور كلوي حاد', 'Acute kidney injury'), los: 2, doc: 'D1' },
			{ st: 'reserved', p: 'MRN-100421' }, { st: 'occupied', p: 'MRN-100409', dx: P('مغص كلوي', 'Renal colic'), los: 1, doc: 'D1' }, { st: 'vacant' },
			{ st: 'occupied', p: 'MRN-100414', dx: P('ألم صدري للملاحظة', 'Chest pain, observation'), los: 1, doc: 'D2' }, { st: 'blocked' }]) },
		{ id: 'W5', name: P('جناح ٥ — الولادة', 'Ward 5 — Maternity'), beds: beds('5', [
			{ st: 'occupied', p: 'MRN-100405', dx: P('ما بعد الولادة', 'Postpartum'), los: 1, doc: 'D5' }, { st: 'vacant' }, { st: 'occupied', p: 'MRN-100411', dx: P('سكري الحمل', 'Gestational diabetes'), los: 2, doc: 'D5' },
			{ st: 'occupied', p: 'MRN-100317', dx: P('قيصرية — يوم ٢', 'C-section — day 2'), los: 2, doc: 'D5' }, { st: 'vacant' }, { st: 'cleaning' }]) },
		{ id: 'ICU', name: P('العناية المركزة', 'Intensive care unit'), beds: beds('I', [
			{ st: 'occupied', p: 'MRN-100425', dx: P('صدمة إنتانية', 'Septic shock'), los: 4, doc: 'D2' }, { st: 'occupied', p: 'MRN-100288', dx: P('قصور قلب حاد', 'Acute heart failure'), los: 3, doc: 'D2' },
			{ st: 'vacant' }, { st: 'occupied', p: 'MRN-100368', dx: P('ما بعد العملية', 'Post-operative'), los: 1, doc: 'D6' }]) },
		{ id: 'W2', name: P('جناح ٢ — جراحة', 'Ward 2 — Surgical'), beds: beds('2', [
			{ st: 'cleaning' }, { st: 'occupied', p: 'MRN-100418', dx: P('كسر عنق الفخذ', 'Hip fracture'), los: 5, doc: 'D4' },
			{ st: 'vacant' }, { st: 'vacant' }, { st: 'reserved', p: 'MRN-100260' }, { st: 'occupied', p: 'MRN-100428', dx: P('ما بعد منظار المرارة', 'Post cholecystectomy'), los: 2, doc: 'D6' },
			{ st: 'cleaning' }, { st: 'occupied', p: 'MRN-100437', dx: P('استئصال اللوز', 'Tonsillectomy'), los: 1, doc: 'D6' }]) }
	];

	// Medication administration record for Ward 3. cells: g given, d due, m missed, h held, '' none
	D.mar = [
		{ p: 'MRN-100231', bed: '301', drug: P('سيفترياكسون ١ جم وريدي', 'Ceftriaxone 1 g IV'), freq: P('كل ٢٤ ساعة', 'q24h'), cells: { '06': 'g', '10': '', '14': '', '18': '', '22': '' } },
		{ p: 'MRN-100231', bed: '301', drug: P('إنسولين جلارجين ٢٠ وحدة', 'Insulin glargine 20 U'), freq: P('مساءً', 'nightly'), cells: { '06': '', '10': '', '14': '', '18': '', '22': 'd' } },
		{ p: 'MRN-100231', bed: '301', drug: P('أملوديبين ٥ ملجم', 'Amlodipine 5 mg'), freq: P('يومياً', 'daily'), cells: { '06': '', '10': 'g', '14': '', '18': '', '22': '' } },
		{ p: 'MRN-100301', bed: '302', drug: P('كلينداميسين ٦٠٠ ملجم وريدي', 'Clindamycin 600 mg IV'), freq: P('كل ٨ ساعات', 'q8h'), cells: { '06': 'g', '10': '', '14': 'd', '18': '', '22': 'd' } },
		{ p: 'MRN-100434', bed: '305', drug: P('فوروسيميد ٤٠ ملجم وريدي', 'Furosemide 40 mg IV'), freq: P('مرتين يومياً', 'bid'), cells: { '06': 'm', '10': '', '14': '', '18': 'd', '22': '' } },
		{ p: 'MRN-100434', bed: '305', drug: P('باراسيتامول ١ جم', 'Paracetamol 1 g'), freq: P('عند اللزوم', 'prn'), cells: { '06': '', '10': 'h', '14': '', '18': '', '22': '' } },
		{ p: 'MRN-100329', bed: '307', drug: P('كيتورولاك ٣٠ ملجم وريدي', 'Ketorolac 30 mg IV'), freq: P('كل ٦ ساعات', 'q6h'), cells: { '06': 'g', '10': '', '14': 'd', '18': '', '22': 'd' } }
	];

	// Theatre: start hour (decimal), duration hours. st: done | live | planned
	D.theatres = [P('غرفة ١', 'Theatre 1'), P('غرفة ٢', 'Theatre 2'), P('غرفة ٣', 'Theatre 3'), P('غرفة الولادة', 'Delivery suite')];
	D.surgeries = [
		{ room: 0, start: 8, dur: 1.5, p: 'MRN-100329', proc: P('استئصال المرارة بالمنظار', 'Laparoscopic cholecystectomy'), surgeon: 'D6', anes: 'D8', st: 'done', cpt: '47562' },
		{ room: 0, start: 10, dur: 2, p: 'MRN-100301', proc: P('تثبيت كسر عنق الفخذ', 'Hip fracture fixation'), surgeon: 'D4', anes: 'D8', st: 'live', cpt: '27236' },
		{ room: 0, start: 13, dur: 1, p: 'MRN-100274', proc: P('استئصال اللوزتين', 'Tonsillectomy'), surgeon: 'D6', anes: 'D8', st: 'planned', cpt: '42826' },
		{ room: 1, start: 8.5, dur: 1, p: 'MRN-100356', proc: P('استئصال الزائدة', 'Appendectomy'), surgeon: 'D6', anes: 'D8', st: 'done', cpt: '44970' },
		{ room: 1, start: 11, dur: 2.5, p: 'MRN-100260', proc: P('قسطرة قلبية تشخيصية', 'Diagnostic cardiac catheterisation'), surgeon: 'D2', anes: 'D8', st: 'planned', cpt: '93458' },
		{ room: 2, start: 9, dur: 1.5, p: 'MRN-100231', proc: P('منظار قولون', 'Colonoscopy'), surgeon: 'D1', anes: 'D8', st: 'done', cpt: '45378' },
		{ room: 3, start: 9.5, dur: 1.5, p: 'MRN-100317', proc: P('ولادة قيصرية', 'Caesarean section'), surgeon: 'D5', anes: 'D8', st: 'done', cpt: '59510' },
		{ room: 3, start: 14, dur: 1, p: 'MRN-100342', proc: P('ولادة طبيعية متوقعة', 'Expected vaginal delivery'), surgeon: 'D5', anes: 'D8', st: 'planned', cpt: '59400' }
	];

	// Laboratory. st: ordered | collected | processing | verified
	D.labOrders = [
		{ id: 'LAB-24117', p: 'MRN-100231', test: P('صورة دم كاملة', 'Complete blood count'), code: 'CBC', prio: 'stat', st: 'processing', from: P('العناية المركزة', 'ICU'), doc: 'D2', time: '09:12' },
		{ id: 'LAB-24118', p: 'MRN-100231', test: P('وظائف كلى', 'Renal function panel'), code: 'RFT', prio: 'stat', st: 'verified', from: P('العناية المركزة', 'ICU'), doc: 'D2', time: '09:12' },
		{ id: 'LAB-24120', p: 'MRN-100260', test: P('إنزيمات القلب (تروبونين)', 'Cardiac troponin I'), code: 'TROP', prio: 'stat', st: 'verified', from: P('الطوارئ', 'ER'), doc: 'D7', time: '09:40', critical: true },
		{ id: 'LAB-24124', p: 'MRN-100245', test: P('تحليل الحمل الكمي', 'Quantitative β-hCG'), code: 'BHCG', prio: 'routine', st: 'collected', from: P('عيادة النساء', 'OBG clinic'), doc: 'D5', time: '10:05' },
		{ id: 'LAB-24126', p: 'MRN-100301', test: P('سكر تراكمي', 'HbA1c'), code: 'HBA1C', prio: 'routine', st: 'ordered', from: P('عيادة الباطنة', 'IM clinic'), doc: 'D1', time: '10:20' },
		{ id: 'LAB-24127', p: 'MRN-100274', test: P('مزرعة حلق', 'Throat culture'), code: 'CULT', prio: 'routine', st: 'processing', from: P('عيادة الأطفال', 'Paediatrics'), doc: 'D3', time: '10:31' },
		{ id: 'LAB-24129', p: 'MRN-100434', test: P('أملاح الدم', 'Electrolytes'), code: 'LYTES', prio: 'urgent', st: 'collected', from: P('جناح ٣', 'Ward 3'), doc: 'D1', time: '10:44' },
		{ id: 'LAB-24131', p: 'MRN-100371', test: P('إنزيمات البنكرياس', 'Lipase'), code: 'LIP', prio: 'urgent', st: 'ordered', from: P('الطوارئ', 'ER'), doc: 'D7', time: '10:52' }
	];
	D.cbc = [
		{ t: P('كريات الدم البيضاء', 'WBC'), v: 15.8, u: '10³/µL', lo: 4.0, hi: 11.0 },
		{ t: P('كريات الدم الحمراء', 'RBC'), v: 4.1, u: '10⁶/µL', lo: 4.5, hi: 5.9 },
		{ t: P('الهيموجلوبين', 'Haemoglobin'), v: 11.9, u: 'g/dL', lo: 13.5, hi: 17.5 },
		{ t: P('الهيماتوكريت', 'Haematocrit'), v: 36.2, u: '%', lo: 41, hi: 53 },
		{ t: P('الصفائح الدموية', 'Platelets'), v: 245, u: '10³/µL', lo: 150, hi: 400 },
		{ t: P('العدلات', 'Neutrophils'), v: 84, u: '%', lo: 40, hi: 75 },
		{ t: P('الخلايا الليمفاوية', 'Lymphocytes'), v: 11, u: '%', lo: 20, hi: 45 }
	];

	// Radiology. st: scheduled | arrived | scanning | reporting | signed
	D.radOrders = [
		{ id: 'RAD-8812', p: 'MRN-100260', exam: P('أشعة صدر', 'Chest X-ray'), mod: 'XR', st: 'signed', prio: 'stat', room: 'XR-1', doc: 'D7' },
		{ id: 'RAD-8815', p: 'MRN-100329', exam: P('أشعة مقطعية للكلى بدون صبغة', 'CT KUB, non-contrast'), mod: 'CT', st: 'reporting', prio: 'urgent', room: 'CT-1', doc: 'D7', contrastAlert: true },
		{ id: 'RAD-8817', p: 'MRN-100245', exam: P('موجات صوتية للحوض', 'Pelvic ultrasound'), mod: 'US', st: 'scanning', prio: 'routine', room: 'US-2', doc: 'D5' },
		{ id: 'RAD-8819', p: 'MRN-100301', exam: P('رنين مغناطيسي للركبة', 'MRI knee'), mod: 'MR', st: 'scheduled', prio: 'routine', room: 'MR-1', doc: 'D4' },
		{ id: 'RAD-8820', p: 'MRN-100402', exam: P('إيكو القلب', 'Echocardiogram'), mod: 'US', st: 'arrived', prio: 'urgent', room: 'ECHO', doc: 'D2' },
		{ id: 'RAD-8822', p: 'MRN-100371', exam: P('أشعة مقطعية للبطن بالصبغة', 'CT abdomen with contrast'), mod: 'CT', st: 'scheduled', prio: 'urgent', room: 'CT-1', doc: 'D7' }
	];

	// Pharmacy: prescriptions waiting; stock comes from Inventory batches (FEFO).
	D.rx = [
		{ id: 'RX-55021', p: 'MRN-100245', doc: 'D5', src: 'op', st: 'new', items: [
			{ drug: P('حمض الفوليك ٥ ملجم', 'Folic acid 5 mg'), sig: P('قرص يومياً · ٣٠ يوم', '1 tab daily · 30 days'), qty: 30, price: 0.6, batch: 'FA-2611', exp: '2027-08', stock: 840 },
			{ drug: P('حديد ٣٢٥ ملجم', 'Ferrous sulfate 325 mg'), sig: P('قرص يومياً · ٣٠ يوم', '1 tab daily · 30 days'), qty: 30, price: 0.9, batch: 'FS-2604', exp: '2027-02', stock: 410 }] },
		{ id: 'RX-55024', p: 'MRN-100301', doc: 'D4', src: 'op', st: 'new', interaction: true, items: [
			{ drug: P('ديكلوفيناك ٥٠ ملجم', 'Diclofenac 50 mg'), sig: P('قرص مرتين · ٥ أيام', '1 tab bid · 5 days'), qty: 10, price: 1.4, batch: 'DC-2509', exp: '2026-12', stock: 96 },
			{ drug: P('أوميبرازول ٢٠ ملجم', 'Omeprazole 20 mg'), sig: P('كبسولة يومياً · ١٤ يوم', '1 cap daily · 14 days'), qty: 14, price: 1.1, batch: 'OM-2702', exp: '2027-09', stock: 1200 }] },
		{ id: 'RX-55026', p: 'MRN-100274', doc: 'D3', src: 'op', st: 'verified', items: [
			{ drug: P('أموكسيسيلين شراب ٢٥٠ ملجم', 'Amoxicillin susp. 250 mg/5 ml'), sig: P('٥ مل ٣ مرات · ٧ أيام', '5 ml tid · 7 days'), qty: 1, price: 18, batch: 'AX-2603', exp: '2026-11', stock: 22, nearExpiry: true }] },
		{ id: 'RX-55027', p: 'MRN-100231', doc: 'D1', src: 'ip', st: 'new', items: [
			{ drug: P('سيفترياكسون ١ جم فيال', 'Ceftriaxone 1 g vial'), sig: P('وريدي كل ٢٤ ساعة · ٥ أيام', 'IV q24h · 5 days'), qty: 5, price: 22, batch: 'CF-2610', exp: '2027-06', stock: 140 }] },
		{ id: 'RX-55029', p: 'MRN-100371', doc: 'D7', src: 'er', st: 'dispensed', items: [
			{ drug: P('أوندانسيترون ٤ ملجم', 'Ondansetron 4 mg'), sig: P('عند اللزوم', 'prn'), qty: 6, price: 2.5, batch: 'ON-2701', exp: '2027-03', stock: 300 }] }
	];

	// One encounter bill: an inpatient stay, insured.
	D.bill = {
		no: 'INV-H-2026-01842', p: 'MRN-100231', enc: 'ADM-2026-0912', from: '2026-10-29', to: '2026-11-01',
		lines: [
			{ code: 'ROOM-W3', d: P('إقامة جناح — ٣ ليالٍ', 'Ward stay — 3 nights'), dept: 'IM', qty: 3, price: 650, cov: 80 },
			{ code: 'ICU-DAY', d: P('عناية مركزة — يوم', 'ICU — per day'), dept: 'IM', qty: 1, price: 2400, cov: 100 },
			{ code: 'CONS-IM', d: P('زيارة استشاري يومية', 'Daily consultant visit'), dept: 'IM', qty: 3, price: 300, cov: 80 },
			{ code: 'LAB-CBC', d: P('صورة دم كاملة', 'Complete blood count'), dept: 'LAB', qty: 2, price: 85, cov: 80 },
			{ code: 'LAB-RFT', d: P('وظائف كلى', 'Renal function panel'), dept: 'LAB', qty: 2, price: 140, cov: 80 },
			{ code: 'RAD-CXR', d: P('أشعة صدر', 'Chest X-ray'), dept: 'RAD', qty: 1, price: 180, cov: 80 },
			{ code: 'PH-CEF1G', d: P('سيفترياكسون ١ جم', 'Ceftriaxone 1 g'), dept: 'PH', qty: 4, price: 22, cov: 80 },
			{ code: 'CONS-NUR', d: P('مستهلكات تمريض', 'Nursing consumables'), dept: 'NUR', qty: 1, price: 260, cov: 0 }
		],
		paid: 500
	};

	D.preauths = [
		{ id: 'PA-3301', p: 'MRN-100260', payer: 'CORP', svc: P('قسطرة قلبية تشخيصية', 'Diagnostic cardiac catheterisation'), amt: 14500, st: 'pending', sent: '10:58' },
		{ id: 'PA-3298', p: 'MRN-100301', payer: 'GHC', svc: P('رنين مغناطيسي للركبة', 'MRI knee'), amt: 1900, st: 'approved', sent: '09:20' },
		{ id: 'PA-3296', p: 'MRN-100317', payer: 'TKF', svc: P('تمديد إقامة الولادة يومين', 'Maternity stay extension, 2 days'), amt: 2600, st: 'partial', sent: '08:41' },
		{ id: 'PA-3290', p: 'MRN-100342', payer: 'NIC', svc: P('فحص الغدة الشامل', 'Comprehensive thyroid panel'), amt: 620, st: 'rejected', sent: 'yesterday', reason: P('خارج التغطية لفئة B', 'Not covered for class B') }
	];
	D.claims = [
		{ id: 'CLB-2026-10', payer: 'NIC', period: '2026-10', count: 186, amount: 248900, st: 'submitted', accepted: 0, rejected: 0 },
		{ id: 'CLB-2026-09', payer: 'NIC', period: '2026-09', count: 201, amount: 263450, st: 'paid', accepted: 252100, rejected: 11350 },
		{ id: 'CLB-2026-10G', payer: 'GHC', period: '2026-10', count: 97, amount: 118300, st: 'draft', accepted: 0, rejected: 0 },
		{ id: 'CLB-2026-09G', payer: 'GHC', period: '2026-09', count: 104, amount: 126700, st: 'partial', accepted: 108400, rejected: 18300 },
		{ id: 'CLB-2026-09T', payer: 'TKF', period: '2026-09', count: 41, amount: 52900, st: 'rejected-lines', accepted: 43100, rejected: 9800 }
	];
	D.rejections = [
		{ claim: 'CLB-2026-09G', p: 'MRN-100301', line: P('رنين مغناطيسي للركبة', 'MRI knee'), code: 'MN-01', reason: P('لا يوجد ما يبرر الضرورة الطبية', 'Medical necessity not justified'), amt: 1900 },
		{ claim: 'CLB-2026-09G', p: 'MRN-100245', line: P('موجات صوتية للحوض', 'Pelvic ultrasound'), code: 'AU-03', reason: P('بدون موافقة مسبقة', 'No prior authorisation'), amt: 650 },
		{ claim: 'CLB-2026-09T', p: 'MRN-100317', line: P('إقامة إضافية', 'Extra stay'), code: 'LM-02', reason: P('تجاوز حد الإقامة', 'Length-of-stay limit exceeded'), amt: 1300 },
		{ claim: 'CLB-2026-09', p: 'MRN-100231', line: P('سيفترياكسون ١ جم', 'Ceftriaxone 1 g'), code: 'PR-07', reason: P('السعر أعلى من قائمة العقد', 'Price above contract list'), amt: 18 }
	];

	D.icd = [
		{ c: 'J18.9', t: P('التهاب رئوي، غير محدد', 'Pneumonia, unspecified organism') },
		{ c: 'E11.9', t: P('داء السكري النوع الثاني بدون مضاعفات', 'Type 2 diabetes mellitus without complications') },
		{ c: 'I10', t: P('ارتفاع ضغط الدم الأساسي', 'Essential (primary) hypertension') },
		{ c: 'R07.4', t: P('ألم في الصدر، غير محدد', 'Chest pain, unspecified') },
		{ c: 'N20.0', t: P('حصوة الكلية', 'Calculus of kidney') },
		{ c: 'J02.9', t: P('التهاب البلعوم الحاد', 'Acute pharyngitis, unspecified') },
		{ c: 'O24.4', t: P('سكري الحمل', 'Gestational diabetes mellitus') }
	];
})();
