using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Comm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.Comm
{
    // =============================================================================================================
    // EMAIL PHASE 2 — MAIL FROM OUTSIDE COMES BACK IN (owner, 2026-11-23: "the real mail as it is, nothing external").
    //
    // No new mail service and no new library. The company already has ONE mail account (appsettings Smtp) that sends
    // everything; the same account is read here over IMAP, and a message is delivered to the person it is for by the
    // TAG in the address it was sent to:
    //
    //   account+e<employee>k<check>@domain   every employee's own address for the outside world (shown in their mailbox)
    //   account+m<message>k<check>@domain    the Reply-To of every message we send: the answer comes back to its writer
    //
    // Mail servers deliver "account+anything@domain" to "account@domain" (plus addressing), so nothing has to be
    // configured anywhere. <check> is a keyed hash of the rest, so an address cannot be guessed by counting ids.
    // Mail in the account without a valid tag — or whose In-Reply-To names none of our messages — is not ours and is
    // left alone.
    // =============================================================================================================

    /// The tags in the addresses. One place builds and reads them, so the two cannot disagree.
    public static class MailTags
    {
        static byte[] Key(CommService.SmtpOptions s) =>
            SHA256.HashData(Encoding.UTF8.GetBytes("crossbuy-mail-tags\n" + s.User + "\n" + s.Password));

        static string Check(CommService.SmtpOptions s, string body) =>
            Convert.ToHexString(HMACSHA256.HashData(Key(s), Encoding.UTF8.GetBytes(body)))[..8].ToLowerInvariant();

        static (string local, string domain)? Account(CommService.SmtpOptions s)
        {
            var at = (s.From ?? "").Trim().LastIndexOf('@');
            if (at <= 0) return null;
            var local = s.From!.Trim()[..at];
            var plus = local.IndexOf('+');
            return (plus > 0 ? local[..plus] : local, s.From.Trim()[(at + 1)..]);
        }

        /// account+e{employee}k{check}@domain — this employee's address for mail from outside.
        public static string? ForEmployee(CommService.SmtpOptions s, int employeeId) => Build(s, "e" + employeeId);
        /// account+m{message}k{check}@domain — where a reply to this message comes back.
        public static string? ForMessage(CommService.SmtpOptions s, int messageId) => Build(s, "m" + messageId);
        /// The Message-ID our message is sent with; a reply names it in In-Reply-To.
        public static string MessageId(CommService.SmtpOptions s, int messageId) =>
            "<cb." + messageId + "." + Check(s, "id" + messageId) + "@crossbuy.mail>";

        static string? Build(CommService.SmtpOptions s, string body)
        {
            var a = Account(s);
            return a == null ? null : $"{a.Value.local}+{body}k{Check(s, body)}@{a.Value.domain}";
        }

        /// What an address (or a Message-ID) points at: ('e', employee) or ('m', message); null when it is not ours.
        public static (char kind, int id)? Read(CommService.SmtpOptions s, string? address)
        {
            if (string.IsNullOrWhiteSpace(address)) return null;
            var a = Account(s);
            if (a == null) return null;
            var m = Regex.Match(address.Trim(), @"^(?<l>[^+@\s]+)\+(?<k>[em])(?<n>\d{1,9})k(?<c>[0-9a-f]{8})@(?<d>[^@\s]+)$", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            if (!string.Equals(m.Groups["l"].Value, a.Value.local, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(m.Groups["d"].Value, a.Value.domain, StringComparison.OrdinalIgnoreCase)) return null;
            var body = m.Groups["k"].Value.ToLowerInvariant() + m.Groups["n"].Value;
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Check(s, body)), Encoding.ASCII.GetBytes(m.Groups["c"].Value.ToLowerInvariant()))) return null;
            return (body[0], int.Parse(m.Groups["n"].Value));
        }

        /// A Message-ID of ours (from In-Reply-To / References) → the message it names.
        public static int? ReadMessageId(CommService.SmtpOptions s, string? id)
        {
            var m = Regex.Match(id ?? "", @"<cb\.(?<n>\d{1,9})\.(?<c>[0-9a-f]{8})@crossbuy\.mail>", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            var n = int.Parse(m.Groups["n"].Value);
            return string.Equals(Check(s, "id" + n), m.Groups["c"].Value, StringComparison.OrdinalIgnoreCase) ? n : null;
        }
    }

    // ---------------------------------------------------------------------------------------------- IMAP, the few verbs
    /// LOGIN, SELECT, UID SEARCH, UID FETCH, LOGOUT over TLS — all this reader needs, nothing more.
    internal sealed class ImapLite : IAsyncDisposable
    {
        private readonly TcpClient _tcp = new();
        private Stream _s = Stream.Null;
        private int _tag;
        private readonly byte[] _buf = new byte[64 * 1024];
        private int _len, _pos;

        public sealed record Line(string Text, List<byte[]> Literals);

        public async Task ConnectAsync(string host, int port, CancellationToken ct)
        {
            await _tcp.ConnectAsync(host, port, ct);
            var ssl = new SslStream(_tcp.GetStream(), false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, ct);
            _s = ssl;
            var hello = await ReadLineAsync(ct);
            if (!hello.Text.StartsWith("* OK", StringComparison.OrdinalIgnoreCase)) throw new IOException("IMAP greeting: " + Trim(hello.Text));
        }

        public static string Quote(string v) => "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        /// Sends one command and returns its untagged lines; a NO or BAD answer throws.
        public async Task<List<Line>> CommandAsync(string command, CancellationToken ct)
        {
            var tag = "A" + (++_tag);
            var bytes = Encoding.UTF8.GetBytes(tag + " " + command + "\r\n");
            await _s.WriteAsync(bytes, ct); await _s.FlushAsync(ct);
            var lines = new List<Line>();
            while (true)
            {
                var line = await ReadLineAsync(ct);
                if (line.Text.StartsWith(tag + " ", StringComparison.Ordinal))
                {
                    var rest = line.Text[(tag.Length + 1)..];
                    if (!rest.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
                        throw new IOException("IMAP " + command.Split(' ')[0] + ": " + Trim(rest));
                    return lines;
                }
                lines.Add(line);
            }
        }

        static string Trim(string s) => s.Length > 200 ? s[..200] : s;

        /// One response line, with every {n} literal inside it read in full.
        private async Task<Line> ReadLineAsync(CancellationToken ct)
        {
            var text = new StringBuilder();
            var lits = new List<byte[]>();
            while (true)
            {
                var raw = await ReadRawLineAsync(ct);
                text.Append(raw);
                var m = Regex.Match(raw, @"\{(\d+)\}$");
                if (!m.Success) return new Line(text.ToString(), lits);
                var n = int.Parse(m.Groups[1].Value);
                if (n > 40 * 1024 * 1024) throw new IOException("IMAP literal too large");
                var lit = new byte[n];
                var got = 0;
                while (got < n)
                {
                    if (_pos < _len) { var take = Math.Min(n - got, _len - _pos); Buffer.BlockCopy(_buf, _pos, lit, got, take); _pos += take; got += take; continue; }
                    if (!await FillAsync(ct)) throw new IOException("IMAP connection closed");
                }
                lits.Add(lit);
                text.Append("\u0001");      // where the literal was
            }
        }

        private async Task<string> ReadRawLineAsync(CancellationToken ct)
        {
            var ms = new MemoryStream();
            while (true)
            {
                if (_pos >= _len && !await FillAsync(ct)) throw new IOException("IMAP connection closed");
                var b = _buf[_pos++];
                if (b == (byte)'\n')
                {
                    var arr = ms.ToArray();
                    var len = arr.Length > 0 && arr[^1] == (byte)'\r' ? arr.Length - 1 : arr.Length;
                    return Encoding.Latin1.GetString(arr, 0, len);
                }
                ms.WriteByte(b);
                if (ms.Length > 1024 * 1024) throw new IOException("IMAP line too long");
            }
        }

        private async Task<bool> FillAsync(CancellationToken ct)
        {
            _len = await _s.ReadAsync(_buf, ct); _pos = 0;
            return _len > 0;
        }

        public async ValueTask DisposeAsync()
        {
            try { if (_s != Stream.Null) await _s.DisposeAsync(); } catch { }
            _tcp.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------------- MIME, enough to read mail
    /// A received message taken apart: who, to whom, the subject, the text or HTML, and the files.
    public sealed class MimeMail
    {
        public string? From, FromName, ReplyTo, Subject, MessageId, InReplyTo, References;
        public List<string> To = new(), Cc = new(), Delivered = new();
        public DateTime? Date;
        public string? Text, Html;
        public List<(string name, string type, byte[] data, string? contentId)> Files = new();

        static MimeMail() { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }

        public static MimeMail Parse(byte[] raw)
        {
            var mail = new MimeMail();
            var (headers, body) = Split(Encoding.Latin1.GetString(raw));
            string? H(string n) => headers.LastOrDefault(h => h.name.Equals(n, StringComparison.OrdinalIgnoreCase)).value;
            IEnumerable<string> All(string n) => headers.Where(h => h.name.Equals(n, StringComparison.OrdinalIgnoreCase)).Select(h => h.value);

            var from = Addresses(Words(H("From"))).FirstOrDefault();
            mail.From = from.address; mail.FromName = from.name;
            mail.ReplyTo = Addresses(Words(H("Reply-To"))).Select(a => a.address).FirstOrDefault();
            mail.To = Addresses(Words(string.Join(",", All("To")))).Select(a => a.address).ToList();
            mail.Cc = Addresses(Words(string.Join(",", All("Cc")))).Select(a => a.address).ToList();
            mail.Delivered = All("Delivered-To").Concat(All("X-Original-To")).Concat(All("Envelope-To"))
                .SelectMany(v => Addresses(v)).Select(a => a.address).ToList();
            mail.Subject = Words(H("Subject"))?.Trim();
            mail.MessageId = H("Message-ID")?.Trim();
            mail.InReplyTo = H("In-Reply-To")?.Trim();
            mail.References = H("References")?.Trim();
            mail.Date = ParseDate(H("Date"));
            mail.Walk(headers, body, 0);
            return mail;
        }

        /// RFC 5322 dates: "Mon, 23 Nov 2026 14:46:00 +0300 (AST)" — the weekday and the comment are optional, the
        /// zone is +hhmm or a name (GMT/UT/Z).
        static DateTime? ParseDate(string? v)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;
            var t = Regex.Replace(v, @"\([^)]*\)", "").Trim();
            t = Regex.Replace(t, @"^[A-Za-z]{3},\s*", "");
            t = Regex.Replace(t, @"\s+(GMT|UTC|UT|Z)$", " +00:00", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"\s([+-]\d{2})(\d{2})$", " $1:$2");
            t = Regex.Replace(t, @"\s+", " ");
            var formats = new[] { "d MMM yyyy HH:mm:ss zzz", "d MMM yyyy HH:mm zzz", "d MMM yy HH:mm:ss zzz" };
            if (DateTimeOffset.TryParseExact(t, formats, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AllowWhiteSpaces, out var d))
                return d.UtcDateTime;
            return DateTimeOffset.TryParse(t, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AllowWhiteSpaces, out d) ? d.UtcDateTime : null;
        }

        // ---- the parts
        void Walk(List<(string name, string value)> headers, string body, int depth)
        {
            if (depth > 12) return;
            string? H(string n) => headers.LastOrDefault(h => h.name.Equals(n, StringComparison.OrdinalIgnoreCase)).value;
            var (type, prm) = Params(H("Content-Type") ?? "text/plain");
            var (disp, dprm) = Params(H("Content-Disposition") ?? "");
            if (type.StartsWith("multipart/") && prm.TryGetValue("boundary", out var boundary))
            {
                foreach (var part in Parts(body, boundary))
                {
                    var (h, b) = Split(part);
                    Walk(h, b, depth + 1);
                }
                return;
            }
            var bytes = Decode(body, H("Content-Transfer-Encoding"));
            var name = Words(dprm.GetValueOrDefault("filename") ?? prm.GetValueOrDefault("name"));
            var cid = H("Content-ID")?.Trim().Trim('<', '>');
            var isFile = disp == "attachment" || (!string.IsNullOrWhiteSpace(name) && !type.StartsWith("text/"))
                         || (!type.StartsWith("text/") && !type.StartsWith("multipart/"));
            if (!isFile && type == "text/html" && Html == null) { Html = Text_(bytes, prm.GetValueOrDefault("charset")); return; }
            if (!isFile && type == "text/plain" && Text == null) { Text = Text_(bytes, prm.GetValueOrDefault("charset")); return; }
            if (!isFile && type.StartsWith("text/")) return;      // a second alternative of the same text
            var ext = type switch { "image/png" => ".png", "image/jpeg" => ".jpg", "image/gif" => ".gif", "application/pdf" => ".pdf", "message/rfc822" => ".eml", "text/calendar" => ".ics", _ => "" };
            Files.Add((string.IsNullOrWhiteSpace(name) ? "file" + (Files.Count + 1) + ext : name!, type, bytes, cid));
        }

        static IEnumerable<string> Parts(string body, string boundary)
        {
            var marker = "--" + boundary;
            var lines = body.Split('\n');
            StringBuilder? cur = null;
            foreach (var l0 in lines)
            {
                var l = l0.TrimEnd('\r');
                if (l == marker || l == marker + "--")
                {
                    if (cur != null) yield return cur.ToString();
                    if (l.EndsWith("--") && l == marker + "--") yield break;
                    cur = new StringBuilder();
                    continue;
                }
                cur?.Append(l0).Append('\n');
            }
            if (cur != null && cur.Length > 0) yield return cur.ToString();
        }

        static (List<(string name, string value)> headers, string body) Split(string s)
        {
            var i = s.IndexOf("\r\n\r\n", StringComparison.Ordinal); var skip = 4;
            var j = s.IndexOf("\n\n", StringComparison.Ordinal);
            if (i < 0 || (j >= 0 && j < i)) { i = j; skip = 2; }
            var head = i < 0 ? s : s[..i];
            var body = i < 0 ? "" : s[(i + skip)..];
            var list = new List<(string, string)>();
            foreach (var line in Regex.Split(head.Replace("\r\n", "\n"), @"\n(?![ \t])"))
            {
                var c = line.IndexOf(':');
                if (c <= 0) continue;
                list.Add((line[..c].Trim(), Regex.Replace(line[(c + 1)..], @"\n[ \t]+", " ").Trim()));
            }
            return (list, body);
        }

        static (string type, Dictionary<string, string> prm) Params(string v)
        {
            var parts = Regex.Matches(v, @"(?:[^;""]|""(?:\\.|[^""])*"")+").Select(m => m.Value.Trim()).Where(p => p.Length > 0).ToList();
            var type = parts.Count > 0 ? parts[0].ToLowerInvariant() : "";
            var prm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in parts.Skip(1))
            {
                var e = p.IndexOf('='); if (e <= 0) continue;
                var k = p[..e].Trim(); var val = p[(e + 1)..].Trim().Trim('"');
                if (k.EndsWith("*"))      // RFC 2231: utf-8''name%20here (continuations k*0* joined in order)
                {
                    var bare = Regex.Replace(k, @"\*\d*\*?$", "");
                    var q = val.IndexOf("''", StringComparison.Ordinal);
                    var cs = q >= 0 ? val[..q] : null;
                    var dec = Enc(cs).GetString(Percent(q >= 0 ? val[(q + 2)..] : val));
                    var first = !Regex.IsMatch(k, @"\*[1-9]\d*\*?$");
                    prm[bare] = !first && prm.TryGetValue(bare, out var had) ? had + dec : dec;
                }
                else if (!prm.ContainsKey(k)) prm[k] = val;
            }
            return (type, prm);
        }

        static byte[] Percent(string s)
        {
            var o = new List<byte>(s.Length);
            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] == '%' && i + 2 < s.Length && Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2])) { o.Add(Convert.ToByte(s.Substring(i + 1, 2), 16)); i += 2; }
                else o.Add((byte)s[i]);
            }
            return o.ToArray();
        }

        static Encoding Enc(string? charset)
        {
            try { return string.IsNullOrWhiteSpace(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset.Trim().Trim('"')); }
            catch { return Encoding.UTF8; }
        }

        static byte[] Decode(string body, string? cte)
        {
            var e = (cte ?? "").Trim().ToLowerInvariant();
            if (e == "base64")
            {
                var clean = Regex.Replace(body, @"[^A-Za-z0-9+/=]", "");
                clean = clean.Length % 4 == 0 ? clean : clean[..(clean.Length - clean.Length % 4)];
                try { return Convert.FromBase64String(clean); } catch { return Array.Empty<byte>(); }
            }
            if (e == "quoted-printable") return QuotedPrintable(body);
            return Encoding.Latin1.GetBytes(body);
        }

        static byte[] QuotedPrintable(string s)
        {
            var o = new List<byte>(s.Length);
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (c == '=' && i + 1 < s.Length && (s[i + 1] == '\r' || s[i + 1] == '\n'))
                { i++; if (s[i] == '\r' && i + 1 < s.Length && s[i + 1] == '\n') i++; continue; }
                if (c == '=' && i + 2 < s.Length && Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2]))
                { o.Add(Convert.ToByte(s.Substring(i + 1, 2), 16)); i += 2; continue; }
                o.Add((byte)c);
            }
            return o.ToArray();
        }

        static string Text_(byte[] bytes, string? charset) => Enc(charset).GetString(bytes).Replace("\r\n", "\n");

        /// RFC 2047 encoded words ("=?utf-8?B?...?="), and raw 8-bit headers read as UTF-8.
        public static string? Words(string? v)
        {
            if (v == null) return null;
            var latin = Encoding.Latin1.GetBytes(v);
            if (latin.Any(b => b > 127))
            {
                try { v = new UTF8Encoding(false, true).GetString(latin); } catch { }
            }
            v = Regex.Replace(v, @"(\?=)\s+(=\?)", "$1$2");
            return Regex.Replace(v, @"=\?(?<cs>[^?]+)\?(?<e>[BbQq])\?(?<t>[^?]*)\?=", m =>
            {
                try
                {
                    var enc = Enc(m.Groups["cs"].Value.Split('*')[0]);
                    var t = m.Groups["t"].Value;
                    var bytes = m.Groups["e"].Value.ToUpperInvariant() == "B"
                        ? Convert.FromBase64String(t.PadRight(t.Length + (4 - t.Length % 4) % 4, '='))
                        : QuotedPrintable(t.Replace('_', ' '));
                    return enc.GetString(bytes);
                }
                catch { return m.Value; }
            });
        }

        /// "Name <a@b>, c@d" → (address, name) pairs.
        static List<(string address, string? name)> Addresses(string? v)
        {
            var list = new List<(string, string?)>();
            if (string.IsNullOrWhiteSpace(v)) return list;
            foreach (Match m in Regex.Matches(v, @"(?:""?(?<n>[^""<,]*)""?\s*)?<(?<a>[^<>\s]+@[^<>\s]+)>|(?<b>[^\s,<>""]+@[^\s,<>""]+)"))
            {
                var a = (m.Groups["a"].Success ? m.Groups["a"].Value : m.Groups["b"].Value).Trim().Trim('.', ';');
                var n = m.Groups["n"].Success ? m.Groups["n"].Value.Trim() : null;
                list.Add((a, string.IsNullOrWhiteSpace(n) ? null : n));
            }
            return list;
        }
    }

    // ---------------------------------------------------------------------------------------------- the reader
    public interface ICommInboundService
    {
        /// One look at the company's mail account: new messages for our people are delivered; returns how many.
        Task<int> PollAsync(CancellationToken ct = default);
    }

    public sealed class CommInboundService : ICommInboundService
    {
        public const int MaxBytes = 25 * 1024 * 1024;
        const int BatchSize = 50;
        private readonly CrossDbContext _db;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<CommInboundService> _log;
        private readonly INotificationService? _notify;
        private readonly CommService.SmtpOptions _smtp = new();
        private readonly bool _enabled;
        private readonly string _imapHost;
        private readonly int _imapPort;

        public CommInboundService(CrossDbContext db, IConfiguration cfg, IWebHostEnvironment env, ILogger<CommInboundService> log, INotificationService? notify = null)
        {
            _db = db; _env = env; _log = log; _notify = notify;
            cfg.GetSection("Smtp").Bind(_smtp);
            // ON WHEREVER MAIL IS SENT, unless Smtp:Inbound:Enabled says false. The IMAP host is the SMTP host's twin
            // (smtp.x → imap.x) unless Smtp:Inbound:Host names another.
            _enabled = _smtp.IsConfigured && !string.IsNullOrWhiteSpace(_smtp.User) && cfg.GetValue("Smtp:Inbound:Enabled", true);
            _imapHost = cfg["Smtp:Inbound:Host"] ?? (_smtp.Host.StartsWith("smtp.", StringComparison.OrdinalIgnoreCase) ? "imap." + _smtp.Host[5..] : _smtp.Host);
            _imapPort = cfg.GetValue("Smtp:Inbound:Port", 993);
        }

        public async Task<int> PollAsync(CancellationToken ct = default)
        {
            if (!_enabled) return 0;
            var account = _smtp.User.Trim().ToLowerInvariant();
            var state = await _db.Set<CommMailInbound>().FirstOrDefaultAsync(x => x.Account == account, ct);
            var received = 0;
            try
            {
                await using var imap = new ImapLite();
                await imap.ConnectAsync(_imapHost, _imapPort, ct);
                await imap.CommandAsync("LOGIN " + ImapLite.Quote(_smtp.User) + " " + ImapLite.Quote(_smtp.Password), ct);
                var sel = await imap.CommandAsync("SELECT INBOX", ct);
                long Num(string key) => sel.Select(l => Regex.Match(l.Text, @"\[" + key + @" (\d+)\]")).Where(m => m.Success).Select(m => long.Parse(m.Groups[1].Value)).FirstOrDefault();
                var validity = Num("UIDVALIDITY"); var next = Num("UIDNEXT");

                // THE FIRST LOOK STARTS FROM NOW. The account already holds years of mail that was never meant for
                // anyone's mailbox here; only what arrives from this point on is delivered. A changed UIDVALIDITY
                // (the server renumbered the folder) starts again the same way rather than re-reading everything.
                if (state == null || state.UidValidity != validity)
                {
                    if (state == null) { state = new CommMailInbound { Account = account }; _db.Add(state); }
                    state.UidValidity = validity; state.LastUid = Math.Max(0, next - 1);
                    state.CheckedAt = DateTime.UtcNow; state.LastError = null;
                    await _db.SaveChangesAsync(ct);
                    await imap.CommandAsync("LOGOUT", ct);
                    return 0;
                }

                var found = await imap.CommandAsync("UID SEARCH UID " + (state.LastUid + 1) + ":*", ct);
                var uids = found.Where(l => l.Text.StartsWith("* SEARCH", StringComparison.OrdinalIgnoreCase))
                    .SelectMany(l => l.Text[8..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    .Select(x => long.TryParse(x, out var n) ? n : 0).Where(n => n > state.LastUid).OrderBy(n => n).Take(BatchSize).ToList();

                foreach (var uid in uids)
                {
                    var size = await imap.CommandAsync("UID FETCH " + uid + " (RFC822.SIZE)", ct);
                    var bytes = size.Select(l => Regex.Match(l.Text, @"RFC822\.SIZE (\d+)")).Where(m => m.Success).Select(m => long.Parse(m.Groups[1].Value)).FirstOrDefault();
                    if (bytes > 0 && bytes <= MaxBytes)
                    {
                        var got = await imap.CommandAsync("UID FETCH " + uid + " (BODY.PEEK[])", ct);
                        var raw = got.SelectMany(l => l.Literals).OrderByDescending(b => b.Length).FirstOrDefault();
                        if (raw != null)
                        {
                            try { if (await ImportAsync(MimeMail.Parse(raw), ct)) received++; }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            { _log.LogWarning(ex, "Inbound mail UID {Uid} could not be delivered; it is skipped", uid); _db.ChangeTracker.Clear(); state = await _db.Set<CommMailInbound>().FirstAsync(x => x.Account == account, ct); }
                        }
                    }
                    else if (bytes > MaxBytes) _log.LogWarning("Inbound mail UID {Uid} is {Bytes} bytes, over the limit; skipped", uid, bytes);
                    state.LastUid = uid;
                    await _db.SaveChangesAsync(ct);
                }
                await imap.CommandAsync("LOGOUT", ct);
                state.CheckedAt = DateTime.UtcNow; state.LastError = null; state.Received += received;
                await _db.SaveChangesAsync(ct);
                return received;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Inbound mail: the look at {Host} failed", _imapHost);
                if (state != null)
                {
                    try { state.CheckedAt = DateTime.UtcNow; state.LastError = (ex.Message.Length > 390 ? ex.Message[..390] : ex.Message); await _db.SaveChangesAsync(ct); } catch { }
                }
                return received;
            }
        }

        /// Deliver one received message to the person its tag (or its In-Reply-To) names. False when it is not ours.
        internal async Task<bool> ImportAsync(MimeMail mail, CancellationToken ct)
        {
            // ---- WHO IT IS FOR
            int? employeeId = null, parentId = null;
            foreach (var a in mail.Delivered.Concat(mail.To).Concat(mail.Cc))
            {
                var t = MailTags.Read(_smtp, a);
                if (t is { kind: 'm' }) { parentId = t.Value.id; break; }
                if (t is { kind: 'e' } && employeeId == null) employeeId = t.Value.id;
            }
            if (parentId == null && employeeId == null)
                foreach (var r in (mail.InReplyTo + " " + mail.References).Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    if (MailTags.ReadMessageId(_smtp, r) is int n) { parentId = n; break; }

            CommMessage? parent = null;
            if (parentId is int pid)
            {
                parent = await _db.CommMessages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == pid, ct);
                // The answer goes to whoever WROTE the message it answers; a message from outside is answered by
                // its owner, the colleague whose Inbox it is in.
                if (parent != null)
                    employeeId ??= parent.CreatedBy is int by && by > 0 ? by
                        : await _db.Set<CommMailboxItem>().AsNoTracking().Where(i => i.MessageId == pid && i.Box == "Inbox").Select(i => (int?)i.EmployeeId).FirstOrDefaultAsync(ct);
            }
            if (employeeId == null) return false;
            var emp = await _db.Employee.AsNoTracking().Where(e => e.ID == employeeId && e.IsActive)
                .Select(e => new { e.ID, e.EmpCompanyID }).FirstOrDefaultAsync(ct);
            if (emp == null) return false;
            var companyId = emp.EmpCompanyID;
            if (parent != null && parent.CompanyID != companyId) parent = null;

            // ---- ONCE
            var externalId = string.IsNullOrWhiteSpace(mail.MessageId) ? null : (mail.MessageId!.Length > 300 ? mail.MessageId[..300] : mail.MessageId);
            if (externalId != null && await _db.CommMessages.AsNoTracking().AnyAsync(m => m.CompanyID == companyId && m.ExternalMessageId == externalId, ct))
                return false;

            // ---- THE MESSAGE
            var now = DateTime.UtcNow;
            var tagged = MailTags.ForEmployee(_smtp, emp.ID) ?? _smtp.From;
            string Cut(string? s, int n) => (s ?? "").Length > n ? s![..n] : (s ?? "");
            var m = new CommMessage
            {
                CompanyID = companyId, Direction = "In",
                // THE ADDRESS TO ANSWER: Reply-To when the writer set one (our own mail sets it to its tag), else From.
                FromAddress = Cut(mail.ReplyTo ?? mail.From, 260), FromName = string.IsNullOrWhiteSpace(mail.FromName) ? null : Cut(mail.FromName, 200),
                ToAddress = Cut(string.Join(", ", mail.To.Count > 0 ? mail.To : new List<string> { tagged }), 260),
                Cc = mail.Cc.Count > 0 ? Cut(string.Join(", ", mail.Cc), 400) : null,
                Subject = Cut(string.IsNullOrWhiteSpace(mail.Subject) ? "(no subject)" : mail.Subject, 300),
                Status = "Sent", SentAt = mail.Date ?? now, CreatedAt = now,
                ParentId = parent?.Id, ThreadId = parent == null ? null : (parent.ThreadId ?? parent.Id),
                Kind = parent == null ? "New" : "Reply", ExternalMessageId = externalId,
            };
            _db.CommMessages.Add(m);
            await _db.SaveChangesAsync(ct);
            if (m.ThreadId == null) m.ThreadId = m.Id;

            // ---- THE FILES: saved beside the mail's own uploads; an inline picture keeps its place in the HTML
            var html = mail.Html;
            var dir = Path.Combine(_env.WebRootPath, "uploads", "comm");
            Directory.CreateDirectory(dir);
            foreach (var f in mail.Files.Take(30))
            {
                if (f.data.Length == 0) continue;
                var safeName = Regex.Replace(Path.GetFileName(f.name), @"[\\/:*?""<>|\r\n]+", "_");
                var ext = Path.GetExtension(safeName);
                if (ext.Length > 10 || Regex.IsMatch(ext, @"[^.\w]")) ext = "";
                var stored = Guid.NewGuid().ToString("N") + ext.ToLowerInvariant();
                await File.WriteAllBytesAsync(Path.Combine(dir, stored), f.data, ct);
                var path = "/uploads/comm/" + stored;
                if (!string.IsNullOrEmpty(f.contentId) && html != null && html.Contains("cid:" + f.contentId, StringComparison.OrdinalIgnoreCase))
                {
                    html = Regex.Replace(html, "cid:" + Regex.Escape(f.contentId), path, RegexOptions.IgnoreCase);
                    if (f.type.StartsWith("image/")) continue;      // shown in the text, not listed as a file
                }
                _db.CommAttachments.Add(new CommAttachment { CommMessageId = m.Id, FilePath = path, FileName = Cut(safeName, 260), Size = f.data.LongLength, CreatedAt = now });
            }
            // HTML stays HTML (the message screen shows it in a sandbox without scripts); plain text becomes safe HTML.
            m.Body = html ?? ("<div style=\"white-space:pre-wrap\">" + System.Net.WebUtility.HtmlEncode(mail.Text ?? "") + "</div>");

            _db.Set<CommMailboxItem>().Add(new CommMailboxItem { CompanyID = companyId, MessageId = m.Id, EmployeeId = emp.ID, Box = "Inbox", Address = tagged, CreatedAt = now });
            await _db.SaveChangesAsync(ct);

            if (_notify != null)
            {
                try
                {
                    var who = m.FromName ?? m.FromAddress ?? "";
                    await _notify.NotifyAsync(emp.ID, "رسالة جديدة من " + who, "New mail from " + who, m.Subject, m.Subject,
                        NotificationTypes.MailReceived, m.Id, "/Comm/View/" + m.Id, companyId);
                }
                catch (Exception ex) { _log.LogWarning(ex, "Inbound mail {Id}: the arrival notification failed; the message is delivered", m.Id); }
            }
            _log.LogInformation("Inbound mail {Id} delivered to employee {Employee} (company {Company})", m.Id, emp.ID, companyId);
            return true;
        }
    }

    /// Looks at the company's mail account every minute (Smtp:Inbound:PollSeconds). Only the worker-primary process
    /// reads, the same rule as the outbox; startup never waits on the mail server.
    public sealed class CommInboundHostedService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly CrossBuy.BL.Platform.IWorkerGate _gate;
        private readonly ILogger<CommInboundHostedService> _log;

        // Singletons only (a hosted service is one): the configuration is read through a scope when the work starts.
        public CommInboundHostedService(IServiceScopeFactory scopes, CrossBuy.BL.Platform.IWorkerGate gate, ILogger<CommInboundHostedService> log)
        { _scopes = scopes; _gate = gate; _log = log; }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); } catch (TaskCanceledException) { return; }
            if (!await _gate.WaitUntilAllowedAsync(nameof(CommInboundHostedService), stoppingToken)) return;
            int seconds;
            using (var s = _scopes.CreateScope())
                seconds = Math.Max(15, s.ServiceProvider.GetService<IConfiguration>()?.GetValue("Smtp:Inbound:PollSeconds", 60) ?? 60);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
            do
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<ICommInboundService>().PollAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex) { _log.LogError(ex, "Inbound mail pass failed"); }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
    }
}
