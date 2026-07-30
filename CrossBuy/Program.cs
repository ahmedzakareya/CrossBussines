using CrossBuy.BL;
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
builder.Services.AddScoped<IAccountingAccessService, AccountingAccessService>();
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
builder.Services.AddScoped<IPosAccessService, PosAccessService>();
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
builder.Services.AddScoped<IStockService, StockService>();
builder.Services.AddScoped<IManufService, ManufService>();
builder.Services.AddScoped<IProcurementService, ProcurementService>();
builder.Services.AddScoped<ISellingService, SellingService>();
builder.Services.AddScoped<IPricingService, PricingService>();
builder.Services.AddScoped<IThreeWayMatchService, ThreeWayMatchService>();
builder.Services.AddScoped<ICrmService, CrmService>();
builder.Services.AddScoped<ICrmCustomerLink, CrmCustomerLink>();
builder.Services.AddScoped<ICrmAccessService, CrmAccessService>();
builder.Services.AddScoped<IInventoryAccessService, InventoryAccessService>();
builder.Services.AddScoped<IInventoryApprovalService, InventoryApprovalService>();
builder.Services.AddScoped<IOpeningBalanceService, OpeningBalanceService>();
builder.Services.AddScoped<IIntegrityCheckService, IntegrityCheckService>();
builder.Services.AddHostedService<IntegrityCheckHostedService>();
builder.Services.AddHostedService<CrmReminderHostedService>();
builder.Services.AddHostedService<TaskGeneratorHostedService>();   // TM-7
builder.Services.AddHostedService<TaskScheduleMatchHostedService>();   // TM-9-ب: scheduled-task matcher
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
    c.DefaultRequestHeaders.Add("X-AI-Secret", cfg["AiService:Secret"] ?? "");
    c.Timeout = TimeSpan.FromSeconds(120);
});
builder.Services.AddScoped<IAiInsightsService, AiInsightsService>();

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

