using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	public static class HospitalFeedKind
	{
		/// Admissions, transfers, discharges (ADT^A01 / A02 / A03).
		public const string Adt = "ADT";
		/// Verified laboratory results (ORU^R01).
		public const string Oru = "ORU";
		public static readonly IReadOnlyList<string> All = new[] { Adt, Oru };
	}

	public static class HospitalFeedStatus
	{
		public const string Queued = "Queued", Sent = "Sent", Error = "Error";
	}

	// ==========================================================================================
	// HOSPITAL H247 — OUTBOUND HL7 v2 FEEDS to other systems (a data warehouse, a regional record, a specialist system).
	//   · A FEED is entered by the administrator: what it carries (ADT or ORU), where (MLLP host and port), the receiving
	//     application and facility. It is off until enabled — nothing leaves the building before that.
	//   · Every MESSAGE is queued once per event (the event key is unique per feed: "A01:adm:12", "R01:lab:40"), kept as
	//     it was built, and sent over MLLP with its acknowledgement recorded; a failure stays queued for the next send.
	// deploy/sql/hospital_h247_outbound_feeds.sql builds the tables.
	// ==========================================================================================
	public class HospitalOutboundFeed
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string Kind { get; set; } = HospitalFeedKind.Adt;
		public string? Host { get; set; }
		public int? Port { get; set; }
		public string? ReceivingApp { get; set; }
		public string? ReceivingFacility { get; set; }
		public bool Enabled { get; set; }
		/// Events from this moment on are queued (a new feed does not replay the hospital's history).
		public DateTime StartFrom { get; set; }
		public DateTime UpdatedAt { get; set; }
	}

	public class HospitalOutboundMessage
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int FeedId { get; set; }
		public string EventKey { get; set; } = "";
		public string MessageType { get; set; } = "";
		public int PatientId { get; set; }
		public string ControlId { get; set; } = "";
		public string Raw { get; set; } = "";
		public string Status { get; set; } = HospitalFeedStatus.Queued;
		public int Attempts { get; set; }
		public string? AckCode { get; set; }
		public string? LastError { get; set; }
		public DateTime EventAt { get; set; }
		public DateTime CreatedAt { get; set; }
		public DateTime? SentAt { get; set; }
	}

	public static class HospitalOutboundModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalOutboundFeed>(e =>
			{
				e.ToTable("HospitalOutboundFeeds");
				e.Property(x => x.Code).HasMaxLength(20); e.Property(x => x.Name).HasMaxLength(100); e.Property(x => x.Kind).HasMaxLength(5);
				e.Property(x => x.Host).HasMaxLength(200); e.Property(x => x.ReceivingApp).HasMaxLength(60); e.Property(x => x.ReceivingFacility).HasMaxLength(60);
				e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
			});
			b.Entity<HospitalOutboundMessage>(e =>
			{
				e.ToTable("HospitalOutboundMessages");
				e.Property(x => x.EventKey).HasMaxLength(40); e.Property(x => x.MessageType).HasMaxLength(15); e.Property(x => x.ControlId).HasMaxLength(30);
				e.Property(x => x.Status).HasMaxLength(8); e.Property(x => x.AckCode).HasMaxLength(4); e.Property(x => x.LastError).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.FeedId, x.EventKey }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.Status });
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
		}
	}
}
