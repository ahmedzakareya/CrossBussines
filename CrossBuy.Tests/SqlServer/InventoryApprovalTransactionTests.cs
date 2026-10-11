using System.Text.Json;
using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Platform;
using CrossBuy.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrossBuy.Tests.SqlServer
{
	// =============================================================================================
	// APPROVAL-TRANSACTION-INTEGRITY-INVENTORY — the P0.
	//
	// WHAT WAS WRONG. InventoryApprovalService.ApproveAsync replayed the held payload through the
	// authoritative module service, which COMMITTED the purchase order or the stock movement, and only
	// then marked the approval Approved with a SEPARATE SaveChanges. Anything that failed in between
	// left a real document permanently in the books with the approval still Pending. The approver
	// retries, the payload is replayed, and a SECOND document is created for one decision.
	//
	// WHY THIS SUITE IS ON REAL SQL SERVER. The property under test is a transaction boundary. SQLite
	// would answer a different question - it has different transaction and constraint behaviour, and a
	// rollback that works there proves nothing about the engine this ships on. The non-transactional
	// rules (company isolation, segregation of duties, decision fields) are proved by the in-memory
	// suite that already owns them; this one proves only what needs an engine.
	//
	// TWO DOCTYPES, TWO MECHANISMS, DELIBERATELY. The four delegated operations reach the outer
	// transaction two different ways, and both are exercised here:
	//   PurchaseOrder  → ProcurementService opens NO transaction; its SaveChanges ENLISTS in the ambient one.
	//   StockTransfer  → StockService opens ScopedTx.BeginOrJoinAsync, which JOINS rather than beginning.
	// StockCount and WriteOff use the identical BeginOrJoin path as Transfer - the same mechanism, and
	// they are covered by the same proof, which is stated rather than implied.
	// =============================================================================================
	[Collection(UatSqlProbeCollection.Name)]
	public class InventoryApprovalTransactionTests : IAsyncLifetime
	{
		private const int Co = 41;
		private const int Preparer = 7;
		private const int Approver = 9;

		private readonly UatSqlProbeFixture _sql;
		public InventoryApprovalTransactionTests(UatSqlProbeFixture sql) => _sql = sql;
		private void Ready() => Skip.If(!_sql.Available, _sql.SkipReason);

		private UatSqlProbeFixture.ProbeDatabase? _probe;
		private int _vendorId, _itemId, _whFrom, _whTo;

		public async Task InitializeAsync()
		{
			if (!_sql.Available) return;
			_probe = await _sql.CreateProbeDatabaseAsync("INVAPPRTX");
			await ArrangeAsync();
		}

		public async Task DisposeAsync()
		{
			if (_probe != null) await _sql.DropProbeDatabaseAsync(_probe);
		}

		// ---- arrangement ---------------------------------------------------------------------------

		private async Task ArrangeAsync()
		{
			using var db = _sql.ContextFor(_probe!, Co);

			// CurrencyRounding refuses to assume two decimals, so the company currency comes first or
			// every amount is rejected before any arithmetic happens.
			db.Currencies.Add(new Currency { Code = "EGP", Name = "جنيه مصري", NameEn = "Egyptian Pound", DecimalPlaces = 2 });
			await db.SaveChangesAsync();

			// Every stock movement resolves a fiscal period before it posts, so an open one covering the
			// payload date is arrangement, not a test of anything.
			var fy = new FiscalYear { CompanyID = Co, Name = "2026", StartDate = new DateTime(2026, 1, 1), EndDate = new DateTime(2026, 12, 31), Status = "Open" };
			db.FiscalYears.Add(fy);
			await db.SaveChangesAsync();
			db.FiscalPeriods.Add(new FiscalPeriod { FiscalYearId = fy.ID, PeriodNo = 9, StartDate = new DateTime(2026, 9, 1), EndDate = new DateTime(2026, 9, 30), Status = "Open" });
			await db.SaveChangesAsync();

			Account Acct(string code, string name) =>
				new() { Code = code, Name = name, NameEn = name, CompanyID = Co, AccountTypeId = 1, IsPostable = true, IsActive = true };
			var inv = Acct("110301", "المخزون");
			var cogs = Acct("510101", "تكلفة المبيعات");
			var adj = Acct("520110", "تسويات مخزنية");
			// The write-off expense account StockService resolves by code. Arrangement, not assertion.
			var wof = Acct("510103", "مصروف الإعدام");
			db.Accounts.AddRange(inv, cogs, adj, wof);
			await db.SaveChangesAsync();

			var cat = new ItemCategory
			{
				Code = "APPRTX", Name = "APPRTX", NameEn = "APPRTX", CompanyID = Co,
				InventoryAccountId = inv.ID, CogsAccountId = cogs.ID, AdjustmentAccountId = adj.ID,
			};
			db.ItemCategories.Add(cat);
			await db.SaveChangesAsync();

			var item = new Item
			{
				CompanyID = Co, ItemCode = "APPRTX-1", Barcode = "APPRTX-1", Name = "صنف", NameEn = "Item",
				ItemCategoryId = cat.ID, ItemType = "Stockable", BaseUoMId = 1, IsActive = true,
			};
			db.Items.Add(item);

			var from = new Warehouse { CompanyID = Co, Code = "WH-A", Name = "مخزن أ", NameEn = "WH A", IsActive = true };
			var to = new Warehouse { CompanyID = Co, Code = "WH-B", Name = "مخزن ب", NameEn = "WH B", IsActive = true };
			db.Warehouses.AddRange(from, to);

			var vendor = new Vendor { CompanyID = Co, Name = "مورّد", NameEn = "Vendor", IsActive = true };
			db.Vendors.Add(vendor);
			await db.SaveChangesAsync();

			_itemId = item.ID; _whFrom = from.ID; _whTo = to.ID; _vendorId = vendor.ID;

			// Stock for the transfer to move. Written straight to the balance rather than through a
			// receipt: the arrangement is not what this suite is testing.
			db.StockBalances.Add(new StockBalance
			{
				CompanyID = Co, ItemId = _itemId, WarehouseId = _whFrom,
				QtyOnHand = 100m, AvgCost = 10m, TotalValue = 1000m,
			});
			await db.SaveChangesAsync();
		}

		// ---- the service graph ---------------------------------------------------------------------

		private ServiceProvider Graph(int companyId = Co)
		{
			var services = new ServiceCollection();
			services.AddLogging();
			services.AddLocalization();
			services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
				.AddInMemoryCollection(new Dictionary<string, string?>()).Build());
			services.AddScoped(_ => _sql.ContextFor(_probe!, companyId));
			services.AddScoped<ICurrencyService, CurrencyService>();
			services.AddScoped<ICurrencyRounding, CurrencyRounding>();
			services.AddScoped<IFiscalPeriodService, FiscalPeriodService>();
			services.AddScoped<IJournalEntryService, JournalEntryService>();
			services.AddScoped<IStockService, StockService>();
			// ProcurementService reaches PayableService, FixedAssetService and ThreeWayMatchService.
			// Registered here for the same reason the rest are: this graph must be the REAL services,
			// because a faked delegate would have its own transaction behaviour and would prove nothing
			// about the boundary under test.
			services.AddScoped<IPayableService, PayableService>();
			services.AddScoped<IFixedAssetService, FixedAssetService>();
			services.AddScoped<IThreeWayMatchService, ThreeWayMatchService>();
			services.AddScoped<IProcurementService, ProcurementService>();
			services.AddScoped<IEntityRegistry, EntityRegistry>();
			services.AddScoped<IBusinessContextAccessor>(_ => new StubContextAccessor(new BusinessContext
			{
				CompanyId = companyId, EmployeeId = Approver, UserId = "uat-invapprtx",
				Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid(),
			}));
			services.AddScoped<IBusinessEventService, BusinessEventService>();
			services.AddScoped<INotificationService, CountingNotifications>();

			// The REAL Inventory access service, so the approval service passes the same authorization gate
			// production does. A permissive double here would let a wiring break reach production green.
			services.AddScoped<Microsoft.AspNetCore.Http.IHttpContextAccessor, Microsoft.AspNetCore.Http.HttpContextAccessor>();
			services.AddScoped<CrossBuy.BL.Platform.IBootstrapAccessPolicyReader, CrossBuy.BL.Platform.BootstrapAccessPolicyReader>();
			services.AddScoped<InventoryAccessService>();
			services.AddScoped<CrossBuy.BL.Platform.IModuleAccessService>(sp => sp.GetRequiredService<InventoryAccessService>());

			services.AddScoped<InventoryApprovalService>();
			return services.BuildServiceProvider();
		}

		/// Counts what was sent. A notification is a claim that something happened, so "how many" is the
		/// assertion that separates a committed fact from a rolled-back one.
		private sealed class CountingNotifications : INotificationService
		{
			public int Sent;

			public Task NotifyAsync(int recipientEmployeeId, string? titleAr, string? titleEn,
				string? bodyAr, string? bodyEn, string type, int? refId = null,
				string? url = null, int? companyId = null, int? actorEmployeeId = null,
				string? priority = null, string? category = null, string? dedupKey = null,
				DateTime? expiresAt = null, string? icon = null,
				string? entityType = null, int? entityId = null)
			{ Sent++; return Task.CompletedTask; }

			public Task<int> NotifyRoleAsync(int companyId, string scope, string[] roles,
				string? titleAr, string? titleEn, string? bodyAr, string? bodyEn,
				string type, int? refId = null, int? exceptEmployeeId = null) => Task.FromResult(0);
		}

		// ---- seeding an approval -------------------------------------------------------------------

		private async Task<int> SeedApprovalAsync(string docType)
		{
			object payload = docType switch
			{
				"PurchaseOrder" => new PoApprovalPayload
				{
					VendorId = _vendorId, WarehouseId = _whFrom, OrderDate = new DateTime(2026, 9, 1),
					Lines = new List<PoLineInput> { new() { ItemId = _itemId, Qty = 5m, UnitPrice = 10m } },
				},
				"StockTransfer" => new TransferApprovalPayload
				{
					FromWarehouseId = _whFrom, ToWarehouseId = _whTo, Date = new DateTime(2026, 9, 1),
					Lines = new List<TransferLineInput> { new() { ItemId = _itemId, Qty = 4m } },
				},
				"StockCount" => new CountApprovalPayload
				{
					WarehouseId = _whFrom, CountDate = new DateTime(2026, 9, 1),
					Lines = new List<CountLineInput> { new() { ItemId = _itemId, CountedQty = 97m } },
				},
				_ => new WriteOffApprovalPayload
				{
					WarehouseId = _whFrom, WriteOffDate = new DateTime(2026, 9, 1), Reason = "Damaged",
					Lines = new List<WriteOffLineInput> { new() { ItemId = _itemId, Qty = 3m, Reason = "Damaged" } },
				},
			};

			using var db = _sql.ContextFor(_probe!, Co);
			var ap = new InventoryApproval
			{
				CompanyID = Co, DocType = docType, Amount = 50m,
				PayloadJson = JsonSerializer.Serialize(payload),
				Status = "Pending", RequestedByEmployeeId = Preparer, RequestedAt = DateTime.UtcNow,
			};
			db.InventoryApprovals.Add(ap);

			// The approver must actually hold the right the service now asks for. Seeded once — these tests
			// are about the transaction boundary, and an approver without the right would fail them for a
			// reason that has nothing to do with atomicity.
			if (!await db.InventoryUserRoles.AnyAsync(r => r.CompanyID == Co && r.EmployeeId == Approver))
				db.InventoryUserRoles.Add(new CrossBuy.Models.Context.Inventory.InventoryUserRole
				{
					CompanyID = Co, EmployeeId = Approver, Role = "InventoryManager",
				});

			await db.SaveChangesAsync();
			return ap.ID;
		}

		/// The authoritative consequence, counted from the tables the module owns - never from the return
		/// value of the call under test.
		private async Task<int> ConsequenceCountAsync(string docType)
		{
			using var db = _sql.ContextFor(_probe!, Co);
			return docType switch
			{
				"PurchaseOrder" => await db.PurchaseOrders.CountAsync(o => o.CompanyID == Co),
				"StockTransfer" => await db.StockTransfers.CountAsync(t => t.CompanyID == Co),
				"StockCount" => await db.StockCounts.CountAsync(c => c.CompanyID == Co),
				// A write-off can land as its own StockWriteOff document or as a count adjustment carrying
				// Reason = WriteOff, depending on the company's configured mode - so the consequence is
				// counted on the STOCK MOVEMENT it always produces, which is the fact either way.
				_ => await db.StockMovements.CountAsync(m => m.CompanyID == Co && m.Direction == -1),
			};
		}

		private async Task<string> StatusAsync(int approvalId)
		{
			using var db = _sql.ContextFor(_probe!, Co);
			return await db.InventoryApprovals.Where(a => a.ID == approvalId).Select(a => a.Status).FirstAsync();
		}

		// ---- I1..I4: the consequence and the decision commit together ------------------------------

		[SkippableTheory]
		[InlineData("PurchaseOrder")]
		[InlineData("StockTransfer")]
		[InlineData("StockCount")]
		[InlineData("WriteOff")]
		public async Task I1_an_approval_commits_its_document_and_its_decision_together(string docType)
		{
			Ready();
			int id = await SeedApprovalAsync(docType);
			Assert.Equal(0, await ConsequenceCountAsync(docType));

			using var scope = Graph();
			var svc = scope.GetRequiredService<InventoryApprovalService>();
			var notifications = (CountingNotifications)scope.GetRequiredService<INotificationService>();

			var (ok, err) = await svc.ApproveAsync(id, Approver, "معتمد");

			Assert.True(ok, err);
			// Verified from the authoritative table, not from the return value.
			Assert.Equal(1, await ConsequenceCountAsync(docType));
			Assert.Equal("Approved", await StatusAsync(id));
			Assert.Equal(1, notifications.Sent);                          // I14
		}

		// ---- I5: the consequence fails ---------------------------------------------------------------

		[SkippableFact]
		public async Task I5_a_failed_document_leaves_the_approval_pending_and_writes_nothing()
		{
			Ready();

			// A transfer of more than the warehouse holds. A real refusal from the real service, not an
			// injected one - the delegated operation decides, exactly as it does in production.
			using (var db = _sql.ContextFor(_probe!, Co))
			{
				var ap = new InventoryApproval
				{
					CompanyID = Co, DocType = "StockTransfer", Amount = 50m,
					PayloadJson = JsonSerializer.Serialize(new TransferApprovalPayload
					{
						FromWarehouseId = _whFrom, ToWarehouseId = _whTo, Date = new DateTime(2026, 9, 1),
						Lines = new List<TransferLineInput> { new() { ItemId = _itemId, Qty = 100000m } },
					}),
					Status = "Pending", RequestedByEmployeeId = Preparer, RequestedAt = DateTime.UtcNow,
				};
				db.InventoryApprovals.Add(ap);
				await db.SaveChangesAsync();

				using var scope = Graph();
				var svc = scope.GetRequiredService<InventoryApprovalService>();
				var notifications = (CountingNotifications)scope.GetRequiredService<INotificationService>();

				var (ok, err) = await svc.ApproveAsync(ap.ID, Approver, null);

				Assert.False(ok);
				Assert.False(string.IsNullOrWhiteSpace(err));
				Assert.Equal(0, await ConsequenceCountAsync("StockTransfer"));
				Assert.Equal("Pending", await StatusAsync(ap.ID));
				Assert.Equal(0, notifications.Sent);                      // I15
			}
		}

		// ---- I6: the regression that matters ---------------------------------------------------------

		[SkippableTheory]
		[InlineData("PurchaseOrder")]
		[InlineData("StockTransfer")]
		public async Task I6_a_failure_after_the_document_but_before_the_decision_rolls_everything_back(string docType)
		{
			Ready();
			int id = await SeedApprovalAsync(docType);

			using var scope = Graph();
			var svc = scope.GetRequiredService<InventoryApprovalService>();
			var notifications = (CountingNotifications)scope.GetRequiredService<INotificationService>();

			// THE EXACT WINDOW THE DEFECT LIVED IN: the document has been written by the authoritative
			// service and the decision is not yet persisted. Before this repair the document was already
			// COMMITTED by then and nothing could take it back.
			svc.BeforeDecisionSaveHookForTests =
				() => throw new InvalidOperationException("the decision could not be persisted");

			var (ok, err) = await svc.ApproveAsync(id, Approver, null);

			Assert.False(ok);
			Assert.Contains("تعذّر حفظ قرار الاعتماد", err!, StringComparison.Ordinal);

			// All three, and the first is the one that used to be wrong.
			Assert.Equal(0, await ConsequenceCountAsync(docType));
			Assert.Equal("Pending", await StatusAsync(id));
			Assert.Equal(0, notifications.Sent);
		}

		// ---- I7: and the retry produces exactly one --------------------------------------------------

		[SkippableTheory]
		[InlineData("PurchaseOrder")]
		[InlineData("StockTransfer")]
		public async Task I7_the_retry_after_a_rolled_back_approval_creates_exactly_one_document(string docType)
		{
			Ready();
			int id = await SeedApprovalAsync(docType);

			using (var failing = Graph())
			{
				var svc = failing.GetRequiredService<InventoryApprovalService>();
				svc.BeforeDecisionSaveHookForTests = () => throw new InvalidOperationException("boom");
				var (ok, _) = await svc.ApproveAsync(id, Approver, null);
				Assert.False(ok);
			}

			Assert.Equal(0, await ConsequenceCountAsync(docType));

			// A fresh scope, exactly as a retried request would be.
			using (var retry = Graph())
			{
				var svc = retry.GetRequiredService<InventoryApprovalService>();
				var (ok, err) = await svc.ApproveAsync(id, Approver, "معتمد بعد إعادة المحاولة");
				Assert.True(ok, err);
			}

			// ONE. Before the repair this was two: the first attempt's document survived the failure.
			Assert.Equal(1, await ConsequenceCountAsync(docType));
			Assert.Equal("Approved", await StatusAsync(id));
		}

		// ---- I8: a second approve after success --------------------------------------------------------

		[SkippableFact]
		public async Task I8_approving_an_already_approved_request_creates_no_second_document()
		{
			Ready();
			int id = await SeedApprovalAsync("PurchaseOrder");

			using (var first = Graph())
			{
				var (ok, err) = await first.GetRequiredService<InventoryApprovalService>()
					.ApproveAsync(id, Approver, null);
				Assert.True(ok, err);
			}
			Assert.Equal(1, await ConsequenceCountAsync("PurchaseOrder"));

			using (var second = Graph())
			{
				var svc = second.GetRequiredService<InventoryApprovalService>();
				var notifications = (CountingNotifications)second.GetRequiredService<INotificationService>();
				var (ok, err) = await svc.ApproveAsync(id, Approver, null);

				// The Pending-only guard refuses it. That guard is not the fix - it can only refuse what
				// was recorded, and the transaction is what makes the record and the document agree about
				// whether the first attempt happened at all.
				Assert.False(ok);
				Assert.False(string.IsNullOrWhiteSpace(err));
				Assert.Equal(0, notifications.Sent);
			}

			Assert.Equal(1, await ConsequenceCountAsync("PurchaseOrder"));
		}

		// ---- I13: the decision is recorded, and by whom ------------------------------------------------

		[SkippableFact]
		public async Task I13_the_decision_records_its_actor_time_note_and_document_number()
		{
			Ready();
			int id = await SeedApprovalAsync("PurchaseOrder");
			var before = DateTime.UtcNow.AddSeconds(-5);

			using var scope = Graph();
			var (ok, err) = await scope.GetRequiredService<InventoryApprovalService>()
				.ApproveAsync(id, Approver, "ملاحظة الاعتماد");
			Assert.True(ok, err);

			using var db = _sql.ContextFor(_probe!, Co);
			var ap = await db.InventoryApprovals.SingleAsync(a => a.ID == id);
			Assert.Equal("Approved", ap.Status);
			Assert.Equal(Approver, ap.DecidedByEmployeeId);
			Assert.NotNull(ap.DecidedAt);
			Assert.True(ap.DecidedAt >= before);
			Assert.Equal("ملاحظة الاعتماد", ap.DecisionNote);
			Assert.False(string.IsNullOrWhiteSpace(ap.ResultDocNo));   // the wording still needs it
		}

		// ---- I11 / I12: segregation of duties, unchanged ------------------------------------------------

		[SkippableFact]
		public async Task I11_the_preparer_cannot_approve_their_own_document()
		{
			Ready();
			int id = await SeedApprovalAsync("PurchaseOrder");

			using var scope = Graph();
			var (ok, err) = await scope.GetRequiredService<InventoryApprovalService>()
				.ApproveAsync(id, Preparer, null);

			Assert.False(ok);
			// EN-default service contract. This refusal reason is produced by the service layer; the
			// Arabic a user sees is a presentation-boundary concern, not part of this contract.
			Assert.Contains("Segregation of duties", err!, StringComparison.Ordinal);
			Assert.Equal(0, await ConsequenceCountAsync("PurchaseOrder"));
			Assert.Equal("Pending", await StatusAsync(id));
		}

		[SkippableFact]
		public async Task I12_the_preparer_cannot_reject_their_own_document()
		{
			Ready();
			int id = await SeedApprovalAsync("PurchaseOrder");

			using var scope = Graph();
			var (ok, err) = await scope.GetRequiredService<InventoryApprovalService>()
				.RejectAsync(id, Preparer, null);

			Assert.False(ok);
			Assert.Contains("Segregation of duties", err!, StringComparison.Ordinal);
			Assert.Equal("Pending", await StatusAsync(id));
		}

		[Fact]
		public void The_decision_is_saved_BEFORE_the_transaction_commits()
		{
			// FOUND BY MUTATION. Swapping SaveChanges and Commit left every behavioural test green: in the
			// happy path both still happen, and in the failure path the seam throws before either. But the
			// swapped order is the INVERSE defect this repair exists to prevent - commit the document, then
			// try to persist the decision outside any transaction, and a failure there leaves a committed
			// document with a Pending approval. The window is real; no behavioural test can enter it,
			// because in correct code it does not exist.
			//
			// So the ORDER is asserted directly. Comments are stripped first - a source guard that matches
			// prose is not reading the code - and the search is scoped to ApproveAsync.
			var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
			while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
			Assert.True(dir != null, "could not locate the repository root");

			var raw = System.IO.File.ReadAllText(
				System.IO.Path.Combine(dir!.FullName, "CrossBuy", "BL", "InventoryApprovalService.cs"));
			var code = string.Join(Environment.NewLine, raw
				.Split('\n')
				.Select(l => { int c = l.IndexOf("//", StringComparison.Ordinal); return c < 0 ? l : l[..c]; }));

			// LastIndexOf, not IndexOf: the interface declaration at the top of the file carries the same
			// signature, and slicing between the two DECLARATIONS produces an empty body that would make
			// this guard pass by finding nothing. The implementation is the last occurrence of each.
			int approve = code.LastIndexOf("ApproveAsync(int id, int approverEmp", StringComparison.Ordinal);
			int reject = code.LastIndexOf("RejectAsync(int id, int approverEmp", StringComparison.Ordinal);
			Assert.True(approve > 0 && reject > approve, "could not isolate ApproveAsync");
			var body = code[approve..reject];

			int save = body.IndexOf("_db.SaveChangesAsync()", StringComparison.Ordinal);
			int commit = body.IndexOf("tx.CommitAsync()", StringComparison.Ordinal);
			Assert.True(save > 0, "ApproveAsync no longer saves the decision");
			Assert.True(commit > 0, "ApproveAsync no longer commits");
			Assert.True(save < commit,
				"the decision is persisted AFTER the commit - a failure there would leave a committed document with a Pending approval.");

			// And the transaction opens before anything is delegated.
			int begin = body.IndexOf("ScopedTx.BeginOrJoinAsync", StringComparison.Ordinal);
			int delegateCall = body.IndexOf("CreatePurchaseOrderAsync", StringComparison.Ordinal);
			Assert.True(begin > 0 && delegateCall > begin,
				"the delegated document is created outside the approval transaction.");
		}

		// ---- the seam is a test seam ----------------------------------------------------------------

		[Fact]
		public void The_failure_seam_is_never_armed_by_production_code()
		{
			// BeforeDecisionSaveHookForTests had to be public: the test assembly is separate and this
			// repository declares no InternalsVisibleTo. Public is a door, so this is the lock - nothing
			// under CrossBuy/ may assign it, and a controller or service that ever did would fail here
			// rather than in production.
			var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
			while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
			Assert.True(dir != null, "could not locate the repository root");

			var production = System.IO.Path.Combine(dir!.FullName, "CrossBuy");
			var offenders = System.IO.Directory
				.EnumerateFiles(production, "*.cs", System.IO.SearchOption.AllDirectories)
				.Where(f => !f.Contains(System.IO.Path.DirectorySeparatorChar + "obj" + System.IO.Path.DirectorySeparatorChar))
				.Where(f => System.IO.File.ReadAllText(f).Contains("BeforeDecisionSaveHookForTests =", StringComparison.Ordinal))
				.ToList();

			Assert.True(offenders.Count == 0, "production code arms the test seam: " + string.Join(", ", offenders));
		}

		// ---- I9 / I10: company isolation ------------------------------------------------------------

		[SkippableFact]
		public async Task I9_an_approver_in_another_company_cannot_approve_or_reject()
		{
			Ready();
			int id = await SeedApprovalAsync("PurchaseOrder");

			// Company 77 asking about company 41's approval. It must read as missing, not as forbidden -
			// a different answer would let an approver enumerate another company's queue.
			using var foreign = Graph(companyId: 77);
			var svc = foreign.GetRequiredService<InventoryApprovalService>();

			var (approveOk, approveErr) = await svc.ApproveAsync(id, Approver, null);
			var (rejectOk, rejectErr) = await svc.RejectAsync(id, Approver, null);

			Assert.False(approveOk);
			Assert.False(rejectOk);
			Assert.Equal(approveErr, rejectErr);          // the same "not found", both directions
			Assert.Equal(0, await ConsequenceCountAsync("PurchaseOrder"));
			Assert.Equal("Pending", await StatusAsync(id));
		}
	}
}
