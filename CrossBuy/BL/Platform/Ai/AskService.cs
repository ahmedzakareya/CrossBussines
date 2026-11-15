using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.Platform.Ai
{
	// =================================================================================================
	// THE GATE. Every question passes through here and there is no other way in.
	//
	// The order of the four steps is the security property, and it is deliberate:
	//
	//   1. COMPANY, from the server. Resolved through IRequestCompanyResolver, which takes nothing
	//      from the caller. An unresolved company stops the request before a handler is even located,
	//      so no code path can read a row while the company is in doubt.
	//   2. INTENT, from a CLOSED catalogue. An unknown intent is refused, never approximated. There is
	//      no "nearest match" and no free-text fallback, because a near match is a different question.
	//   3. AUTHORITY, re-checked here. The handler's module and action are put to the SAME
	//      IModuleAccessService the screens use. A handler cannot grant itself access by existing.
	//   4. ONLY THEN the handler runs, and it is still responsible for row-level scoping inside its
	//      own module — this gate cannot know which warehouses or projects an asker may see.
	//
	// WHY THE AUTHORITY CHECK IS NOT LEFT TO THE HANDLER. A handler that forgets it would answer
	// everything, and the omission would be invisible — nothing fails, a screen simply shows data to
	// someone who should not have it. Checking centrally means a new handler is deny-by-default:
	// declare a module and action that do not resolve, and it answers nobody.
	//
	// REFUSALS ARE INDISTINGUISHABLE ON PURPOSE. "You may not ask this" and "this returned nothing"
	// are different codes, but "that item is not yours" and "that item does not exist" are the same
	// answer, because telling them apart lets a caller enumerate ids they cannot see.
	// =================================================================================================
	public sealed class AskService : IAskService
	{
		private readonly IRequestCompanyResolver _company;
		private readonly IBusinessContextAccessor _context;
		private readonly IEnumerable<IAskHandler> _handlers;
		private readonly IEnumerable<IModuleAccessService> _modules;
		private readonly IAskConversation _conversation;
		private readonly IAskIntentResolver _resolver;
		private readonly IAskRefusalLog _refusals;
		private readonly ILogger<AskService> _log;

		public AskService(
			IRequestCompanyResolver company,
			IBusinessContextAccessor context,
			IEnumerable<IAskHandler> handlers,
			IEnumerable<IModuleAccessService> modules,
			IAskConversation conversation,
			IAskIntentResolver resolver,
			IAskRefusalLog refusals,
			ILogger<AskService> log)
		{
			_refusals = refusals ?? throw new ArgumentNullException(nameof(refusals));
			_resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
			_company = company ?? throw new ArgumentNullException(nameof(company));
			_context = context ?? throw new ArgumentNullException(nameof(context));
			_handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
			_modules = modules ?? throw new ArgumentNullException(nameof(modules));
			_conversation = conversation ?? throw new ArgumentNullException(nameof(conversation));
			_log = log;
		}

		private static readonly AskText NotUnderstood = AskText.Of(
			"لا أعرف كيف أجيب على هذا السؤال. الأسئلة التي يمكنني الإجابة عليها محدودة بقائمة مُعرَّفة،"
			+ " ولا أخمّن ما لم أفهمه.",
			"I do not know how to answer that. The questions I can answer are a defined list, and I do"
			+ " not guess at one I did not understand.");

		private static readonly AskText NotPermitted = AskText.Of(
			"ليست لديك صلاحية على هذه البيانات.",
			"You do not have permission for this data.");

		private static readonly AskText NoCompany = AskText.Of(
			"تعذّر تحديد الشركة لهذا الطلب، ولا تُقرأ أي بيانات بدونها.",
			"No company could be determined for this request, and nothing is read without one.");

		public async Task<AskAnswer> AskAsync(AskRequest request, CancellationToken ct = default)
		{
			ArgumentNullException.ThrowIfNull(request);

			// ---- 1. company, server-derived -------------------------------------------------------
			var resolution = await _company.ResolveAsync(cancellationToken: ct);
			if (!resolution.Ok)
				return AskAnswer.Refuse(AskRefusal.NoCompany, NoCompany);

			// ---- 2. intent, from the closed catalogue ---------------------------------------------
			var handler = _handlers.FirstOrDefault(h =>
				string.Equals(h.Intent, request.Intent, StringComparison.OrdinalIgnoreCase));
			if (handler == null)
				return AskAnswer.Refuse(AskRefusal.NotUnderstood, NotUnderstood);

			// ---- 3. authority, against the module's own service ------------------------------------
			var context = await _context.GetCurrentAsync(ct);
			var module = _modules.FirstOrDefault(m =>
				string.Equals(m.Scope, handler.Module, StringComparison.OrdinalIgnoreCase));
			if (module == null)
			{
				// A handler naming a module that is not registered answers NOBODY. Deny-by-default
				// rather than fall through to "no check was possible, so allow".
				_log?.LogWarning(
					"Ask intent {Intent} names module {Module}, which has no registered access service. Refusing.",
					handler.Intent, handler.Module);
				return AskAnswer.Refuse(AskRefusal.NotPermitted, NotPermitted);
			}

			if (!await module.CanAsync(context, handler.Action, target: null, cancellationToken: ct))
				return AskAnswer.Refuse(AskRefusal.NotPermitted, NotPermitted);

			// ---- 4. the handler, which still scopes its own rows ------------------------------------
			// THE EXTRA ACTIONS, DECIDED HERE AND NOWHERE ELSE. A surface declares what a column
			// would need; the module's own access service decides whether this caller holds it. A
			// surface that could answer that question for itself would be deciding its own authority.
			var granted = new List<string>();
			foreach (var extra in handler.ExtraActions)
				if (await module.CanAsync(context, extra, target: null, cancellationToken: ct))
					granted.Add(extra);

			var scope = new AskScope
			{
				CompanyId = resolution.CompanyId,
				Roles = await module.RolesAsync(context, ct),
				Granted = granted,
			};

			// Parameters are reduced to the ones the intent DECLARED. An extra key a caller invents —
			// or a language model hallucinates — never reaches the handler.
			var declared = new HashSet<string>(handler.Parameters, StringComparer.OrdinalIgnoreCase);
			var clean = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
			foreach (var kv in request.Parameters)
				if (declared.Contains(kv.Key)) clean[kv.Key] = kv.Value;

			return await handler.AnswerAsync(
				// EVERY FIELD THE CALLER SET MUST SURVIVE THIS REBUILD. The request is rebuilt here
				// to strip parameters the intent never declared, and anything not copied across is
				// silently lost — ContinuesAList was, so a follow-up narrowing a LIST came back as
				// a count. ComparePeriods was lost the same way, by the validator, weeks ago.
				new AskRequest
				{
					Intent = handler.Intent,
					Parameters = clean,
					Question = request.Question,
					ContinuesAList = request.ContinuesAList,
					RepeatsLastRecord = request.RepeatsLastRecord,
				},
				scope, ct);
		}

		// =============================================================================================
		// FREE TEXT -> INTENT. The only place text is interpreted, and it interprets text into a
		// CHOICE, never into a query.
		//
		// This is keyword matching, not language understanding, and the screen says so. It is
		// deliberately narrow: a question that matches nothing is refused and the catalogue is
		// offered, because a loose matcher answers the WRONG question confidently — and a figure
		// that looks like the one you asked for is more dangerous than no figure at all.
		//
		// When a language model replaces this method it gains no reach. It will pick from the same
		// catalogue, fill the same declared parameters, and pass through the same four-step gate.
		// =============================================================================================
		private static readonly AskText Catalogue = AskText.Of(
			"لم أفهم السؤال. هذه هي الأسئلة التي أستطيع الإجابة عليها:",
			"I did not understand that. These are the questions I can answer:");

		/// Arabic arrives written several ways for the same word. Normalising both sides means a
		/// person who types ‏"المخزون"‏ and one who types ‏"مخزون"‏ reach the same intent, without the
		/// matcher becoming loose about WHICH intent.
		private static string Normalise(string s)
		{
			if (string.IsNullOrWhiteSpace(s)) return string.Empty;
			var sb = new System.Text.StringBuilder(s.Length);
			foreach (var ch in s)
			{
				// Tashkeel and tatweel carry no lexical weight here.
				if (ch >= '\u064B' && ch <= '\u0652') continue;
				if (ch == '\u0640') continue;
				sb.Append(ch switch
				{
					'\u0623' or '\u0625' or '\u0622' => '\u0627',   // أ إ آ -> ا
					'\u0649' => '\u064A',                            // ى -> ي
					'\u0629' => '\u0647',                            // ة -> ه
					_ => char.ToLowerInvariant(ch),
				});
			}
			return sb.ToString();
		}

		/// Words that carry no intent. Removed from the parameter so "كام رصيد مياه معدنية" asks
		/// about "مياه معدنية" and not about the whole sentence.
		// INTERROGATIVES BELONG HERE, not in any intent's keywords. They were in the item-stock
		// keyword list, which made "مين أحسن مورد عندنا؟" match it; taking them out fixed that and
		// broke something else, because a word that is neither keyword nor stopword survives into
		// the PARAMETER — the lookup then searched for "كام مياه معدنية" as one string and could
		// never match. A question word has to be removed somewhere; this is the somewhere.
		private static readonly string[] Stopwords =
		{
			"ال", "من", "في", "عن", "على", "عندنا", "عندي", "لدينا", "متاح", "موجود",
			"كم", "كام", "ايه", "ما", "ماهو", "هو", "هي", "هل", "من فضلك", "لو سمحت", "كمية", "الكمية",
			"اخر", "آخر", "اخير", "الاخيرة", "بتاع", "بتاعة", "علي", "الى", "إلى",
			"the", "of", "in", "on", "for", "to", "we", "our", "us", "have", "has", "is", "are",
			"do", "does", "did", "what", "much", "many", "how", "there", "any", "please",
			"quantity", "latest", "last", "recent", "show", "me", "give", "list", "tell",
		};

		// =============================================================================================
		// BEING SPOKEN TO.
		//
		// "ازيك" used to get "I do not know how to answer that. The questions I can answer are a
		// defined list" — which is true, and is also the single clearest way to tell somebody they
		// are talking to a form. A greeting reads no data and needs no authority, so it is answered
		// HERE, before the catalogue is even consulted, and cannot reach anything if it is wrong.
		//
		// Deliberately tiny. This is not a personality; it is the difference between a reply and a
		// rebuff, and anything more would be a chatbot pretending to be a colleague.
		// =============================================================================================
		private static readonly (string[] Words, AskText Reply)[] SmallTalk =
		{
			(new[] { "ازيك", "إزيك", "ازيك عامل ايه", "عامل ايه", "اخبارك", "كيف حالك", "السلام عليكم",
					 "صباح الخير", "مساء الخير", "اهلا", "أهلا", "هاي", "hello", "hi there", "good morning",
					 "good evening", "how are you" },
				AskText.Of("أهلًا. اسألني عن المخزون أو المبيعات أو الحسابات، في حدود صلاحياتك.",
						   "Hello. Ask me about stock, sales or the accounts, within your permissions.")),

			(new[] { "شكرا", "شكراً", "متشكر", "تسلم", "thanks", "thank you", "cheers" },
				AskText.Of("تحت أمرك.", "Any time.")),

			(new[] { "مين انت", "انت مين", "انت ايه", "who are you", "what are you" },
				AskText.Of("مساعد داخل النظام. أقرأ ما تملك صلاحية قراءته، وأقول من أين جاء كل رقم.",
						   "An assistant inside the system. I read what you are permitted to read, and I say where every figure came from.")),

			// "انت معايا؟" is what a person types when the thing has gone quiet on them — which it had,
			// because the instance was being restarted mid-conversation. Answering it is the least I
			// can do; not causing it is the rest.
			//
			// BOTH SPELLINGS, because people write both, and because the first version of this entry
			// said "معاي" — a \u escape in a patch script had eaten the final alef, so the very phrase
			// this was added for still fell through to a two-minute refusal. Arabic in source belongs
			// in the file as Arabic.
			(new[] { "انت معايا", "أنت معايا", "انت معاي", "أنت معاي", "معايا", "معاي",
					 "سامعني", "فين ردك", "رد عليا", "هلو",
					 "are you there", "you there", "still there" },
				AskText.Of("أيوه، معاك. اسأل.", "Yes, still here. Go ahead.")),

			(new[] { "باي", "مع السلامة", "سلام", "bye", "goodbye" },
				AskText.Of("مع السلامة.", "Goodbye.")),
		};

		/// A greeting only when the message is SHORT. "صباح الخير، ايه رصيد مياه معدنية" is a question
		/// with a greeting attached, and answering the greeting would drop the question.
		private static AskAnswer? SmallTalkAnswer(string question)
		{
			// AskSearch.Normalise, NOT the local one: this Normalise folds letters but leaves
			// punctuation alone, so "انت معايا ؟" did not equal "انت معايا" and the greeting fell
			// through to the catalogue - after a 125-second detour through the language model.
			// Punctuation is not part of what somebody said.
			var folded = string.Join(" ", AskSearch.Tokens(question));
			if (folded.Length == 0) return null;
			var words = folded.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (words.Length > 4) return null;

			foreach (var (candidates, reply) in SmallTalk)
			{
				foreach (var c in candidates)
				{
					var n = string.Join(" ", AskSearch.Tokens(c));
					if (n.Length > 0 && folded == n)
						return new AskAnswer { Answered = false, Refusal = AskRefusal.None, Spoken = reply };
				}
			}
			return null;
		}

		// =============================================================================================
		// THE ONE EXIT A REFUSAL LEAVES BY.
		//
		// Written as a single helper rather than a call at each return, because there are six ways out
		// of the method below and a refusal that escapes through the one nobody updated is precisely
		// the invisible gap this log exists to remove.
		//
		// An ANSWER is not recorded. The log is a list of what is missing, not an audit trail of what
		// people read — the second thing is a different artifact with different rules, and nobody
		// asked for it.
		// =============================================================================================
		private AskAnswer Leaving(
			AskAnswer answer, string question, int companyId, string engine, string? triedIntent,
			string? conversationId = null)
		{
			if (!answer.Answered && answer.Refusal != AskRefusal.None)
				_refusals.Record(question, answer.Refusal, triedIntent, companyId, engine, conversationId);
			return answer;
		}

		public async Task<AskAnswer> AskTextAsync(string question, string? conversationId = null,
			CancellationToken ct = default)
		{
			var text = Normalise(question);
			if (text.Length == 0)
				return AskAnswer.Refuse(AskRefusal.NotUnderstood, Catalogue);

			// ---- being spoken to, before anything is read ------------------------------------------
			var pleasantry = SmallTalkAnswer(question);
			if (pleasantry != null) return pleasantry;

			// ---- ANSWERING A QUESTION THE ASSISTANT ASKED -------------------------------------------
			//
			// If it asked "رصيد أنهي صنف؟" then this message is probably the answer, not a new
			// question. It is TRIED as the answer rather than assumed to be one: the pending intent
			// runs with this text as its parameter, and only an actual answer is accepted. Somebody
			// who ignored the question and asked something else gets a refusal from that attempt and
			// falls through to normal matching, which is what should happen.
			var pending = _conversation.GetPending(conversationId);
			if (pending != null && !string.IsNullOrWhiteSpace(pending.Parameter))
			{
				var resumed = await AskAsync(new AskRequest
				{
					Intent = pending.Intent,
					Parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
					{
						[pending.Parameter] = question.Trim(),
					},
					Question = question,
				}, ct);

				if (resumed.Answered)
				{
					_conversation.RememberPending(conversationId, null);
					if (resumed.Subject != null) _conversation.Remember(conversationId, resumed.Subject);
					resumed.About = AskText.Of(
						"ردًا على: " + pending.Asked, "In answer to: " + pending.Asked);
					return resumed;
				}
			}

			// Only intents the asker MAY use are candidates, so an unauthorised question is never
			// selected and then refused — it is simply not in the running.
			var available = await AvailableAsync(ct);

			// SCORED BY MATCHED LENGTH, NOT BY HIT COUNT.
			//
			// A longer phrase is a more specific claim about what was meant. "كام رصيد النقدية" hits
			// "رصيد" for item stock and "النقدية" for the cash balance — one hit each, so counting
			// would make it a coin toss decided by registration order. Weighing the characters that
			// actually matched lets the more specific word win, which is the one the asker chose.
			// WHICH QUESTION IS THIS — asked of the resolver chain rather than decided here.
			//
			// It used to be decided here, in a keyword loop, which meant improving the understanding
			// meant editing the gate. It is now interchangeable, and that is safe precisely because
			// the choice carries no authority: `available` is already filtered to intents this asker
			// may use, and whatever comes back is re-checked by AskAsync below. See
			// AskIntentResolvers.cs.
			var choice = await _resolver.ChooseAsync(question, available, ct);
			var best = choice.Intent == null
				? null
				: available.FirstOrDefault(h =>
					string.Equals(h.Intent, choice.Intent, StringComparison.OrdinalIgnoreCase));

			var continuesAList = false;
			var repeatsLastRecord = false;

			/// "his details", "her file", "tell me more about it" — a question made entirely of
			/// pointing. It names nothing, so nothing can route it; what it means is the record the
			/// previous turn just named.
			static bool IsDetailFollowUp(string q)
			{
				var f = AskSearch.Normalise(q);
				string[] words = { "بياناته", "بياناتها", "تفاصيله", "تفاصيلها", "معلوماته", "معلوماتها",
								   "ملفه", "ملفها", "عنه", "عنها", "كل حاجه عنه", "اعرف عنه",
								   "his details", "her details", "his file", "her file", "more about", "tell me more" };
				return words.Any(w => f.Contains(AskSearch.Normalise(w), StringComparison.Ordinal));
			}

			// A FOLLOW-UP THAT NARROWS THE LAST ANSWER.
			//
			// The owner asked for the list of doctors, got it, and said «الاسنان فقط» — and was
			// refused, because two words name no intent. They name no intent on their own and the
			// sentence before them gives them all the meaning they need.
			//
			// Only the surface that answered LAST is offered the question, and only when it says
			// it holds that value. A generous version of this would answer an unrelated question
			// with the previous subject's figures, which is worse than the refusal it replaces.
			if (best == null)
			{
				var remembered = _conversation.GetLastIntent(conversationId);
				var wasList = remembered != null && remembered.EndsWith(":list", StringComparison.Ordinal);
				var lastIntent = wasList ? remembered![..^5] : remembered;
				var previous = lastIntent == null ? null
					: available.FirstOrDefault(h => string.Equals(h.Intent, lastIntent, StringComparison.OrdinalIgnoreCase));
				if (previous != null)
				{
					var narrowScope = await ScopeForAsync(previous, ct);
					if (narrowScope != null && await previous.CanNarrowAsync(question, narrowScope, ct))
					{
						best = previous;
						continuesAList = wasList;
					}
					// NOTHING TO NARROW BY, BECAUSE THE QUESTION IS ONLY POINTING. The subject the
					// previous turn left behind IS the answer, so the question becomes those words
					// again and the surface finds the same record.
					// Read from the conversation rather than from `carried`, which is bound further
					// down this method: the subject is needed to DECIDE the route, not after it.
					else if (IsDetailFollowUp(question)
						&& _conversation.Get(conversationId) is { } pointed
						&& pointed.Kind == AskSurfaceQueryHandler.NarrowKind
						&& !string.IsNullOrWhiteSpace(pointed.Value))
					{
						best = previous;
						repeatsLastRecord = true;
						question = pointed.Value;
					}
				}
			}

			if (best == null)
			{
				// The company is resolved again only on this path. Nothing matched, so no handler
				// ran and no provenance exists to read it from, and one extra resolve on a refusal
				// is cheaper than carrying it through a method that does not otherwise need it.
				var refusedFor = await _company.ResolveAsync(cancellationToken: ct);

				// A QUESTION SOMEONE ELSE COULD HAVE ANSWERED IS NOT A GAP.
				//
				// `available` is filtered by authority, so a caller who may not read the accounting
				// reports matches nothing and lands here — with a question the product answers
				// perfectly well for the people who may ask it. Recording that would fill the
				// unanswered-questions screen with features that already exist, and that screen is
				// how the next thing to build gets chosen: it would send someone to build revenue
				// analysis twice. The ASKER still gets the same reply either way, which is the
				// right posture — a refusal that named the permission would enumerate, to anyone
				// who asked, exactly which capabilities they are missing.
				var withheld = await WithheldAsync(available, ct);
				// The SAME test the keyword resolver uses, so "this would have matched" means the
				// same thing on both sides of the permission line. A third of the question's
				// characters having to be matched keeps a single shared short word — "كام" — from
				// turning every unmatched question into a permission story.
				var folded = AskSearch.Normalise(question);
				var isPermission = folded.Length > 0 && withheld.Any(h =>
					h.Keywords.Select(AskSearch.Normalise)
						.Where(k => k.Length > 0 && folded.Contains(k, StringComparison.Ordinal))
						.Sum(k => k.Length) * 3 >= folded.Length);

				var sorry = NotUnderstoodWithClosest(question, available, choice.Ranked);
				return isPermission
					? sorry
					: Leaving(sorry, question, refusedFor.Ok ? refusedFor.CompanyId : 0,
						choice.Engine, null, conversationId);
			}

			// THE WORDS THAT SELECTED THE INTENT ARE NOT PART OF WHAT WAS ASKED ABOUT, so they are
			// dropped before the rest becomes the parameter. Recomputed from the CHOSEN handler
			// rather than reported by the matcher, because a resolver may not have matched on words
			// at all — a statistical one matches on character n-grams and has no "matched word" to
			// report, yet the keyword that IS present still must not leak into the lookup.
			var matchedWords = best.Keywords
				.Select(Normalise)
				.Where(nk => nk.Length > 0 && text.Contains(nk, StringComparison.Ordinal))
				.ToList();

			// THE PARAMETER KEEPS ITS ORIGINAL SPELLING. Normalising is for MATCHING only.
			//
			// The first version passed the normalised text on to the handler, and the lookup could
			// then never succeed: normalisation folds ة into ه, so "مياه معدنية" became
			// "مياه معدنيه" and was compared against a stored name that still reads "معدنية".
			// Normalising one side of a comparison and not the other guarantees a miss.
			//
			// So the words are dropped token by token — a token goes if its NORMALISED form is a
			// keyword or a stopword — and what survives is joined back in the spelling the person
			// actually typed, which is the spelling the database holds.
			var dropped = new HashSet<string>(StringComparer.Ordinal);
			foreach (var w in matchedWords)
			{
				dropped.Add(w);
				// A MULTI-WORD KEYWORD DROPS ITS WORDS INDIVIDUALLY TOO. The removal below works
				// token by token, so "اخر حركات" as one entry never matched the lone token "اخر",
				// which then survived into the lookup — "آخر حركات مياه معدنية" searched for an item
				// whose name contains "آخر" and found nothing. If a phrase selected the intent, every
				// word in that phrase is an intent word, not part of what is being asked about.
				foreach (var part in w.Split(' ', StringSplitOptions.RemoveEmptyEntries))
					dropped.Add(part);
			}
			foreach (var w in Stopwords)
			{
				var nw = Normalise(w);
				dropped.Add(nw);
				foreach (var part in nw.Split(' ', StringSplitOptions.RemoveEmptyEntries))
					dropped.Add(part);
			}

			var kept = question
				.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
				.Select(tok => tok.Trim(' ', '?', '\u061F', '.', ',', '!', '؛', ':'))
				.Where(tok => tok.Length > 0)
				.Where(tok => !Droppable(tok, dropped))
				.ToList();
			var residue = string.Join(' ', kept).Trim();

			var parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
			var first = best.Parameters.FirstOrDefault();

			// THE FOLLOW-UP. The intent was understood and names a parameter, but the question put
			// nothing in it — "والتكلفة؟" after "كام رصيد برجر دجاج". If the conversation holds a
			// subject OF THAT PARAMETER'S KIND, use it. A subject of another kind is ignored rather
			// than coerced: an item is not an account, and filling one with the other would answer
			// a question nobody asked.
			AskSubject? carried = null;
			if (first != null)
			{
				if (residue.Length < 2)
				{
					var remembered = _conversation.Get(conversationId);
					if (remembered != null
						&& string.Equals(remembered.Kind, first, StringComparison.OrdinalIgnoreCase)
						&& remembered.Value.Length >= 2)
					{
						residue = remembered.Value;
						carried = remembered;
					}
				}
				parameters[first] = residue;
			}

			var answer = await AskAsync(
				new AskRequest { Intent = best.Intent, Parameters = parameters, Question = question,
					ContinuesAList = continuesAList, RepeatsLastRecord = repeatsLastRecord }, ct);

			// SAY WHAT WAS ASSUMED. A follow-up that silently picks up a subject is indistinguishable
			// from one that answered about the wrong thing, and the person has no way to tell.
			//
			// AND SAY WHEN THE QUESTION WAS INTERPRETED RATHER THAN MATCHED. When the generative
			// tail engine is what understood a wording the other engines could not, the person is
			// told which question it was taken to be — because that engine reports no comparable
			// confidence, so the interpretation itself is the only thing they can check. Both notes
			// can apply at once, and both are shown.
			var notes = new List<AskText>();
			if (carried != null)
				notes.Add(AskText.Of("عن: " + carried.Display, "About: " + carried.Display));
			if (choice.Engine.StartsWith("llm:", StringComparison.Ordinal))
				notes.Add(AskText.Of(
					"فهمت سؤالك على أنه: " + best.Example.Ar,
					"Read as: " + best.Example.En));
			if (notes.Count > 0)
				answer.About = AskText.Of(
					string.Join(" · ", notes.Select(n => n.Ar)),
					string.Join(" · ", notes.Select(n => n.En)));

			// WHAT THE NEXT QUESTION MAY BUILD ON. Only a handler knows what it actually resolved,
			// so only a handler sets this. A turn that resolved nothing clears the subject, so a
			// refusal never leaves a stale one standing for the turn after it.
			_conversation.Remember(conversationId, answer.Subject ?? carried);

			// WHAT A FOLLOW-UP NARROWS. Only an ANSWER earns this: a refusal leaves the previous
			// subject matter standing, because the question that failed told us nothing new about
			// what the conversation is about.
			// The shape travels with the intent: ":list" marks an answer that was a list of records,
			// so a follow-up narrowing it gives a shorter LIST rather than a count of what is left.
			if (answer.Answered)
				_conversation.RememberLastIntent(conversationId,
					best.Intent + (answer.IsRecordList ? ":list" : ""));

			// RECORDED ON THE WAY OUT. A handler refusal is the common kind - the question was
			// understood and the data could not answer it - and is exactly what nobody could count
			// before.
			Leaving(answer, question, answer.Provenance.CompanyId, choice.Engine, best.Intent, conversationId);

			// WHEN THE HANDLER ASKED BACK, remember what it asked so the next message can answer it.
			// Any other outcome clears a previous question: leaving one standing makes the turn after
			// next answer something nobody asked.
			if (!string.IsNullOrWhiteSpace(answer.NeedsParameter))
			{
				var askedText = answer.Spoken ?? answer.Message;
				_conversation.RememberPending(conversationId, new AskPending
				{
					Intent = best.Intent,
					Parameter = answer.NeedsParameter!,
					Asked = askedText == null
						? ""
						: (System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar"
							? askedText.Ar : askedText.En),
				});
			}
			else
			{
				_conversation.RememberPending(conversationId, null);
			}

			return answer;
		}

		/// ARABIC GLUES ITS LITTLE WORDS ON. A token is dropped when it is a keyword or a stopword —
		/// and also when it is one of those wearing a conjunction in front or a pronoun behind.
		///
		/// Both were real. "برجر دجاج والتكلفة" put "والتكلفة" into the PARAMETER, and the lookup
		/// went hunting for an item whose name contains "والتكلفه". "اخر حركاته" did the same with
		/// "حركاته" — and that one matters twice over, because the pronoun IS the follow-up: "its
		/// movements" names no item precisely because the previous question already did.
		///
		/// Stripped only when what remains is itself droppable, and only while the remainder is
		/// still long enough to be a word. Otherwise "ورق" becomes "رق" and "مياه" becomes "ميا",
		/// and the matcher starts inventing words nobody typed.
		private static readonly string[] Clitics = { "ها", "هم", "هن", "كم", "نا", "ه", "ك", "ي" };

		private static bool Droppable(string token, HashSet<string> dropped)
		{
			var n = Normalise(token);
			if (IsBare(n, dropped)) return true;
			// A leading conjunction, with or without a trailing pronoun: "وحركاته".
			if (n.Length >= 4 && n[0] == 'و' && IsBare(n.Substring(1), dropped)) return true;
			return false;
		}

		private static bool IsBare(string n, HashSet<string> dropped)
		{
			if (dropped.Contains(n)) return true;
			foreach (var c in Clitics)
			{
				if (n.Length < c.Length + 3) continue;          // the stem must survive as a word
				if (!n.EndsWith(c, StringComparison.Ordinal)) continue;
				if (dropped.Contains(n.Substring(0, n.Length - c.Length))) return true;
			}
			return false;
		}

		// A REFUSAL THAT STILL MOVES THE PERSON FORWARD.
		//
		// The catalogue is ranked against the words they actually used — matched loosely, across the
		// intent's keywords, its description and its example — so the nearest question comes first
		// rather than the one that happened to be registered first. It is offered, never run: a
		// loose match is a guess, and acting on a guess is how an assistant answers the wrong
		// question with a straight face.
		private static AskAnswer NotUnderstoodWithClosest(
			string question, IReadOnlyList<IAskHandler> available,
			IReadOnlyList<AskIntentScore>? resolverRanking = null)
		{
			// PREFER THE RESOLVER'S OWN RANKING when it produced one. It measured the whole question
			// against every phrasing of every intent; the local fallback below only re-scores the
			// words, which is the same narrow view that failed to understand the question in the
			// first place. Offering the near misses in the order the better judge put them is the
			// difference between a useful "did you mean" and a list in registration order.
			var byResolver = resolverRanking is { Count: > 0 }
				? resolverRanking
					.Select(r => new
					{
						h = available.FirstOrDefault(a =>
							string.Equals(a.Intent, r.Intent, StringComparison.OrdinalIgnoreCase)),
						score = (int)Math.Round(r.Score * 1000),
					})
					.Where(x => x.h != null && x.score > 0)
					.Select(x => new { h = x.h!, x.score })
					.ToList()
				: null;

			var ranked = byResolver is { Count: > 0 }
				? byResolver
				: available
					.Select(h => new
					{
						h,
						score = AskSearch.Score(question, string.Join(' ', h.Keywords))
							+ AskSearch.Score(question, h.Description.Ar, h.Description.En)
							+ AskSearch.Score(question, h.Example.Ar, h.Example.En),
					})
					.OrderByDescending(x => x.score)
					.ToList();

			var isAr = System.Globalization.CultureInfo.CurrentUICulture
				.TwoLetterISOLanguageName == "ar";

			var rows = ranked
				.Select(x => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
				{
					["name"] = isAr ? x.h.Example.Ar : x.h.Example.En,
					["code"] = isAr ? x.h.Description.Ar : x.h.Description.En,
					["onHand"] = null,
				})
				.ToList();

			return new AskAnswer
			{
				Answered = false,
				Refusal = AskRefusal.NotUnderstood,
				Message = ranked.Count > 0 && ranked[0].score > 0
					// NAME THE REAL LIMIT. "I did not understand" is what the mechanism does and not
					// what happened: a question like "أعلى الأصناف مبيعًا" was recognisably about
					// items and sales, and was refused because no handler existed. Telling someone
					// they were unclear, when the truth is that the catalogue is short, sends them
					// to rephrase a question that was already fine.
					? AskText.Of("هذا السؤال ليس ضمن ما أستطيع الإجابة عليه بعد. أقرب الأسئلة المتاحة:",
								 "That is not something I can answer yet. The closest questions I do have:")
					: Catalogue,
				Rows = rows,
			};
		}

		// THE CATALOGUE THE ASKER MAY ACTUALLY USE. Filtered by authority before it is shown, so a
		// question someone cannot ask is never offered — which also keeps a model from selecting an
		// intent that is certain to be refused.
		/// The scope a handler would read under, built the same way the answering path builds it —
		/// company, roles and the extra actions the surface's columns need. Null when the company
		/// cannot be resolved or the module has no access service, which both mean "do not ask it".
		///
		/// Used before a follow-up is sent back to the surface that answered last: the question it
		/// is asked is "do you hold this value", and that must be answered under the SAME authority
		/// the answer would be, or a narrow check could see a value the answer may not show.
		private async Task<AskScope?> ScopeForAsync(IAskHandler handler, CancellationToken ct)
		{
			var resolution = await _company.ResolveAsync(cancellationToken: ct);
			if (!resolution.Ok) return null;

			var module = _modules.FirstOrDefault(m =>
				string.Equals(m.Scope, handler.Module, StringComparison.OrdinalIgnoreCase));
			if (module == null) return null;

			var context = await _context.GetCurrentAsync(ct);
			var granted = new List<string>();
			foreach (var extra in handler.ExtraActions)
				if (await module.CanAsync(context, extra, target: null, cancellationToken: ct))
					granted.Add(extra);

			return new AskScope
			{
				CompanyId = resolution.CompanyId,
				Roles = await module.RolesAsync(context, ct),
				Granted = granted,
			};
		}

		/// The intents this caller may NOT use. Only ever consulted to decide whether an unmatched
		/// question was a missing capability or a missing permission — never to answer one.
		private async Task<IReadOnlyList<IAskHandler>> WithheldAsync(
			IReadOnlyList<IAskHandler> available, CancellationToken ct)
		{
			var open = new HashSet<string>(available.Select(h => h.Intent), StringComparer.OrdinalIgnoreCase);
			return _handlers.Where(h => !open.Contains(h.Intent)).ToList();
		}

		public async Task<IReadOnlyList<IAskHandler>> AvailableAsync(CancellationToken ct = default)
		{
			var resolution = await _company.ResolveAsync(cancellationToken: ct);
			if (!resolution.Ok) return Array.Empty<IAskHandler>();

			var context = await _context.GetCurrentAsync(ct);
			var allowed = new List<IAskHandler>();
			foreach (var h in _handlers)
			{
				var module = _modules.FirstOrDefault(m =>
					string.Equals(m.Scope, h.Module, StringComparison.OrdinalIgnoreCase));
				if (module == null) continue;
				if (await module.CanAsync(context, h.Action, target: null, cancellationToken: ct))
					allowed.Add(h);
			}
			return allowed;
		}
	}
}
