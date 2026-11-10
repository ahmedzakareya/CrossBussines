using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL — the patient portal's people and bookings (unit H24).
	//
	//   · An ACCOUNT is one sign-in (an AspNetUsers user, H25 signs it in). A VERIFIED account belongs to a
	//     patient file; an UNVERIFIED one (self-registered) has none yet — it can only send requests until the
	//     front desk matches it to a file. A DISABLED account cannot do anything.
	//   · FAMILY links let one account book for other patient files (a spouse, a child), when the hospital allows
	//     family booking (H23).
	//   · A BOOKING records that an H4 appointment was made from the portal: by which account, with which
	//     referral. The appointment itself is the H4 row — the front desk and the portal share the same slots and
	//     the same one-booking-per-slot index, so they cannot double-book.
	//   · A REQUEST is what a "request" clinic (H23) or an unverified account sends; the call centre books or
	//     declines it.
	// CrossBuy/deploy/sql/hospital_h24_portal_accounts.sql builds the tables.
	// ==========================================================================================

	public static class HospitalPortalAccountStatus
	{
		public const string Unverified = "Unverified";
		public const string Verified = "Verified";
		public const string Disabled = "Disabled";
		public static readonly IReadOnlyList<string> All = new[] { Unverified, Verified, Disabled };
	}

	public static class HospitalPortalRelations
	{
		public const string Spouse = "Spouse";
		public const string Child = "Child";
		public const string Parent = "Parent";
		public const string Other = "Other";
		public static readonly IReadOnlyList<string> All = new[] { Spouse, Child, Parent, Other };
	}

	public static class HospitalPortalRequestStatus
	{
		public const string Open = "Open";
		public const string Booked = "Booked";
		public const string Declined = "Declined";
		public const string Withdrawn = "Withdrawn";
		public static readonly IReadOnlyList<string> All = new[] { Open, Booked, Declined, Withdrawn };
	}

	public static class HospitalPortalTimeOfDay
	{
		public const string Morning = "Morning";
		public const string Evening = "Evening";
		public const string Any = "Any";
		public static readonly IReadOnlyList<string> All = new[] { Morning, Evening, Any };
	}

	public class HospitalPortalAccount
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		/// The AspNetUsers id (null until the sign-in exists).
		public string? UserId { get; set; }
		/// The patient file; null while unverified.
		public int? PatientId { get; set; }
		public string Status { get; set; } = HospitalPortalAccountStatus.Unverified;
		/// What a self-registered person said about themselves, for the front desk to match.
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public string? Phone { get; set; }
		public string? NationalId { get; set; }
		public DateTime? BirthDate { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
		public DateTime? VerifiedAt { get; set; }
		public int? VerifiedByEmployeeId { get; set; }
		public DateTime? LastSeenAt { get; set; }
		/// H46: where the portal's emails go (a password-reset link, the notice of a change). Email only, owner ruling.
		public string? Email { get; set; }
		/// H46: when the password was last changed — a portal session issued before it is no longer honoured.
		public DateTime? PasswordChangedAt { get; set; }
	}

	/// H46: one "forgot password" link. Only the SHA-256 of the link's token is kept, never the token; it works once,
	/// for 30 minutes. The email itself is sent straight to the patient and kept nowhere (owner ruling: not the outbox).
	public class HospitalPortalReset
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int AccountId { get; set; }
		public string TokenHash { get; set; } = "";
		/// The address the link was sent to (the request's record; the link is not in it).
		public string Email { get; set; } = "";
		public DateTime CreatedAt { get; set; }
		public DateTime ExpiresAt { get; set; }
		public DateTime? UsedAt { get; set; }
	}

	public class HospitalPortalFamilyLink
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int AccountId { get; set; }
		public int PatientId { get; set; }
		public string Relation { get; set; } = HospitalPortalRelations.Child;
		public DateTime AddedAt { get; set; }
		public int? AddedByEmployeeId { get; set; }
	}

	public class HospitalPortalBooking
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int AppointmentId { get; set; }
		public int AccountId { get; set; }
		public int PatientId { get; set; }
		public string? Referral { get; set; }
		public DateTime BookedAt { get; set; }
		public DateTime? CancelledAt { get; set; }
		/// H48: the patient asked to be told if an earlier time with the same doctor frees up (in line from BookedAt).
		public bool Waitlist { get; set; }
	}

	public static class HospitalPortalOfferStatus
	{
		public const string Open = "Open";
		public const string Taken = "Taken";
		public const string Declined = "Declined";
		public const string Lapsed = "Lapsed";
		/// The desk ended it and passed the time to the next in line.
		public const string Passed = "Passed";
	}

	/// H48: one earlier time offered by email to one waiting booking. Only the SHA-256 of the link's token is kept; the
	/// time is held for that patient until ExpiresAt, then it passes on. At most one open offer per time and per booking.
	public class HospitalPortalOffer
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int BookingId { get; set; }
		/// The later appointment that is waiting (released when the offer is taken).
		public int AppointmentId { get; set; }
		public int AccountId { get; set; }
		public int PatientId { get; set; }
		public int PractitionerId { get; set; }
		public DateTime OfferDate { get; set; }
		public int StartMinute { get; set; }
		public string TokenHash { get; set; } = "";
		public string Email { get; set; } = "";
		public string Status { get; set; } = HospitalPortalOfferStatus.Open;
		public DateTime SentAt { get; set; }
		public DateTime ExpiresAt { get; set; }
		public DateTime? RespondedAt { get; set; }
		public int? NewAppointmentId { get; set; }
		public int? PassedByEmployeeId { get; set; }
	}

	public class HospitalPortalRequest
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string RequestNo { get; set; } = "";
		public int AccountId { get; set; }
		/// The patient it is for; null when an unverified account asks for itself.
		public int? PatientId { get; set; }
		public int DepartmentId { get; set; }
		public string VisitType { get; set; } = HospitalVisitTypes.New;
		public string? PreferredDays { get; set; }
		public string TimeOfDay { get; set; } = HospitalPortalTimeOfDay.Any;
		public string? Note { get; set; }
		public string Status { get; set; } = HospitalPortalRequestStatus.Open;
		public int? AppointmentId { get; set; }
		public string? DeclineReason { get; set; }
		public DateTime CreatedAt { get; set; }
		public DateTime? HandledAt { get; set; }
		public int? HandledByEmployeeId { get; set; }
	}

	public static class HospitalPortalAccountsModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalPortalOffer>(e =>
			{
				e.ToTable("HospitalPortalOffers");   // H48 (hospital_h48_portal_waitlist.sql); the one-open-per-time and
				e.Property(x => x.TokenHash).HasMaxLength(64);   // one-open-per-booking filtered indexes are the slice's
				e.Property(x => x.Email).HasMaxLength(256);
				e.Property(x => x.Status).HasMaxLength(12);
				e.Property(x => x.OfferDate).HasColumnType("date");
				e.HasIndex(x => x.TokenHash).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.Status });
				e.HasIndex(x => x.BookingId);
			});
			b.Entity<HospitalPortalReset>(e =>
			{
				e.ToTable("HospitalPortalResets");   // H46 (hospital_h46_portal_password.sql)
				e.Property(x => x.TokenHash).HasMaxLength(64);
				e.Property(x => x.Email).HasMaxLength(256);
				e.HasIndex(x => x.TokenHash).IsUnique();
				e.HasIndex(x => new { x.AccountId, x.CreatedAt });
			});
			b.Entity<HospitalPortalAccount>(e =>
			{
				e.ToTable("HospitalPortalAccounts");   // no DbSet: each names its table
				e.Property(x => x.UserId).HasMaxLength(450);
				e.Property(x => x.Status).HasMaxLength(12);
				e.Property(x => x.Name).HasMaxLength(200);
				e.Property(x => x.NameEn).HasMaxLength(200);
				e.Property(x => x.Phone).HasMaxLength(40);
				e.Property(x => x.NationalId).HasMaxLength(20);
				e.Property(x => x.Email).HasMaxLength(256);
				// one sign-in, one account; one account per patient file
				e.HasIndex(x => x.UserId, "UX_HospitalPortalAccounts_UserId").IsUnique().HasFilter("[UserId] IS NOT NULL");
				e.HasIndex(x => new { x.CompanyId, x.PatientId }, "UX_HospitalPortalAccounts_Patient").IsUnique().HasFilter("[PatientId] IS NOT NULL");
			});
			b.Entity<HospitalPortalFamilyLink>(e =>
			{
				e.ToTable("HospitalPortalFamilyLinks");
				e.Property(x => x.Relation).HasMaxLength(10);
				e.HasIndex(x => new { x.AccountId, x.PatientId }).IsUnique();
			});
			b.Entity<HospitalPortalBooking>(e =>
			{
				e.ToTable("HospitalPortalBookings");
				e.Property(x => x.Referral).HasMaxLength(60);
				e.HasIndex(x => x.AppointmentId).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.AccountId });
			});
			b.Entity<HospitalPortalRequest>(e =>
			{
				e.ToTable("HospitalPortalRequests");
				e.Property(x => x.RequestNo).HasMaxLength(20);
				e.Property(x => x.VisitType).HasMaxLength(20);
				e.Property(x => x.PreferredDays).HasMaxLength(120);
				e.Property(x => x.TimeOfDay).HasMaxLength(10);
				e.Property(x => x.Note).HasMaxLength(400);
				e.Property(x => x.Status).HasMaxLength(12);
				e.Property(x => x.DeclineReason).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.RequestNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.Status });
			});
		}
	}
}
