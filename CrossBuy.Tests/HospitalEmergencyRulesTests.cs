using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H13 — the emergency rules, DATABASE-FREE: pure calls on HospitalEmergencyRules, and the EF model
	// built with the SQL Server conventions and no connection, read against its slice's text.
	//
	// What must never happen: a patient taken back to triage or skipped from triage into treatment, a visit
	// closed with a doctor's outcome before disposition, "left without being seen" recorded for a patient a
	// doctor saw, a triage without its vital signs, two open visits in one bay or for one patient, or a model
	// that names a table its slice does not create.
	// ==========================================================================================
	public class HospitalEmergencyRulesTests
	{
		private static readonly HospitalErVitals Calm = new(120, 80, 16, 98, 36.8m);

		[Theory]
		[InlineData(HospitalErStage.Triage, HospitalErStage.Waiting, false, true)]
		[InlineData(HospitalErStage.Waiting, HospitalErStage.Treatment, false, true)]
		[InlineData(HospitalErStage.Treatment, HospitalErStage.Observation, true, true)]
		[InlineData(HospitalErStage.Observation, HospitalErStage.Treatment, true, true)]
		[InlineData(HospitalErStage.Treatment, HospitalErStage.Disposition, true, true)]
		[InlineData(HospitalErStage.Observation, HospitalErStage.Disposition, true, true)]
		[InlineData(HospitalErStage.Triage, HospitalErStage.Treatment, false, false)]      // never skips the doctor's queue
		[InlineData(HospitalErStage.Waiting, HospitalErStage.Triage, false, false)]        // never back to triage
		[InlineData(HospitalErStage.Waiting, HospitalErStage.Disposition, false, false)]   // no decision before a doctor
		[InlineData(HospitalErStage.Closed, HospitalErStage.Treatment, true, false)]
		[InlineData(HospitalErStage.Treatment, HospitalErStage.Treatment, true, false)]
		public void The_board_moves_only_along_its_lanes(string from, string to, bool seen, bool allowed)
			=> Assert.Equal(allowed, HospitalEmergencyRules.CanMove(from, to, seen));

		[Fact]
		public void A_doctors_outcome_needs_disposition()
		{
			Assert.Null(HospitalEmergencyRules.CloseRefusal(HospitalErStage.Disposition, true, HospitalErOutcome.Discharged));
			Assert.NotNull(HospitalEmergencyRules.CloseRefusal(HospitalErStage.Treatment, true, HospitalErOutcome.Admitted));
			Assert.NotNull(HospitalEmergencyRules.CloseRefusal(HospitalErStage.Closed, true, HospitalErOutcome.Discharged));
			Assert.NotNull(HospitalEmergencyRules.CloseRefusal(HospitalErStage.Disposition, true, "Vanished"));
		}

		[Fact]
		public void Left_without_being_seen_only_before_any_doctor()
		{
			Assert.Null(HospitalEmergencyRules.CloseRefusal(HospitalErStage.Waiting, false, HospitalErOutcome.LeftWithoutBeingSeen));
			Assert.Null(HospitalEmergencyRules.CloseRefusal(HospitalErStage.Triage, false, HospitalErOutcome.LeftWithoutBeingSeen));
			Assert.NotNull(HospitalEmergencyRules.CloseRefusal(HospitalErStage.Waiting, true, HospitalErOutcome.LeftWithoutBeingSeen));
			Assert.NotNull(HospitalEmergencyRules.CloseRefusal(HospitalErStage.Disposition, true, HospitalErOutcome.LeftWithoutBeingSeen));
		}

		[Fact]
		public void Esi_one_is_triaged_on_sight_every_other_level_needs_vitals()
		{
			var none = new HospitalErVitals(null, null, null, null, null);
			Assert.Null(HospitalEmergencyRules.TriageRefusal(1, none));
			Assert.NotNull(HospitalEmergencyRules.TriageRefusal(3, none));
			Assert.NotNull(HospitalEmergencyRules.TriageRefusal(3, none with { HeartRate = 90 }));
			Assert.Null(HospitalEmergencyRules.TriageRefusal(3, Calm));
			Assert.NotNull(HospitalEmergencyRules.TriageRefusal(0, Calm));
			Assert.NotNull(HospitalEmergencyRules.TriageRefusal(6, Calm));
		}

		[Fact]
		public void Impossible_vital_signs_are_refused()
		{
			Assert.NotNull(HospitalEmergencyRules.TriageRefusal(3, Calm with { SpO2 = 120 }));
			Assert.NotNull(HospitalEmergencyRules.TriageRefusal(3, Calm with { HeartRate = 400 }));
			Assert.NotNull(HospitalEmergencyRules.TriageRefusal(3, Calm with { TemperatureC = 50m }));
		}

		[Theory]
		[InlineData(120, 80, 16, 98, 36.8, false)]
		[InlineData(120, 112, 16, 98, 36.8, true)]   // tachycardia
		[InlineData(120, 80, 26, 98, 36.8, true)]    // tachypnoea
		[InlineData(120, 80, 16, 89, 36.8, true)]    // hypoxia
		[InlineData(84, 80, 16, 98, 36.8, true)]     // hypotension
		[InlineData(120, 80, 16, 98, 39.4, true)]    // fever
		public void Danger_zone_vitals_are_flagged(int bp, int hr, int rr, int spo2, double t, bool flagged)
			=> Assert.Equal(flagged, HospitalEmergencyRules.DangerZone(new HospitalErVitals(bp, hr, rr, spo2, (decimal)t)));

		[Fact]
		public void Missing_vitals_never_flag_danger()
			=> Assert.False(HospitalEmergencyRules.DangerZone(new HospitalErVitals(null, null, null, null, null)));

		[Fact]
		public void Waits_count_to_the_doctor_or_to_now_and_never_go_negative()
		{
			var at = new DateTime(2026, 11, 3, 8, 0, 0);
			Assert.Equal(18, HospitalEmergencyRules.WaitMinutes(at, at.AddMinutes(18.9), at.AddHours(3)));
			Assert.Equal(52, HospitalEmergencyRules.WaitMinutes(at, null, at.AddMinutes(52)));
			Assert.Equal(0, HospitalEmergencyRules.WaitMinutes(at, null, at.AddMinutes(-5)));
		}

		[Fact]
		public void The_board_shows_the_sickest_first_then_the_longest_waiting_and_untriaged_last()
		{
			var at = new DateTime(2026, 11, 3, 8, 0, 0);
			var v = new[] { (Esi: (int?)3, At: at), (Esi: (int?)null, At: at.AddMinutes(-60)), (Esi: (int?)1, At: at.AddMinutes(10)), (Esi: (int?)3, At: at.AddMinutes(-20)) };
			var order = HospitalEmergencyRules.BoardOrder(v, x => x.Esi, x => x.At).ToList();
			Assert.Equal(new int?[] { 1, 3, 3, null }, order.Select(x => x.Esi));
			Assert.True(order[1].At < order[2].At);
		}

		[Fact]
		public void Door_to_doctor_and_the_lwbs_rate()
		{
			var at = new DateTime(2026, 11, 3, 8, 0, 0);
			var visits = new[]
			{
				new HospitalErTimes(at, at.AddMinutes(10), HospitalErStage.Closed, HospitalErOutcome.Discharged),
				new HospitalErTimes(at, at.AddMinutes(30), HospitalErStage.Treatment, null),
				new HospitalErTimes(at, null, HospitalErStage.Closed, HospitalErOutcome.LeftWithoutBeingSeen),
				new HospitalErTimes(at, null, HospitalErStage.Waiting, null),
			};
			Assert.Equal(20, HospitalEmergencyRules.DoorToDoctor(visits));
			Assert.Equal(50m, HospitalEmergencyRules.LeftWithoutBeingSeenRate(visits));
			Assert.Null(HospitalEmergencyRules.DoorToDoctor(Array.Empty<HospitalErTimes>()));
			Assert.Null(HospitalEmergencyRules.LeftWithoutBeingSeenRate(visits.Where(x => x.Stage != HospitalErStage.Closed)));
		}

		// ------------------------------------------------------------------ the model against its slice, no database

		private static string Slice()
		{
			for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
			{
				var f = Path.Combine(d.FullName, "CrossBuy", "deploy", "sql", "hospital_h13_emergency.sql");
				if (File.Exists(f)) return File.ReadAllText(f);
			}
			throw new FileNotFoundException("CrossBuy/deploy/sql/hospital_h13_emergency.sql not found above " + AppContext.BaseDirectory);
		}

		private static Microsoft.EntityFrameworkCore.Metadata.IEntityType Entity(Type? t = null)
		{
			var mb = new ModelBuilder(SqlServerConventionSetBuilder.Build());
			HospitalEmergencyModel.Configure(mb);
			return mb.FinalizeModel().FindEntityType(t ?? typeof(HospitalErVisit))!;
		}

		[Theory]
		[InlineData(typeof(HospitalErVisit), "HospitalErVisits")]
		[InlineData(typeof(HospitalErBay), "HospitalErBays")]
		public void The_model_names_the_tables_its_slice_creates(Type entity, string table)
		{
			Assert.Equal(table, Entity(entity).GetTableName());
			Assert.Contains($"CREATE TABLE dbo.{table} (", Slice().Split('\n').Select(l => l.Trim()));
		}

		/// The department's bays are its own: the slice references no facility bed, so an ER bay can never be
		/// offered as a ward bed by admission (H3).
		[Fact]
		public void A_bay_is_never_a_facility_bed()
		{
			Assert.DoesNotContain("HospitalBeds", Slice());
			var fk = Entity().GetForeignKeys().Single(f => f.Properties.Single().Name == nameof(HospitalErVisit.BayId));
			Assert.Equal(typeof(HospitalErBay), fk.PrincipalEntityType.ClrType);
		}

		[Theory]
		[InlineData("UX_HospitalErVisits_OpenBay", "BayId", "[BayId] IS NOT NULL AND [Stage] <> 'Closed'", "CREATE UNIQUE INDEX UX_HospitalErVisits_OpenBay ON dbo.HospitalErVisits (BayId) WHERE BayId IS NOT NULL AND Stage <> N'Closed';")]
		[InlineData("UX_HospitalErVisits_OpenPatient", "CompanyId,PatientId", "[PatientId] IS NOT NULL AND [Stage] <> 'Closed'", "CREATE UNIQUE INDEX UX_HospitalErVisits_OpenPatient ON dbo.HospitalErVisits (CompanyId, PatientId) WHERE PatientId IS NOT NULL AND Stage <> N'Closed';")]
		public void One_open_visit_per_bay_and_per_patient_in_model_and_slice(string name, string columns, string filter, string statement)
		{
			var ix = Entity().GetIndexes().Single(i => i.GetDatabaseName() == name);
			Assert.True(ix.IsUnique);
			Assert.Equal(columns, string.Join(",", ix.Properties.Select(p => p.Name)));
			Assert.Equal(filter, ix.GetFilter());
			Assert.Contains(statement, Slice().Split('\n').Select(l => l.Trim()));
		}

		[Fact]
		public void Every_table_the_slice_references_is_a_checked_prerequisite()
		{
			var sql = Slice();
			var created = System.Text.RegularExpressions.Regex.Matches(sql, @"CREATE TABLE dbo\.(\w+)").Select(m => m.Groups[1].Value).ToHashSet();
			var referenced = System.Text.RegularExpressions.Regex.Matches(sql, @"REFERENCES dbo\.(\w+)\(").Select(m => m.Groups[1].Value).Distinct().Except(created).ToList();
			var guard = sql[..sql.IndexOf("THROW", StringComparison.Ordinal)];
			Assert.Equal(2, referenced.Count);   // patients and practitioners; the bays are its own
			foreach (var t in referenced) Assert.Contains($"OBJECT_ID('dbo.{t}','U') IS NULL", guard);
		}
	}
}
