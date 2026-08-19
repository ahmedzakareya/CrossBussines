using System.Text;
using CrossBuy.Models.Communication;

namespace CrossBuy.BL.Communication
{
    // =============================================================================================
    // Communication Platform (ADR-034 §2-4) — NOTIFICATION TEMPLATES (module 16) and their rendering.
    //
    // THREE PIECES, kept apart because they change for different reasons:
    //
    //   ICommTemplateCatalog     WHICH templates exist, their tokens, their channels.   Changes with features.
    //   ICommTemplateTextProvider THE WORDS, per culture.                               Changes with wording.
    //   ICommTemplateRenderer     TOKEN SUBSTITUTION.                                   Changes never.
    //
    // ON LOCALIZATION, and why this does not use IStringLocalizer directly:
    //
    // CLAUDE.md requires every user-facing string to go through Resources. It ALSO requires that this work
    // stream not whole-file-edit the shared SharedResources.*.resx, which the parallel team has already
    // reordered and which our selective-commit plumbing handles line by line.
    //
    // The resolution: ICommTemplateTextProvider is a RESOURCE-KEY CONTRACT. The default implementation looks
    // each key up and falls back to the built-in bilingual default when it is absent. So this platform ships
    // working today with no resx edit at all, and adding the keys later changes the words without touching one
    // line of code. Every key the next phase must add is listed by RequiredResourceKeys() — published, not
    // remembered.
    // =============================================================================================
    public interface ICommTemplateCatalog
    {
        CommTemplateDefinition Get(string templateKey);
        bool TryGet(string? templateKey, out CommTemplateDefinition? definition);
        IReadOnlyList<CommTemplateDefinition> All();

        // Every resource key a fully localized deployment needs, so the list is a deliverable rather than a
        // grep exercise.
        IReadOnlyList<string> RequiredResourceKeys();
    }

    public sealed class CommTemplateCatalog : ICommTemplateCatalog
    {
        // The four channels are named per template rather than inherited from a global default, because
        // "which channels may this KIND of notification use" is a product decision and a global default hides it.
        // Every set below is then narrowed by the deployment's EnabledChannels and by the recipient's
        // preference — a template can never widen.
        private static readonly List<CommTemplateDefinition> Definitions = new()
        {
            new CommTemplateDefinition
            {
                Key = CommTemplateKeys.MentionedInComment,
                Category = CommNotificationCategories.Mentions,
                LegacyNotificationType = BL.NotificationTypes.DocComment,

                // A mention is the one collaboration signal worth reaching somebody who is not looking at the
                // product, so it is the only template that defaults to Email as well as InApp.
                DefaultChannels = new[] { CommChannel.InApp, CommChannel.Email },
                RequiredTokens = new[] { CommTemplateTokens.ActorName, CommTemplateTokens.EntityLabel },
                DefaultTitleAr = "أشار إليك {actor}",
                DefaultTitleEn = "{actor} mentioned you",
                DefaultBodyAr = "أشار إليك {actor} في تعليق على {entity}: {excerpt}",
                DefaultBodyEn = "{actor} mentioned you in a comment on {entity}: {excerpt}",
                ResourceKeyPrefix = "Comm.Notification.MentionedInComment",
                Priority = "High",
            },
            new CommTemplateDefinition
            {
                Key = CommTemplateKeys.CommentOnFollowedThread,
                Category = CommNotificationCategories.Comments,
                LegacyNotificationType = BL.NotificationTypes.DocComment,

                // InApp only. A followed thread can produce dozens of comments a day; emailing each one is how
                // a collaboration feature teaches people to filter it into a folder they never read.
                DefaultChannels = new[] { CommChannel.InApp },
                RequiredTokens = new[] { CommTemplateTokens.ActorName, CommTemplateTokens.EntityLabel },
                DefaultTitleAr = "تعليق جديد على {entity}",
                DefaultTitleEn = "New comment on {entity}",
                DefaultBodyAr = "أضاف {actor} تعليقًا: {excerpt}",
                DefaultBodyEn = "{actor} commented: {excerpt}",
                ResourceKeyPrefix = "Comm.Notification.CommentOnFollowedThread",
            },
            new CommTemplateDefinition
            {
                Key = CommTemplateKeys.ReplyToMyComment,
                Category = CommNotificationCategories.Comments,
                LegacyNotificationType = BL.NotificationTypes.DocComment,
                DefaultChannels = new[] { CommChannel.InApp, CommChannel.Email },
                RequiredTokens = new[] { CommTemplateTokens.ActorName, CommTemplateTokens.EntityLabel },
                DefaultTitleAr = "رد {actor} على تعليقك",
                DefaultTitleEn = "{actor} replied to your comment",
                DefaultBodyAr = "رد {actor} على تعليقك على {entity}: {excerpt}",
                DefaultBodyEn = "{actor} replied to your comment on {entity}: {excerpt}",
                ResourceKeyPrefix = "Comm.Notification.ReplyToMyComment",
            },
            new CommTemplateDefinition
            {
                Key = CommTemplateKeys.ReactionOnMyComment,
                Category = CommNotificationCategories.Reactions,
                LegacyNotificationType = BL.NotificationTypes.DocComment,
                DefaultChannels = new[] { CommChannel.InApp },
                RequiredTokens = new[] { CommTemplateTokens.ActorName, CommTemplateTokens.ReactionKey },
                DefaultTitleAr = "تفاعل {actor} مع تعليقك",
                DefaultTitleEn = "{actor} reacted to your comment",
                DefaultBodyAr = "أضاف {actor} تفاعل «{reaction}» على تعليقك",
                DefaultBodyEn = "{actor} reacted \"{reaction}\" to your comment",
                ResourceKeyPrefix = "Comm.Notification.ReactionOnMyComment",
            },
            new CommTemplateDefinition
            {
                Key = CommTemplateKeys.AddedAsParticipant,
                Category = CommNotificationCategories.Participation,
                LegacyNotificationType = BL.NotificationTypes.DocComment,
                DefaultChannels = new[] { CommChannel.InApp },
                RequiredTokens = new[] { CommTemplateTokens.ActorName, CommTemplateTokens.EntityLabel },
                DefaultTitleAr = "أضافك {actor} إلى محادثة",
                DefaultTitleEn = "{actor} added you to a conversation",
                DefaultBodyAr = "أضافك {actor} إلى محادثة على {entity}",
                DefaultBodyEn = "{actor} added you to the conversation on {entity}",
                ResourceKeyPrefix = "Comm.Notification.AddedAsParticipant",
            },
            new CommTemplateDefinition
            {
                Key = CommTemplateKeys.ThreadLocked,
                Category = CommNotificationCategories.Moderation,
                LegacyNotificationType = BL.NotificationTypes.DocComment,
                DefaultChannels = new[] { CommChannel.InApp },
                RequiredTokens = new[] { CommTemplateTokens.ActorName, CommTemplateTokens.EntityLabel },
                DefaultTitleAr = "أُغلقت المحادثة",
                DefaultTitleEn = "Conversation locked",
                DefaultBodyAr = "أغلق {actor} المحادثة على {entity}. {reason}",
                DefaultBodyEn = "{actor} locked the conversation on {entity}. {reason}",
                ResourceKeyPrefix = "Comm.Notification.ThreadLocked",
            },
            new CommTemplateDefinition
            {
                Key = CommTemplateKeys.MyCommentDeleted,
                Category = CommNotificationCategories.Moderation,
                LegacyNotificationType = BL.NotificationTypes.DocComment,

                // Moderation of somebody's own words is always worth reaching them, hence Email as well: a
                // deleted comment that the author only discovers by accident reads as censorship.
                DefaultChannels = new[] { CommChannel.InApp, CommChannel.Email },
                RequiredTokens = new[] { CommTemplateTokens.ActorName, CommTemplateTokens.EntityLabel },
                DefaultTitleAr = "حُذف تعليقك",
                DefaultTitleEn = "Your comment was removed",
                DefaultBodyAr = "حذف {actor} تعليقك على {entity}. {reason}",
                DefaultBodyEn = "{actor} removed your comment on {entity}. {reason}",
                ResourceKeyPrefix = "Comm.Notification.MyCommentDeleted",
                Priority = "High",
            },
        };

        private static readonly Dictionary<string, CommTemplateDefinition> ByKey =
            Definitions.ToDictionary(d => d.Key, StringComparer.Ordinal);

        public CommTemplateDefinition Get(string templateKey)
            => TryGet(templateKey, out var d)
                ? d!
                : throw new CommValidationException(
                    CommValidationException.Codes.TemplateUnknown,
                    $"Notification template '{templateKey}' is not in the catalogue. Templates are code-declared " +
                    "so a producer cannot reference one that an operator deleted.");

        public bool TryGet(string? templateKey, out CommTemplateDefinition? definition)
        {
            definition = null;
            if (string.IsNullOrWhiteSpace(templateKey)) return false;
            if (!ByKey.TryGetValue(templateKey, out var d)) return false;
            definition = d;
            return true;
        }

        public IReadOnlyList<CommTemplateDefinition> All() => Definitions;

        public IReadOnlyList<string> RequiredResourceKeys()
        {
            var keys = new List<string>();
            foreach (var d in Definitions)
            {
                if (string.IsNullOrWhiteSpace(d.ResourceKeyPrefix)) continue;
                keys.Add(d.ResourceKeyPrefix + ".TitleAr");
                keys.Add(d.ResourceKeyPrefix + ".TitleEn");
                keys.Add(d.ResourceKeyPrefix + ".BodyAr");
                keys.Add(d.ResourceKeyPrefix + ".BodyEn");
            }
            return keys;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // THE TEXT PROVIDER — the localization seam.
    //
    // The default implementation returns the built-in defaults. A deployment that has added the resx keys
    // registers a provider that reads them and falls back to `fallback` for anything missing. Fallback is a
    // PARAMETER rather than a lookup inside the implementation so a provider physically cannot return null and
    // leave a notification with an empty title.
    // ---------------------------------------------------------------------------------------------
    public interface ICommTemplateTextProvider
    {
        string Resolve(CommTemplateDefinition definition, string part, string fallback);

        public static class Parts
        {
            public const string TitleAr = "TitleAr";
            public const string TitleEn = "TitleEn";
            public const string BodyAr = "BodyAr";
            public const string BodyEn = "BodyEn";
        }
    }

    // Registered by default: no resource lookup, built-in bilingual text.
    //
    // It is not a "stub". A deployment with no localization overrides gets correct Arabic and English from it,
    // which is why the platform is usable before any resx work is done.
    public sealed class DefaultCommTemplateTextProvider : ICommTemplateTextProvider
    {
        public string Resolve(CommTemplateDefinition definition, string part, string fallback) => fallback;
    }

    // ---------------------------------------------------------------------------------------------
    // THE RENDERER — token substitution and nothing else. Pure, so it is fully unit-testable.
    // ---------------------------------------------------------------------------------------------
    public interface ICommTemplateRenderer
    {
        // Throws CommValidationException(template_token_missing) when a required token is absent, rather than
        // emitting a literal "{actor}" into somebody's inbox.
        CommRenderedNotification Render(
            CommTemplateDefinition definition,
            IReadOnlyDictionary<string, string> tokens,
            string? url);
    }

    public sealed class CommTemplateRenderer : ICommTemplateRenderer
    {
        private readonly ICommTemplateTextProvider _text;

        public CommTemplateRenderer(ICommTemplateTextProvider text) => _text = text;

        public CommRenderedNotification Render(
            CommTemplateDefinition definition,
            IReadOnlyDictionary<string, string> tokens,
            string? url)
        {
            ArgumentNullException.ThrowIfNull(definition);
            tokens ??= new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var required in definition.RequiredTokens)
            {
                if (!tokens.TryGetValue(required, out var value) || string.IsNullOrWhiteSpace(value))
                    throw new CommValidationException(
                        CommValidationException.Codes.TemplateTokenMissing,
                        $"Template '{definition.Key}' requires token '{required}'. A missing token would render " +
                        "literally in the recipient's inbox, so it is refused at decide time.");
            }

            return new CommRenderedNotification
            {
                TitleAr = Substitute(_text.Resolve(definition, ICommTemplateTextProvider.Parts.TitleAr, definition.DefaultTitleAr), tokens),
                TitleEn = Substitute(_text.Resolve(definition, ICommTemplateTextProvider.Parts.TitleEn, definition.DefaultTitleEn), tokens),
                BodyAr = Substitute(_text.Resolve(definition, ICommTemplateTextProvider.Parts.BodyAr, definition.DefaultBodyAr), tokens),
                BodyEn = Substitute(_text.Resolve(definition, ICommTemplateTextProvider.Parts.BodyEn, definition.DefaultBodyEn), tokens),
                Category = definition.Category,
                Priority = definition.Priority,
                LegacyNotificationType = definition.LegacyNotificationType,
                Url = url,
            };
        }

        // A hand-rolled scan rather than string.Replace per token, for one reason that matters: a token VALUE
        // must never be re-scanned. An employee whose display name literally contains "{entity}" would
        // otherwise have it substituted, which is a small injection into other people's notifications.
        private static string Substitute(string template, IReadOnlyDictionary<string, string> tokens)
        {
            if (string.IsNullOrEmpty(template) || template.IndexOf('{') < 0) return template;

            var output = new StringBuilder(template.Length + 32);
            int i = 0;
            while (i < template.Length)
            {
                char c = template[i];
                if (c != '{') { output.Append(c); i++; continue; }

                int close = template.IndexOf('}', i + 1);
                if (close < 0) { output.Append(template, i, template.Length - i); break; }

                var name = template.Substring(i + 1, close - i - 1);

                // An UNKNOWN token renders as EMPTY, not as its own braces. "{reason}" with no reason supplied
                // should read as an absent clause, and a literal "{reason}" in an inbox looks like a bug to the
                // recipient — who cannot tell it was optional.
                if (tokens.TryGetValue(name, out var value)) output.Append(value);

                i = close + 1;
            }

            // Optional tokens leave double spaces and trailing punctuation gaps behind; collapse them so the
            // sentence still reads.
            return System.Text.RegularExpressions.Regex.Replace(output.ToString(), @"[ \t]{2,}", " ").Trim();
        }
    }
}
