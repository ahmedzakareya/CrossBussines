using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H99 — THE CLINICAL CANVAS (the approved prototype screens cv-*): "everything drawn on paper must be on the
	// system". One engine for every drawing a clinic makes on paper, one SHEET per visit and kind:
	//   * coded MARKS — a finding with a place, a side and a size. A lesion keeps one number across the patient's visits;
	//     a mark brought forward from the last visit is "carried — unverified" until the doctor confirms it, and the sheet
	//     cannot be saved as the visit's version while one is unconfirmed;
	//   * free INK — vector strokes with pressure (InkML-like), kept as strokes, never flattened to a picture;
	//   * the kind's own STATE (burn depths, audiogram points, joints, the family tree) and what its calculator derives.
	// CrossBuy/deploy/sql/hospital_h99_canvas.sql builds the tables.
	// ==========================================================================================
	public static class HospitalCanvasKind
	{
		public const string Body = "Body";
		public const string Fundus = "Fundus";
		public const string Burn = "Burn";
		public const string Audio = "Audio";
		public const string Joints = "Joints";
		public const string Pedigree = "Pedigree";
		public static readonly IReadOnlyList<string> All = new[] { Body, Fundus, Burn, Audio, Joints, Pedigree };
	}

	public static class HospitalCanvasStatus
	{
		public const string Draft = "Draft";
		/// Saved as the visit's version: nothing on it changes again.
		public const string Signed = "Signed";
	}

	public class HospitalCanvasSheet
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int PatientId { get; set; }
		public int EncounterId { get; set; }
		public string Kind { get; set; } = HospitalCanvasKind.Body;
		public string Status { get; set; } = HospitalCanvasStatus.Draft;
		/// The free ink: [{"c":"#7239EA","w":2.5,"p":[[x,y,pressure],…]},…] in the drawing's own coordinates.
		public string InkJson { get; set; } = "[]";
		/// The kind's own state (burn depths, audiogram points, joints, the family tree); "{}" for a map of marks only.
		public string StateJson { get; set; } = "{}";
		/// H111: the picture of the drawing as the doctor saw it when it was saved (cleaned SVG) — for its printed paper.
		public string? SnapshotSvg { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
		public DateTime? UpdatedAt { get; set; }
		public DateTime? SignedAt { get; set; }
		public int? SignedByEmployeeId { get; set; }
	}

	public class HospitalCanvasMark
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int PatientId { get; set; }
		public int SheetId { get; set; }
		/// The lesion's number for this patient: the same lesion keeps it on every later visit.
		public int LesionNo { get; set; }
		/// The finding's key in the kind's list (Naevus, Haem, …) and its standard code.
		public string Finding { get; set; } = "";
		public string? FindingCode { get; set; }
		/// The region the mark sits in (head, trunk, armR …) and the view or eye: F / B for the body, R / L for the eyes.
		public string? Region { get; set; }
		public string? Side { get; set; }
		public decimal X { get; set; }
		public decimal Y { get; set; }
		public decimal? SizeMm { get; set; }
		/// Brought forward from the last visit's sheet and not yet confirmed by the doctor.
		public bool Carried { get; set; }
		public int? CarriedFromMarkId { get; set; }
		/// A photo of the mark, in the File Manager (the patient's documents).
		public long? PhotoDocumentId { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
		public DateTime? VerifiedAt { get; set; }
		public int? VerifiedByEmployeeId { get; set; }
	}

	public static class HospitalCanvasModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalCanvasSheet>(e =>
			{
				e.ToTable("HospitalCanvasSheets");   // no DbSet: each names its table
				e.Property(x => x.Kind).HasMaxLength(10);
				e.Property(x => x.Status).HasMaxLength(10);
				e.HasIndex(x => new { x.EncounterId, x.Kind }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.PatientId, x.Kind });
			});
			b.Entity<HospitalCanvasMark>(e =>
			{
				e.ToTable("HospitalCanvasMarks");
				e.Property(x => x.Finding).HasMaxLength(20);
				e.Property(x => x.FindingCode).HasMaxLength(30);
				e.Property(x => x.Region).HasMaxLength(20);
				e.Property(x => x.Side).HasMaxLength(1);
				e.Property(x => x.X).HasPrecision(7, 2);
				e.Property(x => x.Y).HasPrecision(7, 2);
				e.Property(x => x.SizeMm).HasPrecision(6, 1);
				e.HasIndex(x => new { x.CompanyId, x.SheetId });
				e.HasIndex(x => new { x.CompanyId, x.PatientId, x.LesionNo });
			});
		}
	}
}
