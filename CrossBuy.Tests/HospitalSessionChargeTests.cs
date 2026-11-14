using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H119 — every treatment session is charged, with no database: each kind names its price-list code; the
	// services can still be made by hand (a test's) without billing; the three columns are in their slice and refer to
	// the bill line.
	// ==========================================================================================
	public class HospitalSessionChargeTests
	{
		[Fact]
		public void Each_kind_names_its_price_list_code()
		{
			Assert.Equal("HD-SESSION", HospitalSessionKinds.Code(HospitalSessionKinds.Dialysis));
			Assert.Equal("CHEMO-ADMIN", HospitalSessionKinds.Code(HospitalSessionKinds.Chemo));
			Assert.Equal("PT-SESSION", HospitalSessionKinds.Code(HospitalSessionKinds.Physio));
			Assert.Throws<ArgumentOutOfRangeException>(() => HospitalSessionKinds.Code("Massage"));
			Assert.Equal(3, HospitalSessionKinds.All.Distinct().Count());
		}

		[Fact]
		public void The_dependency_container_builds_the_services_with_billing()
		{
			// the constructor the container uses is the one with the billing service; the old one still builds them by hand
			foreach (var t in new[] { typeof(HospitalDialysisService), typeof(HospitalOncologyService), typeof(HospitalRehabService) })
			{
				var withBilling = t.GetConstructors().Single(c => c.GetParameters().Any(p => p.ParameterType == typeof(HospitalBillingService)));
				Assert.True(withBilling.IsDefined(typeof(Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructorAttribute), false));
				Assert.Contains(t.GetConstructors(), c => c.GetParameters().Length == 2);
			}
		}

		[Fact]
		public void The_columns_match_their_slice()
		{
			var s = HospitalSliceParity.Slice("hospital_h119_session_charges.sql");
			foreach (var tbl in new[] { "HospitalDialysisSessions", "HospitalChemoCycles", "HospitalRehabSessions" })
			{
				Assert.Contains("ALTER TABLE dbo." + tbl + " ADD BillLineId int NULL", s);
				Assert.Contains("REFERENCES dbo.HospitalBillLines(ID)", s);
			}
			foreach (var tbl in new[] { "HospitalBillLines", "HospitalDialysisSessions", "HospitalChemoCycles", "HospitalRehabSessions" })
				Assert.Contains("OBJECT_ID('dbo." + tbl + "','U') IS NULL", s);
		}
	}
}
