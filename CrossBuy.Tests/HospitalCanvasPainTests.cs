using CrossBuy.BL.Hospital;
using CrossBuy.BL.Reporting;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H112 — the patient draws their pain on the tablet (the prototype's canvas layer 8), with no database:
	// the strength 0–10 is required and bounded, the words come from a fixed list only, the places are put on the body
	// by the server as on the body map, the strength becomes the LOINC 72514-3 result, and the paper says it is the
	// patient's own report.
	// ==========================================================================================
	public class HospitalCanvasPainTests
	{
		[Fact]
		public void The_strength_is_required_and_bounded()
		{
			Assert.Equal("Choose how strong the pain is, from 0 to 10", HospitalPainRules.Read(null).Error);
			Assert.Equal("Choose how strong the pain is, from 0 to 10", HospitalPainRules.Read("{}").Error);
			Assert.Equal("Choose how strong the pain is, from 0 to 10", HospitalPainRules.Read("{\"words\":[\"burning\"]}").Error);
			Assert.Equal("Choose how strong the pain is, from 0 to 10", HospitalPainRules.Read("{\"nrs\":11}").Error);
			Assert.Equal("Choose how strong the pain is, from 0 to 10", HospitalPainRules.Read("{\"nrs\":-1}").Error);
			Assert.Equal("Choose how strong the pain is, from 0 to 10", HospitalPainRules.Read("{\"nrs\":6.5}").Error);
			Assert.Equal("Choose how strong the pain is, from 0 to 10", HospitalPainRules.Read("{\"nrs\":\"7\"}").Error);
			Assert.Equal(0, HospitalPainRules.Read("{\"nrs\":0}").Result!.Nrs);   // no pain is an answer
			Assert.Equal(10, HospitalPainRules.Read("{\"nrs\":10}").Result!.Nrs);
		}

		[Fact]
		public void The_words_come_from_the_list_only()
		{
			var r = HospitalPainRules.Read("{\"nrs\":7,\"words\":[\"burning\",\"spreading\",\"burning\"]}").Result!;
			Assert.Equal(new[] { "burning", "spreading" }, r.Words);   // a word counts once
			Assert.NotNull(HospitalPainRules.Read("{\"nrs\":7,\"words\":[\"<b>sharp</b>\"]}").Error);   // nothing typed in
			Assert.NotNull(HospitalPainRules.Read("{\"nrs\":7,\"words\":\"burning\"}").Error);
			Assert.NotNull(HospitalPainRules.Read("{\"nrs\":7,\"words\":[3]}").Error);
			Assert.NotNull(HospitalPainRules.Read("[1]").Error);
			Assert.NotNull(HospitalPainRules.Read("{nrs").Error);
			Assert.Equal("{\"nrs\":7,\"words\":[\"burning\",\"spreading\"]}", HospitalPainRules.Store(r));
		}

		[Fact]
		public void The_places_sit_on_the_body_as_on_the_body_map()
		{
			var (marks, err, _) = HospitalCanvasRules.Place(HospitalCanvasKind.Pain, new[]
			{
				new HospitalCanvasMarkInput { Finding = "Pain", X = 100, Y = 120 },        // front trunk
				new HospitalCanvasMarkInput { Finding = "Pain", X = 230 + 40, Y = 150 },   // back, the drawn right arm = the patient's left
			});
			Assert.Null(err);
			Assert.Equal(("trunk", "F"), (marks![0].Region, marks[0].Side));
			Assert.Equal(("armL", "B"), (marks[1].Region, marks[1].Side));
			Assert.All(marks, m => Assert.Equal("SNOMED 22253000", m.FindingCode));
			Assert.NotNull(HospitalCanvasRules.Place(HospitalCanvasKind.Pain, new[] { new HospitalCanvasMarkInput { Finding = "Pain", X = 5, Y = 450 } }).Error);   // off the body
			Assert.NotNull(HospitalCanvasRules.Place(HospitalCanvasKind.Pain, new[] { new HospitalCanvasMarkInput { Finding = "Melanoma", X = 100, Y = 120 } }).Error);   // not the patient's word
		}

		[Fact]
		public void The_strength_is_the_result_and_the_paper_names_the_patient()
		{
			Assert.Equal(new[] { HospitalPainRules.NrsCode }, HospitalCanvasRules.ResultCodes(HospitalCanvasKind.Pain));
			var st = HospitalPainRules.Store(HospitalPainRules.Read("{\"nrs\":7,\"words\":[\"burning\"]}").Result!);
			Assert.Equal(new[] { ("72514-3", (string?)null, 7m) }, HospitalCanvasRules.Results(HospitalCanvasKind.Pain, st));
			var p = new HospitalPatient { Name = "م", Sex = "M" };
			Assert.Equal("Pain now (patient-reported): ‎7/10‎ · burning", HospitalCanvasPaper.Results(HospitalCanvasKind.Pain, st, p, DateTime.UtcNow, false));
			Assert.Equal("شدة الألم (من المريض): 7 من 10 · حارق", HospitalCanvasPaper.Results(HospitalCanvasKind.Pain, st, p, DateTime.UtcNow, true));
			Assert.Contains("patient", HospitalCanvasPaper.Title(HospitalCanvasKind.Pain, false));
			Assert.Contains(HospitalObsSource.Patient, HospitalObsSource.All);
		}

		/// The parity test leaves text widths to the slice — so a code longer than its column passed every test and failed
		/// only when a visit was signed ('Patient' into nvarchar(6)). Every allowed code must fit the column the model writes.
		[Fact]
		public void Every_code_fits_its_column()
		{
			var obs = HospitalSliceParity.Model(HospitalClinicalContentModel.Configure).FindEntityType(typeof(HospitalClinicalObservation))!;
			var src = obs.FindProperty(nameof(HospitalClinicalObservation.Source))!.GetMaxLength()!.Value;
			Assert.All(HospitalObsSource.All, s => Assert.True(s.Length <= src, $"'{s}' is longer than Source ({src})"));
			var sheet = HospitalSliceParity.Model(HospitalCanvasModel.Configure).FindEntityType(typeof(HospitalCanvasSheet))!;
			var kind = sheet.FindProperty(nameof(HospitalCanvasSheet.Kind))!.GetMaxLength()!.Value;
			Assert.All(HospitalCanvasKind.All, k => Assert.True(k.Length <= kind, $"'{k}' is longer than Kind ({kind})"));
			var s = HospitalSliceParity.Slice("hospital_h112_patient_pain.sql");
			Assert.Contains("ALTER COLUMN Source nvarchar(" + src + ") NOT NULL", s);   // the database is widened to what the model writes
		}
	}
}
