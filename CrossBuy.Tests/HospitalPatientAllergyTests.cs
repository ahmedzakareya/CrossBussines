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
	// HOSPITAL H270 — the patient's allergies as a coded list. What must never happen:
	//   · a coded drug allergy whose printed text does not meet the drugs of its family;
	//   · "no known allergy" beside an allergy, an unknown code, reaction or severity, the same allergy twice;
	//   · an allergy taken off the list deleted (its row stays, marked removed);
	//   · a file written before the list losing an allergy when it is opened in the form;
	//   · the rows staying behind when two files are merged.
	// ==========================================================================================
	public class HospitalPatientAllergyTests
	{
		private static BusinessContext Ctx(int company = 1) => new() { CompanyId = company, EmployeeId = 7, UserId = "u7", Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() };

		[Theory]
		[InlineData("DRUG-PEN", "Amoxicillin 500 mg")]
		[InlineData("DRUG-CEPH", "Ceftriaxone 1 g")]
		[InlineData("DRUG-SULFA", "Co-trimoxazole 960 mg")]
		[InlineData("DRUG-ASA", "Ibuprofen 400 mg")]
		[InlineData("DRUG-NSAID", "Diclofenac 50 mg")]
		[InlineData("DRUG-OPIOID", "Codeine 30 mg")]
		[InlineData("DRUG-MACRO", "Azithromycin 500 mg")]
		[InlineData("DRUG-QUIN", "Ciprofloxacin 500 mg")]
		[InlineData("DRUG-TETRA", "Doxycycline 100 mg")]
		public void A_coded_drug_allergy_meets_the_drugs_of_its_family(string code, string drug)
		{
			var text = HospitalAllergenCatalog.Describe(new[] { new HospitalAllergyItem { Code = code, Reaction = "Rash" } });
			Assert.Single(HospitalAllergyMatch.Hits(drug, text));
			Assert.Empty(HospitalAllergyMatch.Hits("Paracetamol 500 mg", text));
		}

		[Fact]
		public void Every_drug_entry_is_tested_and_the_contrast_entry_reaches_radiology()
		{
			var tested = new[] { "DRUG-PEN", "DRUG-CEPH", "DRUG-SULFA", "DRUG-ASA", "DRUG-NSAID", "DRUG-OPIOID", "DRUG-MACRO", "DRUG-QUIN", "DRUG-TETRA", "DRUG-IODINE" };
			Assert.Equal(tested.OrderBy(x => x), HospitalAllergenCatalog.All.Where(a => a.Kind == HospitalAllergenCatalog.Drug).Select(a => a.Code).OrderBy(x => x));
			Assert.True(HospitalRadiologyService.ContrastAlert(true, HospitalAllergenCatalog.Describe(new[] { new HospitalAllergyItem { Code = "DRUG-IODINE" } })));
		}

		[Fact]
		public void The_printed_text_reads_in_english_and_french()
		{
			var text = HospitalAllergenCatalog.Describe(new[] { new HospitalAllergyItem { Code = "DRUG-PEN", Reaction = "Anaphylaxis" }, new HospitalAllergyItem { Code = "FOOD-SESAME" } });
			Assert.Equal("بنسلين (صدمة تحسسية)\nسمسم", text);
			Assert.Equal("Penicillin (anaphylaxis), Sesame", HospitalAllergyText.Display(text, "en"));
			foreach (var a in HospitalAllergenCatalog.All) Assert.NotEqual(a.Ar, HospitalAllergyText.Display(a.Ar, "en"));   // every name translates
		}

		[Fact]
		public void The_list_is_refused_when_it_contradicts_itself()
		{
			List<HospitalAllergyItem> L(params HospitalAllergyItem[] i) => i.ToList();
			Assert.Null(HospitalAllergenCatalog.Refusal(L(new HospitalAllergyItem { Code = "DRUG-PEN", Reaction = "Rash", Severity = "Severe" }, new HospitalAllergyItem { Text = "Kiwi" })));
			Assert.Null(HospitalAllergenCatalog.Refusal(L(new HospitalAllergyItem { Code = "NKA" })));
			Assert.NotNull(HospitalAllergenCatalog.Refusal(L(new HospitalAllergyItem { Code = "NKA" }, new HospitalAllergyItem { Code = "DRUG-PEN" })));
			Assert.NotNull(HospitalAllergenCatalog.Refusal(L(new HospitalAllergyItem { Code = "DRUG-XYZ" })));
			Assert.NotNull(HospitalAllergenCatalog.Refusal(L(new HospitalAllergyItem { Code = "DRUG-PEN", Reaction = "Sneezing" })));
			Assert.NotNull(HospitalAllergenCatalog.Refusal(L(new HospitalAllergyItem { Code = "DRUG-PEN", Severity = "Deadly" })));
			Assert.NotNull(HospitalAllergenCatalog.Refusal(L(new HospitalAllergyItem { Code = "DRUG-PEN" }, new HospitalAllergyItem { Code = "DRUG-PEN", Reaction = "Rash" })));
			Assert.NotNull(HospitalAllergenCatalog.Refusal(L(new HospitalAllergyItem { Text = "الكيوي" }, new HospitalAllergyItem { Text = "كيوي" })));   // the same word, folded
			Assert.NotNull(HospitalAllergenCatalog.Refusal(L(new HospitalAllergyItem { Text = "x" })));
		}

		[Fact]
		public void Text_written_before_the_list_is_read_into_it()
		{
			var items = HospitalAllergenCatalog.FromText("البنسلين (طفح جلدي)\nفول سوداني، Kiwi (itching)\nشيء غريب (عطس)");
			Assert.Equal(new[] { "DRUG-PEN", "FOOD-PEANUT", null, null }, items.Select(i => i.Code));
			Assert.Equal("Rash", items[0].Reaction);
			Assert.Equal(("Kiwi", "Itching"), (items[2].Text, items[2].Reaction));
			Assert.Equal("شيء غريب (عطس)", items[3].Text);   // an unknown reaction stays in the words, never dropped
		}

		[Fact]
		public async Task The_file_keeps_the_list_and_prints_it_and_a_removed_allergy_keeps_its_row()
		{
			using var host = new PlatformTestHost();
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			var svc = new HospitalPatientService(host.Db, access);
			var input = new HospitalPatientInput { Name = "م اختبار", NameEn = "Allergy Test", Sex = "M",
				AllergyItemsJson = "[{\"code\":\"DRUG-PEN\",\"reaction\":\"Rash\",\"severity\":\"Severe\"},{\"text\":\"Kiwi\"}]" };
			var saved = await svc.SaveAsync(Ctx(), input, 7, DateTime.UtcNow);
			Assert.True(saved.Ok, saved.Error);
			var id = saved.Id!.Value;
			Assert.Equal("بنسلين (طفح جلدي)\nKiwi", (await host.Db.HospitalPatients.AsNoTracking().SingleAsync(p => p.ID == id)).Allergies);

			// the penicillin row keeps its id (severity changed); Kiwi is taken off and stays, removed
			var penId = (await host.Db.Set<HospitalPatientAllergy>().AsNoTracking().SingleAsync(a => a.AllergenCode == "DRUG-PEN")).ID;
			input.Id = id; input.AllergyItemsJson = "[{\"code\":\"DRUG-PEN\",\"reaction\":\"Rash\",\"severity\":\"Moderate\"}]";
			Assert.True((await svc.SaveAsync(Ctx(), input, 7, DateTime.UtcNow)).Ok);
			var rows = await host.Db.Set<HospitalPatientAllergy>().AsNoTracking().Where(a => a.PatientId == id).ToListAsync();
			Assert.Equal(2, rows.Count);
			Assert.Equal(("Moderate", (DateTime?)null), (rows.Single(a => a.ID == penId).Severity, rows.Single(a => a.ID == penId).RemovedAt));
			Assert.NotNull(rows.Single(a => a.Text == "Kiwi").RemovedAt);

			// no known allergy: the text is empty, the form opens on it
			input.AllergyItemsJson = "[{\"code\":\"NKA\"}]";
			Assert.True((await svc.SaveAsync(Ctx(), input, 7, DateTime.UtcNow)).Ok);
			var p = await host.Db.HospitalPatients.AsNoTracking().SingleAsync(x => x.ID == id);
			Assert.Null(p.Allergies);
			Assert.Equal(new[] { "NKA" }, (await svc.AllergyItemsAsync(Ctx(), p)).Select(i => i.Code));

			// a contradiction is refused and nothing changes
			input.AllergyItemsJson = "[{\"code\":\"NKA\"},{\"code\":\"DRUG-PEN\"}]";
			Assert.False((await svc.SaveAsync(Ctx(), input, 7, DateTime.UtcNow)).Ok);
			Assert.Equal(new[] { "NKA" }, (await svc.AllergyItemsAsync(Ctx(), p)).Select(i => i.Code));
		}

		[Fact]
		public async Task A_file_written_as_text_opens_as_a_list_and_text_written_since_is_not_lost()
		{
			using var host = new PlatformTestHost();
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			var svc = new HospitalPatientService(host.Db, access);
			var id = (await svc.SaveAsync(Ctx(), new HospitalPatientInput { Name = "م قديم", NameEn = "Old File", Sex = "F", Allergies = "بنسلين\nلاتكس" }, 7, DateTime.UtcNow)).Id!.Value;
			var p = await host.Db.HospitalPatients.AsNoTracking().SingleAsync(x => x.ID == id);
			Assert.Equal(new[] { "DRUG-PEN", "ENV-LATEX" }, (await svc.AllergyItemsAsync(Ctx(), p)).Select(i => i.Code));

			// saved as a list, then the kiosk appends a line to the text: the form shows both
			Assert.True((await svc.SaveAsync(Ctx(), new HospitalPatientInput { Id = id, Name = "م قديم", NameEn = "Old File", Sex = "F", AllergyItemsJson = "[{\"code\":\"DRUG-PEN\"}]" }, 7, DateTime.UtcNow)).Ok);
			var row = await host.Db.HospitalPatients.SingleAsync(x => x.ID == id);
			row.Allergies += "\nسمسم"; await host.Db.SaveChangesAsync();
			Assert.Equal(new[] { "DRUG-PEN", "FOOD-SESAME" }, (await svc.AllergyItemsAsync(Ctx(), row)).Select(i => i.Code));
		}

		[Fact]
		public void The_rows_follow_the_patient_and_match_their_slice()
		{
			Assert.Contains(HospitalPatientMergeRules.PatientTables, t => t.Entity == typeof(HospitalPatientAllergy));
			var m = HospitalSliceParity.Model(HospitalPatientAllergyModel.Configure);
			var s = HospitalSliceParity.Slice("hospital_h270_patient_allergies.sql");
			HospitalSliceParity.NamesItsTable(m, s, typeof(HospitalPatientAllergy), "HospitalPatientAllergies");
			HospitalSliceParity.ColumnsMatch(m, s, typeof(HospitalPatientAllergy), "HospitalPatientAllergies");
		}
	}
}
