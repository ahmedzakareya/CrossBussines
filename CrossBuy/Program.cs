using CrossBuy.BL;
using CrossBuy.BL.Reporting;   // ADR-037: AddCrossBusinessReporting()
using CrossBuy.BL.Workspace;   // CrossBusiness Workspace: AddCrossBusinessWorkspace()
using CrossBuy.Hubs;
using CrossBuy.Models;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using Microsoft.AspNetCore.Authentication.Certificate;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Configuration;
using System.Globalization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;



var builder = WebApplication.CreateBuilder(args);

builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");

var mvcBuilder = builder.Services.AddControllersWithViews()
    .AddViewLocalization(LanguageViewLocationExpanderFormat.Suffix)
    .AddDataAnnotationsLocalization()
    // HM-1 (B-2): parse all posted decimal/double/float invariantly regardless of request culture.
    .AddMvcOptions(o => o.ModelBinderProviders.Insert(0, new CrossBuy.Models.Binders.InvariantNumberModelBinderProvider()));

// Render Arabic (and all Unicode) literally instead of &#xNNNN; entities. The HtmlEncoder still escapes the
// syntactically dangerous characters (< > & " '), so XSS protection is unchanged — but Arabic in @T(...) used
// inside inline <script>/textContent no longer leaks raw entity codes. Project-wide fix.
builder.Services.Configure<Microsoft.Extensions.WebEncoders.WebEncoderOptions>(o =>
    o.TextEncoderSettings = new System.Text.Encodings.Web.TextEncoderSettings(System.Text.Unicode.UnicodeRanges.All));

// Local dev: edit .cshtml and just refresh the browser — no rebuild needed.
// (Production keeps precompiled views for speed.)
if (builder.Environment.IsDevelopment())
{
    mvcBuilder.AddRazorRuntimeCompilation();
}

// HM-1: number-only culture normalization for Arabic. The Arabic UI, DATE formats, calendar and
// collation are left 100% UNTOUCHED — we only force Latin digits + "." decimal / "," group so that
// decimal round-trips (display via CurrentCulture AND POST model binding) are consistent.
static CultureInfo FixNumbers(CultureInfo c)
{
    var nf = c.NumberFormat;
    nf.NumberDecimalSeparator   = "."; nf.NumberGroupSeparator   = ",";
    nf.CurrencyDecimalSeparator = "."; nf.CurrencyGroupSeparator = ",";
    nf.PercentDecimalSeparator  = "."; nf.PercentGroupSeparator  = ",";
    nf.NativeDigits = new[] { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9" };
    nf.DigitSubstitution = DigitShapes.None;
    return c; // DateTimeFormat + Calendar intentionally NOT touched
}
var enCulture = new CultureInfo("en");                 // unchanged (already Latin/"." )
var arCulture = FixNumbers(new CultureInfo("ar"));     // mutable instance — numbers normalized, dates/RTL kept
var frCulture = new CultureInfo("fr");                 // unchanged
var appCultures = new[] { enCulture, arCulture, frCulture };

builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    // register the SAME normalized instances the middleware returns by name on each request
    options.DefaultRequestCulture = new RequestCulture(arCulture);
    options.SupportedCultures     = appCultures;
    options.SupportedUICultures   = appCultures;   // UI language unchanged
});

// Threads OUTSIDE the request pipeline (hosted services / scheduled / background / logging):
// keep English NUMBER+DATE formatting (no injected RTL marks in date strings / collation), but Arabic UI text.
CultureInfo.DefaultThreadCurrentCulture   = enCulture;
CultureInfo.DefaultThreadCurrentUICulture = arCulture;


builder.Services.AddDbContext<CrossDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")),
	ServiceLifetime.Scoped 
);




builder.Services.AddIdentity<Users, IdentityRole>()
    .AddEntityFrameworkStores<CrossDbContext>()
    .AddDefaultTokenProviders();

builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<IEmployeeService, EmployeeService>();
builder.Services.AddScoped<IFileManagerService, FileManagerService>();  // Company document library (Metronic file-manager)
builder.Services.AddScoped<ICommService, CommService>();          // Comm Hub — email (Metronic inbox)
builder.Services.AddScoped<IAnnouncementService, AnnouncementService>(); // Comm Hub P5 — announcements
builder.Services.AddScoped<IDocCommentService, DocCommentService>();     // Comm Hub P5 — document comments/timeline
// ---- Reporting Platform (ADR-037) ----
//
// ONE line by design: the whole platform's registration and lifetime story lives in
// ReportingServiceCollectionExtensions, so this shared file does not grow a 40-line block that collides on every
// merge. No hosted service is registered (see ReportScheduleService.cs), and no existing registration changes —
// this is purely additive.
builder.Services.AddCrossBusinessReporting(reporting => reporting
    // The archive keeps its bytes beside the product's other uploads. WebRootPath is null when wwwroot is absent
    // (some test hosts), so ContentRootPath is the fallback rather than letting the archive land in the binary dir.
    .UseArchiveRoot(Path.Combine(
        builder.Environment.WebRootPath ?? builder.Environment.ContentRootPath, "uploads", "reports"))
    // Report permissions are FAIL-CLOSED: a permission key with no role mapping is denied. Module report keys are
    // mapped when those modules add their data sources. See IReportPermissionEvaluator — replacing the role-map
    // evaluator with one backed by the platform permission provider stays deferred until the first tab completes
    // B6, by owner decision.
    .MapPermission(CrossBuy.BL.Reporting.ReportPermissions.Administer, "Admin", "SuperAdmin")

    // ---- R1 ACTIVATION: the Business Event log ----
    //
    // THREE TIERS, MAPPED SEPARATELY AND DELIBERATELY NARROWLY.
    //
    // Holding `view` lets a caller see WHICH records changed and WHEN across the whole company, WITHOUT the
    // per-record permission check the kernel's own timeline applies per row (a cross-entity report cannot do
    // 25 000 per-row permission evaluations — the reasoning is in BusinessEventsDataset's header). It is
    // therefore an AUDIT right, and it is mapped to administrators only, never to an ordinary module role.
    //
    // `confidential` additionally reveals Confidential rows and the Payload column — a payload is a summary, but
    // a summary of a sales invoice still carries its total.
    // `restricted` additionally reveals Restricted and System rows.
    //
    // Narrow these further, or widen them, by editing THIS line — nothing in the platform changes.
    .MapPermission(CrossBuy.BL.Reporting.BusinessEventsReportPermissions.View, "Admin", "SuperAdmin", "Auditor")
    .MapPermission(CrossBuy.BL.Reporting.BusinessEventsReportPermissions.Confidential, "Admin", "SuperAdmin")
    .MapPermission(CrossBuy.BL.Reporting.BusinessEventsReportPermissions.Restricted, "SuperAdmin")

    // ---- R2 ACTIVATION: the MODULE datasets ----
    //
    // One key per module for the datasets themselves, and a SEPARATE narrower key for the money-sensitive
    // columns inside them. The split is the point: reading that a customer was billed 10 000 is a different
    // grant from reading that the company earned 800 on it, and reading how many units are on a shelf is a
    // different grant from reading what they cost.
    //
    //   accounting.reports.view          the five Accounting datasets (registers + aging + profitability rows)
    //   accounting.reports.profitability additionally reveals Revenue / COGS / Margin / Margin %
    //   inventory.reports.view           stock on hand and the movement register, QUANTITIES only
    //   inventory.reports.cost           additionally reveals AvgCost / TotalValue / UnitCost / movement value
    //   crm.reports.view                 leads and opportunities
    //
    // Accountant and Storekeeper are mapped here as the ordinary operational roles for their own module's
    // registers. The COST and PROFITABILITY tiers are deliberately NOT given to them — those stay with
    // administration until an owner decides otherwise. Narrow these further, or widen them, by editing THESE
    // lines; nothing in the platform changes.
    .MapPermission(CrossBuy.BL.Reporting.AccountingReportPermissions.View,
        "Admin", "SuperAdmin", "Auditor", "Accountant")
    .MapPermission(CrossBuy.BL.Reporting.AccountingReportPermissions.Profitability,
        "Admin", "SuperAdmin")
    .MapPermission(CrossBuy.BL.Reporting.InventoryReportPermissions.View,
        "Admin", "SuperAdmin", "Auditor", "Storekeeper")
    .MapPermission(CrossBuy.BL.Reporting.InventoryReportPermissions.Cost,
        "Admin", "SuperAdmin")
    .MapPermission(CrossBuy.BL.Reporting.CrmReportPermissions.View,
        "Admin", "SuperAdmin", "Auditor", "Sales"));

// HR Product Batch 1 — employee onboarding. Appended in TAB-2's own region, next to the module's other
// registration. The service is scoped because it takes the request's DbContext and BusinessContext.
builder.Services.AddScoped<CrossBuy.BL.Hr.IReportClockShim, CrossBuy.BL.Hr.SystemOnboardingClock>();
builder.Services.AddScoped<CrossBuy.BL.Hr.IEmployeeOnboardingService, CrossBuy.BL.Hr.EmployeeOnboardingService>();

// ---- CrossBusiness Workspace (R1–R3) ----
//
// ONE line, same reasoning as the Reporting block above: this file is edited by several tabs at once and a
// multi-line block collides on every merge.
//
// The Workspace is a READ MODEL over services registered elsewhere in this file — it owns no table, no writer
// and no business rule, and it registers no hosted service. Its two extension points
// (IWorkspaceFavoritesSource / IWorkspaceActivitySource) let a module contribute rows by registering AFTER
// this call, without the Workspace learning about that module.
builder.Services.AddCrossBusinessWorkspace();

builder.Services.AddScoped<ICalendarService, CalendarService>();  // Company calendar (Metronic FullCalendar)
builder.Services.AddScoped<CrossBuy.BL.TasksCalendar.IWorkspaceAgendaService, CrossBuy.BL.TasksCalendar.WorkspaceAgendaService>();
builder.Services.AddScoped<CrossBuy.BL.Uat.IUatDatasetSeeder, CrossBuy.BL.Uat.UatDatasetSeeder>();
builder.Services.AddScoped<CrossBuy.BL.TasksCalendar.ITaskChecklistService, CrossBuy.BL.TasksCalendar.TaskChecklistService>();
builder.Services.AddScoped<CrossBuy.BL.TasksCalendar.ITaskDependencyService, CrossBuy.BL.TasksCalendar.TaskDependencyService>();
builder.Services.AddScoped<CrossBuy.BL.TasksCalendar.ITaskTemplateService, CrossBuy.BL.TasksCalendar.TaskTemplateService>();
builder.Services.AddScoped<CrossBuy.BL.TasksCalendar.ITaskCalendarEventPublisher, CrossBuy.BL.TasksCalendar.TaskCalendarEventPublisher>();
builder.Services.AddScoped<CrossBuy.BL.TasksCalendar.ITaskNotificationService, CrossBuy.BL.TasksCalendar.TaskNotificationService>();
builder.Services.AddScoped<CrossBuy.BL.TasksCalendar.ITaskEscalationService, CrossBuy.BL.TasksCalendar.TaskEscalationService>();   // overdue -> direct-manager escalation
// SHF-01 shared-file change, approved for this closure and bounded to ONE call: the Tasks/Calendar
// worker composition. It registers ITaskOverdueSweepService, which had an implementation and tests but
// no registration and therefore no caller. Nothing above or below is reordered.
CrossBuy.BL.TasksCalendar.TasksCalendarRegistration.AddTasksCalendarWorkers(builder.Services);
builder.Services.AddScoped<CrossBuy.BL.TasksCalendar.ICalendarSchedulingService, CrossBuy.BL.TasksCalendar.CalendarSchedulingService>();
builder.Services.AddScoped<ICompanyService, CompanyService>();
builder.Services.AddScoped<IJobTitles, JobTitleService>();
builder.Services.AddScoped<IAdministrativeBodiesCompanyService, AdministrativeBodiesCompanyService>();
builder.Services.AddScoped<IAdministrativeStructureService, AdministrativeStructureService>();
builder.Services.AddScoped<IPolicesService, PolicesService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<IChatService, ChatService>();
builder.Services.AddScoped<ILeaveDashboardService, LeaveDashboardService>();
builder.Services.AddScoped<ILeaveAccrualService, LeaveAccrualService>();
builder.Services.AddScoped<IHrDocumentService, HrDocumentService>();
builder.Services.AddScoped<IRecruitmentService, RecruitmentService>();   // Recruitment R0 — hiring pipeline + required-document catalog
builder.Services.AddScoped<IFinalSettlementService, FinalSettlementService>();
builder.Services.AddScoped<IHolidayService, HolidayService>();
builder.Services.AddScoped<IAttendanceService, AttendanceService>();
// ---- Accounting authorization composition (Phase 3B). The MINIMUM registration chain the two
//      Accounting controllers need to resolve at runtime — nothing broader. Deliberately NOT the
//      full Platform Kernel composition, AI, certification-runtime or module DI, which stay deferred.
builder.Services.AddScoped<CrossBuy.BL.Platform.ICompanyScopeHolder, CrossBuy.BL.Platform.CompanyScopeHolder>();
builder.Services.AddScoped<CrossBuy.BL.Platform.IBusinessContextFactory, CrossBuy.BL.Platform.BusinessContextFactory>();
builder.Services.AddScoped<CrossBuy.BL.Platform.IBusinessContextAccessor, CrossBuy.BL.Platform.BusinessContextAccessor>();
// this branch at all — AssertSafe above has already refused a certification database on a
// non-certification host, so this can only ever be true for the certification dataset.
var certificationRuntime = CrossBuy.BL.Platform.CertificationDataContract.IsCertificationRuntime(
    builder.Environment.IsDevelopment(),
    Environment.GetEnvironmentVariable(CrossBuy.BL.Platform.CertificationDataContract.EnableEnvironmentVariable),
    builder.Configuration.GetConnectionString("DefaultConnection"));

// The SAME decision the registrations below use is carried to /BusinessEventMonitor/Runtime, which the
// determinism gate reads to choose its contract. Registering the resolved value (rather than letting the
// endpoint re-evaluate it) is what guarantees "certification mode" and "writers suppressed" always
// describe this one process.
builder.Services.AddSingleton(new CrossBuy.BL.Platform.CertificationRuntimeState(certificationRuntime));

builder.Services.AddSingleton<CrossBuy.BL.Platform.ICompanyBypassAudit, CrossBuy.BL.Platform.LoggingCompanyBypassAudit>();
builder.Services.AddSingleton<CrossBuy.BL.Platform.ICompanyBypassPolicy, CrossBuy.BL.Platform.CompanyBypassPolicy>();
builder.Services.AddSingleton<CrossBuy.BL.Platform.IRuntimeInstanceInfo, CrossBuy.BL.Platform.RuntimeInstanceInfo>();
// Stage 0 Batch B — single-worker-process control. The gate holds ONE application lock for the
// process lifetime, so a second instance stays on standby instead of duplicating background work.
// Singleton for that reason: a scoped gate would take a new lock per request. The concrete type is
// registered too because WorkerGate is IAsyncDisposable and the lease must be released once, by the
// same instance that took it.
builder.Services.Configure<CrossBuy.BL.Platform.RuntimeOptions>(builder.Configuration.GetSection("Runtime"));
builder.Services.AddSingleton<CrossBuy.BL.Platform.WorkerGate>();
builder.Services.AddSingleton<CrossBuy.BL.Platform.IWorkerGate>(sp => sp.GetRequiredService<CrossBuy.BL.Platform.WorkerGate>());

// The company list every multi-company worker iterates. SCOPED, because it reads CrossDbContext.
//
// This was MISSING, and three committed workers already resolved it with GetRequiredService:
// TaskGeneratorHostedService, TaskScheduleMatchHostedService and TaskEscalationHostedService. An
// unregistered GetRequiredService throws, each of those catches and logs inside its own tick loop, and
// the result was three workers that failed on every cycle forever while looking like they were running.
// Their tests did not catch it because they inject a stub company scope instead of resolving one.
builder.Services.AddScoped<CrossBuy.BL.Platform.IWorkerCompanyScope, CrossBuy.BL.Platform.WorkerCompanyScope>();
builder.Services.AddScoped<CrossBuy.BL.Platform.ICompanyIsolationBypass, CrossBuy.BL.Platform.CompanyIsolationBypass>();
builder.Services.AddScoped<CrossBuy.BL.Platform.IEventDispatchStore, CrossBuy.BL.Platform.SqlEventDispatchStore>();            // ADR-003: per-consumer outbox state
builder.Services.AddScoped<CrossBuy.BL.Platform.IBusinessEventService, CrossBuy.BL.Platform.BusinessEventService>();           // ADR-001: in-transaction event recording
builder.Services.AddScoped<CrossBuy.BL.Platform.IPlatformAdminIdentity, CrossBuy.BL.Platform.IdentityPlatformAdminIdentity>();
builder.Services.AddSingleton<CrossBuy.BL.Platform.IPlatformPermissionVocabularyRegistry, CrossBuy.BL.Platform.PlatformPermissionVocabularyRegistry>();
builder.Services.AddScoped<CrossBuy.BL.Platform.IPlatformGrantWriter, CrossBuy.BL.Platform.PlatformGrantWriter>();
builder.Services.AddScoped<CrossBuy.BL.Platform.ITimelineProjectionService, CrossBuy.BL.Platform.TimelineProjectionService>(); // the ONLY timeline read path
builder.Services.AddScoped<CrossBuy.BL.Platform.ILegacyTimelineAdapter, CrossBuy.BL.Platform.SalesInvoiceLegacyTimelineAdapter>();
builder.Services.AddScoped<CrossBuy.BL.Platform.ILegacyTimelineAdapter, CrossBuy.BL.Platform.CustomerLegacyTimelineAdapter>();               // slice 2
builder.Services.AddScoped<CrossBuy.BL.Platform.ILegacyTimelineAdapter, CrossBuy.BL.Platform.PurchaseInvoiceLegacyTimelineAdapter>();       // slice 2
builder.Services.AddScoped<CrossBuy.BL.Platform.ILegacyTimelineAdapter, CrossBuy.BL.Platform.ManufWorkOrderLegacyTimelineAdapter>();        // slice 2
builder.Services.AddScoped<CrossBuy.BL.Platform.IBusinessEventMonitorService, CrossBuy.BL.Platform.BusinessEventMonitorService>(); // Stage 0 Batch B: operator read model + guarded retry

// The transactional-outbox dispatcher. Nothing else drains BusinessEventDispatch, so without this the
// timeline, notification and AI consumers are constructed by no one and the fan-out is inert.
//
// Options bind from Platform:EventDispatch; every value has a safe class default, so a deployment with
// no section behaves exactly as the committed defaults describe.
//
// SUPPRESSED IN CERTIFICATION RUNTIME. The dispatcher mutates state - it claims rows, writes timeline
// and notification projections, and advances per-consumer dispatch state - and a conformance capture
// must OBSERVE stable data rather than consume it, or two runs of the same page disagree.
// `certificationRuntime` is the value HEAD already computes and carries; it is true only when
// development, the explicit opt-in variable and the certification catalogue ALL agree, so this cannot
// accidentally disable the dispatcher on a normal host. Only this worker is gated here.
builder.Services.Configure<CrossBuy.BL.Platform.BusinessEventDispatchOptions>(
    builder.Configuration.GetSection("Platform:EventDispatch"));
if (!certificationRuntime) builder.Services.AddHostedService<CrossBuy.BL.Platform.BusinessEventDispatchWorker>();
builder.Services.AddScoped<CrossBuy.BL.Platform.IRequestCompanyResolver, CrossBuy.BL.Platform.RequestCompanyResolver>();   // D1/CORRECTION-005: validated company source
builder.Services.AddScoped<CrossBuy.BL.Platform.IPlatformRoleDirectory, CrossBuy.BL.Platform.PlatformRoleDirectory>();
builder.Services.AddScoped<CrossBuy.BL.Platform.IOrgHierarchy, CrossBuy.BL.Platform.OrgHierarchy>();
builder.Services.AddScoped<CrossBuy.BL.Platform.IBootstrapAccessPolicyReader, CrossBuy.BL.Platform.BootstrapAccessPolicyReader>();

// Each access service behind the concrete type, its module interface, and IModuleAccessService — the
// last is what AccountingApiAuthorization resolves by scope, and what ApiPermAttribute asks for HR.
builder.Services.AddScoped<AccountingAccessService>();
builder.Services.AddScoped<IAccountingAccessService>(sp => sp.GetRequiredService<AccountingAccessService>());
builder.Services.AddScoped<CrossBuy.BL.Platform.IModuleAccessService>(sp => sp.GetRequiredService<AccountingAccessService>());
builder.Services.AddScoped<HrAccessService>();
builder.Services.AddScoped<IHrAccessService>(sp => sp.GetRequiredService<HrAccessService>());
builder.Services.AddScoped<CrossBuy.BL.Platform.IModuleAccessService>(sp => sp.GetRequiredService<HrAccessService>());

// The accounting API gate. It resolves the accounting module out of IEnumerable<IModuleAccessService>
// by scope, so it adds no permission rule of its own and cannot drift from the MVC screens decisions.
builder.Services.AddScoped<IAccountingApiAuthorization, AccountingApiAuthorization>();

// Projects authorization (Phase 3C-1). ProjectsAccessService needs the role directory and the
// accounting service, both already registered above, so this adds only the Projects surface.
builder.Services.AddScoped<ProjectsAccessService>();
builder.Services.AddScoped<IProjectsAccessService>(sp => sp.GetRequiredService<ProjectsAccessService>());
builder.Services.AddScoped<CrossBuy.BL.Platform.IModuleAccessService>(sp => sp.GetRequiredService<ProjectsAccessService>());
builder.Services.AddScoped<ILeaveWorkflowService, LeaveWorkflowService>();
builder.Services.AddScoped<IEmployeeRequestService, EmployeeRequestService>();
builder.Services.AddScoped<IAppraisalService, AppraisalService>();
builder.Services.AddScoped<ITrainingService, TrainingService>();
builder.Services.AddScoped<IBrandService, BrandService>();
builder.Services.AddScoped<ICrmCustomFieldService, CrmCustomFieldService>();
builder.Services.AddScoped<ICrmAutomationService, CrmAutomationService>();
builder.Services.AddScoped<IProjectService, ProjectService>();
builder.Services.AddScoped<IBoqService, BoqService>();   // Projects & Contracting P1 BOQ
builder.Services.AddScoped<IContractService, ContractService>();   // Projects & Contracting P2 contract (advance/retention)
builder.Services.AddScoped<IProgressService, ProgressService>();   // Projects & Contracting P3 execution/progress (operational)
builder.Services.AddScoped<IProgressBillingService, ProgressBillingService>();   // Projects & Contracting P4 progress billing (المستخلص)
builder.Services.AddScoped<IProjectMaterialIssueService, ProjectMaterialIssueService>();   // Projects & Contracting P5-أ material issue (actual cost)
builder.Services.AddScoped<IProjectLaborService, ProjectLaborService>();   // Projects & Contracting P5-ب project labor (actual cost)
builder.Services.AddScoped<IProjectBudgetService, ProjectBudgetService>();   // Projects & Contracting P6-أ budget-vs-actual report (read-only)
builder.Services.AddScoped<ISubcontractBillingService, SubcontractBillingService>();   // Projects & Contracting P6-ج subcontractor billing
builder.Services.AddScoped<IVariationOrderService, VariationOrderService>();   // Projects & Contracting P6-د variation orders
builder.Services.AddScoped<IEquipmentDepreciationService, EquipmentDepreciationService>();   // Projects & Contracting P6-هـ owned-equipment depreciation allocation
builder.Services.AddScoped<IMaintenanceService, MaintenanceService>();
builder.Services.AddScoped<IPosSetupService, PosSetupService>();
builder.Services.AddScoped<IPosOrderService, PosOrderService>();
// POS authorization (Phase 3C-2). Three registrations, not one: the concrete type, the module
// interface used by POS screens, and IModuleAccessService — the last is how the platform finds
// POS by scope, and without it the service resolves fine while authorizing nothing.
builder.Services.AddScoped<PosAccessService>();
builder.Services.AddScoped<IPosAccessService>(sp => sp.GetRequiredService<PosAccessService>());
builder.Services.AddScoped<CrossBuy.BL.Platform.IModuleAccessService>(sp => sp.GetRequiredService<PosAccessService>());

// Tasks authorization (Phase 3C-4). TasksAccessService asks the platform permission provider for
// the linked-entity check, so the provider and the entity registry it reads come with it.
builder.Services.AddScoped<CrossBuy.BL.Platform.IEntityRegistry, CrossBuy.BL.Platform.EntityRegistry>();

// ---- Central Document Platform: the SHARED SECURITY SPINE only ----
//
// No document schema, no versions, no metadata, no events - those belong to the document domain and are
// not registered here. What is registered is the seam that domain will call, so it does not have to
// reopen the tenant/owner decisions to build on it.
//
// The resolver takes EVERY IDocumentOwnerResolver and EVERY IModuleAccessService, so onboarding a family
// is one registration below rather than an edit to the resolver: the centre never learns a module name.
builder.Services.AddScoped<CrossBuy.BL.Platform.IDocumentOwnerResolver, CrossBuy.BL.Platform.EmployeeDocumentOwnerResolver>();
builder.Services.AddScoped<CrossBuy.BL.Platform.IDocumentAccessResolver, CrossBuy.BL.Platform.DocumentAccessResolver>();

// Storage lives OUTSIDE wwwroot, deliberately: anything under the web root is reachable by
// UseStaticFiles, and a static pipeline cannot ask who is calling. A file stored here has no URL at all.
// Singleton because it holds only a root path. Nothing is migrated onto it in this batch.
builder.Services.AddSingleton<CrossBuy.BL.Platform.IDocumentStorage>(_ =>
    new CrossBuy.BL.Platform.LocalDocumentStorage(
        System.IO.Path.Combine(builder.Environment.ContentRootPath, "App_Data", "documents")));

// ---------------- Communication Platform (ADR-030) — ACTIVATED ----------------
//
// Dormant until now: the code, the EF mapping (CrossDbContext -> CommunicationModel.Configure) and the
// schema slice were all committed, but nothing registered the services, so every platform service
// resolved to null and every consumer reported the platform "not activated".
//
// WHY IT IS SAFE TO ACTIVATE, audited before flipping it on:
//   * EVERY registration inside is AddScoped. There is NO AddHostedService and NO BackgroundService in
//     AddCommunicationPlatform, so activation starts no worker and no timer. Nothing begins polling.
//   * It needs IEntityRegistry (the line above), IOrgHierarchy, IPlatformPermissionProvider and
//     ITimelineProjectionService. All four are already registered here and all are Scoped.
//   * The business-event bridge is NOT part of this call. AddCommunicationPlatform binds
//     NullCommBusinessEventBridge; the real PlatformBusinessEventBridge is a SEPARATE opt-in
//     (UseBusinessEventBridge) and is deliberately NOT called, because it makes RecordAsync a hard
//     dependency of every comment and CLAUDE.md records that coupling failing a whole screen when a
//     kernel table is missing (HM-D44/D45, HM-D53). Coordinate with the kernel owner before enabling it.
//
// SQL BEFORE CODE: deploy/sql/communication_platform_slice_001.sql (14 additive tables, idempotent) is
// applied first. Re-running it creates nothing, which is how it is meant to behave.
CrossBuy.BL.Communication.CommunicationPlatformRegistration.AddCommunicationPlatform(builder.Services, builder.Configuration);

// Central Document Platform (TAB-3). ONE registration extension method, per SHF-01. It registers a
// single scoped service and starts nothing - no hosted service, no timer, no sweep. It depends on the
// document spine registered above and never re-registers it.
CrossBuy.BL.Documents.DocumentPlatformRegistration.AddDocumentPlatform(builder.Services);
builder.Services.AddScoped<CrossBuy.BL.Platform.IPlatformPermissionProvider, CrossBuy.BL.Platform.PlatformPermissionProvider>();
builder.Services.AddScoped<CrossBuy.BL.Platform.IModulePermissionAdapter, CrossBuy.BL.Platform.DefaultPermissionAdapter>();

// The ten per-module adapters. Until now DefaultPermissionAdapter above was the ONLY registration, so
// PlatformPermissionProvider found no adapter for any scope but "None" and denied - correctly and fail
// closed, but for a configuration reason rather than a policy one. That denied 11 of the 14 registered
// entities, including all six that support a timeline, which is why the governed recent feed came back
// empty for every caller regardless of what they were entitled to see.
//
// NO SEMANTIC CHANGE. Each adapter delegates to its module's existing IModuleAccessService; the mapping
// from the three canonical platform actions onto module action strings was written with the adapters and
// is not touched here. Registering them lets the module answer instead of the platform refusing to ask.
//
// STILL FAIL CLOSED, in two ways worth stating because both look like bugs from outside:
//   * a scope with no adapter is unchanged - it denies, and PlatformPermissionVocabularyTests holds that;
//   * an adapter whose module has no IModuleAccessService registered denies too, naming the module. Four
//     are in that state today (Inventory, Manufacturing which delegates to Inventory, Crm, Communication):
//     their access services exist but are not registered as IModuleAccessService. Registering the adapter
//     changes only WHICH honest denial they get, never whether they are denied.
builder.Services.AddScoped<CrossBuy.BL.Platform.IModulePermissionAdapter, CrossBuy.BL.ModulePermissions.AccountingPermissionAdapter>();
builder.Services.AddScoped<CrossBuy.BL.Platform.IModulePermissionAdapter, CrossBuy.BL.ModulePermissions.InventoryPermissionAdapter>();
builder.Services.AddScoped<CrossBuy.BL.Platform.IModulePermissionAdapter, CrossBuy.BL.ModulePermissions.ManufacturingPermissionAdapter>();
builder.Services.AddScoped<CrossBuy.BL.Platform.IModulePermissionAdapter, CrossBuy.BL.ModulePermissions.CrmPermissionAdapter>();
builder.Services.AddScoped<CrossBuy.BL.Platform.IModulePermissionAdapter, CrossBuy.BL.ModulePermissions.PosPermissionAdapter>();
builder.Services.AddScoped<CrossBuy.BL.Platform.IModulePermissionAdapter, CrossBuy.BL.ModulePermissions.HrPermissionAdapter>();
builder.Services.AddScoped<CrossBuy.BL.Platform.IModulePermissionAdapter, CrossBuy.BL.ModulePermissions.ProjectsPermissionAdapter>();
builder.Services.AddScoped<CrossBuy.BL.Platform.IModulePermissionAdapter, CrossBuy.BL.ModulePermissions.CalendarPermissionAdapter>();
builder.Services.AddScoped<CrossBuy.BL.Platform.IModulePermissionAdapter, CrossBuy.BL.ModulePermissions.TasksPermissionAdapter>();
builder.Services.AddScoped<CrossBuy.BL.Platform.IModulePermissionAdapter, CrossBuy.BL.ModulePermissions.CommunicationPermissionAdapter>();

// Func<>, NOT the provider itself: the provider's adapters depend on IEnumerable<IModuleAccessService>,
// which contains this very service — a cycle that hung startup with no exception. The Func defers
// resolution past construction.
builder.Services.AddScoped<Func<CrossBuy.BL.Platform.IPlatformPermissionProvider>>(
    sp => () => sp.GetRequiredService<CrossBuy.BL.Platform.IPlatformPermissionProvider>());

builder.Services.AddScoped<CrossBuy.BL.TasksAccessService>();
builder.Services.AddScoped<CrossBuy.BL.ITasksAccessService>(sp => sp.GetRequiredService<CrossBuy.BL.TasksAccessService>());
builder.Services.AddScoped<CrossBuy.BL.Platform.IModuleAccessService>(sp => sp.GetRequiredService<CrossBuy.BL.TasksAccessService>());

builder.Services.AddScoped<CrossBuy.BL.CalendarAccessService>();
builder.Services.AddScoped<CrossBuy.BL.ICalendarAccessService>(sp => sp.GetRequiredService<CrossBuy.BL.CalendarAccessService>());
builder.Services.AddScoped<CrossBuy.BL.Platform.IModuleAccessService>(sp => sp.GetRequiredService<CrossBuy.BL.CalendarAccessService>());

// Communication, registered exactly as the five module access services above are: the concrete type,
// the module's own interface, and IModuleAccessService - all three resolving the SAME scoped instance.
//
// This is the layer the Communication scope was missing. CommunicationPermissionAdapter has been
// registered since the adapters landed, but an adapter with no IModuleAccessService behind it denies
// with "No IModuleAccessService is registered for scope 'Communication'" - fail closed and honest,
// but the module was never actually asked. Now it is.
//
// It takes no IPlatformPermissionProvider, so it adds nothing to the resolution cycle that
// TasksAccessService and CalendarAccessService break with Func<>.
builder.Services.AddScoped<CrossBuy.BL.CommunicationAccessService>();
builder.Services.AddScoped<CrossBuy.BL.ICommunicationAccessService>(sp => sp.GetRequiredService<CrossBuy.BL.CommunicationAccessService>());
builder.Services.AddScoped<CrossBuy.BL.Platform.IModuleAccessService>(sp => sp.GetRequiredService<CrossBuy.BL.CommunicationAccessService>());
builder.Services.AddScoped<ITaskService, TaskService>();   // TM-1: task management
builder.Services.AddScoped<ITaskLinkResolver, TaskLinkResolver>();   // TM-2: polymorphic record link
builder.Services.AddScoped<ITimesheetService, TimesheetService>();   // TM-3: timesheet
builder.Services.AddScoped<IEmployeeCostService, EmployeeCostService>();   // TM-4: shared hourly cost
builder.Services.AddScoped<ITaskCostService, TaskCostService>();   // TM-4: task costing + WO labor posting
builder.Services.AddScoped<ITaskBillingService, TaskBillingService>();   // TM-5: task billing
builder.Services.AddScoped<ITaskGeneratorService, TaskGeneratorService>();   // TM-7: auto-task rules
builder.Services.AddScoped<ITaskScheduleMatcher, TaskScheduleMatcher>();   // TM-9-ب: scheduled-task matcher
builder.Services.AddScoped<IStoreCatalogService, StoreCatalogService>();   // E-commerce Phase 1: storefront catalog (display only)
builder.Services.AddSingleton<IIdProtector, IdProtector>();                 // encrypts entity IDs in public storefront URLs
builder.Services.AddScoped<ITaskReportService, TaskReportService>();   // TM-8: reports
builder.Services.AddScoped<IChartOfAccountsService, ChartOfAccountsService>();
builder.Services.AddScoped<IFiscalPeriodService, FiscalPeriodService>();
builder.Services.AddScoped<IJournalEntryService, JournalEntryService>();
builder.Services.AddScoped<IGeneralLedgerService, GeneralLedgerService>();
builder.Services.AddScoped<ICostCenterService, CostCenterService>();
builder.Services.AddScoped<IAccountingPostingService, AccountingPostingService>();
builder.Services.AddScoped<IReceivableService, ReceivableService>();
builder.Services.AddScoped<IPayableService, PayableService>();
builder.Services.AddScoped<IAccountingDashboardService, AccountingDashboardService>();
builder.Services.AddScoped<IExecutiveDashboardService, ExecutiveDashboardService>();
builder.Services.AddScoped<ICurrencyService, CurrencyService>();
builder.Services.AddScoped<ICurrencyRounding, CurrencyRounding>();   // HM-2: single source of currency rounding precision
builder.Services.AddScoped<IFxRevaluationService, FxRevaluationService>();
builder.Services.AddScoped<IBankService, BankService>();
builder.Services.AddScoped<IFixedAssetService, FixedAssetService>();
builder.Services.AddScoped<ITaxService, TaxService>();
builder.Services.AddScoped<IEtaInvoiceService, EtaInvoiceServiceStub>();
builder.Services.AddScoped<IFinancialStatementService, FinancialStatementService>();
builder.Services.AddScoped<IClosingService, ClosingService>();
builder.Services.AddScoped<IItemService, ItemService>();
builder.Services.AddScoped<IWarehouseService, WarehouseService>();
// The canonical bill-of-materials explosion. One registration, one implementation: POS RecipeAtSale,
// the Bundle route, immediate production, work-order planning, MRP and standard costing all resolve
// THIS instance, which is what stops one recipe from consuming different quantities down different
// routes. Scoped, like every service that reads through the request's CrossDbContext.
builder.Services.AddScoped<IBomExplosionService, BomExplosionService>();
// Physical consumption, recorded separately from the payment fact. Scoped like every request-bound
// service; it writes stock only through StockService and events only through the platform.
builder.Services.AddScoped<IPosPreparationService, PosPreparationService>();
builder.Services.AddScoped<IStockService, StockService>();
builder.Services.AddScoped<IManufService, ManufService>();
builder.Services.AddScoped<IProcurementService, ProcurementService>();
builder.Services.AddScoped<ISellingService, SellingService>();
builder.Services.AddScoped<IPricingService, PricingService>();
builder.Services.AddScoped<IShelfLabelService, ShelfLabelService>();   // HM-4: EAN-13 SVG shelf-label generator (zero dependency)
builder.Services.AddScoped<IThreeWayMatchService, ThreeWayMatchService>();
builder.Services.AddScoped<ICrmService, CrmService>();
builder.Services.AddScoped<ICrmCustomerLink, CrmCustomerLink>();
// CRM authorization. THREE registrations resolving ONE scoped instance, the same shape Tasks and
// Calendar use: registering IModuleAccessService with its own AddScoped<,> would hand a request two
// CrmAccessService objects, two role reads and two caches, and the two could disagree within one call.
//
// IModuleAccessService is what makes CrmPermissionAdapter able to answer at all. Until now the adapter
// was registered with nothing behind it, so PlatformPermissionProvider denied the Crm scope for a
// configuration reason rather than a policy one.
builder.Services.AddScoped<CrmAccessService>();
builder.Services.AddScoped<ICrmAccessService>(sp => sp.GetRequiredService<CrmAccessService>());
builder.Services.AddScoped<CrossBuy.BL.Platform.IModuleAccessService>(sp => sp.GetRequiredService<CrmAccessService>());
// Inventory authorization. THREE registrations resolving ONE scoped instance, the shape Accounting,
// Tasks, Calendar and CRM already use. Registering IModuleAccessService with its own AddScoped<,> would
// give a request two InventoryAccessService objects with two role reads that can disagree in one call.
//
// IModuleAccessService is what lets InventoryPermissionAdapter answer at all - and, because
// ManufacturingPermissionAdapter deliberately sets ModuleScope => ScopeInventory, it is also the
// authority Manufacturing resolves through. One engine, two adapters.
builder.Services.AddScoped<InventoryAccessService>();
builder.Services.AddScoped<IInventoryAccessService>(sp => sp.GetRequiredService<InventoryAccessService>());
builder.Services.AddScoped<CrossBuy.BL.Platform.IModuleAccessService>(sp => sp.GetRequiredService<InventoryAccessService>());
builder.Services.AddScoped<IInventoryApprovalService, InventoryApprovalService>();

// Approvals read platform (TAB-6, narrow read ownership). The reusable cross-silo inbox: it consumes
// the three module readers registered above and holds no CrossDbContext of its own, so it cannot
// query a module table even by accident. Read-only - approve/reject stay with the module services.
builder.Services.AddScoped<CrossBuy.BL.Approvals.IApprovalInboxService, CrossBuy.BL.Approvals.ApprovalInboxService>();

// Projects membership writer (TAB-6, narrow Projects foundation ownership). The ONE registration this
// pass adds. dbo.ProjectMembers had two readers and no writer, so a company that configured its first
// Projects role would have locked every non-role-holder out of its own projects. This service holds no
// permission rule of its own - it asks IProjectsAccessService, registered above, for every decision.
builder.Services.AddScoped<IProjectMembershipService, ProjectMembershipService>();
builder.Services.AddScoped<IOpeningBalanceService, OpeningBalanceService>();
builder.Services.AddScoped<IIntegrityCheckService, IntegrityCheckService>();
builder.Services.AddHostedService<IntegrityCheckHostedService>();
builder.Services.AddHostedService<CrmReminderHostedService>();
builder.Services.AddHostedService<TaskGeneratorHostedService>();   // TM-7
builder.Services.AddHostedService<TaskScheduleMatchHostedService>();   // TM-9-ب: scheduled-task matcher
// Overdue manager escalation. It suppresses ITSELF when the host is a certification runtime, via
// CertificationRuntimeState.BackgroundWritersSuppressed, so this registration stays unconditional.
// Deliberately NOT gated here: Program.cs carries exactly one gated hosted service (the event
// dispatcher), an invariant asserted by BusinessEventDispatchWorkerCompositionTests. Suppressing
// inside the worker is also stronger, because it holds however the service is composed.
builder.Services.AddHostedService<CrossBuy.BL.TasksCalendar.TaskEscalationHostedService>();
builder.Services.AddSignalR();



// Persist DataProtection keys to a MACHINE-WIDE path (ProgramData) with a fixed app name + long
// lifetime, so the key ring is SHARED across every run mode (Kestrel CLI, IIS Express, VS) regardless
// of the running identity — and survives rebuilds. This is the root fix for "re-login on every rebuild":
// per-user %LOCALAPPDATA% keys aren't shared when IIS Express runs under a different identity, so each
// instance minted its own keys and couldn't decrypt the other's cookie. ProgramData is readable by all.
var keysDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CrossBuy", "keys");
try { Directory.CreateDirectory(keysDir); }
catch { keysDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrossBuy", "keys"); Directory.CreateDirectory(keysDir); }
builder.Services.AddDataProtection()
	.PersistKeysToFileSystem(new DirectoryInfo(keysDir))
	.SetApplicationName("CrossBuy")
	.SetDefaultKeyLifetime(TimeSpan.FromDays(3650));

// Store sessions in SQL Server (not in-memory) so Session["Employee"] survives restarts/rebuilds.
builder.Services.AddDistributedSqlServerCache(o =>
{
	o.ConnectionString = builder.Configuration.GetConnectionString("DefaultConnection");
	o.SchemaName = "dbo";
	o.TableName = "AppCache";
});
builder.Services.AddSession(options =>
{
	options.IdleTimeout = TimeSpan.FromHours(8);
	options.Cookie.HttpOnly = true;
	options.Cookie.IsEssential = true;
	options.Cookie.Name = "CrossBuy.Session";
	// the same login cookie must work whether the dev hits Kestrel (http://localhost:5000) or
	// IIS Express (https://localhost:44368) — a Secure-only cookie set on HTTPS is dropped on HTTP
	// (and vice-versa), forcing a re-login when switching instances. None + Lax keeps it usable on both.
	options.Cookie.SecurePolicy = CookieSecurePolicy.None;
	options.Cookie.SameSite = SameSiteMode.Lax;
});
builder.Services.AddAuthentication(
        CertificateAuthenticationDefaults.AuthenticationScheme)
    .AddCertificate()
    .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
    {
        var jwt = builder.Configuration.GetSection("Jwt");
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt["Issuer"],
            ValidAudience = jwt["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(jwt["Key"]!)),
            ClockSkew = TimeSpan.Zero
        };
        // allow SignalR (mobile) to pass the JWT via the access_token query string
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var accessToken = ctx.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) &&
                    ctx.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                {
                    ctx.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });

// CORS so the mobile app (and any external client) can reach the API
builder.Services.AddCors(options =>
{
    options.AddPolicy("MobileCors", policy =>
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITokenService, TokenService>();

// AI layer: typed client proxying to the Python AI service (crossbuy_ai, :8000).
// The shared secret is sent on every call; the Python side rejects without it.
builder.Services.AddHttpClient<IAiService, AiService>((sp, c) =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    c.BaseAddress = new Uri(cfg["AiService:BaseUrl"] ?? "http://localhost:8000");
    // Attached ONLY when a usable secret exists. `?? ""` sent an EMPTY credential, which is
    // fail-open: the request still left the estate and was merely refused at the far end.
    // IsUsableSecret is the committed egress rule (AiEgressPolicy), so both layers agree.
    var secret = cfg["AiService:Secret"];
    if (CrossBuy.BL.Platform.Ai.AiEgressPolicy.IsUsableSecret(secret))
        c.DefaultRequestHeaders.Add("X-AI-Secret", secret);
    c.Timeout = TimeSpan.FromSeconds(120);
});
// AI GOVERNANCE RUNTIME (integration hotfix). Committed HEAD registered IAiInsightsService while
// its IAiEgressPolicy dependency had no registration, so ValidateOnBuild threw before startup and
// the application could not boot at all. These are the only two the graph actually needs.
//
// Neither enables anything. AiProviderAuthority approves NOTHING by construction — it returns
// UnderAssessment or OwnerDecisionRequired, both of which deny — and AiEgressPolicy is the gate that
// asks it. Registering them restores the refusal path; without them there is no path at all.
builder.Services.AddSingleton<CrossBuy.BL.Platform.Ai.IAiProviderAuthority, CrossBuy.BL.Platform.Ai.AiProviderAuthority>();
builder.Services.AddScoped<CrossBuy.BL.Platform.Ai.IAiEgressPolicy, CrossBuy.BL.Platform.Ai.AiEgressPolicy>();
builder.Services.AddScoped<IAiInsightsService, AiInsightsService>();

// ---- AI Foundation Increment 1 — read-only projection boundary ----
// The consumer writes AiProjections and NOTHING else: no business writes, no model call, no provider.
// Registration only decides what it is OFFERED; IAiConsumerGrants (default-deny) decides what it may keep.
// Outbox consumers. BusinessEventConsumers.Registered must stay in step with what is registered
// here, in BOTH directions: a name with no implementation accumulates dispatch rows nothing drains
// (BusinessEventDispatchWorker logs an error for each), and an implementation with no name is never
// dispatched to at all. All three names in Registered now have one.
builder.Services.AddScoped<CrossBuy.BL.Platform.IBusinessEventConsumer, CrossBuy.BL.Platform.TimelineProjectionConsumer>();
builder.Services.AddScoped<CrossBuy.BL.Platform.IBusinessEventConsumer, CrossBuy.BL.Platform.NotificationProjectionConsumer>();   // slice 2
builder.Services.AddScoped<CrossBuy.BL.Platform.IBusinessEventNotificationMapper, CrossBuy.BL.Platform.BusinessEventNotificationMapper>();
builder.Services.AddScoped<CrossBuy.BL.Platform.IBusinessEventConsumer, CrossBuy.BL.Platform.Ai.AiProjectionConsumer>();
builder.Services.AddSingleton<CrossBuy.BL.Platform.Ai.IAiConsumerGrants, CrossBuy.BL.Platform.Ai.AiConsumerGrants>();
builder.Services.AddScoped<CrossBuy.BL.Platform.Ai.IAiProjectionStore, CrossBuy.BL.Platform.Ai.AiProjectionStore>();
builder.Services.AddScoped<CrossBuy.BL.Platform.Ai.IAiProjectionBuilder, CrossBuy.BL.Platform.Ai.TaskLifecycleProjectionBuilder>();
builder.Services.AddScoped<CrossBuy.BL.Platform.Ai.IAiProjectionBuilder, CrossBuy.BL.Platform.Ai.CalendarSchedulingProjectionBuilder>();

// ---- AI Foundation Increment 2 — secure READ side + revocation ----
// The reader re-authorizes every row against the AUTHENTICATED USER's BusinessContext on every call; it
// never runs as a system context and exposes no IQueryable. Revocation is tenant-scoped and idempotent.
builder.Services.AddSingleton<CrossBuy.BL.Platform.Ai.IAiProjectionShapeRegistry, CrossBuy.BL.Platform.Ai.AiProjectionShapeRegistry>();
builder.Services.AddScoped<CrossBuy.BL.Platform.Ai.IAiProjectionReader, CrossBuy.BL.Platform.Ai.AiProjectionReader>();
builder.Services.AddScoped<CrossBuy.BL.Platform.Ai.IAiProjectionRevocationService, CrossBuy.BL.Platform.Ai.AiProjectionRevocationService>();

// ---- AI Foundation Increment 3 — egress governance + retention ----
// IAiEgressPolicy is the ONE boundary that approves outbound AI data; IAiService cannot be called
// without an AiEgressApproval that only this policy can mint. The retention registry decides how long
// AI-derived data may exist, and a shape with no declared policy is never persisted.
// IAiEgressPolicy is already registered above, in the AI GOVERNANCE RUNTIME block.
builder.Services.AddSingleton<CrossBuy.BL.Platform.Ai.IAiRetentionPolicyRegistry, CrossBuy.BL.Platform.Ai.AiRetentionPolicyRegistry>();

// ---- AI Foundation Increment 4.3 — RUNTIME PROVIDER APPROVAL (BLOCKER-1) ----
// The governance record that decides whether an EXTERNAL processor is approved. Registered as a
// SINGLETON because it holds no per-request state and reads nothing: no configuration, no HttpContext,
// no environment. Before this existed, `AiService:DestinationClass` in a settings file was the entire
// approval — a configuration line could confer authority the owner had never granted.
// IAiProviderAuthority is already registered above, in the AI GOVERNANCE RUNTIME block.

// ---- AI Foundation Increment 4.5 — cost, volume and failure controls ----
//
// SINGLETONS because they hold counters that must survive across requests — a per-request rate limiter
// counts to one and permits everything. They read IConfiguration for their NUMBERS only; none of them
// can approve a provider, and none is on the local-loopback path.
builder.Services.AddSingleton<CrossBuy.BL.Platform.Ai.IAiRateLimiter, CrossBuy.BL.Platform.Ai.AiRateLimiter>();
builder.Services.AddSingleton<CrossBuy.BL.Platform.Ai.IAiPricingProvider, CrossBuy.BL.Platform.Ai.AiConfiguredPricingProvider>();
builder.Services.AddSingleton<CrossBuy.BL.Platform.Ai.IAiUsageGuard, CrossBuy.BL.Platform.Ai.AiUsageGuard>();
builder.Services.AddSingleton<CrossBuy.BL.Platform.Ai.IAiProviderSwitchboard, CrossBuy.BL.Platform.Ai.AiProviderSwitchboard>();
builder.Services.AddSingleton<CrossBuy.BL.Platform.Ai.IAiCircuitBreaker, CrossBuy.BL.Platform.Ai.AiCircuitBreaker>();

// ---- Increment 4.6 — the audit is now DURABLE as well as logged ----
//
// SCOPED, not singleton: the store takes CrossDbContext, which is scoped. AiEgressAuditSink writes the
// structured log line FIRST and the database row SECOND — the copy that cannot fail is written before
// the one that can — and a persistence failure never propagates to the caller. See the Phase 5 note on
// AiEgressAuditSink for why a failed audit does not fail (or retry) a paid provider call.
builder.Services.AddScoped<CrossBuy.BL.Platform.Ai.IAiEgressAuditStore, CrossBuy.BL.Platform.Ai.SqlAiEgressAuditStore>();
builder.Services.AddScoped<CrossBuy.BL.Platform.Ai.IAiEgressAuditSink, CrossBuy.BL.Platform.Ai.AiEgressAuditSink>();

// The OpenAI adapter. Registering it does NOT approve OpenAI: SendAsync requires an AiEgressApproval,
// which only AiEgressPolicy can mint and which it will not mint for an unapproved external processor.
// The adapter is resolvable, testable and — today — unreachable.
builder.Services.AddHttpClient(CrossBuy.BL.Platform.Ai.OpenAiProviderAdapter.HttpClientName, (sp, c) =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var opt = CrossBuy.BL.Platform.Ai.OpenAiOptions.FromConfiguration(cfg);

    // No Authorization header here. The credential is attached per request and never stored on a shared
    // handler, so it cannot outlive the call or be observed by another caller.
    c.Timeout = TimeSpan.FromSeconds(opt.TimeoutSeconds + 5);   // backstop; the adapter's own token fires first
});
builder.Services.AddScoped<CrossBuy.BL.Platform.Ai.IAiExternalProvider, CrossBuy.BL.Platform.Ai.OpenAiProviderAdapter>();


// Swagger / OpenAPI — only documents the mobile/web REST API (the /api/* controllers)
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "CrossBuy API",
        Version = "v1",
        Description = "REST API for the CrossBuy mobile app and React web (JWT-secured)."
    });

    // Only show the REST endpoints (skip the MVC view controllers to avoid route conflicts)
    c.DocInclusionPredicate((_, api) => api.RelativePath?.StartsWith("api/") == true);
    c.ResolveConflictingActions(apis => apis.First());

    // JWT bearer support in the Swagger UI ("Authorize" button)
    var scheme = new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description = "Paste the token from /api/auth/login (without the word 'Bearer').",
        Reference = new Microsoft.OpenApi.Models.OpenApiReference
        {
            Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
            Id = "Bearer"
        }
    };
    c.AddSecurityDefinition("Bearer", scheme);
    c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        { scheme, Array.Empty<string>() }
    });
});

builder.Services.AddAutoMapper(typeof(Program));

var app = builder.Build();

// [DIAG] confirm DataProtection key ring is shared across run modes (CLI vs IIS Express vs VS)
{
    var keyCount = Directory.Exists(keysDir) ? Directory.GetFiles(keysDir, "key-*.xml").Length : 0;
    app.Logger.LogWarning("[AUTH-DIAG] DataProtection keysDir={Dir} keyFiles={Count} AppName=CrossBuy ContentRoot={Root} Urls={Urls}",
        keysDir, keyCount, app.Environment.ContentRootPath, string.Join(',', app.Urls.Count == 0 ? new[] { "(from launchSettings/ASPNETCORE_URLS)" } : app.Urls.ToArray()));
}

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "CrossBuy API v1");
    c.DocumentTitle = "CrossBuy API";
});

app.UseAuthentication();

var localizationOptions = app.Services.GetRequiredService<IOptions<RequestLocalizationOptions>>().Value;
app.UseRequestLocalization(localizationOptions);

// serve modern image formats (avif/webp) — not in the default static-files MIME map,
// so without this they fall through to auth and return 302 instead of the image
var staticContentTypes = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
staticContentTypes.Mappings[".avif"] = "image/avif";
staticContentTypes.Mappings[".webp"] = "image/webp";
staticContentTypes.Mappings[".webmanifest"] = "application/manifest+json";   // POS-9a: PWA manifest
// PrivateFileGate must sit immediately BEFORE UseStaticFiles: StaticFileMiddleware short-circuits and
// writes the response itself, so a gate placed after it never runs. Without this line every file the
// FileManager uploads under wwwroot/uploads/library is served anonymously to anyone with the URL.
CrossBuy.BL.Platform.PrivateFileGateExtensions.UsePrivateFileGate(app);
app.UseStaticFiles(new Microsoft.AspNetCore.Builder.StaticFileOptions
{
    ContentTypeProvider = staticContentTypes,
    // POS-9a: allow the root-served cashier service worker to claim the /pos/ scope
    OnPrepareResponse = ctx =>
    {
        if (ctx.File.Name.Equals("pos-sw.js", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers["Service-Worker-Allowed"] = "/pos/";
            ctx.Context.Response.Headers["Cache-Control"] = "no-cache";
        }
    }
});
app.UseRouting();
app.UseCors("MobileCors");
// "Comm" is an ACTIVE feature - MainMenu registers an Email entry (Controller = "Comm", Action =
// "Index") and _LayoutInventory carries an Email button - but every one of those links is built by
// Url.Action, which emits "/Comm/Index". The bare "/Comm" was reached only by a hand-typed,
// bookmarked or shared URL, and by the UI conformance matrix, which is what reported it:
// authenticated "/Comm" resolved to CommController.Login, an action that does not exist, and
// returned an EMPTY 404 while "/Comm/Index" returned the real inbox. Unauthenticated it 302'd to
// sign-in, so the defect was invisible until a signed-in request asked for it. This maps the bare
// URL to the EXISTING Index action - no second screen, no redirect hop, no duplicated view.
app.MapControllerRoute(name: "comm-home", pattern: "Comm",
    defaults: new { controller = "Comm", action = "Index" });

app.MapControllerRoute(name: "calendar-home", pattern: "Calendar",
    defaults: new { controller = "Calendar", action = "Index" });

app.MapControllerRoute(name: "reports-home", pattern: "Reports",
    defaults: new { controller = "Reports", action = "Index" });

app.MapControllerRoute(name: "workspace-home", pattern: "Workspace",
    defaults: new { controller = "Workspace", action = "Index" });

// TAB-3 Internal Chat closure (F7). /Chat returned 404 while /Chat/Index worked: the default route
// supplies a controller but no action for a single-segment path, so the bare module URL never matched.
// Comm, Calendar, Reports and Workspace above already each carry this one line; Chat was simply missing
// from the list, which is why the navigation entry existed and the URL did not resolve. Same pattern,
// no new screen and no redirect chain.
app.MapControllerRoute(name: "chat-home", pattern: "Chat",
    defaults: new { controller = "Chat", action = "Index" });

app.MapControllerRoute(name: "business-event-monitor-home", pattern: "BusinessEventMonitor",
    defaults: new { controller = "BusinessEventMonitor", action = "Index" });

app.MapControllerRoute(name: "tasks-home", pattern: "Tasks",
    defaults: new { controller = "Tasks", action = "Index" });

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Account}/{action=Login}/{id?}");


app.Use(async (context, next) =>
{
    var culture = Thread.CurrentThread.CurrentCulture.TwoLetterISOLanguageName;

    context.Items["Culture"] = culture;

    await next.Invoke();
});

app.UseSession();

app.UseRouting();
app.MapControllers();
app.MapHub<NotificationsHub>("/hubs/notifications");
app.MapHub<CrossBuy.Hubs.PosHub>("/hubs/pos");
app.MapHub<CrossBuy.Hubs.ChatHub>("/hubs/chat");
app.UseMiddleware<SessionValidationMiddleware>();

app.UseAuthentication();
app.UseAuthorization();

app.UseHttpsRedirection();

app.Run();

