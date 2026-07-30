using System.ComponentModel.DataAnnotations.Schema;

namespace CrossBuy.Models.Context.Accounting
{
	// Projects & Contracting — P3 Execution / progress measurement.
	// OPERATIONAL ONLY: no GL, no journal entries. Each record is a DATED CUMULATIVE snapshot of executed
	// quantity per BOQ item. Basis for progress billing (المستخلصات, P4) — period delta = this cumulative −
	// the previous measurement's cumulative. Attribution stays via ProjectId; flexible; nothing posted.
	public class ProjectProgress
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int ProjectId { get; set; }                 // → Project (P0)
		public int MeasurementNo { get; set; }             // sequence per project (1,2,3…)
		public DateTime MeasurementDate { get; set; }      // date of the field measurement/حصر
		public string? Note { get; set; }
		public string Status { get; set; } = "Draft";      // Draft (editable) / Confirmed (frozen snapshot)
		public decimal OverallPercent { get; set; }        // snapshot: value-weighted Σ executedValue ÷ Σ boqValue × 100
		public decimal ExecutedValue { get; set; }         // snapshot: Σ executed value (capped at BOQ value per line)
		public DateTime? CreatedAt { get; set; }
		public int? CreatedBy { get; set; }

		public List<ProjectProgressLine> Lines { get; set; } = new();
	}

	public class ProjectProgressLine
	{
		public int ID { get; set; }
		public int ProgressId { get; set; }                // → ProjectProgress
		public int? BoqItemId { get; set; }                // → BoqItem (P1). null = whole-project manual % (project w/o BOQ)
		public decimal CumulativeQty { get; set; }         // cumulative executed quantity to date
		public decimal? ManualPercent { get; set; }        // lump-sum override (0..100); when set, % is manual not qty-driven
		public string? Note { get; set; }

		[ForeignKey(nameof(ProgressId))]
		public ProjectProgress? Progress { get; set; }
	}
}
