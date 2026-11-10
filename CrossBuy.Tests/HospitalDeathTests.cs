using CrossBuy.BL.Hospital;
using CrossBuy.BL.Reporting;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H59 — death certificates, with no database: a patient dies once, the time is real, the WHO chain is
	// filled in order and has its immediate cause, the underlying cause is the last one written, the table against its
	// slice (one live certificate per patient is a filtered unique index), and the Report Studio certificate.
	// ==========================================================================================
	public class HospitalDeathTests
	{
		private static readonly DateTime Now = new(2026, 11, 8, 12, 0, 0);
		private static HospitalDeathInput Ok() => new() { PatientId = 3, DiedAt = Now.AddHours(-1), Place = HospitalDeathPlace.Icu, ImmediateCause = "Septic shock", DueTo1 = "Pneumonia", Manner = HospitalDeathManner.Natural };

		[Fact]
		public void A_well_formed_certificate_is_accepted_once()
		{
			Assert.Null(HospitalDeathRules.CertifyRefusal(Ok(), null, Now));
			Assert.Equal("This patient's death is already certified", HospitalDeathRules.CertifyRefusal(Ok(), Now.AddDays(-1), Now));
		}

		[Fact]
		public void The_time_is_given_not_in_the_future_and_not_older_than_the_civil_registry_takes()
		{
			var i = Ok(); i.DiedAt = null; Assert.Equal("Give the time of death", HospitalDeathRules.CertifyRefusal(i, null, Now));
			i = Ok(); i.DiedAt = Now.AddHours(1); Assert.Equal("The time of death is in the future", HospitalDeathRules.CertifyRefusal(i, null, Now));
			i = Ok(); i.DiedAt = Now.AddDays(-HospitalDeathRules.MaxDaysLate - 1); Assert.Equal("A death older than {0} days is certified with the civil registry", HospitalDeathRules.CertifyRefusal(i, null, Now));
		}

		[Fact]
		public void The_chain_has_its_immediate_cause_and_no_gaps()
		{
			var i = Ok(); i.ImmediateCause = " "; Assert.Equal("Write the immediate cause of death", HospitalDeathRules.CertifyRefusal(i, null, Now));
			i = Ok(); i.DueTo1 = null; i.DueTo2 = "Diabetes"; Assert.Equal("Fill the causes in order, from the immediate cause down", HospitalDeathRules.CertifyRefusal(i, null, Now));
			i = Ok(); i.Place = "Home"; Assert.Equal("Choose where the death happened", HospitalDeathRules.CertifyRefusal(i, null, Now));
			i = Ok(); i.Manner = "Unknown"; Assert.Equal("Choose the manner of death", HospitalDeathRules.CertifyRefusal(i, null, Now));
		}

		[Fact]
		public void The_underlying_cause_is_the_last_cause_written()
		{
			Assert.Equal("Septic shock", HospitalDeathRules.Underlying(new HospitalDeath { ImmediateCause = "Septic shock" }));
			Assert.Equal("Pneumonia", HospitalDeathRules.Underlying(new HospitalDeath { ImmediateCause = "Septic shock", DueTo1 = "Pneumonia" }));
			Assert.Equal("COPD", HospitalDeathRules.Underlying(new HospitalDeath { ImmediateCause = "Septic shock", DueTo1 = "Pneumonia", DueTo2 = "x", UnderlyingCause = "COPD" }));
		}

		[Fact]
		public void The_table_matches_its_slice_and_a_patient_dies_once()
		{
			var s = HospitalSliceParity.Slice("hospital_h59_deaths.sql");
			var m = HospitalSliceParity.Model(HospitalDeathModel.Configure);
			HospitalSliceParity.NamesItsTable(m, s, typeof(HospitalDeath), "HospitalDeaths");
			HospitalSliceParity.ColumnsMatch(m, s, typeof(HospitalDeath), "HospitalDeaths");
			Assert.Contains("CREATE UNIQUE INDEX UX_HospitalDeaths_LivePatient ON dbo.HospitalDeaths (CompanyId, PatientId) WHERE VoidedAt IS NULL;", s);
			Assert.Contains("ALTER TABLE dbo.HospitalPatients ADD DeceasedAt datetime2 NULL;", s);
			foreach (var x in HospitalDeathPlace.All.Concat(HospitalDeathManner.All)) Assert.Contains("'" + x + "'", s);
			Assert.Equal(new[] { "HospitalPatients" }, HospitalSliceParity.CheckedPrerequisites(s));
		}

		[Fact]
		public void The_certificate_prints_only_through_report_studio_and_moves_in_a_merge()
		{
			var d = HospitalDocumentDatasets.All().Single(x => x.DatasetCode == HospitalDocumentDatasetCodes.DeathCertificate);
			Assert.Contains(d.Parameters, p => p.Key == "DeathId" && p.Required);
			foreach (var k in new[] { "DiedAt", "Place", "CauseA", "CauseB", "UnderlyingCause", "Manner", "PatientName", "DocumentNo" }) Assert.Contains(d.Fields, f => f.Key == k);
			Assert.Contains(ReportScreenBindings.For(ReportScreenKeys.HospitalDeath), b => b.ReportCode == HospitalDocumentDatasetCodes.DeathCertificate && b.IdParameter == "DeathId");
			Assert.Contains(HospitalPatientMergeRules.PatientTables, t => t.Entity == typeof(HospitalDeath));
		}
	}
}
