using System;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Portal
{
    // =============================================================================================
    // CLIENT PORTAL — the external identity.
    //
    // THE BOUNDARY THIS EXISTS TO CREATE. An employee is identified by an Employee row; every
    // internal access service in this product resolves its company through BusinessContextFactory,
    // which reads the "Employee" session blob and REFUSES when there is none. A portal user has no
    // Employee row and never will, so an external caller reaching any internal service arrives with
    // an unresolved BusinessContext and is refused by machinery that already exists.
    //
    // That is the whole design, and it is deliberately a structural boundary rather than a policy
    // one: "customer cannot become employee" is not enforced by a check somebody could forget, it is
    // enforced by the customer not having the thing employees are identified by.
    //
    // WHAT THIS ROW IS. A link between an Identity user and exactly ONE customer of exactly ONE
    // company. Not a role, not a permission set for the internal product — a statement that this
    // login speaks for this account.
    //
    // IsEndUser IS NOT REUSED. It looks like an external marker and is not one: AccountController
    // refuses login when !IsEndUser, so it means "may sign in at all" and every internal user has it.
    // Overloading it would have made every employee a portal user.
    // =============================================================================================
    public class PortalUser
    {
        public int Id { get; set; }

        /// The tenant. Written when the link is created and never taken from a request.
        public int CompanyID { get; set; }

        /// The ONE customer this login speaks for. Every portal query is scoped by this AND the
        /// company; neither alone is sufficient, because two customers of one company must not see
        /// each other and the same customer id in another company is a different business entirely.
        public int CustomerId { get; set; }

        /// The AspNetUsers key. Nullable so a portal user can be provisioned (invited) before the
        /// Identity account exists, which is how an invitation flow works without a placeholder login.
        public string? UserId { get; set; }

        /// The person, for display and for addressing correspondence. Deliberately NOT a link to
        /// Employee: a customer contact is not staff, and modelling them as one would put them in
        /// org charts, approval chains and HR reports.
        public string ContactName { get; set; } = "";
        public string? Email { get; set; }
        public string? Phone { get; set; }

        /// The contact's photograph. Nullable, and nullable is the normal case: a portal contact is
        /// not obliged to have a picture, and the portal shows the theme's neutral avatar when this
        /// is empty rather than inventing one. Set from the CRM screen by staff - a customer cannot
        /// change their own picture, by decision.
        ///
        /// THIS IS A STORED LOCATION, NOT A URL TO HAND OUT. It is resolved server-side by
        /// PortalPhotoService and served only through an authorising endpoint. The convergence wave
        /// that introduced that endpoint made the distinction load-bearing: the previous design put
        /// this value straight into an &lt;img src&gt;, which made a guessable path the only thing
        /// standing between a stranger and a named person's photograph.
        public string? PhotoPath { get; set; }

        /// Comma-separated portal capabilities (see PortalCapabilities). Absent = the read-only
        /// default; a capability is something granted, never something inferred.
        public string? Capabilities { get; set; }

        /// Revocation without deletion. A former contact keeps their audit trail and loses access.
        public bool IsActive { get; set; } = true;

        public DateTime CreatedAt { get; set; }
        public int? CreatedByEmployeeId { get; set; }
        public DateTime? LastSeenAt { get; set; }
    }

    /// What a portal login may do. Small on purpose: this batch reads, so the vocabulary is the set
    /// of things a customer may LOOK at, plus the one action the product may later grant them.
    ///
    /// These are NOT internal permissions and must never be compared against one. An internal action
    /// name appearing here would be the first step toward a portal user answering an employee's
    /// permission question.
    public static class PortalCapabilities
    {
        public const string ViewQuotations = "portal-quotations";
        public const string ViewProjects = "portal-projects";
        public const string ViewInvoices = "portal-invoices";
        public const string ViewDocuments = "portal-documents";

        /// The one WRITE a portal login may hold, and it is still not in Default: accepting a
        /// quotation is a business act with contractual meaning, so somebody decides per contact that
        /// this person speaks for the customer. PortalDataService.RespondToQuotationAsync is the only
        /// thing that reads it, and it is checked on the POST rather than trusted from the screen.
        public const string RespondToQuotations = "portal-quotation-respond";

        /// Asking a human to get in touch. A SEPARATE capability rather than something every login
        /// can do, because it is the portal's second WRITE: it puts a row in front of staff, and a
        /// portal account handed to a shared mailbox should not necessarily be able to raise work.
        ///
        /// NOT IN Default, deliberately and with a consequence worth naming: an existing portal user
        /// cannot request contact until somebody grants it. That is the conservative direction, and
        /// whether it should join the default is a PRODUCT decision, reported rather than taken here
        /// - the provisioning contract states the default as the three read capabilities.
        public const string RequestContact = "portal-contact-request";

        /// The safe default for a newly linked contact: they can see the commercial documents
        /// addressed to them and nothing else until somebody decides otherwise.
        public static readonly string[] Default =
        {
            ViewQuotations, ViewProjects, ViewInvoices,
        };

        /// EVERY capability this platform declares. It exists so provisioning can REFUSE a name
        /// nobody implements: a capability that is stored but unimplemented is a grant no one can
        /// reason about, and it reads as access that was given.
        public static readonly string[] All =
        {
            ViewQuotations, ViewProjects, ViewInvoices, ViewDocuments,
            RespondToQuotations, RequestContact,
        };

        public static bool IsKnown(string? capability)
            => !string.IsNullOrWhiteSpace(capability)
            && System.Array.IndexOf(All, capability) >= 0;
    }

    /// EF mapping, in a file this work stream owns, so CrossDbContext keeps a one-line footprint —
    /// the pattern the Communication and Document platforms already use.
    public static class PortalModel
    {
        public static void Configure(ModelBuilder builder)
        {
            builder.Entity<PortalUser>(e =>
            {
                e.ToTable("PortalUsers");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedOnAdd();
                e.Property(x => x.ContactName).HasMaxLength(200).IsRequired();
                e.Property(x => x.Email).HasMaxLength(200);
                e.Property(x => x.Phone).HasMaxLength(64);
                e.Property(x => x.UserId).HasMaxLength(450);
                e.Property(x => x.Capabilities).HasMaxLength(400);
                // 400 matches Capabilities and is far beyond what a generated file name needs; the
                // deploy slice (portal_user_photo.sql) states the same width, so EF and the database
                // cannot disagree about it.
                e.Property(x => x.PhotoPath).HasMaxLength(400);

                // The resolution every request performs: this login, in this company. Unique so one
                // Identity account cannot be linked twice and silently resolve to whichever row the
                // database happened to return first — that ambiguity would be a customer boundary
                // decided by query plan.
                e.HasIndex(x => x.UserId).IsUnique().HasFilter("[UserId] IS NOT NULL");

                // The listing an administrator uses, and the scope predicate itself.
                e.HasIndex(x => new { x.CompanyID, x.CustomerId });
            });

            // The portal's second table, mapped in its own file for the same reason this one is:
            // CrossDbContext keeps a zero-line footprint for the whole Client Portal.
            PortalContactRequestModel.Configure(builder);

            // DOC1 - published client documents. Same reason again, and it is why the DOC1 slice
            // needed no edit to CrossDbContext at all: PortalModel is already wired there, so a new
            // portal table costs one line HERE rather than a DbSet in a file three work streams share.
            ClientDocumentPublicationModel.Configure(builder);
        }
    }
}
