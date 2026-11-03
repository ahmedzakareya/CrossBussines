using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL CONSULTATION SERVICE — the doctor's worklist and the consultation itself.
	//
	//   · Opening a consultation creates its encounter (one per appointment) and, when the patient
	//     was waiting, takes them into the room — one patient in a doctor's room at a time.
	//   · A Draft may be saved as often as needed; vitals are checked against human ranges.
	//   · Signing needs a chief complaint and at least one diagnosis with exactly one primary,
	//     every code from the catalogue; it locks the encounter and finishes the appointment.
	//   · A signed encounter is never changed again.
	// Every write asks HospitalAccessService for `consult` itself.
	// ==========================================================================================

	public sealed class HospitalDiagnosisInput
	{
		public string Code { get; set; } = "";
		public bool Primary { get; set; }
	}

	public sealed class HospitalPrescriptionInput
	{
		public string Drug { get; set; } = "";
		/// H10: the inventory item picked from the pharmacy's stock (null = free text, not stocked).
		public int? ItemId { get; set; }
		public string? Instructions { get; set; }
		public int Quantity { get; set; } = 1;
	}

	public sealed class HospitalEncounterInput
	{
		public string? ChiefComplaint { get; set; }
		public string? History { get; set; }
		public string? Examination { get; set; }
		public string? PlanNote { get; set; }
		public int? BpSystolic { get; set; }
		public int? BpDiastolic { get; set; }
		public int? HeartRate { get; set; }
		public decimal? TemperatureC { get; set; }
		public int? SpO2 { get; set; }
		public decimal? WeightKg { get; set; }
		public decimal? HeightCm { get; set; }
		public int? FollowUpDays { get; set; }
		public int? ReferToDepartmentId { get; set; }
		public List<HospitalDiagnosisInput> Diagnoses { get; set; } = new();
		public List<HospitalPrescriptionInput> Prescription { get; set; } = new();
		/// H6: catalogue services the doctor orders (lab, imaging, procedures); billed at signing.
		public List<int> OrderServiceIds { get; set; } = new();
	}

	public sealed record HospitalWorklistRow(HospitalAppointmentView Appointment, int? EncounterId, string? EncounterStatus, bool HasVitals);

	public sealed record HospitalEncounterView(HospitalEncounter Encounter, IReadOnlyList<HospitalEncounterDiagnosis> Diagnoses, IReadOnlyList<HospitalPrescriptionLine> Prescription,
		IReadOnlyList<HospitalEncounterOrder>? Orders = null);

	public sealed record HospitalEncounterSummary(int Id, DateTime StartedAt, int PractitionerId, string Status, string? ChiefComplaint, string? PrimaryCode, string? PrimaryName, string? PrimaryNameEn);

	public sealed class HospitalConsultationService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly HospitalBillingService? _billing;

		public HospitalConsultationService(CrossDbContext db, IHospitalAccessService access, HospitalBillingService? billing = null)
		{ _db = db; _access = access; _billing = billing; }

		private Task<bool> CanConsultAsync(BusinessContext ctx, CancellationToken ct) => _access.CanAsync(ctx, HospitalActions.Consult, null, ct);

		// ====================================================================================
		// THE DIAGNOSIS CATALOGUE
		// ====================================================================================

		/// A starter set of common outpatient ICD-10 codes, for a hospital that has none yet. The
		/// country's own edition (ICD-10-AM, ICD-10-CM) replaces it from the country settings unit.
		private static readonly (string Code, string Ar, string En)[] StandardCodes =
		{
			("E11.9", "داء السكري من النوع الثاني بدون مضاعفات", "Type 2 diabetes mellitus without complications"),
			("E11.65", "داء السكري من النوع الثاني مع ارتفاع السكر", "Type 2 diabetes mellitus with hyperglycaemia"),
			("I10", "ارتفاع ضغط الدم الأساسي", "Essential (primary) hypertension"),
			("I25.10", "مرض الشريان التاجي التصلبي", "Atherosclerotic heart disease of native coronary artery"),
			("I48.91", "رجفان أذيني غير محدد", "Unspecified atrial fibrillation"),
			("I50.9", "قصور القلب غير محدد", "Heart failure, unspecified"),
			("E78.5", "فرط شحميات الدم غير محدد", "Hyperlipidaemia, unspecified"),
			("E66.9", "السمنة غير محددة", "Obesity, unspecified"),
			("E03.9", "قصور الغدة الدرقية غير محدد", "Hypothyroidism, unspecified"),
			("D50.9", "فقر الدم بنقص الحديد غير محدد", "Iron deficiency anaemia, unspecified"),
			("E55.9", "نقص فيتامين د غير محدد", "Vitamin D deficiency, unspecified"),
			("J06.9", "التهاب حاد في المجاري التنفسية العليا", "Acute upper respiratory infection, unspecified"),
			("J02.9", "التهاب البلعوم الحاد", "Acute pharyngitis, unspecified"),
			("J03.90", "التهاب اللوزتين الحاد", "Acute tonsillitis, unspecified"),
			("J18.9", "التهاب رئوي غير محدد", "Pneumonia, unspecified organism"),
			("J45.909", "الربو غير محدد", "Unspecified asthma, uncomplicated"),
			("J44.9", "داء الانسداد الرئوي المزمن", "Chronic obstructive pulmonary disease, unspecified"),
			("J30.9", "التهاب الأنف التحسسي", "Allergic rhinitis, unspecified"),
			("H66.90", "التهاب الأذن الوسطى", "Otitis media, unspecified"),
			("K21.9", "ارتجاع المريء", "Gastro-oesophageal reflux disease without oesophagitis"),
			("K29.70", "التهاب المعدة غير محدد", "Gastritis, unspecified"),
			("K59.00", "الإمساك غير محدد", "Constipation, unspecified"),
			("A09", "التهاب المعدة والأمعاء المعدي", "Infectious gastroenteritis and colitis, unspecified"),
			("K35.80", "التهاب الزائدة الدودية الحاد", "Unspecified acute appendicitis"),
			("N39.0", "التهاب المسالك البولية", "Urinary tract infection, site not specified"),
			("N20.0", "حصوة الكلى", "Calculus of kidney"),
			("M54.5", "ألم أسفل الظهر", "Low back pain"),
			("M17.9", "خشونة الركبة", "Osteoarthritis of knee, unspecified"),
			("M79.1", "ألم عضلي", "Myalgia"),
			("S82.90", "كسر في الساق غير محدد", "Unspecified fracture of lower leg"),
			("S72.009", "كسر عنق الفخذ", "Fracture of unspecified part of neck of femur"),
			("R51", "صداع", "Headache"),
			("G43.909", "الصداع النصفي", "Migraine, unspecified"),
			("R50.9", "حمى غير محددة", "Fever, unspecified"),
			("R10.9", "ألم بطني غير محدد", "Unspecified abdominal pain"),
			("R07.9", "ألم في الصدر غير محدد", "Chest pain, unspecified"),
			("E86.0", "الجفاف", "Dehydration"),
			("L20.9", "الإكزيما التأتبية", "Atopic dermatitis, unspecified"),
			("L70.0", "حب الشباب", "Acne vulgaris"),
			("B35.4", "فطريات الجلد", "Tinea corporis"),
			("F41.9", "اضطراب القلق", "Anxiety disorder, unspecified"),
			("F32.9", "نوبة اكتئاب", "Major depressive disorder, single episode, unspecified"),
			("Z34.90", "متابعة حمل طبيعي", "Encounter for supervision of normal pregnancy"),
			("O24.419", "سكري الحمل", "Gestational diabetes mellitus in pregnancy"),
			("N94.6", "عسر الطمث", "Dysmenorrhoea, unspecified"),
			("Z00.00", "فحص طبي عام", "General adult medical examination"),
			("Z23", "تطعيم", "Encounter for immunisation"),
		};

		public async Task<HospitalResult> SeedDefaultCodesAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Manage, null, ct)) return HospitalResult.Fail("forbidden");
			var have = (await _db.HospitalDiagnosisCodes.Where(c => c.CompanyId == ctx.CompanyId).Select(c => c.Code).ToListAsync(ct)).ToHashSet();
			foreach (var c in StandardCodes.Where(c => !have.Contains(c.Code)))
				_db.HospitalDiagnosisCodes.Add(new HospitalDiagnosisCode { CompanyId = ctx.CompanyId, Code = c.Code, Name = c.Ar, NameEn = c.En, CreatedAt = DateTime.UtcNow });
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success();
		}

		public async Task<IReadOnlyList<HospitalDiagnosisCode>> SearchCodesAsync(BusinessContext ctx, string? q, CancellationToken ct = default)
		{
			var term = (q ?? "").Trim();
			var codes = _db.HospitalDiagnosisCodes.AsNoTracking().Where(c => c.CompanyId == ctx.CompanyId && c.IsActive);
			if (term.Length > 0) codes = codes.Where(c => c.Code.Contains(term) || c.Name.Contains(term) || c.NameEn.Contains(term));
			return await codes.OrderBy(c => c.Code).Take(30).ToListAsync(ct);
		}

		public Task<int> CodeCountAsync(BusinessContext ctx, CancellationToken ct = default)
			=> _db.HospitalDiagnosisCodes.CountAsync(c => c.CompanyId == ctx.CompanyId && c.IsActive, ct);

		// ====================================================================================
		// THE WORKLIST
		// ====================================================================================

		public async Task<IReadOnlyList<HospitalWorklistRow>> WorklistAsync(BusinessContext ctx, HospitalOutpatientService outpatient, int practitionerId, DateTime day, CancellationToken ct = default)
		{
			var appts = (await outpatient.DayAsync(ctx, day, ct)).Where(a => a.PractitionerId == practitionerId).ToList();
			var ids = appts.Select(a => a.Id).ToList();
			var encs = await _db.HospitalEncounters.AsNoTracking().Where(e => e.CompanyId == ctx.CompanyId && ids.Contains(e.AppointmentId)).ToListAsync(ct);
			var byAppt = encs.ToDictionary(e => e.AppointmentId);
			return appts.Select(a => byAppt.TryGetValue(a.Id, out var e)
				? new HospitalWorklistRow(a, e.ID, e.Status, e.HasVitals)
				: new HospitalWorklistRow(a, null, null, false)).ToList();
		}

		// ====================================================================================
		// THE ENCOUNTER
		// ====================================================================================

		/// Opens the consultation for an appointment: creates its encounter and, when the patient was
		/// waiting, takes them into the room. Returns the encounter id.
		public async Task<HospitalResult> OpenAsync(BusinessContext ctx, int appointmentId, int? employeeId, DateTime now, CancellationToken ct = default)
		{
			if (!await CanConsultAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			var a = await _db.HospitalAppointments.FirstOrDefaultAsync(x => x.ID == appointmentId && x.CompanyId == ctx.CompanyId, ct);
			if (a == null) return HospitalResult.Fail("not_found");
			var existing = await _db.HospitalEncounters.AsNoTracking().FirstOrDefaultAsync(e => e.CompanyId == ctx.CompanyId && e.AppointmentId == a.ID, ct);
			if (existing != null) return HospitalResult.Success(existing.ID);
			if (a.Status is not (HospitalAppointmentStatus.Arrived or HospitalAppointmentStatus.InRoom))
				return HospitalResult.Fail("The patient must check in before the consultation starts");

			if (a.Status == HospitalAppointmentStatus.Arrived)
			{
				if (await _db.HospitalAppointments.AnyAsync(x => x.CompanyId == ctx.CompanyId && x.PractitionerId == a.PractitionerId && x.Status == HospitalAppointmentStatus.InRoom && x.ID != a.ID, ct))
					return HospitalResult.Fail("The doctor already has a patient in the room");
				a.Status = HospitalAppointmentStatus.InRoom; a.StartedAt = now; a.UpdatedAt = DateTime.UtcNow; a.UpdatedByEmployeeId = employeeId;
			}
			var e = new HospitalEncounter
			{
				CompanyId = ctx.CompanyId, AppointmentId = a.ID, PatientId = a.PatientId, PractitionerId = a.PractitionerId,
				Status = HospitalEncounterStatus.Draft, StartedAt = now, CreatedAt = DateTime.UtcNow, CreatedByEmployeeId = employeeId,
			};
			_db.HospitalEncounters.Add(e);
			try { await _db.SaveChangesAsync(ct); }
			catch (DbUpdateException)
			{
				// Two openings at once: the other one created it. Answer with that encounter.
				_db.ChangeTracker.Clear();
				var other = await _db.HospitalEncounters.AsNoTracking().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.AppointmentId == appointmentId, ct);
				return other == null ? HospitalResult.Fail("The change could not be saved") : HospitalResult.Success(other.ID);
			}
			return HospitalResult.Success(e.ID);
		}

		public async Task<HospitalEncounterView?> GetAsync(BusinessContext ctx, int encounterId, CancellationToken ct = default)
		{
			var e = await _db.HospitalEncounters.AsNoTracking().FirstOrDefaultAsync(x => x.ID == encounterId && x.CompanyId == ctx.CompanyId, ct);
			if (e == null) return null;
			var dx = await _db.HospitalEncounterDiagnoses.AsNoTracking().Where(d => d.CompanyId == ctx.CompanyId && d.EncounterId == e.ID).OrderBy(d => d.SortOrder).ToListAsync(ct);
			var rx = await _db.HospitalPrescriptionLines.AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId && r.EncounterId == e.ID).OrderBy(r => r.SortOrder).ToListAsync(ct);
			var orders = await _db.HospitalEncounterOrders.AsNoTracking().Where(o => o.CompanyId == ctx.CompanyId && o.EncounterId == e.ID).OrderBy(o => o.ID).ToListAsync(ct);
			return new HospitalEncounterView(e, dx, rx, orders);
		}

		public async Task<IReadOnlyList<HospitalEncounterSummary>> PatientEncountersAsync(BusinessContext ctx, int patientId, CancellationToken ct = default)
		{
			var encs = await _db.HospitalEncounters.AsNoTracking().Where(e => e.CompanyId == ctx.CompanyId && e.PatientId == patientId)
				.OrderByDescending(e => e.StartedAt).Take(100).ToListAsync(ct);
			var ids = encs.Select(e => e.ID).ToList();
			var primaries = await _db.HospitalEncounterDiagnoses.AsNoTracking().Where(d => d.CompanyId == ctx.CompanyId && ids.Contains(d.EncounterId) && d.IsPrimary)
				.ToDictionaryAsync(d => d.EncounterId, ct);
			return encs.Select(e => primaries.TryGetValue(e.ID, out var p)
				? new HospitalEncounterSummary(e.ID, e.StartedAt, e.PractitionerId, e.Status, e.ChiefComplaint, p.Code, p.Name, p.NameEn)
				: new HospitalEncounterSummary(e.ID, e.StartedAt, e.PractitionerId, e.Status, e.ChiefComplaint, null, null, null)).ToList();
		}

		public Task<HospitalResult> SaveDraftAsync(BusinessContext ctx, int encounterId, HospitalEncounterInput input, int? employeeId, CancellationToken ct = default)
			=> WriteAsync(ctx, encounterId, input, employeeId, sign: false, DateTime.UtcNow, ct);

		public Task<HospitalResult> SignAsync(BusinessContext ctx, int encounterId, HospitalEncounterInput input, int? employeeId, DateTime now, CancellationToken ct = default)
			=> WriteAsync(ctx, encounterId, input, employeeId, sign: true, now, ct);

		private async Task<HospitalResult> WriteAsync(BusinessContext ctx, int encounterId, HospitalEncounterInput input, int? employeeId, bool sign, DateTime now, CancellationToken ct)
		{
			if (!await CanConsultAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			var e = await _db.HospitalEncounters.FirstOrDefaultAsync(x => x.ID == encounterId && x.CompanyId == ctx.CompanyId, ct);
			if (e == null) return HospitalResult.Fail("not_found");
			if (e.Status == HospitalEncounterStatus.Signed) return HospitalResult.Fail("This visit is signed and can no longer be changed");

			var range = Ranges(input);
			if (range != null) return range;
			if (input.FollowUpDays is int f && f is < 1 or > 730) return HospitalResult.Fail("Follow-up is 1 to 730 days");
			if (input.ReferToDepartmentId is int dep && !await _db.HospitalDepartments.AnyAsync(d => d.ID == dep && d.CompanyId == ctx.CompanyId, ct))
				return HospitalResult.Fail("not_found");

			// The diagnoses, resolved against the catalogue: an unknown code is refused, never stored.
			var codes = input.Diagnoses.Select(d => (d.Code ?? "").Trim()).Where(c => c.Length > 0).ToList();
			if (codes.Count != codes.Distinct(StringComparer.OrdinalIgnoreCase).Count()) return HospitalResult.Fail("A diagnosis is listed twice");
			var catalogue = await _db.HospitalDiagnosisCodes.AsNoTracking().Where(c => c.CompanyId == ctx.CompanyId && codes.Contains(c.Code)).ToDictionaryAsync(c => c.Code, ct);
			var unknown = codes.FirstOrDefault(c => !catalogue.ContainsKey(c));
			if (unknown != null) return HospitalResult.Fail("Unknown diagnosis code: {0}", unknown);
			var lines = input.Prescription.Where(r => !string.IsNullOrWhiteSpace(r.Drug)).ToList();
			// H10: a picked item must be one the pharmacy dispenses (a mapped category of this company); otherwise the line stays free text.
			var picked = lines.Where(r => r.ItemId != null).Select(r => r.ItemId!.Value).Distinct().ToList();
			var mapped = await _db.HospitalItemCategories.AsNoTracking().Where(c => c.CompanyId == ctx.CompanyId).Select(c => c.ItemCategoryId).ToListAsync(ct);
			var formulary = (await _db.Items.AsNoTracking().Where(i => i.CompanyID == ctx.CompanyId && i.IsActive && picked.Contains(i.ID) && mapped.Contains(i.ItemCategoryId)).Select(i => i.ID).ToListAsync(ct)).ToHashSet();
			var orderIds = input.OrderServiceIds.Distinct().ToList();
			var orderable = HospitalServiceCategories.Orderable.ToList();
			var ordered = await _db.HospitalServices.AsNoTracking()
				.Where(s => s.CompanyId == ctx.CompanyId && s.IsActive && orderIds.Contains(s.ID) && orderable.Contains(s.Category)).ToListAsync(ct);
			if (ordered.Count != orderIds.Count) return HospitalResult.Fail("An order is not a service the doctor can order");
			if (lines.Any(r => r.Quantity is < 1 or > 10000)) return HospitalResult.Fail("A quantity is 1 to 10000");

			var complaint = Clean(input.ChiefComplaint, 400);
			if (sign)
			{
				if (complaint == null) return HospitalResult.Fail("Enter the chief complaint before signing");
				if (codes.Count == 0) return HospitalResult.Fail("Add at least one diagnosis before signing");
				if (input.Diagnoses.Count(d => d.Primary && !string.IsNullOrWhiteSpace(d.Code)) != 1) return HospitalResult.Fail("Mark exactly one diagnosis as primary");
			}

			e.ChiefComplaint = complaint; e.History = Clean(input.History, 4000); e.Examination = Clean(input.Examination, 4000); e.PlanNote = Clean(input.PlanNote, 4000);
			e.BpSystolic = input.BpSystolic; e.BpDiastolic = input.BpDiastolic; e.HeartRate = input.HeartRate; e.TemperatureC = input.TemperatureC;
			e.SpO2 = input.SpO2; e.WeightKg = input.WeightKg; e.HeightCm = input.HeightCm;
			e.FollowUpDays = input.FollowUpDays; e.ReferToDepartmentId = input.ReferToDepartmentId;
			e.UpdatedAt = DateTime.UtcNow; e.UpdatedByEmployeeId = employeeId;

			await using var tx = await _db.Database.BeginTransactionAsync(ct);
			_db.HospitalEncounterDiagnoses.RemoveRange(await _db.HospitalEncounterDiagnoses.Where(d => d.CompanyId == ctx.CompanyId && d.EncounterId == e.ID).ToListAsync(ct));
			_db.HospitalPrescriptionLines.RemoveRange(await _db.HospitalPrescriptionLines.Where(r => r.CompanyId == ctx.CompanyId && r.EncounterId == e.ID).ToListAsync(ct));
			_db.HospitalEncounterOrders.RemoveRange(await _db.HospitalEncounterOrders.Where(o => o.CompanyId == ctx.CompanyId && o.EncounterId == e.ID).ToListAsync(ct));
			var orderRows = ordered.Select(s => new HospitalEncounterOrder { CompanyId = ctx.CompanyId, EncounterId = e.ID, ServiceId = s.ID, Code = s.Code, Name = s.Name, NameEn = s.NameEn, Category = s.Category, Price = s.Price }).ToList();
			_db.HospitalEncounterOrders.AddRange(orderRows);
			int order = 0;
			foreach (var d in input.Diagnoses.Where(d => !string.IsNullOrWhiteSpace(d.Code)))
			{
				var c = catalogue[d.Code.Trim()];
				_db.HospitalEncounterDiagnoses.Add(new HospitalEncounterDiagnosis { CompanyId = ctx.CompanyId, EncounterId = e.ID, Code = c.Code, Name = c.Name, NameEn = c.NameEn, IsPrimary = d.Primary, SortOrder = order++ });
			}
			order = 0;
			foreach (var r in lines)
				_db.HospitalPrescriptionLines.Add(new HospitalPrescriptionLine { CompanyId = ctx.CompanyId, EncounterId = e.ID, Drug = r.Drug.Trim()[..Math.Min(200, r.Drug.Trim().Length)], ItemId = r.ItemId is int iid && formulary.Contains(iid) ? iid : null, Instructions = Clean(r.Instructions, 400), Quantity = r.Quantity, SortOrder = order++ });

			if (sign)
			{
				e.Status = HospitalEncounterStatus.Signed; e.SignedAt = now; e.SignedByEmployeeId = employeeId;
				// The visit is over: the appointment follows.
				var a = await _db.HospitalAppointments.FirstOrDefaultAsync(x => x.ID == e.AppointmentId && x.CompanyId == ctx.CompanyId, ct);
				if (a != null && a.Status == HospitalAppointmentStatus.InRoom) { a.Status = HospitalAppointmentStatus.Done; a.FinishedAt = now; a.UpdatedAt = DateTime.UtcNow; a.UpdatedByEmployeeId = employeeId; }
				// The visit's charges - consultation fee and orders - join its bill in the same transaction.
				if (_billing != null) await _billing.AddEncounterChargesAsync(ctx, e, orderRows, employeeId, now, ct);
			}
			await _db.SaveChangesAsync(ct);
			await tx.CommitAsync(ct);
			return HospitalResult.Success(e.ID);
		}

		/// Human ranges: a value outside them is a typing slip, not a patient.
		private static HospitalResult? Ranges(HospitalEncounterInput i)
		{
			if (i.BpSystolic is int s && s is < 50 or > 260) return HospitalResult.Fail("Systolic pressure is 50 to 260");
			if (i.BpDiastolic is int d && d is < 30 or > 160) return HospitalResult.Fail("Diastolic pressure is 30 to 160");
			if (i.BpSystolic is int s2 && i.BpDiastolic is int d2 && d2 >= s2) return HospitalResult.Fail("Diastolic pressure must be below systolic");
			if (i.HeartRate is int h && h is < 20 or > 250) return HospitalResult.Fail("Heart rate is 20 to 250");
			if (i.TemperatureC is decimal t && (t < 30m || t > 44m)) return HospitalResult.Fail("Temperature is 30 to 44 °C");
			if (i.SpO2 is int o && o is < 50 or > 100) return HospitalResult.Fail("Oxygen saturation is 50 to 100");
			if (i.WeightKg is decimal w && (w < 0.5m || w > 400m)) return HospitalResult.Fail("Weight is 0.5 to 400 kg");
			if (i.HeightCm is decimal ht && (ht < 30m || ht > 250m)) return HospitalResult.Fail("Height is 30 to 250 cm");
			return null;
		}

		private static string? Clean(string? s, int max)
		{
			var v = (s ?? "").Trim();
			return v.Length == 0 ? null : v.Length > max ? v[..max] : v;
		}
	}
}
