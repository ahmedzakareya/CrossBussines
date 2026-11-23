using System.Globalization;
using System.Text;
using CrossBuy.Models.Context.Hospital;

namespace CrossBuy.BL.Hospital
{
	public sealed record HospitalHl7Visit(string AdmissionNo, string? Ward, string? Room, string? Bed, string? Attending, DateTime AdmittedAt, DateTime? DischargedAt);
	public sealed record HospitalHl7Result(string Code, string Name, string? Value, string ValueType, string? Unit, string? Range, string? Flag, string? Loinc = null);

	// ==========================================================================================
	// HOSPITAL H247 — building HL7 v2.5.1 messages (the reader is HospitalHl7, H198). Encoding |^~\&, segments end with a
	// carriage return. Text is escaped (| ^ ~ \ & as \F\ \S\ \R\ \E\ \T\), times are the ward's clock (yyyyMMddHHmmss).
	//   ADT^A01 admit · ADT^A02 transfer · ADT^A03 discharge: MSH, EVN, PID, PV1.
	//   ORU^R01 result: MSH, PID, OBR (accession, test, collected, verified, status F), one OBX per result
	//     (NM with value, unit, reference range and abnormal flag H/L/HH/LL/A; ST for text).
	// ==========================================================================================
	public static class HospitalHl7Out
	{
		public const string SendingApp = "CROSSBUY";

		public static string Esc(string? s)
		{
			if (string.IsNullOrEmpty(s)) return "";
			var b = new StringBuilder(s.Length);
			foreach (var c in s)
				b.Append(c switch { '\\' => "\\E\\", '|' => "\\F\\", '^' => "\\S\\", '~' => "\\R\\", '&' => "\\T\\", '\r' or '\n' => " ", _ => c.ToString() });
			return b.ToString();
		}

		public static string Ts(DateTime ward) => ward.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

		private static string Msh(string type, string controlId, string? recvApp, string? recvFac, string facility, DateTime nowWard) =>
			$"MSH|^~\\&|{SendingApp}|{Esc(facility)}|{Esc(recvApp)}|{Esc(recvFac)}|{Ts(nowWard)}||{type}|{Esc(controlId)}|P|2.5.1";

		private static string Pid(HospitalPatient p)
		{
			var ids = Esc(p.Mrn) + "^^^" + SendingApp + "^MR" + (string.IsNullOrWhiteSpace(p.NationalId) ? "" : "~" + Esc(p.NationalId) + "^^^KW^NI");
			var parts = (string.IsNullOrWhiteSpace(p.NameEn) ? p.Name : p.NameEn).Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
			var name = parts.Length == 2 ? Esc(parts[1]) + "^" + Esc(parts[0]) : Esc(parts.FirstOrDefault());
			var sex = p.Sex == HospitalPatientSex.Female ? "F" : p.Sex == HospitalPatientSex.Male ? "M" : "U";
			return $"PID|1||{ids}||{name}||{(p.BirthDate is DateTime b ? b.ToString("yyyyMMdd", CultureInfo.InvariantCulture) : "")}|{sex}";
		}

		/// ADT^A01 / A02 / A03 for an inpatient stay.
		public static string Adt(string trigger, string controlId, HospitalPatient p, HospitalHl7Visit v, string? recvApp, string? recvFac, string facility, DateTime eventWard, DateTime nowWard)
		{
			var pv1 = new string[46];
			pv1[0] = "PV1"; pv1[1] = "1"; pv1[2] = "I"; pv1[3] = Esc(v.Ward) + "^" + Esc(v.Room) + "^" + Esc(v.Bed); pv1[7] = Esc(v.Attending);
			pv1[19] = Esc(v.AdmissionNo); pv1[44] = Ts(v.AdmittedAt); pv1[45] = v.DischargedAt is DateTime d ? Ts(d) : "";
			return string.Join("\r", new[]
			{
				Msh("ADT^" + trigger + "^ADT_" + (trigger == "A02" || trigger == "A03" ? trigger : "A01"), controlId, recvApp, recvFac, facility, nowWard),
				$"EVN|{trigger}|{Ts(eventWard)}",
				Pid(p),
				string.Join("|", pv1.Select(x => x ?? "")),
			}) + "\r";
		}

		/// ORU^R01 for a verified laboratory order.
		public static string Oru(string controlId, HospitalPatient p, string accession, string testCode, string testName, DateTime? collectedWard, DateTime verifiedWard,
			IReadOnlyList<HospitalHl7Result> results, string? recvApp, string? recvFac, string facility, DateTime nowWard)
		{
			var lines = new List<string>
			{
				Msh("ORU^R01^ORU_R01", controlId, recvApp, recvFac, facility, nowWard),
				Pid(p),
				$"OBR|1|{Esc(accession)}|{Esc(accession)}|{Esc(testCode)}^{Esc(testName)}|||{(collectedWard is DateTime c ? Ts(c) : "")}|||||||||||||||{Ts(verifiedWard)}|||F",
			};
			int i = 0;
			foreach (var r in results)
				lines.Add($"OBX|{++i}|{r.ValueType}|{HospitalLoinc.Obx3(r.Code, r.Name, r.Loinc, Esc)}||{Esc(r.Value)}|{Esc(r.Unit)}|{Esc(r.Range)}|{Esc(r.Flag)}|||F");   // H261: LOINC first when known
			return string.Join("\r", lines) + "\r";
		}

		/// The HL7 abnormal flag for a laboratory flag.
		public static string? Flag(string? labFlag) => labFlag switch
		{
			HospitalLabFlag.High => "H", HospitalLabFlag.Low => "L", HospitalLabFlag.CriticalHigh => "HH", HospitalLabFlag.CriticalLow => "LL", HospitalLabFlag.Normal or null => null, _ => "A",
		};

		public static string? Range(decimal? low, decimal? high) =>
			low is decimal l && high is decimal h ? l.ToString(CultureInfo.InvariantCulture) + "-" + h.ToString(CultureInfo.InvariantCulture)
			: high is decimal h2 ? "<" + h2.ToString(CultureInfo.InvariantCulture) : low is decimal l2 ? ">" + l2.ToString(CultureInfo.InvariantCulture) : null;

		/// MLLP framing: VT … FS CR.
		public static byte[] Frame(string message) => Encoding.UTF8.GetBytes("\v" + message + "\u001c\r");

		/// The acknowledgement code of an ACK (MSA-1), or null when it is not one.
		public static string? AckCode(string? raw)
		{
			if (string.IsNullOrWhiteSpace(raw)) return null;
			foreach (var seg in raw.Trim('\v', '\u001c', '\r', '\n').Split('\r', '\n'))
				if (seg.StartsWith("MSA|", StringComparison.Ordinal)) { var f = seg.Split('|'); return f.Length > 1 ? f[1] : null; }
			return null;
		}

		public static string? FeedRefusal(string? code, string? name, string? kind, string? host, int? port, bool enabled)
		{
			if (string.IsNullOrWhiteSpace(code) || code.Trim().Length > 20) return "Enter the feed's code";
			if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100) return "Enter the feed's name";
			if (!HospitalFeedKind.All.Contains(kind ?? "")) return "Choose what the feed carries";
			if (enabled && (string.IsNullOrWhiteSpace(host) || port is not (> 0 and < 65536))) return "An enabled feed needs its host and port";
			return (host ?? "").Trim().Length > 200 ? "The host is 200 characters at most" : null;
		}
	}
}
