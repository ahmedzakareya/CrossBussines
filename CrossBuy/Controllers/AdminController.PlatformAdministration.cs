using CrossBuy.BL.Platform;
using CrossBuy.Models;
using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CrossBuy.Controllers;

public partial class AdminController
{
    private static List<CrossBuy.Models.Menu.MenuCategory> PlatformAdministrationNavigation()=>new() {
        new() {LabelAr="إدارة المنصة",LabelEn="Platform administration",Items=new() {
            new() {LabelAr="الصلاحيات",LabelEn="Security Console",Controller="Admin",Action="SecurityConsole"},
            new() {LabelAr="البيانات الأساسية",LabelEn="Master data",Controller="Admin",Action="MasterDataConsole"},
            new() {LabelAr="الدعم",LabelEn="Support workspace",Controller="Crm",Action="SupportWorkspace"}
        }} };
    [SessionValidation][HttpGet("/SecurityConsole")]
    public async Task<IActionResult> SecurityConsole(CancellationToken ct=default)
    {
        var services=HttpContext.RequestServices;
        var context=await services.GetRequiredService<IBusinessContextAccessor>().TryGetCurrentAsync(ct);
        var identity=services.GetRequiredService<IPlatformAdminIdentity>();
        if(!await PlatformAdministrationReader.CanReadAsync(context,identity,ct)) return Forbid();
        ViewBag.SidebarMenu=PlatformAdministrationNavigation();
        var environment=services.GetRequiredService<IWebHostEnvironment>();
        var model=await PlatformAdministrationReader.ReadAsync(context,services.GetRequiredService<CrossDbContext>(),identity,
            services.GetRequiredService<IPlatformGrantWriter>(),services.GetRequiredService<IBootstrapAccessPolicyReader>(),
            services.GetRequiredService<IPlatformPermissionVocabularyRegistry>(),environment.ContentRootPath,ct);
        return model is null ? Forbid() : View("~/Views/PlatformAdministration/SecurityConsole.cshtml",model);
    }
    [SessionValidation][HttpGet("/MasterData")]
    public async Task<IActionResult> MasterDataConsole(CancellationToken ct=default)
    {
        var services=HttpContext.RequestServices;
        var context=await services.GetRequiredService<IBusinessContextAccessor>().TryGetCurrentAsync(ct);
        if(!await PlatformAdministrationReader.CanReadAsync(context,services.GetRequiredService<IPlatformAdminIdentity>(),ct)) return Forbid();
        ViewBag.SidebarMenu=PlatformAdministrationNavigation();
        var codes=new[]{EntityRegistry.Employee,EntityRegistry.Customer,EntityRegistry.Item,EntityRegistry.Supplier};
        var registry=services.GetRequiredService<IEntityRegistry>();
        return View("~/Views/PlatformAdministration/MasterData.cshtml",codes.Select(registry.GetDefinition).ToArray());
    }
}
