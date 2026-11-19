namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // THE HOSPITAL'S PRINTED PAPERS — the bill (an A4 invoice and a clinic receipt are two templates on
    // it), the prescription and the dental insurance claim. Each is a DOCUMENT dataset: one row per line,
    // with the header (the facility, the patient, the doctor, the totals) repeated on every row, so a
    // ReportHeader/ReportFooter band reads it from the first row exactly as the trade documents do.
    //
    // ONE KEY FOR ALL THREE, answered by the hospital module (HospitalReportModuleAuthority), never by the
    // reporting role map: a person who may open the patient's records in the hospital may print them.
    // ============================================================================================
    public static class HospitalDocumentDatasetCodes
    {
        public const string Bill = "Hospital.BillDocument";
        public const string Prescription = "Hospital.PrescriptionDocument";
        /// The prescription as ONE row (a fixed paper: the medicines are one field), so a full-page form never runs to a second page.
        public const string PrescriptionForm = "Hospital.PrescriptionForm";
        public const string DentalClaim = "Hospital.DentalClaimDocument";
        /// H55: the discharge summary of a stay, as ONE row (a fixed paper).
        public const string DischargeSummary = "Hospital.DischargeSummary";
        /// H56: a sick leave certificate, as ONE row.
        public const string SickLeave = "Hospital.SickLeave";
        /// H58: a birth notification, as ONE row.
        public const string BirthNotice = "Hospital.BirthNotice";
        /// H59: a medical certificate of cause of death, as ONE row.
        public const string DeathCertificate = "Hospital.DeathCertificate";
        /// H60: a signed consent (or a signed refusal), as ONE row.
        public const string Consent = "Hospital.Consent";
        /// H63: a patient's account statement — bills, payments and charity waivers with a running balance.
        public const string PatientStatement = "Hospital.PatientStatement";
        /// H109: the specialty visit's papers, each ONE row from the signed visit.
        public const string GlassesRx = "Hospital.GlassesRx";
        public const string EyeLetter = "Hospital.EyeLetter";
        public const string AllergyPlan = "Hospital.AllergyPlan";
        /// H111: one signed clinical drawing (the canvas) — its picture, its marks and its results.
        public const string CanvasDrawing = "Hospital.CanvasDrawing";
        /// H144: the front desk's papers, each ONE row — the visit (its slip and its queue ticket are two layouts of it),
        /// the patient's wristband, and a deposit's receipt.
        public const string VisitSlip = "Hospital.VisitSlip";
        public const string Wristband = "Hospital.Wristband";
        public const string DepositReceipt = "Hospital.DepositReceipt";
        /// H147: the pharmacy's dispensing labels — ONE row per medicine given (a label each), from one dispense.
        public const string RxLabel = "Hospital.RxLabel";
        /// H149: one credit note on a closed bill (medicine brought back), as ONE row.
        public const string CreditNote = "Hospital.CreditNote";
        /// H153: a compounded preparation's bag label, as ONE row.
        public const string CompoundLabel = "Hospital.CompoundLabel";
        /// H204: a referral's letter (to another department or another facility), as ONE row.
        public const string ReferralLetter = "Hospital.ReferralLetter";
        /// H206: a specimen tube's label — the accession number as text and as the QR the bedside scan reads, as ONE row.
        public const string SpecimenLabel = "Hospital.SpecimenLabel";
        public const string CategoryKey = "hospital.reporting";
    }

    public static class HospitalReportPermissions
    {
        public const string Prefix = "hospital.";
        public const string View = "hospital.reports.view";
        /// H156: a report that carries clinical content (a prescription, a discharge summary, a certificate, a label, the
        /// clinical and management analyses) — the care team's (`clinical-read`), never a reception-only person's.
        public const string Clinical = "hospital.reports.clinical";
        /// H49: the financial and insurance management reports — the billing desk's (`bill`), not every reader's.
        public const string Finance = "hospital.reports.finance";
    }

    public static class HospitalDocumentDatasets
    {
        public const int MaxRows = 500;

        private static ReportDatasetField F(string key, string ar, string en, ReportFieldType type = ReportFieldType.String,
            double width = 30, bool visible = false, ReportAlign? align = null) => new()
        {
            Key = key, TitleAr = ar, TitleEn = en, Type = type, WidthMm = width, VisibleByDefault = visible,
            Align = align ?? (type is ReportFieldType.Money or ReportFieldType.Decimal or ReportFieldType.Integer ? ReportAlign.End : ReportAlign.Start),
        };

        // Who issued the paper, who it is about and who signed it — the same keys on all three.
        private static IEnumerable<ReportDatasetField> Header() => new[]
        {
            F("CompanyName", "اسم المنشأة", "Facility"),
            F("CompanyTaxNo", "الرقم الضريبي", "Tax no."),
            F("CompanyAddress", "العنوان", "Address", width: 60),
            F("CompanyPhone", "الهاتف", "Phone"),
            F("BranchName", "الفرع", "Branch"),
            F("DocumentNo", "رقم المستند", "Document no."),
            F("DocumentDate", "التاريخ", "Date", ReportFieldType.DateTime),
            F("PatientName", "اسم المريض", "Patient", width: 50),
            F("PatientMrn", "رقم الملف", "MRN"),
            F("PatientAge", "السن", "Age", width: 14),
            F("PatientSex", "النوع", "Sex", width: 14),
            F("PatientPhone", "الموبايل", "Mobile"),
            F("DoctorName", "الطبيب", "Doctor", width: 50),
            F("DoctorTitle", "التخصص", "Specialty"),
            F("DoctorLicense", "رقم الترخيص", "Licence no."),
            F("Payer", "جهة التأمين", "Insurer"),
            F("PolicyNo", "رقم المؤمَّن عليه", "Insured no."),
        };

        private static ReportParameterDescriptor[] Params(string key, string ar, string en) => new[]
        {
            new ReportParameterDescriptor
            {
                Key = key, TitleAr = ar, TitleEn = en, Type = ReportFieldType.Integer, Required = true,
                HelpTextAr = "معرّف المستند في النظام — تصل إليه الشاشة تلقائيًا.",
                HelpTextEn = "The document's internal id — the screen fills this in for you.",
            },
            AccountingDatasets.CompanyParam(),
        };

        /// H156: the papers with clinical content. The desk's own papers (bill, statement, visit slip, wristband, deposit
        /// receipt, credit note, consent) stay on the module's read.
        public static readonly IReadOnlySet<string> ClinicalDocuments = new HashSet<string>(StringComparer.Ordinal)
        {
            HospitalDocumentDatasetCodes.Prescription, HospitalDocumentDatasetCodes.PrescriptionForm, HospitalDocumentDatasetCodes.DischargeSummary,
            HospitalDocumentDatasetCodes.SickLeave, HospitalDocumentDatasetCodes.BirthNotice, HospitalDocumentDatasetCodes.DeathCertificate,
            HospitalDocumentDatasetCodes.GlassesRx, HospitalDocumentDatasetCodes.EyeLetter, HospitalDocumentDatasetCodes.AllergyPlan,
            HospitalDocumentDatasetCodes.CanvasDrawing, HospitalDocumentDatasetCodes.RxLabel, HospitalDocumentDatasetCodes.CompoundLabel,
            HospitalDocumentDatasetCodes.ReferralLetter,   // H204
            HospitalDocumentDatasetCodes.SpecimenLabel,    // H206
        };

        private static ReportDatasetDefinition Doc(string code, string ar, string en, string descAr, string descEn,
            IEnumerable<ReportDatasetField> lines, IEnumerable<ReportDatasetField> extra, ReportParameterDescriptor[] parameters) => new()
        {
            DatasetCode = code,
            Module = "Hospital",
            TitleAr = ar,
            TitleEn = en,
            DescriptionAr = descAr,
            DescriptionEn = descEn,
            DataSourceKey = code,
            RequiredPermissionKey = ClinicalDocuments.Contains(code) ? HospitalReportPermissions.Clinical : HospitalReportPermissions.View,   // H156
            MaxRows = MaxRows,
            RowCapPolicy = ReportRowCapPolicy.TruncateAndDeclare,
            Fields = lines.Concat(Header()).Concat(extra).ToArray(),
            Parameters = parameters,
        };

        public static ReportDatasetDefinition Bill() => Doc(HospitalDocumentDatasetCodes.Bill,
            "فاتورة مريض", "Patient bill",
            "فاتورة مريض واحدة بكل خدماتها: الكمية والسعر ونصيب التأمين ونصيب المريض والضريبة، مع المدفوع والمتبقي.",
            "One patient bill with every service: quantity, price, the insurer's and the patient's share and VAT, with what was paid and what remains.",
            new[]
            {
                F("LineNo", "م", "#", ReportFieldType.Integer, 10, true, ReportAlign.Center),
                F("ServiceCode", "الكود", "Code", width: 22),
                F("Description", "الخدمة", "Service", width: 62, visible: true),
                F("Category", "الفئة", "Category", width: 24),
                F("Qty", "الكمية", "Qty", ReportFieldType.Decimal, 16, true),
                F("UnitPrice", "السعر", "Price", ReportFieldType.Money, 24, true),
                F("Gross", "الإجمالي", "Amount", ReportFieldType.Money, 26, true),
                F("InsurerShare", "نصيب التأمين", "Insurer share", ReportFieldType.Money, 26),
                F("PatientShare", "نصيب المريض", "Patient share", ReportFieldType.Money, 26),
                F("Vat", "الضريبة", "VAT", ReportFieldType.Money, 22),
                F("Notes", "ملاحظات", "Notes", width: 40),
            },
            new[]
            {
                F("Status", "الحالة", "Status"),
                F("Currency", "العملة", "Currency", width: 14),
                F("TotalGross", "إجمالي الخدمات", "Total", ReportFieldType.Money),
                F("Discount", "الخصم", "Discount", ReportFieldType.Money),
                F("TotalInsurer", "على التأمين", "Insurer pays", ReportFieldType.Money),
                F("TotalPatient", "على المريض", "Patient pays", ReportFieldType.Money),
                F("TotalVat", "الضريبة", "VAT", ReportFieldType.Money),
                F("NetDue", "الصافي المستحق", "Net due", ReportFieldType.Money),
                F("Paid", "المدفوع", "Paid", ReportFieldType.Money),
                F("Waived", "الإعفاء الخيري", "Charity waiver", ReportFieldType.Money),   // H63
                F("Remaining", "المتبقي", "Remaining", ReportFieldType.Money),
                F("PaymentMethod", "طريقة الدفع", "Payment method"),
                F("ReceiptNo", "رقم الإيصال", "Receipt no."),
            },
            Params("BillId", "رقم الفاتورة الداخلي", "Bill id"));

        public static ReportDatasetDefinition Prescription() => Doc(HospitalDocumentDatasetCodes.Prescription,
            "روشتة", "Prescription",
            "روشتة الكشف: الأدوية وطريقة استعمالها، مع الشكوى والتشخيص وخطة العلاج وبيانات الطبيب.",
            "A visit's prescription: the medicines and how to take them, with the complaint, the diagnosis, the plan and the doctor.",
            new[]
            {
                F("LineNo", "م", "#", ReportFieldType.Integer, 10, true, ReportAlign.Center),
                F("Drug", "الدواء", "Medicine", width: 60, visible: true),
                F("Instructions", "طريقة الاستعمال", "Directions", width: 80, visible: true),
                F("Quantity", "الكمية", "Qty", ReportFieldType.Integer, 16, true),
            },
            new[]
            {
                F("ChiefComplaint", "الشكوى", "Chief complaint", width: 80),
                F("MedicalCondition", "الحالة المرضية", "Medical condition", width: 80),
                F("Allergies", "الحساسية", "Allergies", width: 60),
                F("Diagnosis", "التشخيص", "Diagnosis", width: 80),
                F("TreatmentPlan", "خطة العلاج", "Treatment plan", width: 80),
                F("FollowUp", "المتابعة", "Follow-up", width: 40),
            },
            Params("EncounterId", "رقم الكشف الداخلي", "Visit id"));

        public static ReportDatasetDefinition PrescriptionForm() => Doc(HospitalDocumentDatasetCodes.PrescriptionForm,
            "روشتة (ورقة واحدة)", "Prescription paper",
            "الروشتة كورقة واحدة: بيانات المريض والشكوى والتشخيص وخطة العلاج، والأدوية في حقل واحد.",
            "The prescription as one paper: the patient, complaint, diagnosis and plan, with the medicines in one field.",
            new[] { F("LineNo", "م", "#", ReportFieldType.Integer, 10, false, ReportAlign.Center) },
            new[]
            {
                F("ChiefComplaint", "الشكوى", "Chief complaint", width: 80, visible: true),
                F("MedicalCondition", "الحالة المرضية", "Medical condition", width: 80),
                F("Allergies", "الحساسية", "Allergies", width: 60),
                F("Diagnosis", "التشخيص", "Diagnosis", width: 80, visible: true),
                F("TreatmentPlan", "خطة العلاج", "Treatment plan", width: 80),
                F("Medicines", "الأدوية", "Medicines", width: 120, visible: true),
                F("FollowUp", "المتابعة", "Follow-up", width: 40),
            },
            Params("EncounterId", "رقم الكشف الداخلي", "Visit id"));

        public static ReportDatasetDefinition DischargeSummary() => Doc(HospitalDocumentDatasetCodes.DischargeSummary,
            "ملخص الخروج", "Discharge summary",
            "ملخص خروج المريض من التنويم كورقة واحدة: بيانات الإقامة، والتشخيص النهائي، وسير الحالة، والعمليات، والحالة عند الخروج، وأدوية المنزل، والتعليمات والمتابعة، وتوقيع الطبيب.",
            "A patient's discharge from a stay as one paper: the stay, the final diagnosis, the course in hospital, the operations, the condition at discharge, the medicines to take home, the instructions and follow-up, and the doctor's signature.",
            new[] { F("LineNo", "م", "#", ReportFieldType.Integer, 10, false, ReportAlign.Center) },
            new[]
            {
                F("AdmittedAt", "تاريخ الدخول", "Admitted", ReportFieldType.DateTime, visible: true),
                F("DischargedAt", "تاريخ الخروج", "Discharged", ReportFieldType.DateTime, visible: true),
                F("StayDays", "مدة الإقامة (يوم)", "Length of stay (days)", ReportFieldType.Decimal, 20),
                F("Ward", "الجناح والسرير", "Ward and bed", width: 50),
                F("Attending", "الطبيب المعالج", "Attending doctor", width: 50),
                F("AdmissionReason", "سبب الدخول", "Reason for admission", width: 80),
                F("FinalDiagnosis", "التشخيص النهائي", "Final diagnosis", width: 120, visible: true),
                F("Course", "سير الحالة أثناء الإقامة", "Course in hospital", width: 180),
                F("Procedures", "العمليات والإجراءات", "Procedures", width: 120),
                F("Condition", "الحالة عند الخروج", "Condition at discharge", width: 40, visible: true),
                F("Medications", "أدوية المنزل", "Medicines to take home", width: 140),
                F("Instructions", "التعليمات", "Instructions", width: 140),
                F("FollowUpOn", "موعد المتابعة", "Follow-up on", ReportFieldType.Date),
                F("FollowUpWith", "المتابعة مع", "Follow-up with", width: 60),
                F("Status", "الحالة", "Status", width: 20),
                F("SignedBy", "وقّعه", "Signed by", width: 50),
                F("SignedAt", "وقت التوقيع", "Signed at", ReportFieldType.DateTime),
            },
            Params("AdmissionId", "رقم الإقامة الداخلي", "Stay id"));

        public static ReportDatasetDefinition SickLeave() => Doc(HospitalDocumentDatasetCodes.SickLeave,
            "إجازة مرضية", "Sick leave certificate",
            "شهادة إجازة مرضية واحدة: المدة وعدد الأيام والتوصية، والتشخيص فقط إذا اختار الطبيب طباعته، ورقم الشهادة وتوقيع الطبيب.",
            "One sick leave certificate: the period and the days, the recommendation, the diagnosis only when the doctor chose to print it, the certificate number and the doctor.",
            new[] { F("LineNo", "م", "#", ReportFieldType.Integer, 10, false, ReportAlign.Center) },
            new[]
            {
                F("FromDate", "من تاريخ", "From", ReportFieldType.Date, visible: true),
                F("ToDate", "إلى تاريخ", "To", ReportFieldType.Date, visible: true),
                F("Days", "عدد الأيام", "Days", ReportFieldType.Integer, 16, true),
                F("Kind", "التوصية", "Recommendation", width: 40, visible: true),
                F("Diagnosis", "التشخيص", "Diagnosis", width: 80),
                F("Note", "ملاحظات", "Notes", width: 80),
                F("Void", "ملغاة", "Void", width: 40),
            },
            Params("SickLeaveId", "رقم الإجازة الداخلي", "Certificate id"));

        public static ReportDatasetDefinition BirthNotice() => Doc(HospitalDocumentDatasetCodes.BirthNotice,
            "إشعار ولادة", "Birth notification",
            "إشعار ولادة واحدة: الأم، ووقت الولادة وطريقتها، ونوع المولود ووزنه وقياساته ودرجة أبجار، ورقم ملف المولود.",
            "One birth notification: the mother, when and how the baby was delivered, the baby's sex, weight, measures and Apgar, and the newborn's file number.",
            new[] { F("LineNo", "م", "#", ReportFieldType.Integer, 10, false, ReportAlign.Center) },
            new[]
            {
                F("BornAt", "وقت الولادة", "Born at", ReportFieldType.DateTime, visible: true),
                F("BabySex", "نوع المولود", "Sex", width: 20, visible: true),
                F("Outcome", "النتيجة", "Outcome", width: 24, visible: true),
                F("DeliveryMode", "طريقة الولادة", "Delivery", width: 30),
                F("GestationWeeks", "عمر الحمل (أسبوع)", "Gestation (weeks)", ReportFieldType.Integer, 18),
                F("BirthOrder", "ترتيب المولود", "Birth order", ReportFieldType.Integer, 16),
                F("WeightGrams", "الوزن (جم)", "Weight (g)", ReportFieldType.Integer, 18, true),
                F("LengthCm", "الطول (سم)", "Length (cm)", ReportFieldType.Decimal, 16),
                F("HeadCm", "محيط الرأس (سم)", "Head (cm)", ReportFieldType.Decimal, 16),
                F("Apgar", "أبجار 1 / 5", "Apgar 1 / 5", width: 18),
                F("BabyName", "اسم المولود", "Baby's name", width: 50),
                F("BabyMrn", "رقم ملف المولود", "Baby's MRN", width: 24),
                F("Note", "ملاحظات", "Notes", width: 80),
                F("Void", "ملغى", "Void", width: 40),
            },
            Params("BirthId", "رقم الولادة الداخلي", "Birth id"));

        public static ReportDatasetDefinition DeathCertificate() => Doc(HospitalDocumentDatasetCodes.DeathCertificate,
            "شهادة وفاة", "Death certificate",
            "الشهادة الطبية لسبب الوفاة بنموذج منظمة الصحة العالمية: وقت الوفاة ومكانها، وتسلسل الأسباب من السبب المباشر إلى السبب الأساسي، والحالات الأخرى، وطريقة الوفاة، والتشريح، والطبيب.",
            "The medical certificate of cause of death in the WHO form: when and where, the chain of causes from the immediate to the underlying cause, other conditions, the manner of death, autopsy, and the doctor.",
            new[] { F("LineNo", "م", "#", ReportFieldType.Integer, 10, false, ReportAlign.Center) },
            new[]
            {
                F("DiedAt", "وقت الوفاة", "Time of death", ReportFieldType.DateTime, visible: true),
                F("Place", "مكان الوفاة", "Place of death", width: 40, visible: true),
                F("CauseA", "(أ) السبب المباشر", "(a) Immediate cause", width: 100, visible: true),
                F("CauseB", "(ب) ناتج عن", "(b) Due to", width: 100),
                F("CauseC", "(ج) ناتج عن", "(c) Due to", width: 100),
                F("CauseD", "(د) ناتج عن", "(d) Due to", width: 100),
                F("UnderlyingCause", "السبب الأساسي", "Underlying cause", width: 100, visible: true),
                F("OtherConditions", "حالات أخرى ساهمت", "Other significant conditions", width: 120),
                F("Manner", "طريقة الوفاة", "Manner of death", width: 30),
                F("Autopsy", "طُلب تشريح", "Autopsy requested", width: 16),
                F("Void", "ملغاة", "Void", width: 40),
            },
            Params("DeathId", "رقم الشهادة الداخلي", "Certificate id"));

        public static ReportDatasetDefinition Consent() => Doc(HospitalDocumentDatasetCodes.Consent,
            "إقرار موافقة مريض", "Patient consent form",
            "إقرار الموافقة (أو الرفض) كما وقّعه المريض أو من يمثله: النص الذي قرأه، والإجراء، والمخاطر التي شُرحت، ومن شرح، والشاهد، ووقت التوقيع، وبصمة التوقيع الإلكتروني.",
            "The consent (or refusal) as the patient or their representative signed it: the text they read, the procedure, the risks explained, who explained, the witness, when it was signed, and the fingerprint of the electronic signature.",
            new[] { F("LineNo", "م", "#", ReportFieldType.Integer, 10, false, ReportAlign.Center) },
            new[]
            {
                F("Title", "عنوان الإقرار", "Form title", width: 80, visible: true),
                F("ConsentText", "نص الإقرار", "Consent text", width: 180, visible: true),
                F("ProcedureName", "الإجراء", "Procedure", width: 80, visible: true),
                F("Risks", "المخاطر والبدائل التي شُرحت", "Risks and alternatives explained", width: 120),
                F("Decision", "القرار", "Decision", width: 30, visible: true),
                F("SignerName", "الموقّع", "Signed by", width: 60, visible: true),
                F("SignerRelation", "صلة الموقّع بالمريض", "Relation to the patient", width: 40),
                F("ProxyReason", "سبب التوقيع نيابةً", "Why a representative signed", width: 50),
                F("Interpreter", "المترجم", "Interpreter", width: 50),
                F("ExplainedBy", "شرح الإجراء", "Explained by", width: 60, visible: true),
                F("Witness", "الشاهد", "Witness", width: 60, visible: true),
                F("SignedAt", "وقت التوقيع", "Signed at", ReportFieldType.DateTime, visible: true),
                F("CaseNo", "رقم العملية", "Theatre case", width: 24),
                F("SignatureFingerprint", "بصمة التوقيع الإلكتروني (SHA-256)", "E-signature fingerprint (SHA-256)", width: 120, visible: true),
                F("Withdrawn", "سحب الموافقة", "Withdrawn", width: 60),
            },
            Params("ConsentId", "رقم الإقرار الداخلي", "Consent id"));

        public static ReportDatasetDefinition PatientStatement() => Doc(HospitalDocumentDatasetCodes.PatientStatement,
            "كشف حساب مريض", "Patient account statement",
            "كشف حساب المريض: كل فاتورة مغلقة (مدين بنصيب المريض)، وكل دفعة وكل إعفاء خيري معتمد (دائن)، بالرصيد الجاري. فواتير العملات الأجنبية خارج الكشف.",
            "The patient's account: every closed bill (a debit, the patient's share), every payment and approved charity waiver (a credit), with the running balance. Foreign-currency bills are left out.",
            new[]
            {
                F("LineNo", "م", "#", ReportFieldType.Integer, 10, true, ReportAlign.Center),
                F("EntryDate", "التاريخ", "Date", ReportFieldType.Date, 22, true),
                F("Kind", "البيان", "Entry", width: 26, visible: true),
                F("Reference", "المرجع", "Reference", width: 26, visible: true),
                F("Description", "التفاصيل", "Details", width: 60, visible: true),
                F("Debit", "مدين", "Debit", ReportFieldType.Money, 24, true),
                F("Credit", "دائن", "Credit", ReportFieldType.Money, 24, true),
                F("Balance", "الرصيد", "Balance", ReportFieldType.Money, 26, true),
            },
            new[]
            {
                F("Currency", "العملة", "Currency", width: 14),
                F("TotalDebit", "إجمالي المدين", "Total debit", ReportFieldType.Money),
                F("TotalCredit", "إجمالي الدائن", "Total credit", ReportFieldType.Money),
                F("ClosingBalance", "الرصيد المستحق", "Balance due", ReportFieldType.Money),
            },
            Params("PatientId", "رقم المريض الداخلي", "Patient id"));

        public static ReportDatasetDefinition DentalClaim() => Doc(HospitalDocumentDatasetCodes.DentalClaim,
            "مطالبة تأمين أسنان", "Dental claim form",
            "خطة علاج الأسنان كمطالبة تأمين: الإجراء ورقم السن وعدد الجلسات والأتعاب، مع التشخيص والأدوية.",
            "A dental treatment plan as an insurance claim: procedure, tooth, sessions and fees, with the diagnosis and medicines.",
            new[]
            {
                F("LineNo", "م", "#", ReportFieldType.Integer, 10, true, ReportAlign.Center),
                F("Procedure", "الإجراء الطبي", "Medical act", width: 60, visible: true),
                F("ProcedureCode", "الكود", "Code", width: 22),
                F("Tooth", "رقم السن", "Tooth no.", width: 16, visible: true, align: ReportAlign.Center),
                F("Surfaces", "الأسطح", "Surfaces", width: 16),
                F("Phase", "المرحلة", "Phase", width: 30),
                F("Sessions", "عدد الجلسات", "Sessions", ReportFieldType.Integer, 18, true),
                F("FeePerSession", "أتعاب الجلسة", "Fee / session", ReportFieldType.Money, 24, true),
                F("TotalFee", "إجمالي الأتعاب", "Total fees", ReportFieldType.Money, 26, true),
                F("LineStatus", "الحالة", "Status", width: 20),
            },
            new[]
            {
                F("Diagnosis", "التشخيص", "Diagnosis", width: 80),
                F("Medications", "الأدوية", "Medications", width: 80),
                F("Panoramic", "أشعة بانوراما", "Panoramic X-ray", width: 18),
                F("PanoramicMark", "علامة البانوراما", "Panoramic mark", width: 8),
                F("Currency", "العملة", "Currency", width: 14),
                F("PlanTotal", "إجمالي الخطة", "Plan total", ReportFieldType.Money),
            },
            Params("PlanId", "رقم الخطة الداخلي", "Plan id"));

        // ---- H109: the specialty visit's papers (the approved prototype's outputs), from the SIGNED visit ----
        private static IEnumerable<ReportDatasetField> Eyes(params (string Key, string Ar, string En)[] fields) =>
            new[] { ("Od", "العين اليمنى", "right eye"), ("Os", "العين اليسرى", "left eye") }
                .SelectMany(s => fields.Select(f => F(s.Item1 + f.Key, f.Ar + " — " + s.Item2, f.En + " — " + s.Item3, width: 22)));

        public static ReportDatasetDefinition GlassesRx() => Doc(HospitalDocumentDatasetCodes.GlassesRx,
            "وصفة نظارة", "Glasses prescription",
            "وصفة نظارة من قياسات زيارة العيون الموقّعة: الكروي والأسطواني والمحور والإضافة للقراءة لكل عين، والنظر بالنظارة.",
            "A glasses prescription from the signed eye visit's readings: sphere, cylinder, axis and near add for each eye, and the corrected vision.",
            new[] { F("LineNo", "م", "#", ReportFieldType.Integer, 10, false, ReportAlign.Center) },
            Eyes(("Sph", "كروي", "Sphere"), ("Cyl", "أسطواني", "Cylinder"), ("Axis", "المحور", "Axis"), ("Add", "إضافة القراءة", "Near add"), ("Va", "النظر بالنظارة", "Corrected vision"))
                .Append(F("Notes", "ملاحظات", "Notes", width: 120)),
            Params("EncounterId", "رقم الزيارة الداخلي", "Visit id"));

        public static ReportDatasetDefinition EyeLetter() => Doc(HospitalDocumentDatasetCodes.EyeLetter,
            "تقرير العيون لطبيب السكري", "Eye report to the diabetes doctor",
            "تقرير من زيارة العيون الموقّعة لطبيب السكري: النظر وضغط العين ودرجة اعتلال الشبكية ونسبة الكأس للقرص لكل عين، ونتائج رسم قاع العين، والتشخيص والخطة.",
            "A report from the signed eye visit to the diabetes doctor: vision, eye pressure, retinopathy grade and cup-to-disc ratio for each eye, the fundus drawing's findings, the diagnosis and the plan.",
            new[] { F("LineNo", "م", "#", ReportFieldType.Integer, 10, false, ReportAlign.Center) },
            Eyes(("Va", "النظر", "Vision"), ("Iop", "ضغط العين", "Eye pressure"), ("Dr", "اعتلال الشبكية", "Retinopathy"), ("Cdr", "الكأس للقرص", "Cup-to-disc"))
                .Concat(new[] { F("FundusFindings", "نتائج قاع العين", "Fundus findings", width: 140), F("Assessment", "التشخيص", "Assessment", width: 140), F("Plan", "الخطة", "Plan", width: 140) }),
            Params("EncounterId", "رقم الزيارة الداخلي", "Visit id"));

        public static ReportDatasetDefinition AllergyPlan() => Doc(HospitalDocumentDatasetCodes.AllergyPlan,
            "خطة طوارئ الحساسية", "Allergy emergency plan",
            "خطة الطوارئ لمريض الحساسية من زيارته الموقّعة: مسببات الحساسية، والربو، وحقنة الأدرينالين الموصوفة وطريقة استخدامها، وخطوات التصرف.",
            "The allergy patient's emergency action plan from the signed visit: the triggers, asthma, the adrenaline auto-injector prescribed and how to use it, and the steps to take.",
            new[] { F("LineNo", "م", "#", ReportFieldType.Integer, 10, false, ReportAlign.Center) },
            new[]
            {
                F("Triggers", "مسببات الحساسية", "Triggers", width: 140),
                F("Asthma", "ربو", "Asthma", width: 80),
                F("AdrenalineDevice", "حقنة الأدرينالين", "Adrenaline auto-injector", width: 80),
                F("AdrenalineUse", "طريقة الاستخدام", "How to use it", width: 140),
            },
            Params("EncounterId", "رقم الزيارة الداخلي", "Visit id"));

        public static ReportDatasetDefinition CanvasDrawing() => Doc(HospitalDocumentDatasetCodes.CanvasDrawing,
            "رسم إكلينيكي", "Clinical drawing",
            "رسم واحد من زيارة موقّعة (خريطة الجسم، قاع العين، الحروق، السمع، المفاصل، شجرة العائلة): الرسم كما رآه الطبيب، وعلاماته، ونتائج حسابه.",
            "One drawing of a signed visit (body map, fundus, burns, audiogram, joints, pedigree): the drawing as the doctor saw it, its marks and its results.",
            // H113: one row per mark — a box of fixed height cut the list off after four lines; rows flow onto new pages
            new[] { F("LineNo", "م", "#", ReportFieldType.Integer, 10, true, ReportAlign.Center), F("Mark", "العلامة", "Mark", width: 170, visible: true) },
            new[]
            {
                F("DrawingTitle", "الرسم", "Drawing", width: 60, visible: true),
                F("Drawing", "صورة الرسم", "Drawing picture", width: 20),
                F("Findings", "العلامات", "Marks", width: 140),
                F("Results", "النتائج", "Results", width: 140),
                F("SignedAt", "وقت التوقيع", "Signed at", ReportFieldType.DateTime),
            },
            Params("SheetId", "رقم الرسم الداخلي", "Drawing id"));

        // ---- H144: the front desk's papers ----

        /// The same dataset with some shared header fields shown by default — the platform's starter layout (seeded once
        /// per report) prints the visible fields, and a front-desk paper without the patient's name is no paper.
        private static ReportDatasetDefinition Show(ReportDatasetDefinition d, params string[] keys) => new()
        {
            DatasetCode = d.DatasetCode, Module = d.Module, TitleAr = d.TitleAr, TitleEn = d.TitleEn, DescriptionAr = d.DescriptionAr, DescriptionEn = d.DescriptionEn,
            DataSourceKey = d.DataSourceKey, RequiredPermissionKey = d.RequiredPermissionKey, MaxRows = d.MaxRows, RowCapPolicy = d.RowCapPolicy, Parameters = d.Parameters,
            Fields = d.Fields.Select(f => keys.Contains(f.Key) ? F(f.Key, f.TitleAr, f.TitleEn, f.Type, f.WidthMm, true, f.Align) : f).ToArray(),
        };

        public static ReportDatasetDefinition VisitSlip() => Show(Doc(HospitalDocumentDatasetCodes.VisitSlip,
            "ورقة الزيارة وتذكرة الدور", "Visit slip and queue ticket",
            "زيارة واحدة كما تُسلَّم للمريض عند الشباك: الطبيب والعيادة والغرفة والموعد ورقم الدور وكم مريضًا أمامه، وجهة الدفع. ورقة الزيارة وتذكرة الدور قالبان لها.",
            "One visit as the patient takes it from the counter: the doctor, the clinic, the room, the time, the queue number and how many are ahead, and the payer. The visit slip and the queue ticket are two layouts of it.",
            new[] { F("LineNo", "م", "#", ReportFieldType.Integer, 10, false, ReportAlign.Center) },
            new[]
            {
                F("QueueNumber", "رقم الدور", "Queue no.", ReportFieldType.Integer, 20, true, ReportAlign.Center),
                F("VisitDate", "اليوم", "Day", ReportFieldType.Date, visible: true),
                F("VisitTime", "الموعد", "Time", width: 16, visible: true),
                F("VisitType", "نوع الزيارة", "Visit type", width: 24, visible: true),
                F("Department", "العيادة", "Clinic", width: 40, visible: true),
                F("Room", "الغرفة", "Room", width: 16, visible: true),
                F("AheadOfYou", "أمامك", "Ahead of you", ReportFieldType.Integer, 16, true, ReportAlign.Center),
                F("Status", "الحالة", "Status", width: 20),
                F("ArrivedAt", "وقت الحضور", "Arrived at", ReportFieldType.DateTime),
            },
            Params("AppointmentId", "رقم الموعد الداخلي", "Appointment id")), "PatientName", "PatientMrn", "DoctorName");

        public static ReportDatasetDefinition Wristband() => Show(Doc(HospitalDocumentDatasetCodes.Wristband,
            "سوار المريض", "Patient wristband",
            "سوار تعريف المريض: الاسم ورقم الملف وتاريخ الميلاد والنوع وفصيلة الدم والحساسية، والقسم والسرير إن كان منوَّمًا.",
            "The patient's identification band: the name, the file number, the date of birth, the sex, the blood group and the allergies, and the ward and bed when admitted.",
            new[] { F("LineNo", "م", "#", ReportFieldType.Integer, 10, false, ReportAlign.Center) },
            new[]
            {
                F("BirthDate", "تاريخ الميلاد", "Date of birth", ReportFieldType.Date, visible: true),
                F("BloodGroup", "فصيلة الدم", "Blood group", width: 14, visible: true),
                F("Allergies", "الحساسية", "Allergies", width: 80, visible: true),
                F("AllergyAlert", "تنبيه الحساسية", "Allergy alert", width: 30),
                F("Ward", "القسم", "Ward", width: 30),
                F("Bed", "السرير", "Bed", width: 16),
                F("AdmittedOn", "تاريخ الدخول", "Admitted on", ReportFieldType.Date),
            },
            Params("PatientId", "رقم المريض الداخلي", "Patient id")), "PatientName", "PatientMrn", "PatientAge", "PatientSex");

        public static ReportDatasetDefinition DepositReceipt() => Show(Doc(HospitalDocumentDatasetCodes.DepositReceipt,
            "إيصال عربون", "Deposit receipt",
            "إيصال عربون واحد: المبلغ وطريقة الدفع والغرض، ومن حصّله، وما استُخدم منه وما رُدّ وما بقي.",
            "One deposit's receipt: the amount, how it was paid and what for, who took it, and what was used, refunded and is left.",
            new[] { F("LineNo", "م", "#", ReportFieldType.Integer, 10, false, ReportAlign.Center) },
            new[]
            {
                // amounts as TEXT in the facility currency's own decimals: a Money field prints two, and the dinar has three
                F("Amount", "المبلغ", "Amount", width: 24, visible: true, align: ReportAlign.End),
                F("Currency", "العملة", "Currency", width: 14, visible: true),
                F("Method", "طريقة الدفع", "Method", width: 24, visible: true),
                F("Purpose", "الغرض", "For", width: 24, visible: true),
                F("Note", "ملاحظة", "Note", width: 60),
                F("Cashier", "المحصّل", "Received by", width: 40, visible: true),
                F("Used", "المستخدم", "Used", width: 24, align: ReportAlign.End),
                F("Refunded", "المردود", "Refunded", width: 24, align: ReportAlign.End),
                F("Left", "المتبقي", "Left", width: 24, align: ReportAlign.End),
            },
            Params("DepositId", "رقم العربون الداخلي", "Deposit id")), "DocumentNo", "DocumentDate", "PatientName", "PatientMrn");

        public static ReportDatasetDefinition RxLabel()
        {
            var d = Show(Doc(HospitalDocumentDatasetCodes.RxLabel,
                "ملصقات الدواء", "Dispensing labels",
                "ملصق لكل دواء صُرف من الصيدلية: المريض والدواء وتركيزه وطريقة الاستعمال والكمية والتشغيلة وتاريخ الانتهاء وتحذيرات الحفظ.",
                "A label for each medicine the pharmacy gave: the patient, the medicine and its strength, how to take it, the quantity, the batch, the expiry and the storage warnings.",
                new[] { F("LineNo", "م", "#", ReportFieldType.Integer, 10, false, ReportAlign.Center) },
                new[]
                {
                    F("Drug", "الدواء", "Medicine", width: 60, visible: true),
                    F("DrugDetail", "المادة والتركيز", "Generic and strength", width: 60, visible: true),
                    F("Instructions", "طريقة الاستعمال", "How to take it", width: 80, visible: true),
                    F("Quantity", "الكمية", "Quantity", width: 16, visible: true, align: ReportAlign.End),
                    F("Batch", "التشغيلة", "Batch", width: 24, visible: true),
                    F("Expiry", "تاريخ الانتهاء", "Expiry", ReportFieldType.Date, visible: true),
                    F("Warnings", "تحذيرات", "Warnings", width: 80, visible: true),
                    F("DispensedOn", "تاريخ الصرف", "Dispensed on", ReportFieldType.Date),
                    F("Pharmacist", "الصيدلي", "Pharmacist", width: 40),
                },
                Params("DispenseId", "رقم الصرف الداخلي", "Dispense id")), "DocumentNo", "PatientName", "PatientMrn", "DoctorName");
            // one medicine's label again (a reprint), not the whole dispense
            var line = new ReportParameterDescriptor
            {
                Key = "LineId", TitleAr = "سطر الدواء", TitleEn = "Medicine line", Type = ReportFieldType.Integer, Required = false,
                HelpTextAr = "اتركه فارغًا لطباعة ملصقات كل الأدوية المصروفة.", HelpTextEn = "Leave it empty to print a label for every medicine given.",
            };
            return new ReportDatasetDefinition
            {
                DatasetCode = d.DatasetCode, Module = d.Module, TitleAr = d.TitleAr, TitleEn = d.TitleEn, DescriptionAr = d.DescriptionAr, DescriptionEn = d.DescriptionEn,
                DataSourceKey = d.DataSourceKey, RequiredPermissionKey = d.RequiredPermissionKey, MaxRows = d.MaxRows, RowCapPolicy = d.RowCapPolicy,
                Fields = d.Fields, Parameters = d.Parameters.Concat(new[] { line }).ToArray(),
            };
        }

        /// H204: the referral letter — what the referring doctor asks, of whom, how urgently, with the clinical summary, the
        /// visit's diagnoses and the allergies; the doctor's name, specialty and licence come from the shared header.
        public static ReportDatasetDefinition ReferralLetter() => Show(Doc(HospitalDocumentDatasetCodes.ReferralLetter,
            "خطاب تحويل", "Referral letter",
            "خطاب تحويل واحد: إلى أي قسم أو جهة، ودرجة الاستعجال، والسؤال، والملخص السريري، وتشخيصات الزيارة، والحساسية، والطبيب المحوِّل، ومساحة لرد الطبيب المستقبِل.",
            "One referral letter: to which department or facility, the urgency, the question, the clinical summary, the visit's diagnoses, the allergies, the referring doctor, and space for the receiving doctor's reply.",
            new[] { F("LineNo", "م", "#", ReportFieldType.Integer, 10, false, ReportAlign.Center) },
            new[]
            {
                F("ToWhom", "إلى", "To", width: 80, visible: true),
                F("ToSpecialty", "التخصص المطلوب", "Specialty asked", width: 60),
                F("Urgency", "درجة الاستعجال", "Urgency", width: 24, visible: true),
                F("DueBy", "مطلوب الرد قبل", "Answer due by", ReportFieldType.DateTime),
                F("Reason", "سبب التحويل", "Reason for referral", width: 140, visible: true),
                F("Summary", "الملخص السريري", "Clinical summary", width: 140),
                F("Diagnoses", "التشخيص", "Diagnosis", width: 140),
                F("Allergies", "الحساسية", "Allergies", width: 80),
                F("Reply", "الرد", "Reply", width: 140),
            },
            Params("ReferralId", "رقم التحويل الداخلي", "Referral id")), "DocumentNo", "DocumentDate", "PatientName", "PatientMrn", "DoctorName");

        /// H206: the specimen tube's label — the accession number (its QR is what the bedside scan reads), the patient, the
        /// test, the specimen and its section, the priority, and when it was ordered and collected.
        public static ReportDatasetDefinition SpecimenLabel() => Show(Doc(HospitalDocumentDatasetCodes.SpecimenLabel,
            "ملصق عينة", "Specimen label",
            "ملصق أنبوب العينة: رقم الطلب نصًا ورمز QR الذي يقرؤه المسح عند السرير، والمريض، والتحليل، ونوع العينة وقسمها، والأولوية، ووقت الطلب والسحب.",
            "The specimen tube's label: the accession number as text and as the QR the bedside scan reads, the patient, the test, the specimen and its section, the priority, and when it was ordered and collected.",
            new[] { F("LineNo", "م", "#", ReportFieldType.Integer, 10, false, ReportAlign.Center) },
            new[]
            {
                F("Test", "التحليل", "Test", width: 60, visible: true),
                F("Specimen", "العينة", "Specimen", width: 30, visible: true),
                F("Section", "القسم", "Section", width: 30),
                F("Priority", "الأولوية", "Priority", width: 16, visible: true),
                F("CollectedAt", "وقت السحب", "Collected", ReportFieldType.DateTime),
            },
            Params("LabOrderId", "رقم طلب التحليل الداخلي", "Lab order id")), "DocumentNo", "DocumentDate", "PatientName", "PatientMrn");

        public static ReportDatasetDefinition CreditNote() => Show(Doc(HospitalDocumentDatasetCodes.CreditNote,
            "إشعار دائن", "Credit note",
            "إشعار دائن واحد على فاتورة مغلقة: الدواء الذي أُرجع وكميته وسعره، والصافي والضريبة والإجمالي الذي لم يعد على المريض، والسبب، وما ذهب إلى عربونه.",
            "One credit note on a closed bill: the medicine brought back, its quantity and price, the net, the VAT and the total the patient no longer owes, the reason, and what went to the patient's deposit.",
            new[] { F("LineNo", "م", "#", ReportFieldType.Integer, 10, false, ReportAlign.Center) },
            new[]
            {
                F("BillNo", "الفاتورة", "Bill", width: 24, visible: true),
                F("RxNo", "الروشتة", "Prescription", width: 24),
                F("Drug", "الدواء", "Medicine", width: 60, visible: true),
                // amounts as TEXT in the facility currency's own decimals (a Money field prints two; the dinar has three)
                F("Quantity", "الكمية", "Quantity", width: 16, visible: true, align: ReportAlign.End),
                F("UnitPrice", "السعر", "Price", width: 20, align: ReportAlign.End),
                F("Net", "الصافي", "Net", width: 24, align: ReportAlign.End),
                F("Vat", "الضريبة", "VAT", width: 20, align: ReportAlign.End),
                F("Total", "الإجمالي", "Total", width: 24, visible: true, align: ReportAlign.End),
                F("Currency", "العملة", "Currency", width: 14),
                F("Reason", "السبب", "Reason", width: 60, visible: true),
                F("IssuedBy", "أصدره", "Issued by", width: 40),
                F("ToDeposit", "إلى العربون", "To the deposit", width: 24, align: ReportAlign.End),
            },
            Params("CreditNoteId", "رقم الإشعار الداخلي", "Credit note id")), "DocumentNo", "DocumentDate", "PatientName", "PatientMrn");

        public static ReportDatasetDefinition CompoundLabel() => Show(Doc(HospitalDocumentDatasetCodes.CompoundLabel,
            "ملصق تحضير", "Compounded preparation label",
            "ملصق كيس التحضير المعقَّم لمريض واحد: المريض والمستحضر والجرعة والمذيب والحجم، وموعد انتهاء الاستعمال وطريقة الحفظ، والتحذير للأدوية السامة للخلايا، ومن حضّر ومن راجع.",
            "The bag label of one patient's sterile preparation: the patient, the product, the dose, the diluent and volume, the beyond-use time and storage, the cytotoxic warning, who prepared it and who checked it.",
            new[] { F("LineNo", "م", "#", ReportFieldType.Integer, 10, false, ReportAlign.Center) },
            new[]
            {
                F("Product", "المستحضر", "Product", width: 60, visible: true),
                F("Dose", "الجرعة", "Dose", width: 30, visible: true),
                F("Diluent", "المذيب والحجم", "Diluent and volume", width: 40, visible: true),
                F("BeyondUse", "يُستعمل قبل", "Use before", ReportFieldType.DateTime, visible: true),
                F("Storage", "الحفظ", "Storage", width: 30, visible: true),
                F("Hazard", "تحذير", "Hazard", width: 50),
                F("Kind", "النوع", "Kind", width: 20),
                F("PreparedBy", "حضّره", "Prepared by", width: 40),
                F("CheckedBy", "راجعه", "Checked by", width: 40),
            },
            Params("CompoundingId", "رقم التحضير الداخلي", "Preparation id")), "DocumentNo", "PatientName", "PatientMrn");

        public static IEnumerable<ReportDatasetDefinition> All()
        {
            yield return Bill();
            yield return Prescription();
            yield return PrescriptionForm();
            yield return DischargeSummary();
            yield return SickLeave();
            yield return BirthNotice();
            yield return DeathCertificate();
            yield return Consent();
            yield return PatientStatement();
            yield return GlassesRx();
            yield return EyeLetter();
            yield return AllergyPlan();
            yield return CanvasDrawing();
            yield return VisitSlip();        // H144
            yield return Wristband();        // H144
            yield return DepositReceipt();   // H144
            yield return RxLabel();          // H147
            yield return CreditNote();       // H149
            yield return ReferralLetter();   // H204
            yield return SpecimenLabel();    // H206
            yield return CompoundLabel();    // H153
            yield return DentalClaim();
        }
    }

    public sealed class HospitalReportDefinitionProvider : IReportDefinitionProvider
    {
        public string ProviderName => "Hospital";

        public IEnumerable<ReportDefinition> GetDefinitions()
        {
            int order = 400;
            foreach (var dataset in HospitalDocumentDatasets.All())
            {
                yield return new ReportDefinition
                {
                    Code = dataset.DatasetCode,
                    Module = dataset.Module,
                    TitleAr = dataset.TitleAr,
                    TitleEn = dataset.TitleEn,
                    DescriptionAr = dataset.DescriptionAr,
                    DescriptionEn = dataset.DescriptionEn,
                    DataSourceKey = dataset.DataSourceKey,
                    PermissionKey = dataset.RequiredPermissionKey,
                    CategoryKey = HospitalDocumentDatasetCodes.CategoryKey,
                    Tags = new[] { "hospital", "document" },
                    Columns = dataset.Fields.Select(f => f.ToColumn()).ToList(),
                    Parameters = dataset.Parameters,
                    // A document reads in the order it was written.
                    DefaultSorts = new[] { ReportSort.By("LineNo") },
                    DefaultFormat = ReportOutputFormat.Html,
                    Capabilities = new ReportCapabilities
                    {
                        Formats = new[] { ReportOutputFormat.Html, ReportOutputFormat.PrintHtml, ReportOutputFormat.Pdf },
                        MaxRows = dataset.MaxRows,
                        PreviewRows = 100,
                        AllowSchedule = false,
                        AllowArchive = true,
                        AllowShare = true,
                    },
                    Icon = "ki-outline ki-document",
                    Color = "primary",
                    SortOrder = order++,
                    ShippedVisual = HospitalDeskLayouts.For(dataset.DatasetCode),   // H144: the desk's papers are designed, not tables (H147: and the pharmacy label)
                    DefaultPageSetup = HospitalDeskLayouts.For(dataset.DatasetCode)?.Page ?? ReportPageSetup.Default,
                };
            }
        }
    }
}
