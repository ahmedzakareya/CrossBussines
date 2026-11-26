using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Communication
{
    // =============================================================================================
    // WHO MAY BE OFFERED IN AN @-MENTION PICKER.
    //
    // ONE RULE, NOT ONE PER SCREEN. Tasks shipped this query first; the document conversation panel
    // needs the same list, and so will the next screen that mounts the panel. Four copies of a
    // twenty-line query is four places for the rule to drift - and the way it drifts is silent,
    // because a picker that offers the wrong people still looks like it works.
    //
    // THE CALLER IS LEFT OUT. Mentioning yourself notifies nobody by design (the notification path
    // excludes the actor), so offering your own name would advertise an action that does nothing.
    //
    // MATCHED ON EITHER NAME. Somebody typing Latin letters must still find a colleague whose stored
    // name is Arabic, and the reverse - one staff list is read by people writing in both.
    //
    // COMPANY-SCOPED, AND THAT IS THE SECURITY PROPERTY HERE: the picker is a directory, so an
    // unresolved company must return nothing rather than everyone.
    //
    // THIS IS A SUGGESTION LIST, NOT AN AUTHORIZATION. Being offered here does not make someone a
    // recipient: CommNotificationService authorizes every recipient against the subject afterwards
    // and drops anyone who may not read it. So this stays a plain directory lookup, and the place to
    // add a narrower rule later - thread participants only, say - is here rather than in each view.
    // =============================================================================================
    public static class CommMentionCandidates
    {
        public const int DefaultTake = 8;

        public sealed record Candidate(int Id, string Name, string? Avatar);

        public static async Task<IReadOnlyList<Candidate>> SearchAsync(
            CrossDbContext db,
            int companyId,
            int? excludeEmployeeId,
            string? term,
            bool arabic,
            int take = DefaultTake,
            CancellationToken cancellationToken = default)
        {
            if (db is null) throw new ArgumentNullException(nameof(db));
            if (companyId <= 0) return Array.Empty<Candidate>();
            if (take < 1) take = DefaultTake; else if (take > 25) take = 25;

            var t = (term ?? string.Empty).Trim();
            int me = excludeEmployeeId ?? 0;

            var rows = await db.Employee.AsNoTracking()
                .Where(e => e.EmpCompanyID == companyId
                         && e.FullName != null && e.FullName != ""
                         && e.ID != me)
                .Where(e => t == ""
                         || e.FullName!.Contains(t)
                         || (e.FullNameEn != null && e.FullNameEn.Contains(t)))
                .OrderBy(e => e.FullName)
                .Take(take)
                .Select(e => new { e.ID, e.FullName, e.FullNameEn, e.ProfileImage })
                .ToListAsync(cancellationToken);

            return rows.Select(r => new Candidate(
                r.ID,
                (!arabic && !string.IsNullOrWhiteSpace(r.FullNameEn)) ? r.FullNameEn! : r.FullName!,
                string.IsNullOrWhiteSpace(r.ProfileImage) ? null : r.ProfileImage)).ToList();
        }
    }
}
