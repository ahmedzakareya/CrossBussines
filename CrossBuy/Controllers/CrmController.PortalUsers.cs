using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.BL.Portal;
using CrossBuy.Models.Context.Portal;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CrossBuy.Controllers
{
    // =============================================================================================
    // PORTAL CONTACTS — the staff-side screen for the people who sign in to the Client Portal.
    //
    // IT LIVES IN CRM, NOT IN THE PORTAL. A portal contact is a customer's person, so the authority
    // that says who may look at them is the CRM one the rest of this controller already answers to.
    // Putting it on ClientPortalController would have given the external surface a staff screen,
    // which is the one thing that controller exists to prevent.
    //
    // IT IS A PARTIAL OF CrmController ON PURPOSE. A new controller would have been the obvious
    // move and the wrong one: this is customer data that already has a controller, a company
    // resolver and an access service wired — and the platform's controller count is frozen.
    //
    // WHAT THE CONVERGENCE CHANGED, and both changes are the point of the wave:
    //
    //   * PHOTOGRAPHS NO LONGER TOUCH wwwroot. Writing and reading both go through
    //     IPortalPhotoService, whose store sits under the content root and therefore has no URL.
    //     The screen renders PortalUserPhotoContent, never a file path. See that service for the
    //     measured reason the old static-url arrangement could not be kept.
    //
    //   * PROVISIONING LANDED HERE rather than on a screen of its own. Creating a portal login,
    //     revoking it and setting its capabilities are decisions about exactly these rows, and the
    //     brief is explicit that they must not become a second screen.
    //
    // THE SERVICES ARE RESOLVED PER REQUEST rather than injected. CrmController's constructor is a
    // shared surface this wave has no business widening — the single word this file needs from
    // CrmController.cs is `partial`, and keeping it to that is what makes the convergence reviewable.
    // =============================================================================================
    public partial class CrmController
    {
        /// What the screen shows per row. A projection rather than the entity, so a column added to
        /// PortalUser later cannot appear on a staff screen by accident.
        ///
        /// NOTE WHAT IS NOT HERE: the photograph's stored locator. The screen needs to know whether
        /// there IS a picture, never where it lives — so it carries a bool, and a stored path cannot
        /// reach the browser even by a rendering mistake.
        public sealed class PortalContactRow
        {
            public int Id { get; set; }
            public string ContactName { get; set; } = "";
            public string? Email { get; set; }
            public string? Phone { get; set; }
            public bool IsActive { get; set; }
            public bool HasLogin { get; set; }
            public bool HasPhoto { get; set; }
            public string? Capabilities { get; set; }
            public int CustomerId { get; set; }
            public string CustomerName { get; set; } = "";
        }

        private IPortalPhotoService Photos => HttpContext.RequestServices.GetRequiredService<IPortalPhotoService>();
        private IPortalProvisioningService Provisioning => HttpContext.RequestServices.GetRequiredService<IPortalProvisioningService>();

        // ---- the screen ---------------------------------------------------------------------------

        [HttpGet]
        public async Task<IActionResult> PortalUsers(string? q)
        {
            var (okCo, cid, denyCo) = await ResolveCompanyAsync();
            if (!okCo) return denyCo;
            if (!await _access.CanAsync("read")) return Forbid();

            // Company in the WHERE clause, not checked afterwards: a contact of another company is
            // never materialised, so "belongs to someone else" and "does not exist" look the same.
            var rows = await (from p in _context.Set<PortalUser>().AsNoTracking()
                              join c in _context.Set<Models.Context.Accounting.Customer>().AsNoTracking()
                                   on new { Id = p.CustomerId, Co = p.CompanyID } equals new { Id = c.ID, Co = c.CompanyID } into cj
                              from c in cj.DefaultIfEmpty()
                              where p.CompanyID == cid
                              orderby p.ContactName
                              select new PortalContactRow
                              {
                                  Id = p.Id,
                                  ContactName = p.ContactName,
                                  Email = p.Email,
                                  Phone = p.Phone,
                                  IsActive = p.IsActive,
                                  HasLogin = p.UserId != null,
                                  HasPhoto = p.PhotoPath != null,
                                  Capabilities = p.Capabilities,
                                  CustomerId = p.CustomerId,
                                  CustomerName = c != null ? c.Name : ""
                              }).ToListAsync();

            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim();
                rows = rows.Where(r =>
                    r.ContactName.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || (r.Email ?? "").Contains(term, StringComparison.OrdinalIgnoreCase)
                    || r.CustomerName.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            ViewBag.Query = q;
            ViewBag.AllCapabilities = PortalCapabilities.All;
            return View(rows);
        }

        // ---- the photograph, STAFF audience -------------------------------------------------------

        // The staff twin of ClientPortalController.ContactPhoto. Same service, same store, a
        // different question: this one asks whether the CALLER may see a contact of THEIR company,
        // and IPortalPhotoService answers it with the CRM read right plus the resolved company.
        //
        // A cross-company employee is refused here even though they are perfectly well signed in —
        // which is exactly what the static url could not express, and the reason it is gone.
        [HttpGet]
        public async Task<IActionResult> PortalUserPhotoContent(int id, CancellationToken ct = default)
        {
            var (okCo, cid, denyCo) = await ResolveCompanyAsync();
            if (!okCo) return denyCo;

            var content = await Photos.OpenForStaffAsync(id, cid, ct);

            // One answer for absent, another company's, no photograph, and a locator that is not
            // ours. NotFound rather than Forbid: confirming the id names a real contact is itself a
            // disclosure to somebody who may not see them.
            if (content == null) return NotFound();

            Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, private";
            return File(content.Stream, content.ContentType);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequestSizeLimit(PortalPhotoService.MaxBytes + 512 * 1024)]
        public async Task<IActionResult> PortalUserPhoto(int id, IFormFile? photo, CancellationToken ct = default)
        {
            var (okCo, cid, denyCo) = await ResolveCompanyAsync();
            if (!okCo) return denyCo;
            if (!await _access.CanAsync("manage")) return Forbid();

            var contact = await _context.Set<PortalUser>()
                .FirstOrDefaultAsync(p => p.Id == id && p.CompanyID == cid, ct);
            if (contact == null) return NotFound();

            if (photo == null || photo.Length == 0)
            {
                TempData["CrmErr"] = L["Choose an image first."].Value;
                return RedirectToAction(nameof(PortalUsers));
            }

            if (photo.Length > PortalPhotoService.MaxBytes)
            {
                TempData["CrmErr"] = L["The image is larger than 4 MB."].Value;
                return RedirectToAction(nameof(PortalUsers));
            }

            // The accepted types are the service's, not this controller's, so the screen cannot
            // start accepting something the reader will refuse to serve.
            if (Photos.ExtensionFor(photo.ContentType) == null)
            {
                TempData["CrmErr"] = L["Only JPG, PNG or WEBP images are accepted."].Value;
                return RedirectToAction(nameof(PortalUsers));
            }

            var locator = await Photos.SaveAsync(cid, photo, ct);
            if (locator == null)
            {
                // The same failure the employee photo documents: on a deployed server the app pool
                // identity often cannot write under the content root. Named as a relative folder and
                // a fix, rather than letting the raw exception publish the server's absolute layout.
                TempData["CrmErr"] = L["The photo could not be saved: App_Data/portal-photos is not writable on the server."].Value;
                return RedirectToAction(nameof(PortalUsers));
            }

            var previous = contact.PhotoPath;
            contact.PhotoPath = locator;
            await _context.SaveChangesAsync(ct);

            // Only AFTER the row points at the new file: a delete that ran first would leave the
            // contact pointing at nothing if the save failed.
            Photos.Delete(cid, previous);

            TempData["CrmMsg"] = L["Photo updated."].Value;
            return RedirectToAction(nameof(PortalUsers));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PortalUserPhotoRemove(int id, CancellationToken ct = default)
        {
            var (okCo, cid, denyCo) = await ResolveCompanyAsync();
            if (!okCo) return denyCo;
            if (!await _access.CanAsync("manage")) return Forbid();

            var contact = await _context.Set<PortalUser>()
                .FirstOrDefaultAsync(p => p.Id == id && p.CompanyID == cid, ct);
            if (contact == null) return NotFound();

            var previous = contact.PhotoPath;
            contact.PhotoPath = null;
            await _context.SaveChangesAsync(ct);

            Photos.Delete(cid, previous);

            TempData["CrmMsg"] = L["Photo removed."].Value;
            return RedirectToAction(nameof(PortalUsers));
        }

        // ---- provisioning, on THIS screen ----------------------------------------------------------
        //
        // Every one of these delegates the decision to IPortalProvisioningService, which resolves its
        // own BusinessContext and refuses an external caller by construction. The `manage` check here
        // is the CRM screen's own gate and is NOT the service's authority — two independent refusals,
        // and neither is standing in for the other.

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PortalUserProvision(
            int customerId, string contactName, string? email, string? phone, string? userId,
            string[]? capabilities, CancellationToken ct = default)
        {
            var (okCo, _, denyCo) = await ResolveCompanyAsync();
            if (!okCo) return denyCo;
            if (!await _access.CanAsync("manage")) return Forbid();

            var result = await Provisioning.ProvisionAsync(
                customerId, contactName ?? string.Empty, email, phone, userId, capabilities, ct);

            return ProvisioningOutcome(result, L["Portal contact created."].Value);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PortalUserDeactivate(int id, CancellationToken ct = default)
        {
            var (okCo, _, denyCo) = await ResolveCompanyAsync();
            if (!okCo) return denyCo;
            if (!await _access.CanAsync("manage")) return Forbid();

            return ProvisioningOutcome(await Provisioning.DeactivateAsync(id, ct), L["Portal access revoked."].Value);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PortalUserReactivate(int id, CancellationToken ct = default)
        {
            var (okCo, _, denyCo) = await ResolveCompanyAsync();
            if (!okCo) return denyCo;
            if (!await _access.CanAsync("manage")) return Forbid();

            return ProvisioningOutcome(await Provisioning.ReactivateAsync(id, ct), L["Portal access restored."].Value);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PortalUserCapabilities(
            int id, string[]? capabilities, CancellationToken ct = default)
        {
            var (okCo, _, denyCo) = await ResolveCompanyAsync();
            if (!okCo) return denyCo;
            if (!await _access.CanAsync("manage")) return Forbid();

            var result = await Provisioning.SetCapabilitiesAsync(id, capabilities ?? Array.Empty<string>(), ct);
            return ProvisioningOutcome(result, L["Portal capabilities updated."].Value);
        }

        /// ONE translation of the service's refusal codes, so four actions cannot drift into four
        /// different vocabularies for the same outcome.
        private IActionResult ProvisioningOutcome(PortalProvisioningResult result, string success)
        {
            if (result.Ok) TempData["CrmMsg"] = success;
            else TempData["CrmErr"] = result.Code switch
            {
                PortalProvisioningResult.NoSuchCustomer => L["That customer does not exist in this company."].Value,
                PortalProvisioningResult.AlreadyLinked => L["That login or contact is already linked."].Value,
                PortalProvisioningResult.UnknownCapability => L["One of the capabilities is not one this platform grants."].Value,
                PortalProvisioningResult.NoSuchPortalUser => L["That portal contact does not exist in this company."].Value,
                PortalProvisioningResult.InvalidArgument => L["Some of the details are missing or invalid."].Value,
                _ => L["You are not permitted to do that."].Value,
            };

            return RedirectToAction(nameof(PortalUsers));
        }
    }
}
