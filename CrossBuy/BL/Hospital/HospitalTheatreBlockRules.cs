using CrossBuy.Models.Context.Hospital;

namespace CrossBuy.BL.Hospital
{
	public sealed record HospitalBlockCase(int RoomId, DateTime Day, int Start, int Minutes, int SurgeonId);
	public sealed record HospitalBlockUse(int Sessions, int BlockMinutes, int UsedMinutes, int OthersMinutes)
	{
		public decimal? UtilisationPct => BlockMinutes == 0 ? null : Math.Round(UsedMinutes * 100m / BlockMinutes, 0, MidpointRounding.AwayFromZero);
	}

	// ==========================================================================================
	// HOSPITAL H244 — block time, free of the database.
	//   · A block's SESSIONS over a window are the dates on its weekday between the first and last day (inclusive).
	//   · A case USES the block for the minutes it overlaps the block's window in the same theatre on that date; only the
	//     block owner's cases count (a block for a named service line with no surgeon counts every case). Other surgeons'
	//     minutes inside the block are shown apart — the block was used, but not by its owner.
	//   · A case's minutes are its actual start to end when done, otherwise its booked start and duration.
	//   · Utilisation at or above 75 % is the commonly used target; under 50 % suggests the block be released.
	// ==========================================================================================
	public static class HospitalTheatreBlockRules
	{
		public const decimal Target = 75m, ReleaseBelow = 50m;

		public static int Overlap(int aStart, int aEnd, int bStart, int bEnd) => Math.Max(0, Math.Min(aEnd, bEnd) - Math.Max(aStart, bStart));

		public static IReadOnlyList<DateTime> Sessions(int weekday, DateTime from, DateTime to)
		{
			var list = new List<DateTime>();
			for (var d = from.Date; d <= to.Date; d = d.AddDays(1)) if ((int)d.DayOfWeek == weekday) list.Add(d);
			return list;
		}

		public static HospitalBlockUse Use(HospitalTheatreBlock b, IEnumerable<HospitalBlockCase> cases, DateTime from, DateTime to)
		{
			var days = Sessions(b.Weekday, from, to).ToHashSet();
			int used = 0, others = 0;
			foreach (var c in cases.Where(c => c.RoomId == b.RoomId && days.Contains(c.Day.Date)))
			{
				int m = Overlap(c.Start, c.Start + c.Minutes, b.StartMinute, b.EndMinute);
				if (b.SurgeonId == null || c.SurgeonId == b.SurgeonId) used += m; else others += m;
			}
			int blockMinutes = days.Count * (b.EndMinute - b.StartMinute);
			return new HospitalBlockUse(days.Count, blockMinutes, Math.Min(used, blockMinutes), others);
		}

		public static string Level(decimal? pct) => pct is not decimal p ? "None" : p >= Target ? "Good" : p < ReleaseBelow ? "Release" : "Low";

		public static string? BlockRefusal(int weekday, int start, int end, int? surgeonId, string? label, IEnumerable<HospitalTheatreBlock> sameRoomDay, int? selfId)
		{
			if (weekday is < 0 or > 6) return "Choose the day";
			if (start < 0 || end > 24 * 60 || end - start < 60) return "A block is at least an hour within the day";
			if (surgeonId == null && string.IsNullOrWhiteSpace(label)) return "Choose the surgeon or name the service line";
			if ((label ?? "").Trim().Length > 100) return "The name is 100 characters at most";
			return sameRoomDay.Any(o => o.ID != selfId && o.IsActive && Overlap(start, end, o.StartMinute, o.EndMinute) > 0) ? "Another block holds this theatre at that time" : null;
		}

		public static string? CardRefusal(params string?[] fields) =>
			fields.All(string.IsNullOrWhiteSpace) ? "Fill at least one part of the card" : null;
	}
}
