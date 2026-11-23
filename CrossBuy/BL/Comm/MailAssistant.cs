using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using CrossBuy.BL.Communication;
using CrossBuy.BL.Platform.Ai;
using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Comm
{
    // =============================================================================================================
    // EMAIL PHASE 4 — THE ASSISTANT IN THE MAILBOX (owner, 2026-11-23: mail like the chat and the calls).
    //
    //   * summary   — of the conversation the reader is looking at: overview, points, decisions, tasks with who does them
    //   * reply     — a DRAFT answer to its last message, written for the reader to edit and send (optionally following
    //                 an instruction such as "agree, but ask for one more week")
    //   * translate — the open message into the other language (ar <-> en)
    //
    // The same internal AI service as the chat assistant (crossbuy_ai /assist, on the graphics card), under the same
    // egress purpose (ChatAssistance: internal destination only, free business text) — nothing leaves the building.
    // WHO: only what the reader holds. The conversation is read through ICommService.ThreadAsync, which returns the
    // messages the reader has a copy of (all of them only for the outbox's manager); a job is read only by whoever
    // started it.
    // =============================================================================================================
    public sealed class MailAssistant
    {
        private const int MaxChars = 6000;     // per message: a long newsletter must not crowd out the conversation

        private readonly ICommService _mail;
        private readonly CrossDbContext _db;
        private readonly IAiService _ai;
        private readonly IAiEgressPolicy _egress;
        private readonly IAiProviderAuthority _providers;
        private readonly IConfiguration _config;
        private readonly ILogger<MailAssistant> _log;

        private static readonly ConcurrentDictionary<string, (int Company, int Employee, string Mode, DateTime At)> Jobs = new();

        public MailAssistant(ICommService mail, CrossDbContext db, IAiService ai, IAiEgressPolicy egress, IAiProviderAuthority providers, IConfiguration config, ILogger<MailAssistant> log)
        { _mail = mail; _db = db; _ai = ai; _egress = egress; _providers = providers; _config = config; _log = log; }

        public sealed record Started(bool Ok, string? Job, string? Code);

        private Task<AiProxyResult?> Post(int companyId, string path, object payload, CancellationToken ct)
            => CommAi.PostAsync(_ai, _egress, _providers, _config, _log, AiEgressPurpose.ChatAssistance, AiDataClassification.FreeTextBusinessContent, companyId, path, payload, ct);

        /// Mail is HTML; the model reads its words. Quoted history below a reply ("----- subject -----", "On ... wrote:")
        /// is cut, because the conversation already carries those messages on their own.
        public static string Text(string? html)
        {
            if (string.IsNullOrWhiteSpace(html)) return "";
            var t = Regex.Replace(html, @"<(script|style)[^>]*>.*?</\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"<hr\s*/?>.*$", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"<(br|/p|/div|/li|/tr)[^>]*>", "\n", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, "<[^>]+>", " ");
            t = System.Net.WebUtility.HtmlDecode(t);
            t = Regex.Replace(t, @"\n\s*(On .{5,120} wrote:|في .{5,120} كتب:)[\s\S]*$", "");
            t = Regex.Replace(t, @"[ \t ]+", " ");
            t = Regex.Replace(t, @"\s*\n\s*", "\n").Trim();
            return t.Length > MaxChars ? t[..MaxChars] : t;
        }

        public async Task<Started> StartAsync(int companyId, int employeeId, int messageId, string mode, string? instruction, bool manager, CancellationToken ct)
        {
            if (mode is not ("summary" or "reply" or "translate")) return new(false, null, "bad_mode");
            var m = await _mail.GetAsync(companyId, employeeId, messageId, manager);
            if (m == null) return new(false, null, "not_found");

            var lang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar" ? "ar" : "en";
            object payload;
            if (mode == "translate")
            {
                var body = Text(m.Body);
                if (body.Length == 0) return new(false, null, "nothing_to_translate");
                int ar = body.Count(ch => ch >= '؀' && ch <= 'ۿ'), latin = body.Count(char.IsAsciiLetter);
                lang = ar >= latin ? "en" : "ar";
                payload = new { mode, language = lang, participants = Array.Empty<string>(), messages = new[] { new { id = m.Id, sender = "", text = body.Length > 4000 ? body[..4000] : body } } };
            }
            else
            {
                var thread = await _mail.ThreadAsync(companyId, employeeId, m.ThreadId ?? m.Id, manager);
                if (!thread.Any(t => t.Id == m.Id)) thread.Add(new MailThreadItem { Id = m.Id, FromName = m.FromName, Body = m.Body, At = m.SentAt ?? m.CreatedAt });
                // a reply answers the message being read: what came after it is not the question
                var upTo = thread.Where(t => mode != "reply" || (t.At ?? DateTime.MinValue) <= (m.SentAt ?? m.CreatedAt ?? DateTime.MaxValue) || t.Id == m.Id)
                    .OrderBy(t => t.At).ThenBy(t => t.Id).ToList();
                var me = await _db.Employee.AsNoTracking().Where(e => e.ID == employeeId).Select(e => new { e.FullName, e.FullNameEn }).FirstOrDefaultAsync(ct);
                var myName = me == null ? "" : EmployeeNames.Of(me.FullName, me.FullNameEn);
                var names = upTo.Select(t => t.FromName ?? "").Where(n => n.Length > 0).Distinct().ToList();
                payload = new
                {
                    mode, language = lang,
                    question = mode == "reply" && !string.IsNullOrWhiteSpace(instruction) ? instruction!.Trim()[..Math.Min(400, instruction.Trim().Length)] : null,
                    participants = new[] { myName }.Concat(names.Where(n => n != myName)).Take(50).ToList(),
                    messages = upTo.Select(t => new { id = t.Id, sender = t.FromName ?? "", text = Text(t.Body) }).Where(x => x.text.Length > 0).ToList(),
                };
            }

            var res = await Post(companyId, "/assist/jobs", payload, ct);
            if (res == null || res.Status != 200) return new(false, null, "not_available");
            var job = JsonDocument.Parse(res.Json).RootElement.GetProperty("job").GetString()!;
            foreach (var old in Jobs.Where(j => j.Value.At < DateTime.UtcNow.AddHours(-2)).Select(j => j.Key).ToList()) Jobs.TryRemove(old, out _);
            Jobs[job] = (companyId, employeeId, mode, DateTime.UtcNow);
            return new(true, job, null);
        }

        /// The job as it stands: working, done with its result (read once), or failed. Null when it is not this person's.
        public async Task<object?> StatusAsync(int companyId, int employeeId, string job, CancellationToken ct)
        {
            if (!Jobs.TryGetValue(job, out var owner) || owner.Company != companyId || owner.Employee != employeeId) return null;
            var res = await Post(companyId, "/assist/jobs/status", new { job }, ct);
            if (res == null) return new { status = "working", mode = owner.Mode };
            if (res.Status == 404) { Jobs.TryRemove(job, out _); return new { status = "failed", mode = owner.Mode, error = "lost" }; }
            if (res.Status != 200) return new { status = "working", mode = owner.Mode };
            var j = JsonDocument.Parse(res.Json).RootElement;
            var status = j.GetProperty("status").GetString();
            if (status != "working") Jobs.TryRemove(job, out _);
            return new
            {
                status, mode = owner.Mode,
                result = j.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Object ? JsonSerializer.Deserialize<JsonElement>(r.GetRawText()) : (JsonElement?)null,
            };
        }
    }
}
