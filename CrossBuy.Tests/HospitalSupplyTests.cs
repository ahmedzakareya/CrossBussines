using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H32 — ward requisitions, with no database: the suggested quantity, what a unit, a par, a line and an
	// approval may be, how a request moves, the store's average time; and the model against its slice.
	// H33 — the English twins of the free texts a patient reads: the slice only adds nullable columns, each guarded,
	// and every one is a column the model reads.
	// ==========================================================================================
	public class HospitalSupplyTests
	{
		[Theory]
		[InlineData(40, 12, 28)]
		[InlineData(40, 40, 0)]
		[InlineData(40, 55, 0)]       // above par: nothing is suggested, never a negative
		[InlineData(10.5, 0.5, 10)]
		public void The_suggested_quantity_is_par_less_what_the_ward_holds(decimal par, decimal onWard, decimal suggested) =>
			Assert.Equal(suggested, HospitalSupplyRules.Suggest(par, onWard));

		[Fact]
		public void A_unit_has_a_code_a_name_and_two_different_stores()
		{
			Assert.Equal("Enter the unit's code and name", HospitalSupplyRules.UnitRefusal("", "Ward 3", 1, 2));
			Assert.Equal("Choose the unit's store and the store it draws from", HospitalSupplyRules.UnitRefusal("W3", "Ward 3", 0, 2));
			Assert.Equal("The unit's store and its source are two warehouses", HospitalSupplyRules.UnitRefusal("W3", "Ward 3", 2, 2));
			Assert.Null(HospitalSupplyRules.UnitRefusal("W3", "Ward 3", 1, 2));
		}

		[Fact]
		public void Lines_change_only_on_a_draft_and_with_a_positive_quantity()
		{
			Assert.Null(HospitalSupplyRules.LineRefusal(HospitalRequisitionStatus.Draft, 5));
			Assert.Equal("A quantity is more than zero", HospitalSupplyRules.LineRefusal(HospitalRequisitionStatus.Draft, 0));
			foreach (var s in new[] { HospitalRequisitionStatus.Submitted, HospitalRequisitionStatus.Approved, HospitalRequisitionStatus.Dispatched })
				Assert.Equal("Only a draft request is changed", HospitalSupplyRules.LineRefusal(s, 5));
			Assert.Equal("A par is more than zero", HospitalSupplyRules.ParRefusal(0));
		}

		[Fact]
		public void The_store_approves_while_submitted_zero_to_what_was_asked()
		{
			Assert.Null(HospitalSupplyRules.ApproveRefusal(HospitalRequisitionStatus.Submitted, 28, 28));
			Assert.Null(HospitalSupplyRules.ApproveRefusal(HospitalRequisitionStatus.Submitted, 28, 0));
			Assert.Equal("The store gives at most what was asked", HospitalSupplyRules.ApproveRefusal(HospitalRequisitionStatus.Submitted, 28, 29));
			Assert.Equal("The approved quantity is 0 or more", HospitalSupplyRules.ApproveRefusal(HospitalRequisitionStatus.Submitted, 28, -1));
			Assert.Equal("Quantities are approved on a submitted request", HospitalSupplyRules.ApproveRefusal(HospitalRequisitionStatus.Draft, 28, 5));
		}

		private static string? M(string from, string to, int lines = 2, bool every = true, bool any = true, string? why = null) =>
			HospitalSupplyRules.MoveRefusal(from, to, lines, every, any, why);

		[Fact]
		public void A_request_moves_through_its_steps()
		{
			Assert.Equal("Add at least one item", M(HospitalRequisitionStatus.Draft, HospitalRequisitionStatus.Submitted, lines: 0));
			Assert.Null(M(HospitalRequisitionStatus.Draft, HospitalRequisitionStatus.Submitted));
			Assert.Equal("Approve a quantity on every line (0 for none)", M(HospitalRequisitionStatus.Submitted, HospitalRequisitionStatus.Approved, every: false));
			Assert.Equal("Approve at least one quantity, or cancel the request", M(HospitalRequisitionStatus.Submitted, HospitalRequisitionStatus.Approved, any: false));
			Assert.Null(M(HospitalRequisitionStatus.Submitted, HospitalRequisitionStatus.Approved));
			Assert.Null(M(HospitalRequisitionStatus.Approved, HospitalRequisitionStatus.Picking));
			Assert.Null(M(HospitalRequisitionStatus.Picking, HospitalRequisitionStatus.Dispatched));
			Assert.Equal("Write why the request is cancelled", M(HospitalRequisitionStatus.Submitted, HospitalRequisitionStatus.Cancelled, why: " "));
			Assert.Null(M(HospitalRequisitionStatus.Picking, HospitalRequisitionStatus.Cancelled, why: "Ward closed"));
		}

		[Theory]
		[InlineData(HospitalRequisitionStatus.Draft, HospitalRequisitionStatus.Approved)]
		[InlineData(HospitalRequisitionStatus.Submitted, HospitalRequisitionStatus.Dispatched)]
		[InlineData(HospitalRequisitionStatus.Dispatched, HospitalRequisitionStatus.Cancelled)]   // a posted transfer is not taken back here
		[InlineData(HospitalRequisitionStatus.Cancelled, HospitalRequisitionStatus.Submitted)]
		public void Any_other_move_is_refused(string from, string to) =>
			Assert.Equal("This request cannot make that move", M(from, to, why: "x"));

		[Fact]
		public void The_average_fulfilment_is_from_sent_to_dispatched()
		{
			var t = new DateTime(2026, 11, 4, 8, 0, 0);
			var done = new (DateTime?, DateTime?)[] { (t, t.AddHours(2)), (t, t.AddHours(3)), (null, t) };
			Assert.Equal(2.5, HospitalSupplyRules.AverageHours(done));
			Assert.Null(HospitalSupplyRules.AverageHours(Array.Empty<(DateTime?, DateTime?)>()));
		}

		// ---------------- the model against its slice ----------------

		private static string Slice() => HospitalSliceParity.Slice("hospital_h32_supply.sql");
		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalSupplyModel.Configure);

		public static TheoryData<Type, string> Tables => new()
		{
			{ typeof(HospitalSupplyUnit), "HospitalSupplyUnits" },
			{ typeof(HospitalSupplyPar), "HospitalSupplyPars" },
			{ typeof(HospitalRequisition), "HospitalRequisitions" },
			{ typeof(HospitalRequisitionLine), "HospitalRequisitionLines" },
		};

		[Theory, MemberData(nameof(Tables))]
		public void The_model_names_the_tables_its_slice_creates(Type entity, string table) => HospitalSliceParity.NamesItsTable(Model(), Slice(), entity, table);

		[Theory, MemberData(nameof(Tables))]
		public void Every_column_is_read_in_the_type_its_slice_creates(Type entity, string table) => HospitalSliceParity.ColumnsMatch(Model(), Slice(), entity, table);

		[Fact]
		public void Every_status_and_priority_the_code_knows_the_slice_allows()
		{
			foreach (var s in HospitalRequisitionStatus.All) Assert.Contains($"N'{s}'", Slice());
			foreach (var p in HospitalRequisitionPriority.All) Assert.Contains($"N'{p}'", Slice());
			Assert.DoesNotContain("ALTER TABLE", Slice(), StringComparison.OrdinalIgnoreCase);
		}

		// ---------------- H36: who asks and who gives is the unit's setting ----------------

		[Theory]
		// manage, named, anyoneNamed, holdsDefaultRole -> may
		[InlineData(true, false, true, false, true)]     // the administrator always
		[InlineData(false, true, true, false, true)]     // named for the duty
		[InlineData(false, false, true, true, false)]    // the role no longer decides once someone is named
		[InlineData(false, false, false, true, true)]    // nobody named: the role it defaults to
		[InlineData(false, false, false, false, false)]
		public void A_unit_names_who_may_act_and_until_it_does_the_role_decides(bool manage, bool named, bool anyoneNamed, bool role, bool may) =>
			Assert.Equal(may, HospitalSupplyRules.MayAct(manage, named, anyoneNamed, role));

		[Fact]
		public void Asking_defaults_to_the_ward_role_and_giving_to_the_pharmacy()
		{
			Assert.Equal(CrossBuy.BL.HospitalActions.Admit, HospitalSupplyRules.DefaultRole(HospitalSupplyDuty.Request));
			Assert.Equal(CrossBuy.BL.HospitalActions.Pharmacy, HospitalSupplyRules.DefaultRole(HospitalSupplyDuty.Approve));
		}

		[Fact]
		public void The_duty_table_matches_its_slice_and_allows_exactly_the_duties()
		{
			var sql = HospitalSliceParity.Slice("hospital_h36_supply_duties.sql");
			HospitalSliceParity.NamesItsTable(Model(), sql, typeof(HospitalSupplyUnitPerson), "HospitalSupplyUnitPeople");
			HospitalSliceParity.ColumnsMatch(Model(), sql, typeof(HospitalSupplyUnitPerson), "HospitalSupplyUnitPeople");
			foreach (var d in HospitalSupplyDuty.All) Assert.Contains($"N'{d}'", sql);
			Assert.Equal(new[] { "HospitalSupplyUnits" }, HospitalSliceParity.CheckedPrerequisites(sql));
			Assert.DoesNotContain("ALTER TABLE", sql, StringComparison.OrdinalIgnoreCase);
			var ix = Model().FindEntityType(typeof(HospitalSupplyUnitPerson))!.GetIndexes().Single(i => i.IsUnique);
			Assert.Equal(new[] { "CompanyId", "UnitId", "EmployeeId", "Duty" }, ix.Properties.Select(x => x.Name));
		}

		// ---------------- H33: the English twins ----------------

		[Fact]
		public void The_bilingual_slice_only_adds_guarded_nullable_columns_the_models_read()
		{
			var sql = HospitalSliceParity.Slice("hospital_h33_bilingual_text.sql");
			var adds = System.Text.RegularExpressions.Regex.Matches(sql, @"IF COL_LENGTH\('dbo\.(\w+)', '(\w+)'\) IS NULL ALTER TABLE dbo\.(\w+) ADD (\w+) \w+(?:\(\d+\))? NULL;");
			Assert.Equal(7, adds.Count);
			foreach (System.Text.RegularExpressions.Match m in adds)
			{
				Assert.Equal(m.Groups[1].Value, m.Groups[3].Value);   // the guard asks about the table it alters
				Assert.Equal(m.Groups[2].Value, m.Groups[4].Value);   // ...and the column it adds
			}
			Assert.Equal(7, System.Text.RegularExpressions.Regex.Matches(sql, "ALTER TABLE").Count);   // nothing else is altered
			Assert.DoesNotContain("DROP ", sql, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain("UPDATE ", sql, StringComparison.OrdinalIgnoreCase);
			// every twin is a property the model reads
			foreach (var (t, col) in new (Type, string)[] { (typeof(HospitalRadOrder), "FindingsEn"), (typeof(HospitalRadOrder), "ImpressionEn"), (typeof(HospitalPrescriptionLine), "DrugEn"),
				(typeof(HospitalPrescriptionLine), "InstructionsEn"), (typeof(HospitalDentalFinding), "NoteEn"), (typeof(HospitalPortalRelease), "NoteEn"), (typeof(HospitalPortalRefill), "AnswerEn") })
				Assert.NotNull(t.GetProperty(col));
		}
	}
}
