using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H245 — biomedical engineering. What must never happen: a high-risk device allowed a year between safety
	// tests; a never-tested device shown as current; downtime counted outside the window or an open outage forgotten;
	// a device taken down twice, or brought up while in service; a failed test with no reason; a model naming a table its
	// slice does not create.
	// ==========================================================================================
	public class HospitalBiomedTests
	{
		private static readonly DateTime Today = new(2026, 11, 22);

		[Fact]
		public void A_high_risk_device_is_tested_every_six_months()
		{
			Assert.False(HospitalBiomedRules.SafetyOverdue(HospitalBiomedRisk.High, Today.AddMonths(-5), Today));
			Assert.True(HospitalBiomedRules.SafetyOverdue(HospitalBiomedRisk.High, Today.AddMonths(-7), Today));
			Assert.False(HospitalBiomedRules.SafetyOverdue(HospitalBiomedRisk.Low, Today.AddMonths(-7), Today));
			Assert.True(HospitalBiomedRules.SafetyOverdue(HospitalBiomedRisk.Low, null, Today));
		}

		[Fact]
		public void Pm_compliance_is_the_share_of_schedules_not_past_due()
		{
			Assert.Equal(67m, HospitalBiomedRules.PmCompliance(new[] { Today, Today.AddDays(5), Today.AddDays(-1) }, Today));
			Assert.Null(HospitalBiomedRules.PmCompliance(Array.Empty<DateTime>(), Today));
		}

		[Fact]
		public void Downtime_is_clipped_to_the_window_and_an_open_outage_runs_to_the_end()
		{
			var from = new DateTime(2026, 11, 1); var to = new DateTime(2026, 11, 11);
			HospitalBiomedEvent E(string k, DateTime at) => new() { Kind = k, At = at };
			var ev = new[]
			{
				E(HospitalBiomedEventKind.Down, from.AddHours(-10)), E(HospitalBiomedEventKind.Up, from.AddHours(6)),   // 6 h inside
				E(HospitalBiomedEventKind.SafetyTest, from.AddDays(2)),
				E(HospitalBiomedEventKind.Down, to.AddHours(-12)),                                                          // open: 12 h
			};
			Assert.Equal(18, HospitalBiomedRules.DowntimeHours(ev, from, to));
			Assert.Equal(92.5m, HospitalBiomedRules.Availability(18, from, to));   // 240 h window
		}

		[Fact]
		public void Each_event_fits_the_devices_state()
		{
			Assert.Null(HospitalBiomedRules.EventRefusal(HospitalBiomedStatus.InService, HospitalBiomedEventKind.Down, null, "No power"));
			Assert.NotNull(HospitalBiomedRules.EventRefusal(HospitalBiomedStatus.InService, HospitalBiomedEventKind.Down, null, " "));
			Assert.NotNull(HospitalBiomedRules.EventRefusal(HospitalBiomedStatus.Down, HospitalBiomedEventKind.Down, null, "again"));
			Assert.NotNull(HospitalBiomedRules.EventRefusal(HospitalBiomedStatus.InService, HospitalBiomedEventKind.Up, null, null));
			Assert.Null(HospitalBiomedRules.EventRefusal(HospitalBiomedStatus.Down, HospitalBiomedEventKind.Up, null, null));
			Assert.NotNull(HospitalBiomedRules.EventRefusal(HospitalBiomedStatus.InService, HospitalBiomedEventKind.SafetyTest, false, null));
			Assert.Null(HospitalBiomedRules.EventRefusal(HospitalBiomedStatus.InService, HospitalBiomedEventKind.SafetyTest, true, null));
			Assert.NotNull(HospitalBiomedRules.EventRefusal(HospitalBiomedStatus.Retired, HospitalBiomedEventKind.SafetyTest, true, null));
			Assert.NotNull(HospitalBiomedRules.DeviceRefusal("Robot", HospitalBiomedRisk.High, null, null, null));
		}

		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalBiomedModel.Configure);
		private static string Slice() => HospitalSliceParity.Slice("hospital_h245_biomed.sql");

		[Theory]
		[InlineData(typeof(HospitalBiomedDevice), "HospitalBiomedDevices")]
		[InlineData(typeof(HospitalBiomedEvent), "HospitalBiomedEvents")]
		public void Every_column_is_read_in_the_type_its_slice_creates(Type entity, string table)
		{
			HospitalSliceParity.NamesItsTable(Model(), Slice(), entity, table);
			HospitalSliceParity.ColumnsMatch(Model(), Slice(), entity, table);
		}
	}
}
