using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	public static class HospitalFhirMessageKind
	{
		public const string Eligibility = "Eligibility", Preauth = "Preauth";
		public static readonly IReadOnlyList<string> All = new[] { Eligibility, Preauth };
	}

	// ==========================================================================================
	// HOSPITAL H242 — FHIR eligibility and pre-authorisation with the payer.
	//   · A payer's GATEWAY is entered by the administrator when the payer (or the ministry's exchange) gives it: the FHIR
	//     base address, the header that carries the key, and the key. Until then — or when it is switched off — every
	//     message is a file: downloaded, sent by the payer's own portal, and the answer file loaded back. Nothing leaves
	//     the building until an administrator has entered and enabled the gateway.
	//   · Every MESSAGE is kept as it went and as it came (CoverageEligibilityRequest / Response; Claim with use
	//     "preauthorization" / ClaimResponse), with its outcome. A pre-authorisation answer is applied through the same
	//     path as an answer typed by hand (HospitalInsuranceService.DecidePreauthAsync).
	// deploy/sql/hospital_h242_fhir_eligibility.sql builds the tables.
	// ==========================================================================================
	public class HospitalPayerGateway
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int PayerId { get; set; }
		public string BaseUrl { get; set; } = "";
		public string? KeyHeader { get; set; }
		public string? ApiKey { get; set; }
		public bool Enabled { get; set; }
		public DateTime UpdatedAt { get; set; }
		public int? UpdatedByEmployeeId { get; set; }
	}

	public class HospitalFhirMessage
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Kind { get; set; } = HospitalFhirMessageKind.Eligibility;
		public int PatientId { get; set; }
		public int PayerId { get; set; }
		public int? PreauthId { get; set; }
		public string Request { get; set; } = "";
		public string? Response { get; set; }
		/// Eligibility: InForce / NotInForce; pre-authorisation: Approved / Rejected / Pending; Error when the payer failed.
		public string? Outcome { get; set; }
		public string? Detail { get; set; }
		public bool Sent { get; set; }
		public DateTime At { get; set; }
		public DateTime? AnsweredAt { get; set; }
		public int? ByEmployeeId { get; set; }
	}

	public static class HospitalPayerGatewayModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalPayerGateway>(e =>
			{
				e.ToTable("HospitalPayerGateways");
				e.Property(x => x.BaseUrl).HasMaxLength(300); e.Property(x => x.KeyHeader).HasMaxLength(60); e.Property(x => x.ApiKey).HasMaxLength(500);
				e.HasIndex(x => new { x.CompanyId, x.PayerId }).IsUnique();
			});
			b.Entity<HospitalFhirMessage>(e =>
			{
				e.ToTable("HospitalFhirMessages");
				e.Property(x => x.Kind).HasMaxLength(12); e.Property(x => x.Outcome).HasMaxLength(12); e.Property(x => x.Detail).HasMaxLength(500);
				e.HasIndex(x => new { x.CompanyId, x.PatientId, x.At });
				e.HasIndex(x => new { x.CompanyId, x.PreauthId });
			});
		}
	}
}
