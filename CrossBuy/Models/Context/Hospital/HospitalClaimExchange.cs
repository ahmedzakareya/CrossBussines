using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	public static class HospitalClaimExchangeDirection
	{
		public const string Sent = "Sent";
		public const string Received = "Received";
		public static readonly IReadOnlyList<string> All = new[] { Sent, Received };
	}

	// HOSPITAL H200 — every electronic file of a claim batch, kept as it went or came: the claims sent to the payer
	// (FHIR R4 Claim) and the payer's answers received (FHIR R4 ClaimResponse), with what the answer changed.
	public class HospitalClaimExchange
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int BatchId { get; set; }
		public string Direction { get; set; } = HospitalClaimExchangeDirection.Sent;
		public string Format { get; set; } = "FHIR-R4";
		public string Raw { get; set; } = "";
		/// Received: lines whose answer was recorded, and lines left as they were (already answered, unknown, refused).
		public int Applied { get; set; }
		public int Skipped { get; set; }
		public string? Detail { get; set; }
		public DateTime At { get; set; }
		public int? ByEmployeeId { get; set; }
	}

	public static class HospitalClaimExchangeModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalClaimExchange>(e =>
			{
				e.ToTable("HospitalClaimExchanges");   // no DbSet: it names its table
				e.Property(x => x.Direction).HasMaxLength(10);
				e.Property(x => x.Format).HasMaxLength(20);
				e.Property(x => x.Detail).HasMaxLength(1000);
				e.HasIndex(x => new { x.CompanyId, x.BatchId });
			});
		}
	}
}
