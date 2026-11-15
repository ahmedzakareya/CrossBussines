using System.Collections.Generic;

namespace CrossBuy.BL.Platform.Ai
{
	// =================================================================================================
	// THE SAFE-QUESTION LAYER.
	//
	// THE ONE RULE THIS EXISTS TO ENFORCE: an answer may never contain a row the person asking could
	// not have read for themselves on the ordinary screen. Not "is unlikely to"; may not.
	//
	// HOW THAT IS GUARANTEED, AND WHY IT DOES NOT DEPEND ON THE LANGUAGE MODEL BEHAVING.
	// A model never touches the database here, and never composes a query. The only thing it may
	// produce is an INTENT NAME from a fixed catalogue plus values for parameters that intent has
	// declared. Everything after that point is deterministic C# that re-derives the company from the
	// server, re-checks the module authority, and filters rows through the SAME access service the
	// screens use. A model that hallucinates an intent gets `NotUnderstood`; a model that invents a
	// parameter gets it ignored; a model instructed by a malicious question to "read everything"
	// has no mechanism through which to do so, because there is no path from text to a query.
	//
	// WHAT AN ANSWER MUST CARRY. Every answer states the company it was read under, the authority it
	// was read with, and HOW MANY rows were withheld for want of permission. The count matters: an
	// answer that silently omits what the asker cannot see reads as a complete answer and is a lie
	// by omission. The count is all that is disclosed — never which rows, never their values.
	// =================================================================================================

	/// A bilingual string. Kept local to the platform layer so BL does not reach into ViewModel.
	public sealed class AskText
	{
		public string Ar { get; init; } = "";
		public string En { get; init; } = "";
		public static AskText Of(string ar, string en) => new() { Ar = ar, En = en };
	}

	/// Why an answer was not given. Travels as a CODE, never as message text: matching a localised
	/// string works in Arabic and silently stops working in English.
	public enum AskRefusal
	{
		None = 0,
		/// No intent in the catalogue matches. The catalogue is closed by design.
		NotUnderstood,
		/// The asker does not hold the authority this intent requires.
		NotPermitted,
		/// No company could be resolved for the request. Nothing is read without one.
		NoCompany,
		/// The question was understood and permitted, and the data holds no answer.
		NoData,
	}

	/// The server-derived scope a question is answered under. There is no constructor that takes a
	/// company from a caller: it comes from IRequestCompanyResolver and nowhere else.
	public sealed class AskScope
	{
		public int CompanyId { get; init; }
		public IReadOnlyList<string> Roles { get; init; } = System.Array.Empty<string>();

		/// The actions this caller actually holds, from the ones the handler named. Decided by
		/// AskService against the module's own access service — a surface never decides its own
		/// authority, it only declares what it would need.
		public IReadOnlyCollection<string> Granted { get; init; } = System.Array.Empty<string>();

		public bool Holds(string? action) =>
			string.IsNullOrEmpty(action)
			|| Granted.Contains(action!, System.StringComparer.OrdinalIgnoreCase);
	}

	/// A structured question. This is the ONLY shape that reaches a handler — never free text.
	public sealed class AskRequest
	{
		/// An id from the catalogue. Anything unrecognised is refused, not guessed at.
		public string Intent { get; init; } = "";
		/// Values for the parameters the intent declared. Unknown keys are ignored, not passed through.
		public IReadOnlyDictionary<string, string?> Parameters { get; init; }
			= new Dictionary<string, string?>();

		/// THE PREVIOUS ANSWER WAS A LIST, AND THIS QUESTION NARROWS IT.
		///
		/// Set only by AskService, and only when routing a follow-up back to the surface that
		/// answered last. Someone who asked for a list and then said "dentistry only" asked for a
		/// shorter LIST; handing them a count is answering a question they did not ask twice.
		public bool ContinuesAList { get; init; }

		/// THE ASKER SAID "his details" ABOUT THE RECORD JUST NAMED.
		///
		/// The answer is that same record, and the sentence above it says it is everything this
		/// surface holds — otherwise showing the row again reads as the question being ignored.
		public bool RepeatsLastRecord { get; init; }

		/// THE WORDS AS TYPED, for lookups that need to search the other way round.
		///
		/// A parameter is extracted by subtraction - strip the intent words, strip the stopwords,
		/// hope the rest is a name - and an unanticipated phrasing leaves residue that defeats the
		/// lookup even though the intent was understood. Carrying the original lets a handler ask
		/// instead whether a stored name appears ANYWHERE in what was typed, which needs no list of
		/// words to strip. It is never used to build a query and never reaches the database; it is
		/// matched against rows the asker is already permitted to see.
		public string Question { get; init; } = "";
	}

	/// What was read, under what authority, and what was held back.
	public sealed class AskProvenance
	{
		public int CompanyId { get; init; }
		public string? CompanyName { get; init; }
		/// The tables the answer was read from, so a reader can go and check it.
		public IReadOnlyList<string> Sources { get; init; } = System.Array.Empty<string>();
		/// e.g. "inventory:read" — the authority that permitted this answer.
		public string Authority { get; init; } = "";

		/// How many rows the asker may not see were removed from this answer. A COUNT only: naming
		/// them, or totalling them, would hand over exactly what the permission withholds.
		public int RowsWithheld { get; init; }
		public AskText? WithheldNote { get; init; }

		/// True when the answer covers everything in scope. False means the reader must treat the
		/// figures as partial, and the note says so.
		public bool Complete => RowsWithheld == 0;
	}

	/// WHAT THE CONVERSATION IS CURRENTLY ABOUT.
	///
	/// A chat in which every question must restate its subject is not a chat; it is a search box with
	/// a transcript. "كام رصيد برجر دجاج" followed by "والتكلفة؟" is the natural way to ask, and the
	/// second question carries no item at all.
	///
	/// THIS CARRIES NO AUTHORITY. It holds a subject the asker ALREADY resolved and was ALREADY shown
	/// — nothing more. The follow-up still passes the whole four-step gate: company re-resolved,
	/// intent re-matched, module authority re-checked, rows re-scoped. Remembering what we were
	/// talking about can therefore never widen what someone may read; at worst it re-asks a question
	/// they were already allowed to ask, and if their permission changed in between, the second
	/// answer refuses exactly as a freshly typed one would.
	public sealed class AskSubject
	{
		/// The PARAMETER this subject can fill, e.g. "item". A subject only ever fills the parameter
		/// of its own kind, so an item is never silently handed to a question about an account.
		public string Kind { get; init; } = "";
		/// The text to put back into that parameter — the stored spelling, not what was typed.
		public string Value { get; init; } = "";
		/// What to show the person, so a follow-up says out loud what it assumed.
		public string Display { get; init; } = "";
	}

	/// A QUESTION THE ASSISTANT ASKED, waiting for its answer.
	///
	/// This is what turns a refusal into a conversation. When a handler needs a parameter nobody
	/// supplied, the gate records which intent it was about to run and which parameter was missing,
	/// and the NEXT message is taken as that parameter rather than as a fresh question. "كام
	/// الرصيد" -> "رصيد أنهي صنف؟" -> "مياه معدنية" now works, where before the third message was a
	/// question about nothing.
	///
	/// It carries no authority, exactly as AskSubject does not: the resumed turn goes through the
	/// whole gate again — company re-resolved, module re-checked, rows re-scoped. All that is
	/// remembered is WHAT WAS ASKED, and only for as long as a person would remember being asked.
	public sealed class AskPending
	{
		/// The intent that was about to run.
		public string Intent { get; init; } = "";
		/// The parameter it was missing.
		public string Parameter { get; init; } = "";
		/// What the person was asked, so a stale answer can be matched to the right question.
		public string Asked { get; init; } = "";
	}

	/// A question offered as a button. Clicking it ASKS it — the assistant never runs a guess on
	/// someone's behalf, it only saves them the typing.
	public sealed class AskSuggestion
	{
		public string Label { get; init; } = "";
		public string Question { get; init; } = "";
	}

	public sealed class AskAnswer
	{
		/// THIS ANSWER WAS A LIST OF RECORDS, not a figure about them.
		///
		/// Stated by the handler that built it, because sniffing it from the rows afterwards is a
		/// guess: the first version read it off which keys the row happened to carry. A follow-up
		/// that narrows a list has to produce a shorter LIST, and it can only know that if the
		/// answer it narrows says so plainly.
		public bool IsRecordList { get; init; }

		public bool Answered { get; init; }
		public AskRefusal Refusal { get; init; } = AskRefusal.None;
		public AskText? Message { get; init; }

		/// Flat rows. Keys are field names the intent declared; no entity graph leaves this layer.
		public IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows { get; init; }
			= System.Array.Empty<IReadOnlyDictionary<string, object?>>();

		public AskProvenance Provenance { get; init; } = new();

		/// Offered next questions. On a refusal these are the near misses; clicking one asks it.
		public IReadOnlyList<AskSuggestion> Suggestions { get; init; }
			= System.Array.Empty<AskSuggestion>();

		/// THE ANSWER IN WORDS. One sentence, built from the rows by the handler, never by a model
		/// — so it cannot say anything the data does not. A table is a result; a sentence is a
		/// reply, and a person who asked a question wanted the second one.
		public AskText? Spoken { get; init; }

		/// SET WHEN THE ASSISTANT IS ASKING BACK rather than refusing. The parameter it needs; the
		/// gate remembers it and treats the next message as the answer.
		public string? NeedsParameter { get; init; }

		/// What this answer turned out to be about, so the NEXT question may be a follow-up.
		public AskSubject? Subject { get; init; }

		/// Set by a HANDLER when it answered about something other than the literal words typed —
		/// a corrected spelling. An assumption the person cannot see is an assumption they cannot
		/// correct, so every one of them is said out loud above the rows.
		public AskText? Understood { get; init; }

		/// Set by the GATE when the question named no subject and the remembered one was used.
		/// Not init-only: only the gate knows a question was a follow-up, and it learns that after
		/// the handler has already built its answer.
		public AskText? About { get; set; }

		public static AskAnswer Refuse(AskRefusal why, AskText message) =>
			new() { Answered = false, Refusal = why, Message = message };

		public static AskAnswer Refuse(AskRefusal why, AskText message, IReadOnlyList<AskSuggestion> offer) =>
			new() { Answered = false, Refusal = why, Message = message, Suggestions = offer };
	}

	/// One answerable question. A handler is the ONLY thing that reads data, and it is responsible
	/// for row-level scoping within its own module.
	public interface IAskHandler
	{
		/// Stable catalogue id. This is what a language model is allowed to choose from.
		string Intent { get; }
		/// The module scope whose authority gates this question, e.g. EntityRegistry.ScopeInventory.
		string Module { get; }
		/// The action within that module, e.g. "read".
		string Action { get; }

		/// FURTHER ACTIONS SOME OF THIS SURFACE'S COLUMNS NEED, beyond the one that admits the
		/// question at all. Evaluated once per request and handed to the handler in AskScope, so a
		/// column can be withheld from a caller who may ask the question but not read that figure.
		///
		/// Accounting draws exactly this line already and the assistant was ignoring it: `read` is
		/// UNCONDITIONAL — any authenticated employee of a configured company may open an accounting
		/// screen — while report-read requires a role and profitability-read is narrower still. The
		/// code says why: a report crosses documents, periods and customers in one artifact that can
		/// be exported and mailed, so it answers to a role rather than to mere membership. An Ask
		/// answer is precisely that artifact, and it was being served under the screen's permission.
		IReadOnlyCollection<string> ExtraActions => System.Array.Empty<string>();

		/// WOULD THESE WORDS NARROW THE ANSWER THIS HANDLER JUST GAVE?
		///
		/// Asked of the handler that answered the PREVIOUS turn, about a question that reached no
		/// surface of its own. «الاسنان فقط» after the list of doctors is two words that mean
		/// nothing alone and everything after the sentence before them; without this they are
		/// refused and the thing is a search box with a greeting.
		///
		/// It must answer only on a value it genuinely holds — never "probably". A yes here sends
		/// the question back to this surface, so a generous yes would answer an unrelated question
		/// with the previous subject's figures, which is the failure this layer exists to stop.
		System.Threading.Tasks.Task<bool> CanNarrowAsync(string? question, AskScope scope,
			System.Threading.CancellationToken ct = default)
			=> System.Threading.Tasks.Task.FromResult(false);
		/// Parameter names this intent accepts. Anything else a caller sends is dropped.
		IReadOnlyCollection<string> Parameters { get; }
		/// Shown to a person choosing what to ask; also what a model is given to select from.
		AskText Description { get; }

		/// Phrasings that select this intent WHILE NO LANGUAGE MODEL IS WIRED. Deterministic: a word
		/// that is not in some handler's list selects nothing, and nothing is approximated. Kept on
		/// the handler so a question and the words that reach it cannot drift apart. When a model is
		/// wired it replaces the matcher ONLY — it still chooses from these same intents and can
		/// still reach nothing else.
		IReadOnlyCollection<string> Keywords { get; }

		/// A question a person can click instead of typing, so the catalogue is discoverable without
		/// guessing at phrasing.
		AskText Example { get; }

		Task<AskAnswer> AnswerAsync(AskRequest request, AskScope scope, CancellationToken ct = default);
	}

	/// The entry point. Resolves the company, finds the intent, checks the authority, then delegates.
	public interface IAskService
	{
		Task<AskAnswer> AskAsync(AskRequest request, CancellationToken ct = default);

		/// Free text in, governed answer out. The text NEVER becomes a query: it selects an intent
		/// from the catalogue and fills that intent's declared parameters, and nothing else. This is
		/// the seam a language model will one day sit in — it will choose the intent instead of the
		/// keyword matcher, and will have no more reach than the matcher has now.
		/// `conversationId` is an opaque token the CLIENT invents, per chat box. It selects a
		/// remembered subject and nothing else: the store is keyed by the authenticated user as well,
		/// so a guessed or stolen id reaches no one else's context.
		Task<AskAnswer> AskTextAsync(string question, string? conversationId = null,
			CancellationToken ct = default);
		/// The catalogue the asker is actually allowed to use — already filtered by their authority,
		/// so a question they cannot ask is never offered to them in the first place.
		Task<IReadOnlyList<IAskHandler>> AvailableAsync(CancellationToken ct = default);
	}
}
