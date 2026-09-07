﻿using CrossBuy.Models.Context.Admin;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using System.Reflection.Emit;
using static Microsoft.EntityFrameworkCore.DbLoggerCategory.Database;

namespace CrossBuy.Models.Context
{
    public class CrossDbContext:IdentityDbContext<Users>
    {
        // Stage 1 Batch B / B2 — the holder the pilot global query filters read. See CompanyQueryFilters.
        //
        // It is a PUBLIC PROPERTY, not just a field, and the filters read it THROUGH this context instance
        // (`CompanyQueryFilters.Apply(builder, this)`). That is not a style choice — it is the difference between
        // a working filter and a cross-tenant leak:
        //
        // EF Core caches the model per context type, so OnModelCreating runs ONCE per process. A filter that
        // closed over a holder object passed in as a plain argument would capture the FIRST context's holder and
        // keep using it for every later request — every request in the process would then be filtered to the
        // first request's company. EF re-evaluates filter expressions rooted at the executing DbContext instance,
        // so routing them through this property is what makes the value per-request.
        //
        // Proven by Stage1QueryFilterTests.Two_contexts_sharing_the_cached_model_are_filtered_by_their_own_scope,
        // which FAILED against the captured-argument version.
        public CrossBuy.BL.Platform.ICompanyScopeHolder CompanyScope { get; }

        // The constructor DI uses. AddDbContext resolves the constructor with the most resolvable parameters, and
        // ICompanyScopeHolder is registered Scoped alongside this context, so every production instance gets one.
        public CrossDbContext(
            DbContextOptions<CrossDbContext> options,
            CrossBuy.BL.Platform.ICompanyScopeHolder companyScope) : base(options)
        {
            CompanyScope = companyScope ?? throw new ArgumentNullException(nameof(companyScope));
        }

        // The pre-B2 constructor, kept so no caller breaks — and it FAILS CLOSED rather than unfiltered.
        //
        // It supplies a fresh, permanently UNRESOLVED holder, so the twelve pilot entities read NOTHING through a
        // context built this way. The alternative (install no filters) would make `new CrossDbContext(options)` a
        // quiet way to read every company's data, which is exactly the hole B2 exists to close. Production has no
        // such call site today — verified — so this path costs nothing and guards the next one.
        public CrossDbContext(DbContextOptions<CrossDbContext> options) : base (options)
        {
            CompanyScope = new CrossBuy.BL.Platform.CompanyScopeHolder();
        }

		// HM-2 (Batch 4.5 / HM-D27): EF Core's DEFAULT decimal mapping is (18,2). With no precision declared on the
		// money properties, EF sent Scale=2 parameters and SILENTLY ROUNDED every money value to 2dp ON SAVE — dropping
		// KWD fils (3rd decimal) even though the DB columns are decimal(19,4). Default all decimals to (19,4) to match
		// the money/cost columns; the finer-scale rate/factor columns are raised in OnModelCreating below.
		// Widening scale 2→4 never changes an EGP (2dp) value, so EGP behavior is unaffected.
		protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
		{
			base.ConfigureConventions(configurationBuilder);
			configurationBuilder.Properties<decimal>().HavePrecision(19, 4);
		}

		protected override void OnModelCreating(ModelBuilder builder)
		{
			base.OnModelCreating(builder);

			builder.Entity<Employee>()
				.HasOne(e => e.User)
				.WithOne()
				.HasForeignKey<Employee>(e => e.UserId)
				.OnDelete(DeleteBehavior.Cascade);

			builder.Entity<Employee>()
				.HasOne(e => e.Country)
				.WithMany(c => c.Employees)
				.HasForeignKey(e => e.CountryID)
				.OnDelete(DeleteBehavior.Restrict); 

			builder.Entity<Employee>()
				.HasOne(e => e.Company)
				.WithMany(c => c.Employees)
				.HasForeignKey(e => e.EmpCompanyID)
				.OnDelete(DeleteBehavior.Restrict); 

			builder.Entity<Employee>()
				.HasOne(e => e.Branch)
				.WithMany()
				.HasForeignKey(e => e.BranchID)
				.OnDelete(DeleteBehavior.Restrict);


			builder.Entity<Companies>()
			.HasOne(c => c.CompanyType)
			.WithMany(ct => ct.Companies)
			.HasForeignKey(c => c.CompanyTypeId)
			.OnDelete(DeleteBehavior.Restrict);


			builder.Entity<Companies>()
		.HasOne(c => c.Parent)
		.WithMany(c => c.ChildCompanies)
		.HasForeignKey(c => c.ParentCompany)
		.OnDelete(DeleteBehavior.Restrict);

			// Configure Hierarchical
			builder.Entity<Hierarchical>(entity =>
			{
				entity.HasKey(h => h.H_ID);

				// Self-referencing relationship for parent/child hierarchy
				entity.HasOne(h => h.Parent)
					  .WithMany(p => p.Children)
					  .HasForeignKey(h => h.H_Parent)
					  .OnDelete(DeleteBehavior.Restrict); // Avoid circular delete issues

				// Relationship with HierarchicalType
				entity.HasOne(h => h.Type)
					  .WithMany(t => t.Hierarchicals)
					  .HasForeignKey(h => h.H_Type)
					  .OnDelete(DeleteBehavior.Cascade);
			});

			// Configure HierarchicalType
			builder.Entity<HierarchicalType>(entity =>
			{
				entity.HasKey(ht => ht.ID);
			});

			builder.Entity<CompanyType>().HasData(
		new CompanyType { Id = 1, NameAr = "شركة مساهمة", NameEn = "Public Company" },
		new CompanyType { Id = 2, NameAr = "شركة ذات مسؤولية محدودة", NameEn = "Limited Liability Company" },
		new CompanyType { Id = 3, NameAr = "مؤسسة فردية", NameEn = "Sole Proprietorship" });

			// LeaveRequest — Restrict to avoid multiple cascade paths
			builder.Entity<LeaveRequest>(entity =>
			{
				entity.HasKey(r => r.ID);

				entity.HasOne(r => r.Employee)
					  .WithMany()
					  .HasForeignKey(r => r.EmployeeID)
					  .OnDelete(DeleteBehavior.Restrict);

				entity.HasOne(r => r.LeaveType)
					  .WithMany()
					  .HasForeignKey(r => r.LeaveTypeID)
					  .OnDelete(DeleteBehavior.Restrict);
			});

			// Notification — Restrict on recipient
			builder.Entity<Notification>(entity =>
			{
				entity.HasKey(n => n.ID);
				entity.HasOne(n => n.Recipient)
					  .WithMany()
					  .HasForeignKey(n => n.RecipientEmployeeID)
					  .OnDelete(DeleteBehavior.Restrict);
			});

			// HM-2 (Batch 5 item 1 / HM-D27): the (19,4) default above matches the 305 money/cost columns. EVERY decimal column
			// whose real DB type differs from (19,4) is pinned HERE to its EXACT (precision,scale) — so the EF model equals the DB
			// on all 352 decimal properties (ZERO divergence), which the permanent precision check in inv-test-integrity asserts.
			// Keyed by table.column (authoritative), sourced from sys.columns. No name heuristics (a bare "Rate" is FX vs tax-% by table).
			// NOTE: this is model-only — the DB is already these types (manual SQL); no migration exists or should be generated.
			var pin = new Dictionary<string, (int p, int s)>(StringComparer.OrdinalIgnoreCase)
			{
				// FX / settlement rates — decimal(19,8)
				["DeliveryNotes.ExchangeRate"] = (19, 8), ["ExchangeRates.Rate"] = (19, 8), ["GoodsReceipts.ExchangeRate"] = (19, 8),
				["JournalEntryLines.ExchangeRate"] = (19, 8), ["ManufWorkOrderLabor.ExchangeRate"] = (19, 8),
				["PaymentAllocations.InvoiceRate"] = (19, 8), ["PaymentAllocations.PaymentRate"] = (19, 8), ["Payments.ExchangeRate"] = (19, 8),
				["PurchaseInvoices.ExchangeRate"] = (19, 8), ["PurchaseOrders.ExchangeRate"] = (19, 8), ["PurchaseReturns.ExchangeRate"] = (19, 8),
				["Quotations.ExchangeRate"] = (19, 8), ["ReceiptAllocations.InvoiceRate"] = (19, 8), ["ReceiptAllocations.ReceiptRate"] = (19, 8),
				["Receipts.ExchangeRate"] = (19, 8), ["SalesInvoices.ExchangeRate"] = (19, 8), ["SalesOrders.ExchangeRate"] = (19, 8),
				["SalesReturns.ExchangeRate"] = (19, 8),
				// UoM conversion factor — decimal(19,6)
				["UoMConversions.Factor"] = (19, 6),
				// percentages / tax rates / confidence — scale 4, narrower precision than money
				["AiInteractions.Confidence"] = (5, 4),
				["PurchaseInvoiceLines.TaxRate"] = (7, 4), ["SalesInvoiceLines.TaxRate"] = (7, 4), ["TaxCodes.Rate"] = (7, 4),
				["BranchPosSettings.ServiceChargePct"] = (9, 4), ["PosOrderLines.TaxRate"] = (9, 4),
				["ProgressBillings.RetentionPercent"] = (9, 4), ["ProgressBillings.TaxRate"] = (9, 4),
				["ProjectProgresses.OverallPercent"] = (9, 4), ["ProjectProgressLines.ManualPercent"] = (9, 4),
				["Projects.AdvancePercent"] = (9, 4), ["Projects.RetentionPercent"] = (9, 4),
				["PurchaseOrderLines.TaxRate"] = (9, 4), ["SalesOrderLines.TaxRate"] = (9, 4),
				["SubcontractBillings.RetentionPercent"] = (9, 4), ["SubcontractBillings.TaxRate"] = (9, 4),
				["Subcontracts.RetentionPercent"] = (9, 4),
				// kitchen-dispatched quantity — decimal(18,3)
				["PosOrderLines.SentQty"] = (18, 3),
				// hours / layout coordinates / rating / legacy 2dp price — scale 2 (their columns are 2dp by design)
				["Items.StoreRating"] = (3, 2), ["AttendancePolicies.WorkHoursPerDay"] = (4, 2), ["AttendanceRecords.WorkedHours"] = (9, 2),
				["RestaurantTables.H"] = (9, 2), ["RestaurantTables.W"] = (9, 2), ["RestaurantTables.X"] = (9, 2), ["RestaurantTables.Y"] = (9, 2),
				["TaskItems.ActualHours"] = (9, 2), ["TaskItems.EstimatedHours"] = (9, 2), ["TimesheetEntries.Hours"] = (9, 2),
				// HM-D28 (deferred): StoreOldPrice is a MONEY column at 2dp — in a 3-decimal currency it would lose the fil.
				// Pinned to its real 2dp here so model==DB; widening it needs a manual idempotent SQL script (deploy/sql) IF the storefront ever prices in KWD.
				["Items.StoreOldPrice"] = (10, 2),
			};
			foreach (var et in builder.Model.GetEntityTypes())
			{
				var table = et.GetTableName();
				if (table == null) continue;
				var soi = Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(table, et.GetSchema());
				foreach (var p in et.GetProperties())
				{
					if (p.ClrType != typeof(decimal) && p.ClrType != typeof(decimal?)) continue;
					var col = p.GetColumnName(soi) ?? p.Name;
					if (pin.TryGetValue(table + "." + col, out var pr)) { p.SetPrecision(pr.p); p.SetScale(pr.s); }
				}
			}

			// ---- Accounting period control: close/reopen evidence and its history ----
			// Migrations are disabled here, so deploy/sql/accounting_period_control.sql is what actually
			// creates these. This mapping exists so the MODEL states the same widths the script declares -
			// without it the strings are unbounded on SQLite, where the tests run, and a close that produced
			// an over-long reason would pass every test and fail on SQL Server at month end.
			builder.Entity<Accounting.AccountingPeriodAudit>(e =>
			{
				e.ToTable("AccountingPeriodAudits");
				e.Property(x => x.FromStatus).HasMaxLength(20).IsRequired();
				e.Property(x => x.ToStatus).HasMaxLength(20).IsRequired();
				e.Property(x => x.Reason).HasMaxLength(500);
				e.HasIndex(x => new { x.CompanyID, x.FiscalPeriodId, x.OccurredAt })
					.HasDatabaseName("IX_AccountingPeriodAudits_ByPeriod");
			});

			builder.Entity<Accounting.FiscalPeriod>(e =>
				e.Property(x => x.ReopenReason).HasMaxLength(500));

			// ---- Platform Kernel slice 1: BusinessEvents + BusinessEventDispatch ----
			// The real structure ships as an idempotent script (deploy/sql/platform_business_events.sql)
			// because migrations are disabled in this project. This mapping exists so EF generates the same
			// column names, keys and indexes the script creates — and so a test host can materialise the
			// two tables from the model alone.
			builder.Entity<Platform.BusinessEvent>(e =>
			{
				e.ToTable("BusinessEvents");
				e.HasKey(x => x.EventId);
				e.Property(x => x.EventId).ValueGeneratedOnAdd();
				e.Property(x => x.EntityType).HasMaxLength(60).IsRequired();
				e.Property(x => x.EventType).HasMaxLength(80).IsRequired();
				e.Property(x => x.Visibility).HasMaxLength(40).IsRequired();
				e.Property(x => x.DedupKey).HasMaxLength(120);
				e.HasIndex(x => new { x.CompanyID, x.EntityType, x.EntityId, x.CreatedAt }).HasDatabaseName("IX_BusinessEvents_Entity");
				e.HasIndex(x => x.EventUid).IsUnique().HasDatabaseName("UX_BusinessEvents_EventUid");
				// Filtered unique index — idempotent recording per company (DedupKey).
				e.HasIndex(x => new { x.CompanyID, x.DedupKey }).IsUnique()
					.HasFilter("[DedupKey] IS NOT NULL").HasDatabaseName("UX_BusinessEvents_DedupKey");
				e.HasIndex(x => x.CreatedAt).HasDatabaseName("IX_BusinessEvents_CreatedAt");
			});

			// ---- AI Foundation Increment 1: AiProjections ----
			// The ONLY table the future AI/RAG layer reads. Structure ships in
			// deploy/sql/platform_ai_projections.sql; this mapping exists so EF generates the same names and
			// so a test host can materialise it from the model alone (the pattern used by Communication above).
			builder.Entity<Platform.AiProjection>(e =>
			{
				e.ToTable("AiProjections");
				e.HasKey(x => x.Id);
				e.Property(x => x.Id).ValueGeneratedOnAdd();
				e.Property(x => x.Consumer).HasMaxLength(60).IsRequired();
				e.Property(x => x.EntityType).HasMaxLength(60).IsRequired();
				e.Property(x => x.EventType).HasMaxLength(80).IsRequired();
				e.Property(x => x.ProjectionType).HasMaxLength(60).IsRequired();
				e.Property(x => x.PayloadJson).IsRequired();
				// Increment 2 — read-side classification + revocation tombstone.
				// Structure ships in deploy/sql/platform_ai_projections_slice_002.sql.
				e.Property(x => x.Visibility).HasMaxLength(40).IsRequired().HasDefaultValue("Internal");
				e.Property(x => x.RevokedBy).HasMaxLength(100);
				e.Property(x => x.RevocationReason).HasMaxLength(200);
				e.Ignore(x => x.IsRevoked);
				// Increment 3 — retention. Nullable on purpose: NULL = unknown = not eligible, never
				// "keeps forever". Structure ships in deploy/sql/platform_ai_projections_slice_003.sql.
				e.Property(x => x.RetentionClass).HasMaxLength(40);
				// The retrieval read path: live rows for one company and shape, newest first.
				e.HasIndex(x => new { x.CompanyID, x.ProjectionType, x.OccurredAt })
					.HasFilter("[RevokedAt] IS NULL").HasDatabaseName("IX_AiProjections_Retrieval");
				// IDEMPOTENCY, enforced by the database and not by the application: a retried or
				// concurrently-delivered dispatch must not produce a second row of the same shape.
				e.HasIndex(x => new { x.BusinessEventId, x.ProjectionType }).IsUnique()
					.HasDatabaseName("UX_AiProjections_Event_Shape");
				// The read path the AI layer will use, and the one a revocation sweep needs.
				e.HasIndex(x => new { x.CompanyID, x.EntityType, x.EntityId, x.OccurredAt })
					.HasDatabaseName("IX_AiProjections_Company_Entity");
			});

			// ---- AI Foundation Increment 4.6: AiEgressAudits ----
			// The DURABLE record of every external AI egress attempt, allowed or refused. Structure ships in
			// deploy/sql/platform_ai_egress_audit.sql; this mapping exists so EF generates the same names and
			// so a test host can materialise it from the model alone — the same pattern as AiProjections above.
			//
			// NO PAYLOAD COLUMN EXISTS, here or in the table. The classification matrix controls what leaves
			// the estate; storing the prompt would recreate that retention inside CrossBuy.
			builder.Entity<Platform.AiEgressAudit>(e =>
			{
				e.ToTable("AiEgressAudits");
				e.HasKey(x => x.Id);
				e.Property(x => x.Id).ValueGeneratedOnAdd();
				e.Property(x => x.ProviderId).HasMaxLength(64).IsRequired();
				e.Property(x => x.Feature).HasMaxLength(64).IsRequired();
				e.Property(x => x.Model).HasMaxLength(128);
				e.Property(x => x.Classification).HasMaxLength(64).IsRequired();
				e.Property(x => x.DestinationClass).HasMaxLength(64).IsRequired();
				e.Property(x => x.GovernanceDecision).HasMaxLength(256).IsRequired();
				e.Property(x => x.ApprovalReference).HasMaxLength(256);
				e.Property(x => x.CorrelationId).HasMaxLength(128).IsRequired();
				e.Property(x => x.Outcome).HasMaxLength(32).IsRequired();
				e.Property(x => x.FailureCategory).HasMaxLength(128);
				e.Property(x => x.CostCurrency).HasMaxLength(8);
				e.Property(x => x.EstimatedCost).HasPrecision(18, 6);
				// Computed in the database and never written from code — a total that can disagree with its
				// own parts is a reporting bug waiting to happen.
				e.Property(x => x.TotalTokens)
					.HasComputedColumnSql("[InputTokens] + [OutputTokens]", stored: true)
					.ValueGeneratedOnAddOrUpdate();
				// "What did this tenant send, and when" — every operator screen.
				e.HasIndex(x => new { x.CompanyID, x.OccurredAtUtc }).HasDatabaseName("IX_AiEgressAudits_Company_Time");
				// "What has this provider cost this period" — billing reconciliation.
				e.HasIndex(x => new { x.ProviderId, x.OccurredAtUtc }).HasDatabaseName("IX_AiEgressAudits_Provider_Time");
				// "Show me every refusal." Filtered, so a healthy database keeps the security question cheap.
				e.HasIndex(x => x.OccurredAtUtc).HasFilter("[Success] = 0").HasDatabaseName("IX_AiEgressAudits_Failed");
			});

			// ---- Communication Platform (ADR-030 §5) — the platform's ENTIRE EF mapping, in ONE line ----
			// Fourteen tables, their keys, lengths and indexes all live in
			// Models/Context/Communication/CommunicationModel.cs, which that work stream owns outright. This file
			// is shared with two other active work streams, so the footprint here is deliberately one call and no
			// DbSet properties — services reach the entities through db.Set<T>() (see BL/Communication/CommDb).
			// Structure ships in deploy/sql/communication_platform_slice_001.sql; this mapping exists so EF
			// generates the same names and so a test host can materialise the platform from the model alone.
			Communication.CommunicationModel.Configure(builder);

			// Central Document Platform (TAB-3). Same one-line footprint as the platform above and for the
			// same reason: every table name, key, length and index lives in a file that work stream owns
			// outright, so a merge here is a one-line conflict rather than a nine-property one. Kept SEPARATE
			// from the Communication call because these are not Communication tables - CommunicationSchemaParity
			// asserts that model maps exactly its own slice, and it was right to fail when they were merged.
			Documents.PlatformDocumentModel.Configure(builder);
			// ---- Stage 0 (Slice-003): CommMessage outbox dispatch index ----
			// Structure ships in deploy/sql/comm_outbox_slice_003.sql; this mapping exists so EF generates the
			// same index (and so a test host can materialise it from the model alone).
			builder.Entity<Comm.CommMessage>(e =>
			{
				e.HasIndex(x => new { x.Status, x.Attempts, x.ClaimedAt, x.UpdatedAt })
					.HasDatabaseName("IX_CommMessages_Dispatch")
					.HasFilter("[Status] <> 'Sent'");
			});

			builder.Entity<Platform.BusinessEventDispatch>(e =>
			{
				e.ToTable("BusinessEventDispatch");
				e.HasKey(x => x.ID);
				e.Property(x => x.ID).ValueGeneratedOnAdd();
				e.Property(x => x.Consumer).HasMaxLength(40).IsRequired();
				e.Property(x => x.Status).HasMaxLength(20).IsRequired();
				e.Property(x => x.Error).HasMaxLength(400);
				e.HasIndex(x => new { x.EventId, x.Consumer }).IsUnique().HasDatabaseName("UX_BusinessEventDispatch_Event_Consumer");
				e.HasIndex(x => new { x.Consumer, x.Status, x.UpdatedAt }).HasDatabaseName("IX_BusinessEventDispatch_Pending");
				// No navigation property: the dispatch row is queue state, not part of the event aggregate.
				e.HasOne<Platform.BusinessEvent>().WithMany()
					.HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Restrict)
					.HasConstraintName("FK_BusinessEventDispatch_Event");
			});

			// ---- Stage 1 Batch C: shared role assignments + project membership ----
			// The real structure ships as idempotent SQL; this mapping exists so EF generates the same
			// columns, keys and indexes the scripts create, and so a test host can materialise both tables
			// from the model alone.
			builder.Entity<Platform.PlatformRoleAssignment>(e =>
			{
				e.ToTable("PlatformRoleAssignments");
				e.HasKey(x => x.ID);
				e.Property(x => x.ID).ValueGeneratedOnAdd();
				e.Property(x => x.Scope).HasMaxLength(40).IsRequired();
				e.Property(x => x.PrincipalType).HasMaxLength(20).IsRequired();
				e.Property(x => x.Role).HasMaxLength(60).IsRequired();
				// The duplicate guard is FILTERED on IsActive so a revoked grant may be re-granted without
				// deleting the audit row — see the script for why that matters.
				e.HasIndex(x => new { x.CompanyID, x.Scope, x.PrincipalType, x.PrincipalId, x.Role, x.ScopeBranchId })
					.IsUnique().HasFilter("[IsActive] = 1").HasDatabaseName("UX_PlatformRoleAssignments_ActiveGrant");
				e.HasIndex(x => new { x.CompanyID, x.Scope, x.PrincipalType, x.PrincipalId })
					.HasDatabaseName("IX_PlatformRoleAssignments_Principal");
				e.HasIndex(x => new { x.CompanyID, x.Scope }).HasDatabaseName("IX_PlatformRoleAssignments_ScopeConfigured");

				// Stage 2A Batch A — slice 2 columns. Lengths mirror the script exactly; a model that disagreed
				// with the DDL would truncate silently on one path and not the other.
				e.Property(x => x.Reason).HasMaxLength(400);
				e.Property(x => x.SourceSystem).HasMaxLength(40);
				e.Property(x => x.IdempotencyKey).HasMaxLength(120);

				// Idempotency is a DATABASE guarantee, per company. Filtered to non-null keys because the key is
				// optional and SQL Server treats NULLs as equal in a unique index — unfiltered, exactly one
				// keyless grant per company would be permitted in total.
				e.HasIndex(x => new { x.CompanyID, x.IdempotencyKey })
					.IsUnique().HasFilter("[IdempotencyKey] IS NOT NULL")
					.HasDatabaseName("UX_PlatformRoleAssignments_Idempotency");
			});

			// Stage 2A Batch B — B2: the explicit bootstrap policy store. A SEPARATE table from the grant store,
			// because a policy is not a grant and the coexistence rule is that sources are never unioned.
			builder.Entity<Platform.BootstrapAccessPolicy>(e =>
			{
				e.ToTable("BootstrapAccessPolicies");
				e.HasKey(x => x.ID);
				e.Property(x => x.ID).ValueGeneratedOnAdd();
				e.Property(x => x.Scope).HasMaxLength(40).IsRequired();
				e.Property(x => x.ActionCode).HasMaxLength(60).IsRequired();
				e.Property(x => x.State).HasMaxLength(30).IsRequired();
				e.Property(x => x.Reason).HasMaxLength(400);
				e.Property(x => x.SourceSystem).HasMaxLength(40);

				// ONE active policy per (company, scope, action). FILTERED on IsActive so superseded history
				// survives — a company's record of what it used to permit is the audit trail, and a full unique
				// key would force deleting it to change a policy.
				e.HasIndex(x => new { x.CompanyID, x.Scope, x.ActionCode })
					.IsUnique().HasFilter("[IsActive] = 1")
					.HasDatabaseName("UX_BootstrapAccessPolicies_ActivePolicy");

				// The reader's hot path: one company, one scope, active rows only.
				e.HasIndex(x => new { x.CompanyID, x.Scope })
					.HasDatabaseName("IX_BootstrapAccessPolicies_CompanyScope");

				// Review and expiry sweeps (B12 warnings) scan by expiry across companies.
				e.HasIndex(x => x.ExpiresAt).HasDatabaseName("IX_BootstrapAccessPolicies_Expiry");
			});

			builder.Entity<Accounting.ProjectMember>(e =>
			{
				e.ToTable("ProjectMembers");
				e.HasKey(x => x.ID);
				e.Property(x => x.ID).ValueGeneratedOnAdd();
				e.Property(x => x.RoleOnProject).HasMaxLength(20).IsRequired();
				e.Property(x => x.AllocationPct).HasPrecision(5, 2);
				e.HasIndex(x => new { x.ProjectId, x.EmployeeId })
					.IsUnique().HasFilter("[IsActive] = 1").HasDatabaseName("UX_ProjectMembers_ActiveMembership");
				e.HasIndex(x => new { x.CompanyID, x.EmployeeId, x.ProjectId }).HasDatabaseName("IX_ProjectMembers_Access");
				// No navigation properties: membership is read by id through the access service, and a
				// navigation would invite an Include() that loads a project the caller may not access.
			});

			// ---- Stage 1 Batch B / B2: the pilot company query filters ----
			// LAST in OnModelCreating, deliberately: a HasQueryFilter call replaces any previous filter for that
			// entity, so applying these after every other mapping means nothing above can silently drop one.
			// The twelve entities, the ten deliberately-unfiltered ones, and the reasoning are in
			// CompanyQueryFilters. BusinessEventDispatch — configured immediately above — is NOT filtered.
			//
			// `this` is passed, not the holder: see the CompanyScope property's comment. The model is cached, so a
			// filter must read the scope through the EXECUTING context or it would serve every request from the
			// first request's company.
			// ---- Construction & Contracting C1 — commercial foundation mapping ----
			// Indexes, concurrency tokens and the one-primary-contract-per-project rule. Placed BEFORE the
			// query-filter call above so it cannot displace a filter, and kept in its own class so this tab's
			// footprint in this shared file stays at one line plus its DbSet declarations.
			CrossBuy.Models.Context.Construction.ConstructionCommercialModel.Configure(builder);

			// Task ecosystem: checklist, dependency edges (unique per pair), templates.
			CrossBuy.Models.Context.Tasks.TaskEcosystemModel.Configure(builder);
			CrossBuy.Models.Context.Calendar.CalendarSchedulingModel.Configure(builder);

			// Client Portal (TAB-3). One line, same as the platforms above: the external identity table
			// is mapped in a file that work stream owns, so this shared file stays a list of calls.
			Portal.PortalModel.Configure(builder);

			CrossBuy.BL.Platform.CompanyQueryFilters.Apply(builder, this);
		}

        // (legacy empty/unused ItemCategory + ItemCategoryGroup scaffold removed — superseded by Inventory module)
        public DbSet<CountriesLookup> CountriesLookup { get; set; }
		public DbSet<Employee> Employee { get; set; }
		public DbSet<Companies> Companies { get; set; }
		public DbSet<CompanyType> CompanyTypes { get; set; }

		public DbSet<Branch> Branches { get; set; }
		public DbSet<JobTitle  > JobTitles { get; set; }

		public DbSet<SystemForms> SystemForms { get; set; }
		public DbSet<Attachment> Attachments { get; set; }

		public DbSet<Hierarchical> Hierarchicals { get; set; }
		public DbSet<HierarchicalType> HierarchicalTypes { get; set; }
        public DbSet<AdministrativeBodiesCompany>  AdministrativeBodiesCompanies { get; set; }
        public DbSet<LeaveTypes> LeaveTypes { get; set; }
        public DbSet<LeavePolicies> LeavePolicies { get; set; }
        public DbSet<SalaryPolicies> SalaryPolicies { get; set; }

        public DbSet<PolicyAssignments> PolicyAssignments { get; set; }
        public DbSet<Policies> Policies { get; set; }
        public DbSet<AttendancePolicies> AttendancePolicies { get; set; }
        public DbSet<LeaveRequest> LeaveRequests { get; set; }
        public DbSet<OfficialHoliday> OfficialHolidays { get; set; }
        public DbSet<AttendanceRecord> AttendanceRecords { get; set; }
        public DbSet<Payslip> Payslips { get; set; }
        public DbSet<Admin.EmployeeRequest> EmployeeRequests { get; set; }
        public DbSet<Admin.EmployeeRequestStep> EmployeeRequestSteps { get; set; }
        public DbSet<Admin.RequiredDocumentType> RequiredDocumentTypes { get; set; }   // Recruitment R0 — required-document catalog
        public DbSet<Admin.JobApplication> JobApplications { get; set; }                // Recruitment R0 — hiring request / applicant
        public DbSet<Admin.ApplicationDocument> ApplicationDocuments { get; set; }      // Recruitment R0 — application document (→ checklist)
        public DbSet<Admin.AppraisalCycle> AppraisalCycles { get; set; }
        public DbSet<Admin.AppraisalTemplate> AppraisalTemplates { get; set; }
        public DbSet<Admin.AppraisalCriterion> AppraisalCriteria { get; set; }
        public DbSet<Admin.Appraisal> Appraisals { get; set; }
        public DbSet<Admin.AppraisalLine> AppraisalLines { get; set; }
        public DbSet<Admin.TrainingCourse> TrainingCourses { get; set; }
        public DbSet<Admin.TrainingEnrollment> TrainingEnrollments { get; set; }
        public DbSet<Admin.Brand> Brands { get; set; }
        public DbSet<LeaveEncashment> LeaveEncashments { get; set; }
        public DbSet<LeaveProvisionRun> LeaveProvisionRuns { get; set; }
        public DbSet<EmploymentContract> EmploymentContracts { get; set; }
        public DbSet<EmployeeDocument> EmployeeDocuments { get; set; }
        public DbSet<Admin.HrDocumentAttachment> HrDocumentAttachments { get; set; }
        public DbSet<Chat.Conversation> Conversations { get; set; }
        public DbSet<Chat.ConversationMember> ConversationMembers { get; set; }
        public DbSet<Chat.ChatMessage> ChatMessages { get; set; }
        public DbSet<Chat.ChatReaction> ChatReactions { get; set; }
        public DbSet<Comm.CommMessage> CommMessages { get; set; }
        public DbSet<Comm.CommAttachment> CommAttachments { get; set; }
        public DbSet<Calendar.CalendarEvent> CalendarEvents { get; set; }
        public DbSet<Calendar.CalendarEventAttendee> CalendarEventAttendees { get; set; }
        // Calendar scheduling satellites (recurrence / time zone / resources) — new tables, never
        // columns on CalendarEvents, so an unapplied script cannot break the existing calendar.
        public DbSet<Calendar.CalendarEventSchedule> CalendarEventSchedules { get; set; }
        public DbSet<Calendar.CalendarResource> CalendarResources { get; set; }
        public DbSet<Calendar.CalendarEventResource> CalendarEventResources { get; set; }
        public DbSet<Library.LibraryItem> LibraryItems { get; set; }
        public DbSet<Comm.Announcement> Announcements { get; set; }
        public DbSet<Comm.AnnouncementRead> AnnouncementReads { get; set; }
        public DbSet<Comm.DocComment> DocComments { get; set; }
        public DbSet<Admin.NotificationMute> NotificationMutes { get; set; }
        // Platform Kernel slice 1 — the business event log + its per-consumer outbox state.
        // Structure is deployed by deploy/sql/platform_business_events.sql (migrations are disabled).
        public DbSet<Platform.BusinessEvent> BusinessEvents { get; set; }
        public DbSet<Platform.BusinessEventDispatch> BusinessEventDispatches { get; set; }
        public DbSet<Platform.AiProjection> AiProjections { get; set; }

        // Increment 4.6 — the durable external-AI egress audit. Append-only; nothing updates a row.
        // Structure is deployed by deploy/sql/platform_ai_egress_audit.sql (migrations are disabled).
        public DbSet<Platform.AiEgressAudit> AiEgressAudits { get; set; }

        // Stage 1 Batch C — the ONE shared module role-assignment table, and project membership.
        // Structure ships in deploy/sql/platform_role_assignments.sql and deploy/sql/project_members.sql
        // (migrations are disabled). Nothing but IPlatformRoleDirectory may query PlatformRoleAssignments.
        public DbSet<Platform.PlatformRoleAssignment> PlatformRoleAssignments { get; set; }

        // Stage 2A Batch B — B2. Only IBootstrapAccessPolicyReader may query this for a DECISION; the seed writes
        // it. The same discipline as PlatformRoleAssignments: one reader, so the active/expiry/Never policy is
        // written once and cannot drift between call sites.
        public DbSet<Platform.BootstrapAccessPolicy> BootstrapAccessPolicies { get; set; }
        public DbSet<Accounting.ProjectMember> ProjectMembers { get; set; }

        // ---- Reporting Platform (ADR-037) ----
        // Structure ships in deploy/sql/reporting_platform.sql (migrations are disabled). Every table is
        // company-scoped and additive: the reporting platform writes NONE of these from a financial path, and it
        // writes no GL or stock at all — JournalEntryService and StockService remain the only writers of those.
        // CompanyID = 0 on ReportTemplates / ReportCategories means a PLATFORM row that belongs to no tenant.
        public DbSet<Reporting.ReportTemplate> ReportTemplates { get; set; }
        public DbSet<Reporting.ReportTemplateVersion> ReportTemplateVersions { get; set; }
        public DbSet<Reporting.ReportCategory> ReportCategories { get; set; }
        public DbSet<Reporting.ReportTag> ReportTags { get; set; }
        public DbSet<Reporting.ReportTagLink> ReportTagLinks { get; set; }
        public DbSet<Reporting.ReportFavorite> ReportFavorites { get; set; }
        public DbSet<Reporting.ReportShare> ReportShares { get; set; }
        public DbSet<Reporting.ReportRun> ReportRuns { get; set; }
        public DbSet<Reporting.ReportArchiveEntry> ReportArchiveEntries { get; set; }
        public DbSet<Reporting.ReportSchedule> ReportSchedules { get; set; }
        public DbSet<Reporting.ReportScheduleRecipient> ReportScheduleRecipients { get; set; }
        public DbSet<Reporting.ReportDeliveryAttempt> ReportDeliveryAttempts { get; set; }
        // Report Studio V2: stored logos, signatures and stamps. One line, approved as a shared-file edit;
        // the entity and its DDL live in the Reporting tree the slice owns.
        public DbSet<Reporting.ReportAsset> ReportAssets { get; set; }

        // HR Product Batches 1 and 2 - employee onboarding and roster/shift management.
        //
        // Re-inserted here rather than where they were first written: that region was relocated by
        // another work stream in the same file, and the move carried these lines with it.
        public DbSet<Hr.WorkShift> WorkShifts { get; set; }
        public DbSet<Hr.RosterPeriod> RosterPeriods { get; set; }
        public DbSet<Hr.RosterAssignment> RosterAssignments { get; set; }
        public DbSet<Hr.RosterAssignmentRevision> RosterAssignmentRevisions { get; set; }

        // HR Product Batch 1 - employee onboarding. Four tables, one block.
        //
        // Re-inserted here rather than where they were first written: that region was relocated by
        // another work stream in the same file, and the move carried these four lines with it.
        public DbSet<Hr.EmployeeOnboarding> EmployeeOnboardings { get; set; }
        public DbSet<Hr.EmployeeOnboardingItem> EmployeeOnboardingItems { get; set; }
        public DbSet<Hr.OnboardingTemplate> OnboardingTemplates { get; set; }
        public DbSet<Hr.OnboardingTemplateItem> OnboardingTemplateItems { get; set; }

        public DbSet<FinalSettlement> FinalSettlements { get; set; }
        public DbSet<LeaveCarryOver> LeaveCarryOvers { get; set; }
        public DbSet<LeaveApprovalStep> LeaveApprovalSteps { get; set; }
        public DbSet<Notification> Notifications { get; set; }

        // ---- Accounting module (Phase 0 foundation) ----
        public DbSet<Accounting.AccountType> AccountTypes { get; set; }
        public DbSet<Accounting.Account> Accounts { get; set; }
        public DbSet<Accounting.Currency> Currencies { get; set; }
        public DbSet<Accounting.ExchangeRate> ExchangeRates { get; set; }
        public DbSet<Accounting.ReceiptAllocation> ReceiptAllocations { get; set; }
        public DbSet<Accounting.PaymentAllocation> PaymentAllocations { get; set; }
        public DbSet<Accounting.FxRevaluationRun> FxRevaluationRuns { get; set; }
        public DbSet<Accounting.FiscalYear> FiscalYears { get; set; }
        public DbSet<Accounting.FiscalPeriod> FiscalPeriods { get; set; }
        public DbSet<Accounting.AccountingPeriodAudit> AccountingPeriodAudits { get; set; }   // period close/reopen history
        public DbSet<Accounting.JournalEntry> JournalEntries { get; set; }
        public DbSet<Accounting.JournalEntryLine> JournalEntryLines { get; set; }
        public DbSet<Accounting.NumberSequence> NumberSequences { get; set; }
        public DbSet<Accounting.CostCenter> CostCenters { get; set; }
        public DbSet<Accounting.Project> Projects { get; set; }
        public DbSet<Accounting.ProjectActivityType> ProjectActivityTypes { get; set; }   // Projects & Contracting P0 lookup
        public DbSet<Accounting.BoqItem> BoqItems { get; set; }                           // Projects & Contracting P1 BOQ
        public DbSet<Accounting.ProjectProgress> ProjectProgresses { get; set; }           // Projects & Contracting P3 progress (header)
        public DbSet<Accounting.ProjectProgressLine> ProjectProgressLines { get; set; }    // Projects & Contracting P3 progress (lines)
        public DbSet<Accounting.ProgressBilling> ProgressBillings { get; set; }             // Projects & Contracting P4 progress billing (المستخلص) header
        public DbSet<Accounting.ProgressBillingLine> ProgressBillingLines { get; set; }     // Projects & Contracting P4 progress billing (lines)

        // Project Billing Batch 3 - closeout evidence. Its own table rather than columns on Project:
        // a project can be closed, reopened and closed again, and columns record only the last of those.
        public DbSet<Accounting.ProjectCloseout> ProjectCloseouts { get; set; }
        public DbSet<Accounting.ProjectMaterialIssue> ProjectMaterialIssues { get; set; }           // Projects & Contracting P5-أ material issue (header)
        public DbSet<Accounting.ProjectMaterialIssueLine> ProjectMaterialIssueLines { get; set; }   // Projects & Contracting P5-أ material issue (lines)
        public DbSet<Accounting.Subcontract> Subcontracts { get; set; }                             // Projects & Contracting P6-ج subcontract (عقد باطن)
        public DbSet<Accounting.SubcontractBilling> SubcontractBillings { get; set; }               // Projects & Contracting P6-ج subcontract billing (مستخلص باطن)
        public DbSet<Accounting.VariationOrder> VariationOrders { get; set; }                        // Projects & Contracting P6-د variation order (أمر تغيير)
        public DbSet<Accounting.VariationOrderLine> VariationOrderLines { get; set; }                // Projects & Contracting P6-د variation order lines
        public DbSet<Accounting.EquipmentDepreciationAllocation> EquipmentDepreciationAllocations { get; set; }  // Projects & Contracting P6-هـ owned-equipment depreciation allocation to project

        // ---- Construction & Contracting C1 — commercial foundation (CR-01/CR-02/CR-03) ----
        // All NEW tables; no existing table is altered. Mapping lives in ConstructionCommercialModel.
        public DbSet<Construction.ClientContract> ClientContracts { get; set; }                              // C1 D-01 contract ownership
        public DbSet<Construction.BoqLineState> BoqLineStates { get; set; }                                  // C1 CR-01 BOQ line stable identity + retirement
        public DbSet<Construction.CommercialRevision> CommercialRevisions { get; set; }                      // C1 CR-03 immutable commercial revision
        public DbSet<Construction.CommercialRevisionLine> CommercialRevisionLines { get; set; }              // C1 CR-03 previous/new per line
        public DbSet<Construction.SubcontractScope> SubcontractScopes { get; set; }                          // C1 CR-02 the certification cap
        public DbSet<Construction.SubcontractCertificateLine> SubcontractCertificateLines { get; set; }      // C1 CR-02 line-level certification
        public DbSet<Construction.CertificateLineSnapshot> CertificateLineSnapshots { get; set; }            // C1 CR-03 client certificate line snapshot
        public DbSet<Construction.ConstructionAuditEntry> ConstructionAuditEntries { get; set; }

        // ---- Task ecosystem (TAB 4) — all NEW tables; no existing Tasks table is altered ----
        public DbSet<Tasks.TaskChecklistItem> TaskChecklistItems { get; set; }
        public DbSet<Tasks.TaskDependency> TaskDependencies { get; set; }
        public DbSet<Tasks.TaskTemplate> TaskTemplates { get; set; }
        public DbSet<Tasks.TaskTemplateItem> TaskTemplateItems { get; set; }             // C1 append-only field-level audit
        public DbSet<Accounting.PostingRule> PostingRules { get; set; }
        public DbSet<Accounting.AccountingUserRole> AccountingUserRoles { get; set; }
        public DbSet<Accounting.AccountingSettings> AccountingSettings { get; set; }
        public DbSet<Accounting.PayrollSettings> PayrollSettings { get; set; }
        public DbSet<Accounting.PayrollTaxBracket> PayrollTaxBrackets { get; set; }
        public DbSet<Accounting.Customer> Customers { get; set; }
        public DbSet<Accounting.Vendor> Vendors { get; set; }
        public DbSet<Accounting.SalesInvoice> SalesInvoices { get; set; }
        public DbSet<Accounting.SalesInvoiceLine> SalesInvoiceLines { get; set; }
        public DbSet<Accounting.SalesReturn> SalesReturns { get; set; }
        public DbSet<Accounting.SalesReturnLine> SalesReturnLines { get; set; }
        public DbSet<Accounting.PurchaseInvoice> PurchaseInvoices { get; set; }
        public DbSet<Accounting.PurchaseInvoiceLine> PurchaseInvoiceLines { get; set; }
        public DbSet<Accounting.PurchaseReturn> PurchaseReturns { get; set; }
        public DbSet<Accounting.PurchaseReturnLine> PurchaseReturnLines { get; set; }
        public DbSet<Accounting.Receipt> Receipts { get; set; }
        public DbSet<Accounting.Payment> Payments { get; set; }
        public DbSet<Accounting.BankAccount> BankAccounts { get; set; }
        public DbSet<Accounting.MaintenanceSchedule> MaintenanceSchedules { get; set; }
        public DbSet<Accounting.MaintenanceRecord> MaintenanceRecords { get; set; }
        public DbSet<Accounting.CashBox> CashBoxes { get; set; }
        public DbSet<Accounting.BankReconciliation> BankReconciliations { get; set; }
        public DbSet<Accounting.BankReconciliationLine> BankReconciliationLines { get; set; }
        public DbSet<Accounting.AssetCategory> AssetCategories { get; set; }
        public DbSet<Accounting.FixedAsset> FixedAssets { get; set; }
        public DbSet<Accounting.DepreciationRun> DepreciationRuns { get; set; }
        public DbSet<Accounting.DepreciationLine> DepreciationLines { get; set; }
        public DbSet<Accounting.TaxCode> TaxCodes { get; set; }
        public DbSet<Accounting.VatReturn> VatReturns { get; set; }
        public DbSet<Accounting.EtaSettings> EtaSettings { get; set; }
        public DbSet<Accounting.YearEndClosing> YearEndClosings { get; set; }
        // ===== Inventory (Phase I0) =====
        public DbSet<Inventory.ItemCategory> ItemCategories { get; set; }
        public DbSet<Inventory.UnitOfMeasure> UnitsOfMeasure { get; set; }
        public DbSet<Inventory.Item> Items { get; set; }
        public DbSet<Inventory.UoMConversion> UoMConversions { get; set; }
        public DbSet<Inventory.ItemComponent> ItemComponents { get; set; }
        public DbSet<Inventory.ItemBarcode> ItemBarcodes { get; set; }
        public DbSet<Inventory.ItemImage> ItemImages { get; set; }   // storefront gallery (display only)
        public DbSet<Inventory.Warehouse> Warehouses { get; set; }
        public DbSet<Inventory.BinLocation> BinLocations { get; set; }
        public DbSet<Inventory.BinStock> BinStocks { get; set; }
        // Operations platform (POS) setup
        public DbSet<Pos.ActivityPreset> ActivityPresets { get; set; }
        public DbSet<Pos.ActivityPresetCapability> ActivityPresetCapabilities { get; set; }
        public DbSet<Pos.BranchCapability> BranchCapabilities { get; set; }
        public DbSet<Pos.BranchPosSetting> BranchPosSettings { get; set; }
        public DbSet<Loyalty.PointsMovement> PointsMovements { get; set; }   // HM-9 slice 2: loyalty points ledger (derived balance)
        public DbSet<Pos.HyperPayToken> HyperPayTokens { get; set; }         // HM-10 slice A: hyper pay-idempotency keys (INSERT-keyed)
        public DbSet<Pos.DiningArea> DiningAreas { get; set; }
        public DbSet<Pos.KitchenStation> KitchenStations { get; set; }
        public DbSet<Pos.RestaurantTable> RestaurantTables { get; set; }
        public DbSet<Pos.PosMenuGroup> PosMenuGroups { get; set; }
        public DbSet<Pos.PosQuickItem> PosQuickItems { get; set; }
        public DbSet<Pos.PosOrder> PosOrders { get; set; }
        public DbSet<Pos.PosOrderLine> PosOrderLines { get; set; }
        public DbSet<Pos.PosPayment> PosPayments { get; set; }
        public DbSet<Pos.CustomerAddress> CustomerAddresses { get; set; }
        public DbSet<Pos.DeliveryZone> DeliveryZones { get; set; }
        public DbSet<Pos.Driver> Drivers { get; set; }
        public DbSet<Pos.Reservation> Reservations { get; set; }
        public DbSet<Pos.ModifierGroup> ModifierGroups { get; set; }
        public DbSet<Pos.ModifierOption> ModifierOptions { get; set; }
        public DbSet<Pos.ItemModifierGroup> ItemModifierGroups { get; set; }
        public DbSet<Pos.PosOrderLineModifier> PosOrderLineModifiers { get; set; }
        public DbSet<Pos.BranchItemSourcing> BranchItemSourcings { get; set; }
        public DbSet<Pos.PosSyncLog> PosSyncLogs { get; set; }
        public DbSet<Pos.PosSyncConflict> PosSyncConflicts { get; set; }
        public DbSet<Tasks.TaskItem> TaskItems { get; set; }   // TM-1: task management
        public DbSet<Tasks.TimesheetEntry> TimesheetEntries { get; set; }   // TM-3: timesheet
        public DbSet<Tasks.TaskAutoRule> TaskAutoRules { get; set; }        // TM-7: auto-task rules
        public DbSet<Tasks.TaskAutoLog> TaskAutoLogs { get; set; }          // TM-7: dedupe log
        public DbSet<Tasks.TaskMatchSuggestion> TaskMatchSuggestions { get; set; }   // TM-9-ب: scheduled-task match candidates
        public DbSet<Pos.BranchPaymentMethod> BranchPaymentMethods { get; set; }
        public DbSet<Pos.BranchUserRole> BranchUserRoles { get; set; }
        public DbSet<Pos.PosTerminal> PosTerminals { get; set; }
        public DbSet<Pos.PosShift> PosShifts { get; set; }
        public DbSet<Inventory.ItemWarehouseSetting> ItemWarehouseSettings { get; set; }
        public DbSet<Inventory.PriceList> PriceLists { get; set; }
        public DbSet<Inventory.PriceListLine> PriceListLines { get; set; }
        public DbSet<Inventory.Promotion> Promotions { get; set; }
        public DbSet<Inventory.PriceChangeLog> PriceChangeLogs { get; set; }   // HM-4: bulk price-change audit
        public DbSet<Crm.Campaign> Campaigns { get; set; }
        public DbSet<Crm.Lead> Leads { get; set; }
        public DbSet<Crm.Opportunity> Opportunities { get; set; }
        public DbSet<Crm.CrmCustomField> CrmCustomFields { get; set; }
        public DbSet<Crm.CrmCustomFieldValue> CrmCustomFieldValues { get; set; }
        public DbSet<Crm.CrmAutomationRule> CrmAutomationRules { get; set; }
        public DbSet<Crm.Activity> Activities { get; set; }
        public DbSet<Crm.CrmUserRole> CrmUserRoles { get; set; }
        public DbSet<Crm.CrmAccount> CrmAccounts { get; set; }
        public DbSet<Crm.CrmContact> CrmContacts { get; set; }
        public DbSet<Crm.CrmPipeline> CrmPipelines { get; set; }
        public DbSet<Crm.CrmPipelineStage> CrmPipelineStages { get; set; }
        public DbSet<Crm.OpportunityProduct> OpportunityProducts { get; set; }
        public DbSet<Crm.CampaignMember> CampaignMembers { get; set; }
        public DbSet<Crm.CrmMarketingList> CrmMarketingLists { get; set; }
        public DbSet<Crm.CrmListMember> CrmListMembers { get; set; }
        public DbSet<Crm.CrmSlaPolicy> CrmSlaPolicies { get; set; }
        public DbSet<Crm.CrmTicket> CrmTickets { get; set; }
        public DbSet<Crm.CrmScoringRule> CrmScoringRules { get; set; }
        public DbSet<Crm.CrmSettings> CrmSettings { get; set; }
        // ---- Inventory Phase I1: stock ledger, costing & GL ----
        public DbSet<Inventory.StockMovement> StockMovements { get; set; }
        public DbSet<Inventory.StockBalance> StockBalances { get; set; }
        public DbSet<Inventory.StockCostLayer> StockCostLayers { get; set; }
        public DbSet<Inventory.InventoryReconcileLog> InventoryReconcileLogs { get; set; }
        public DbSet<Inventory.StockBatch> StockBatches { get; set; }
        public DbSet<Inventory.StockSerial> StockSerials { get; set; }
        // ---- Inventory Phase I3: procurement ----
        public DbSet<Inventory.PurchaseOrder> PurchaseOrders { get; set; }
        public DbSet<Inventory.PurchaseOrderLine> PurchaseOrderLines { get; set; }
        public DbSet<Inventory.GoodsReceipt> GoodsReceipts { get; set; }
        public DbSet<Inventory.GoodsReceiptLine> GoodsReceiptLines { get; set; }
        // ---- Inventory Phase I4: sales ----
        public DbSet<Inventory.SalesOrder> SalesOrders { get; set; }
        public DbSet<Inventory.SalesOrderLine> SalesOrderLines { get; set; }
        public DbSet<Inventory.Quotation> Quotations { get; set; }
        public DbSet<Inventory.QuotationLine> QuotationLines { get; set; }
        public DbSet<Inventory.DeliveryNote> DeliveryNotes { get; set; }
        public DbSet<Inventory.DeliveryNoteLine> DeliveryNoteLines { get; set; }
        // ---- Inventory Phase I6: transfers ----
        public DbSet<Inventory.StockTransfer> StockTransfers { get; set; }
        public DbSet<Inventory.StockTransferLine> StockTransferLines { get; set; }
        // ---- Inventory Phase I7: stock count ----
        public DbSet<Inventory.StockCount> StockCounts { get; set; }
        public DbSet<Inventory.StockCountLine> StockCountLines { get; set; }
        public DbSet<Inventory.StockWriteOff> StockWriteOffs { get; set; }
        public DbSet<Inventory.StockWriteOffLine> StockWriteOffLines { get; set; }
        // ---- Inventory Phase I9: landed cost ----
        public DbSet<Inventory.LandedCost> LandedCosts { get; set; }
        public DbSet<Inventory.LandedCostCharge> LandedCostCharges { get; set; }
        public DbSet<Inventory.ManufWorkOrder> ManufWorkOrders { get; set; }
        public DbSet<Inventory.ManufWorkOrderComponent> ManufWorkOrderComponents { get; set; }
        public DbSet<Inventory.ManufWorkCenter> ManufWorkCenters { get; set; }
        public DbSet<Inventory.ManufRoutingOp> ManufRoutingOps { get; set; }
        public DbSet<Inventory.ManufPlan> ManufPlans { get; set; }
        public DbSet<Inventory.ManufPlanDemand> ManufPlanDemands { get; set; }
        public DbSet<Inventory.ManufWorkOrderLabor> ManufWorkOrderLabor { get; set; }
        public DbSet<Inventory.InventorySettings> InventorySettings { get; set; }
        public DbSet<Inventory.InventoryUserRole> InventoryUserRoles { get; set; }
        public DbSet<Inventory.InventoryApproval> InventoryApprovals { get; set; }
        public DbSet<Inventory.OpeningBalance> OpeningBalances { get; set; }
        public DbSet<Inventory.OpeningBalanceControl> OpeningBalanceControls { get; set; }
        public DbSet<Inventory.IntegrityCheckRun> IntegrityCheckRuns { get; set; }

    }
}
