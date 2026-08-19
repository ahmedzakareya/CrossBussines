using System.Security.Claims;
using CrossBuy.BL;
using CrossBuy.Models;
using CrossBuy.Models.Context;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
    // Communication Hub P5 — document comments / timeline (any EntityType + EntityId).
    [SessionValidation]
    public class CommentsController : Controller
    {
        private readonly IDocCommentService _comments;
        private readonly IEmployeeService _employees;
        private readonly CrossDbContext _db;
        public CommentsController(IDocCommentService comments, IEmployeeService employees, CrossDbContext db)
        { _comments = comments; _employees = employees; _db = db; }

        private async Task<(int empId, int companyId)?> MeAsync()
        {
            var uid = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(uid)) return null;
            var emp = await _employees.GetEmployeeByUserIdAsync(uid);
            if (emp == null) return null;
            var companyId = await _db.Employee.AsNoTracking().Where(e => e.ID == emp.ID).Select(e => e.EmpCompanyID).FirstOrDefaultAsync();
            return (emp.ID, companyId);
        }

        [HttpGet]
        public async Task<IActionResult> List(string entityType, int entityId)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            if (string.IsNullOrWhiteSpace(entityType)) return BadRequest();
            return Json(await _comments.ListAsync(me.Value.companyId, me.Value.empId, entityType, entityId));
        }

        [HttpPost]
        public async Task<IActionResult> Add(string entityType, int entityId, string body, string? mentionedIds)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            if (string.IsNullOrWhiteSpace(entityType) || string.IsNullOrWhiteSpace(body)) return BadRequest();
            var ids = (mentionedIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var n) ? n : 0).Where(n => n > 0).ToList();
            try
            {
                var dto = await _comments.AddAsync(me.Value.companyId, me.Value.empId, entityType, entityId, body, ids);
                return Json(dto);
            }
            catch (DocCommentEntityTypeException)
            {
                // Stage 0: an unregistered comment target is a client/wiring error, not a server fault.
                // The message is intentionally not echoed — it names internal registry state.
                return BadRequest();
            }
        }

        [HttpPost]
        public async Task<IActionResult> Delete(int id)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            return Json(new { ok = await _comments.DeleteAsync(me.Value.empId, id) });
        }
    }
}
