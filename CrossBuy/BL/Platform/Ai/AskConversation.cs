using System;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace CrossBuy.BL.Platform.Ai
{
	// =================================================================================================
	// WHAT WE WERE TALKING ABOUT.
	//
	// The complaint this exists to answer: "مش لازم اعيد الموضوع كل مره". A chat that forgets the
	// subject between turns makes you restate it every time, which is a search box wearing a
	// transcript.
	//
	// WHAT IT STORES, AND WHY THAT IS SAFE. One subject per conversation: the kind of thing
	// ("item"), the stored spelling to put back into the parameter, and a label to show. No rows, no
	// figures, no ids the asker has not already been shown. It is a convenience for the PARAMETER,
	// not a cache of the ANSWER — the follow-up re-runs the whole gate: company re-resolved from the
	// server, intent re-matched, module authority re-checked, rows re-scoped by the same access
	// service. So a remembered subject can never widen what someone may read, and if their
	// permission changed between the two questions, the second answer refuses just as a freshly
	// typed one would.
	//
	// KEYED BY THE USER AS WELL AS THE CONVERSATION. The conversation id is invented by the browser
	// and is therefore attacker-controlled; on its own it would let anyone who guesses one read
	// another person's last subject. Binding the key to the authenticated identity makes a guessed
	// id reach nothing. An unauthenticated request gets no store at all rather than a shared one.
	//
	// IT EXPIRES. Twenty minutes of silence and the subject is gone, because "والتكلفة؟" an hour
	// later is a new question that happens to look like a follow-up, and answering it about a
	// forgotten item is worse than asking which item.
	// =================================================================================================
	public interface IAskConversation
	{
		AskSubject? Get(string? conversationId);
		void Remember(string? conversationId, AskSubject? subject);

		/// The question the assistant last asked and has not had answered.
		AskPending? GetPending(string? conversationId);

		/// Record it, or clear it with null. Cleared as soon as it is used or superseded: a stale
		/// pending question makes the turn after next answer something nobody asked.
		void RememberPending(string? conversationId, AskPending? pending);

		/// THE SURFACE THAT LAST ANSWERED — what a follow-up narrows.
		///
		/// The subject above carries a VALUE for a parameter; this carries the question's SUBJECT
		/// MATTER. The owner asked for the list of doctors, got it, then said «الاسنان فقط» and was
		/// refused: two words that mean nothing on their own and everything after the sentence
		/// before them. Without this, every follow-up starts from nothing and the thing is a search
		/// box with a greeting.
		string? GetLastIntent(string? conversationId);
		void RememberLastIntent(string? conversationId, string? intent);
	}

	public sealed class AskConversation : IAskConversation
	{
		private readonly IMemoryCache _cache;
		private readonly IHttpContextAccessor _http;

		private static readonly TimeSpan Forget = TimeSpan.FromMinutes(20);

		public AskConversation(IMemoryCache cache, IHttpContextAccessor http)
		{
			_cache = cache ?? throw new ArgumentNullException(nameof(cache));
			_http = http ?? throw new ArgumentNullException(nameof(http));
		}

		/// Null whenever the subject could not be bound to a signed-in person. Returning null makes
		/// the caller fall back to asking, which is the correct behaviour; returning a shared bucket
		/// would silently mix two people's conversations.
		private string? Key(string? conversationId)
		{
			if (string.IsNullOrWhiteSpace(conversationId)) return null;
			if (conversationId.Length > 64) return null;

			var user = _http.HttpContext?.User;
			if (user?.Identity?.IsAuthenticated != true) return null;
			var who = user.Identity.Name;
			if (string.IsNullOrWhiteSpace(who)) return null;

			return "ask:" + who + ":" + conversationId;
		}

		// ---- the question we asked, and are waiting on ------------------------------------------
		//
		// Kept on a SHORTER leash than the subject. A subject is context a person carries for as long
		// as they are talking about something; an unanswered question goes stale much faster, and
		// answering the turn after next with it is worse than having asked nothing.
		private static readonly TimeSpan ForgetPending = TimeSpan.FromMinutes(5);

		public string? GetLastIntent(string? conversationId)
		{
			var k = Key(conversationId);
			if (k == null) return null;
			return _cache.TryGetValue(k + ":intent", out string? v) ? v : null;
		}

		public void RememberLastIntent(string? conversationId, string? intent)
		{
			var k = Key(conversationId);
			if (k == null) return;
			if (string.IsNullOrWhiteSpace(intent)) _cache.Remove(k + ":intent");
			else _cache.Set(k + ":intent", intent, Forget);
		}

		public AskPending? GetPending(string? conversationId)
		{
			var k = Key(conversationId);
			if (k == null) return null;
			return _cache.TryGetValue(k + ":pending", out AskPending? p) ? p : null;
		}

		public void RememberPending(string? conversationId, AskPending? pending)
		{
			var k = Key(conversationId);
			if (k == null) return;
			if (pending == null || string.IsNullOrWhiteSpace(pending.Parameter))
			{
				_cache.Remove(k + ":pending");
				return;
			}
			_cache.Set(k + ":pending", pending,
				new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = ForgetPending });
		}

		public AskSubject? Get(string? conversationId)
		{
			var k = Key(conversationId);
			if (k == null) return null;
			return _cache.TryGetValue(k, out AskSubject? s) ? s : null;
		}

		public void Remember(string? conversationId, AskSubject? subject)
		{
			var k = Key(conversationId);
			if (k == null) return;

			// A turn that resolved nothing CLEARS the subject rather than leaving the old one
			// standing. Otherwise a refused question silently keeps a stale subject alive, and the
			// turn after it answers about something two questions ago.
			if (subject == null || string.IsNullOrWhiteSpace(subject.Value)) { _cache.Remove(k); return; }

			// Sliding, not absolute: a conversation that is still going should not expire mid-way.
			_cache.Set(k, subject, new MemoryCacheEntryOptions { SlidingExpiration = Forget });
		}
	}
}
