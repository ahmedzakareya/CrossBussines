using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL PATIENTS — the master patient index (Wave 1, unit H2).
	//
	// One row per person per company, identified by a medical record number (MRN) the system
	// generates. Every later unit — appointments, consultations, admission into a bed, billing —
	// points at this row, so its identity rules are strict: one file per national ID, and a
	// duplicate check against ID, mobile and name before a new file is opened.
	//
	// Names are bilingual and BOTH are required: the desk speaks Arabic, the claim and the lab
	// label are printed in English.
	//
	// Opening a file is recorded (HospitalPatientAccessLog): who looked at whose record, and when.
	// That is the "record-access log" the roadmap's Wave 0 asks for, written by the service on
	// every open, not by the page.
	//
	// COMPANY ISOLATION IS EXPLICIT, as in HospitalFacility.cs: no global filter, every query in
	// HospitalPatientService names the caller's company.
	// ==========================================================================================

	public static class HospitalPatientSex
	{
		public const string Male = "M";
		public const string Female = "F";

		public static readonly IReadOnlyList<string> All = new[] { Male, Female };
		public static bool IsKnown(string? s) => s != null && All.Contains(s, StringComparer.Ordinal);
	}

	public static class HospitalBloodGroups
	{
		public static readonly IReadOnlyList<string> All = new[] { "O+", "O-", "A+", "A-", "B+", "B-", "AB+", "AB-" };
		public static bool IsKnown(string? g) => g != null && All.Contains(g, StringComparer.Ordinal);
	}

	public static class HospitalPatientAccessActions
	{
		public const string Open = "open";
		public const string Create = "create";
		public const string Update = "update";
	}

	public class HospitalPatient
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }

		/// The medical record number: "MRN-100001", unique in the company, never reused or edited.
		public string Mrn { get; set; } = "";

		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public string Sex { get; set; } = HospitalPatientSex.Male;
		public DateTime? BirthDate { get; set; }

		/// The national ID or residence number. Optional (a newborn, an unidentified ER arrival), but
		/// unique in the company when present.
		public string? NationalId { get; set; }

		/// CountriesLookup.ID.
		public int? NationalityId { get; set; }
		public string? Phone { get; set; }
		public string? BloodGroup { get; set; }

		/// Free text, one allergy per line. Shown as a red alert on the file; later units carry it into
		/// consultation, dispensing and nursing.
		public string? Allergies { get; set; }
		public string? ChronicConditions { get; set; }

		public string? EmergencyName { get; set; }
		public string? EmergencyRelation { get; set; }
		public string? EmergencyPhone { get; set; }

		/// The general consent to treatment and the privacy notice were signed at registration.
		public bool ConsentSigned { get; set; }

		/// H3: the patient's photo, a web path under /uploads/hospital/patients. Shown on the file, the
		/// registry and the bed the patient lies in — the person, not a glyph.
		public string? PhotoPath { get; set; }
		/// H7: the price list the patient's bills use (null = the main list) until insurance contracts assign one.
		public int? PriceListId { get; set; }

		public bool IsActive { get; set; } = true;
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
		public DateTime? UpdatedAt { get; set; }
		public int? UpdatedByEmployeeId { get; set; }
	}

	/// One row per time someone opened, created or changed a patient's file.
	public class HospitalPatientAccessLog
	{
		public long ID { get; set; }
		public int CompanyId { get; set; }
		public int PatientId { get; set; }
		public int? EmployeeId { get; set; }
		public string Action { get; set; } = HospitalPatientAccessActions.Open;
		public DateTime At { get; set; }
	}

	public static class HospitalPatientsModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalPatient>(e =>
			{
				e.HasIndex(x => new { x.CompanyId, x.Mrn }).IsUnique();
				// One file per national ID — only where one was given.
				e.HasIndex(x => new { x.CompanyId, x.NationalId }).IsUnique().HasFilter("[NationalId] IS NOT NULL");
				e.HasIndex(x => new { x.CompanyId, x.Phone });
			});
			b.Entity<HospitalPatientAccessLog>(e => e.HasIndex(x => new { x.CompanyId, x.PatientId, x.At }));
		}
	}
}
