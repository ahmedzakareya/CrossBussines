using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CrossBuy.BL.CrmIdentity
{
	/// <summary>
	/// CRM C4 — the inbox's rules that do not need a database: WhatsApp's 24-hour window, Meta's webhook signature and
	/// payload, the party key per channel. Pure, so every rule is pinned by tests with real-shaped payloads.
	/// </summary>
	public static class CrmInboxRules
	{
		public static readonly TimeSpan WhatsAppWindow = TimeSpan.FromHours(24);

		/// WhatsApp lets a business answer freely only within 24 hours of the customer's last message; after that only
		/// an approved template may open the conversation again.
		public static bool InsideWindow(DateTime? lastInboundUtc, DateTime nowUtc) =>
			lastInboundUtc is DateTime t && nowUtc - t <= WhatsAppWindow && nowUtc >= t;

		/// Meta signs every webhook with the app secret: header "X-Hub-Signature-256: sha256=&lt;hex&gt;" over the RAW body.
		/// Compared in constant time; a missing secret or header is never valid.
		public static bool SignatureValid(string? appSecret, byte[] body, string? header)
		{
			if (string.IsNullOrEmpty(appSecret) || string.IsNullOrEmpty(header) || !header.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)) return false;
			byte[] given;
			try { given = Convert.FromHexString(header[7..]); } catch (FormatException) { return false; }
			var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), body);
			return given.Length == expected.Length && CryptographicOperations.FixedTimeEquals(given, expected);
		}

		public static string Sign(string appSecret, byte[] body) => "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), body)).ToLowerInvariant();

		/// The party's key on a channel: digits for a phone (WhatsApp sends "96599001122"), lower case for an e-mail,
		/// a hash for a chat visitor's token (the raw token is a capability and is never stored).
		public static string PhoneKey(string phone) => "wa:" + new string(phone.Where(char.IsDigit).ToArray());
		public static string EmailKey(string email) => "mail:" + email.Trim().ToLowerInvariant();
		public static string VisitorKey(string token) => "chat:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..40];

		public static string NewToken(int bytes = 24) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();

		public sealed record Inbound(string PhoneNumberId, string From, string? Name, string MessageId, string Text, DateTime AtUtc);
		public sealed record StatusUpdate(string MessageId, string Status, string? Error);

		/// Reads a WhatsApp Cloud API webhook: the customer's messages and the delivery statuses of ours. A message type
		/// other than text is kept, described in words, rather than dropped — a photo the customer sent must not vanish.
		public static (List<Inbound> messages, List<StatusUpdate> statuses) ParseWebhook(string json)
		{
			var messages = new List<Inbound>(); var statuses = new List<StatusUpdate>();
			using var doc = JsonDocument.Parse(json);
			if (!doc.RootElement.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array) return (messages, statuses);
			foreach (var entry in entries.EnumerateArray())
			{
				if (!entry.TryGetProperty("changes", out var changes)) continue;
				foreach (var change in changes.EnumerateArray())
				{
					if (!change.TryGetProperty("value", out var v)) continue;
					var phoneId = v.TryGetProperty("metadata", out var md) && md.TryGetProperty("phone_number_id", out var pid) ? pid.GetString() ?? "" : "";
					var names = new Dictionary<string, string>();
					if (v.TryGetProperty("contacts", out var contacts))
						foreach (var c in contacts.EnumerateArray())
							if (c.TryGetProperty("wa_id", out var wa) && c.TryGetProperty("profile", out var pr) && pr.TryGetProperty("name", out var nm))
								names[wa.GetString() ?? ""] = nm.GetString() ?? "";
					if (v.TryGetProperty("messages", out var msgs))
						foreach (var m in msgs.EnumerateArray())
						{
							var from = m.TryGetProperty("from", out var f) ? f.GetString() ?? "" : "";
							var id = m.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
							var type = m.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
							var text = type == "text" && m.TryGetProperty("text", out var tx) && tx.TryGetProperty("body", out var b) ? b.GetString() ?? ""
								: type == "button" && m.TryGetProperty("button", out var bt) && bt.TryGetProperty("text", out var btx) ? btx.GetString() ?? ""
								: "[" + type + "]";
							var ts = m.TryGetProperty("timestamp", out var tse) && long.TryParse(tse.GetString(), out var secs) ? DateTimeOffset.FromUnixTimeSeconds(secs).UtcDateTime : DateTime.UtcNow;
							if (from.Length > 0 && id.Length > 0) messages.Add(new Inbound(phoneId, from, names.GetValueOrDefault(from), id, text, ts));
						}
					if (v.TryGetProperty("statuses", out var sts))
						foreach (var s in sts.EnumerateArray())
						{
							var id = s.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
							var st = s.TryGetProperty("status", out var x) ? x.GetString() ?? "" : "";
							string? err = s.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array && errs.GetArrayLength() > 0
								&& errs[0].TryGetProperty("title", out var et) ? et.GetString() : null;
							if (id.Length > 0) statuses.Add(new StatusUpdate(id, st, err));
						}
				}
			}
			return (messages, statuses);
		}

		/// Meta's status words mapped to ours. A status never moves a message backwards (read stays read).
		public static string? DeliveryFor(string metaStatus) => metaStatus switch
		{
			"sent" => "Sent", "delivered" => "Delivered", "read" => "Read", "failed" => "Failed", _ => null,
		};

		static readonly string[] Order = { "Queued", "Sent", "Delivered", "Read" };
		public static bool Advances(string current, string next) =>
			next == "Failed" ? current is "Queued" or "Sent" : Array.IndexOf(Order, next) > Array.IndexOf(Order, current);
	}
}
