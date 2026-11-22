using CrossBuy.BL.Hospital;
using FellowOakDicom;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H228 — the DICOM Modality Worklist. What must never happen: a CT handed an ultrasound order, an order from
	// another day passed as today's, the same order stamped with two different study UIDs on two pulls, a port opened
	// without its interface and key, or a machine outside the allowed list heard.
	// ==========================================================================================
	public class HospitalDicomWorklistTests
	{
		private static HospitalWorklistItem Item(string acc = "RAD-000041", string mrn = "MRN-100023", string mod = "CT", int hour = 9, int day = 22) =>
			new(acc, mrn, "سامي حداد", mod, "CT chest", new DateTime(2026, 11, day, hour, 30, 0, DateTimeKind.Local).ToUniversalTime(), "Stat", "MSH|...");

		[Theory]
		[InlineData("", null, null)]
		[InlineData("20261122", "2026-11-22", "2026-11-22")]
		[InlineData("20261120-20261122", "2026-11-20", "2026-11-22")]
		[InlineData("20261120-", "2026-11-20", null)]
		[InlineData("-20261122", null, "2026-11-22")]
		public void A_dicom_date_or_range_is_read(string value, string? from, string? to)
		{
			var (f, t) = HospitalDicomWorklistRules.DateRange(value);
			Assert.Equal(from, f?.ToString("yyyy-MM-dd"));
			Assert.Equal(to, t?.ToString("yyyy-MM-dd"));
		}

		[Fact]
		public void A_machine_gets_only_its_modality_its_days_and_the_patient_it_asked_for()
		{
			var today = new DateTime(2026, 11, 22);
			Assert.True(HospitalDicomWorklistRules.Matches(Item(), new("CT", today, today, null)));
			Assert.True(HospitalDicomWorklistRules.Matches(Item(), new("ct", null, null, "MRN-1000*")));
			Assert.False(HospitalDicomWorklistRules.Matches(Item(mod: "US"), new("CT", null, null, null)));
			Assert.False(HospitalDicomWorklistRules.Matches(Item(day: 21), new(null, today, today, null)));
			Assert.False(HospitalDicomWorklistRules.Matches(Item(), new(null, null, null, "MRN-100025")));
		}

		[Fact]
		public void An_order_is_always_the_same_study_and_never_another_companys()
		{
			string a = HospitalDicomWorklistRules.StudyUid(1, "RAD-000041");
			Assert.Equal(a, HospitalDicomWorklistRules.StudyUid(1, "RAD-000041"));
			Assert.NotEqual(a, HospitalDicomWorklistRules.StudyUid(2, "RAD-000041"));
			Assert.NotEqual(a, HospitalDicomWorklistRules.StudyUid(1, "RAD-000042"));
			Assert.StartsWith("2.25.", a);
			Assert.True(a.Length <= 64);   // a UID is 64 characters at most
			Assert.Matches("^2\\.25\\.[1-9][0-9]*$", a);
		}

		[Fact]
		public void The_answer_carries_what_a_machine_needs_in_utf8()
		{
			var d = HospitalDicomWorklistRules.Answer(Item(), 1, "CT01");
			Assert.Equal("ISO_IR 192", d.GetSingleValue<string>(DicomTag.SpecificCharacterSet));
			Assert.Equal(("RAD-000041", "MRN-100023", "HIGH"), (d.GetSingleValue<string>(DicomTag.AccessionNumber), d.GetSingleValue<string>(DicomTag.PatientID), d.GetSingleValue<string>(DicomTag.RequestedProcedurePriority)));
			Assert.Equal("سامي حداد", d.GetString(DicomTag.PatientName));
			var sps = d.GetSequence(DicomTag.ScheduledProcedureStepSequence).Items[0];
			Assert.Equal(("CT", "CT01", "20261122", "093000"), (sps.GetSingleValue<string>(DicomTag.Modality), sps.GetSingleValue<string>(DicomTag.ScheduledStationAETitle),
				sps.GetSingleValue<string>(DicomTag.ScheduledProcedureStepStartDate), sps.GetSingleValue<string>(DicomTag.ScheduledProcedureStepStartTime)));
		}

		[Fact]
		public void The_query_is_read_from_the_machines_request()
		{
			var req = new DicomDataset { { DicomTag.PatientID, "MRN-100023" },
				{ DicomTag.ScheduledProcedureStepSequence, new DicomDataset { { DicomTag.Modality, "CT" }, { DicomTag.ScheduledProcedureStepStartDate, "20261122" } } } };
			var q = HospitalDicomWorklistRules.QueryOf(req);
			Assert.Equal(("CT", "MRN-100023", "2026-11-22"), (q.Modality, q.PatientId, q.From?.ToString("yyyy-MM-dd")));
			Assert.Null(HospitalDicomWorklistRules.QueryOf(Wild()).Modality);
			Assert.Equal(new HospitalWorklistQuery(null, null, null, null), HospitalDicomWorklistRules.QueryOf(null));
		}

		// a machine's "any modality" arrives as the wildcard, which CS would refuse if the dataset were validated (one from the network is not)
		private static DicomDataset Wild()
		{
			var sps = new DicomDataset().NotValidated(); sps.Add(DicomTag.Modality, "*");
			var root = new DicomDataset().NotValidated(); root.Add(new DicomSequence(DicomTag.ScheduledProcedureStepSequence, sps));
			return root;
		}

		[Fact]
		public void Only_a_port_with_an_interface_a_key_and_a_valid_ae_opens_and_only_allowed_machines_are_heard()
		{
			HospitalDicomWorklistOptions O(int port = 104, string iface = "PACS-MWL", string key = "k", string ae = "CROSSBUSINESS", string bind = "127.0.0.1") =>
				new() { Port = port, Interface = iface, Key = key, AeTitle = ae, Bind = bind };
			Assert.NotNull(HospitalDicomWorklistRules.Validate(O()).Ok);
			Assert.Null(HospitalDicomWorklistRules.Validate(O(port: 0)).Ok);
			Assert.Null(HospitalDicomWorklistRules.Validate(O(key: "")).Ok);
			Assert.Null(HospitalDicomWorklistRules.Validate(O(ae: "A_TITLE_LONGER_THAN_16")).Ok);
			Assert.Null(HospitalDicomWorklistRules.Validate(O(bind: "pacs-host")).Ok);
			Assert.Equal((null, null), HospitalDicomWorklistRules.Read(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()));
			var only = O(); only.AllowedCallingAEs.Add("CT01");
			Assert.True(HospitalDicomWorklistRules.CallingAllowed(only, "ct01 "));
			Assert.False(HospitalDicomWorklistRules.CallingAllowed(only, "US02"));
			Assert.True(HospitalDicomWorklistRules.CallingAllowed(O(), "ANY"));
		}
	}
}
