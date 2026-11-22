using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H230 — microbiology. What must never happen: a culture resulted through the analyte grid; a second-line
	// antibiotic shown when every first-line one works (cascade); an MRSA / VRE / ESBL / CRE missed from the results;
	// a culture completed with growth and no organism or no susceptibility; a positive blood culture that is not
	// critical; a verified culture changed; a resistant organism on an admitted patient that infection control never
	// hears of (or hears of twice).
	// ==========================================================================================
	public class HospitalMicroTests
	{
		private static readonly DateTime Today = new(2026, 11, 15);
		private static readonly DateTime Now = new(2026, 11, 15, 9, 0, 0, DateTimeKind.Utc);
		private static BusinessContext Ctx(int emp = 7) => new() { CompanyId = 1, EmployeeId = emp, UserId = "u" + emp, Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() };
		private static HospitalMicroAstLine L(int id, string code, string result, int tier = 1) => new(id, code, code, result, null);

		[Fact]
		public void Second_line_agents_are_reported_only_when_a_first_line_one_is_resisted()
		{
			var panel = new[] { new HospitalMicroPanelLine(1, "AMP", "Ampicillin", 1, 1), new HospitalMicroPanelLine(2, "CRO", "Ceftriaxone", 1, 2), new HospitalMicroPanelLine(3, "MEM", "Meropenem", 2, 3) };
			Assert.Equal(new[] { 1, 2 }, HospitalMicroRules.Reported(panel, new[] { L(1, "AMP", "S"), L(2, "CRO", "S"), L(3, "MEM", "S") }).OrderBy(x => x));
			Assert.Equal(new[] { 1, 2, 3 }, HospitalMicroRules.Reported(panel, new[] { L(1, "AMP", "S"), L(2, "CRO", "R"), L(3, "MEM", "S") }).OrderBy(x => x));
			Assert.Equal(new[] { 1, 9 }, HospitalMicroRules.Reported(panel, new[] { L(1, "AMP", "I"), L(9, "XYZ", "S") }).OrderBy(x => x));   // off-panel: tested on purpose, reported
		}

		[Theory]
		[InlineData(HospitalMicroGroup.Staphylococci, true, "OXA", "MRSA")]
		[InlineData(HospitalMicroGroup.Staphylococci, true, "FOX", "MRSA")]
		[InlineData(HospitalMicroGroup.Staphylococci, false, "OXA", null)]          // coagulase-negative: not MRSA
		[InlineData(HospitalMicroGroup.Enterococci, false, "VAN", "VRE")]
		[InlineData(HospitalMicroGroup.Enterobacterales, false, "CRO", "ESBL")]
		[InlineData(HospitalMicroGroup.Enterobacterales, false, "MEM", "CRE")]
		[InlineData(HospitalMicroGroup.Acinetobacter, false, "IPM", "CRAB")]
		[InlineData(HospitalMicroGroup.Pseudomonas, false, "MEM", "CRPA")]
		[InlineData(HospitalMicroGroup.Pseudomonas, false, "CIP", null)]
		public void A_resistant_organism_is_named_from_its_results(string group, bool aureus, string resisted, string? mdro) =>
			Assert.Equal(mdro, HospitalMicroRules.Mdro(group, aureus, new[] { L(1, resisted, "R"), L(2, "GEN", "S") }));

		[Fact]
		public void Carbapenem_resistance_outranks_esbl_and_susceptible_is_never_a_flag()
		{
			Assert.Equal("CRE", HospitalMicroRules.Mdro(HospitalMicroGroup.Enterobacterales, false, new[] { L(1, "CRO", "R"), L(2, "ETP", "R") }));
			Assert.Null(HospitalMicroRules.Mdro(HospitalMicroGroup.Enterobacterales, false, new[] { L(1, "CRO", "S"), L(2, "MEM", "I") }));
		}

		[Fact]
		public void A_culture_completes_only_when_it_is_whole()
		{
			Assert.Equal("Record whether there is growth", HospitalMicroRules.CompleteRefusal(HospitalMicroGrowth.Pending, Array.Empty<(string, int)>()));
			Assert.Null(HospitalMicroRules.CompleteRefusal(HospitalMicroGrowth.NoGrowth, Array.Empty<(string, int)>()));
			Assert.Equal("Remove the isolates: there is no significant growth", HospitalMicroRules.CompleteRefusal(HospitalMicroGrowth.Contaminated, new[] { (HospitalMicroGroup.Enterobacterales, 3) }));
			Assert.Equal("Add the organism that grew", HospitalMicroRules.CompleteRefusal(HospitalMicroGrowth.Growth, Array.Empty<(string, int)>()));
			Assert.Equal("Enter the susceptibility of every isolate", HospitalMicroRules.CompleteRefusal(HospitalMicroGrowth.Growth, new[] { (HospitalMicroGroup.Enterobacterales, 4), (HospitalMicroGroup.Staphylococci, 0) }));
			Assert.Null(HospitalMicroRules.CompleteRefusal(HospitalMicroGrowth.Growth, new[] { (HospitalMicroGroup.Fungi, 0) }));   // a yeast may go without
			Assert.True(HospitalMicroRules.Critical("Blood", HospitalMicroGrowth.Growth));
			Assert.False(HospitalMicroRules.Critical("Urine", HospitalMicroGrowth.Growth));
			Assert.False(HospitalMicroRules.Critical("Blood", HospitalMicroGrowth.NoGrowth));
		}

		[Fact]
		public void The_report_reads_like_a_microbiology_report()
		{
			var start = Now.AddHours(-49);
			Assert.Equal("No growth after 49 h.", HospitalMicroRules.Summary(HospitalMicroGrowth.NoGrowth, Array.Empty<HospitalMicroIsolateSummary>(), start, Now, null));
			var s = HospitalMicroRules.Summary(HospitalMicroGrowth.Growth, new[] { new HospitalMicroIsolateSummary("Escherichia coli", ">100,000 CFU/mL", "ESBL",
				new[] { new HospitalMicroAstLine(1, "MEM", "Meropenem", "S", 0.125m), new HospitalMicroAstLine(2, "CRO", "Ceftriaxone", "R", null), new HospitalMicroAstLine(3, "CIP", "Ciprofloxacin", "I", null) }) }, start, Now, "Repeat after treatment");
			Assert.Equal("1) Escherichia coli >100,000 CFU/mL [ESBL]; S: Meropenem (MIC 0.125); I: Ciprofloxacin; R: Ceftriaxone — Repeat after treatment", s);
			Assert.Equal(1.5m, HospitalMicroRules.Mic("<=1.5"));
			Assert.Null(HospitalMicroRules.Mic("abc"));
		}

		// ---- the whole path on a real order -------------------------------------------------------------------------
		private sealed class NoLedger : IHospitalLedger
		{
			public Task<(bool Ok, string? Error, int? EntryId)> PostAsync(BusinessContext ctx, DateTime date, string description, string descriptionEn, string sourceType, int sourceId, IReadOnlyList<HospitalLedgerLine> lines, CancellationToken ct = default)
				=> Task.FromResult<(bool, string?, int?)>((true, null, 1));
		}

		[Fact]
		public async Task A_urine_culture_goes_from_the_bench_to_the_doctor_and_its_esbl_to_infection_control()
		{
			using var host = new PlatformTestHost();
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			var billing = new HospitalBillingService(host.Db, access, new NoLedger());
			var lab = new HospitalLabService(host.Db, access); var micro = new HospitalMicroService(host.Db, access);
			var consult = new HospitalConsultationService(host.Db, access, billing); var op = new HospitalOutpatientService(host.Db, access); var patients = new HospitalPatientService(host.Db, access);
			var ctx = Ctx();

			int Svc(string code, string cat) { var r = billing.SaveServiceAsync(ctx, new HospitalServiceInput { Code = code, Name = "خ " + code, NameEn = "S " + code, Category = cat, Price = 5m }, 7).Result; Assert.True(r.Ok, r.Error); return r.Id!.Value; }
			var cult = Svc("LAB-UCULT", HospitalServiceCategories.Lab);
			Assert.True((await lab.SaveTestAsync(ctx, cult, new HospitalLabTestInput { Specimen = "بول", Section = "أحياء دقيقة", IsCulture = true })).Ok);
			Assert.True((await micro.SeedStandardAsync(ctx)).Ok);
			Assert.True((await consult.SeedDefaultCodesAsync(ctx)).Ok);
			var fee = await host.Db.HospitalServices.Where(s => s.Code == "CONS-IM").Select(s => (int?)s.ID).FirstOrDefaultAsync() ?? Svc("CONS-IM", HospitalServiceCategories.Consultation);
			var doc = (await op.SavePractitionerAsync(ctx, new HospitalPractitionerInput { Name = "د س", NameEn = "Dr S", WorkDays = new() { 0 }, SessionStartMinute = 540, SessionEndMinute = 600, ConsultationServiceId = fee }, 7)).Id!.Value;
			var pid = (await patients.SaveAsync(ctx, new HospitalPatientInput { Name = "م ع", NameEn = "P U", Sex = "F" }, 7, Now)).Id!.Value;
			var appt = (await op.BookAsync(ctx, new HospitalBookingInput { PatientId = pid, PractitionerId = doc, Date = Today, StartMinute = 540 }, 7, Today)).Id!.Value;
			Assert.True((await op.MoveAsync(ctx, appt, HospitalAppointmentStatus.Arrived, 7, Now)).Ok);
			var enc = (await consult.OpenAsync(ctx, appt, 7, Now)).Id!.Value;
			Assert.True((await consult.SignAsync(ctx, enc, new HospitalEncounterInput { ChiefComplaint = "Dysuria", Diagnoses = new() { new() { Code = "J02.9", Primary = true } }, OrderServiceIds = new() { cult } }, 7, Now)).Ok);
			Assert.Equal(1, await lab.SyncOrdersAsync(ctx));
			var order = await host.Db.HospitalLabOrders.AsNoTracking().SingleAsync();
			host.Seed.HospitalAdmissions.Add(new HospitalAdmission { CompanyId = 1, PatientId = pid, AdmittedAt = Now.AddDays(-3), CreatedAt = Now.AddDays(-3) });
			await host.Seed.SaveChangesAsync();

			Assert.Equal("Receive the specimen in the lab before entering results", (await micro.StartAsync(ctx, order.ID, "Urine", null, 7, Now)).Error);
			Assert.True((await lab.CollectAsync(ctx, order.ID, 7, Now, scanOverride: "test: no scanner")).Ok);
			Assert.True((await lab.ReceiveAsync(ctx, order.ID, 7, Now)).Ok);
			var cultAnalyte = await host.Db.HospitalLabAnalytes.AsNoTracking().SingleAsync(a => a.ServiceId == cult);
			Assert.Equal("A culture is reported from its microbiology panel", (await lab.SaveResultsAsync(ctx, order.ID, new Dictionary<int, string?> { [cultAnalyte.ID] = "E. coli" }, null, 7, Now)).Error);
			Assert.Equal("Start the culture first", (await micro.PreliminaryAsync(ctx, order.ID, HospitalMicroGrowth.Growth, null, "x", 7, Now)).Error);

			Assert.True((await micro.StartAsync(ctx, order.ID, "Urine", "midstream", 7, Now)).Ok);
			Assert.True((await micro.PreliminaryAsync(ctx, order.ID, HospitalMicroGrowth.Growth, "Gram-negative rods", "Growth at 24 h; identification to follow", 7, Now.AddHours(24))).Ok);
			Assert.False((await host.Db.HospitalLabOrders.AsNoTracking().SingleAsync()).IsCritical);   // urine: not critical
			var ecoli = await host.Db.Set<HospitalMicroOrganism>().AsNoTracking().SingleAsync(o => o.Code == "ECOL");
			Assert.True((await micro.AddIsolateAsync(ctx, order.ID, ecoli.ID, ">100,000 CFU/mL")).Ok);
			var iso = await host.Db.Set<HospitalMicroIsolate>().AsNoTracking().SingleAsync();
			Assert.Equal("Enter the susceptibility of every isolate", (await micro.CompleteAsync(ctx, order.ID, HospitalMicroGrowth.Growth, null, 7, Now.AddHours(48))).Error);
			var abx = await host.Db.Set<HospitalMicroAntibiotic>().AsNoTracking().ToDictionaryAsync(a => a.Code, a => a.ID);
			HospitalMicroAstInput A(string code, string r, string? mic = null) => new() { AntibioticId = abx[code], Result = r, Mic = mic };
			Assert.Equal("A result is S, I or R", (await micro.SaveAstAsync(ctx, order.ID, iso.ID, new[] { A("AMP", "X") })).Error);
			Assert.True((await micro.SaveAstAsync(ctx, order.ID, iso.ID, new[] { A("AMP", "R"), A("CRO", "R"), A("CIP", "S"), A("GEN", "S"), A("NIT", "s"), A("MEM", "S", "0.06") })).Ok);
			Assert.Equal("ESBL", (await host.Db.Set<HospitalMicroIsolate>().AsNoTracking().SingleAsync()).Mdro);

			Assert.True((await micro.CompleteAsync(ctx, order.ID, HospitalMicroGrowth.Growth, null, 7, Now.AddHours(48))).Ok);
			var o2 = await host.Db.HospitalLabOrders.AsNoTracking().SingleAsync();
			Assert.Equal(HospitalLabStatus.Resulted, o2.Status);
			var text = (await host.Db.HospitalLabResults.AsNoTracking().SingleAsync()).TextValue!;
			Assert.StartsWith("1) Escherichia coli >100,000 CFU/mL [ESBL]", text);
			Assert.Contains("Meropenem (MIC 0.06)", text);   // tier 2, released because a tier-1 agent is resisted
			Assert.Contains("R: Ampicillin, Ceftriaxone", text);

			// any change after completion sends it back to the bench
			Assert.True((await micro.SaveAstAsync(ctx, order.ID, iso.ID, new[] { A("AMP", "R"), A("CRO", "R"), A("CIP", "S"), A("GEN", "S"), A("NIT", "S"), A("MEM", "S", "0.06") })).Ok);
			Assert.Equal(HospitalLabStatus.Received, (await host.Db.HospitalLabOrders.AsNoTracking().SingleAsync()).Status);
			Assert.True((await micro.CompleteAsync(ctx, order.ID, HospitalMicroGrowth.Growth, null, 7, Now.AddHours(48))).Ok);

			Assert.True((await lab.VerifyAsync(Ctx(8), order.ID, 8, Now.AddHours(49))).Ok);
			var c = await host.Db.Set<HospitalMicroCulture>().AsNoTracking().SingleAsync();
			Assert.Equal((HospitalMicroStatus.Final, "ESBL"), (c.Status, c.Mdro));
			var ic = Assert.Single(await host.Db.Set<HospitalInfectionCase>().AsNoTracking().ToListAsync());
			Assert.Equal((HospitalInfectionKind.Mdro, HospitalInfectionStatus.Suspected, pid), (ic.Kind, ic.Status, ic.PatientId));
			Assert.Contains("Escherichia coli (ESBL)", ic.Organism);
			Assert.Equal(ic.ID, c.InfectionCaseId);
			Assert.Equal("This result is verified and locked", (await micro.AddIsolateAsync(ctx, order.ID, ecoli.ID, null)).Error);
			Assert.Equal(1, await host.Db.Set<HospitalInfectionCase>().CountAsync());
		}

		[Fact]
		public async Task A_blood_culture_with_growth_is_critical_at_the_preliminary()
		{
			using var host = new PlatformTestHost();
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			var micro = new HospitalMicroService(host.Db, access);
			var pid = (await new HospitalPatientService(host.Db, access).SaveAsync(Ctx(), new HospitalPatientInput { Name = "م د", NameEn = "P B", Sex = "M" }, 7, Now)).Id!.Value;
			host.Seed.HospitalLabTests.Add(new HospitalLabTest { CompanyId = 1, ServiceId = 900, Specimen = "دم", Section = "أحياء دقيقة", IsCulture = true });
			host.Seed.HospitalLabOrders.Add(new HospitalLabOrder { CompanyId = 1, AccessionNo = "LAB-000900", PatientId = pid, ServiceId = 900, Code = "LAB-BCULT", Name = "ز", NameEn = "Blood culture", Status = HospitalLabStatus.Received, OrderedAt = Now, ReceivedAt = Now });
			await host.Seed.SaveChangesAsync();
			var id = (await host.Db.HospitalLabOrders.AsNoTracking().SingleAsync()).ID;
			Assert.True((await micro.StartAsync(Ctx(), id, "Blood", "aerobic bottle", 7, Now)).Ok);
			Assert.True((await micro.PreliminaryAsync(Ctx(), id, HospitalMicroGrowth.NoGrowth, null, "No growth at 24 h", 7, Now)).Ok);
			Assert.False((await host.Db.HospitalLabOrders.AsNoTracking().SingleAsync()).IsCritical);
			Assert.True((await micro.PreliminaryAsync(Ctx(), id, HospitalMicroGrowth.Growth, "Gram-positive cocci in clusters", "Positive bottle at 30 h", 7, Now)).Ok);
			var o = await host.Db.HospitalLabOrders.AsNoTracking().SingleAsync();
			Assert.True(o.IsCritical);
			Assert.Null(o.CriticalAcknowledgedAt);   // the call-back is still owed
		}

		[Fact]
		public void The_tables_match_their_slice_and_follow_a_merged_patient()
		{
			var model = HospitalSliceParity.Model(HospitalMicroModel.Configure);
			var s = HospitalSliceParity.Slice("hospital_h230_microbiology.sql");
			foreach (var (t, name) in new (Type, string)[] { (typeof(HospitalMicroOrganism), "HospitalMicroOrganisms"), (typeof(HospitalMicroAntibiotic), "HospitalMicroAntibiotics"),
				(typeof(HospitalMicroPanelItem), "HospitalMicroPanelItems"), (typeof(HospitalMicroCulture), "HospitalMicroCultures"), (typeof(HospitalMicroIsolate), "HospitalMicroIsolates"),
				(typeof(HospitalMicroSusceptibility), "HospitalMicroSusceptibilities") })
			{
				HospitalSliceParity.NamesItsTable(model, s, t, name);
				HospitalSliceParity.ColumnsMatch(model, s, t, name);
			}
			foreach (var x in HospitalMicroStatus.All.Concat(HospitalMicroGrowth.All).Concat(HospitalMdro.All).Concat(HospitalMicroGroup.All)) Assert.Contains("'" + x + "'", s);
			Assert.Contains(HospitalPatientMergeRules.PatientTables, x => x.Entity == typeof(HospitalMicroCulture));
			Assert.Contains(HospitalPatientMergeRules.PatientTables, x => x.Entity == typeof(HospitalMicroIsolate));
		}
	}
}
