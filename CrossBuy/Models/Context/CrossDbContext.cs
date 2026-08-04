using CrossBuy.Models.Context.Admin;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using System.Reflection.Emit;
using static Microsoft.EntityFrameworkCore.DbLoggerCategory.Database;

namespace CrossBuy.Models.Context
{
    public class CrossDbContext:IdentityDbContext<Users>
    {
        public CrossDbContext(DbContextOptions<CrossDbContext> options) : base (options)
        {

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
        public DbSet<Admin.NotificationMute> NotificationMutes { get; set; }
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
        public DbSet<Accounting.ProjectMaterialIssue> ProjectMaterialIssues { get; set; }           // Projects & Contracting P5-أ material issue (header)
        public DbSet<Accounting.ProjectMaterialIssueLine> ProjectMaterialIssueLines { get; set; }   // Projects & Contracting P5-أ material issue (lines)
        public DbSet<Accounting.Subcontract> Subcontracts { get; set; }                             // Projects & Contracting P6-ج subcontract (عقد باطن)
        public DbSet<Accounting.SubcontractBilling> SubcontractBillings { get; set; }               // Projects & Contracting P6-ج subcontract billing (مستخلص باطن)
        public DbSet<Accounting.VariationOrder> VariationOrders { get; set; }                        // Projects & Contracting P6-د variation order (أمر تغيير)
        public DbSet<Accounting.VariationOrderLine> VariationOrderLines { get; set; }                // Projects & Contracting P6-د variation order lines
        public DbSet<Accounting.EquipmentDepreciationAllocation> EquipmentDepreciationAllocations { get; set; }  // Projects & Contracting P6-هـ owned-equipment depreciation allocation to project
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
