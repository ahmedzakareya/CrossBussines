using System.Text;
using System.Text.RegularExpressions;
using CrossBuy.Models.Communication;
using Microsoft.Extensions.Options;

namespace CrossBuy.BL.Communication
{
    // =============================================================================================
    // Communication Platform (ADR-030 §9) — THE BODY POLICY: validation, mention extraction, structural
    // analysis. Covers "Markdown / Rich Text / Images / Files / Links / Code Blocks / Tables" and module 10.
    //
    // WHAT THIS CLASS DELIBERATELY DOES NOT DO: render.
    //
    // It never produces HTML. The body is STORED as authored and rendered by the UI layer, which this phase
    // does not ship. That is not a gap — it is the safe division:
    //   * Storing rendered HTML means the sanitizer's rules are frozen into the data. A sanitizer bug
    //     discovered next year is then in every historical row, unfixable without rewriting content.
    //   * Storing the source means a rendering fix is a deploy, not a data migration.
    // So the platform's contract is: authored markdown in, authored markdown out, plus a STRUCTURAL REPORT
    // of what the body contains so callers can make decisions without parsing it themselves.
    //
    // Everything here is a PURE FUNCTION of (body, format, options). No database, no context, no I/O — the
    // same discipline ADR-006 imposes on BusinessEventNotificationMapper, and the reason this is the most
    // heavily unit-tested class in the platform.
    // =============================================================================================
    public interface ICommBodyPolicy
    {
        // Validates and normalises. Throws CommValidationException with a machine code on rejection.
        // Returns the body to STORE (trimmed, line endings normalised) plus its structural analysis.
        CommBodyResult Prepare(string? body, string? format);

        // The mention tokens found in a body, deduped, in first-appearance order. Tokens inside fenced or
        // inline code are NOT mentions — see StripCode.
        IReadOnlyList<CommMentionToken> ExtractMentions(string? body, string? format);

        // A short plain-text lead-in for a notification or a mention list. Markdown syntax removed, newlines
        // collapsed, hard-truncated at a character budget. Never used as a substitute for reading the comment:
        // the excerpt goes to people who were mentioned, who are authorized by construction.
        string Excerpt(string? body, string? format, int maxChars = 160);
    }

    public sealed class CommBodyResult
    {
        public required string Body { get; init; }
        public required string Format { get; init; }
        public required CommBodyAnalysisDto Analysis { get; init; }
        public required IReadOnlyList<CommMentionToken> Mentions { get; init; }
    }

    // One @mention as written. TargetKey carries the raw right-hand side: a numeric id for
    // employee/team/department, a role name for role.
    public sealed record CommMentionToken(string TargetKind, int? TargetId, string? TargetKey, string? Label)
    {
        // Identity for dedup: the same target written twice in one body is one mention.
        public string DedupKey =>
            TargetKind + ":" + (TargetId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? TargetKey ?? "");
    }

    public sealed class CommBodyPolicy : ICommBodyPolicy
    {
        // ---- mention grammar -----------------------------------------------------------------------
        //
        // TWO accepted forms, both anchored on '@' so an author can type either:
        //
        //   @employee:12                     canonical, what a client sends when it knows the id
        //   @[Ahmed Zakarya](employee:12)    labelled, deliberately shaped like a markdown link so that an
        //                                    UNRENDERED body still reads as a name rather than as an id
        //
        // The kind is a closed alternation, not \w+, so a typo ("@empoyee:12") is plain text rather than an
        // unknown mention kind that would have to be rejected at write time and would break the whole comment.
        private const string KindAlternation = "employee|team|department|role";

        private static readonly Regex LabelledMention = new(
            @"@\[(?<label>[^\]\r\n]{1,200})\]\((?<kind>" + KindAlternation + @"):(?<key>[A-Za-z0-9_\-\.]{1,120})\)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex PlainMention = new(
            @"(?<![\w\]])@(?<kind>" + KindAlternation + @"):(?<key>[A-Za-z0-9_\-\.]{1,120})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // ---- markdown structure ---------------------------------------------------------------------
        private static readonly Regex FencedCode = new(
            @"^[ \t]{0,3}(`{3,}|~{3,})[^\r\n]*\r?\n(?<content>[\s\S]*?)(?:^[ \t]{0,3}\1[ \t]*(?:\r?\n|$))",
            RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant);

        private static readonly Regex InlineCode = new(@"`[^`\r\n]+`",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Image first, because an image "![x](y)" also matches the link pattern; the link counter runs on a
        // body with images already removed so one construct is never counted twice.
        private static readonly Regex Image = new(@"!\[(?<alt>[^\]\r\n]*)\]\((?<url>[^)\s]+)[^)]*\)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // (?<!@) excludes the labelled-mention form, which is a mention, not a link.
        private static readonly Regex Link = new(@"(?<!@)\[(?<text>[^\]\r\n]*)\]\((?<url>[^)\s]+)[^)]*\)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex AutoLink = new(@"(?<![(\w])(?<url>https?://[^\s<>""')]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex Heading = new(@"^[ \t]{0,3}#{1,6}[ \t]+\S",
            RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant);

        private static readonly Regex ListItem = new(@"^[ \t]{0,3}(?:[-*+][ \t]+|\d{1,9}[.)][ \t]+)\S",
            RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant);

        private static readonly Regex BlockQuote = new(@"^[ \t]{0,3}>[ \t]?",
            RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant);

        // A GFM table is a header row of pipes followed by a delimiter row of dashes/colons. The DELIMITER
        // row is what makes it a table — counting rows containing '|' would count every sentence with a pipe.
        private static readonly Regex TableDelimiter = new(
            @"^[ \t]{0,3}\|?[ \t]*:?-{1,}:?[ \t]*(\|[ \t]*:?-{1,}:?[ \t]*)+\|?[ \t]*$",
            RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant);

        private static readonly Regex Emphasis = new(@"(\*\*|__|\*|_|~~)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly CommunicationPlatformOptions _options;

        public CommBodyPolicy(IOptions<CommunicationPlatformOptions> options)
            => _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        public CommBodyResult Prepare(string? body, string? format)
        {
            format = string.IsNullOrWhiteSpace(format) ? CommBodyFormat.Markdown : format.Trim();
            if (!CommBodyFormat.IsValid(format))
                throw new CommValidationException(
                    CommValidationException.Codes.BodyFormatInvalid,
                    $"Body format '{format}' is not one of {string.Join(" | ", CommBodyFormat.Values)}. " +
                    "HTML is deliberately not accepted — see CommBodyFormat.");

            // Normalise line endings BEFORE measuring. A CRLF body from a Windows client would otherwise
            // measure larger than the same text from a mobile client and could be rejected while the other
            // is accepted — a size limit that depends on the client is not a limit.
            var normalised = (body ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();

            if (normalised.Length == 0)
                throw new CommValidationException(
                    CommValidationException.Codes.BodyRequired, "A comment body is required.");

            // BYTES, not characters. An Arabic body is roughly two bytes per character, so a character cap
            // would silently halve the real limit for Arabic authors — the same reasoning that put a byte
            // limit on the kernel's event payload.
            int bytes = Encoding.UTF8.GetByteCount(normalised);
            if (bytes > _options.MaxBodyBytes)
                throw new CommValidationException(
                    CommValidationException.Codes.BodyTooLarge,
                    $"Body is {bytes} bytes which exceeds the {_options.MaxBodyBytes}-byte limit. " +
                    "Attach a file instead of pasting a document into a comment.");

            var mentions = ExtractMentionsCore(normalised, format);
            if (mentions.Count > _options.MaxMentionsPerComment)
                throw new CommValidationException(
                    CommValidationException.Codes.TooManyMentions,
                    $"{mentions.Count} mentions exceeds the limit of {_options.MaxMentionsPerComment}.");

            return new CommBodyResult
            {
                Body = normalised,
                Format = format,
                Analysis = Analyse(normalised, format, bytes, mentions.Count),
                Mentions = mentions,
            };
        }

        public IReadOnlyList<CommMentionToken> ExtractMentions(string? body, string? format)
        {
            var normalised = (body ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            return ExtractMentionsCore(normalised, string.IsNullOrWhiteSpace(format) ? CommBodyFormat.Markdown : format);
        }

        // ---------------------------------------------------------------------------------------------
        private IReadOnlyList<CommMentionToken> ExtractMentionsCore(string body, string format)
        {
            if (body.Length == 0) return Array.Empty<CommMentionToken>();

            // CODE IS NOT PROSE. "@employee:12" inside a fenced block is documentation about the mention
            // syntax, and notifying employee 12 because somebody documented the feature is exactly the kind
            // of surprise that makes a team switch mentions off. PlainText bodies have no code concept, so
            // they are scanned whole.
            var scanned = format == CommBodyFormat.Markdown ? StripCode(body) : body;

            var found = new List<CommMentionToken>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Labelled form first, and its matches are BLANKED from the text before the plain scan runs —
            // otherwise "@[Ahmed](employee:12)" would also match the plain pattern on its "employee:12" tail
            // and produce the same mention twice (harmless after dedup, but it would corrupt the count that
            // MaxMentionsPerComment checks).
            var withoutLabelled = LabelledMention.Replace(scanned, match =>
            {
                Add(found, seen, match.Groups["kind"].Value, match.Groups["key"].Value, match.Groups["label"].Value);
                return new string(' ', match.Length);
            });

            foreach (Match match in PlainMention.Matches(withoutLabelled))
                Add(found, seen, match.Groups["kind"].Value, match.Groups["key"].Value, label: null);

            return found;
        }

        private static void Add(List<CommMentionToken> found, HashSet<string> seen, string kind, string key, string? label)
        {
            // The grammar's alternation is case-insensitive so an author may type "@Employee:12"; the value
            // STORED is always the canonical PascalCase vocabulary constant.
            string canonicalKind = kind.ToLowerInvariant() switch
            {
                "employee" => CommMentionTargetKind.Employee,
                "team" => CommMentionTargetKind.Team,
                "department" => CommMentionTargetKind.Department,
                "role" => CommMentionTargetKind.Role,
                _ => "",
            };
            if (canonicalKind.Length == 0) return;

            int? id = null;
            string? targetKey = null;
            if (canonicalKind == CommMentionTargetKind.Role)
            {
                // A role is named, not numbered.
                targetKey = key;
            }
            else if (int.TryParse(key, System.Globalization.NumberStyles.None,
                     System.Globalization.CultureInfo.InvariantCulture, out int parsed) && parsed > 0)
            {
                id = parsed;
            }
            else
            {
                // "@employee:abc" is not a mention. It is dropped as plain text rather than rejected: a
                // malformed token must not fail the whole comment, because the author cannot always tell
                // where the token grammar ends and their sentence begins.
                return;
            }

            var token = new CommMentionToken(canonicalKind, id, targetKey, string.IsNullOrWhiteSpace(label) ? null : label!.Trim());
            if (seen.Add(token.DedupKey)) found.Add(token);
        }

        // ---------------------------------------------------------------------------------------------
        private static CommBodyAnalysisDto Analyse(string body, string format, int bytes, int mentionCount)
        {
            if (format != CommBodyFormat.Markdown)
            {
                // PlainText has no structure by definition. Reporting counts from a regex scan of it would
                // claim structure the renderer will not honour.
                return new CommBodyAnalysisDto
                {
                    Bytes = bytes, LinkCount = 0, ImageCount = 0, CodeBlockCount = 0, TableCount = 0,
                    MentionTokenCount = mentionCount, HasHeading = false, HasList = false,
                    HasBlockQuote = false, LinkHosts = Array.Empty<string>(),
                };
            }

            int codeBlocks = FencedCode.Matches(body).Count;

            // Structure is measured on the body WITH code removed, so a markdown example inside a fence is
            // not reported as the comment's own structure.
            var prose = StripCode(body);

            var images = Image.Matches(prose);
            var withoutImages = Image.Replace(prose, " ");
            var links = Link.Matches(withoutImages);
            var autoLinks = AutoLink.Matches(Link.Replace(withoutImages, " "));

            var hosts = new List<string>();
            var seenHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in images) AddHost(hosts, seenHosts, m.Groups["url"].Value);
            foreach (Match m in links) AddHost(hosts, seenHosts, m.Groups["url"].Value);
            foreach (Match m in autoLinks) AddHost(hosts, seenHosts, m.Groups["url"].Value);

            return new CommBodyAnalysisDto
            {
                Bytes = bytes,
                LinkCount = links.Count + autoLinks.Count,
                ImageCount = images.Count,
                CodeBlockCount = codeBlocks,
                TableCount = TableDelimiter.Matches(prose).Count,
                MentionTokenCount = mentionCount,
                HasHeading = Heading.IsMatch(prose),
                HasList = ListItem.IsMatch(prose),
                HasBlockQuote = BlockQuote.IsMatch(prose),
                LinkHosts = hosts,
            };
        }

        // Recorded because a comment body is a plausible exfiltration vector: a markdown image with a remote
        // src fires a GET, carrying a referrer, the moment anybody opens the record. A deployment that wants
        // to allow-list hosts has to know which ones appear first, and this is where it finds out.
        private static void AddHost(List<string> hosts, HashSet<string> seen, string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return;   // relative = in-product, not a host
            if (uri.Host.Length == 0) return;
            if (seen.Add(uri.Host)) hosts.Add(uri.Host);
        }

        // Blanks fenced and inline code, PRESERVING LENGTH so that any index computed against the original
        // body still lines up. Replacing with "" would shift every later offset.
        private static string StripCode(string body)
        {
            var withoutFences = FencedCode.Replace(body, m => Blank(m.Value));
            return InlineCode.Replace(withoutFences, m => Blank(m.Value));
        }

        // Newlines are kept so line-anchored patterns (headings, list items, table delimiters) still see the
        // correct line structure around the blanked region.
        private static string Blank(string value)
        {
            var buffer = new StringBuilder(value.Length);
            foreach (char c in value) buffer.Append(c == '\n' ? '\n' : ' ');
            return buffer.ToString();
        }

        public string Excerpt(string? body, string? format, int maxChars = 160)
        {
            if (string.IsNullOrWhiteSpace(body)) return "";
            if (maxChars <= 0) return "";

            var text = body.Replace("\r\n", "\n").Replace('\r', '\n');

            if (string.IsNullOrWhiteSpace(format) || format == CommBodyFormat.Markdown)
            {
                text = FencedCode.Replace(text, " [code] ");
                text = InlineCode.Replace(text, m => m.Value.Trim('`'));
                text = Image.Replace(text, m =>
                {
                    var alt = m.Groups["alt"].Value;
                    return string.IsNullOrWhiteSpace(alt) ? " [image] " : " [" + alt + "] ";
                });
                // A mention becomes its label when it has one, and its canonical token otherwise — never a
                // bare numeric id, which tells the reader nothing.
                text = LabelledMention.Replace(text, m => "@" + m.Groups["label"].Value);
                text = Link.Replace(text, m =>
                {
                    var label = m.Groups["text"].Value;
                    return string.IsNullOrWhiteSpace(label) ? m.Groups["url"].Value : label;
                });
                text = Heading.Replace(text, m => m.Value.TrimStart(' ', '\t', '#'));
                text = BlockQuote.Replace(text, "");
                text = Emphasis.Replace(text, "");
                text = text.Replace("|", " ");
            }

            // Collapse ALL whitespace: an excerpt is one line by definition, and a markdown body is mostly
            // newlines.
            var collapsed = Regex.Replace(text, @"\s+", " ").Trim();
            if (collapsed.Length <= maxChars) return collapsed;

            // Cut on a word boundary when one is close enough, so the excerpt does not end mid-word. The 60%
            // threshold avoids the opposite failure: a body with no spaces (a long URL) truncating to almost
            // nothing.
            var cut = collapsed.Substring(0, maxChars);
            int lastSpace = cut.LastIndexOf(' ');
            if (lastSpace > maxChars * 6 / 10) cut = cut.Substring(0, lastSpace);
            return cut.TrimEnd() + "…";
        }
    }
}