using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// HOSPITAL H210 — a payer's CLAIM SHEET: the columns (in order, each with the header that payer's sheet uses), the
	// separator and the date format of the spreadsheet a third-party administrator takes the batch's claims in. One per
	// payer; a payer without one gets the standard sheet.
	public class HospitalPayerClaimSheet
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int PayerId { get; set; }
		/// "Key=Header" pairs separated by '|', in column order (a key from HospitalClaimSheetFields).
		public string Columns { get; set; } = "";
		/// "," ";" or "TAB".
		public string Delimiter { get; set; } = ",";
		public string DateFormat { get; set; } = "yyyy-MM-dd";
		public DateTime UpdatedAt { get; set; }
		public int? UpdatedByEmployeeId { get; set; }
	}

	public static class HospitalClaimSheetModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalPayerClaimSheet>(e =>
			{
				e.ToTable("HospitalPayerClaimSheets");
				e.Property(x => x.Columns).HasMaxLength(2000);
				e.Property(x => x.Delimiter).HasMaxLength(3);
				e.Property(x => x.DateFormat).HasMaxLength(20);
				e.HasIndex(x => new { x.CompanyId, x.PayerId }).IsUnique();
			});
		}
	}
}
