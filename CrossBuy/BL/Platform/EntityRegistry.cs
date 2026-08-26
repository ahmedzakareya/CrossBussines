using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Platform
{
    // Platform Kernel (ADR-002) — the concrete registry. The definition table is static and frozen; the
    // per-type search / resolve queries are lifted verbatim from TM-2 TaskLinkResolver so the record
    // picker behaves exactly as before promotion.
    //
    // Capability flags describe what is WIRED today, not what is theoretically possible: they flip on
    // per entity as each one is onboarded, which keeps the registry from over-promising to callers that
    // gate on them (ITimelineProjectionService rejects an entity whose SupportsTimeline is false).
    public class EntityRegistry : IEntityRegistry
    {
        public const int SearchTake = 20;

        private readonly CrossDbContext _db;
        public EntityRegistry(CrossDbContext db) { _db = db; }

        // ---- canonical entity codes (frozen vocabulary) ----
        public const string SalesInvoice = "SalesInvoice";
        public const string PurchaseInvoice = "PurchaseInvoice";   // slice 2
        public const string Quotation = "Quotation";               // slice 3 (Stage 0)
        public const string JournalEntry = "JournalEntry";         // slice 3 (Stage 0)
        public const string Customer = "Customer";
        public const string Supplier = "Supplier";
        public const string ManufWorkOrder = "ManufWorkOrder";
        public const string PosOrder = "PosOrder";
        public const string Employee = "Employee";
        public const string Project = "Project";
        public const string Item = "Item";

        // Tasks & Calendar integration (TAB 4). Onboarded so a task and a calendar event can carry a
        // timeline, comments, mentions and files, and so their business events can be validated against
        // this registry — RecordAsync throws on an unknown code, so registration is what makes the
        // already-defined Task.* / CalendarEvent.* contracts publishable at all.
        public const string Task = "Task";
        public const string CalendarEvent = "CalendarEvent";

        // Stage 2A Batch A. The grant store becomes an event PRODUCER: IPlatformGrantWriter records
        // Created / Revoked / ValidityChanged. RecordAsync validates EntityCode against this registry and
        // throws on an unknown code, so onboarding the code is what makes the events possible at all.
        public const string PlatformRoleAssignment = "PlatformRoleAssignment";

        // ---- permission scope keys — routed by IPlatformPermissionProvider ----
        public const string ScopeAccounting = "Accounting";
        public const string ScopeInventory = "Inventory";
        public const string ScopePos = "Pos";
        public const string ScopeCrm = "Crm";
        // Slice 2: manufacturing has no RBAC service of its own — its screens live in InventoryController and
        // are governed by inventory roles. The scope is separate anyway so manufacturing policy can diverge
        // later without touching the inventory entities; today the adapter delegates to inventory.
        public const string ScopeManufacturing = "Manufacturing";

        // ---- Stage 1 Batch C: the four scopes that had no access service ----
        //
        // The comment that used to sit here said HR and Projects "authorize on authenticated + same company
        // only", and that tightening them "needs its own slice with a real access service". This is that
        // slice. Each of these four now has a session-free, context-aware IModuleAccessService and a
        // PlatformPermissionProvider adapter.
        //
        // These strings are also the `Scope` column of PlatformRoleAssignments — one authoritative registry
        // for both, so a role row and an access service cannot disagree about what a module is called. An
        // industry pack (Hospital, Hotel) adds a constant here plus ROWS; it never adds a role table.
        public const string ScopeHr = "Hr";
        public const string ScopeProjects = "Projects";
        public const string ScopeTasks = "Tasks";
        public const string ScopeCommunication = "Communication";
        // Calendar gets its own scope so CalendarAccessService can own the policy. It was ScopeNone,
        // which DefaultPermissionAdapter denies for every action except View — correct while no policy
        // existed, and the reason a calendar write could not be authorized at all.
        public const string ScopeCalendar = "Calendar";

        public const string ScopeNone = "None";

        // Every scope that may legitimately appear in PlatformRoleAssignments.Scope. IPlatformRoleDirectory
        // validates against this at STARTUP, so an unknown or misspelled scope fails loudly instead of
        // quietly matching no rows and granting nothing (a typo must not read as "no roles configured").
        //
        // ScopeNone is absent deliberately: it means "no module owns this", so a role assignment against it
        // would be a grant nobody evaluates.
        public static readonly IReadOnlyList<string> PermissionScopes = new[]
        {
            ScopeAccounting, ScopeInventory, ScopePos, ScopeCrm, ScopeManufacturing,
            ScopeHr, ScopeProjects, ScopeTasks, ScopeCommunication, ScopeCalendar,
        };

        public static bool IsKnownScope(string? scope)
            => scope != null && PermissionScopes.Contains(scope, StringComparer.Ordinal);

        private static readonly List<EntityDefinition> Definitions = new()
        {
            new EntityDefinition
            {
                Code = SalesInvoice,
                DisplayNameAr = "فاتورة مبيعات", DisplayNameEn = "Sales invoice",
                Module = "Accounting", Icon = "ki-outline ki-bill", Color = "primary",
                RouteTemplate = "/Accounting/SalesInvoiceDetail?id={id}",
                SupportsSearch = true,
                // Pilot entity for the kernel: the only type that produces events and renders a timeline.
                SupportsTimeline = true,
                SupportsComments = true,
                SupportsFiles = false, SupportsFollowers = false,
                PermissionScope = ScopeAccounting,
                ListedInRecordPicker = true,
            },
            new EntityDefinition
            {
                // Slice 2 pilot. PermissionScope stays Accounting, NOT Crm: this Customer is the ACCOUNTING
                // customer master (Models/Context/Accounting), its screens are /Accounting/Customers and
                // /Accounting/CustomerStatement, and SaveCustomer is gated by [AccPerm("post")].
                // CrmAccessService governs a different set of tables (CrmAccounts/CrmContacts/leads, linked
                // through CrmCustomerLink), so authorizing this entity by CRM roles would block accounting
                // users from a page they can already open. See Slice-002 doc.
                Code = Customer,
                DisplayNameAr = "عميل", DisplayNameEn = "Customer",
                Module = "Accounting", Icon = "ki-outline ki-profile-circle", Color = "success",
                RouteTemplate = "/Accounting/CustomerStatement?id={id}",
                SupportsSearch = true, SupportsTimeline = true, SupportsComments = false,
                SupportsFiles = false, SupportsFollowers = false,
                PermissionScope = ScopeAccounting,
                ListedInRecordPicker = true,
            },
            new EntityDefinition
            {
                // Slice 2 pilot. Detail page is /Accounting/PurchaseInvoiceDetail, so the scope is Accounting
                // even though posting also writes a stock receipt.
                Code = PurchaseInvoice,
                DisplayNameAr = "فاتورة مشتريات", DisplayNameEn = "Purchase invoice",
                Module = "Accounting", Icon = "ki-outline ki-bill", Color = "warning",
                RouteTemplate = "/Accounting/PurchaseInvoiceDetail?id={id}",
                SupportsSearch = true, SupportsTimeline = true,
                // The comments widget (_DocTimeline) is already wired into PurchaseInvoiceDetail.
                SupportsComments = true,
                SupportsFiles = false, SupportsFollowers = false,
                PermissionScope = ScopeAccounting,
                // Not offered by the TM-2 record picker before the kernel; keeping it out preserves that.
                ListedInRecordPicker = false,
            },
            new EntityDefinition
            {
                // Slice 3 (Stage 0). Registered because QuotationDetails.cshtml ALREADY stores document comments
                // under the literal EntityType "Quotation" — which happens to equal this canonical code exactly,
                // so registration needs no alias mapping and existing rows keep resolving.
                // Timeline is NOT enabled: no producer emits quotation events yet and no screen renders one.
                Code = Quotation,
                DisplayNameAr = "عرض سعر", DisplayNameEn = "Quotation",
                Module = "Inventory", Icon = "ki-outline ki-document", Color = "info",
                RouteTemplate = "/Inventory/QuotationDetails?id={id}",
                SupportsSearch = true, SupportsTimeline = false,
                SupportsComments = true,     // _DocTimeline is wired on QuotationDetails.cshtml
                SupportsFiles = false, SupportsFollowers = false,
                PermissionScope = ScopeInventory,
                // Never a TM-2 picker type; keeping it out preserves the picker's seven types.
                ListedInRecordPicker = false,
            },
            new EntityDefinition
            {
                // Slice 3 (Stage 0). Registered so JournalEntry.Reversed can be recorded — reversal is the most
                // audit-relevant operation in the system and previously left no durable trace.
                // Timeline is NOT enabled: there is no journal-entry timeline screen, and enabling the flag
                // without one would make ITimelineProjectionService answer for a screen that does not exist.
                Code = JournalEntry,
                DisplayNameAr = "قيد يومية", DisplayNameEn = "Journal entry",
                Module = "Accounting", Icon = "ki-outline ki-notepad-edit", Color = "dark",
                RouteTemplate = "/Accounting/JournalEntry?id={id}",
                SupportsSearch = true, SupportsTimeline = false, SupportsComments = false,
                SupportsFiles = false, SupportsFollowers = false,
                PermissionScope = ScopeAccounting,
                ListedInRecordPicker = false,
            },
            new EntityDefinition
            {
                // Searchable but NOT offered by the record picker — see ListedInRecordPicker. TM-9 scheduled
                // tasks search suppliers directly; the picker never listed the type, so ResolveAsync must
                // keep returning "not a picker type" for it (preserved by the compatibility wrapper).
                Code = Supplier,
                DisplayNameAr = "مورّد", DisplayNameEn = "Supplier",
                Module = "Accounting", Icon = "ki-outline ki-truck", Color = "warning",
                RouteTemplate = null,
                SupportsSearch = true, SupportsTimeline = false, SupportsComments = false,
                SupportsFiles = false, SupportsFollowers = false,
                PermissionScope = ScopeAccounting,
                ListedInRecordPicker = false,
            },
            new EntityDefinition
            {
                // Slice 2 pilot — the only entity in the kernel so far with a genuine multi-state lifecycle
                // (Draft → Released → Completed, or → Cancelled).
                Code = ManufWorkOrder,
                DisplayNameAr = "أمر تشغيل", DisplayNameEn = "Work order",
                Module = "Manufacturing", Icon = "ki-outline ki-gear", Color = "info",
                RouteTemplate = "/Inventory/WorkOrderDetails?id={id}",
                SupportsSearch = true, SupportsTimeline = true, SupportsComments = false,
                SupportsFiles = false, SupportsFollowers = false,
                PermissionScope = ScopeManufacturing,
                ListedInRecordPicker = true,
            },
            new EntityDefinition
            {
                Code = PosOrder,
                DisplayNameAr = "طلب مطعم", DisplayNameEn = "Restaurant order",
                Module = "Pos", Icon = "ki-outline ki-handcart", Color = "dark",
                // No admin detail screen for a POS order yet → reference-only (label shown, no "open").
                RouteTemplate = null,
                SupportsSearch = true, SupportsTimeline = false, SupportsComments = false,
                SupportsFiles = false, SupportsFollowers = false,
                PermissionScope = ScopePos,
                ListedInRecordPicker = true,
            },
            new EntityDefinition
            {
                Code = Employee,
                DisplayNameAr = "موظف", DisplayNameEn = "Employee",
                Module = "Hr", Icon = "ki-outline ki-user", Color = "primary",
                // List-only screen (no per-id detail view) — BuildUrl returns it unchanged.
                RouteTemplate = "/Admin/EmployeesList",
                SupportsSearch = true, SupportsTimeline = false, SupportsComments = false,
                SupportsFiles = false, SupportsFollowers = false,
                PermissionScope = ScopeNone,
                ListedInRecordPicker = true,
            },
            new EntityDefinition
            {
                Code = Project,
                DisplayNameAr = "مشروع", DisplayNameEn = "Project",
                Module = "Projects", Icon = "ki-outline ki-abstract-26", Color = "info",
                RouteTemplate = "/Project/Projects",
                SupportsSearch = true, SupportsTimeline = false, SupportsComments = false,
                SupportsFiles = false, SupportsFollowers = false,
                PermissionScope = ScopeProjects,
                ListedInRecordPicker = true,
            },
            new EntityDefinition
            {
                Code = Item,
                DisplayNameAr = "صنف", DisplayNameEn = "Item",
                Module = "Inventory", Icon = "ki-outline ki-basket", Color = "success",
                RouteTemplate = "/Inventory/EditItem?id={id}",
                SupportsSearch = true, SupportsTimeline = false, SupportsComments = false,
                SupportsFiles = false, SupportsFollowers = false,
                PermissionScope = ScopeInventory,
                ListedInRecordPicker = true,
            },
            new EntityDefinition
            {
                // ---- Tasks & Calendar integration (TAB 4) — TCI-D-01 ----
                //
                // PermissionScope is ScopeTasks, NOT ScopeNone. TasksAccessService already exists with eight
                // actions and three roles; registering with no scope would make this registry the loosest
                // door into a module that already has a lock.
                //
                // RouteTemplate has no {id}: Tasks has a list screen and no per-task detail screen, so a
                // /Tasks/Details/{id} route would be a dead link. BuildUrl returns the list unchanged —
                // the same treatment PosOrder already gets.
                //
                // ListedInRecordPicker is FALSE, and that is a deliberate deviation from this tab's own
                // earlier proposal: EntityRegistryTests pins the picker to exactly seven types in order
                // ("TaskLinkResolver_preserves_its_pre_kernel_behaviour"). Adding Task there changes a
                // behaviour another tab's test guards, so it needs that owner's agreement — everything
                // else about this registration works without it.
                Code = Task,
                DisplayNameAr = "مهمة", DisplayNameEn = "Task",
                Module = "Tasks", Icon = "ki-outline ki-check-square", Color = "primary",
                RouteTemplate = "/Tasks/Index",
                SupportsSearch = true, SupportsTimeline = true, SupportsComments = true,
                SupportsFiles = true, SupportsFollowers = true,
                PermissionScope = ScopeTasks,
                ListedInRecordPicker = false,
            },
            new EntityDefinition
            {
                // ---- Calendar event (TAB 4) ----
                //
                // PermissionScope is ScopeNone deliberately, and it is not an oversight: Calendar has NO
                // access service. Its real rule is record-level — organiser OR company-scope OR attendee —
                // and it already lives in CalendarService.Visible(). Inventing a module scope here would
                // create an authorization surface nobody owns and no role fills.
                //
                // SupportsFollowers is FALSE because attendees ARE the follower set; a second concept would
                // diverge from the attendee list the moment either changed.
                //
                // An attendee email is not an authenticated principal, so external attendees stay out of
                // this registration entirely pending an ExternalPrincipalContext.
                Code = CalendarEvent,
                DisplayNameAr = "حدث", DisplayNameEn = "Calendar event",
                Module = "Calendar", Icon = "ki-outline ki-calendar", Color = "info",
                RouteTemplate = "/Calendar/Index",
                SupportsSearch = true, SupportsTimeline = true, SupportsComments = true,
                SupportsFiles = true, SupportsFollowers = false,
                PermissionScope = ScopeCalendar,
                ListedInRecordPicker = false,
            },
            new EntityDefinition
            {
                // Stage 2A Batch A — the platform grant store, onboarded so the Grant Writer can record events.
                //
                // SupportsTimeline / Comments / Files / Followers are all FALSE, and it is NOT listed in the
                // record picker. A role grant is a security artefact, not a collaboration subject: a timeline
                // would render who-granted-what to anyone who can open a record, and a comment thread on a
                // privilege escalation is not a feature. The events exist for AUDIT, read through the platform
                // event monitor, which already applies its own authorization.
                //
                // PermissionScope is ScopeNone deliberately: no single module owns grant administration, and
                // attributing it to one would let that module's roles decide who may read security events.
                Code = PlatformRoleAssignment,
                DisplayNameAr = "تعيين دور", DisplayNameEn = "Role grant",
                Module = "Platform", Icon = "ki-outline ki-shield-tick", Color = "warning",
                RouteTemplate = null,
                SupportsSearch = false, SupportsTimeline = false, SupportsComments = false,
                SupportsFiles = false, SupportsFollowers = false,
                PermissionScope = ScopeNone,
                ListedInRecordPicker = false,
            },
        };

        private static readonly Dictionary<string, EntityDefinition> ByCode =
            Definitions.ToDictionary(d => d.Code, StringComparer.Ordinal);

        public IReadOnlyList<EntityDefinition> GetDefinitions() => Definitions;

        public EntityDefinition GetDefinition(string entityCode)
            => TryGetDefinition(entityCode, out var d) ? d! : throw new EntityCodeNotRegisteredException(entityCode);

        public bool TryGetDefinition(string? entityCode, out EntityDefinition? definition)
        {
            definition = null;
            if (string.IsNullOrWhiteSpace(entityCode)) return false;
            if (!ByCode.TryGetValue(entityCode, out var d)) return false;
            definition = d;
            return true;
        }

        public bool IsValid(string? entityCode) => TryGetDefinition(entityCode, out _);

        public string? BuildUrl(string entityCode, int entityId)
        {
            var def = GetDefinition(entityCode);
            if (string.IsNullOrEmpty(def.RouteTemplate)) return null;
            // A template without "{id}" is a list-only screen — return it as-is.
            return def.RouteTemplate.Contains("{id}", StringComparison.Ordinal)
                ? def.RouteTemplate.Replace("{id}", entityId.ToString(), StringComparison.Ordinal)
                : def.RouteTemplate;
        }

        // Search queries lifted from TM-2 TaskLinkResolver.SearchAsync unchanged (same filters, ordering
        // and take) so the promoted registry returns identical rows to the pre-kernel picker.
        public async Task<List<EntitySearchResult>> SearchAsync(string entityCode, string? query, BusinessContext context, CancellationToken cancellationToken = default)
        {
            var def = GetDefinition(entityCode);
            if (!def.SupportsSearch) return new List<EntitySearchResult>();

            int companyId = context.CompanyId;
            var term = (query ?? "").Trim();
            bool any = term.Length == 0;

            List<(int Id, string Label)> rows = entityCode switch
            {
                SalesInvoice => await _db.SalesInvoices.AsNoTracking()
                    .Where(i => i.CompanyID == companyId && (any || (i.InvoiceNo != null && i.InvoiceNo.Contains(term))))
                    .OrderByDescending(i => i.ID).Take(SearchTake)
                    .Select(i => new ValueTuple<int, string>(i.ID, i.InvoiceNo ?? ("#" + i.ID))).ToListAsync(cancellationToken),

                // Slice 2: mirrors the SalesInvoice search (number contains, newest first, same take) and the
                // PurchaseInvoicesData list screen, which also filters on InvoiceNo.
                PurchaseInvoice => await _db.PurchaseInvoices.AsNoTracking()
                    .Where(i => i.CompanyID == companyId && (any || (i.InvoiceNo != null && i.InvoiceNo.Contains(term))))
                    .OrderByDescending(i => i.ID).Take(SearchTake)
                    .Select(i => new ValueTuple<int, string>(i.ID, i.InvoiceNo ?? ("#" + i.ID))).ToListAsync(cancellationToken),

                // Slice 3: quotations search by document number, same shape as the invoice searches.
                Quotation => await _db.Quotations.AsNoTracking()
                    .Where(q => q.CompanyID == companyId && (any || (q.QuoteNo != null && q.QuoteNo.Contains(term))))
                    .OrderByDescending(q => q.ID).Take(SearchTake)
                    .Select(q => new ValueTuple<int, string>(q.ID, q.QuoteNo ?? ("#" + q.ID))).ToListAsync(cancellationToken),

                JournalEntry => await _db.JournalEntries.AsNoTracking()
                    .Where(j => j.CompanyID == companyId && (any || j.EntryNo.Contains(term)))
                    .OrderByDescending(j => j.ID).Take(SearchTake)
                    .Select(j => new ValueTuple<int, string>(j.ID, j.EntryNo)).ToListAsync(cancellationToken),

                Customer => await _db.Customers.AsNoTracking()
                    .Where(c => c.CompanyID == companyId && (any || c.Name.Contains(term)))
                    .OrderBy(c => c.Name).Take(SearchTake)
                    .Select(c => new ValueTuple<int, string>(c.ID, c.Name)).ToListAsync(cancellationToken),

                Supplier => await _db.Vendors.AsNoTracking()
                    .Where(v => v.CompanyID == companyId && (any || v.Name.Contains(term)))
                    .OrderBy(v => v.Name).Take(SearchTake)
                    .Select(v => new ValueTuple<int, string>(v.ID, v.Name)).ToListAsync(cancellationToken),

                ManufWorkOrder => await _db.ManufWorkOrders.AsNoTracking()
                    .Where(w => w.CompanyID == companyId && (any || (w.WoNo != null && w.WoNo.Contains(term))))
                    .OrderByDescending(w => w.ID).Take(SearchTake)
                    .Select(w => new ValueTuple<int, string>(w.ID, w.WoNo ?? ("#" + w.ID))).ToListAsync(cancellationToken),

                PosOrder => await _db.PosOrders.AsNoTracking()
                    .Where(o => o.CompanyId == companyId && (any || (o.ReceiptNo != null && o.ReceiptNo.Contains(term))))
                    .OrderByDescending(o => o.ID).Take(SearchTake)
                    .Select(o => new ValueTuple<int, string>(o.ID, o.ReceiptNo ?? ("#" + o.ID))).ToListAsync(cancellationToken),

                // KNOWN DEVIATION from the platform company-isolation rule, preserved deliberately: the
                // TM-2 employee picker never filtered by company. Adding the filter here would silently
                // hide employees from an existing multi-company picker, which is a behaviour change outside
                // this slice's pilot. Tracked in PKS-001 "Known limitations".
                Employee => await _db.Employee.AsNoTracking()
                    .Where(e => e.FullName != null && e.FullName != "" && (any || e.FullName.Contains(term)))
                    .OrderBy(e => e.FullName).Take(SearchTake)
                    .Select(e => new ValueTuple<int, string>(e.ID, e.FullName!)).ToListAsync(cancellationToken),

                Project => await _db.Projects.AsNoTracking()
                    .Where(p => p.CompanyID == companyId && (any || p.Name.Contains(term) || p.Code.Contains(term)))
                    .OrderBy(p => p.Name).Take(SearchTake)
                    .Select(p => new ValueTuple<int, string>(p.ID, p.Code + " — " + p.Name)).ToListAsync(cancellationToken),

                Item => await _db.Items.AsNoTracking()
                    .Where(i => i.CompanyID == companyId && (any || i.Name.Contains(term) || (i.ItemCode != null && i.ItemCode.Contains(term))))
                    .OrderBy(i => i.Name).Take(SearchTake)
                    .Select(i => new ValueTuple<int, string>(i.ID, (i.ItemCode ?? "") + " — " + i.Name)).ToListAsync(cancellationToken),

                // Tasks & Calendar integration (TAB 4). Both filter by company, so a search can never
                // surface another tenant's row. Calendar deliberately does NOT apply the per-employee
                // visibility rule here: this is the link picker, and CalendarService.Visible() remains the
                // only place that decides who may READ an event — see the note on ResolveAsync below.
                Task => await _db.TaskItems.AsNoTracking()
                    .Where(t => t.CompanyId == companyId && (any || t.Title.Contains(term)))
                    .OrderByDescending(t => t.ID).Take(SearchTake)
                    .Select(t => new ValueTuple<int, string>(t.ID, t.Title)).ToListAsync(cancellationToken),

                CalendarEvent => await _db.CalendarEvents.AsNoTracking()
                    .Where(e => e.CompanyID == companyId && e.DeletedAt == null && (any || e.Title.Contains(term)))
                    .OrderByDescending(e => e.StartAt).Take(SearchTake)
                    .Select(e => new ValueTuple<int, string>(e.Id, e.Title)).ToListAsync(cancellationToken),

                _ => new List<(int, string)>(),
            };

            return rows.Select(r => new EntitySearchResult
            {
                EntityCode = def.Code,
                EntityId = r.Id,
                Label = r.Label,
                Url = BuildUrl(def.Code, r.Id),
            }).ToList();
        }

        // Resolve queries lifted from TM-2 TaskLinkResolver.ResolveAsync unchanged.
        public async Task<EntityResolveResult> ResolveAsync(string entityCode, int entityId, BusinessContext context, CancellationToken cancellationToken = default)
        {
            var def = GetDefinition(entityCode);
            int companyId = context.CompanyId;
            string? label = null;

            switch (entityCode)
            {
                case SalesInvoice:
                    label = await _db.SalesInvoices.AsNoTracking()
                        .Where(i => i.ID == entityId && i.CompanyID == companyId)
                        .Select(i => i.InvoiceNo ?? ("#" + i.ID)).FirstOrDefaultAsync(cancellationToken);
                    break;
                case PurchaseInvoice:
                    label = await _db.PurchaseInvoices.AsNoTracking()
                        .Where(i => i.ID == entityId && i.CompanyID == companyId)
                        .Select(i => i.InvoiceNo ?? ("#" + i.ID)).FirstOrDefaultAsync(cancellationToken);
                    break;
                case Quotation:
                    label = await _db.Quotations.AsNoTracking()
                        .Where(q => q.ID == entityId && q.CompanyID == companyId)
                        .Select(q => q.QuoteNo ?? ("#" + q.ID)).FirstOrDefaultAsync(cancellationToken);
                    break;
                case JournalEntry:
                    label = await _db.JournalEntries.AsNoTracking()
                        .Where(j => j.ID == entityId && j.CompanyID == companyId)
                        .Select(j => j.EntryNo).FirstOrDefaultAsync(cancellationToken);
                    break;
                case Customer:
                    label = await _db.Customers.AsNoTracking()
                        .Where(c => c.ID == entityId && c.CompanyID == companyId)
                        .Select(c => c.Name).FirstOrDefaultAsync(cancellationToken);
                    break;
                case Supplier:
                    label = await _db.Vendors.AsNoTracking()
                        .Where(v => v.ID == entityId && v.CompanyID == companyId)
                        .Select(v => v.Name).FirstOrDefaultAsync(cancellationToken);
                    break;
                case ManufWorkOrder:
                    label = await _db.ManufWorkOrders.AsNoTracking()
                        .Where(w => w.ID == entityId && w.CompanyID == companyId)
                        .Select(w => w.WoNo ?? ("#" + w.ID)).FirstOrDefaultAsync(cancellationToken);
                    break;
                case Item:
                    label = await _db.Items.AsNoTracking()
                        .Where(i => i.ID == entityId && i.CompanyID == companyId)
                        .Select(i => (i.ItemCode ?? "") + " — " + i.Name).FirstOrDefaultAsync(cancellationToken);
                    break;
                case Employee:
                    // Same deliberate deviation as the search above: no company filter (TM-2 behaviour).
                    label = await _db.Employee.AsNoTracking()
                        .Where(e => e.ID == entityId)
                        .Select(e => e.FullName).FirstOrDefaultAsync(cancellationToken);
                    break;
                case Project:
                    label = await _db.Projects.AsNoTracking()
                        .Where(p => p.ID == entityId && p.CompanyID == companyId)
                        .Select(p => p.Code + " — " + p.Name).FirstOrDefaultAsync(cancellationToken);
                    break;
                case PosOrder:
                    label = await _db.PosOrders.AsNoTracking()
                        .Where(o => o.ID == entityId && o.CompanyId == companyId)
                        .Select(o => o.ReceiptNo ?? ("#" + o.ID)).FirstOrDefaultAsync(cancellationToken);
                    break;

                // ---- Tasks & Calendar integration (TAB 4) ----
                case Task:
                    label = await _db.TaskItems.AsNoTracking()
                        .Where(t => t.ID == entityId && t.CompanyId == companyId)
                        .Select(t => t.Title).FirstOrDefaultAsync(cancellationToken);
                    break;

                // A LABEL only, and company-filtered. Resolving a title is not the same as being allowed to
                // OPEN the event: CalendarService.Visible() (organiser / company-scope / attendee) remains the
                // read decision, and nothing here bypasses it. A Personal event the caller may not see still
                // resolves to its title here — which is why this code is not in the record picker, and why the
                // agenda redacts rather than relying on this path.
                case CalendarEvent:
                    label = await _db.CalendarEvents.AsNoTracking()
                        .Where(e => e.Id == entityId && e.CompanyID == companyId && e.DeletedAt == null)
                        .Select(e => e.Title).FirstOrDefaultAsync(cancellationToken);
                    break;
            }

            bool found = label != null;
            return new EntityResolveResult
            {
                EntityCode = def.Code,
                EntityId = entityId,
                Label = label ?? ("#" + entityId),
                // A missing row gets no deep link — never navigate to a record that is gone.
                Url = found ? BuildUrl(def.Code, entityId) : null,
                Found = found,
                Definition = def,
            };
        }
    }
}