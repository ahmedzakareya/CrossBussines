using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H158: the desk's handoffs. What must never happen:
	//   · a person sent on against a request that is not theirs, or sent twice for the same request;
	//   · an urgent call held up because the patient is not identified yet;
	//   · a visitor told where a confidential patient is;
	//   · the desk "accepting" for the receiving team;
	//   · a handoff nobody took left looking fine (it must turn overdue, and stop being so once taken).
	// ==========================================================================================
	public class HospitalDeskHandoffTests
	{
		private static readonly DateTime Today = new(2026, 11, 17);
		private static readonly DateTime Now = new(2026, 11, 17, 8, 0, 0, DateTimeKind.Utc);
		private static BusinessContext Ctx(int employeeId = 7, int companyId = 1) => new() { CompanyId = companyId, EmployeeId = employeeId, UserId = "u" + employeeId, Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() };

		private static HospitalAccessService Access(PlatformTestHost host) =>
			new(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);

		private static async Task<(PlatformTestHost Host, HospitalDeskHandoffService Svc, int Patient, int Other)> World()
		{
			var host = new PlatformTestHost();
			var patients = new HospitalPatientService(host.Db, Access(host));
			var p = (await patients.SaveAsync(Ctx(), new HospitalPatientInput { Name = "م عمر", NameEn = "Omar", Sex = "M" }, 7, Now)).Id!.Value;
			var o = (await patients.SaveAsync(Ctx(), new HospitalPatientInput { Name = "م سمر", NameEn = "Samar", Sex = "F" }, 7, Now)).Id!.Value;
			return (host, new HospitalDeskHandoffService(host.Db, Access(host)), p, o);
		}

		private static HospitalLabOrder Lab(int patient, string status = HospitalLabStatus.Ordered) => new()
		{ CompanyId = 1, PatientId = patient, AccessionNo = "L-" + Guid.NewGuid().ToString("N")[..6], Code = "CBC", Name = "صورة دم", NameEn = "Blood count", Status = status, OrderedAt = Now };

		[Theory]
		[InlineData(HospitalArrivalReason.Lab, null, null, "Choose the request they came for")]
		[InlineData(HospitalArrivalReason.Referral, null, null, "Write who referred the patient")]
		[InlineData(HospitalArrivalReason.Visitor, "Hana", null, "Write the visitor's name and relation to the patient")]
		[InlineData("Shopping", null, null, "Choose why they came")]
		[InlineData(HospitalArrivalReason.Admin, null, null, null)]
		public void A_handoff_names_why_and_what_for(string reason, string? visitor, string? relation, string? refusal)
			=> Assert.Equal(refusal, HospitalHandoffRules.Refuse(new HospitalHandoffInput { PatientId = 1, Reason = reason, VisitorName = visitor, VisitorRelation = relation }));

		[Fact]
		public void Only_an_urgent_call_goes_without_a_patient_and_it_turns_overdue_first()
		{
			Assert.Null(HospitalHandoffRules.Refuse(new HospitalHandoffInput { Reason = HospitalArrivalReason.Urgent }));
			Assert.Equal("Open the patient's file first", HospitalHandoffRules.Refuse(new HospitalHandoffInput { Reason = HospitalArrivalReason.Clinic }));
			var urgent = new HospitalDeskHandoff { Reason = HospitalArrivalReason.Urgent, Status = HospitalHandoffStatus.Sent, SentAt = Now };
			var clinic = new HospitalDeskHandoff { Reason = HospitalArrivalReason.Clinic, Status = HospitalHandoffStatus.Sent, SentAt = Now };
			Assert.True(HospitalHandoffRules.Overdue(urgent, Now.AddMinutes(3)));
			Assert.False(HospitalHandoffRules.Overdue(clinic, Now.AddMinutes(3)));
			Assert.True(HospitalHandoffRules.Overdue(clinic, Now.AddMinutes(11)));
			clinic.Status = HospitalHandoffStatus.Accepted; clinic.AcceptedAt = Now.AddMinutes(11);
			Assert.False(HospitalHandoffRules.Overdue(clinic, Now.AddMinutes(12)));   // taken: no longer the desk's worry
			Assert.True(HospitalHandoffRules.Overdue(clinic, Now.AddMinutes(45)));    // …until they never arrive
			clinic.Status = HospitalHandoffStatus.Arrived;
			Assert.False(HospitalHandoffRules.Overdue(clinic, Now.AddDays(1)));
		}

		[Fact]
		public async Task The_desk_sends_against_what_is_waiting_for_this_patient_once()
		{
			var (host, svc, p, other) = await World();
			using var _h = host;
			var mine = Lab(p); var theirs = Lab(other); var done = Lab(p, HospitalLabStatus.Verified);
			host.Db.AddRange(mine, theirs, done); await host.Db.SaveChangesAsync();

			var opts = await svc.OptionsAsync(Ctx(), p, Today);
			var opt = Assert.Single(opts);   // neither the other patient's order nor a finished one
			Assert.Equal((HospitalArrivalReason.Lab, mine.ID, HospitalHandoffDestination.Lab), (opt.Reason, opt.LinkId, opt.Destination));

			HospitalHandoffInput Send(int link) => new() { PatientId = p, Reason = HospitalArrivalReason.Lab, LinkKind = HospitalHandoffLink.LabOrder, LinkId = link };
			Assert.Equal("This request is not open for the patient", (await svc.SendAsync(Ctx(), Send(theirs.ID), 7, Now, Today)).Result.Error);
			var (r, sent) = await svc.SendAsync(Ctx(), Send(mine.ID), 7, Now, Today);
			Assert.True(r.Ok, r.Error);
			Assert.Equal("The patient was already sent for this request", (await svc.SendAsync(Ctx(), Send(mine.ID), 7, Now, Today)).Result.Error);

			var row = await host.Db.Set<HospitalDeskHandoff>().FindAsync(sent!.Id);
			Assert.Equal((HospitalHandoffStatus.Sent, HospitalHandoffDestination.Lab, "Blood count", "HO-000001"), (row!.Status, row.Destination, row.DetailEn, row.HandoffNo));
			Assert.Equal((1, 0), await svc.CountsAsync(Ctx(), Now.AddMinutes(5)));
			Assert.Equal((1, 1), await svc.CountsAsync(Ctx(), Now.AddMinutes(15)));
			Assert.Equal((0, 0), await svc.CountsAsync(Ctx(companyId: 2), Now.AddMinutes(15)));   // another company sees none
		}

		[Fact]
		public async Task An_urgent_call_needs_no_patient_and_the_team_takes_it_or_says_why_not()
		{
			var (host, svc, _, _) = await World();
			using var _h = host;
			var (r, _) = await svc.SendAsync(Ctx(), new HospitalHandoffInput { Reason = HospitalArrivalReason.Urgent, Note = "fainted at the door" }, 7, Now, Today);
			Assert.True(r.Ok, r.Error);
			var id = r.Id!.Value;
			Assert.Equal(HospitalHandoffDestination.Emergency, (await host.Db.Set<HospitalDeskHandoff>().FindAsync(id))!.Destination);

			Assert.Equal("Write why the team cannot take the patient", (await svc.FailAsync(Ctx(9), id, " ", 9, Now)).Error);
			Assert.True((await svc.AcceptAsync(Ctx(9), id, 9, Now.AddMinutes(1))).Ok);
			Assert.Equal("This handoff has moved on already", (await svc.AcceptAsync(Ctx(9), id, 9, Now.AddMinutes(1))).Error);
			Assert.True((await svc.ArrivedAsync(Ctx(9), id, 9, Now.AddMinutes(3))).Ok);
			Assert.Equal("This handoff has moved on already", (await svc.RemindAsync(Ctx(), id, Now.AddMinutes(4))).Error);   // nothing left to chase
			var (rows, total) = await svc.ListAsync(Ctx(), null, null, false, 1, 10, Now.AddMinutes(5));
			Assert.Equal(1, total);
			Assert.Equal((HospitalHandoffStatus.Arrived, 9), (rows[0].H.Status, rows[0].H.ArrivedByEmployeeId!.Value));
			Assert.Empty((await svc.ListAsync(Ctx(), null, null, true, 1, 10, Now.AddMinutes(5))).Rows);   // open only: it is done
		}

		[Fact]
		public async Task Reception_sends_and_chases_but_only_the_receiving_team_takes_the_person()
		{
			var (host, _, p, _) = await World();
			using var _h = host;
			host.Seed.PlatformRoleAssignments.Add(new PlatformRoleAssignment
			{ CompanyID = 1, Scope = EntityRegistry.ScopeHospital, PrincipalType = PlatformPrincipalTypes.Employee, PrincipalId = 20, Role = HospitalRoles.HospitalReception, IsActive = true, CreatedAt = DateTime.UtcNow });
			host.Seed.PlatformRoleAssignments.Add(new PlatformRoleAssignment
			{ CompanyID = 1, Scope = EntityRegistry.ScopeHospital, PrincipalType = PlatformPrincipalTypes.Employee, PrincipalId = 21, Role = HospitalRoles.HospitalStaff, IsActive = true, CreatedAt = DateTime.UtcNow });
			await host.Seed.SaveChangesAsync();
			var svc = new HospitalDeskHandoffService(host.Db, Access(host));

			var (r, _) = await svc.SendAsync(Ctx(20), new HospitalHandoffInput { PatientId = p, Reason = HospitalArrivalReason.Admin }, 20, Now, Today);
			Assert.True(r.Ok, r.Error);
			var id = r.Id!.Value;
			Assert.True((await svc.RemindAsync(Ctx(20), id, Now.AddMinutes(12))).Ok);
			Assert.Equal("forbidden", (await svc.AcceptAsync(Ctx(20), id, 20, Now.AddMinutes(13))).Error);
			Assert.True((await svc.AcceptAsync(Ctx(21), id, 21, Now.AddMinutes(13))).Ok);
			var row = await host.Db.Set<HospitalDeskHandoff>().FindAsync(id);
			Assert.Equal((1, 21), (row!.Reminders, row.AcceptedByEmployeeId!.Value));
		}

		[Fact]
		public async Task A_visitor_is_told_the_place_unless_the_file_is_confidential_and_the_inquiry_is_recorded()
		{
			var (host, svc, p, other) = await World();
			using var _h = host;
			host.Db.Add(new HospitalRestrictedFile { CompanyId = 1, PatientId = other, Reason = "VIP", RestrictedAt = Now, RestrictedByEmployeeId = 1 });
			await host.Db.SaveChangesAsync();
			HospitalHandoffInput Ask(int who) => new() { PatientId = who, Reason = HospitalArrivalReason.Visitor, VisitorName = "Hana", VisitorRelation = "Sister" };

			var (open, told) = await svc.SendAsync(Ctx(), Ask(p), 7, Now, Today);
			Assert.True(open.Ok, open.Error);
			Assert.False(told!.Confidential);
			Assert.NotNull(told.Where);   // not inside today: an empty answer, never a guess

			var (shut, secret) = await svc.SendAsync(Ctx(), Ask(other), 7, Now, Today);
			Assert.True(shut.Ok, shut.Error);
			Assert.True(secret!.Confidential);
			Assert.Null(secret.Where);
			var rows = host.Db.Set<HospitalDeskHandoff>().Where(h => h.Reason == HospitalArrivalReason.Visitor).ToList();
			Assert.Equal(2, rows.Count);
			Assert.All(rows, h => Assert.Equal((HospitalHandoffStatus.Closed, "Hana", "Sister"), (h.Status, h.VisitorName, h.VisitorRelation)));   // recorded, nothing to accept
			Assert.Equal((0, 0), await svc.CountsAsync(Ctx(), Now.AddHours(1)));
		}
	}
}
