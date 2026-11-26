using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers;

public partial class CrmController
{
    [HttpGet("/SupportWorkspace")][CrmPerm("read")]
    public async Task<IActionResult> SupportWorkspace(string? q,string? status,int page=1)
    {
        var (ok,companyId,denial)=await ResolveCompanyAsync();
        if(!ok) return denial;
        var (rows,total)=await _crm.SearchTicketsAsync(companyId,q,status,null,Math.Max(1,page),25);
        ViewBag.Total=total;ViewBag.Page=Math.Max(1,page);ViewBag.Query=q;ViewBag.Status=status;
        return View("~/Views/PlatformAdministration/SupportWorkspace.cshtml",rows);
    }
}
