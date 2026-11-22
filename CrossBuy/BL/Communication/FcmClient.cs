using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CrossBuy.BL.Communication
{
	// ==========================================================================================
	// PUSH TO A CLOSED PHONE — the Firebase Cloud Messaging client (owner, 2026-11-22: «Google FCM, no content»).
	//
	// WHAT LEAVES THE NETWORK: a data-only message holding the KIND of news ("msg" or "call") and nothing else — no
	// text, no name, no conversation. Google only wakes the app; the app then asks OUR server what is new over its own
	// signed-in connection (api/chat/push/summary) and shows the notice itself. So the message text never passes
	// through Google, and an FCM log reveals only that someone got something at some time.
	//
	// CONFIGURATION: Communication:Push:Fcm:ServiceAccountFile — the path of the service-account JSON the Firebase
	// console gives (Project settings → Service accounts → Generate new private key). Absent or unreadable → IsEnabled
	// is false and nothing is sent: push stays off quietly, and the chat works exactly as before.
	//
	// AUTH: FCM HTTP v1 takes an OAuth2 access token. It is obtained from the service account by a JWT signed with its
	// RS256 key (the standard "JWT bearer" grant), and cached until five minutes before it expires.
	// ==========================================================================================
	public sealed class FcmClient
	{
		public enum Result { Sent, DeadToken, Failed }

		private sealed record Account(string ProjectId, string ClientEmail, string PrivateKeyPem, string TokenUri);

		private static readonly SemaphoreSlim Gate = new(1, 1);
		private static string? _accessToken;
		private static DateTime _accessUntil;
		private static Account? _account;
		private static string? _accountPath;

		private readonly IHttpClientFactory _http;
		private readonly ILogger _log;
		private readonly string? _path;

		public FcmClient(IHttpClientFactory http, IConfiguration config, ILogger log)
		{
			_http = http; _log = log;
			_path = config["Communication:Push:Fcm:ServiceAccountFile"];
		}

		public bool IsEnabled => Load() != null;

		private Account? Load()
		{
			if (string.IsNullOrWhiteSpace(_path)) return null;
			if (_account != null && _accountPath == _path) return _account;
			try
			{
				using var doc = JsonDocument.Parse(File.ReadAllText(_path));
				var r = doc.RootElement;
				_account = new Account(r.GetProperty("project_id").GetString()!, r.GetProperty("client_email").GetString()!,
					r.GetProperty("private_key").GetString()!, r.TryGetProperty("token_uri", out var u) ? u.GetString()! : "https://oauth2.googleapis.com/token");
				_accountPath = _path;
				return _account;
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
			{
				_log.LogWarning("Push: the FCM service-account file {Path} could not be read ({Error}); push stays off", _path, e.GetType().Name);
				return null;
			}
		}

		private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

		private async Task<string?> AccessTokenAsync(Account a, CancellationToken ct)
		{
			if (_accessToken != null && DateTime.UtcNow < _accessUntil) return _accessToken;
			await Gate.WaitAsync(ct);
			try
			{
				if (_accessToken != null && DateTime.UtcNow < _accessUntil) return _accessToken;
				var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
				var head = B64(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
				var claims = B64(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
				{
					["iss"] = a.ClientEmail, ["scope"] = "https://www.googleapis.com/auth/firebase.messaging",
					["aud"] = a.TokenUri, ["iat"] = now, ["exp"] = now + 3600,
				}));
				using var rsa = RSA.Create();
				rsa.ImportFromPem(a.PrivateKeyPem);
				var sig = B64(rsa.SignData(Encoding.ASCII.GetBytes(head + "." + claims), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
				var http = _http.CreateClient(nameof(FcmClient));
				using var res = await http.PostAsync(a.TokenUri, new FormUrlEncodedContent(new Dictionary<string, string>
				{
					["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer", ["assertion"] = head + "." + claims + "." + sig,
				}), ct);
				if (!res.IsSuccessStatusCode) { _log.LogWarning("Push: Google refused the FCM sign-in ({Status})", (int)res.StatusCode); return null; }
				using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
				_accessToken = doc.RootElement.GetProperty("access_token").GetString();
				var secs = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
				_accessUntil = DateTime.UtcNow.AddSeconds(Math.Max(60, secs - 300));
				return _accessToken;
			}
			finally { Gate.Release(); }
		}

		/// One wake-up to one device. kind: "msg" or "call" (a call is urgent and short-lived; a message may wait).
		public async Task<Result> WakeAsync(string token, string kind, CancellationToken ct)
		{
			var a = Load(); if (a == null) return Result.Failed;
			try
			{
				var access = await AccessTokenAsync(a, ct); if (access == null) return Result.Failed;
				var call = kind == "call";
				var body = new
				{
					message = new
					{
						token,
						data = new Dictionary<string, string> { ["t"] = "wake", ["k"] = call ? "call" : "msg" },
						android = new { priority = "HIGH", ttl = call ? "45s" : "3600s", collapse_key = call ? null : "cb-msg" },
						apns = new { headers = new Dictionary<string, string> { ["apns-priority"] = "10", ["apns-push-type"] = "background" }, payload = new { aps = new { content_available = 1 } } },
					},
				};
				var http = _http.CreateClient(nameof(FcmClient));
				using var req = new HttpRequestMessage(HttpMethod.Post, $"https://fcm.googleapis.com/v1/projects/{a.ProjectId}/messages:send")
				{
					Content = new StringContent(JsonSerializer.Serialize(body, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }), Encoding.UTF8, "application/json"),
				};
				req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
				using var res = await http.SendAsync(req, ct);
				if (res.IsSuccessStatusCode) return Result.Sent;
				var text = await res.Content.ReadAsStringAsync(ct);
				// a token the app no longer holds (uninstalled, signed out, rotated): retire it, never retry it
				if (res.StatusCode == HttpStatusCode.NotFound || text.Contains("UNREGISTERED", StringComparison.Ordinal) || text.Contains("INVALID_ARGUMENT", StringComparison.Ordinal) && text.Contains("token", StringComparison.OrdinalIgnoreCase))
					return Result.DeadToken;
				if (res.StatusCode == HttpStatusCode.Unauthorized) _accessToken = null;
				_log.LogWarning("Push: FCM answered {Status}", (int)res.StatusCode);
				return Result.Failed;
			}
			catch (Exception e) when (e is HttpRequestException or TaskCanceledException or CryptographicException or JsonException)
			{
				_log.LogWarning("Push: the wake-up was not sent ({Error})", e.GetType().Name);
				return Result.Failed;
			}
		}
	}
}
