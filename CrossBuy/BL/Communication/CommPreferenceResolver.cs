using CrossBuy.Models.Communication;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Communication;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CrossBuy.BL.Communication
{
    // =============================================================================================
    // Communication Platform (ADR-034 §5) — NOTIFICATION PREFERENCES (module 22).
    //
    // THE CHANNEL PLAN IS AN INTERSECTION, NEVER A UNION. Three gates, all narrowing:
    //
    //     template.DefaultChannels  ∩  options.EnabledChannels  ∩  recipient preference ≠ Off
    //
    // Order does not matter to the result, but the direction does: no gate can ADD a channel. That is what makes
    // "turn Email off for this deployment" an absolute statement rather than a default somebody's stored
    // preference can override.
    //
    // ABSENCE OF A ROW IS NOT "OFF" — it is "use the deployment default". This distinction is the difference
    // between a new channel reaching everybody when an operator enables it, and reaching only the handful of
    // people who once opened the preferences screen. Storing a row per (employee × category × channel) up front
    // would be the alternative, and it would need a backfill for every new employee and every new category.
    //
    // DIGEST IS RESOLVED, NOT DELIVERED. Digest currently behaves as Immediate for the InApp channel — an
    // in-app notification IS the digest, since it waits in the inbox — and as Off for push and email, because no
    // digest batching worker exists. That is stated in DigestNote() and reported per channel rather than
    // silently treated as Immediate, which would email somebody who explicitly asked for a daily summary.
    // =============================================================================================
    public interface ICommPreferenceResolver
    {
        // Which channels to create delivery rows on, for one recipient and one template.
        Task<CommChannelPlan> ResolveAsync(
            int companyId, int employeeId, CommTemplateDefinition template,
            IReadOnlyList<string>? requestedChannels, CancellationToken cancellationToken = default);

        // The recipient's effective preferences, including inherited defaults, for a settings screen.
        Task<IReadOnlyList<CommPreferenceDto>> GetAsync(
            int companyId, int employeeId, CancellationToken cancellationToken = default);

        // Upsert. A caller may only change their OWN preferences unless they are acting on themselves —
        // enforced by the caller passing context.EmployeeId; this method is the storage step.
        Task<CommPreferenceDto> SetAsync(
            BusinessContext context, CommPreferenceUpdateRequest request, CancellationToken cancellationToken = default);
    }

    public sealed class CommPreferenceResolver : ICommPreferenceResolver
    {
        private readonly CommDb _db;
        private readonly CommunicationPlatformOptions _options;

        public CommPreferenceResolver(CrossDbContext db, IOptions<CommunicationPlatformOptions> options)
        {
            _db = new CommDb(db);
            _options = options.Value;
        }

        public async Task<CommChannelPlan> ResolveAsync(
            int companyId, int employeeId, CommTemplateDefinition template,
            IReadOnlyList<string>? requestedChannels, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(template);

            var excluded = new Dictionary<string, string>(StringComparer.Ordinal);

            if (companyId <= 0 || employeeId <= 0)
                return new CommChannelPlan
                {
                    EmployeeId = employeeId,
                    Channels = Array.Empty<string>(),
                    ExcludedChannels = new Dictionary<string, string>(StringComparer.Ordinal)
                    { ["*"] = "no company or employee resolved" },
                };

            // ---- gate 1: what the template may use, optionally narrowed further by the producer.
            var candidates = (requestedChannels is { Count: > 0 }
                    ? template.DefaultChannels.Intersect(requestedChannels, StringComparer.Ordinal)
                    : template.DefaultChannels.AsEnumerable())
                .Distinct(StringComparer.Ordinal)
                .ToList();

            foreach (var dropped in template.DefaultChannels.Except(candidates, StringComparer.Ordinal))
                excluded[dropped] = "not requested by the producer";

            // ---- gate 2: the deployment.
            var allowedByDeployment = new List<string>();
            foreach (var channel in candidates)
            {
                if (!CommChannel.IsValid(channel))
                {
                    excluded[channel] = "not a CommChannel value";
                    continue;
                }
                if (!_options.EnabledChannels.Contains(channel, StringComparer.Ordinal))
                {
                    excluded[channel] = "not in CommunicationPlatform:EnabledChannels";
                    continue;
                }
                allowedByDeployment.Add(channel);
            }

            if (allowedByDeployment.Count == 0)
                return new CommChannelPlan { EmployeeId = employeeId, Channels = Array.Empty<string>(), ExcludedChannels = excluded };

            // ---- gate 3: the recipient. One query for every channel of this category.
            var stored = await _db.Preferences.AsNoTracking()
                .Where(p => p.CompanyID == companyId
                            && p.EmployeeId == employeeId
                            && p.Category == template.Category)
                .Select(p => new { p.Channel, p.Mode })
                .ToListAsync(cancellationToken);

            var byChannel = stored.ToDictionary(p => p.Channel, p => p.Mode, StringComparer.Ordinal);

            var final = new List<string>();
            foreach (var channel in allowedByDeployment)
            {
                var mode = byChannel.TryGetValue(channel, out var stored1) ? stored1 : _options.DefaultPreferenceMode;

                if (mode == CommPreferenceMode.Off)
                {
                    excluded[channel] = CommNotificationResult.SkipReasons.PreferenceOff;
                    continue;
                }

                if (mode == CommPreferenceMode.Digest)
                {
                    var note = DigestNote(channel);
                    if (note != null) { excluded[channel] = note; continue; }
                }

                final.Add(channel);
            }

            return new CommChannelPlan { EmployeeId = employeeId, Channels = final, ExcludedChannels = excluded };
        }

        // Digest, honestly.
        //
        // Returns null when the channel may still be used, or the reason it may not. InApp is null because an
        // in-app notification waits in an inbox — which is what a digest is. Email/Push/WhatsApp are excluded
        // because no batching worker exists yet, and treating "send me a daily summary" as "send me each one
        // immediately" is the opposite of what the recipient asked for.
        private static string? DigestNote(string channel) => channel switch
        {
            CommChannel.InApp => null,
            _ => "preference is Digest and no digest batching worker exists for this channel yet (ADR-034 §5)",
        };

        public async Task<IReadOnlyList<CommPreferenceDto>> GetAsync(
            int companyId, int employeeId, CancellationToken cancellationToken = default)
        {
            if (companyId <= 0 || employeeId <= 0) return Array.Empty<CommPreferenceDto>();

            var stored = await _db.Preferences.AsNoTracking()
                .Where(p => p.CompanyID == companyId && p.EmployeeId == employeeId)
                .ToListAsync(cancellationToken);

            var lookup = stored.ToDictionary(p => p.Category + "|" + p.Channel, p => p.Mode, StringComparer.Ordinal);

            // The full matrix, with IsDefault marking what is inherited. A settings screen must show "inherited"
            // rather than presenting a deployment default as the recipient's own choice.
            var results = new List<CommPreferenceDto>();
            foreach (var category in CommNotificationCategories.Values)
            foreach (var channel in _options.EnabledChannels.Where(CommChannel.IsValid).Distinct(StringComparer.Ordinal))
            {
                bool isStored = lookup.TryGetValue(category + "|" + channel, out var mode);
                results.Add(new CommPreferenceDto
                {
                    EmployeeId = employeeId,
                    Category = category,
                    Channel = channel,
                    Mode = isStored ? mode! : _options.DefaultPreferenceMode,
                    IsDefault = !isStored,
                });
            }
            return results;
        }

        public async Task<CommPreferenceDto> SetAsync(
            BusinessContext context, CommPreferenceUpdateRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(request);

            if (context.CompanyId <= 0)
                throw new CommValidationException(
                    CommValidationException.Codes.ActorRequired, "Setting a preference requires a company scope.");

            // A preference is PERSONAL. There is no administrative override, deliberately: an administrator who
            // can switch somebody else's notifications off can silence a mention they were meant to see, and
            // there is no product reason to allow it.
            if (context.EmployeeId is not > 0 || request.EmployeeId != context.EmployeeId.Value)
                throw new CommAccessDeniedException(
                    "preference-set", null,
                    "a notification preference may only be changed by its owner; there is no administrative override");

            if (!CommNotificationCategories.IsValid(request.Category))
                throw new CommValidationException(
                    CommValidationException.Codes.PreferenceModeInvalid,
                    $"Category '{request.Category}' is not one of {string.Join(" | ", CommNotificationCategories.Values)}.");
            if (!CommChannel.IsValid(request.Channel))
                throw new CommValidationException(
                    CommValidationException.Codes.ChannelInvalid,
                    $"Channel '{request.Channel}' is not one of {string.Join(" | ", CommChannel.Values)}.");
            if (!CommPreferenceMode.IsValid(request.Mode))
                throw new CommValidationException(
                    CommValidationException.Codes.PreferenceModeInvalid,
                    $"Mode '{request.Mode}' is not one of {string.Join(" | ", CommPreferenceMode.Values)}.");

            var row = await _db.Preferences
                .Where(p => p.CompanyID == context.CompanyId
                            && p.EmployeeId == request.EmployeeId
                            && p.Category == request.Category
                            && p.Channel == request.Channel)
                .FirstOrDefaultAsync(cancellationToken);

            if (row == null)
            {
                row = new CommNotificationPreference
                {
                    CompanyID = context.CompanyId,
                    EmployeeId = request.EmployeeId,
                    Category = request.Category,
                    Channel = request.Channel,
                    Mode = request.Mode,
                    CreatedBy = context.EmployeeId,
                    CreatedAt = DateTime.UtcNow,
                };
                _db.Preferences.Add(row);
            }
            else
            {
                row.Mode = request.Mode;
                row.updatedBy = context.EmployeeId;
                row.UpdatedAt = DateTime.UtcNow;
            }

            await _db.SaveAsync(cancellationToken);

            // No event and no audit row: a preference is personal UI state, not a fact about a business record,
            // and auditing every toggle would fill an append-only log with nothing an investigation can use.
            return new CommPreferenceDto
            {
                EmployeeId = row.EmployeeId,
                Category = row.Category,
                Channel = row.Channel,
                Mode = row.Mode,
                IsDefault = false,
            };
        }
    }
}
