using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H76 — INFECTION PREVENTION AND CONTROL (phase 2 of the 2026-11-09 gap research; JCI PCI, CBAHI IPC and the
	// NHSN surveillance definitions). Three records:
	//   * an ISOLATION: the precautions a patient is nursed under (contact, droplet, airborne, protective), with the
	//     organism when known — shown wherever the patient's bed is, until someone ends it;
	//   * an INVASIVE DEVICE: a central line, a urinary catheter or a ventilator, inserted and removed — its days are the
	//     denominator of the device-associated infection rates, and its day count is what the ward reviews every morning;
	//   * an INFECTION CASE: reported as suspected by the ward, confirmed or ruled out by infection control. Whether it is
	//     hospital-acquired is decided by the NHSN day-3 rule, not by whoever types it.
	// CrossBuy/deploy/sql/hospital_h76_infection.sql builds the tables.
	// ==========================================================================================
	public static class HospitalPrecaution
	{
		public const string Contact = "Contact";
		public const string Droplet = "Droplet";
		public const string Airborne = "Airborne";
		/// Protective (reverse) isolation: the patient is the one protected — neutropenia, transplant.
		public const string Protective = "Protective";
		public static readonly IReadOnlyList<string> All = new[] { Contact, Droplet, Airborne, Protective };
	}

	public static class HospitalDeviceKind
	{
		public const string CentralLine = "CentralLine";
		public const string UrinaryCatheter = "UrinaryCatheter";
		public const string Ventilator = "Ventilator";
		public static readonly IReadOnlyList<string> All = new[] { CentralLine, UrinaryCatheter, Ventilator };
	}

	public static class HospitalInfectionKind
	{
		/// Central line–associated bloodstream infection.
		public const string Clabsi = "CLABSI";
		/// Catheter-associated urinary tract infection.
		public const string Cauti = "CAUTI";
		/// Ventilator-associated pneumonia.
		public const string Vap = "VAP";
		/// Surgical site infection.
		public const string Ssi = "SSI";
		/// Clostridioides difficile infection.
		public const string Cdi = "CDI";
		/// A multidrug-resistant organism (MRSA, VRE, CRE, ESBL…): the organism is required.
		public const string Mdro = "MDRO";
		public const string Other = "Other";
		public static readonly IReadOnlyList<string> All = new[] { Clabsi, Cauti, Vap, Ssi, Cdi, Mdro, Other };

		/// The device a device-associated infection needs; null for the rest.
		public static string? DeviceOf(string kind) => kind switch
		{
			Clabsi => HospitalDeviceKind.CentralLine,
			Cauti => HospitalDeviceKind.UrinaryCatheter,
			Vap => HospitalDeviceKind.Ventilator,
			_ => null,
		};

		/// The kinds that spread by contact and put the patient under contact precautions when confirmed.
		public static bool NeedsContact(string kind) => kind is Mdro or Cdi;
	}

	public static class HospitalInfectionStatus
	{
		public const string Suspected = "Suspected";
		public const string Confirmed = "Confirmed";
		public const string RuledOut = "RuledOut";
		public static readonly IReadOnlyList<string> All = new[] { Suspected, Confirmed, RuledOut };
	}

	public class HospitalIsolation
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int PatientId { get; set; }
		/// The stay it was started in (null when started from the patient's file outside a stay).
		public int? AdmissionId { get; set; }
		public string Precaution { get; set; } = HospitalPrecaution.Contact;
		public string? Organism { get; set; }
		public string? Reason { get; set; }
		/// The infection case that started it, when it was started by confirming one.
		public int? CaseId { get; set; }
		public DateTime StartedAt { get; set; }
		public int? StartedByEmployeeId { get; set; }
		public DateTime? EndedAt { get; set; }
		public int? EndedByEmployeeId { get; set; }
		public string? EndNote { get; set; }
	}

	public class HospitalInvasiveDevice
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int AdmissionId { get; set; }
		public int PatientId { get; set; }
		public string Kind { get; set; } = HospitalDeviceKind.CentralLine;
		/// "right subclavian", "16 Fr Foley" — as written.
		public string? Site { get; set; }
		public DateTime InsertedAt { get; set; }
		public int? InsertedByEmployeeId { get; set; }
		public DateTime? RemovedAt { get; set; }
		public int? RemovedByEmployeeId { get; set; }
		public DateTime CreatedAt { get; set; }
	}

	public class HospitalInfectionCase
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string CaseNo { get; set; } = "";
		public int PatientId { get; set; }
		public int? AdmissionId { get; set; }
		public string Kind { get; set; } = HospitalInfectionKind.Other;
		public string? Organism { get; set; }
		/// The ward-local date of the event (first symptom or the positive specimen).
		public DateTime OnsetDate { get; set; }
		/// NHSN: on or after the third calendar day of the stay (the admission day is day 1).
		public bool HospitalAcquired { get; set; }
		/// The ward the patient was on that day — where the case counts.
		public int? WardId { get; set; }
		/// The device it is associated with (CLABSI, CAUTI, VAP).
		public int? DeviceId { get; set; }
		public string Status { get; set; } = HospitalInfectionStatus.Suspected;
		public string? Notes { get; set; }
		public DateTime ReportedAt { get; set; }
		public int? ReportedByEmployeeId { get; set; }
		public DateTime? DecidedAt { get; set; }
		public int? DecidedByEmployeeId { get; set; }
		public string? DecisionNote { get; set; }
	}

	public static class HospitalInfectionModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalIsolation>(e =>
			{
				e.ToTable("HospitalIsolations");   // no DbSet: each names its table
				e.Property(x => x.Precaution).HasMaxLength(12);
				e.Property(x => x.Organism).HasMaxLength(100);
				e.Property(x => x.Reason).HasMaxLength(300);
				e.Property(x => x.EndNote).HasMaxLength(300);
				// one live isolation per patient and precaution: the slice's filtered unique index (a filter here breaks SQLite hosts)
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
			b.Entity<HospitalInvasiveDevice>(e =>
			{
				e.ToTable("HospitalInvasiveDevices");
				e.Property(x => x.Kind).HasMaxLength(16);
				e.Property(x => x.Site).HasMaxLength(100);
				e.HasIndex(x => new { x.CompanyId, x.AdmissionId });
			});
			b.Entity<HospitalInfectionCase>(e =>
			{
				e.ToTable("HospitalInfectionCases");
				e.Property(x => x.CaseNo).HasMaxLength(20);
				e.Property(x => x.Kind).HasMaxLength(8);
				e.Property(x => x.Organism).HasMaxLength(100);
				e.Property(x => x.OnsetDate).HasColumnType("date");
				e.Property(x => x.Status).HasMaxLength(10);
				e.Property(x => x.Notes).HasMaxLength(1000);
				e.Property(x => x.DecisionNote).HasMaxLength(500);
				e.HasIndex(x => new { x.CompanyId, x.CaseNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.OnsetDate });
			});
		}
	}
}
