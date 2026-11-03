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
	// HOSPITAL H9 — radiology.
	//
	// What must never happen: a signed imaging order that never reaches radiology (or reaches it twice), a
	// patient put in a room of another modality or in one under maintenance, two patients scanned in one
	// room at once, a report written before the scan ended, a report signed without findings, impression or
	// category, a signed report changed, an unsigned report in the patient file, a contrast allergy left
	// unflagged, a critical report whose doctor is never told — or another company's order on the floor.
	// ==========================================================================================
	public class HospitalRadiologyTests
	{
		private const int CompanyOne = 1, CompanyTwo = 2;
		private static readonly DateTime Today = new(2026, 11, 15);   // Sunday
		private static readonly DateTime Now = new(2026, 11, 15, 9, 0, 0, DateTimeKind.Utc);
		private static readonly DateTime Local = new(2026, 11, 15, 10, 0, 0);

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
			public required HospitalRadiologyService Rad;
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
			return new World { Host = host, Rad = new(host.Db, access), Alerts = new(host.Db, access, new HospitalLabService(host.Db, access)), Billing = billing,
				Consult = new(host.Db, access, billing), Op = new(host.Db, access), Patients = new(host.Db, access) };
		}

		private static async Task<int> ServiceAsync(World w, string code, string category = HospitalServiceCategories.Radiology, int companyId = CompanyOne)
		{
			var r = await w.Billing.SaveServiceAsync(Ctx(7, companyId), new HospitalServiceInput { Code = code, Name = "خ " + code, NameEn = "S " + code, Category = category, Price = 10m }, 7);
			Assert.True(r.Ok, r.Error);
			return r.Id!.Value;
		}

		/// A visit signed with the given orders for a new patient (allergies optional). Returns (patient, doctor).
		private static async Task<(int Patient, int Doctor)> SignedVisitAsync(World w, string tag, string? allergies, params int[] orders)
		{
			var ctx = Ctx();
			Assert.True((await w.Consult.SeedDefaultCodesAsync(ctx)).Ok);
			var fee = await w.Host.Db.HospitalServices.Where(s => s.Code == "CONS-IM").Select(s => (int?)s.ID).FirstOrDefaultAsync() ?? await ServiceAsync(w, "CONS-IM", HospitalServiceCategories.Consultation);
			var doc = (await w.Op.SavePractitionerAsync(ctx, new HospitalPractitionerInput { Name = "د " + tag, NameEn = "Dr " + tag, WorkDays = new() { 0 }, SessionStartMinute = 540, SessionEndMinute = 600, ConsultationServiceId = fee }, 7)).Id!.Value;
			var saved = await w.Patients.SaveAsync(ctx, new HospitalPatientInput { Name = "م " + tag, NameEn = "P " + tag, Sex = "M", Allergies = allergies }, 7, Now);
			Assert.True(saved.Ok, saved.Error);
			var appt = (await w.Op.BookAsync(ctx, new HospitalBookingInput { PatientId = saved.Id!.Value, PractitionerId = doc, Date = Today, StartMinute = 540 }, 7, Today)).Id!.Value;
			Assert.True((await w.Op.MoveAsync(ctx, appt, HospitalAppointmentStatus.Arrived, 7, Now)).Ok);
			var enc = (await w.Consult.OpenAsync(ctx, appt, 7, Now)).Id!.Value;
			var signed = await w.Consult.SignAsync(ctx, enc, new HospitalEncounterInput { ChiefComplaint = "Pain", Diagnoses = new() { new() { Code = "J02.9", Primary = true } }, OrderServiceIds = orders.ToList() }, 7, Now);
			Assert.True(signed.Ok, signed.Error);
			return (saved.Id.Value, doc);
		}

		/// RAD-CXR, RAD-CT (contrast) and LAB-CBC; the standard rooms and exam setup; one visit ordering all three.
		private static async Task<(int Patient, int Doctor, HospitalRadOrder Cxr, HospitalRadOrder Ct)> FloorAsync(World w, string? allergies = null)
		{
			await ServiceAsync(w, "RAD-CXR"); await ServiceAsync(w, "RAD-CT");
			var cbc = await ServiceAsync(w, "LAB-CBC", HospitalServiceCategories.Lab);
			Assert.True((await w.Rad.SeedStandardAsync(Ctx())).Ok);
			var ids = await w.Host.Db.HospitalServices.Where(s => s.Category == HospitalServiceCategories.Radiology).Select(s => s.ID).ToListAsync();
			var (patient, doctor) = await SignedVisitAsync(w, "1", allergies, ids.Append(cbc).ToArray());
			Assert.Equal(2, await w.Rad.SyncOrdersAsync(Ctx()));
			var orders = await w.Host.Db.HospitalRadOrders.AsNoTracking().ToListAsync();
			return (patient, doctor, orders.Single(o => o.Code == "RAD-CXR"), orders.Single(o => o.Code == "RAD-CT"));
		}

		private static int RoomId(World w, string code) => w.Host.Db.HospitalRadRooms.Single(m => m.Code == code).ID;

		[Theory]
		[InlineData(true, "Iodinated contrast — hives", true)]
		[InlineData(true, "حساسية من الصبغة", true)]
		[InlineData(true, "Penicillin", false)]
		[InlineData(false, "Iodine", false)]
		[InlineData(true, null, false)]
		public void A_contrast_exam_is_flagged_only_against_a_contrast_allergy(bool contrast, string? allergies, bool flagged)
			=> Assert.Equal(flagged, HospitalRadiologyService.ContrastAlert(contrast, allergies));

		[Theory]
		[InlineData(true, true, 3, "Maintenance")]
		[InlineData(false, true, 2, "Busy")]
		[InlineData(false, false, 1, "Ready")]
		[InlineData(false, false, 0, "Free")]
		public void A_room_shows_maintenance_then_scanning_then_a_waiting_patient(bool maint, bool scanning, int queue, string state)
			=> Assert.Equal(state, HospitalRadiologyService.RoomState(maint, scanning, queue));

		[Fact]
		public async Task Each_signed_imaging_line_becomes_one_order_of_its_modality_once()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var (_, _, cxr, ct) = await FloorAsync(w);
			Assert.Equal(new[] { "RAD-000001", "RAD-000002" }, new[] { cxr.AccessionNo, ct.AccessionNo }.OrderBy(x => x).ToArray());
			Assert.Equal((HospitalRadModality.XRay, false), (cxr.Modality, cxr.Contrast));
			Assert.Equal((HospitalRadModality.CT, true), (ct.Modality, ct.Contrast));
			Assert.Equal(0, await w.Rad.SyncOrdersAsync(Ctx()));
			Assert.DoesNotContain(await host.Db.HospitalRadOrders.ToListAsync(), o => o.Code == "LAB-CBC");
		}

		[Fact]
		public async Task Arrival_gives_a_working_room_of_the_modality_and_a_room_scans_one_patient_at_a_time()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var (_, _, cxr, ct) = await FloorAsync(w);
			Assert.Equal("Choose a working room of this exam's modality", (await w.Rad.ArriveAsync(Ctx(), cxr.ID, RoomId(w, "CT-1"), 7, Now)).Error);
			Assert.True((await w.Rad.SetMaintenanceAsync(Ctx(), RoomId(w, "CT-1"), true, "Tube replacement")).Ok);
			Assert.Equal("No working room does this modality. Add one in radiology setup, or end its maintenance", (await w.Rad.ArriveAsync(Ctx(), ct.ID, null, 7, Now)).Error);
			Assert.True((await w.Rad.SetMaintenanceAsync(Ctx(), RoomId(w, "CT-1"), false, null)).Ok);

			Assert.Equal("Mark the patient arrived before scanning", (await w.Rad.StartScanAsync(Ctx(), cxr.ID, 7, Now)).Error);
			Assert.True((await w.Rad.ArriveAsync(Ctx(), cxr.ID, null, 7, Now)).Ok);
			Assert.Equal(RoomId(w, "XR-1"), (await host.Db.HospitalRadOrders.AsNoTracking().SingleAsync(o => o.ID == cxr.ID)).RadRoomId);
			Assert.True((await w.Rad.StartScanAsync(Ctx(), cxr.ID, 7, Now)).Ok);
			Assert.Equal("A patient is being scanned in this room", (await w.Rad.SetMaintenanceAsync(Ctx(), RoomId(w, "XR-1"), true, null)).Error);

			// a second chest X-ray in the same room waits until the first scan ends
			await SignedVisitAsync(w, "2", null, host.Db.HospitalServices.Single(s => s.Code == "RAD-CXR").ID);
			await w.Rad.SyncOrdersAsync(Ctx());
			var second = await host.Db.HospitalRadOrders.AsNoTracking().SingleAsync(o => o.AccessionNo == "RAD-000003");
			Assert.True((await w.Rad.ArriveAsync(Ctx(), second.ID, RoomId(w, "XR-1"), 7, Now)).Ok);
			Assert.Equal("The room is scanning another patient", (await w.Rad.StartScanAsync(Ctx(), second.ID, 7, Now)).Error);
			Assert.True((await w.Rad.EndScanAsync(Ctx(), cxr.ID, 7, Now)).Ok);
			Assert.True((await w.Rad.StartScanAsync(Ctx(), second.ID, 7, Now)).Ok);
		}

		[Fact]
		public async Task A_report_is_written_after_the_scan_signed_complete_and_then_locked_and_in_the_file()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var (patient, _, cxr, _) = await FloorAsync(w);
			var input = new HospitalRadReportInput { Findings = "Clear lungs.", Impression = "Normal chest.", Category = HospitalRadImpression.Normal };
			Assert.Equal("End the scan before writing the report", (await w.Rad.SaveReportAsync(Ctx(), cxr.ID, input, 7, Now)).Error);
			Assert.True((await w.Rad.ArriveAsync(Ctx(), cxr.ID, null, 7, Now)).Ok);
			Assert.True((await w.Rad.StartScanAsync(Ctx(), cxr.ID, 7, Now)).Ok);
			Assert.True((await w.Rad.EndScanAsync(Ctx(), cxr.ID, 7, Now)).Ok);
			Assert.StartsWith("Exam: chest X-ray.", (await host.Db.HospitalRadOrders.AsNoTracking().SingleAsync(o => o.ID == cxr.ID)).Findings);   // the template

			Assert.Equal("Write the findings and the impression before signing", (await w.Rad.SignAsync(Ctx(), cxr.ID, new HospitalRadReportInput { Findings = "x", Category = HospitalRadImpression.Normal }, 7, Now)).Error);
			Assert.Equal("Choose whether the result is normal, abnormal or critical", (await w.Rad.SignAsync(Ctx(), cxr.ID, new HospitalRadReportInput { Findings = "x", Impression = "y" }, 7, Now)).Error);
			Assert.Equal("Unknown impression category", (await w.Rad.SaveReportAsync(Ctx(), cxr.ID, new HospitalRadReportInput { Category = "Maybe" }, 7, Now)).Error);
			Assert.Equal(0, (await w.Rad.HistoryAsync(Ctx(), patient, false)).Signed.Count);   // not signed, not in the file

			Assert.True((await w.Rad.SignAsync(Ctx(), cxr.ID, input, 7, Now)).Ok);
			var h = await w.Rad.HistoryAsync(Ctx(), patient, false);
			Assert.Equal(("Normal chest.", 1), (Assert.Single(h.Signed).Impression, h.Pending));
			Assert.Equal("This report is signed and locked", (await w.Rad.SaveReportAsync(Ctx(), cxr.ID, input, 7, Now)).Error);
			Assert.Equal("This report is signed and locked", (await w.Rad.SetPriorityAsync(Ctx(), cxr.ID, HospitalLabPriority.Stat)).Error);
		}

		[Fact]
		public async Task The_radiographer_scans_and_only_the_radiologist_signs()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var (_, _, cxr, _) = await FloorAsync(w);
			foreach (var (emp, role) in new[] { (1, HospitalRoles.HospitalAdministrator), (30, HospitalRoles.HospitalRadiographer), (31, HospitalRoles.HospitalRadiologist), (3, HospitalRoles.HospitalDoctor) })
				host.Seed.PlatformRoleAssignments.Add(new PlatformRoleAssignment { CompanyID = CompanyOne, Scope = EntityRegistry.ScopeHospital, PrincipalType = PlatformPrincipalTypes.Employee, PrincipalId = emp, Role = role, IsActive = true, CreatedAt = DateTime.UtcNow });
			await host.Seed.SaveChangesAsync();

			Assert.Equal("forbidden", (await w.Rad.ArriveAsync(Ctx(3), cxr.ID, null, 3, Now)).Error);
			Assert.True((await w.Rad.ArriveAsync(Ctx(30), cxr.ID, null, 30, Now)).Ok);
			Assert.True((await w.Rad.StartScanAsync(Ctx(30), cxr.ID, 30, Now)).Ok);
			Assert.True((await w.Rad.EndScanAsync(Ctx(30), cxr.ID, 30, Now)).Ok);
			var input = new HospitalRadReportInput { Findings = "f", Impression = "i", Category = HospitalRadImpression.Normal };
			Assert.Equal("forbidden", (await w.Rad.SignAsync(Ctx(30), cxr.ID, input, 30, Now)).Error);
			Assert.True((await w.Rad.SignAsync(Ctx(31), cxr.ID, input, 31, Now)).Ok);
		}

		[Fact]
		public async Task A_critical_report_flags_the_order_and_reaches_the_doctor_without_a_number_to_read_back()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var (_, doctor, _, ct) = await FloorAsync(w, "Iodine contrast");
			var doc = await host.Db.HospitalPractitioners.SingleAsync(p => p.ID == doctor);
			doc.EmployeeId = 40; await host.Db.SaveChangesAsync();
			Assert.True(Assert.Single(await w.Rad.WorklistAsync(Ctx(), HospitalRadStatus.Scheduled), r => r.Id == ct.ID).ContrastAlert);

			Assert.True((await w.Rad.ArriveAsync(Ctx(), ct.ID, null, 7, Now)).Ok);
			Assert.True((await w.Rad.StartScanAsync(Ctx(), ct.ID, 7, Now)).Ok);
			Assert.True((await w.Rad.EndScanAsync(Ctx(), ct.ID, 7, Now)).Ok);
			Assert.Equal("This order has no critical value", (await w.Alerts.SendAsync(Ctx(), HospitalAlertSource.Radiology, ct.ID, doctor, false, null, 7, Now)).Error);
			Assert.True((await w.Rad.SignAsync(Ctx(), ct.ID, new HospitalRadReportInput { Findings = "Free air under the diaphragm.", Impression = "Bowel perforation.", Category = HospitalRadImpression.Critical }, 7, Now)).Ok);
			Assert.Equal(1, (await w.Rad.StatsAsync(Ctx(), Today)).OpenCriticals);

			var rec = await w.Alerts.RecipientsAsync(Ctx(), HospitalAlertSource.Radiology, ct.ID, Local);
			Assert.Equal((doctor, HospitalLabAlertReason.Ordering), (rec[0].Practitioner.ID, rec[0].Why));
			Assert.True((await w.Alerts.SendAsync(Ctx(), HospitalAlertSource.Radiology, ct.ID, doctor, false, null, 7, Now)).Ok);
			var a = Assert.Single(await w.Alerts.MineAsync(Ctx(40), Now, Local));
			Assert.Equal((HospitalAlertSource.Radiology, (string?)null, "Bowel perforation."), (a.Source, a.Value, a.Impression));
			Assert.True((await w.Alerts.AcknowledgeAsync(Ctx(40), a.Id, null, Now.AddMinutes(1))).Ok);   // nothing to read back
			var o = await host.Db.HospitalRadOrders.AsNoTracking().SingleAsync(x => x.ID == ct.ID);
			Assert.Equal(((int?)40, HospitalLabAlertService.ReadBackNotePrefix), (o.CriticalAcknowledgedByEmployeeId, o.CriticalNote));
			Assert.Equal(0, (await w.Rad.StatsAsync(Ctx(), Today)).OpenCriticals);
			Assert.Empty(await host.Db.HospitalLabCriticalAlerts.Where(x => x.LabOrderId != null).ToListAsync());
		}

		[Fact]
		public async Task An_urgent_abnormal_report_tells_its_doctor_once_and_a_routine_one_does_not()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var (_, doctor, cxr, ct) = await FloorAsync(w);
			var doc = await host.Db.HospitalPractitioners.SingleAsync(p => p.ID == doctor);
			doc.EmployeeId = 40; await host.Db.SaveChangesAsync();
			async Task Sign(int id) { await w.Rad.ArriveAsync(Ctx(), id, null, 7, Now); await w.Rad.StartScanAsync(Ctx(), id, 7, Now); await w.Rad.EndScanAsync(Ctx(), id, 7, Now);
				Assert.True((await w.Rad.SignAsync(Ctx(), id, new HospitalRadReportInput { Findings = "f", Impression = "Small effusion.", Category = HospitalRadImpression.Abnormal }, 7, Now)).Ok); }
			await Sign(cxr.ID);
			Assert.False(await w.Alerts.NotifyAbnormalAsync(Ctx(), HospitalAlertSource.Radiology, cxr.ID, 7, Now));   // routine
			Assert.True((await w.Rad.SetPriorityAsync(Ctx(), ct.ID, HospitalLabPriority.Urgent)).Ok);
			await Sign(ct.ID);
			Assert.True(await w.Alerts.NotifyAbnormalAsync(Ctx(), HospitalAlertSource.Radiology, ct.ID, 7, Now));
			Assert.False(await w.Alerts.NotifyAbnormalAsync(Ctx(), HospitalAlertSource.Radiology, ct.ID, 7, Now));
			Assert.Equal(HospitalLabAlertLevel.Abnormal, Assert.Single(await w.Alerts.MineAsync(Ctx(40), Now, Local)).Level);
		}

		[Fact]
		public async Task Another_company_neither_sees_nor_works_an_imaging_order()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var (patient, _, cxr, _) = await FloorAsync(w);
			var them = Ctx(9, CompanyTwo);
			Assert.Equal(0, await w.Rad.SyncOrdersAsync(them));
			Assert.Empty(await w.Rad.WorklistAsync(them, null));
			Assert.Null(await w.Rad.OrderAsync(them, cxr.ID));
			Assert.Equal("not_found", (await w.Rad.ArriveAsync(them, cxr.ID, null, 9, Now)).Error);
			Assert.Equal("not_found", (await w.Rad.SetMaintenanceAsync(them, RoomId(w, "XR-1"), true, null)).Error);
			Assert.Empty((await w.Rad.HistoryAsync(them, patient, false)).Signed);
			Assert.Null(await w.Alerts.SubjectAsync(them, HospitalAlertSource.Radiology, cxr.ID));
		}
	}
}
