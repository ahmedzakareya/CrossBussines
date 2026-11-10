using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H57 — merging duplicate patient files, with no database. The guard that matters most: the merge list
	// names EVERY entity that carries a patient (a new table with a PatientId that is not added would be left pointing
	// at the closed file — its rows silently orphaned). Then the suspicion rules, the name normalising, the union of
	// allergies, the refusals, and the merge table against its slice.
	// ==========================================================================================
	public class HospitalPatientMergeTests
	{
		[Fact]
		public void The_merge_moves_every_entity_that_carries_a_patient_except_the_access_log()
		{
			var carrying = typeof(HospitalPatient).Assembly.GetTypes()
				.Where(t => t.IsClass && !t.IsAbstract && t.Namespace == typeof(HospitalPatient).Namespace
					&& t.GetProperty("PatientId") is { } p && (p.PropertyType == typeof(int) || p.PropertyType == typeof(int?)))
				.ToHashSet();
			carrying.Remove(typeof(HospitalPatientAccessLog));   // who opened which file is history: it stays true
			var listed = HospitalPatientMergeRules.PatientTables.Select(t => t.Entity).ToHashSet();
			Assert.Empty(carrying.Except(listed).Select(t => t.Name));
			Assert.Empty(listed.Except(carrying).Select(t => t.Name));
			Assert.Equal(listed.Count, HospitalPatientMergeRules.PatientTables.Select(t => t.Table).Distinct().Count());
			Assert.All(HospitalPatientMergeRules.PatientTables, t => Assert.Matches("^Hospital[A-Za-z]+$", t.Table));   // the names go into SQL: letters only
		}

		[Fact]
		public void Every_other_column_that_names_a_patient_is_moved_too()
		{
			// a patient can be named by a column of another name (a birth's mother and newborn): every int column ending
			// in PatientId, other than PatientId itself and the merge's own record-keeping, must be in OtherPatientColumns
			var notCare = new HashSet<string> { "MergedIntoPatientId", "SurvivorPatientId", "MergedPatientId" };
			var found = typeof(HospitalPatient).Assembly.GetTypes()
				.Where(t => t.IsClass && !t.IsAbstract && t.Namespace == typeof(HospitalPatient).Namespace)
				.SelectMany(t => t.GetProperties().Where(p => p.Name.EndsWith("PatientId") && p.Name != "PatientId" && !notCare.Contains(p.Name)
					&& (p.PropertyType == typeof(int) || p.PropertyType == typeof(int?))).Select(p => (t, p.Name)))
				.ToHashSet();
			var listed = HospitalPatientMergeRules.OtherPatientColumns.Select(x => (x.Entity, x.Column)).ToHashSet();
			Assert.Empty(found.Except(listed).Select(x => x.Item1.Name + "." + x.Item2));
			Assert.Empty(listed.Except(found).Select(x => x.Item1.Name + "." + x.Item2));
			Assert.All(HospitalPatientMergeRules.OtherPatientColumns, x => { Assert.Matches("^Hospital[A-Za-z]+$", x.Table); Assert.Matches("^[A-Za-z]+PatientId$", x.Column); });
		}

		[Theory]
		[InlineData("أحمد محمود", "احمد  محمود")]
		[InlineData("فاطمة الزهراء", "فاطمه الزهراء")]
		[InlineData("مُحَمَّد", "محمد")]
		[InlineData("Ahmed Mahmoud", "ahmed-mahmoud")]
		[InlineData("مصطفى", "مصطفي")]
		public void Two_spellings_of_one_name_compare_equal(string a, string b) => Assert.Equal(HospitalPatientMergeRules.Normalise(a), HospitalPatientMergeRules.Normalise(b));

		private static HospitalPatient P(int id, string name, string? phone, DateTime? birth, string? nameEn = null) => new() { ID = id, Name = name, NameEn = nameEn, Phone = phone, BirthDate = birth };

		[Fact]
		public void A_pair_is_suspected_by_mobile_and_birth_or_by_name_and_birth()
		{
			var b = new DateTime(1980, 5, 1);
			Assert.Contains("SamePhoneAndBirth", HospitalPatientMergeRules.Reasons(P(1, "أ", "+965 5000 1234", b), P(2, "ب", "50001234", b)));
			Assert.Contains("SameNameAndBirth", HospitalPatientMergeRules.Reasons(P(1, "فاطمة محمد", null, b), P(2, "فاطمه محمد", null, b)));
			Assert.Contains("SameEnglishNameAndBirth", HospitalPatientMergeRules.Reasons(P(1, "أ", null, b, "Sara Ali"), P(2, "ب", null, b, "sara  ali")));
			Assert.Contains("SamePhoneAndName", HospitalPatientMergeRules.Reasons(P(1, "سارة علي", "50001234", null), P(2, "ساره علي", "50001234", b)));
			Assert.Empty(HospitalPatientMergeRules.Reasons(P(1, "سارة علي", "50001234", b), P(2, "منى علي", "50009999", b)));   // the same day is not enough
			Assert.Empty(HospitalPatientMergeRules.Reasons(P(1, "سارة", null, null), P(2, "سارة", null, null)));               // the same name is not enough
		}

		[Fact]
		public void Allergies_of_both_files_are_kept_each_once()
		{
			Assert.Equal("Penicillin\nLatex", HospitalPatientMergeRules.Union("Penicillin", "penicillin\nLatex"));
			Assert.Equal("Latex", HospitalPatientMergeRules.Union(null, "Latex"));
			Assert.Null(HospitalPatientMergeRules.Union(" ", null));
		}

		[Fact]
		public void A_merge_needs_two_files_and_a_reason()
		{
			Assert.Equal("Choose two different files", HospitalPatientMergeRules.MergeRefusal("same person", 3, 3));
			Assert.Equal("Write why these two files are one patient", HospitalPatientMergeRules.MergeRefusal("dup", 3, 4));
			Assert.Null(HospitalPatientMergeRules.MergeRefusal("Same national ID on the scanned card", 3, 4));
		}

		[Fact]
		public void The_merge_table_and_the_patient_columns_match_the_slice()
		{
			var s = HospitalSliceParity.Slice("hospital_h57_patient_merge.sql");
			var m = HospitalSliceParity.Model(HospitalPatientsModel.Configure);
			HospitalSliceParity.NamesItsTable(m, s, typeof(HospitalPatientMerge), "HospitalPatientMerges");
			HospitalSliceParity.ColumnsMatch(m, s, typeof(HospitalPatientMerge), "HospitalPatientMerges");
			Assert.Contains("ALTER TABLE dbo.HospitalPatients ADD MergedIntoPatientId int NULL;", s);
			Assert.Contains("ALTER TABLE dbo.HospitalPatients ADD MergedAt datetime2 NULL;", s);
			Assert.Equal(new[] { "MergedPatientId" }, m.FindEntityType(typeof(HospitalPatientMerge))!.GetIndexes().Single(i => i.IsUnique).Properties.Select(x => x.Name));
			Assert.DoesNotContain("DROP ", s, StringComparison.OrdinalIgnoreCase);
		}
	}
}
