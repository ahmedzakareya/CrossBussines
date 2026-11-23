namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // HOSPITAL H49 — THE MANAGEMENT REPORTS (the approved prototype's #/reports: fifteen reports in four groups).
    // Every one is a registered Report Studio dataset (owner rule: every report and printout runs through Report
    // Studio), opened in /Reports/Viewer, laid out, exported, scheduled and archived by the platform. The hospital
    // only answers "what are the rows" (HospitalManagementSources) and "may this person see them"
    // (HospitalReportModuleAuthority):
    //   · clinical and operational reports — the hospital's `read` (the same people who see the screens);
    //   · financial and insurance reports — `bill` (money is the billing desk's and the administrator's).
    // One row per FACT (a diagnosis, a discharge, a lab order, a payment …) or per ward / doctor / payer where the
    // report is a ratio over a population; the Studio groups, sums and charts the rest. The dashboard (H50) reads
    // its tiles from these same datasets through the KPI catalogue, so a tile and its report never disagree.
    // ============================================================================================
    public static class HospitalReportCodes
    {
        public const string TopDiagnoses = "Hospital.TopDiagnoses";
        public const string Readmissions = "Hospital.Readmissions";
        public const string LabTurnaround = "Hospital.LabTurnaround";
        public const string CriticalValues = "Hospital.CriticalValues";
        public const string Antibiogram = "Hospital.Antibiogram";   // H231
        public const string BedOccupancy = "Hospital.BedOccupancy";
        public const string DowntimeCensus = "Hospital.DowntimeCensus";   // H256
        public const string ErWaiting = "Hospital.ErWaiting";
        public const string Theatre = "Hospital.TheatreUtilisation";
        public const string DoctorProductivity = "Hospital.DoctorProductivity";
        public const string Revenue = "Hospital.Revenue";
        public const string InsuranceAgeing = "Hospital.InsuranceAgeing";
        public const string CashierCollection = "Hospital.CashierCollection";
        public const string ServiceProfitability = "Hospital.ServiceProfitability";
        public const string RejectionReasons = "Hospital.RejectionReasons";
        public const string PendingApprovals = "Hospital.PendingApprovals";
        public const string PayerPerformance = "Hospital.PayerPerformance";
    }

    public static class HospitalReportGroups
    {
        public const string Clinical = "Clinical", Operational = "Operational", Financial = "Financial", Insurance = "Insurance";
        public static readonly IReadOnlyList<string> All = new[] { Clinical, Operational, Financial, Insurance };
    }

    /// One management report: its dataset and the group the reports screen lists it under.
    public sealed record HospitalManagementReport(string Group, ReportDatasetDefinition Dataset, string Sort, bool Descending = false);

    public static class HospitalManagementDatasets
    {
        public const int MaxRows = 20_000;

        private static readonly ReportFilterOperator[] Compare = { ReportFilterOperator.Equals, ReportFilterOperator.NotEquals, ReportFilterOperator.GreaterThan,
            ReportFilterOperator.GreaterOrEqual, ReportFilterOperator.LessThan, ReportFilterOperator.LessOrEqual };

        private static ReportDatasetField F(string key, string ar, string en, ReportFieldType type = ReportFieldType.String, double width = 30, bool visible = true)
        {
            bool number = type is ReportFieldType.Money or ReportFieldType.Decimal or ReportFieldType.Integer;
            bool date = type is ReportFieldType.Date or ReportFieldType.DateTime;
            return new()
            {
                Key = key, TitleAr = ar, TitleEn = en, Type = type, WidthMm = width, VisibleByDefault = visible,
                Align = number ? ReportAlign.End : ReportAlign.Start,
                SupportedAggregates = number ? new[] { ReportAggregate.Sum, ReportAggregate.Average, ReportAggregate.Min, ReportAggregate.Max, ReportAggregate.Count }
                    : date ? new[] { ReportAggregate.Min, ReportAggregate.Max, ReportAggregate.Count } : new[] { ReportAggregate.Count, ReportAggregate.CountDistinct },
                SupportedOperators = number || date ? Compare : new[] { ReportFilterOperator.Equals, ReportFilterOperator.NotEquals, ReportFilterOperator.Contains, ReportFilterOperator.StartsWith },
            };
        }

        internal static ReportParameterDescriptor From() => new()
        {
            Key = "From", TitleAr = "من تاريخ", TitleEn = "From", Type = ReportFieldType.Date, Required = true, DefaultValue = "month-start",
            HelpTextAr = "بداية الفترة (شاملة).", HelpTextEn = "Start of the period (inclusive).",
        };
        internal static ReportParameterDescriptor To() => new()
        {
            Key = "To", TitleAr = "إلى تاريخ", TitleEn = "To", Type = ReportFieldType.Date, Required = true, DefaultValue = "today",
            HelpTextAr = "نهاية الفترة (شاملة).", HelpTextEn = "End of the period (inclusive).",
        };
        internal static ReportParameterDescriptor AsOf() => new()
        {
            Key = "AsOf", TitleAr = "في تاريخ", TitleEn = "As of", Type = ReportFieldType.Date, Required = true, DefaultValue = "today",
            HelpTextAr = "التاريخ الذي تُحسب عنده الأعمار.", HelpTextEn = "The date the ages are counted to.",
        };
        private static ReportParameterDescriptor[] Period() => new[] { From(), To(), AccountingDatasets.CompanyParam() };

        private static ReportDatasetDefinition D(string code, string permission, string ar, string en, string descAr, string descEn,
            ReportDatasetField[] fields, ReportParameterDescriptor[]? parameters = null) => new()
        {
            DatasetCode = code, Module = "Hospital", TitleAr = ar, TitleEn = en, DescriptionAr = descAr, DescriptionEn = descEn,
            DataSourceKey = code, RequiredPermissionKey = permission, MaxRows = MaxRows, RowCapPolicy = ReportRowCapPolicy.TruncateAndDeclare,
            Fields = fields, Parameters = parameters ?? Period(),
        };

        private const string C = HospitalReportPermissions.Clinical, M = HospitalReportPermissions.Finance;   // H156: clinical and operational analyses are the care team's

        public static IReadOnlyList<HospitalManagementReport> All() => new[]
        {
            // ---------------- clinical ----------------
            new HospitalManagementReport(HospitalReportGroups.Clinical, D(HospitalReportCodes.TopDiagnoses, C,
                "أكثر التشخيصات شيوعاً", "Top diagnoses",
                "كل كود تشخيص سُجل في كشوفات الفترة: عدد الكشوفات وعدد المرضى ونسبته من الكشوفات ذات التشخيص.",
                "Every diagnosis code recorded on the period's visits: visits, patients, and its share of the diagnosed visits.",
                new[]
                {
                    F("Rank", "الترتيب", "Rank", ReportFieldType.Integer, 12),
                    F("Code", "الكود", "Code", width: 20), F("Diagnosis", "التشخيص", "Diagnosis", width: 70),
                    F("Visits", "الكشوفات", "Visits", ReportFieldType.Integer, 18), F("Patients", "المرضى", "Patients", ReportFieldType.Integer, 18),
                    F("AsPrimary", "كتشخيص رئيسي", "As primary", ReportFieldType.Integer, 20), F("SharePct", "النسبة ٪", "Share %", ReportFieldType.Decimal, 18),
                }), "Visits", true),
            new HospitalManagementReport(HospitalReportGroups.Clinical, D(HospitalReportCodes.Readmissions, C,
                "معدل إعادة الدخول خلال ٣٠ يوماً", "30-day readmission rate",
                "كل خروج في الفترة، وهل عاد المريض للتنويم خلال ٣٠ يوماً. متوسط عمود «أُعيد دخوله» هو المعدل.",
                "Every discharge in the period, and whether the patient was admitted again within 30 days. The average of \"Readmitted\" is the rate.",
                new[]
                {
                    F("PatientName", "المريض", "Patient", width: 50), F("Mrn", "رقم الملف", "MRN", width: 24),
                    F("AdmittedAt", "الدخول", "Admitted", ReportFieldType.DateTime, 30), F("DischargedAt", "الخروج", "Discharged", ReportFieldType.DateTime, 30),
                    F("StayDays", "أيام الإقامة", "Stay (days)", ReportFieldType.Decimal, 18), F("Diagnosis", "التشخيص", "Diagnosis", width: 50),
                    F("Readmitted", "أُعيد دخوله", "Readmitted", ReportFieldType.Integer, 18), F("ReadmittedAt", "تاريخ العودة", "Readmitted on", ReportFieldType.DateTime, 30),
                    F("DaysToReadmission", "بعد (يوم)", "After (days)", ReportFieldType.Decimal, 18),
                }), "DischargedAt"),
            new HospitalManagementReport(HospitalReportGroups.Clinical, D(HospitalReportCodes.LabTurnaround, C,
                "زمن إنجاز المختبر", "Lab turnaround time",
                "كل طلب تحليل في الفترة بمراحله: من الطلب للسحب، ومن الاستلام للاعتماد، ومن الطلب للاعتماد بالدقائق.",
                "Every lab order in the period through its steps: order to collection, receipt to verification, and order to verification, in minutes.",
                new[]
                {
                    F("AccessionNo", "رقم العينة", "Accession", width: 26), F("Test", "التحليل", "Test", width: 50), F("Priority", "الأولوية", "Priority", width: 18),
                    F("PatientName", "المريض", "Patient", width: 45), F("Status", "الحالة", "Status", width: 20),
                    F("OrderedAt", "وقت الطلب", "Ordered", ReportFieldType.DateTime, 30), F("VerifiedAt", "وقت الاعتماد", "Verified", ReportFieldType.DateTime, 30),
                    F("OrderToCollectMin", "طلب ← سحب (د)", "Order → collect (min)", ReportFieldType.Integer, 22),
                    F("ReceiveToVerifyMin", "استلام ← اعتماد (د)", "Receive → verify (min)", ReportFieldType.Integer, 22),
                    F("TurnaroundMin", "الزمن الكلي (د)", "Turnaround (min)", ReportFieldType.Integer, 22),
                    F("Critical", "حرجة", "Critical", ReportFieldType.Integer, 14),
                }), "OrderedAt"),
            new HospitalManagementReport(HospitalReportGroups.Clinical, D(HospitalReportCodes.CriticalValues, C,
                "القيم الحرجة وزمن الإبلاغ", "Critical values and call time",
                "كل تنبيه بقيمة حرجة في الفترة: لمن أُرسل، ومتى أكّد الطبيب استلامه، وهل صُعّد.",
                "Every critical-value alert in the period: who it went to, when the doctor acknowledged it, and whether it was escalated.",
                new[]
                {
                    F("SentAt", "وقت الإرسال", "Sent", ReportFieldType.DateTime, 30), F("Level", "المستوى", "Level", width: 18),
                    F("Test", "الفحص", "Test", width: 45), F("Value", "القيمة", "Value", width: 24), F("Reason", "السبب", "Reason", width: 50),
                    F("PatientName", "المريض", "Patient", width: 45), F("Doctor", "الطبيب", "Doctor", width: 40),
                    F("AcknowledgedAt", "وقت التأكيد", "Acknowledged", ReportFieldType.DateTime, 30),
                    F("MinutesToAcknowledge", "زمن التأكيد (د)", "To acknowledge (min)", ReportFieldType.Integer, 22),
                    F("Acknowledged", "أُكّد", "Acknowledged", ReportFieldType.Integer, 14), F("Escalated", "صُعّد", "Escalated", ReportFieldType.Integer, 14),
                }), "SentAt"),
            // H231: the cumulative antibiogram (CLSI M39) — the first isolate of each organism per patient in the period
            new HospitalManagementReport(HospitalReportGroups.Clinical, D(HospitalReportCodes.Antibiogram, C,
                "خريطة الحساسية التراكمية للمضادات الحيوية", "Cumulative antibiogram",
                "لكل كائن حي ومضاد حيوي: عدد العزلات المختبرة ونسبة الحساس، من أول عزلة لكل مريض في الفترة (معيار CLSI M39). أقل من ٣٠ عزلة يُقرأ بحذر.",
                "Per organism and antibiotic: isolates tested and the percentage susceptible, from each patient's first isolate in the period (CLSI M39). Fewer than 30 isolates: read with caution.",
                new[]
                {
                    F("Organism", "الكائن الحي", "Organism", width: 50), F("Isolates", "العزلات", "Isolates", ReportFieldType.Integer, 16),
                    F("Antibiotic", "المضاد الحيوي", "Antibiotic", width: 45), F("Tested", "المختبرة", "Tested", ReportFieldType.Integer, 16),
                    F("Susceptible", "الحساسة", "Susceptible", ReportFieldType.Integer, 16), F("SusceptiblePct", "نسبة الحساسية ٪", "Susceptible %", ReportFieldType.Decimal, 20),
                    F("Caution", "أقل من ٣٠", "Under 30", ReportFieldType.Integer, 14),
                }), "Organism"),

            // ---------------- operational ----------------
            new HospitalManagementReport(HospitalReportGroups.Operational, D(HospitalReportCodes.BedOccupancy, C,
                "إشغال الأسرة ومتوسط الإقامة", "Bed occupancy and length of stay",
                "لكل جناح: الأسرة، أيام الأسرة المتاحة في الفترة، الأيام المشغولة ونسبة الإشغال، الإقامات التي بدأت وانتهت، ومتوسط الإقامة.",
                "For each ward: beds, the period's available bed-days, the occupied ones and the occupancy, the stays that began and ended, and the average stay.",
                new[]
                {
                    F("Floor", "الدور", "Floor", width: 30), F("Ward", "الجناح", "Ward", width: 40), F("Beds", "الأسرة", "Beds", ReportFieldType.Integer, 16),
                    F("OccupiedNow", "مشغول الآن", "Occupied now", ReportFieldType.Integer, 18),
                    F("BedDays", "أيام الأسرة المتاحة", "Bed-days available", ReportFieldType.Decimal, 24), F("OccupiedBedDays", "أيام مشغولة", "Bed-days occupied", ReportFieldType.Decimal, 22),
                    F("OccupancyPct", "الإشغال ٪", "Occupancy %", ReportFieldType.Decimal, 18),
                    F("StaysStarted", "دخول", "Stays begun", ReportFieldType.Integer, 16), F("StaysEnded", "خروج", "Stays ended", ReportFieldType.Integer, 16),
                    F("AverageStayDays", "متوسط الإقامة (يوم)", "Average stay (days)", ReportFieldType.Decimal, 22),
                }), "Ward"),
            // H256: the read-only downtime copy — printed on paper so the wards keep working when the system is down
            new HospitalManagementReport(HospitalReportGroups.Operational, D(HospitalReportCodes.DowntimeCensus, C,
                "نسخة التعطل: المنوَّمون والأدوية الجارية", "Downtime copy: inpatients and running medicines",
                "نسخة ورقية للعمل حين يتعطل النظام: كل منوَّم الآن بسريره وجناحه، والحساسية، والتشخيص، والطبيب المعالج، وكل دواء جارٍ بجرعته وطريقته ومواعيده. تُطبع في بداية كل مناوبة.",
                "A paper copy to work from when the system is down: every inpatient now with bed and ward, allergies, diagnosis, attending doctor, and every running medicine with dose, route and times. Print at the start of each shift.",
                new[]
                {
                    F("Ward", "الجناح", "Ward", width: 30), F("Bed", "السرير", "Bed", width: 16), F("PatientName", "المريض", "Patient", width: 45), F("Mrn", "رقم الملف", "MRN", width: 22),
                    F("AgeSex", "العمر والجنس", "Age / sex", width: 16), F("Allergies", "الحساسية", "Allergies", width: 40), F("Diagnosis", "التشخيص", "Diagnosis", width: 45),
                    F("Attending", "الطبيب المعالج", "Attending", width: 35), F("Medicines", "الأدوية الجارية", "Running medicines", width: 120),
                    F("AdmittedAt", "الدخول", "Admitted", ReportFieldType.DateTime, 24),
                }), "Ward"),
            new HospitalManagementReport(HospitalReportGroups.Operational, D(HospitalReportCodes.ErWaiting, C,
                "أوقات الانتظار في الطوارئ", "ER waiting times",
                "كل زيارة طوارئ في الفترة: درجة الفرز، والوقت من الوصول للفرز وللطبيب، ومدة البقاء، والنتيجة.",
                "Every ER visit in the period: the triage level, door-to-triage, door-to-doctor, the length of stay and the outcome.",
                new[]
                {
                    F("VisitNo", "رقم الزيارة", "Visit", width: 24), F("ArrivedAt", "الوصول", "Arrived", ReportFieldType.DateTime, 30),
                    F("Esi", "ESI", "ESI", ReportFieldType.Integer, 12), F("PatientName", "المريض", "Patient", width: 45), F("Complaint", "الشكوى", "Complaint", width: 50),
                    F("DoorToTriageMin", "حتى الفرز (د)", "Door → triage (min)", ReportFieldType.Integer, 20),
                    F("DoorToDoctorMin", "حتى الطبيب (د)", "Door → doctor (min)", ReportFieldType.Integer, 20),
                    F("LengthOfStayMin", "مدة البقاء (د)", "Length of stay (min)", ReportFieldType.Integer, 20),
                    F("Outcome", "النتيجة", "Outcome", width: 30), F("Stage", "المرحلة", "Stage", width: 22),
                    F("OpenNow", "ما زال في الطوارئ", "Still in the ER", ReportFieldType.Integer, 16, false),
                    F("WaitingNow", "ينتظر الآن", "Waiting now", ReportFieldType.Integer, 16, false),
                    F("PatientId", "معرّف المريض", "Patient id", ReportFieldType.Integer, 14, false),
                }), "ArrivedAt"),
            new HospitalManagementReport(HospitalReportGroups.Operational, D(HospitalReportCodes.Theatre, C,
                "استغلال غرف العمليات", "Theatre utilisation",
                "كل عملية مجدولة في الفترة: الغرفة والجراح، المدة المخططة والفعلية، تأخر البدء، والإلغاء وسببه.",
                "Every case scheduled in the period: the theatre and the surgeon, the planned and actual minutes, the start delay, and cancellations with their reason.",
                new[]
                {
                    F("CaseNo", "رقم العملية", "Case", width: 22), F("CaseDate", "التاريخ", "Date", ReportFieldType.Date, 24), F("Room", "الغرفة", "Theatre", width: 30),
                    F("Procedure", "العملية", "Procedure", width: 55), F("PatientName", "المريض", "Patient", width: 45), F("Surgeon", "الجراح", "Surgeon", width: 40), F("Status", "الحالة", "Status", width: 20),
                    F("PlannedMin", "المخطط (د)", "Planned (min)", ReportFieldType.Integer, 18), F("ActualMin", "الفعلي (د)", "Actual (min)", ReportFieldType.Integer, 18),
                    F("StartDelayMin", "تأخر البدء (د)", "Start delay (min)", ReportFieldType.Integer, 20), F("CancelReason", "سبب الإلغاء", "Cancel reason", width: 45),
                }), "CaseDate"),
            new HospitalManagementReport(HospitalReportGroups.Operational, D(HospitalReportCodes.DoctorProductivity, C,
                "إنتاجية الأطباء", "Doctor productivity",
                "لكل طبيب في الفترة: المواعيد، ومن كُشف عليهم، والغياب والإلغاء، والكشوفات الموقعة، والمرضى، وإيراد كشوفاته.",
                "For each doctor in the period: appointments, patients seen, no-shows and cancellations, signed visits, patients, and the revenue of their visits.",
                new[]
                {
                    F("Doctor", "الطبيب", "Doctor", width: 45), F("Department", "القسم", "Department", width: 35),
                    F("Appointments", "المواعيد", "Appointments", ReportFieldType.Integer, 18), F("Seen", "كُشف عليهم", "Seen", ReportFieldType.Integer, 16),
                    F("NoShow", "غياب", "No-show", ReportFieldType.Integer, 14), F("Cancelled", "ألغيت", "Cancelled", ReportFieldType.Integer, 14),
                    F("NoShowPct", "الغياب ٪", "No-show %", ReportFieldType.Decimal, 16), F("SignedVisits", "كشوفات موقعة", "Signed visits", ReportFieldType.Integer, 18),
                    F("Patients", "المرضى", "Patients", ReportFieldType.Integer, 16), F("Revenue", "الإيراد", "Revenue", ReportFieldType.Money, 26),
                    F("PractitionerId", "معرّف الطبيب", "Doctor id", ReportFieldType.Integer, 14, false),
                }), "Seen", true),

            // ---------------- financial ----------------
            new HospitalManagementReport(HospitalReportGroups.Financial, D(HospitalReportCodes.Revenue, M,
                "الإيراد حسب القسم والطبيب", "Revenue by department and doctor",
                "كل بند في الفواتير المغلقة في الفترة، بقسمه وطبيبه وفئته؛ يُجمّع في المصمم حسب القسم أو الطبيب.",
                "Every line of the bills closed in the period, with its department, doctor and category; the designer groups it by department or doctor.",
                new[]
                {
                    F("ClosedAt", "تاريخ الإغلاق", "Closed", ReportFieldType.DateTime, 30), F("BillNo", "رقم الفاتورة", "Bill", width: 24),
                    F("PatientName", "المريض", "Patient", width: 45), F("Department", "القسم", "Department", width: 35), F("Doctor", "الطبيب", "Doctor", width: 40),
                    F("Category", "الفئة", "Category", width: 22), F("Service", "الخدمة", "Service", width: 55),
                    F("Qty", "الكمية", "Qty", ReportFieldType.Decimal, 14), F("Gross", "الإجمالي", "Amount", ReportFieldType.Money, 26),
                }), "ClosedAt"),
            new HospitalManagementReport(HospitalReportGroups.Financial, D(HospitalReportCodes.InsuranceAgeing, M,
                "أعمار ذمم التأمين", "Insurance receivables ageing",
                "ما تبقى على كل جهة تأمين من دفعات المطالبات المرفوعة (رصيد دفتر المطالبات نفسه)، موزعاً على أعمار من تاريخ الرفع.",
                "What each insurer still owes on its submitted claim batches (the claim ledger's own balance), spread by age from the submission date.",
                new[]
                {
                    F("Payer", "جهة التأمين", "Insurer", width: 45), F("Batches", "الدفعات", "Batches", ReportFieldType.Integer, 16),
                    F("Current", "حتى ٣٠ يوم", "0–30 days", ReportFieldType.Money, 24), F("Days31To60", "٣١–٦٠", "31–60", ReportFieldType.Money, 24),
                    F("Days61To90", "٦١–٩٠", "61–90", ReportFieldType.Money, 24), F("Over90", "أكثر من ٩٠", "Over 90", ReportFieldType.Money, 24),
                    F("Total", "الإجمالي", "Total", ReportFieldType.Money, 26),
                }, new[] { AsOf(), AccountingDatasets.CompanyParam() }), "Total", true),
            new HospitalManagementReport(HospitalReportGroups.Financial, D(HospitalReportCodes.CashierCollection, M,
                "تحصيل الخزينة اليومي", "Daily cashier collection",
                "كل سند قبض في الفترة: اليوم والخزينة والوردية وطريقة الدفع والمبلغ؛ يُجمّع حسب اليوم أو الطريقة.",
                "Every receipt in the period: the day, the desk and shift, the method and the amount; grouped by day or by method.",
                new[]
                {
                    F("Day", "اليوم", "Day", ReportFieldType.Date, 24), F("ReceivedAt", "الوقت", "Received", ReportFieldType.DateTime, 30),
                    F("Desk", "الخزينة", "Desk", width: 24), F("ShiftId", "الوردية", "Shift", ReportFieldType.Integer, 14),
                    F("ReceiptNo", "رقم الإيصال", "Receipt", width: 24), F("BillNo", "رقم الفاتورة", "Bill", width: 24), F("PatientName", "المريض", "Patient", width: 45),
                    F("Method", "طريقة الدفع", "Method", width: 20), F("Amount", "المبلغ", "Amount", ReportFieldType.Money, 26),
                }), "ReceivedAt"),
            new HospitalManagementReport(HospitalReportGroups.Financial, D(HospitalReportCodes.ServiceProfitability, M,
                "ربحية الخدمة", "Service profitability",
                "لكل خدمة في الفواتير المغلقة في الفترة: الكمية والإيراد ونصيب الأطباء من كشوف الأتعاب، والمساهمة بعده. المستهلكات غير محمّلة هنا.",
                "For each service on the bills closed in the period: quantity, revenue, the doctors' share from the fee statements, and the contribution after it. Consumables are not costed here.",
                new[]
                {
                    F("Code", "الكود", "Code", width: 20), F("Service", "الخدمة", "Service", width: 55), F("Category", "الفئة", "Category", width: 22),
                    F("Qty", "الكمية", "Qty", ReportFieldType.Decimal, 16), F("Revenue", "الإيراد", "Revenue", ReportFieldType.Money, 26),
                    F("DoctorShare", "نصيب الأطباء", "Doctors' share", ReportFieldType.Money, 26), F("Contribution", "المساهمة", "Contribution", ReportFieldType.Money, 26),
                    F("ContributionPct", "المساهمة ٪", "Contribution %", ReportFieldType.Decimal, 18),
                }), "Revenue", true),

            // ---------------- insurance ----------------
            new HospitalManagementReport(HospitalReportGroups.Insurance, D(HospitalReportCodes.RejectionReasons, M,
                "أسباب الرفض", "Rejection reasons",
                "كل بند مطالبة رفضته جهة التأمين في الفترة: الجهة والدفعة والخدمة والمبلغ وكود الرفض وسببه، وهل أُعيد رفعه.",
                "Every claim line an insurer rejected in the period: the insurer, the batch, the service, the amount, the rejection code and reason, and whether it was resubmitted.",
                new[]
                {
                    F("DecidedAt", "تاريخ الرد", "Answered", ReportFieldType.DateTime, 30), F("Payer", "جهة التأمين", "Insurer", width: 40), F("BatchNo", "الدفعة", "Batch", width: 22),
                    F("PatientName", "المريض", "Patient", width: 45), F("Code", "الكود", "Code", width: 20), F("Service", "الخدمة", "Service", width: 50),
                    F("Amount", "المبلغ", "Amount", ReportFieldType.Money, 24), F("RejectCode", "كود الرفض", "Reject code", width: 20),
                    F("RejectReason", "سبب الرفض", "Reason", width: 60), F("Resubmitted", "أُعيد رفعه", "Resubmitted", ReportFieldType.Integer, 16),
                }), "DecidedAt"),
            new HospitalManagementReport(HospitalReportGroups.Insurance, D(HospitalReportCodes.PendingApprovals, M,
                "الموافقات المعلقة", "Pending approvals",
                "كل طلب موافقة مسبقة لم تردّ عليه جهة التأمين بعد، ومنذ كم ساعة ينتظر.",
                "Every pre-authorisation request an insurer has not answered yet, and how many hours it has waited.",
                new[]
                {
                    F("RequestNo", "رقم الطلب", "Request", width: 24), F("RequestedAt", "وقت الطلب", "Requested", ReportFieldType.DateTime, 30),
                    F("HoursWaiting", "ينتظر (ساعة)", "Waiting (hours)", ReportFieldType.Decimal, 20), F("PatientName", "المريض", "Patient", width: 45),
                    F("Mrn", "رقم الملف", "MRN", width: 24), F("Payer", "جهة التأمين", "Insurer", width: 40), F("Service", "الخدمة", "Service", width: 50),
                    F("Amount", "المبلغ", "Amount", ReportFieldType.Money, 24), F("Status", "الحالة", "Status", width: 20),
                }, new[] { AccountingDatasets.CompanyParam() }), "RequestedAt"),
            new HospitalManagementReport(HospitalReportGroups.Insurance, D(HospitalReportCodes.PayerPerformance, M,
                "أداء الجهات الدافعة", "Payer performance",
                "لكل جهة تأمين على دفعات الفترة: المطالبات، والمرفوع، والمقبول والمرفوض ونسبة الرفض، والمعلق، ومتوسط أيام السداد.",
                "For each insurer over the period's batches: claims, the amount claimed, accepted and rejected with the rejection rate, what is pending, and the average days to settle.",
                new[]
                {
                    F("Payer", "جهة التأمين", "Insurer", width: 45), F("Batches", "الدفعات", "Batches", ReportFieldType.Integer, 16), F("Claims", "المطالبات", "Claims", ReportFieldType.Integer, 16),
                    F("Claimed", "المرفوع", "Claimed", ReportFieldType.Money, 26), F("Accepted", "المقبول", "Accepted", ReportFieldType.Money, 26),
                    F("Rejected", "المرفوض", "Rejected", ReportFieldType.Money, 26), F("Pending", "المعلق", "Pending", ReportFieldType.Money, 26),
                    F("RejectionPct", "الرفض ٪", "Rejection %", ReportFieldType.Decimal, 18), F("AverageDaysToSettle", "متوسط أيام السداد", "Average days to settle", ReportFieldType.Decimal, 22),
                }), "Claimed", true),
        };

        public static IEnumerable<ReportDatasetDefinition> Datasets() => All().Select(r => r.Dataset);
    }

    public sealed class HospitalManagementReportProvider : IReportDefinitionProvider
    {
        public string ProviderName => "HospitalManagement";

        public IEnumerable<ReportDefinition> GetDefinitions()
        {
            int order = 450;
            foreach (var r in HospitalManagementDatasets.All())
            {
                var d = r.Dataset;
                yield return new ReportDefinition
                {
                    Code = d.DatasetCode, Module = d.Module, TitleAr = d.TitleAr, TitleEn = d.TitleEn, DescriptionAr = d.DescriptionAr, DescriptionEn = d.DescriptionEn,
                    DataSourceKey = d.DataSourceKey, PermissionKey = d.RequiredPermissionKey, CategoryKey = HospitalDocumentDatasetCodes.CategoryKey,
                    Tags = new[] { "hospital", "management", r.Group.ToLowerInvariant() },
                    Columns = d.Fields.Select(f => f.ToColumn()).ToList(), Parameters = d.Parameters,
                    DefaultSorts = new[] { ReportSort.By(r.Sort, r.Descending) },
                    DefaultFormat = ReportOutputFormat.Html,
                    Capabilities = new ReportCapabilities
                    {
                        Formats = new[] { ReportOutputFormat.Html, ReportOutputFormat.PrintHtml, ReportOutputFormat.Pdf, ReportOutputFormat.Xlsx, ReportOutputFormat.Csv },
                        MaxRows = d.MaxRows, PreviewRows = 200, AllowSchedule = true, AllowArchive = true, AllowShare = true,
                    },
                    Icon = r.Group switch
                    {
                        HospitalReportGroups.Clinical => "ki-outline ki-heart", HospitalReportGroups.Operational => "ki-outline ki-graph-3",
                        HospitalReportGroups.Financial => "ki-outline ki-dollar", _ => "ki-outline ki-shield-tick",
                    },
                    Color = "primary",
                    SortOrder = order++,
                };
            }
        }
    }
}
