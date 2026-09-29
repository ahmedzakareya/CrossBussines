using System;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Portal
{
    // =============================================================================================
    // CLIENT PORTAL — "please get in touch".
    //
    // WHY THIS IS A NEW TABLE AND NOT A CRM Activity. Measured, not assumed. Activity carries
    // CompanyID, Type, Subject, CustomerId, OwnerEmployeeId, EntityType, EntityId, Done and Notes —
    // and it has NO PortalUserId, so it cannot record WHICH external login asked. Its EntityType
    // vocabulary is Lead, Opportunity, Account, Customer and Contact, which does not include
    // Quotation. And OwnerEmployeeId is a STAFF owner, so a customer-originated row would be
    // indistinguishable from work a colleague created. Activity models staff FOLLOW-UP; this models
    // a customer REQUEST. Writing one into the other would make the provenance of the row a lie, and
    // provenance is the only thing this row is for.
    //
    // IT IS ALSO A WRITE BY AN OUTSIDER, which no other row in the CRM schema is. Keeping it in its
    // own table means the blast radius of "a customer can create rows" is one table that staff read,
    // rather than the timeline every salesperson works from.
    //
    // DELIBERATELY SMALL. Who, about what, when, what they said, and whether anyone has dealt with
    // it. No assignment, no priority, no SLA, no channel preference, no thread: none of those exist
    // in the product today, and inventing them here would be inventing a workflow nobody asked for.
    // =============================================================================================
    public class PortalContactRequest
    {
        public int Id { get; set; }

        /// The tenant. Taken from the resolved portal identity and never from the request.
        public int CompanyID { get; set; }

        /// The customer the request is about — also from the identity. Company AND customer, because
        /// neither alone is a boundary: two customers of one company must not see each other, and the
        /// same customer id in another company is a different business entirely.
        public int CustomerId { get; set; }

        /// WHICH EXTERNAL LOGIN ASKED. This is the column that made a new table necessary, and it is
        /// what lets staff answer "who wants us to call" rather than only "which account".
        public int PortalUserId { get; set; }

        /// The quotation this is about, when the customer asked from a quotation. Optional because a
        /// customer may simply want to talk. It is an ownership-checked reference, never an authority:
        /// a quotation id that does not belong to this customer is refused before the row is written.
        public int? QuotationId { get; set; }

        public DateTime RequestedAt { get; set; }

        /// What the customer typed, if anything. Optional, bounded, and stored as they wrote it.
        public string? Message { get; set; }

        /// PortalContactRequestStatus. A closed vocabulary of two, held as constants rather than in a
        /// lookup table — the platform already expresses small closed vocabularies that way, and a
        /// lookup table here would be two rows nobody edits and one more thing to seed.
        public string Status { get; set; } = PortalContactRequestStatus.Requested;

        /// Who dealt with it, and when. Both null until somebody does. HandledByEmployeeId is a STAFF
        /// id on purpose: the customer raises it, staff close it, and the asymmetry is the workflow.
        public DateTime? HandledAt { get; set; }
        public int? HandledByEmployeeId { get; set; }
    }

    /// The two states a request can be in. Two, because the product has exactly two: nobody has
    /// looked at it yet, or somebody has. An "InProgress" that nothing sets would be a state the
    /// screen has to render and no code can reach.
    public static class PortalContactRequestStatus
    {
        public const string Requested = "REQUESTED";
        public const string Handled = "HANDLED";

        public static bool IsValid(string? value)
            => string.Equals(value, Requested, StringComparison.Ordinal)
            || string.Equals(value, Handled, StringComparison.Ordinal);
    }

    /// EF mapping, in the file this work stream owns, so CrossDbContext keeps its zero-line footprint
    /// for the portal — the entity is reached through Set&lt;PortalContactRequest&gt;(), exactly as
    /// PortalUser is, rather than by adding a DbSet to a heavily shared file.
    public static class PortalContactRequestModel
    {
        /// Message length. Long enough for a paragraph, short enough that the column is not a place to
        /// paste a document — an external caller's free text is the one field an outsider fully
        /// controls, and it is bounded at the database as well as in the service.
        public const int MaxMessageLength = 1000;

        public static void Configure(ModelBuilder builder)
        {
            builder.Entity<PortalContactRequest>(e =>
            {
                e.ToTable("PortalContactRequests");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedOnAdd();

                e.Property(x => x.Message).HasMaxLength(MaxMessageLength);
                e.Property(x => x.Status).HasMaxLength(32).IsRequired();

                // The staff queue: open requests for one company, newest first. Filtered on the status
                // so the index stays small as handled rows accumulate — the open set is the working set.
                e.HasIndex(x => new { x.CompanyID, x.Status, x.RequestedAt })
                 .HasDatabaseName("IX_PortalContactRequests_Company_Status");

                // The customer's own history, which is also the duplicate check's predicate.
                e.HasIndex(x => new { x.CompanyID, x.CustomerId, x.QuotationId })
                 .HasDatabaseName("IX_PortalContactRequests_Company_Customer_Quotation");
            });
        }
    }
}
