using CrossBuy.BL;
using CrossBuy.BL.Construction;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Construction;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CrossBuy.Tests
{
    // ==========================================================================================
    // Construction C1 test fixture.
    //
    // Deliberately modelled on PlatformTestHost rather than replacing it: SQLite over a SHARED
    // in-memory connection, the REAL CrossDbContext model (so the mapping under test is the
    // production mapping), and a second context on demand — because the C1 rules are about what the
    // DATABASE holds, and "prove it from a new context" is the only way to assert that honestly.
    //
    // SQLite is the provider because it is the only in-process provider with real transactions. The
    // concurrency token under test is an application-rotated varbinary(16), NOT SQL Server
    // `rowversion` — chosen precisely so the stale-token tests are real tests here rather than tests
    // that are skipped everywhere except a machine with SQL Server configured.
    //
    // Referential integrity is off, for the same reason PlatformTestHost turns it off: the entities
    // under test sit at the end of long FK chains (Project -> Company -> CompanyType, Employee ->
    // AspNetUsers) that have nothing to do with any assertion here.
    // ==========================================================================================
    public sealed class ConstructionTestFixture : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly CompanyScopeHolder _holder = new();

        public CrossDbContext Db { get; }
        public IConstructionAuditService Audit { get; }
        public IBoqService Boq { get; }
        public ICommercialRevisionService Revisions { get; }
        public ISubcontractScopeService Scopes { get; }

        public ConstructionTestFixture(int companyId = 1)
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();
            _holder.Set(companyId, null);

            Db = NewContext();
            Db.Database.EnsureCreated();

            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "PRAGMA foreign_keys = OFF;";
                cmd.ExecuteNonQuery();
            }

            Audit = new ConstructionAuditService(Db);
            Boq = new BoqService(Db, Audit);
            Revisions = new CommercialRevisionService(Db, Audit);
            Scopes = new SubcontractScopeService(Db, Audit);
        }

        public CrossDbContext NewContext()
        {
            var options = new DbContextOptionsBuilder<CrossDbContext>()
                .UseSqlite(_connection)
                .EnableSensitiveDataLogging()
                .AddInterceptors(new CompanyWriteGuardInterceptor(
                    NullLogger<CompanyWriteGuardInterceptor>.Instance))
                .Options;
            return new CrossDbContext(options, _holder);
        }

        /// A service instance bound to a SEPARATE context — what a second concurrent user has.
        public (CrossDbContext db, IBoqService boq, ICommercialRevisionService revisions, ISubcontractScopeService scopes)
            SecondUser()
        {
            var db = NewContext();
            var audit = new ConstructionAuditService(db);
            return (db, new BoqService(db, audit), new CommercialRevisionService(db, audit),
                    new SubcontractScopeService(db, audit));
        }

        // ---------------------------------------------------------------------------------------
        // seeds
        // ---------------------------------------------------------------------------------------
        public async Task<Project> SeedProjectAsync(int companyId = 1, string code = "P-1", string name = "Test project")
        {
            var p = new Project
            {
                CompanyID = companyId, Code = code, Name = name, NameEn = name,
                IsActive = true, Status = "Active", CreatedAt = DateTime.UtcNow
            };
            Db.Projects.Add(p);
            await Db.SaveChangesAsync();
            return p;
        }

        public async Task<ClientContract> SeedContractAsync(int projectId, int companyId = 1,
            string contractNo = "C-001", bool isPrimary = true, decimal? value = 1_000_000m)
        {
            var c = new ClientContract
            {
                CompanyID = companyId, ProjectId = projectId, ContractNo = contractNo,
                Title = "Main contract", ContractValue = value, IsPrimary = isPrimary,
                Status = ClientContractStatus.Active, CreatedAt = DateTime.UtcNow,
                ConcurrencyToken = Guid.NewGuid().ToByteArray()
            };
            Db.ClientContracts.Add(c);
            await Db.SaveChangesAsync();
            return c;
        }

        public async Task<Subcontract> SeedSubcontractAsync(int projectId, int companyId = 1,
            int vendorId = 500, decimal? contractValue = 100_000m, decimal? retentionPct = 5m)
        {
            var s = new Subcontract
            {
                CompanyID = companyId, ProjectId = projectId, VendorId = vendorId,
                Description = "Test subcontract", ContractValue = contractValue,
                RetentionPercent = retentionPct, Status = "Active", CreatedAt = DateTime.UtcNow
            };
            Db.Subcontracts.Add(s);
            await Db.SaveChangesAsync();
            return s;
        }

        /// A subcontractor certificate HEADER in the given status. Lines are added by the service under test.
        public async Task<SubcontractBilling> SeedSubcontractCertificateAsync(
            Subcontract sub, string status = "Draft", int billingNo = 1)
        {
            var b = new SubcontractBilling
            {
                CompanyID = sub.CompanyID, SubcontractId = sub.ID, ProjectId = sub.ProjectId,
                VendorId = sub.VendorId, BillingNo = billingNo, BillingDate = DateTime.Today,
                Status = status, CreatedAt = DateTime.UtcNow
            };
            Db.SubcontractBillings.Add(b);
            await Db.SaveChangesAsync();
            return b;
        }

        /// A POSTED client certificate carrying one line against `boqItemId`. Nothing is posted to the
        /// ledger: this fixture seeds the certificate ROWS, because what CR-01 is about is whether the
        /// certificate's own record of billed value survives a BOQ edit.
        public async Task<ProgressBilling> SeedPostedClientCertificateAsync(
            int projectId, int boqItemId, decimal periodValue, int companyId = 1, int billingNo = 1)
        {
            var progress = new ProjectProgress
            {
                CompanyID = companyId, ProjectId = projectId, MeasurementNo = billingNo,
                MeasurementDate = DateTime.Today, Status = "Confirmed",
                ExecutedValue = periodValue, OverallPercent = 10m, CreatedAt = DateTime.UtcNow
            };
            Db.ProjectProgresses.Add(progress);
            await Db.SaveChangesAsync();

            var billing = new ProgressBilling
            {
                CompanyID = companyId, ProjectId = projectId, ProgressId = progress.ID,
                BillingNo = billingNo, BillingDate = DateTime.Today, Status = "Posted",
                GrossWork = periodValue, NetDue = periodValue, CreatedAt = DateTime.UtcNow,
                PostedAt = DateTime.UtcNow
            };
            billing.Lines.Add(new ProgressBillingLine
            {
                BoqItemId = boqItemId,
                CumulativeExecutedValue = periodValue,
                PreviouslyBilledValue = 0m,
                PeriodValue = periodValue
            });
            Db.ProgressBillings.Add(billing);
            await Db.SaveChangesAsync();
            return billing;
        }

        /// An APPROVED variation order — the only thing that may raise a subcontract cap (D-07).
        public async Task<VariationOrder> SeedApprovedVariationAsync(int projectId, int companyId = 1, int voNo = 1)
        {
            var vo = new VariationOrder
            {
                CompanyID = companyId, ProjectId = projectId, VoNo = voNo,
                Description = $"VO-{voNo}", Reason = "additional work instructed",
                Value = 0m, Status = "Approved", CreatedAt = DateTime.UtcNow,
                ApprovedAt = DateTime.UtcNow, ApprovedBy = 3
            };
            Db.VariationOrders.Add(vo);
            await Db.SaveChangesAsync();
            return vo;
        }

        public async Task<VariationOrder> SeedDraftVariationAsync(int projectId, int companyId = 1, int voNo = 99)
        {
            var vo = new VariationOrder
            {
                CompanyID = companyId, ProjectId = projectId, VoNo = voNo,
                Description = $"VO-{voNo} (draft)", Value = 0m, Status = "Draft", CreatedAt = DateTime.UtcNow
            };
            Db.VariationOrders.Add(vo);
            await Db.SaveChangesAsync();
            return vo;
        }

        // ---------------------------------------------------------------------------------------
        // reads — each one goes through a NEW context, so no assertion can be satisfied by the
        // change tracker of the context that wrote the data.
        // ---------------------------------------------------------------------------------------
        public async Task<int> CertificateLineIdAsync(int progressBillingId)
        {
            await using var db = NewContext();
            return await db.ProgressBillingLines.AsNoTracking()
                .Where(l => l.BillingId == progressBillingId).Select(l => l.ID).FirstAsync();
        }

        public async Task<byte[]> LineTokenAsync(int boqItemId)
        {
            await using var db = NewContext();
            return await db.BoqLineStates.AsNoTracking()
                .Where(s => s.BoqItemId == boqItemId).Select(s => s.ConcurrencyToken).SingleAsync();
        }

        public async Task<byte[]> ScopeTokenAsync(int scopeId)
        {
            await using var db = NewContext();
            return await db.SubcontractScopes.AsNoTracking()
                .Where(s => s.ID == scopeId).Select(s => s.ConcurrencyToken).SingleAsync();
        }

        public async Task<byte[]> RevisionTokenAsync(int revisionId)
        {
            await using var db = NewContext();
            return await db.CommercialRevisions.AsNoTracking()
                .Where(r => r.ID == revisionId).Select(r => r.ConcurrencyToken).SingleAsync();
        }

        public async Task<Dictionary<string, int>> ActiveLineIdsByCodeAsync(int projectId)
        {
            await using var db = NewContext();
            var retired = await db.BoqLineStates.AsNoTracking()
                .Where(s => s.ProjectId == projectId && s.Status == BoqLineStatus.Retired)
                .Select(s => s.BoqItemId).ToListAsync();

            return await db.BoqItems.AsNoTracking()
                .Where(b => b.ProjectId == projectId && b.Code != null && !retired.Contains(b.ID))
                .ToDictionaryAsync(b => b.Code!, b => b.ID);
        }

        /// The EXACT previously-billed lookup ProgressBillingService performs
        /// (BL/ProgressBillingService.cs PreviouslyBilledAsync): posted billings of the project,
        /// grouped by BoqItemId. If a BOQ edit breaks identity, this is the query that stops finding
        /// the money — which is how the same work gets billed twice.
        public async Task<decimal> PreviouslyBilledForAsync(int projectId, int boqItemId, int companyId = 1)
        {
            await using var db = NewContext();
            var rows = await (from l in db.ProgressBillingLines.AsNoTracking()
                              join h in db.ProgressBillings.AsNoTracking() on l.BillingId equals h.ID
                              where h.CompanyID == companyId && h.ProjectId == projectId && h.Status == "Posted"
                                    && l.BoqItemId == boqItemId
                              select l.PeriodValue).ToListAsync();
            return Math.Round(rows.Sum(), 2, MidpointRounding.AwayFromZero);
        }

        public async Task<List<ConstructionAuditEntry>> AuditForAsync(string entityType, int entityId)
        {
            await using var db = NewContext();
            return await db.ConstructionAuditEntries.AsNoTracking()
                .Where(a => a.EntityType == entityType && a.EntityId == entityId)
                .OrderBy(a => a.ID).ToListAsync();
        }

        public void Dispose()
        {
            Db.Dispose();
            _connection.Dispose();
        }
    }
}
