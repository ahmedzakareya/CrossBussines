using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Reporting
{
	// ============================================================================================
	// HOSPITAL H144 — the front desk's papers as rows a Report Studio template lays out (owner rule: nothing prints except
	// through Report Studio). Each is ONE row; the facility and the patient come from the shared header, so a layout's
	// header band reads them as on every hospital paper. Tenancy is checked on the record the id names: another company's
	// id is an empty set.
	//   · Hospital.VisitSlip — one appointment: the visit slip and the queue ticket (how many are ahead is counted now);
	//   · Hospital.Wristband — one patient: identity, blood group, allergies, and the ward and bed while admitted;
	//   · Hospital.DepositReceipt — one deposit: amount, method, purpose, who took it, and what is used, refunded, left.
	// ============================================================================================
	internal static class HospitalDeskDocumentText
	{
		public static string VisitType(string v, bool ar) => v switch
		{
			HospitalVisitTypes.New => ar ? "كشف جديد" : "New visit",
			HospitalVisitTypes.FollowUp => ar ? "متابعة" : "Follow-up",
			HospitalVisitTypes.Procedure => ar ? "إجراء" : "Procedure",
			_ => ar ? "زيارة" : "Visit",
		};

		public static string Status(string s, bool ar) => s switch
		{
			HospitalAppointmentStatus.Booked => ar ? "محجوز" : "Booked",
			HospitalAppointmentStatus.Arrived => ar ? "حضر" : "Arrived",
			HospitalAppointmentStatus.InRoom => ar ? "عند الطبيب" : "With the doctor",
			HospitalAppointmentStatus.Done => ar ? "انتهى" : "Done",
			HospitalAppointmentStatus.NoShow => ar ? "لم يحضر" : "No-show",
			HospitalAppointmentStatus.Cancelled => ar ? "ملغى" : "Cancelled",
			_ => s,
		};

		public static string Method(string m, bool ar) => m switch
		{
			HospitalPaymentMethods.Cash => ar ? "نقدي" : "Cash",
			HospitalPaymentMethods.Card => ar ? "بطاقة" : "Card",
			HospitalPaymentMethods.Transfer => ar ? "تحويل" : "Transfer",
			HospitalPaymentMethods.Wallet => ar ? "محفظة" : "Wallet",
			_ => m,
		};

		public static string Purpose(string p, bool ar) => p switch
		{
			HospitalDepositPurpose.Procedure => ar ? "إجراء" : "Procedure",
			HospitalDepositPurpose.Admission => ar ? "تنويم" : "Admission",
			_ => ar ? "عام" : "General",
		};
	}

	public sealed class HospitalVisitSlipSource : IReportDataSource
	{
		private readonly CrossDbContext _db;
		public HospitalVisitSlipSource(CrossDbContext db) { _db = db; }
		public string Key => HospitalDocumentDatasetCodes.VisitSlip;

		public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(query);
			var ctx = query.Context; var ct = cancellationToken;
			var builder = new ReportDataSetBuilder(query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns);
			if (ctx.CompanyId <= 0) return builder.Build(totalRowCount: 0);
			var id = query.Parameters.GetInt("AppointmentId");
			if (id is not > 0) return builder.Build(totalRowCount: 0);
			var a = await _db.HospitalAppointments.AsNoTracking().FirstOrDefaultAsync(x => x.ID == id.Value && x.CompanyId == ctx.CompanyId, ct);
			if (a == null) return builder.Build(totalRowCount: 0);
			var patient = await _db.HospitalPatients.AsNoTracking().FirstOrDefaultAsync(p => p.ID == a.PatientId && p.CompanyId == ctx.CompanyId, ct);
			if (patient == null) return builder.Build(totalRowCount: 0);
			bool ar = AccountingSourceHelpers.Arabic(query);
			var doc = await _db.HospitalPractitioners.AsNoTracking().FirstOrDefaultAsync(d => d.ID == a.PractitionerId && d.CompanyId == ctx.CompanyId, ct);
			var dep = doc?.DepartmentId is int depId ? await _db.HospitalDepartments.AsNoTracking().FirstOrDefaultAsync(d => d.ID == depId && d.CompanyId == ctx.CompanyId, ct) : null;
			string? room = null;
			if (doc?.ClinicRoomId is int roomId)
				room = await _db.HospitalRooms.AsNoTracking().Where(r => r.ID == roomId && r.CompanyId == ctx.CompanyId).Select(r => r.Number).FirstOrDefaultAsync(ct);
			// how many arrived patients of the same doctor are before this one in the queue
			int? ahead = a.Status == HospitalAppointmentStatus.Arrived && a.QueueNumber is int q
				? await _db.HospitalAppointments.CountAsync(x => x.CompanyId == ctx.CompanyId && x.PractitionerId == a.PractitionerId && x.AppointmentDate == a.AppointmentDate
					&& x.Status == HospitalAppointmentStatus.Arrived && x.QueueNumber != null && x.QueueNumber < q, ct)
				: null;
			var header = await HospitalDocumentRows.HeaderAsync(_db, ctx, ar, patient, a.PractitionerId, "APT-" + a.ID.ToString("000000"), a.ArrivedAt ?? a.CreatedAt, a.ArrivedAt ?? a.CreatedAt, ct);
			header["LineNo"] = 1;
			header["QueueNumber"] = a.QueueNumber;
			header["VisitDate"] = a.AppointmentDate;
			header["VisitTime"] = (a.StartMinute / 60).ToString("00") + ":" + (a.StartMinute % 60).ToString("00");
			header["VisitType"] = HospitalDeskDocumentText.VisitType(a.VisitType, ar);
			header["Department"] = dep == null ? null : HospitalDocumentRows.P(ar, dep.Name, dep.NameEn);
			header["Room"] = room;
			header["AheadOfYou"] = ahead;
			header["Payer"] ??= ar ? "على نفقته" : "Self-pay";   // a slip never leaves the payer blank
			header["Status"] = HospitalDeskDocumentText.Status(a.Status, ar);
			header["ArrivedAt"] = a.ArrivedAt?.ToLocalTime();
			builder.AddRow(header);
			return builder.Build(totalRowCount: 1);
		}
	}

	public sealed class HospitalWristbandSource : IReportDataSource
	{
		private readonly CrossDbContext _db;
		public HospitalWristbandSource(CrossDbContext db) { _db = db; }
		public string Key => HospitalDocumentDatasetCodes.Wristband;

		public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(query);
			var ctx = query.Context; var ct = cancellationToken;
			var builder = new ReportDataSetBuilder(query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns);
			if (ctx.CompanyId <= 0) return builder.Build(totalRowCount: 0);
			var id = query.Parameters.GetInt("PatientId");
			if (id is not > 0) return builder.Build(totalRowCount: 0);
			var patient = await _db.HospitalPatients.AsNoTracking().FirstOrDefaultAsync(p => p.ID == id.Value && p.CompanyId == ctx.CompanyId, ct);
			if (patient == null) return builder.Build(totalRowCount: 0);
			bool ar = AccountingSourceHelpers.Arabic(query);
			var adm = await _db.HospitalAdmissions.AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId && x.PatientId == patient.ID && x.Status == HospitalAdmissionStatus.Active)
				.OrderByDescending(x => x.AdmittedAt).FirstOrDefaultAsync(ct);
			string? ward = null, bed = null;
			if (adm != null)
			{
				var stay = await _db.HospitalBedStays.AsNoTracking().Where(s => s.CompanyId == ctx.CompanyId && s.AdmissionId == adm.ID && s.EndedAt == null).OrderByDescending(s => s.StartedAt).FirstOrDefaultAsync(ct);
				if (stay != null)
				{
					bed = await _db.Set<HospitalBed>().AsNoTracking().Where(b => b.ID == stay.BedId && b.CompanyId == ctx.CompanyId).Select(b => b.Code).FirstOrDefaultAsync(ct);
					var wardId = await _db.HospitalRooms.AsNoTracking().Where(r => r.ID == stay.RoomId && r.CompanyId == ctx.CompanyId).Select(r => r.WardId).FirstOrDefaultAsync(ct);
					if (wardId is int w)
					{
						var wd = await _db.Set<HospitalWard>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == w && x.CompanyId == ctx.CompanyId, ct);
						ward = wd == null ? null : HospitalDocumentRows.P(ar, wd.Name, wd.NameEn);
					}
				}
			}
			var header = await HospitalDocumentRows.HeaderAsync(_db, ctx, ar, patient, null, patient.Mrn, DateTime.UtcNow, DateTime.UtcNow, ct);
			header["LineNo"] = 1;
			header["BirthDate"] = patient.BirthDate;
			header["BloodGroup"] = patient.BloodGroup;
			var allergies = string.IsNullOrWhiteSpace(patient.Allergies) ? null : string.Join(" · ", patient.Allergies.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
			header["Allergies"] = allergies;
			header["AllergyAlert"] = allergies == null ? null : (ar ? "تنبيه: حساسية" : "ALLERGY");
			header["Ward"] = ward; header["Bed"] = bed; header["AdmittedOn"] = adm?.AdmittedAt.ToLocalTime();
			builder.AddRow(header);
			return builder.Build(totalRowCount: 1);
		}
	}

	public sealed class HospitalDepositReceiptSource : IReportDataSource
	{
		private readonly CrossDbContext _db;
		public HospitalDepositReceiptSource(CrossDbContext db) { _db = db; }
		public string Key => HospitalDocumentDatasetCodes.DepositReceipt;

		public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(query);
			var ctx = query.Context; var ct = cancellationToken;
			var builder = new ReportDataSetBuilder(query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns);
			if (ctx.CompanyId <= 0) return builder.Build(totalRowCount: 0);
			var id = query.Parameters.GetInt("DepositId");
			if (id is not > 0) return builder.Build(totalRowCount: 0);
			var d = await _db.Set<HospitalPatientDeposit>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == id.Value && x.CompanyId == ctx.CompanyId, ct);
			if (d == null) return builder.Build(totalRowCount: 0);
			var patient = await _db.HospitalPatients.AsNoTracking().FirstOrDefaultAsync(p => p.ID == d.PatientId && p.CompanyId == ctx.CompanyId, ct);
			if (patient == null) return builder.Build(totalRowCount: 0);
			bool ar = AccountingSourceHelpers.Arabic(query);
			var cashier = await _db.Employee.AsNoTracking().Where(e => e.ID == d.CashierEmployeeId && e.EmpCompanyID == ctx.CompanyId).Select(e => new { e.FullName, e.FullNameEn }).FirstOrDefaultAsync(ct);
			var curId = await _db.Companies.AsNoTracking().Where(c => c.CompanyID == ctx.CompanyId).Select(c => c.DefaultCurrencyId).FirstOrDefaultAsync(ct);   // the facility's currency (a deposit is taken in it)
			var currency = curId is int cid ? await _db.Currencies.AsNoTracking().Where(c => c.ID == cid).Select(c => ar ? (c.Symbol ?? c.Code) : c.Code).FirstOrDefaultAsync(ct) : null;
			var header = await HospitalDocumentRows.HeaderAsync(_db, ctx, ar, patient, null, d.DepositNo, d.ReceivedAt, d.ReceivedAt, ct);
			header["LineNo"] = 1;
			header["Amount"] = d.Amount; header["Currency"] = currency;
			header["Method"] = HospitalDeskDocumentText.Method(d.Method, ar);
			header["Purpose"] = HospitalDeskDocumentText.Purpose(d.Purpose, ar);
			header["Note"] = d.Note;
			header["Cashier"] = cashier == null ? null : HospitalDocumentRows.P(ar, cashier.FullName, cashier.FullNameEn);
			header["Used"] = d.Used; header["Refunded"] = d.Refunded; header["Left"] = HospitalDepositRules.Left(d);
			builder.AddRow(header);
			return builder.Build(totalRowCount: 1);
		}
	}
}
