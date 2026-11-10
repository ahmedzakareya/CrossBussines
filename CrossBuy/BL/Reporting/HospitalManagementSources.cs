using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // HOSPITAL H49 — the rows of the fifteen management reports (HospitalManagementDatasets).
    //
    // TENANCY: every query is filtered by the resolved context's CompanyId at its root, and every lookup
    // (patients, doctors, wards, payers …) by the same CompanyId — nothing is reached by an id alone.
    // A company of 0 is an empty set. A database without a module's tables is an empty set, not an error.
    // THE PERIOD: From/To are local days (To inclusive); a time stored in UTC is compared with the period's UTC
    // bounds, and every time shown is local. The theatre stores its start and end in clinic time, as its screen does.
    // NO SECOND FORMULA where the hospital already has one: the insurance balance is the claim ledger's
    // (HospitalClaimAccountingService.BatchLedgerAsync), the doctors' share is the fee statements'.
    // ============================================================================================
    /// The period a report reads: local days [From, ToExclusive), the same bounds in UTC, and the as-of day.
    public sealed class HospitalReportWindow
    {
        public DateTime From { get; init; }
        public DateTime ToExclusive { get; init; }
        public DateTime AsOf { get; init; }
        public DateTime FromUtc => DateTime.SpecifyKind(From, DateTimeKind.Local).ToUniversalTime();
        public DateTime ToUtc => DateTime.SpecifyKind(ToExclusive, DateTimeKind.Local).ToUniversalTime();
        public void Deconstruct(out DateTime from, out DateTime toExclusive, out DateTime fromUtc, out DateTime toUtc) { from = From; toExclusive = ToExclusive; fromUtc = FromUtc; toUtc = ToUtc; }

        /// Local days, To inclusive (the reports' parameters).
        public static HospitalReportWindow Days(DateTime from, DateTime to) => new() { From = from.Date, ToExclusive = to.Date.AddDays(1), AsOf = to.Date };

        public static HospitalReportWindow Of(ReportDataQuery q)
        {
            var today = DateTime.Today;
            var w = Days(q.Parameters.GetDate("From") ?? new DateTime(today.Year, today.Month, 1), q.Parameters.GetDate("To") ?? today);
            return new HospitalReportWindow { From = w.From, ToExclusive = w.ToExclusive, AsOf = (q.Parameters.GetDate("AsOf") ?? today).Date };
        }
    }

    public abstract class HospitalRegisterSource : IReportDataSource
    {
        protected readonly CrossDbContext Db;
        protected HospitalRegisterSource(CrossDbContext db) { Db = db; }
        public abstract string Key { get; }

        protected abstract Task<List<Dictionary<string, object?>>> RowsAsync(BusinessContext ctx, HospitalReportWindow q, bool arabic, CancellationToken ct);

        /// H50: the dashboard reads a report's rows through here — the same rows the report prints, never a second query.
        public async Task<List<Dictionary<string, object?>>> ReadAsync(BusinessContext ctx, HospitalReportWindow window, bool arabic, CancellationToken ct = default)
        {
            if (ctx.CompanyId <= 0) return new();
            try { return await RowsAsync(ctx, window, arabic, ct); }
            catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { return new(); }
        }

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var columns = query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns;
            var builder = new ReportDataSetBuilder(columns);
            if (query.Context.CompanyId <= 0) return builder.Build(totalRowCount: 0);
            List<Dictionary<string, object?>> rows;
            try { rows = await RowsAsync(query.Context, HospitalReportWindow.Of(query), AccountingSourceHelpers.Arabic(query), cancellationToken); }
            catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { rows = new(); }
            int cap = query.MaxRows > 0 ? query.MaxRows : HospitalManagementDatasets.MaxRows;
            bool truncated = rows.Count > cap;
            foreach (var r in rows.Take(cap)) builder.AddRow(r);
            return builder.Build(truncated, truncated ? null : rows.Count);
        }

        // ---------------- helpers ----------------
        protected static Dictionary<string, object?> Row() => new(StringComparer.Ordinal);
        protected static string P(bool arabic, string? ar, string? en) => HospitalDocumentRows.P(arabic, ar, en);
        protected static DateTime Utc(DateTime local) => DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime();
        protected static DateTime? Local(DateTime? utc) => utc is { } u ? DateTime.SpecifyKind(u, DateTimeKind.Utc).ToLocalTime() : null;
        protected static int? Minutes(DateTime? a, DateTime? b) => a is { } x && b is { } y ? (int)Math.Round((y - x).TotalMinutes) : null;
        protected static decimal Pct(decimal part, decimal whole) => whole == 0 ? 0 : Math.Round(part * 100m / whole, 1);

        protected static HospitalReportWindow Period(HospitalReportWindow w) => w;

        protected async Task<Dictionary<int, (string Name, string Mrn)>> PatientsAsync(int c, IEnumerable<int> ids, bool arabic, CancellationToken ct)
        {
            var list = ids.Distinct().ToList();
            return (await Db.HospitalPatients.AsNoTracking().Where(p => p.CompanyId == c && list.Contains(p.ID)).Select(p => new { p.ID, p.Name, p.NameEn, p.Mrn }).ToListAsync(ct))
                .ToDictionary(p => p.ID, p => (P(arabic, p.Name, p.NameEn), p.Mrn));
        }

        protected async Task<Dictionary<int, string>> DoctorsAsync(int c, IEnumerable<int> ids, bool arabic, CancellationToken ct)
        {
            var list = ids.Distinct().ToList();
            return (await Db.HospitalPractitioners.AsNoTracking().Where(p => p.CompanyId == c && list.Contains(p.ID)).Select(p => new { p.ID, p.Name, p.NameEn }).ToListAsync(ct))
                .ToDictionary(p => p.ID, p => P(arabic, p.Name, p.NameEn));
        }

        protected async Task<Dictionary<int, string>> DepartmentsAsync(int c, bool arabic, CancellationToken ct) =>
            (await Db.HospitalDepartments.AsNoTracking().Where(d => d.CompanyId == c).Select(d => new { d.ID, d.Name, d.NameEn }).ToListAsync(ct))
                .ToDictionary(d => d.ID, d => P(arabic, d.Name, d.NameEn));

        protected async Task<Dictionary<int, string>> PayersAsync(int c, bool arabic, CancellationToken ct) =>
            (await Db.Set<HospitalPayer>().AsNoTracking().Where(p => p.CompanyId == c).Select(p => new { p.ID, p.Name, p.NameEn }).ToListAsync(ct))
                .ToDictionary(p => p.ID, p => P(arabic, p.Name, p.NameEn));

        protected static string Label(bool arabic, string? code) => code == null ? "" : !arabic ? Spaced(code) : code switch
        {
            "Open" => "مفتوحة", "Closed" => "مغلقة", "Pending" => "معلق", "Submitted" => "مرفوع", "Approved" => "موافق", "Rejected" => "مرفوض", "Partial" => "جزئي",
            "Draft" => "مسودة", "Planned" => "مجدولة", "InProgress" => "جارية", "Done" => "انتهت", "Cancelled" => "ملغاة",
            "Ordered" => "مطلوب", "Collected" => "سُحبت", "Received" => "استُلمت", "Resulted" => "ظهرت النتيجة", "Verified" => "معتمد",
            "Routine" => "عادي", "Urgent" => "عاجل", "Stat" => "فوري", "Critical" => "حرجة", "Panic" => "خطرة",
            "Triage" => "فرز", "Waiting" => "انتظار", "Treatment" => "علاج", "Observation" => "ملاحظة", "Disposition" => "قرار",
            "Discharged" => "خروج", "Admitted" => "تنويم", "Transferred" => "تحويل", "LeftWithoutBeingSeen" => "غادر قبل الكشف", "Deceased" => "وفاة",
            "Ordering" => "الطبيب الطالب", "Chosen" => "طبيب محدد", "Escalation" => "تصعيد", "Cash" => "نقدي", "Card" => "بطاقة", "Transfer" => "تحويل", _ => code,
        };
        private static string Spaced(string code) => System.Text.RegularExpressions.Regex.Replace(code, "(?<=[a-z])(?=[A-Z])", " ");
    }

    // ================================= CLINICAL =================================

    public sealed class HospitalTopDiagnosesSource(CrossDbContext db) : HospitalRegisterSource(db)
    {
        public override string Key => HospitalReportCodes.TopDiagnoses;
        protected override async Task<List<Dictionary<string, object?>>> RowsAsync(BusinessContext ctx, HospitalReportWindow q, bool ar, CancellationToken ct)
        {
            var (_, _, f, t) = Period(q); int c = ctx.CompanyId;
            var dx = await (from d in Db.Set<HospitalEncounterDiagnosis>().AsNoTracking()
                            join e in Db.HospitalEncounters.AsNoTracking() on d.EncounterId equals e.ID
                            where d.CompanyId == c && e.CompanyId == c && e.StartedAt >= f && e.StartedAt < t
                            select new { d.Code, d.Name, d.NameEn, d.IsPrimary, d.EncounterId, e.PatientId }).ToListAsync(ct);
            int visits = dx.Select(x => x.EncounterId).Distinct().Count(), rank = 0;
            return dx.GroupBy(x => x.Code).Select(g => new { g.Key, First = g.First(), Visits = g.Select(x => x.EncounterId).Distinct().Count(), Patients = g.Select(x => x.PatientId).Distinct().Count(), Primary = g.Count(x => x.IsPrimary) })
                .OrderByDescending(x => x.Visits).ThenBy(x => x.Key)
                .Select(x => { var r = Row(); r["Rank"] = ++rank; r["Code"] = x.Key; r["Diagnosis"] = P(ar, x.First.Name, x.First.NameEn); r["Visits"] = x.Visits; r["Patients"] = x.Patients;
                    r["AsPrimary"] = x.Primary; r["SharePct"] = Pct(x.Visits, visits); return r; }).ToList();
        }
    }

    public sealed class HospitalReadmissionsSource(CrossDbContext db) : HospitalRegisterSource(db)
    {
        public const int Days = 30;
        public override string Key => HospitalReportCodes.Readmissions;
        protected override async Task<List<Dictionary<string, object?>>> RowsAsync(BusinessContext ctx, HospitalReportWindow q, bool ar, CancellationToken ct)
        {
            var (_, _, f, t) = Period(q); int c = ctx.CompanyId;
            var discharges = await Db.HospitalAdmissions.AsNoTracking().Where(a => a.CompanyId == c && a.DischargedAt >= f && a.DischargedAt < t).OrderBy(a => a.DischargedAt).ToListAsync(ct);
            var pids = discharges.Select(a => a.PatientId).Distinct().ToList();
            var later = await Db.HospitalAdmissions.AsNoTracking().Where(a => a.CompanyId == c && pids.Contains(a.PatientId)).Select(a => new { a.ID, a.PatientId, a.AdmittedAt }).ToListAsync(ct);
            var pats = await PatientsAsync(c, pids, ar, ct);
            return discharges.Select(a =>
            {
                var back = later.Where(x => x.PatientId == a.PatientId && x.ID != a.ID && x.AdmittedAt > a.DischargedAt && x.AdmittedAt <= a.DischargedAt!.Value.AddDays(Days)).OrderBy(x => x.AdmittedAt).FirstOrDefault();
                var p = pats.GetValueOrDefault(a.PatientId); var r = Row();
                r["PatientName"] = p.Name; r["Mrn"] = p.Mrn; r["AdmittedAt"] = Local(a.AdmittedAt); r["DischargedAt"] = Local(a.DischargedAt);
                r["StayDays"] = Math.Round((decimal)(a.DischargedAt!.Value - a.AdmittedAt).TotalDays, 1); r["Diagnosis"] = a.Diagnosis;
                r["Readmitted"] = back == null ? 0 : 1; r["ReadmittedAt"] = Local(back?.AdmittedAt);
                r["DaysToReadmission"] = back == null ? null : Math.Round((decimal)(back.AdmittedAt - a.DischargedAt!.Value).TotalDays, 1);
                return r;
            }).ToList();
        }
    }

    public sealed class HospitalLabTurnaroundSource(CrossDbContext db) : HospitalRegisterSource(db)
    {
        public override string Key => HospitalReportCodes.LabTurnaround;
        protected override async Task<List<Dictionary<string, object?>>> RowsAsync(BusinessContext ctx, HospitalReportWindow q, bool ar, CancellationToken ct)
        {
            var (_, _, f, t) = Period(q); int c = ctx.CompanyId;
            var orders = await Db.HospitalLabOrders.AsNoTracking().Where(o => o.CompanyId == c && o.OrderedAt >= f && o.OrderedAt < t).OrderBy(o => o.OrderedAt).ToListAsync(ct);
            var pats = await PatientsAsync(c, orders.Select(o => o.PatientId), ar, ct);
            return orders.Select(o =>
            {
                var r = Row(); r["AccessionNo"] = o.AccessionNo; r["Test"] = P(ar, o.Name, o.NameEn); r["Priority"] = Label(ar, o.Priority);
                r["PatientName"] = pats.GetValueOrDefault(o.PatientId).Name; r["Status"] = Label(ar, o.Status);
                r["OrderedAt"] = Local(o.OrderedAt); r["VerifiedAt"] = Local(o.VerifiedAt);
                r["OrderToCollectMin"] = Minutes(o.OrderedAt, o.CollectedAt); r["ReceiveToVerifyMin"] = Minutes(o.ReceivedAt, o.VerifiedAt);
                r["TurnaroundMin"] = Minutes(o.OrderedAt, o.VerifiedAt); r["Critical"] = o.IsCritical ? 1 : 0;
                return r;
            }).ToList();
        }
    }

    public sealed class HospitalCriticalValuesSource(CrossDbContext db) : HospitalRegisterSource(db)
    {
        public override string Key => HospitalReportCodes.CriticalValues;
        protected override async Task<List<Dictionary<string, object?>>> RowsAsync(BusinessContext ctx, HospitalReportWindow q, bool ar, CancellationToken ct)
        {
            var (_, _, f, t) = Period(q); int c = ctx.CompanyId;
            var alerts = await Db.Set<HospitalLabCriticalAlert>().AsNoTracking().Where(a => a.CompanyId == c && a.SentAt >= f && a.SentAt < t).OrderBy(a => a.SentAt).ToListAsync(ct);
            var labIds = alerts.Where(a => a.LabOrderId != null).Select(a => a.LabOrderId!.Value).ToList();
            var radIds = alerts.Where(a => a.RadOrderId != null).Select(a => a.RadOrderId!.Value).ToList();
            var labs = await Db.HospitalLabOrders.AsNoTracking().Where(o => o.CompanyId == c && labIds.Contains(o.ID)).Select(o => new { o.ID, o.PatientId, o.Name, o.NameEn }).ToDictionaryAsync(o => o.ID, ct);
            var rads = await Db.Set<HospitalRadOrder>().AsNoTracking().Where(o => o.CompanyId == c && radIds.Contains(o.ID)).Select(o => new { o.ID, o.PatientId, o.Name, o.NameEn }).ToDictionaryAsync(o => o.ID, ct);
            var pats = await PatientsAsync(c, labs.Values.Select(x => x.PatientId).Concat(rads.Values.Select(x => x.PatientId)), ar, ct);
            var docs = await DoctorsAsync(c, alerts.Where(a => a.ToPractitionerId != null).Select(a => a.ToPractitionerId!.Value), ar, ct);
            return alerts.Select(a =>
            {
                var lab = a.LabOrderId is int li ? labs.GetValueOrDefault(li) : null; var rad = a.RadOrderId is int ri ? rads.GetValueOrDefault(ri) : null;
                int? pid = lab?.PatientId ?? rad?.PatientId;
                var r = Row(); r["SentAt"] = Local(a.SentAt); r["Level"] = Label(ar, a.Level);
                r["Test"] = lab != null ? P(ar, lab.Name, lab.NameEn) : rad != null ? P(ar, rad.Name, rad.NameEn) : null;
                r["Value"] = a.Value; r["Reason"] = Label(ar, a.Reason); r["PatientName"] = pid is int p ? pats.GetValueOrDefault(p).Name : null;
                r["Doctor"] = a.ToPractitionerId is int d ? docs.GetValueOrDefault(d) : null; r["AcknowledgedAt"] = Local(a.AcknowledgedAt);
                r["MinutesToAcknowledge"] = Minutes(a.SentAt, a.AcknowledgedAt); r["Acknowledged"] = a.AcknowledgedAt == null ? 0 : 1; r["Escalated"] = a.EscalatedAt == null ? 0 : 1;
                return r;
            }).ToList();
        }
    }

    // ================================= OPERATIONAL =================================

    public sealed class HospitalBedOccupancySource(CrossDbContext db) : HospitalRegisterSource(db)
    {
        public override string Key => HospitalReportCodes.BedOccupancy;
        protected override async Task<List<Dictionary<string, object?>>> RowsAsync(BusinessContext ctx, HospitalReportWindow q, bool ar, CancellationToken ct)
        {
            var (_, _, f, t) = Period(q); int c = ctx.CompanyId; var now = DateTime.UtcNow;
            var end = t < now ? t : now;   // a period reaching into the future counts the days that have passed
            var wards = await Db.Set<HospitalWard>().AsNoTracking().Where(w => w.CompanyId == c && w.IsActive).ToListAsync(ct);
            var floors = await Db.Set<HospitalFloor>().AsNoTracking().Where(x => x.CompanyId == c).ToDictionaryAsync(x => x.ID, ct);
            var rooms = await Db.Set<HospitalRoom>().AsNoTracking().Where(r => r.CompanyId == c && r.WardId != null).Select(r => new { r.ID, r.WardId }).ToDictionaryAsync(r => r.ID, r => r.WardId!.Value, ct);
            var beds = await Db.Set<HospitalBed>().AsNoTracking().Where(b => b.CompanyId == c && b.IsActive).Select(b => new { b.ID, b.RoomId }).ToListAsync(ct);
            var stays = await Db.HospitalBedStays.AsNoTracking().Where(s => s.CompanyId == c && s.StartedAt < t && (s.EndedAt == null || s.EndedAt >= f)).ToListAsync(ct);
            decimal days = (decimal)Math.Max(0, (end - f).TotalDays);
            return wards.Select(w =>
            {
                var wardBeds = beds.Where(b => rooms.GetValueOrDefault(b.RoomId) == w.ID).Select(b => b.ID).ToHashSet();
                var ws = stays.Where(s => rooms.GetValueOrDefault(s.RoomId) == w.ID).ToList();
                decimal occupied = ws.Sum(s => (decimal)Math.Max(0, ((s.EndedAt ?? now) < end ? (s.EndedAt ?? now) : end).Subtract(s.StartedAt > f ? s.StartedAt : f).TotalDays));
                var ended = ws.Where(s => s.EndedAt >= f && s.EndedAt < t).ToList();
                decimal available = wardBeds.Count * days;
                var r = Row(); r["Floor"] = floors.TryGetValue(w.FloorId, out var fl) ? P(ar, fl.Name, fl.NameEn) : null; r["Ward"] = P(ar, w.Name, w.NameEn);
                r["Beds"] = wardBeds.Count; r["OccupiedNow"] = ws.Count(s => s.EndedAt == null);
                r["BedDays"] = Math.Round(available, 1); r["OccupiedBedDays"] = Math.Round(occupied, 1); r["OccupancyPct"] = Pct(occupied, available);
                r["StaysStarted"] = ws.Count(s => s.StartedAt >= f && s.StartedAt < t); r["StaysEnded"] = ended.Count;
                r["AverageStayDays"] = ended.Count == 0 ? null : Math.Round((decimal)ended.Average(s => (s.EndedAt!.Value - s.StartedAt).TotalDays), 1);
                return r;
            }).ToList();
        }
    }

    public sealed class HospitalErWaitingSource(CrossDbContext db) : HospitalRegisterSource(db)
    {
        public override string Key => HospitalReportCodes.ErWaiting;
        protected override async Task<List<Dictionary<string, object?>>> RowsAsync(BusinessContext ctx, HospitalReportWindow q, bool ar, CancellationToken ct)
        {
            var (_, _, f, t) = Period(q); int c = ctx.CompanyId;
            var visits = await Db.Set<HospitalErVisit>().AsNoTracking().Where(v => v.CompanyId == c && v.ArrivedAt >= f && v.ArrivedAt < t).OrderBy(v => v.ArrivedAt).ToListAsync(ct);
            var pats = await PatientsAsync(c, visits.Where(v => v.PatientId != null).Select(v => v.PatientId!.Value), ar, ct);
            return visits.Select(v =>
            {
                var r = Row(); r["VisitNo"] = v.VisitNo; r["ArrivedAt"] = Local(v.ArrivedAt); r["Esi"] = v.Esi;
                r["PatientName"] = v.PatientId is int p ? pats.GetValueOrDefault(p).Name : v.UnknownLabel; r["Complaint"] = v.Complaint;
                r["DoorToTriageMin"] = Minutes(v.ArrivedAt, v.TriagedAt); r["DoorToDoctorMin"] = Minutes(v.ArrivedAt, v.SeenAt); r["LengthOfStayMin"] = Minutes(v.ArrivedAt, v.ClosedAt);
                r["Outcome"] = Label(ar, v.Outcome); r["Stage"] = Label(ar, v.Stage);
                r["OpenNow"] = v.ClosedAt == null ? 1 : 0; r["PatientId"] = v.PatientId;
                r["WaitingNow"] = v.ClosedAt == null && v.Stage is HospitalErStage.Triage or HospitalErStage.Waiting ? 1 : 0;
                return r;
            }).ToList();
        }
    }

    public sealed class HospitalTheatreSource(CrossDbContext db) : HospitalRegisterSource(db)
    {
        public override string Key => HospitalReportCodes.Theatre;
        protected override async Task<List<Dictionary<string, object?>>> RowsAsync(BusinessContext ctx, HospitalReportWindow q, bool ar, CancellationToken ct)
        {
            var (from, to, _, _) = Period(q); int c = ctx.CompanyId;
            var cases = await Db.Set<HospitalSurgery>().AsNoTracking().Where(s => s.CompanyId == c && s.CaseDate >= from && s.CaseDate < to).OrderBy(s => s.CaseDate).ThenBy(s => s.StartMinute).ToListAsync(ct);
            var roomIds = cases.Select(s => s.RoomId).Distinct().ToList();
            var rooms = await Db.Set<HospitalRoom>().AsNoTracking().Where(r => r.CompanyId == c && roomIds.Contains(r.ID)).ToDictionaryAsync(r => r.ID, ct);
            var docs = await DoctorsAsync(c, cases.Select(s => s.SurgeonId), ar, ct);
            var pats = await PatientsAsync(c, cases.Select(s => s.PatientId), ar, ct);
            return cases.Select(s =>
            {
                var room = rooms.GetValueOrDefault(s.RoomId); var r = Row();
                r["CaseNo"] = s.CaseNo; r["CaseDate"] = s.CaseDate.Date; r["Room"] = room == null ? null : P(ar, room.Name ?? room.Number, room.NameEn ?? room.Number);
                r["Procedure"] = P(ar, s.Name, s.NameEn); r["PatientName"] = pats.GetValueOrDefault(s.PatientId).Name; r["Surgeon"] = docs.GetValueOrDefault(s.SurgeonId); r["Status"] = Label(ar, s.Status);
                r["PlannedMin"] = s.DurationMinutes; r["ActualMin"] = Minutes(s.StartedAt, s.EndedAt);
                r["StartDelayMin"] = s.StartedAt is { } st ? (int)Math.Round((st - s.CaseDate.Date.AddMinutes(s.StartMinute)).TotalMinutes) : null;   // clinic time on both sides
                r["CancelReason"] = s.CancelReason;
                return r;
            }).ToList();
        }
    }

    public sealed class HospitalDoctorProductivitySource(CrossDbContext db) : HospitalRegisterSource(db)
    {
        public override string Key => HospitalReportCodes.DoctorProductivity;
        protected override async Task<List<Dictionary<string, object?>>> RowsAsync(BusinessContext ctx, HospitalReportWindow q, bool ar, CancellationToken ct)
        {
            var (from, to, f, t) = Period(q); int c = ctx.CompanyId;
            var appts = await Db.HospitalAppointments.AsNoTracking().Where(a => a.CompanyId == c && a.AppointmentDate >= from && a.AppointmentDate < to)
                .Select(a => new { a.PractitionerId, a.PatientId, a.Status }).ToListAsync(ct);
            var signed = await Db.HospitalEncounters.AsNoTracking().Where(e => e.CompanyId == c && e.SignedAt >= f && e.SignedAt < t).Select(e => new { e.PractitionerId, e.PatientId }).ToListAsync(ct);
            var revenue = await (from l in Db.HospitalBillLines.AsNoTracking()
                                 join b in Db.HospitalBills.AsNoTracking() on l.BillId equals b.ID
                                 join e in Db.HospitalEncounters.AsNoTracking() on b.EncounterId equals e.ID
                                 where l.CompanyId == c && b.CompanyId == c && e.CompanyId == c && b.Status == HospitalBillStatus.Closed && b.ClosedAt >= f && b.ClosedAt < t
                                 select new { e.PractitionerId, Amount = l.Quantity * l.UnitPrice }).ToListAsync(ct);
            var doctors = await Db.HospitalPractitioners.AsNoTracking().Where(p => p.CompanyId == c && p.IsActive).ToListAsync(ct);
            var deps = await DepartmentsAsync(c, ar, ct);
            return doctors.Select(d =>
            {
                var mine = appts.Where(a => a.PractitionerId == d.ID).ToList();
                int booked = mine.Count(a => a.Status != HospitalAppointmentStatus.Cancelled), noShow = mine.Count(a => a.Status == HospitalAppointmentStatus.NoShow);
                var r = Row(); r["Doctor"] = P(ar, d.Name, d.NameEn); r["Department"] = d.DepartmentId is int dep ? deps.GetValueOrDefault(dep) : null;
                r["Appointments"] = booked; r["Seen"] = mine.Count(a => a.Status == HospitalAppointmentStatus.Done); r["NoShow"] = noShow;
                r["Cancelled"] = mine.Count(a => a.Status == HospitalAppointmentStatus.Cancelled); r["NoShowPct"] = Pct(noShow, booked);
                r["SignedVisits"] = signed.Count(e => e.PractitionerId == d.ID);
                r["Patients"] = mine.Where(a => a.Status != HospitalAppointmentStatus.Cancelled).Select(a => a.PatientId).Concat(signed.Where(e => e.PractitionerId == d.ID).Select(e => e.PatientId)).Distinct().Count();
                r["Revenue"] = revenue.Where(x => x.PractitionerId == d.ID).Sum(x => x.Amount); r["PractitionerId"] = d.ID;
                return r;
            }).Where(r => (int)r["Appointments"]! + (int)r["SignedVisits"]! + (int)r["Cancelled"]! > 0 || (decimal)r["Revenue"]! != 0).ToList();
        }
    }

    // ================================= FINANCIAL =================================

    public sealed class HospitalRevenueSource(CrossDbContext db) : HospitalRegisterSource(db)
    {
        public override string Key => HospitalReportCodes.Revenue;
        protected override async Task<List<Dictionary<string, object?>>> RowsAsync(BusinessContext ctx, HospitalReportWindow q, bool ar, CancellationToken ct)
        {
            var (_, _, f, t) = Period(q); int c = ctx.CompanyId;
            var lines = await (from l in Db.HospitalBillLines.AsNoTracking()
                               join b in Db.HospitalBills.AsNoTracking() on l.BillId equals b.ID
                               where l.CompanyId == c && b.CompanyId == c && b.Status == HospitalBillStatus.Closed && b.ClosedAt >= f && b.ClosedAt < t
                               orderby b.ClosedAt, b.ID, l.ID
                               select new { b.ClosedAt, b.BillNo, b.PatientId, b.EncounterId, l.DepartmentId, l.Category, l.Description, l.DescriptionEn, l.Quantity, l.UnitPrice }).ToListAsync(ct);
            var encIds = lines.Where(x => x.EncounterId != null).Select(x => x.EncounterId!.Value).Distinct().ToList();
            var encDoc = await Db.HospitalEncounters.AsNoTracking().Where(e => e.CompanyId == c && encIds.Contains(e.ID)).ToDictionaryAsync(e => e.ID, e => e.PractitionerId, ct);
            var docs = await DoctorsAsync(c, encDoc.Values, ar, ct);
            var docDep = await Db.HospitalPractitioners.AsNoTracking().Where(p => p.CompanyId == c).ToDictionaryAsync(p => p.ID, p => p.DepartmentId, ct);
            var deps = await DepartmentsAsync(c, ar, ct);
            var pats = await PatientsAsync(c, lines.Select(x => x.PatientId), ar, ct);
            return lines.Select(x =>
            {
                int? doc = x.EncounterId is int e && encDoc.TryGetValue(e, out var d) ? d : null;
                int? dep = x.DepartmentId ?? (doc is int dd ? docDep.GetValueOrDefault(dd) : null);
                var r = Row(); r["ClosedAt"] = Local(x.ClosedAt); r["BillNo"] = x.BillNo; r["PatientName"] = pats.GetValueOrDefault(x.PatientId).Name;
                r["Department"] = dep is int k ? deps.GetValueOrDefault(k) : null; r["Doctor"] = doc is int dk ? docs.GetValueOrDefault(dk) : null;
                r["Category"] = x.Category; r["Service"] = P(ar, x.Description, x.DescriptionEn); r["Qty"] = x.Quantity; r["Gross"] = x.Quantity * x.UnitPrice;
                return r;
            }).ToList();
        }
    }

    public sealed class HospitalInsuranceAgeingSource(CrossDbContext db, IServiceProvider services) : HospitalRegisterSource(db)
    {
        public override string Key => HospitalReportCodes.InsuranceAgeing;
        protected override async Task<List<Dictionary<string, object?>>> RowsAsync(BusinessContext ctx, HospitalReportWindow q, bool ar, CancellationToken ct)
        {
            int c = ctx.CompanyId;
            var asOf = q.AsOf; var asOfEndUtc = Utc(asOf.AddDays(1));
            var claims = ActivatorUtilities.CreateInstance<HospitalClaimAccountingService>(services);
            var batches = await Db.Set<HospitalClaimBatch>().AsNoTracking()
                .Where(b => b.CompanyId == c && b.SubmittedAt != null && b.SubmittedAt < asOfEndUtc && b.Status != HospitalClaimBatchStatus.Draft).ToListAsync(ct);
            var payers = await PayersAsync(c, ar, ct);
            var owed = new List<(int Payer, decimal Days, decimal Amount)>();
            foreach (var b in batches)
            {
                var ledger = await claims.BatchLedgerAsync(ctx, b.ID, ct);   // the claim ledger's own balance, never a second sum
                if (ledger is not { } l || l.Outstanding <= 0) continue;
                owed.Add((b.PayerId, (decimal)(asOf - Local(b.SubmittedAt)!.Value.Date).TotalDays, l.Outstanding));
            }
            return owed.GroupBy(o => o.Payer).Select(g =>
            {
                var r = Row(); r["Payer"] = payers.GetValueOrDefault(g.Key); r["Batches"] = g.Count();
                r["Current"] = g.Where(o => o.Days <= 30).Sum(o => o.Amount); r["Days31To60"] = g.Where(o => o.Days > 30 && o.Days <= 60).Sum(o => o.Amount);
                r["Days61To90"] = g.Where(o => o.Days > 60 && o.Days <= 90).Sum(o => o.Amount); r["Over90"] = g.Where(o => o.Days > 90).Sum(o => o.Amount);
                r["Total"] = g.Sum(o => o.Amount);
                return r;
            }).ToList();
        }
    }

    public sealed class HospitalCashierCollectionSource(CrossDbContext db) : HospitalRegisterSource(db)
    {
        public override string Key => HospitalReportCodes.CashierCollection;
        protected override async Task<List<Dictionary<string, object?>>> RowsAsync(BusinessContext ctx, HospitalReportWindow q, bool ar, CancellationToken ct)
        {
            var (_, _, f, t) = Period(q); int c = ctx.CompanyId;
            var pays = await (from p in Db.HospitalPayments.AsNoTracking()
                              join b in Db.HospitalBills.AsNoTracking() on p.BillId equals b.ID
                              join s in Db.Set<HospitalCashierShift>().AsNoTracking() on p.ShiftId equals s.ID
                              where p.CompanyId == c && b.CompanyId == c && s.CompanyId == c && p.ReceivedAt >= f && p.ReceivedAt < t
                              orderby p.ReceivedAt
                              select new { p.ReceivedAt, s.Desk, p.ShiftId, p.ReceiptNo, b.BillNo, b.PatientId, p.Method, Amount = p.BaseAmount ?? p.Amount }).ToListAsync(ct);
            var pats = await PatientsAsync(c, pays.Select(x => x.PatientId), ar, ct);
            return pays.Select(x =>
            {
                var at = Local(x.ReceivedAt)!.Value; var r = Row();
                r["Day"] = at.Date; r["ReceivedAt"] = at; r["Desk"] = x.Desk; r["ShiftId"] = x.ShiftId; r["ReceiptNo"] = x.ReceiptNo; r["BillNo"] = x.BillNo;
                r["PatientName"] = pats.GetValueOrDefault(x.PatientId).Name; r["Method"] = Label(ar, x.Method); r["Amount"] = x.Amount;
                return r;
            }).ToList();
        }
    }

    public sealed class HospitalServiceProfitabilitySource(CrossDbContext db) : HospitalRegisterSource(db)
    {
        public override string Key => HospitalReportCodes.ServiceProfitability;
        protected override async Task<List<Dictionary<string, object?>>> RowsAsync(BusinessContext ctx, HospitalReportWindow q, bool ar, CancellationToken ct)
        {
            var (_, _, f, t) = Period(q); int c = ctx.CompanyId;
            var lines = await (from l in Db.HospitalBillLines.AsNoTracking()
                               join b in Db.HospitalBills.AsNoTracking() on l.BillId equals b.ID
                               where l.CompanyId == c && b.CompanyId == c && b.Status == HospitalBillStatus.Closed && b.ClosedAt >= f && b.ClosedAt < t
                               select new { l.ID, l.ServiceId, l.Code, l.Description, l.DescriptionEn, l.Category, l.Quantity, l.UnitPrice }).ToListAsync(ct);
            var ids = lines.Select(l => l.ID).ToList();
            var share = (await Db.Set<HospitalFeeStatementLine>().AsNoTracking().Where(x => x.CompanyId == c && ids.Contains(x.BillLineId)).Select(x => new { x.BillLineId, x.Share }).ToListAsync(ct))
                .GroupBy(x => x.BillLineId).ToDictionary(g => g.Key, g => g.Sum(x => x.Share));
            return lines.GroupBy(l => l.ServiceId?.ToString() ?? "code:" + l.Code).Select(g =>
            {
                var first = g.First(); decimal revenue = g.Sum(l => l.Quantity * l.UnitPrice), doctors = g.Sum(l => share.GetValueOrDefault(l.ID));
                var r = Row(); r["Code"] = first.Code; r["Service"] = P(ar, first.Description, first.DescriptionEn); r["Category"] = first.Category; r["Qty"] = g.Sum(l => l.Quantity);
                r["Revenue"] = revenue; r["DoctorShare"] = doctors; r["Contribution"] = revenue - doctors; r["ContributionPct"] = Pct(revenue - doctors, revenue);
                return r;
            }).OrderByDescending(r => (decimal)r["Revenue"]!).ToList();
        }
    }

    // ================================= INSURANCE =================================

    public sealed class HospitalRejectionReasonsSource(CrossDbContext db) : HospitalRegisterSource(db)
    {
        public override string Key => HospitalReportCodes.RejectionReasons;
        protected override async Task<List<Dictionary<string, object?>>> RowsAsync(BusinessContext ctx, HospitalReportWindow q, bool ar, CancellationToken ct)
        {
            var (_, _, f, t) = Period(q); int c = ctx.CompanyId;
            var rows = await (from l in Db.Set<HospitalClaimLine>().AsNoTracking()
                              join cl in Db.Set<HospitalClaim>().AsNoTracking() on l.ClaimId equals cl.ID
                              join b in Db.Set<HospitalClaimBatch>().AsNoTracking() on cl.BatchId equals b.ID
                              where l.CompanyId == c && cl.CompanyId == c && b.CompanyId == c && (l.Status == HospitalClaimLineStatus.Rejected || l.Status == HospitalClaimLineStatus.Resubmitted) && l.DecidedAt >= f && l.DecidedAt < t
                              orderby l.DecidedAt
                              select new { l.ID, l.Status, l.DecidedAt, b.PayerId, b.BatchNo, cl.PatientId, l.Code, l.Description, l.DescriptionEn, l.InsurerAmount, l.RejectCode, l.RejectReason }).ToListAsync(ct);
            var ids = rows.Select(x => x.ID).ToList();
            var again = (await Db.Set<HospitalClaimLine>().AsNoTracking().Where(x => x.CompanyId == c && x.ResubmitsLineId != null && ids.Contains(x.ResubmitsLineId.Value)).Select(x => x.ResubmitsLineId!.Value).ToListAsync(ct)).ToHashSet();
            var payers = await PayersAsync(c, ar, ct); var pats = await PatientsAsync(c, rows.Select(x => x.PatientId), ar, ct);
            return rows.Select(x =>
            {
                var r = Row(); r["DecidedAt"] = Local(x.DecidedAt); r["Payer"] = payers.GetValueOrDefault(x.PayerId); r["BatchNo"] = x.BatchNo;
                r["PatientName"] = pats.GetValueOrDefault(x.PatientId).Name; r["Code"] = x.Code; r["Service"] = P(ar, x.Description, x.DescriptionEn);
                r["Amount"] = x.InsurerAmount; r["RejectCode"] = x.RejectCode; r["RejectReason"] = x.RejectReason; r["Resubmitted"] = again.Contains(x.ID) || x.Status == HospitalClaimLineStatus.Resubmitted ? 1 : 0;   // a rejected line is marked Resubmitted once it is sent again
                return r;
            }).ToList();
        }
    }

    public sealed class HospitalPendingApprovalsSource(CrossDbContext db) : HospitalRegisterSource(db)
    {
        public override string Key => HospitalReportCodes.PendingApprovals;
        private static readonly string[] Waiting = { HospitalPreauthStatus.Pending };
        protected override async Task<List<Dictionary<string, object?>>> RowsAsync(BusinessContext ctx, HospitalReportWindow q, bool ar, CancellationToken ct)
        {
            int c = ctx.CompanyId; var now = DateTime.UtcNow;
            var rows = await Db.Set<HospitalPreauth>().AsNoTracking().Where(p => p.CompanyId == c && Waiting.Contains(p.Status)).OrderBy(p => p.RequestedAt).ToListAsync(ct);
            var sids = rows.Select(p => p.ServiceId).Distinct().ToList();
            var services = await Db.Set<HospitalService>().AsNoTracking().Where(s => s.CompanyId == c && sids.Contains(s.ID)).ToDictionaryAsync(s => s.ID, s => P(ar, s.Name, s.NameEn), ct);
            var payers = await PayersAsync(c, ar, ct); var pats = await PatientsAsync(c, rows.Select(x => x.PatientId), ar, ct);
            return rows.Select(p =>
            {
                var pt = pats.GetValueOrDefault(p.PatientId); var r = Row();
                r["RequestNo"] = p.RequestNo; r["RequestedAt"] = Local(p.RequestedAt); r["HoursWaiting"] = Math.Round((decimal)(now - p.RequestedAt).TotalHours, 1);
                r["PatientName"] = pt.Name; r["Mrn"] = pt.Mrn; r["Payer"] = payers.GetValueOrDefault(p.PayerId); r["Service"] = services.GetValueOrDefault(p.ServiceId);
                r["Amount"] = p.Amount; r["Status"] = Label(ar, p.Status);
                return r;
            }).ToList();
        }
    }

    public sealed class HospitalPayerPerformanceSource(CrossDbContext db) : HospitalRegisterSource(db)
    {
        public override string Key => HospitalReportCodes.PayerPerformance;
        protected override async Task<List<Dictionary<string, object?>>> RowsAsync(BusinessContext ctx, HospitalReportWindow q, bool ar, CancellationToken ct)
        {
            var (_, _, f, t) = Period(q); int c = ctx.CompanyId;
            var batches = await Db.Set<HospitalClaimBatch>().AsNoTracking().Where(b => b.CompanyId == c && b.CreatedAt >= f && b.CreatedAt < t && b.Status != HospitalClaimBatchStatus.Draft).ToListAsync(ct);
            var bids = batches.Select(b => b.ID).ToList();
            var lines = await (from l in Db.Set<HospitalClaimLine>().AsNoTracking()
                               join cl in Db.Set<HospitalClaim>().AsNoTracking() on l.ClaimId equals cl.ID
                               where l.CompanyId == c && cl.CompanyId == c && bids.Contains(cl.BatchId) && l.ResubmitsLineId == null
                               select new { cl.BatchId, l.ClaimId, l.Status, l.InsurerAmount, l.AcceptedAmount }).ToListAsync(ct);
            var payers = await PayersAsync(c, ar, ct);
            return batches.GroupBy(b => b.PayerId).Select(g =>
            {
                var ids = g.Select(b => b.ID).ToHashSet(); var mine = lines.Where(l => ids.Contains(l.BatchId)).ToList();
                decimal claimed = mine.Sum(l => l.InsurerAmount), rejected = mine.Where(l => l.Status is HospitalClaimLineStatus.Rejected or HospitalClaimLineStatus.Resubmitted).Sum(l => l.InsurerAmount);
                var settled = g.Where(b => b.SettledAt != null && b.SubmittedAt != null).ToList();
                var r = Row(); r["Payer"] = payers.GetValueOrDefault(g.Key); r["Batches"] = g.Count(); r["Claims"] = mine.Select(l => l.ClaimId).Distinct().Count();
                r["Claimed"] = claimed; r["Accepted"] = mine.Where(l => l.Status == HospitalClaimLineStatus.Accepted).Sum(l => l.AcceptedAmount ?? l.InsurerAmount);
                r["Rejected"] = rejected; r["Pending"] = mine.Where(l => l.Status == HospitalClaimLineStatus.Pending).Sum(l => l.InsurerAmount);
                r["RejectionPct"] = Pct(rejected, claimed);
                r["AverageDaysToSettle"] = settled.Count == 0 ? null : Math.Round((decimal)settled.Average(b => (b.SettledAt!.Value - b.SubmittedAt!.Value).TotalDays), 1);
                return r;
            }).ToList();
        }
    }
}
