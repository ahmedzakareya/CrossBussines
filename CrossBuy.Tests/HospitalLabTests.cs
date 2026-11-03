using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H8 — the laboratory.
	//
	// What must never happen: a signed lab order that never reaches the bench (or reaches it twice), a
	// result entered on a specimen nobody received, an order counted as resulted with an analyte missing,
	// a result verified by the person who entered it, an unverified result in the patient file, a verified
	// one changed afterwards, a critical value whose flag clears without a record of who was told, or
	// another company's order on the bench.
	// ==========================================================================================
	public class HospitalLabTests
	{
		private const int CompanyOne = 1, CompanyTwo = 2;
		private static readonly DateTime Today = new(2026, 11, 15);   // Sunday
		private static readonly DateTime Now = new(2026, 11, 15, 9, 0, 0, DateTimeKind.Utc);

		private static BusinessContext Ctx(int employeeId = 7, int companyId = CompanyOne) => new()
		{
			CompanyId = companyId, EmployeeId = employeeId, UserId = "user-" + employeeId,
			Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid(),
		};

		private sealed class NoLedger : IHospitalLedger
		{
			public Task<(bool Ok, string? Error, int? EntryId)> PostAsync(BusinessContext ctx, DateTime date, string description, string descriptionEn, string sourceType, int sourceId, IReadOnlyList<HospitalLedgerLine> lines, CancellationToken ct = default)
				=> Task.FromResult<(bool, string?, int?)>((true, null, 1));
		}

		private sealed class World
		{
			public required PlatformTestHost Host;
			public required HospitalLabService Lab;
			public required HospitalLabAlertService Alerts;
			public required HospitalBillingService Billing;
			public required HospitalConsultationService Consult;
			public required HospitalOutpatientService Op;
			public required HospitalPatientService Patients;
		}

		private static World Make(PlatformTestHost host)
		{
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			var billing = new HospitalBillingService(host.Db, access, new NoLedger());
			var lab = new HospitalLabService(host.Db, access);
			return new World { Host = host, Lab = lab, Alerts = new(host.Db, access, lab), Billing = billing, Consult = new(host.Db, access, billing), Op = new(host.Db, access), Patients = new(host.Db, access) };
		}

		private static async Task<int> ServiceAsync(World w, string code, string category = HospitalServiceCategories.Lab, int companyId = CompanyOne)
		{
			var r = await w.Billing.SaveServiceAsync(Ctx(7, companyId), new HospitalServiceInput { Code = code, Name = "خ " + code, NameEn = "S " + code, Category = category, Price = 5m }, 7);
			Assert.True(r.Ok, r.Error);
			return r.Id!.Value;
		}

		/// A visit signed with the given orders, for a new patient. Returns (patient, encounter).
		private static async Task<(int Patient, int Encounter)> SignedVisitAsync(World w, string mrnName, params int[] orders)
		{
			var ctx = Ctx();
			Assert.True((await w.Consult.SeedDefaultCodesAsync(ctx)).Ok);
			var fee = await w.Host.Db.HospitalServices.Where(s => s.Code == "CONS-IM").Select(s => (int?)s.ID).FirstOrDefaultAsync() ?? await ServiceAsync(w, "CONS-IM", HospitalServiceCategories.Consultation);
			var doc = (await w.Op.SavePractitionerAsync(ctx, new HospitalPractitionerInput { Name = "د " + mrnName, NameEn = "Dr " + mrnName, WorkDays = new() { 0 }, SessionStartMinute = 540, SessionEndMinute = 600, ConsultationServiceId = fee }, 7)).Id!.Value;
			var saved = await w.Patients.SaveAsync(ctx, new HospitalPatientInput { Name = "م " + mrnName, NameEn = "P " + mrnName, Sex = "M" }, 7, Now);
			Assert.True(saved.Ok, saved.Error);
			var appt = (await w.Op.BookAsync(ctx, new HospitalBookingInput { PatientId = saved.Id!.Value, PractitionerId = doc, Date = Today, StartMinute = 540 }, 7, Today)).Id!.Value;
			Assert.True((await w.Op.MoveAsync(ctx, appt, HospitalAppointmentStatus.Arrived, 7, Now)).Ok);
			var enc = (await w.Consult.OpenAsync(ctx, appt, 7, Now)).Id!.Value;
			var signed = await w.Consult.SignAsync(ctx, enc, new HospitalEncounterInput { ChiefComplaint = "Fever", Diagnoses = new() { new() { Code = "J02.9", Primary = true } }, OrderServiceIds = orders.ToList() }, 7, Now);
			Assert.True(signed.Ok, signed.Error);
			return (saved.Id.Value, enc);
		}

		/// The standard panels on LAB-CBC and LAB-CRP, a visit ordering both and an X-ray; the orders synced.
		private static async Task<(int Patient, HospitalLabOrder Cbc, HospitalLabOrder Crp)> BenchAsync(World w)
		{
			var cbc = await ServiceAsync(w, "LAB-CBC"); var crp = await ServiceAsync(w, "LAB-CRP");
			var xray = await ServiceAsync(w, "RAD-CXR", HospitalServiceCategories.Radiology);
			Assert.True((await w.Lab.SeedStandardAsync(Ctx())).Ok);
			var (patient, _) = await SignedVisitAsync(w, "1", cbc, crp, xray);
			Assert.Equal(2, await w.Lab.SyncOrdersAsync(Ctx()));
			var orders = await w.Host.Db.HospitalLabOrders.AsNoTracking().OrderBy(o => o.ID).ToListAsync();
			return (patient, orders.Single(o => o.Code == "LAB-CBC"), orders.Single(o => o.Code == "LAB-CRP"));
		}

		private static async Task<Dictionary<int, string?>> ValuesAsync(World w, int serviceId, params string?[] values)
		{
			var ids = await w.Host.Db.HospitalLabAnalytes.Where(a => a.ServiceId == serviceId && a.IsActive).OrderBy(a => a.SortOrder).ThenBy(a => a.ID).Select(a => a.ID).ToListAsync();
			return ids.Select((id, i) => (id, v: i < values.Length ? values[i] : null)).ToDictionary(x => x.id, x => x.v);
		}

		private static async Task ReceiveAsync(World w, int id, int emp = 7)
		{
			Assert.True((await w.Lab.CollectAsync(Ctx(emp), id, emp, Now)).Ok);
			Assert.True((await w.Lab.ReceiveAsync(Ctx(emp), id, emp, Now)).Ok);
		}

		[Theory]
		[InlineData(7.0, "N")]
		[InlineData(3.9, "L")]
		[InlineData(11.5, "H")]
		[InlineData(1.9, "LL")]
		[InlineData(30.1, "HH")]
		[InlineData(30.0, "H")]      // a critical limit itself is not past it
		[InlineData(4.0, "N")]       // nor is a reference limit
		public void A_value_is_flagged_against_its_reference_and_critical_limits(double v, string flag)
			=> Assert.Equal(flag, HospitalLabService.Flag((decimal)v, 4.0m, 11.0m, 2.0m, 30.0m));

		[Fact]
		public void An_empty_limit_is_not_checked_and_no_value_is_normal()
		{
			Assert.Equal("H", HospitalLabService.Flag(250m, null, 200m, null, null));
			Assert.Equal("N", HospitalLabService.Flag(-5m, null, 200m, null, null));
			Assert.Equal("N", HospitalLabService.Flag(null, 4m, 11m, 2m, 30m));
		}

		[Fact]
		public async Task Each_signed_lab_line_becomes_one_order_once_and_nothing_else_does()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var (_, cbc, crp) = await BenchAsync(w);
			Assert.Equal(("LAB-000001", "LAB-000002"), (cbc.AccessionNo, crp.AccessionNo));
			Assert.Equal(HospitalLabStatus.Ordered, cbc.Status);
			Assert.Equal(0, await w.Lab.SyncOrdersAsync(Ctx()));                       // idempotent
			Assert.DoesNotContain(await host.Db.HospitalLabOrders.ToListAsync(), o => o.Code == "RAD-CXR");   // imaging is not the lab's

			var tsh = await ServiceAsync(w, "LAB-TSH");
			await SignedVisitAsync(w, "2", tsh);
			Assert.Equal(1, await w.Lab.SyncOrdersAsync(Ctx()));
			Assert.Equal("LAB-000003", (await host.Db.HospitalLabOrders.SingleAsync(o => o.Code == "LAB-TSH")).AccessionNo);
		}

		[Fact]
		public async Task The_bench_walks_in_order_and_an_order_is_resulted_only_with_every_analyte()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var (_, cbc, _) = await BenchAsync(w);
			var six = await ValuesAsync(w, cbc.ServiceId, "7.2", "4.8", "13.9", "41", "260", "58");

			Assert.Equal("Collect the specimen before receiving it in the lab", (await w.Lab.ReceiveAsync(Ctx(), cbc.ID, 7, Now)).Error);
			Assert.True((await w.Lab.CollectAsync(Ctx(), cbc.ID, 7, Now)).Ok);
			Assert.Equal("The specimen was already collected", (await w.Lab.CollectAsync(Ctx(), cbc.ID, 7, Now)).Error);
			Assert.Equal("Receive the specimen in the lab before entering results", (await w.Lab.SaveResultsAsync(Ctx(), cbc.ID, six, null, 7, Now)).Error);
			Assert.True((await w.Lab.ReceiveAsync(Ctx(), cbc.ID, 7, Now)).Ok);

			var five = await ValuesAsync(w, cbc.ServiceId, "7.2", "4.8", "13.9", "41", "260");
			Assert.True((await w.Lab.SaveResultsAsync(Ctx(), cbc.ID, five, null, 7, Now)).Ok);
			Assert.Equal(HospitalLabStatus.Received, (await host.Db.HospitalLabOrders.AsNoTracking().SingleAsync(o => o.ID == cbc.ID)).Status);
			Assert.Equal("Enter every result before verifying", (await w.Lab.VerifyAsync(Ctx(), cbc.ID, 7, Now)).Error);

			Assert.True((await w.Lab.SaveResultsAsync(Ctx(), cbc.ID, six, "ok", 7, Now)).Ok);
			var o = await host.Db.HospitalLabOrders.AsNoTracking().SingleAsync(x => x.ID == cbc.ID);
			Assert.Equal((HospitalLabStatus.Resulted, (int?)7), (o.Status, o.ResultedByEmployeeId));
			var nan = await w.Lab.SaveResultsAsync(Ctx(), cbc.ID, await ValuesAsync(w, cbc.ServiceId, "7.2", "4.8", "high"), null, 7, Now);
			Assert.Equal("{0} needs a number", nan.Error);
			Assert.Contains(Assert.Single(nan.Args!).ToString(), new[] { "Haemoglobin", "الهيموجلوبين" });

			// clearing a value takes it back to the bench
			Assert.True((await w.Lab.SaveResultsAsync(Ctx(), cbc.ID, five, null, 7, Now)).Ok);
			o = await host.Db.HospitalLabOrders.AsNoTracking().SingleAsync(x => x.ID == cbc.ID);
			Assert.Equal((HospitalLabStatus.Received, (DateTime?)null), (o.Status, o.ResultedAt));
		}

		[Fact]
		public async Task A_different_specialist_verifies_and_a_verified_result_is_locked_and_reaches_the_file()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var (patient, cbc, crp) = await BenchAsync(w);
			foreach (var (emp, role) in new[] { (1, HospitalRoles.HospitalAdministrator), (20, HospitalRoles.HospitalLabTechnician), (21, HospitalRoles.HospitalLabSpecialist), (22, HospitalRoles.HospitalLabSpecialist), (3, HospitalRoles.HospitalDoctor) })
				host.Seed.PlatformRoleAssignments.Add(new PlatformRoleAssignment { CompanyID = CompanyOne, Scope = EntityRegistry.ScopeHospital, PrincipalType = PlatformPrincipalTypes.Employee, PrincipalId = emp, Role = role, IsActive = true, CreatedAt = DateTime.UtcNow });
			await host.Seed.SaveChangesAsync();

			Assert.Equal("forbidden", (await w.Lab.CollectAsync(Ctx(3), crp.ID, 3, Now)).Error);    // a doctor orders, the lab works
			await ReceiveAsync(w, crp.ID, 21);
			Assert.True((await w.Lab.SaveResultsAsync(Ctx(21), crp.ID, await ValuesAsync(w, crp.ServiceId, "3.2"), null, 21, Now)).Ok);

			Assert.Equal("forbidden", (await w.Lab.VerifyAsync(Ctx(20), crp.ID, 20, Now)).Error);  // a technician enters, never verifies
			Assert.Equal("A different person verifies the results they did not enter", (await w.Lab.VerifyAsync(Ctx(21), crp.ID, 21, Now)).Error);
			Assert.Empty(await w.Lab.VerifiedForPatientAsync(Ctx(), patient));                      // not verified, not in the file
			Assert.True((await w.Lab.VerifyAsync(Ctx(22), crp.ID, 22, Now)).Ok);

			var (o, results) = Assert.Single(await w.Lab.VerifiedForPatientAsync(Ctx(), patient));
			Assert.Equal((crp.ID, 22), (o.ID, o.VerifiedByEmployeeId));
			Assert.Equal(3.2m, Assert.Single(results).Value);
			Assert.Equal("This result is verified and locked", (await w.Lab.SaveResultsAsync(Ctx(21), crp.ID, await ValuesAsync(w, crp.ServiceId, "9"), null, 21, Now)).Error);
			Assert.Equal("This result is verified and locked", (await w.Lab.SetPriorityAsync(Ctx(21), crp.ID, HospitalLabPriority.Stat)).Error);

			// the hospital administrator may verify what they entered themselves
			await ReceiveAsync(w, cbc.ID, 1);
			Assert.True((await w.Lab.SaveResultsAsync(Ctx(1), cbc.ID, await ValuesAsync(w, cbc.ServiceId, "7", "5", "14", "42", "250", "60"), null, 1, Now)).Ok);
			Assert.True((await w.Lab.VerifyAsync(Ctx(1), cbc.ID, 1, Now)).Ok);
		}

		[Fact]
		public async Task A_critical_value_raises_the_flag_until_someone_records_who_was_told()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var (_, cbc, _) = await BenchAsync(w);
			await ReceiveAsync(w, cbc.ID);
			Assert.Equal("This order has no critical value", (await w.Lab.AcknowledgeCriticalAsync(Ctx(), cbc.ID, "Dr K", 7, Now)).Error);

			Assert.True((await w.Lab.SaveResultsAsync(Ctx(), cbc.ID, await ValuesAsync(w, cbc.ServiceId, "32.4", "4.5", "12.1", "37", "310", "86"), null, 7, Now)).Ok);
			var results = await host.Db.HospitalLabResults.AsNoTracking().OrderBy(r => r.SortOrder).Select(r => r.Flag).ToListAsync();
			Assert.Equal(new[] { "HH", "N", "N", "N", "N", "H" }, results);
			Assert.Equal(1, (await w.Lab.StatsAsync(Ctx(), Today)).OpenCriticals);
			Assert.True(Assert.Single(await w.Lab.WorklistAsync(Ctx(), HospitalLabStatus.Resulted)).CriticalOpen);

			Assert.Equal("Write whom you told", (await w.Lab.AcknowledgeCriticalAsync(Ctx(), cbc.ID, "  ", 7, Now)).Error);
			Assert.True((await w.Lab.AcknowledgeCriticalAsync(Ctx(), cbc.ID, "Dr Khaled by phone", 7, Now)).Ok);
			Assert.Equal("Already recorded", (await w.Lab.AcknowledgeCriticalAsync(Ctx(), cbc.ID, "again", 7, Now)).Error);
			Assert.Equal(0, (await w.Lab.StatsAsync(Ctx(), Today)).OpenCriticals);

			// a correction that is no longer critical lowers the flag; a new critical one raises it afresh
			Assert.True((await w.Lab.SaveResultsAsync(Ctx(), cbc.ID, await ValuesAsync(w, cbc.ServiceId, "12.4", "4.5", "12.1", "37", "310", "86"), null, 7, Now)).Ok);
			Assert.False((await host.Db.HospitalLabOrders.AsNoTracking().SingleAsync(o => o.ID == cbc.ID)).IsCritical);
			Assert.True((await w.Lab.SaveResultsAsync(Ctx(), cbc.ID, await ValuesAsync(w, cbc.ServiceId, "12.4", "4.5", "6.0", "37", "310", "86"), null, 7, Now)).Ok);
			var o2 = await host.Db.HospitalLabOrders.AsNoTracking().SingleAsync(o => o.ID == cbc.ID);
			Assert.Equal((true, (DateTime?)null, (string?)null), (o2.IsCritical, o2.CriticalAcknowledgedAt, o2.CriticalNote));
		}

		[Fact]
		public async Task The_result_screen_shows_the_previous_verified_value_only()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var crp = await ServiceAsync(w, "LAB-CRP");
			Assert.True((await w.Lab.SeedStandardAsync(Ctx())).Ok);
			var analyte = await host.Db.HospitalLabAnalytes.Where(a => a.ServiceId == crp).Select(a => a.ID).SingleAsync();
			await SignedVisitAsync(w, "1", crp);
			await w.Lab.SyncOrdersAsync(Ctx());
			var first = await host.Db.HospitalLabOrders.AsNoTracking().SingleAsync();
			await ReceiveAsync(w, first.ID);
			Assert.True((await w.Lab.SaveResultsAsync(Ctx(), first.ID, new Dictionary<int, string?> { [analyte] = "12.5" }, null, 7, Now)).Ok);

			// a second order for the same patient: the first is not verified yet, so there is no previous value
			host.Db.HospitalLabOrders.Add(new HospitalLabOrder { CompanyId = CompanyOne, AccessionNo = "LAB-000099", PatientId = first.PatientId, ServiceId = crp, Code = "LAB-CRP", Name = "x", NameEn = "x", OrderedAt = Now });
			await host.Db.SaveChangesAsync();
			var second = await host.Db.HospitalLabOrders.AsNoTracking().SingleAsync(o => o.AccessionNo == "LAB-000099");
			Assert.Null(Assert.Single((await w.Lab.OrderAsync(Ctx(), second.ID))!.Lines).Previous);
			Assert.True((await w.Lab.VerifyAsync(Ctx(), first.ID, 7, Now)).Ok);
			Assert.Equal(12.5m, Assert.Single((await w.Lab.OrderAsync(Ctx(), second.ID))!.Lines).Previous);
		}

		[Fact]
		public async Task Another_companys_order_is_neither_seen_nor_worked()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var (patient, cbc, _) = await BenchAsync(w);
			var them = Ctx(9, CompanyTwo);
			Assert.Equal(0, await w.Lab.SyncOrdersAsync(them));
			Assert.Empty(await w.Lab.WorklistAsync(them, null));
			Assert.Null(await w.Lab.OrderAsync(them, cbc.ID));
			Assert.Equal("not_found", (await w.Lab.CollectAsync(them, cbc.ID, 9, Now)).Error);
			Assert.Empty(await w.Lab.VerifiedForPatientAsync(them, patient));
			Assert.Equal("not_found", (await w.Lab.SaveAnalyteAsync(them, new HospitalLabAnalyteInput { ServiceId = cbc.ServiceId, Code = "X", Name = "x", NameEn = "x" })).Error);
		}

		[Fact]
		public async Task Analyte_setup_keeps_its_limits_consistent()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var svc = await ServiceAsync(w, "LAB-NEW");
			HospitalLabAnalyteInput In(decimal? lo, decimal? hi, decimal? clo, decimal? chi, string code = "A1") => new() { ServiceId = svc, Code = code, Name = "أ", NameEn = "A", RefLow = lo, RefHigh = hi, CriticalLow = clo, CriticalHigh = chi };
			Assert.Equal("The low limit is above the high limit", (await w.Lab.SaveAnalyteAsync(Ctx(), In(10, 5, null, null))).Error);
			Assert.Equal("A critical limit lies outside the reference range", (await w.Lab.SaveAnalyteAsync(Ctx(), In(4, 11, 5, null))).Error);
			Assert.Equal("A critical limit lies outside the reference range", (await w.Lab.SaveAnalyteAsync(Ctx(), In(4, 11, null, 10))).Error);
			Assert.True((await w.Lab.SaveAnalyteAsync(Ctx(), In(4, 11, 2, 30, "a1"))).Ok);
			Assert.Equal("A1", (await host.Db.HospitalLabAnalytes.SingleAsync()).Code);
			Assert.Equal("Another analyte of this test uses this code", (await w.Lab.SaveAnalyteAsync(Ctx(), In(null, null, null, null, "A1"))).Error);
			var rad = await ServiceAsync(w, "RAD-X", HospitalServiceCategories.Radiology);
			Assert.Equal("not_found", (await w.Lab.SaveTestAsync(Ctx(), rad, new HospitalLabTestInput { Specimen = "s", Section = "s" })).Error);
		}

		// ==================================================================================
		// CRITICAL-VALUE ALERTS
		// ==================================================================================

		private static readonly DateTime Local = new(2026, 11, 15, 10, 0, 0);   // Sunday 10:00, inside a 09:00-14:00 session

		/// The bench with a critical CBC (WBC 32.4 HH, entered); the ordering doctor is employee 40 in department 1,
		/// with colleagues there: employee 41 on duty, employee 42 off today, and a doctor with no user.
		private static async Task<(HospitalLabOrder Cbc, int Ordering, int OnDuty, int OffDuty, int NoUser)> CriticalAsync(World w)
		{
			var (_, cbc, _) = await BenchAsync(w);
			await ReceiveAsync(w, cbc.ID);
			Assert.True((await w.Lab.SaveResultsAsync(Ctx(), cbc.ID, await ValuesAsync(w, cbc.ServiceId, "32.4", "4.5", "12.1", "37", "310", "86"), null, 7, Now)).Ok);
			var db = w.Host.Db;
			var pid = await db.HospitalEncounters.Where(e => e.ID == cbc.EncounterId).Select(e => e.PractitionerId).SingleAsync();
			var ordering = await db.HospitalPractitioners.SingleAsync(p => p.ID == pid);
			ordering.EmployeeId = 40; ordering.DepartmentId = 1;
			HospitalPractitioner Doc(string n, int? emp, string days) => new() { CompanyId = CompanyOne, Name = "د " + n, NameEn = "Dr " + n, EmployeeId = emp, DepartmentId = 1, WorkDays = days, SessionStartMinute = 540, SessionEndMinute = 840, IsActive = true, CreatedAt = Now };
			var on = Doc("On", 41, "0"); var off = Doc("Off", 42, "1"); var none = Doc("None", null, "1");
			db.HospitalPractitioners.AddRange(off, on, none);   // the off-duty colleague first, so "on duty first" is what picks
			await db.SaveChangesAsync();
			return (await db.HospitalLabOrders.AsNoTracking().SingleAsync(o => o.ID == cbc.ID), ordering.ID, on.ID, off.ID, none.ID);
		}

		[Theory]
		[InlineData("2.84", "2.84", true)]
		[InlineData("2.84", "2.840", true)]
		[InlineData("2.84", " 2.84 ", true)]
		[InlineData("2.84", "2.8", false)]
		[InlineData("2.84", "", false)]
		[InlineData("Positive", "positive", true)]
		[InlineData(null, "2.84", false)]
		public void A_read_back_matches_only_the_value_sent(string? sent, string typed, bool ok) => Assert.Equal(ok, HospitalLabAlertService.ReadBackMatches(sent, typed));

		[Fact]
		public async Task The_ordering_doctor_comes_first_and_an_alert_reaches_only_its_doctor_until_he_reads_the_value_back()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var (cbc, ordering, on, _, none) = await CriticalAsync(w);
			var rec = await w.Alerts.RecipientsAsync(Ctx(), HospitalAlertSource.Lab, cbc.ID, Local);
			Assert.Equal((ordering, HospitalLabAlertReason.Ordering), (rec[0].Practitioner.ID, rec[0].Why));
			Assert.Equal(on, rec[1].Practitioner.ID);                                     // the department, on duty first
			Assert.False(rec.Single(r => r.Practitioner.ID == none).HasUser);

			Assert.Equal("This doctor has no user in the system, so an alert cannot reach him", (await w.Alerts.SendAsync(Ctx(), HospitalAlertSource.Lab, cbc.ID, none, false, null, 7, Now)).Error);
			var sent = await w.Alerts.SendAsync(Ctx(), HospitalAlertSource.Lab, cbc.ID, ordering, false, null, 7, Now);
			Assert.True(sent.Ok, sent.Error);
			Assert.Equal("An alert to this doctor is already waiting", (await w.Alerts.SendAsync(Ctx(), HospitalAlertSource.Lab, cbc.ID, ordering, false, null, 7, Now)).Error);

			Assert.Empty(await w.Alerts.MineAsync(Ctx(41), Now, Local));                  // nobody else sees it
			var mine = Assert.Single(await w.Alerts.MineAsync(Ctx(40), Now, Local));
			Assert.Equal(("32.4", HospitalLabAlertLevel.Critical, "HH"), (mine.Value, mine.Level, mine.Lines[0].Flag));
			Assert.Equal("> 30.0", mine.Lines[0].Critical);
			Assert.Equal("not_found", (await w.Alerts.AcknowledgeAsync(Ctx(41), mine.Id, "32.4", Now)).Error);   // not his
			Assert.Equal("The value you typed is not the value sent. Read it again", (await w.Alerts.AcknowledgeAsync(Ctx(40), mine.Id, "3.24", Now)).Error);
			Assert.Null((await w.Host.Db.HospitalLabOrders.AsNoTracking().SingleAsync(o => o.ID == cbc.ID)).CriticalAcknowledgedAt);

			Assert.True((await w.Alerts.AcknowledgeAsync(Ctx(40), mine.Id, "32.40", Now.AddMinutes(2))).Ok);
			Assert.Empty(await w.Alerts.MineAsync(Ctx(40), Now, Local));
			var o = await w.Host.Db.HospitalLabOrders.AsNoTracking().SingleAsync(x => x.ID == cbc.ID);
			Assert.Equal(((int?)40, HospitalLabAlertService.ReadBackNotePrefix + "32.40"), (o.CriticalAcknowledgedByEmployeeId, o.CriticalNote));
			Assert.Equal(0, (await w.Lab.StatsAsync(Ctx(), Today)).OpenCriticals);
			Assert.Equal("The doctor already acknowledged this critical value", (await w.Alerts.SendAsync(Ctx(), HospitalAlertSource.Lab, cbc.ID, on, false, null, 7, Now)).Error);
		}

		[Fact]
		public async Task An_unacknowledged_alert_is_reminded_then_escalated_to_a_colleague_on_duty_who_can_close_it()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var (cbc, ordering, _, _, _) = await CriticalAsync(w);
			Assert.True((await w.Alerts.SendAsync(Ctx(), HospitalAlertSource.Lab, cbc.ID, ordering, false, null, 7, Now)).Ok);

			await w.Alerts.MaintainAsync(Ctx(), Now.AddMinutes(4), Local);
			var a = await host.Db.HospitalLabCriticalAlerts.AsNoTracking().SingleAsync();
			Assert.Equal(((DateTime?)null, (DateTime?)null), (a.RemindedAt, a.EscalatedAt));

			await w.Alerts.MaintainAsync(Ctx(), Now.AddMinutes(6), Local);
			Assert.NotNull((await host.Db.HospitalLabCriticalAlerts.AsNoTracking().SingleAsync()).RemindedAt);

			await w.Alerts.MaintainAsync(Ctx(), Now.AddMinutes(11), Local);
			await w.Alerts.MaintainAsync(Ctx(), Now.AddMinutes(12), Local);               // idempotent
			var all = await host.Db.HospitalLabCriticalAlerts.AsNoTracking().OrderBy(x => x.ID).ToListAsync();
			Assert.Equal(2, all.Count);
			Assert.Equal((HospitalLabAlertReason.Escalation, 41, (int?)all[0].ID), (all[1].Reason, all[1].ToEmployeeId, all[1].EscalatedFromAlertId));   // on duty beats off duty

			var esc = Assert.Single(await w.Alerts.MineAsync(Ctx(41), Now.AddMinutes(12), Local));
			Assert.True((await w.Alerts.AcknowledgeAsync(Ctx(41), esc.Id, "32.4", Now.AddMinutes(13))).Ok);
			Assert.All(await host.Db.HospitalLabCriticalAlerts.AsNoTracking().ToListAsync(), x => Assert.NotNull(x.AcknowledgedAt));   // both closed
			Assert.Empty(await w.Alerts.MineAsync(Ctx(40), Now.AddMinutes(13), Local));
		}

		[Fact]
		public async Task A_phone_call_recorded_with_the_alert_clears_the_flag_but_the_doctor_still_reads_back()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var (cbc, ordering, _, _, _) = await CriticalAsync(w);
			Assert.True((await w.Alerts.SendAsync(Ctx(), HospitalAlertSource.Lab, cbc.ID, ordering, false, "Dr Khaled by phone", 7, Now)).Ok);
			Assert.Equal("Dr Khaled by phone", (await host.Db.HospitalLabOrders.AsNoTracking().SingleAsync(o => o.ID == cbc.ID)).CriticalNote);
			Assert.Single(await w.Alerts.MineAsync(Ctx(40), Now, Local));                 // the call does not speak for him
		}

		[Fact]
		public async Task An_urgent_order_out_of_range_tells_its_doctor_once_and_a_routine_one_does_not()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var (_, cbc, crp) = await BenchAsync(w);
			var enc = await host.Db.HospitalEncounters.SingleAsync();
			var doc = await host.Db.HospitalPractitioners.SingleAsync(p => p.ID == enc.PractitionerId);
			doc.EmployeeId = 40; await host.Db.SaveChangesAsync();

			await ReceiveAsync(w, crp.ID);
			Assert.True((await w.Lab.SaveResultsAsync(Ctx(), crp.ID, await ValuesAsync(w, crp.ServiceId, "12.5"), null, 7, Now)).Ok);
			Assert.True((await w.Lab.VerifyAsync(Ctx(), crp.ID, 7, Now)).Ok);
			Assert.False(await w.Alerts.NotifyAbnormalAsync(Ctx(), HospitalAlertSource.Lab, crp.ID, 7, Now));         // routine

			Assert.True((await w.Lab.SetPriorityAsync(Ctx(), cbc.ID, HospitalLabPriority.Urgent)).Ok);
			await ReceiveAsync(w, cbc.ID);
			Assert.True((await w.Lab.SaveResultsAsync(Ctx(), cbc.ID, await ValuesAsync(w, cbc.ServiceId, "12.4", "4.5", "12.1", "37", "310", "86"), null, 7, Now)).Ok);
			Assert.False(await w.Alerts.NotifyAbnormalAsync(Ctx(), HospitalAlertSource.Lab, cbc.ID, 7, Now));          // not verified yet
			Assert.True((await w.Lab.VerifyAsync(Ctx(), cbc.ID, 7, Now)).Ok);
			Assert.True(await w.Alerts.NotifyAbnormalAsync(Ctx(), HospitalAlertSource.Lab, cbc.ID, 7, Now));
			Assert.False(await w.Alerts.NotifyAbnormalAsync(Ctx(), HospitalAlertSource.Lab, cbc.ID, 7, Now));          // once

			var n = Assert.Single(await w.Alerts.MineAsync(Ctx(40), Now, Local));
			Assert.Equal(HospitalLabAlertLevel.Abnormal, n.Level);
			Assert.True((await w.Alerts.AcknowledgeAsync(Ctx(40), n.Id, null, Now)).Ok);        // no read-back for a notice
			await w.Alerts.MaintainAsync(Ctx(), Now.AddMinutes(30), Local);
			Assert.Single(await host.Db.HospitalLabCriticalAlerts.ToListAsync());             // a notice is never escalated
		}

		[Fact]
		public async Task The_critical_value_leads_the_alert_even_when_a_milder_one_comes_first()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var (cbc, ordering, _, _, _) = await CriticalAsync(w);
			// RBC low (L) comes before haemoglobin critically low (LL) in the panel
			Assert.True((await w.Lab.SaveResultsAsync(Ctx(), cbc.ID, await ValuesAsync(w, cbc.ServiceId, "9.1", "2.60", "6.4", "21", "180", "62"), null, 7, Now)).Ok);
			Assert.True((await w.Alerts.SendAsync(Ctx(), HospitalAlertSource.Lab, cbc.ID, ordering, false, null, 7, Now)).Ok);
			var a = Assert.Single(await w.Alerts.MineAsync(Ctx(40), Now, Local));
			Assert.Equal(("6.4", "LL", "< 7.0"), (a.Value, a.Lines[0].Flag, a.Lines[0].Critical));
			Assert.Equal(new[] { "LL", "L", "L" }, a.Lines.Select(l => l.Flag).ToArray());
		}

		[Fact]
		public async Task Another_company_neither_sends_nor_sees_nor_acknowledges_an_alert()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var (cbc, ordering, _, _, _) = await CriticalAsync(w);
			Assert.Equal("not_found", (await w.Alerts.SendAsync(Ctx(9, CompanyTwo), HospitalAlertSource.Lab, cbc.ID, ordering, false, null, 9, Now)).Error);
			Assert.True((await w.Alerts.SendAsync(Ctx(), HospitalAlertSource.Lab, cbc.ID, ordering, false, null, 7, Now)).Ok);
			var id = (await host.Db.HospitalLabCriticalAlerts.SingleAsync()).ID;
			Assert.Empty(await w.Alerts.MineAsync(Ctx(40, CompanyTwo), Now, Local));
			Assert.Equal("not_found", (await w.Alerts.AcknowledgeAsync(Ctx(40, CompanyTwo), id, "32.4", Now)).Error);
			Assert.Empty(await w.Alerts.RecipientsAsync(Ctx(9, CompanyTwo), HospitalAlertSource.Lab, cbc.ID, Local));
		}
	}
}
