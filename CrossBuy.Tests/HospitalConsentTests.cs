using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Reporting;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H60 — consents with an electronic signature, with no database: who may sign (a minor never signs for
	// themselves; a representative says who and why), the witness is not the clinician, a procedure consent names the
	// procedure, the signature is a real PNG, the text is filled in, the standard forms are valid and cover every kind,
	// the tables against their slice, the signature document is never replaced, and the Report Studio paper.
	// ==========================================================================================
	public class HospitalConsentTests
	{
		private const int Me = 7, Witness = 9;
		private static readonly string Png = "data:image/png;base64," + Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 });
		private static HospitalConsentForm Form(string kind = HospitalConsentKind.Procedure) => new() { ID = 1, Kind = kind, IsActive = true, Body = "x", BodyEn = "x", Title = "t", TitleEn = "t" };
		private static HospitalConsentInput Ok() => new()
		{
			FormId = 1, Language = "ar", ProcedureName = "Appendectomy", Decision = HospitalConsentDecision.Given, SignerKind = HospitalConsentSigner.Patient,
			WitnessEmployeeId = Witness, Signature = Png,
		};

		[Fact]
		public void A_well_formed_consent_is_accepted()
		{
			Assert.Null(HospitalConsentRules.SignRefusal(Ok(), Form(), 40, false, Me));
			var refused = Ok(); refused.Decision = HospitalConsentDecision.Refused;
			Assert.Null(HospitalConsentRules.SignRefusal(refused, Form(), 40, false, Me));
		}

		[Fact]
		public void The_form_must_be_in_use_and_the_patient_alive()
		{
			Assert.Equal("This patient is deceased", HospitalConsentRules.SignRefusal(Ok(), Form(), 40, true, Me));
			Assert.Equal("Choose the consent form", HospitalConsentRules.SignRefusal(Ok(), null, 40, false, Me));
			var off = Form(); off.IsActive = false;
			Assert.Equal("Choose the consent form", HospitalConsentRules.SignRefusal(Ok(), off, 40, false, Me));
			var i = Ok(); i.Language = "fr"; Assert.Equal("Choose the language the signer read", HospitalConsentRules.SignRefusal(i, Form(), 40, false, Me));
		}

		[Fact]
		public void A_procedure_consent_names_the_procedure_but_a_general_one_need_not()
		{
			var i = Ok(); i.ProcedureName = " ";
			Assert.Equal("Write the procedure the consent is for", HospitalConsentRules.SignRefusal(i, Form(HospitalConsentKind.Anaesthesia), 40, false, Me));
			Assert.Null(HospitalConsentRules.SignRefusal(i, Form(HospitalConsentKind.General), 40, false, Me));
			Assert.True(HospitalConsentKind.NamesAProcedure(HospitalConsentKind.Transfusion));
			Assert.False(HospitalConsentKind.NamesAProcedure(HospitalConsentKind.AgainstAdvice));
		}

		[Fact]
		public void A_minor_never_signs_and_a_representative_says_who_and_why()
		{
			Assert.Equal("A minor's consent is signed by a parent or guardian", HospitalConsentRules.SignRefusal(Ok(), Form(), 17, false, Me));
			var r = Ok(); r.SignerKind = HospitalConsentSigner.Representative;
			Assert.Equal("Give the representative's name and relation to the patient", HospitalConsentRules.SignRefusal(r, Form(), 17, false, Me));
			r.SignerName = "Mona"; r.SignerRelation = "Mother";
			Assert.Equal("Choose why the representative signs", HospitalConsentRules.SignRefusal(r, Form(), 17, false, Me));
			r.ProxyReason = HospitalConsentProxyReason.Minor;
			Assert.Null(HospitalConsentRules.SignRefusal(r, Form(), 17, false, Me));
		}

		[Fact]
		public void The_witness_is_someone_else_and_there_is_a_signature()
		{
			var i = Ok(); i.WitnessEmployeeId = null; Assert.Equal("Choose the witness", HospitalConsentRules.SignRefusal(i, Form(), 40, false, Me));
			i = Ok(); i.WitnessEmployeeId = Me; Assert.Equal("The witness must be someone other than the clinician who explained", HospitalConsentRules.SignRefusal(i, Form(), 40, false, Me));
			i = Ok(); i.Signature = ""; Assert.Equal("Sign in the box", HospitalConsentRules.SignRefusal(i, Form(), 40, false, Me));
		}

		[Fact]
		public void Only_a_png_data_url_is_a_signature_and_its_fingerprint_is_sha256()
		{
			var bytes = HospitalConsentRules.SignaturePng(Png);
			Assert.NotNull(bytes);
			Assert.Null(HospitalConsentRules.SignaturePng("data:image/jpeg;base64,/9j/4AAQ"));
			Assert.Null(HospitalConsentRules.SignaturePng("data:image/png;base64,not-base64!"));
			Assert.Null(HospitalConsentRules.SignaturePng("data:image/png;base64," + Convert.ToBase64String(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5 })));
			Assert.Null(HospitalConsentRules.SignaturePng("data:image/png;base64," + Convert.ToBase64String(new byte[HospitalConsentRules.MaxSignatureBytes + 10])));
			var sha = HospitalConsentRules.Sha256(bytes!);
			Assert.Equal(64, sha.Length);
			Assert.Equal(sha.ToLowerInvariant(), sha);
		}

		[Fact]
		public void Only_a_signed_procedure_consent_in_force_on_that_case_covers_it()
		{
			HospitalConsent C(Action<HospitalConsent> f) { var c = new HospitalConsent { CompanyId = 1, SurgeryId = 5, Kind = HospitalConsentKind.Procedure, Decision = HospitalConsentDecision.Given, SignatureDocumentId = 9 }; f(c); return c; }
			bool Covers(HospitalConsent c) => HospitalConsentRules.CoveringCase(new[] { c }.AsQueryable(), 1, 5).Any();
			Assert.True(Covers(C(_ => { })));
			Assert.False(Covers(C(c => c.WithdrawnAt = DateTime.UtcNow)));
			Assert.False(Covers(C(c => c.Decision = HospitalConsentDecision.Refused)));
			Assert.False(Covers(C(c => c.Kind = HospitalConsentKind.Anaesthesia)));
			Assert.False(Covers(C(c => c.SignatureDocumentId = null)));   // the instant before its signature is stored
			Assert.False(Covers(C(c => c.SurgeryId = 6)));
			Assert.False(Covers(C(c => c.CompanyId = 2)));
		}

		[Fact]
		public void The_text_is_filled_with_this_patients_facts()
		{
			var t = HospitalConsentRules.Fill("{patient} {mrn} {procedure} {doctor} {date}", "Ali", "MRN-1", null, "Dr Sara", new DateTime(2026, 11, 8, 9, 0, 0));
			Assert.Equal("Ali MRN-1 — Dr Sara 2026-11-08", t);
		}

		[Fact]
		public void The_standard_forms_are_valid_and_cover_every_kind()
		{
			var all = HospitalConsentRules.Standard();
			foreach (var f in all) Assert.Null(HospitalConsentRules.FormRefusal(f));
			Assert.Equal(HospitalConsentKind.All.OrderBy(k => k), all.Select(f => f.Kind!).Distinct().OrderBy(k => k));
			Assert.Equal(all.Count, all.Select(f => f.Code).Distinct().Count());
			// a form that names a procedure says it, in both languages
			foreach (var f in all.Where(f => HospitalConsentKind.NamesAProcedure(f.Kind!))) { Assert.Contains("{procedure}", f.Body); Assert.Contains("{procedure}", f.BodyEn); }
			Assert.Equal("Give the form a code of up to 30 characters", HospitalConsentRules.FormRefusal(new HospitalConsentFormInput { Code = new string('A', 31) }));
		}

		[Fact]
		public void The_tables_match_their_slice()
		{
			var s = HospitalSliceParity.Slice("hospital_h60_consents.sql");
			var m = HospitalSliceParity.Model(HospitalConsentModel.Configure);
			HospitalSliceParity.NamesItsTable(m, s, typeof(HospitalConsentForm), "HospitalConsentForms");
			HospitalSliceParity.ColumnsMatch(m, s, typeof(HospitalConsentForm), "HospitalConsentForms");
			HospitalSliceParity.NamesItsTable(m, s, typeof(HospitalConsent), "HospitalConsents");
			HospitalSliceParity.ColumnsMatch(m, s, typeof(HospitalConsent), "HospitalConsents");
			foreach (var x in HospitalConsentKind.All.Concat(HospitalConsentDecision.All).Concat(HospitalConsentSigner.All).Concat(HospitalConsentProxyReason.All)) Assert.Contains("'" + x + "'", s);
			Assert.Contains("CHECK (WitnessEmployeeId <> ExplainedByEmployeeId)", s);
			Assert.Contains("Decision = 'Given'))", s);   // only a consent given can be withdrawn
			Assert.Equal(new[] { "HospitalPatients" }, HospitalSliceParity.CheckedPrerequisites(s));
		}

		[Fact]
		public void The_signature_is_a_document_on_the_consent_never_replaced_or_deleted()
		{
			var r = new HospitalConsentDocumentOwnerResolver(null!);
			Assert.Equal(EntityRegistry.HospitalConsent, r.EntityType);
			Assert.Equal(HospitalActions.Read, r.ModuleActionFor(DocumentAction.View));
			Assert.Equal(HospitalActions.Consult, r.ModuleActionFor(DocumentAction.Upload));
			Assert.Equal("hospital-document-unsupported", r.ModuleActionFor(DocumentAction.Replace));
			Assert.Equal("hospital-document-unsupported", r.ModuleActionFor(DocumentAction.Delete));
		}

		[Fact]
		public void The_consent_prints_only_through_report_studio_and_moves_in_a_merge()
		{
			var d = HospitalDocumentDatasets.All().Single(x => x.DatasetCode == HospitalDocumentDatasetCodes.Consent);
			Assert.Contains(d.Parameters, p => p.Key == "ConsentId" && p.Required);
			foreach (var k in new[] { "Title", "ConsentText", "Decision", "SignerName", "ExplainedBy", "Witness", "SignedAt", "SignatureFingerprint", "PatientName", "DocumentNo" }) Assert.Contains(d.Fields, f => f.Key == k);
			Assert.Contains(ReportScreenBindings.For(ReportScreenKeys.HospitalConsent), b => b.ReportCode == HospitalDocumentDatasetCodes.Consent && b.IdParameter == "ConsentId");
			Assert.Contains(HospitalPatientMergeRules.PatientTables, t => t.Entity == typeof(HospitalConsent));
		}
	}
}
