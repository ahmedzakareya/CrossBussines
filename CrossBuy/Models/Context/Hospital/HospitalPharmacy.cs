using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL PHARMACY (unit H10) — dispensing from the inventory.
	//
	// A signed visit's prescription becomes one DISPENSE (RX-000001) on the pharmacy queue, its lines copied
	// from the prescription. A line names an inventory ITEM (a medicine or consumable the doctor picked) or is
	// free text the hospital does not stock (the patient buys it outside). The pharmacist reviews it
	// (Verified), then dispenses: every stocked line leaves the pharmacy store through the inventory's one
	// stock writer (StockService — nearest expiry first, cost to the ledger) and becomes a charge on the
	// visit's bill at the patient's price. The pharmacy store is the warehouse named in the settings.
	// deploy/sql/hospital_h10_pharmacy.sql builds the tables.
	// ==========================================================================================

	public static class HospitalDispenseStatus
	{
		public const string New = "New";
		public const string Verified = "Verified";
		public const string Dispensed = "Dispensed";
		/// H136: the pharmacist refused it, with the reason; it never reaches dispensing.
		public const string Rejected = "Rejected";
		/// H145: some of it left the store and the rest is owed to the patient (the store was short); dispensing again
		/// gives what is owed. A partial dispense is still a dispense: what left is charged, what is owed is not.
		public const string Partial = "Partial";
		public static readonly IReadOnlyList<string> All = new[] { New, Verified, Dispensed, Rejected, Partial };
		/// The pharmacy can still dispense it: reviewed and not, or not wholly, dispensed.
		public static bool Dispensable(string s) => s is Verified or Partial;
	}

	public static class HospitalDispenseSource
	{
		public const string Outpatient = "Outpatient";
		public const string Inpatient = "Inpatient";
		public const string Emergency = "Emergency";
		/// H120: a chemotherapy cycle's drugs, sent to the pharmacy when the cycle is given.
		public const string Oncology = "Oncology";
	}

	/// The company's pharmacy: which warehouse it dispenses from, and how near an expiry counts as "near".
	public class HospitalPharmacySettings
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int? WarehouseId { get; set; }
		public int NearExpiryDays { get; set; } = 60;
		/// H134: the store a recalled or suspect batch is moved into, out of reach of dispensing (hospital_h134_recalls.sql).
		public int? QuarantineWarehouseId { get; set; }
	}

	public class HospitalDispense
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string RxNo { get; set; } = "";
		public int PatientId { get; set; }
		public int? EncounterId { get; set; }
		public string Source { get; set; } = HospitalDispenseSource.Outpatient;
		public int? PrescribedByEmployeeId { get; set; }
		public int? PractitionerId { get; set; }
		public string Status { get; set; } = HospitalDispenseStatus.New;
		public DateTime PrescribedAt { get; set; }
		public DateTime? VerifiedAt { get; set; }
		public int? VerifiedByEmployeeId { get; set; }
		public string? PharmacistNote { get; set; }
		public DateTime? DispensedAt { get; set; }
		public int? DispensedByEmployeeId { get; set; }
		public int? WarehouseId { get; set; }
		public int? BillId { get; set; }
		/// H136: the pharmacist's priority (HospitalRxPriority; null: what the source implies), and a refusal.
		public string? Priority { get; set; }
		public DateTime? RejectedAt { get; set; }
		public int? RejectedByEmployeeId { get; set; }
		public string? RejectReason { get; set; }
	}

	public class HospitalDispenseLine
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int DispenseId { get; set; }
		public int? PrescriptionLineId { get; set; }
		/// The inventory item, or null for a drug the hospital does not stock (free text).
		public int? ItemId { get; set; }
		public string Drug { get; set; } = "";
		public string? Instructions { get; set; }
		public decimal Quantity { get; set; }
		/// Filled on dispensing: what left the store, at what price, from which batches.
		public decimal? DispensedQty { get; set; }
		public decimal? UnitPrice { get; set; }
		public decimal? UnitCost { get; set; }
		public string? Batches { get; set; }
		public DateTime? NearestExpiry { get; set; }
		public int? BillLineId { get; set; }
		public int SortOrder { get; set; }
	}

	public static class HospitalPharmacyModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalPharmacySettings>(e => e.HasIndex(x => x.CompanyId).IsUnique());
			HospitalDrugModel.Configure(b);   // H133: the drug master hangs off the pharmacy
			HospitalLotRecallModel.Configure(b);   // H134: batch recalls and their quarantine
			HospitalStockDisposalModel.Configure(b);   // H135: what is destroyed or returned to the supplier
			HospitalPharmacyReviewModel.Configure(b);   // H136: priority, refusal and the pharmacist's interventions
			HospitalControlledCountModel.Configure(b);   // H137: blind counts of the controlled drugs and their discrepancies
			HospitalCartFillModel.Configure(b);   // H138: the unit-dose cart fill
			HospitalDeskConfirmationModel.Configure(b);   // H141: the desk confirms tomorrow's appointments
			HospitalPatientDepositModel.Configure(b);   // H143: the patient's deposits taken at the counter
			HospitalPatientAllergyModel.Configure(b);   // H270: the patient's allergies as a coded list
			HospitalDispenseFillModel.Configure(b);   // H145: each time a dispense line leaves the store (partial dispensing)
			HospitalDispenseReturnModel.Configure(b);   // H146: medicine the patient brings back
			HospitalCreditNoteModel.Configure(b);       // H148: credit notes on a closed bill
			HospitalDispenseScanModel.Configure(b);     // H150: packs scanned at the counter (GS1)
			HospitalFormularyRequestModel.Configure(b); // H152: formulary governance (the committee's decisions)
			HospitalCompoundingModel.Configure(b);      // H153: compounding batch records
			HospitalDeskHandoffModel.Configure(b);      // H158: the desk's handoffs (why they came, where they were sent)
			HospitalCheckinGapModel.Configure(b);        // H159: check-in steps followed up after the counter
			HospitalDeskCheckoutModel.Configure(b);      // H160: checkout at the desk after the consultation
			HospitalClinicDisruptionModel.Configure(b);   // H161: clinic disruptions and the patients they reach
			HospitalVisitCoverageModel.Configure(b);     // H162: insurance verified for each visit
			HospitalPatientContactModel.Configure(b);    // H163: contact preferences, help needed, representatives
			HospitalDeskClaimModel.Configure(b);         // H164: who owns an item of the desk's queues
			HospitalDeskHandoverModel.Configure(b);      // H165: the desk's shift handover
			HospitalRequestKeyModel.Configure(b);        // H166: a desk action sent twice is done once
			HospitalImplantStabilityModel.Configure(b);  // H168: implant stability by protocol
			HospitalClinicalRulesModel.Configure(b);     // H171: the clinical rule register and its approvals
			HospitalClinicalAddendaModel.Configure(b);   // H177: addenda and corrections on locked records
			HospitalResultReviewModel.Configure(b);     // H178: the result follow-up inbox
			HospitalDentalSessionModel.Configure(b);    // H179: the signed dental treatment session
			HospitalCallModel.Configure(b);              // H176: internal calls (the record, never the audio) and their settings
			b.Entity<HospitalDispense>(e =>
			{
				e.HasIndex(x => new { x.CompanyId, x.RxNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.EncounterId }).IsUnique().HasFilter("[EncounterId] IS NOT NULL");
				e.HasIndex(x => new { x.CompanyId, x.Status });
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
			b.Entity<HospitalDispenseLine>(e =>
			{
				foreach (var p in new[] { nameof(HospitalDispenseLine.Quantity), nameof(HospitalDispenseLine.DispensedQty), nameof(HospitalDispenseLine.UnitPrice), nameof(HospitalDispenseLine.UnitCost) })
					e.Property(p).HasPrecision(18, 4);
				e.HasOne<HospitalDispense>().WithMany().HasForeignKey(x => x.DispenseId).OnDelete(DeleteBehavior.NoAction);
				e.HasIndex(x => new { x.CompanyId, x.DispenseId });
			});
		}
	}
}
