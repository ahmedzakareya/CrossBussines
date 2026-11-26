using System.Security.Claims;
using CrossBuy.Controllers;
using CrossBuy.ViewModel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Xunit;

namespace CrossBuy.Tests;

public sealed class BranchAdministrationAuthorityTests
{
    private sealed class Localizer : IStringLocalizer<CrossBuy.SharedResources>
    {
        public LocalizedString this[string name]=>new(name,name);
        public LocalizedString this[string name,params object[] args]=>new(name,string.Format(name,args));
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures)=>Array.Empty<LocalizedString>();
    }
    private static ServiceController Controller(string role)
    {
        var controller=new ServiceController(null!,new Localizer());
        controller.ControllerContext=new ControllerContext {HttpContext=new DefaultHttpContext {User=new ClaimsPrincipal(new ClaimsIdentity(new[]{new Claim(ClaimTypes.Role,role)},"test"))}};
        return controller;
    }
    [Theory]
    [InlineData("Admin")]
    [InlineData("Administrator")]
    [InlineData("SalesManager")]
    [InlineData("")]
    public async Task A_company_administrator_cannot_open_or_write_platform_branches(string role)
    {
        var controller=Controller(role);
        Assert.IsType<RedirectToActionResult>(await controller.BranchDetails((int?)null));
        Assert.IsType<RedirectToActionResult>(await controller.BranchDetails(new BranchiesDto(),"[]"));
    }
    [Theory]
    [InlineData("SuperAdmin")]
    [InlineData("PlatformOps")]
    public async Task A_platform_operator_reaches_validation_after_the_authority_gate(string role)
    {
        var controller=Controller(role);controller.ModelState.AddModelError("name","required");
        Assert.IsType<JsonResult>(await controller.BranchDetails(new BranchiesDto(),"[]"));
    }
}
