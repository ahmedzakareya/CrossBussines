namespace CrossBuy.BL.CrmIdentity
{
	/// CRM C9 — the assistant's judgement, as plain rules that say WHY. No model, no database: every number on the
	/// screen can be traced to a line here and a fact in the record. Shadow-tested in CrmAssistRulesTests.
	public static class CrmAssistRules
	{
		// ------------------------------------------------------------------ lead score: fit + behaviour − silence
		/// What the record shows the lead has DONE, counted by the service.
		public sealed record LeadSignals(int FitPoints, bool Booked, DateTime? LastInboundAt, int WebForms, bool AnyOptIn, int ActivitiesDone,
			DateTime? LastTouchAt, DateTime CreatedAt, bool Converted);

		/// One line of the explanation: a code the view words in the reader's language, the points it added (or took),
		/// and the number it rests on.
		public sealed record Reason(string Code, int Points, int? Evidence = null);
		public sealed record LeadScore(int Score, string Band, List<Reason> Reasons);

		public const int Hot = 70, Warm = 40;

		public static LeadScore ScoreLead(LeadSignals s, DateTime nowUtc, int hot = Hot, int warm = Warm)
		{
			var r = new List<Reason>();
			// fit: the company's own scoring rules (source, segment, value) — capped so behaviour can still decide
			var fit = Math.Clamp(s.FitPoints, 0, 40);
			if (fit > 0) r.Add(new Reason("fit", fit, s.FitPoints));
			// behaviour: what they did, strongest first
			if (s.Booked) r.Add(new Reason("booked", 25));
			if (s.LastInboundAt is DateTime li)
			{
				var d = (int)(nowUtc - li).TotalDays;
				if (d <= 7) r.Add(new Reason("messagedRecently", 20, d));
				else if (d <= 30) r.Add(new Reason("messaged", 10, d));
			}
			if (s.WebForms > 0) r.Add(new Reason("webForm", Math.Min(20, 10 * s.WebForms), s.WebForms));
			if (s.AnyOptIn) r.Add(new Reason("optIn", 5));
			if (s.ActivitiesDone > 0) r.Add(new Reason("worked", Math.Min(10, 3 * s.ActivitiesDone), s.ActivitiesDone));
			// silence: the longer nothing has happened, the colder — measured from the last touch of ANY kind
			var last = new[] { s.LastTouchAt, s.LastInboundAt, (DateTime?)s.CreatedAt }.Where(x => x != null).Max()!.Value;
			var quiet = (int)(nowUtc - last).TotalDays;
			if (quiet > 60) r.Add(new Reason("silent", -25, quiet));
			else if (quiet > 30) r.Add(new Reason("silent", -15, quiet));
			var score = s.Converted ? 100 : Math.Clamp(r.Sum(x => x.Points), 0, 100);
			if (s.Converted) r.Insert(0, new Reason("converted", 0));
			return new LeadScore(score, score >= hot ? "Hot" : score >= warm ? "Warm" : "Cold", r.OrderByDescending(x => Math.Abs(x.Points)).ToList());
		}

		// ------------------------------------------------------------------ the next best action on a deal
		public sealed record DealFacts(string Stage, decimal Amount, DateTime? ExpectedClose, int DaysSinceActivity, bool HasOpenTask,
			IReadOnlyList<string> UnmetForNextStage, bool DiscountPending, bool DiscountRejected, bool HasDecisionMaker, int UnansweredInbound,
			bool HasQuotation, int Probability);

		/// One thing to do, most urgent first: a code, a weight (higher = sooner) and an optional parameter for the wording.
		public sealed record Action(string Code, int Weight, string? Param = null);

		public static List<Action> NextActions(DealFacts f, DateTime todayLocal)
		{
			var a = new List<Action>();
			if (f.Stage is "Won" or "Lost") return a;
			if (f.UnansweredInbound > 0) a.Add(new Action("answerCustomer", 100, f.UnansweredInbound.ToString()));
			if (f.DiscountRejected) a.Add(new Action("reviseDiscount", 90));
			else if (f.DiscountPending) a.Add(new Action("chaseApproval", 70));
			if (f.ExpectedClose is DateTime c && c.Date < todayLocal.Date) a.Add(new Action("fixCloseDate", 85, ((int)(todayLocal.Date - c.Date).TotalDays).ToString()));
			else if (f.ExpectedClose is DateTime soon && (soon.Date - todayLocal.Date).TotalDays <= 7 && !f.HasQuotation && f.Probability >= 50) a.Add(new Action("sendQuote", 80));
			if (f.DaysSinceActivity >= 14) a.Add(new Action("reconnect", 60 + Math.Min(20, f.DaysSinceActivity / 7), f.DaysSinceActivity.ToString()));
			if (!f.HasDecisionMaker && f.Amount > 0) a.Add(new Action("findDecisionMaker", 55));
			if (f.UnmetForNextStage.Count > 0) a.Add(new Action("completeStage", 50, string.Join("، ", f.UnmetForNextStage)));
			if (!f.HasOpenTask) a.Add(new Action("planNextStep", 40));
			if (f.ExpectedClose == null) a.Add(new Action("setCloseDate", 30));
			return a.OrderByDescending(x => x.Weight).ToList();
		}

		/// The weight of the most urgent action, used to order "My day"; 0 when nothing needs doing.
		public static int Urgency(IReadOnlyList<Action> actions) => actions.Count == 0 ? 0 : actions[0].Weight;
	}
}
