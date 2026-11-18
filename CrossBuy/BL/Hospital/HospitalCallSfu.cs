using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H176 — the internal media server (LiveKit, self-hosted on the hospital network) for group calls of more
	// than four. Each device sends its picture and voice ONCE to the media server, which passes them to the others, so a
	// group may hold up to twelve. A two-person call stays device to device.
	//
	// The server keeps the API secret; a browser gets only a room token for ONE call, valid six hours, minted here
	// (LiveKit access token: an HS256 JWT — iss = API key, sub = the person, video grant = this room). Removing someone is
	// a call to the media server's RoomService with an admin token for that room only.
	// ==========================================================================================
	public static class HospitalCallSfu
	{
		public static string Room(int companyId, int callId) => $"hcall-{companyId}-{callId}";

		private static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

		/// A LiveKit access token. A participant joins, publishes and subscribes in this one room; an admin token may only
		/// administer this one room (used to remove a person).
		public static string Token(string apiKey, string apiSecret, string identity, string? name, string room, DateTime nowUtc, TimeSpan ttl, bool admin = false)
		{
			long nbf = new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
			var video = admin
				? (object)new { room, roomAdmin = true }
				: new { room, roomJoin = true, canPublish = true, canSubscribe = true, canPublishData = true };
			var payload = new Dictionary<string, object?>
			{
				["iss"] = apiKey, ["sub"] = identity, ["nbf"] = nbf, ["exp"] = nbf + (long)ttl.TotalSeconds, ["video"] = video,
			};
			if (!string.IsNullOrEmpty(name)) payload["name"] = name;
			string head = B64Url(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
			string body = B64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
			using var h = new HMACSHA256(Encoding.UTF8.GetBytes(apiSecret));
			return head + "." + body + "." + B64Url(h.ComputeHash(Encoding.UTF8.GetBytes(head + "." + body)));
		}

		/// The media server's HTTP address for its API (ws → http, wss → https).
		public static string HttpBase(string wsUrl)
		{
			var u = new Uri(wsUrl);
			var b = new UriBuilder(u) { Scheme = u.Scheme == "wss" ? "https" : "http", Port = u.Port };
			return b.Uri.GetLeftPart(UriPartial.Authority);
		}

		private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

		/// Takes a person out of the room on the media server (the hub has already taken them out of the call).
		public static async Task RemoveAsync(string wsUrl, string apiKey, string apiSecret, string room, string identity)
		{
			try
			{
				var token = Token(apiKey, apiSecret, "hospital-server", null, room, DateTime.UtcNow, TimeSpan.FromMinutes(2), admin: true);
				using var req = new HttpRequestMessage(HttpMethod.Post, HttpBase(wsUrl) + "/twirp/livekit.RoomService/RemoveParticipant")
				{ Content = new StringContent(JsonSerializer.Serialize(new { room, identity }), Encoding.UTF8, "application/json") };
				req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
				using var _ = await Http.SendAsync(req);
			}
			catch (Exception) { }   // the person's own tab also leaves the room when the hub tells it it was removed
		}
	}
}
