using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H237 — time-critical emergency pathways and ambulance pre-arrival. What must never happen: a step's clock
	// started anywhere but the door; a step done late shown as on time; a step set aside with no reason; a step recorded
	// twice; the same pathway opened twice on one visit; an ambulance received twice; a received ambulance whose
	// pre-alerted pathway does not start; a model naming a table its slice does not create.
	// ==========================================================================================
	public class HospitalErPathwayTests
	{
		private static readonly DateTime Door = new(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc);

		private static HospitalErPathwayStep Step(string code, int minutes, bool notIndicated = false) =>
			new() { Code = code, At = Door.AddMinutes(minutes), NotIndicated = notIndicated, Note = notIndicated ? "x" : null };

		[Fact]
		public void Every_pathway_has_its_steps_in_the_order_of_their_targets()
		{
			foreach (var k in HospitalErPathwayKind.All)
			{
				var steps = HospitalErPathwayRules.Steps[k];
				Assert.NotEmpty(steps);
				Assert.Equal(steps.Select(s => s.DueMinutes).OrderBy(x => x), steps.Select(s => s.DueMinutes));
				Assert.Equal(steps.Count, steps.Select(s => s.Code).Distinct().Count());
			}
			Assert.Equal(10, HospitalErPathwayRules.Steps[HospitalErPathwayKind.Stemi].Single(s => s.Code == "ECG").DueMinutes);
			Assert.Equal(90, HospitalErPathwayRules.Steps[HospitalErPathwayKind.Stemi].Single(s => s.Code == "BALLOON").DueMinutes);
			Assert.Equal(60, HospitalErPathwayRules.Steps[HospitalErPathwayKind.Sepsis].Single(s => s.Code == "ANTIBIOTICS").DueMinutes);
		}

		[Fact]
		public void Each_step_is_judged_against_the_door()
		{
			var done = new[] { Step("ECG", 8), Step("ASPIRIN", 22) };
			var s = HospitalErPathwayRules.Evaluate(HospitalErPathwayKind.Stemi, Door, done, Door.AddMinutes(40));
			Assert.Equal(HospitalErStepStatus.OnTime, s[0].State); Assert.Equal(8, s[0].Minutes);
			Assert.Equal(HospitalErStepStatus.Late, s[1].State); Assert.Equal(22, s[1].Minutes);
			Assert.Equal(HospitalErStepStatus.Overdue, s[2].State);   // cath lab alert due at 30
			Assert.Equal(HospitalErStepStatus.Due, s[3].State);       // balloon due at 90
			Assert.Equal(Door.AddMinutes(90), s[3].DueAtUtc);
			Assert.Equal(33m, HospitalErPathwayRules.Compliance(s));  // 1 of the 3 timed steps on time
		}

		[Fact]
		public void A_step_set_aside_leaves_the_compliance_and_needs_a_reason()
		{
			var s = HospitalErPathwayRules.Evaluate(HospitalErPathwayKind.Sepsis, Door,
				new[] { Step("LACTATE", 20), Step("CULTURES", 30), Step("ANTIBIOTICS", 45), Step("FLUIDS", 50, true) }, Door.AddHours(5));
			Assert.Equal(HospitalErStepStatus.NotIndicated, s[3].State); Assert.Null(s[3].Minutes);
			Assert.Equal(100m, HospitalErPathwayRules.Compliance(s));
			Assert.Equal("Write why it is not indicated", HospitalErPathwayRules.StepRefusal(HospitalErPathwayKind.Sepsis, "FLUIDS", true, " "));
			Assert.Null(HospitalErPathwayRules.StepRefusal(HospitalErPathwayKind.Sepsis, "FLUIDS", true, "Normotensive, lactate 1.4"));
			Assert.Equal("not_found", HospitalErPathwayRules.StepRefusal(HospitalErPathwayKind.Sepsis, "ECG", false, null));
			Assert.Equal("not_found", HospitalErPathwayRules.StepRefusal("Burns", "ECG", false, null));
			Assert.Null(HospitalErPathwayRules.Compliance(HospitalErPathwayRules.Evaluate(HospitalErPathwayKind.Stroke, Door, Array.Empty<HospitalErPathwayStep>(), Door)));
		}

		[Fact]
		public void A_pre_alert_needs_a_complaint_a_near_time_and_a_real_pathway()
		{
			var now = new DateTime(2026, 11, 10, 10, 0, 0);
			Assert.Null(HospitalErPathwayRules.PreArrivalRefusal(now.AddMinutes(12), now, "Chest pain", 2, HospitalErPathwayKind.Stemi));
			Assert.NotNull(HospitalErPathwayRules.PreArrivalRefusal(now.AddMinutes(12), now, " ", 2, null));
			Assert.NotNull(HospitalErPathwayRules.PreArrivalRefusal(now.AddHours(13), now, "Fall", null, null));
			Assert.NotNull(HospitalErPathwayRules.PreArrivalRefusal(now.AddHours(-3), now, "Fall", null, null));
			Assert.NotNull(HospitalErPathwayRules.PreArrivalRefusal(now, now, "Fall", 6, null));
			Assert.Equal("not_found", HospitalErPathwayRules.PreArrivalRefusal(now, now, "Fall", 3, "Burns"));
		}

		[Fact]
		public async Task An_ambulance_arrives_into_a_visit_with_its_pathway_running_and_each_step_is_kept_once()
		{
			using var host = new PlatformTestHost();
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			var ctx = new BusinessContext { CompanyId = 1, EmployeeId = 7, UserId = "u7", Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() };
			var now = DateTime.UtcNow;
			var svc = new HospitalErPathwayService(host.Db, access);
			var er = new HospitalEmergencyService(host.Db, access);

			var pre = await svc.PreAlertAsync(ctx, new HospitalPreArrivalInput { ExpectedAt = now.ToLocalTime().AddMinutes(10), Complaint = "Right-sided weakness 40 min", AgeText = "67", Sex = "M", EsiEstimate = 2, Pathway = HospitalErPathwayKind.Stroke, Unit = "A-12" }, 7, now);
			Assert.True(pre.Ok, pre.Error);
			var board = await svc.BoardAsync(ctx, now);
			Assert.Single(board.Incoming); Assert.Empty(board.Active);

			var arrived = await svc.ReceiveAsync(ctx, er, pre.Id!.Value, null, 7, now.AddMinutes(9));
			Assert.True(arrived.Ok, arrived.Error);
			var visit = await host.Db.Set<HospitalErVisit>().AsNoTracking().SingleAsync(v => v.ID == arrived.Id);
			Assert.Equal(HospitalErArrival.Ambulance, visit.ArrivalMode);
			Assert.Equal("Right-sided weakness 40 min", visit.Complaint);
			Assert.Equal(HospitalPreArrivalStatus.Arrived, (await host.Db.Set<HospitalErPreArrival>().AsNoTracking().SingleAsync()).Status);
			Assert.False((await svc.ReceiveAsync(ctx, er, pre.Id!.Value, null, 7, now.AddMinutes(9))).Ok);   // never twice

			board = await svc.BoardAsync(ctx, now.AddMinutes(30));
			Assert.Empty(board.Incoming);
			var path = Assert.Single(board.Active);
			Assert.Equal(HospitalErPathwayKind.Stroke, path.Pathway.Kind);
			Assert.Equal(visit.ArrivedAt.AddMinutes(25), path.Steps.Single(s => s.Def.Code == "CT").DueAtUtc);   // from the real door

			Assert.True((await svc.StepAsync(ctx, path.Pathway.ID, "GLUCOSE", false, null, 7, visit.ArrivedAt.AddMinutes(5))).Ok);
			Assert.False((await svc.StepAsync(ctx, path.Pathway.ID, "GLUCOSE", false, null, 7, visit.ArrivedAt.AddMinutes(6))).Ok);
			Assert.False((await svc.StepAsync(ctx, path.Pathway.ID, "DECISION", true, "", 7, visit.ArrivedAt.AddMinutes(6))).Ok);
			Assert.False((await svc.ActivateAsync(ctx, visit.ID, HospitalErPathwayKind.Stroke, 7, now)).Ok);
			Assert.True((await svc.ActivateAsync(ctx, visit.ID, HospitalErPathwayKind.Trauma, 7, now)).Ok);

			Assert.True((await svc.CloseAsync(ctx, path.Pathway.ID, null, now.AddHours(1))).Ok);
			Assert.False((await svc.StepAsync(ctx, path.Pathway.ID, "CT", false, null, 7, now.AddHours(1))).Ok);
			Assert.Equal(HospitalErPathwayKind.Trauma, Assert.Single((await svc.BoardAsync(ctx, now.AddHours(1))).Active).Pathway.Kind);

			var other = await svc.PreAlertAsync(ctx, new HospitalPreArrivalInput { ExpectedAt = now.ToLocalTime().AddMinutes(20), Complaint = "Fall" }, 7, now);
			Assert.True((await svc.CancelPreAlertAsync(ctx, other.Id!.Value)).Ok);
			Assert.False((await svc.ReceiveAsync(ctx, er, other.Id!.Value, null, 7, now)).Ok);
		}

		// ------------------------------------------------------------------ the model against its slice, no database

		private static string Slice()
		{
			for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
			{
				var f = Path.Combine(d.FullName, "CrossBuy", "deploy", "sql", "hospital_h237_er_pathways.sql");
				if (File.Exists(f)) return File.ReadAllText(f);
			}
			throw new FileNotFoundException("hospital_h237_er_pathways.sql not found above " + AppContext.BaseDirectory);
		}

		[Theory]
		[InlineData(typeof(HospitalErPathway), "HospitalErPathways")]
		[InlineData(typeof(HospitalErPathwayStep), "HospitalErPathwaySteps")]
		[InlineData(typeof(HospitalErPreArrival), "HospitalErPreArrivals")]
		public void The_model_names_the_tables_its_slice_creates(Type entity, string table)
		{
			var mb = new ModelBuilder(SqlServerConventionSetBuilder.Build());
			HospitalEmergencyModel.Configure(mb);
			var t = mb.FinalizeModel().FindEntityType(entity)!;
			Assert.Equal(table, t.GetTableName());
			var sql = Slice();
			Assert.Contains($"CREATE TABLE dbo.{table} (", sql.Split('\n').Select(l => l.Trim()));
			foreach (var p in t.GetProperties()) Assert.Contains($"        {p.GetColumnName()} ", sql);
		}
	}
}
